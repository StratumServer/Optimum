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
        string lib = Path.Combine(packageDirectory, "VintagestoryLib.dll");
        var assemblies = new List<string>();
        assemblies.AddRange(Directory.EnumerateFiles(packageDirectory, "*.dll"));
        string libDir = Path.Combine(packageDirectory, "Lib");
        if (Directory.Exists(libDir))
            assemblies.AddRange(Directory.EnumerateFiles(libDir, "*.dll"));
        assemblies.AddRange(Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

        try
        {
            using var context = new MetadataLoadContext(
                new PathAssemblyResolver(assemblies.Distinct(StringComparer.OrdinalIgnoreCase)));
            Assembly libAssembly = context.LoadFromAssemblyPath(lib);

            Type? clientProgram = libAssembly.GetTypes()
                .FirstOrDefault(t => t.FullName == "Vintagestory.Client.ClientProgram");
            if (clientProgram is null)
                return new RuntimeValidationResult(false,
                    "the patched VintagestoryLib.dll no longer contains Vintagestory.Client.ClientProgram");

            MethodInfo? main = clientProgram.GetMethod("Main",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (main is null)
                return new RuntimeValidationResult(false, "Vintagestory.Client.ClientProgram has no static Main");

            return new RuntimeValidationResult(true, null);
        }
        catch (ReflectionTypeLoadException ex)
        {
            string detail = string.Join("; ", ex.LoaderExceptions
                .Where(e => e is not null).Select(e => e!.Message).Take(3));
            return new RuntimeValidationResult(false, $"VintagestoryLib.dll types would not load: {detail}");
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return new RuntimeValidationResult(false, $"could not inspect VintagestoryLib.dll: {ex.Message}");
        }
    }
}
