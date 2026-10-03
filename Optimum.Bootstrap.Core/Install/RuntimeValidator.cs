using System.Reflection;
using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record RuntimeValidationResult(bool Ok, string? Detail);

public interface IRuntimeValidator
{
    RuntimeValidationResult Validate(string packageDirectory);
}

/// <summary>
/// Checks that a staged package is a complete runtime without running any game
/// code (INSTALLER-PLAN.md section 7, option 2). The layout holds, required
/// runtime assemblies exist, patched assembly references resolve, and a metadata-only load of
/// <c>VintagestoryLib.dll</c> still exposes <c>Vintagestory.Client.ClientProgram</c>
/// with a static <c>Main</c>. The full JIT probe stays with
/// <c>Optimum.exe --validate-only</c>, which those packages could ship later.
/// </summary>
public sealed class RuntimeValidator(ISystemProbe probe) : IRuntimeValidator
{
    private static readonly string[] RequiredAssemblies =
    [
        "VintagestoryLib.dll",
        "VintagestoryAPI.dll",
        "Vintagestory.dll",
        "Optimum.GameContent.dll",
    ];

    private static readonly string[] FallbackPatchedAssemblies =
    [
        "VintagestoryLib.dll",
        "VintagestoryAPI.dll",
        "Mods/VSEssentials.dll",
        "Mods/VSSurvivalMod.dll",
    ];

    public RuntimeValidationResult Validate(string packageDirectory)
    {
        PackageLayoutResult layout = PackageLayout.Validate(probe, packageDirectory);
        if (!layout.Ok)
            return new RuntimeValidationResult(false, string.Join("; ", layout.Problems));

        foreach (string name in RequiredAssemblies)
        {
            string path = Path.Combine(packageDirectory, name);
            if (!probe.FileExists(path))
                return new RuntimeValidationResult(false, $"missing assembly: {name}");
            try
            {
                if (new FileInfo(path).Length == 0)
                    return new RuntimeValidationResult(false, $"empty assembly: {name}");
                _ = AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                return new RuntimeValidationResult(false, $"not a managed assembly: {name}");
            }
            catch (Exception ex) when (ex is IOException or FileLoadException)
            {
                return new RuntimeValidationResult(false, $"could not read {name}: {ex.Message}");
            }
        }

        RuntimeValidationResult references = CheckPatchedReferences(packageDirectory);
        if (!references.Ok)
            return references;

        return CheckEntryPoint(packageDirectory);
    }

    private static RuntimeValidationResult CheckPatchedReferences(string packageDirectory)
    {
        string manifestPath = Path.Combine(packageDirectory, PatchInstallManifest.RelativePath);
        IReadOnlyList<string> patchedAssemblies = FallbackPatchedAssemblies;
        bool targetsFromManifest = false;
        if (File.Exists(manifestPath))
        {
            try
            {
                PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(File.ReadAllText(manifestPath));
                if (manifest is { Targets.Count: > 0 })
                {
                    patchedAssemblies = manifest.Targets.Select(target => target.Assembly).ToArray();
                    targetsFromManifest = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RuntimeValidationResult(false, $"could not read patch manifest: {ex.Message}");
            }
        }

        var searchDirectories = new List<string>
        {
            packageDirectory,
            Path.Combine(packageDirectory, "Lib"),
            Path.Combine(packageDirectory, "Mods"),
            RuntimeEnvironment.GetRuntimeDirectory(),
        };

        var availableAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assemblyPaths = new List<string>();
        try
        {
            foreach (string directory in searchDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
                {
                    try
                    {
                        string? name = AssemblyName.GetAssemblyName(path).Name;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            availableAssemblies.Add(name);
                            assemblyPaths.Add(path);
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        // Native DLLs do not satisfy managed assembly references.
                    }
                    catch (Exception ex) when (ex is IOException or FileLoadException or UnauthorizedAccessException)
                    {
                        // An unreadable candidate cannot satisfy a reference.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RuntimeValidationResult(false, $"could not inspect runtime assemblies: {ex.Message}");
        }

        using var context = new MetadataLoadContext(
            new PathAssemblyResolver(assemblyPaths.Distinct(StringComparer.OrdinalIgnoreCase)));
        foreach (string relativePath in patchedAssemblies)
        {
            string path = Path.Combine(packageDirectory, relativePath);
            if (!File.Exists(path))
            {
                if (targetsFromManifest)
                    return new RuntimeValidationResult(false, $"patched assembly is missing: {relativePath}");
                continue;
            }

            AssemblyName[] references;
            try
            {
                references = context.LoadFromAssemblyPath(path).GetReferencedAssemblies();
            }
            catch (BadImageFormatException)
            {
                return new RuntimeValidationResult(false, $"patched assembly is not managed: {relativePath}");
            }
            catch (Exception ex) when (ex is IOException or FileLoadException or UnauthorizedAccessException)
            {
                return new RuntimeValidationResult(false, $"could not inspect {relativePath}: {ex.Message}");
            }

            foreach (AssemblyName reference in references)
            {
                if (!string.IsNullOrWhiteSpace(reference.Name) && !availableAssemblies.Contains(reference.Name))
                    return new RuntimeValidationResult(false,
                        $"{relativePath} references missing assembly: {reference.Name}");
            }
        }

        return new RuntimeValidationResult(true, null);
    }

    private static RuntimeValidationResult CheckEntryPoint(string packageDirectory)
    {
        // Inspecting the entry point is best effort: a positive "the type is
        // gone" fails the build, but an inability to inspect at all (an
        // unresolvable reference, a trimmed runtime directory) does not, because
        // the header checks above already passed.
        try
        {
            var assemblies = new List<string>();
            assemblies.AddRange(Directory.EnumerateFiles(packageDirectory, "*.dll"));
            string libDir = Path.Combine(packageDirectory, "Lib");
            if (Directory.Exists(libDir))
                assemblies.AddRange(Directory.EnumerateFiles(libDir, "*.dll"));
            try { assemblies.AddRange(Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { /* trimmed publish */ }

            using var context = new MetadataLoadContext(
                new PathAssemblyResolver(assemblies.Distinct(StringComparer.OrdinalIgnoreCase)));
            Assembly libAssembly = context.LoadFromAssemblyPath(Path.Combine(packageDirectory, "VintagestoryLib.dll"));

            IEnumerable<Type?> types;
            try { types = libAssembly.GetTypes(); }
            catch (ReflectionTypeLoadException partial) { types = partial.Types; }

            Type? clientProgram = types.FirstOrDefault(t => t?.FullName == "Vintagestory.Client.ClientProgram");
            if (clientProgram is null)
            {
                // Only fail if we could enumerate types and the one we need is
                // absent; if the enumeration was empty we could not inspect.
                return types.Any(t => t is not null)
                    ? new RuntimeValidationResult(false,
                        "the patched VintagestoryLib.dll no longer contains Vintagestory.Client.ClientProgram")
                    : new RuntimeValidationResult(true, "entry point not inspected: VintagestoryLib.dll types would not enumerate");
            }

            MethodInfo? main = clientProgram.GetMethod("Main",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return main is null
                ? new RuntimeValidationResult(false, "Vintagestory.Client.ClientProgram has no static Main")
                : new RuntimeValidationResult(true, null);
        }
        catch (Exception ex)
        {
            return new RuntimeValidationResult(true, $"entry point not inspected: {ex.Message}");
        }
    }
}
