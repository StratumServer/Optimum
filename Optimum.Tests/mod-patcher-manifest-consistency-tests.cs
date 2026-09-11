using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// ModPatcher's manifests (Members/Types/Interfaces) name donor members by
/// string. Nothing in the C# compiler catches a mismatch when the underlying
/// patch renames or drops a field/method the manifest still expects -
/// MemberInjector only finds out at runtime, on a real decompiled build, with
/// "Required donor member not found" (see mod-patcher.cs::coveredPages, which
/// went stale for two minor versions after being renamed to renderedPages in
/// patches/runtime/VSEssentials/.../ChunkMapLayer.cs.patch). These tests
/// cross-check every manifest entry against the actual runtime patches so
/// that drift fails `dotnet test`, not a user's from-source Windows build.
/// </summary>
public sealed class ModPatcherManifestConsistencyTests
{
    public static IEnumerable<object[]> Manifests =>
    [
        ["EssentialsManifest", "VSEssentials"],
        ["SurvivalManifest", "VSSurvivalMod"],
        ["CreativeManifest", "VSCreativeMod"],
    ];

    [Theory]
    [MemberData(nameof(Manifests))]
    public void EveryInjectedMemberIsDeclaredInItsRuntimePatch(string manifestMethodName, string project)
    {
        var members = GetManifestProperty<Dictionary<string, List<string>>>(manifestMethodName, "Members");

        var problems = new List<string>();
        foreach (var (typeFullName, memberNames) in members)
        {
            string shortName = ShortName(typeFullName);
            string? patchFile = FindPatchFile(project, shortName);
            if (patchFile is null)
            {
                problems.Add(
                    $"{typeFullName}: no runtime patch found (searched patches/runtime/{project} for {shortName}.cs.patch)");
                continue;
            }

            string patched = PatchReader.ReadPatchedContent(patchFile);
            foreach (var memberName in memberNames)
            {
                if (!Regex.IsMatch(patched, $@"\b{Regex.Escape(memberName)}\b"))
                {
                    problems.Add(
                        $"{typeFullName}::{memberName} - not found in {RelativeToRepo(patchFile)}. " +
                        "mod-patcher.cs references a name the patch no longer declares (renamed or removed there?).");
                }
            }
        }

        Assert.True(problems.Count == 0, FormatFailure(manifestMethodName, problems));
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void EveryInjectedTypeIsProducedBySourceOrRuntimePatch(string manifestMethodName, string project)
    {
        var types = GetManifestProperty<List<string>>(manifestMethodName, "Types");

        var problems = new List<string>();
        foreach (var typeFullName in types)
        {
            string shortName = ShortName(typeFullName);
            bool hasSourceOverlay = FindSourceFile(shortName) is not null;
            bool hasRuntimePatch = FindPatchFile(project, shortName) is not null;
            if (!hasSourceOverlay && !hasRuntimePatch)
            {
                problems.Add(
                    $"{typeFullName}: neither a sources/**/{shortName}.cs overlay nor a " +
                    $"patches/runtime/{project}/**/{shortName}.cs.patch exists. mod-patcher.cs injects this " +
                    $"type wholesale from the compiled donor, but nothing produces {shortName}.");
            }
        }

        Assert.True(problems.Count == 0, FormatFailure(manifestMethodName, problems));
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void EveryInjectedInterfaceIsDeclaredInItsRuntimePatch(string manifestMethodName, string project)
    {
        var interfaces = GetManifestProperty<Dictionary<string, List<string>>>(manifestMethodName, "Interfaces");

        var problems = new List<string>();
        foreach (var (typeFullName, interfaceNames) in interfaces)
        {
            string shortName = ShortName(typeFullName);
            string? patchFile = FindPatchFile(project, shortName);
            if (patchFile is null)
            {
                problems.Add(
                    $"{typeFullName}: no runtime patch found for interface injection " +
                    $"(searched patches/runtime/{project} for {shortName}.cs.patch)");
                continue;
            }

            string patched = PatchReader.ReadPatchedContent(patchFile);
            foreach (var interfaceName in interfaceNames)
            {
                string interfaceShortName = ShortName(interfaceName);
                if (!Regex.IsMatch(patched, $@"\b{Regex.Escape(interfaceShortName)}\b"))
                {
                    problems.Add(
                        $"{typeFullName} -> {interfaceName}: {interfaceShortName} not declared in " +
                        $"{RelativeToRepo(patchFile)}.");
                }
            }
        }

        Assert.True(problems.Count == 0, FormatFailure(manifestMethodName, problems));
    }

    /// <summary>
    /// Methods entries are the transplants themselves: Cecil copies each named
    /// body out of the compiled donor into the user's own mod assembly. A method
    /// listed here whose declaring type has no runtime patch (and no
    /// Optimum-authored source overlay) is transplanted from an unmodified
    /// decompile, so the installed runtime silently keeps the vanilla body while
    /// the from-source fork build has the changed one - which is exactly how the
    /// TAA P3/P4 movers shipped ghosting for installed players until
    /// patches/runtime gained donors for them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Manifests))]
    public void EveryTransplantedMethodHasARuntimeDonor(string manifestMethodName, string project)
    {
        var methods = GetManifestProperty<List<MethodTarget>>(manifestMethodName, "Methods");

        // Coverage is per METHOD where it can be: a runtime patch that touches
        // one method of a type used to mark every transplant on that type as
        // covered, so a sibling method kept its vanilla body silently. The
        // patch's added lines are located inside the tree it was applied to
        // (.build/runtime-donors, else the fork, both git-ignored) and attributed
        // to the method whose braces enclose them. With neither tree on disk -
        // a clean clone - the check degrades to the old per-type one.
        var uncovered = new List<string>();
        var unchangedBody = new List<string>();
        foreach (var target in methods)
        {
            string shortName = ShortName(target.TypeFullName);
            string entry = $"{target.TypeFullName}::{target.MethodName}";
            string? patchFile = FindPatchFile(project, shortName);
            if (patchFile is not null)
            {
                string? donor = PatchMethodScopes.FindDonorSource(RepoRoot(), project, target.TypeFullName)
                    ?? FindForkSource(project, shortName);
                if (donor is null)
                {
                    continue;
                }
                string donorText = File.ReadAllText(donor);
                var touched = PatchMethodScopes.MethodsTouched(patchFile, donorText);
                // ".ctor" is the IL name; the source declares it under the type
                // name. A method the scanner cannot find at all (an accessor, a
                // local function, a shape this scanner does not model) is left
                // to the per-type check rather than reported as a gap.
                string sourceName = target.MethodName == ".ctor" ? shortName : target.MethodName;
                if (!touched.Contains(sourceName) && DeclaresMethod(donorText, sourceName))
                {
                    unchangedBody.Add(entry);
                }
                continue;
            }
            // Optimum-authored types are copied into the donor tree whole by
            // scripts/prepare-runtime-donors.sh, so they never get a patch.
            if (FindSourceFile(shortName) is not null)
            {
                continue;
            }
            uncovered.Add(entry);
        }

        var untouched = unchangedBody
            .Where(entry => !KnownUnchangedTransplants.Contains(entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            untouched.Count == 0,
            FormatFailure(
                manifestMethodName,
                untouched
                    .Select(entry =>
                        $"{entry}: the type has a patches/runtime/{project} donor, but no hunk in it lands " +
                        "inside this method, so the transplant copies a body the donor never changed. Either " +
                        "the donor patch is behind the fork (the installed runtime then ships a vanilla body) " +
                        "or the manifest entry is redundant - decide which and record it in " +
                        "KnownUnchangedTransplants if it is the latter.")
                    .ToList()));

        var unexpected = uncovered
            .Where(entry => !KnownDonorGaps.Contains(entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            unexpected.Count == 0,
            FormatFailure(
                manifestMethodName,
                unexpected
                    .Select(entry =>
                        $"{entry}: no patches/runtime/{project}/**/*.cs.patch and no sources/** overlay produces " +
                        "this type, so the installed runtime transplants a vanilla body.")
                    .ToList()));

        var closed = KnownDonorGaps
            .Where(entry => methods.Any(m => $"{m.TypeFullName}::{m.MethodName}" == entry) && !uncovered.Contains(entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            closed.Count == 0,
            "These entries now have runtime donors; remove them from KnownDonorGaps:\n  " +
            string.Join("\n  ", closed));
    }

    /// <summary>
    /// Transplants whose declaring type has no runtime donor today, listed so
    /// that the gap is visible and a *new* one still fails the test. Both are
    /// Vulkan-backend work on the FluffyClouds renderers (a separate assembly
    /// that ships inside VSEssentials.dll); neither is TAA.
    /// </summary>
    /// <summary>
    /// Transplants whose declaring type IS patched but whose own body no hunk
    /// touches. Copying an unchanged body is a no-op, so these are redundant
    /// manifest entries rather than ghosting gaps - but a NEW one is how a donor
    /// patch falls behind its fork, which is why they are listed rather than
    /// ignored.
    /// </summary>
    private static readonly HashSet<string> KnownUnchangedTransplants = new(StringComparer.Ordinal)
    {
        // ChunkMapLayer's map-piece caching changed every caller of
        // loadFromChunkPixels; the two-line method itself (enqueue onto the
        // vanilla readyMapPieces) is untouched in both trees.
        "Vintagestory.GameContent.ChunkMapLayer::loadFromChunkPixels",
    };

    private static readonly HashSet<string> KnownDonorGaps = new(StringComparer.Ordinal)
    {
        "FluffyClouds.CloudRendererMap::FreeGlResources",
        "FluffyClouds.CloudRendererMap::OnRenderFrame",
        "FluffyClouds.CloudRendererMap::WriteTexture",
        "FluffyClouds.CloudRendererMap::makeTexture",
        "FluffyClouds.CloudRendererMap::InitCloudTiles",
        "FluffyClouds.CloudRendererVolumetric::OnRenderFrame",
    };

    private static string FormatFailure(string manifestMethodName, List<string> problems) =>
        $"ModPatcher.{manifestMethodName} is out of sync with the runtime patches:\n  " +
        string.Join("\n  ", problems);

    private static string ShortName(string dottedName) => dottedName.Split('.').Last();

    private static T GetManifestProperty<T>(string manifestMethodName, string propertyName)
    {
        var method = typeof(ModPatcher).GetMethod(manifestMethodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"ModPatcher.{manifestMethodName} not found via reflection - did it get renamed?");
        object manifest = method.Invoke(null, null)
            ?? throw new InvalidOperationException($"ModPatcher.{manifestMethodName}() returned null.");
        var property = manifest.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"ModPatcher's Manifest record has no '{propertyName}' property - did it get renamed?");
        return (T)property.GetValue(manifest)!;
    }

    private static string RepoRoot()
    {
        string versionFile = PatchReader.FindRepositoryFile("VERSION");
        return Path.GetDirectoryName(versionFile)!;
    }

    private static bool DeclaresMethod(string source, string methodName) =>
        PatchMethodScopes.Parse(source)
            .Any(scope => scope.Kind == "method" && scope.Name == methodName);

    // The fork tree carries the same edits as the prepared donor decompile, so
    // it stands in when .build/runtime-donors has not been built. File names
    // differ from type names there, so this greps for the declaration.
    private static string? FindForkSource(string project, string shortTypeName)
    {
        string dir = Path.Combine(RepoRoot(), project);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        var declaration = new Regex($@"\b(class|struct|record)\s+{Regex.Escape(shortTypeName)}\b");
        return Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .FirstOrDefault(file => declaration.IsMatch(File.ReadAllText(file)));
    }

    private static string? FindPatchFile(string project, string shortTypeName)
    {
        string dir = Path.Combine(RepoRoot(), "patches", "runtime", project);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        return Directory
            .EnumerateFiles(dir, $"{shortTypeName}.cs.patch", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    // Optimum-authored source overlays are not filename==classname (e.g.
    // sources/VSEssentials/Systems/OptimumStatus.cs declares
    // OptimumStatusModSystem, renamed on copy by bootstrap.ps1/.sh), so this
    // greps file content rather than matching a "{shortTypeName}.cs" filename.
    private static string? FindSourceFile(string shortTypeName)
    {
        string dir = Path.Combine(RepoRoot(), "sources");
        if (!Directory.Exists(dir))
        {
            return null;
        }
        var classDeclaration = new Regex($@"\bclass\s+{Regex.Escape(shortTypeName)}\b");
        return Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .FirstOrDefault(file => classDeclaration.IsMatch(File.ReadAllText(file)));
    }

    private static string RelativeToRepo(string path) => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
}
