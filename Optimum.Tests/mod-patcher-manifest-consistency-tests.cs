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
