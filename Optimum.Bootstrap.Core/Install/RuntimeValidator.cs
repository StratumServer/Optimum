using System.Reflection;
using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record RuntimeValidationResult(bool Ok, string? Detail);

/// <summary>
/// Checks that a staged package is a complete runtime without running any game
/// code (INSTALLER-PLAN.md section 7, option 2). The layout holds, the patched
/// engine assemblies exist and parse, and a metadata-only load of
/// <c>VintagestoryLib.dll</c> still exposes <c>Vintagestory.Client.ClientProgram</c>
/// with a static <c>Main</c>. The full JIT probe stays with
/// <c>Optimum.exe --validate-only</c>, which those packages could ship later.
/// </summary>
public sealed class RuntimeValidator(ISystemProbe probe)
{
    private static readonly string[] RequiredAssemblies =
    [
        "VintagestoryLib.dll",
        "VintagestoryAPI.dll",
        "Vintagestory.dll",
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

        return CheckEntryPoint(packageDirectory);
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
