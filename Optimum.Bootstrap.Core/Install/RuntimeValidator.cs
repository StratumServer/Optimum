using System.Reflection;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record RuntimeValidationResult(bool Ok, string? Detail);

/// <summary>
/// A conservative check that a staged package is a complete runtime: the layout
/// holds, the patched engine assemblies exist and are non-empty, and each parses
/// as a managed assembly. It reads assembly headers without loading game code
/// into this process. The full JIT probe from <c>Optimum.exe --validate-only</c>
/// is a Phase 4 decision (see INSTALLER-PLAN.md section 7).
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

        return new RuntimeValidationResult(true, null);
    }
}
