using System.IO;
using Mono.Cecil;

namespace Optimum.Patcher;

/// <summary>
/// Writes patched assemblies without leaving stale debug-symbol sidecars.
/// </summary>
internal static class AssemblyWriter
{
    public static void Write(
        AssemblyDefinition assembly,
        string outputPath,
        bool writeSymbols)
    {
        if (!writeSymbols)
        {
            File.Delete(Path.ChangeExtension(outputPath, ".pdb"));
        }

        assembly.Write(outputPath, new WriterParameters { WriteSymbols = writeSymbols });
    }
}
