using System;
using System.IO;
using System.Security.Cryptography;
using Mono.Cecil;

namespace Optimum.Patcher;

/// <summary>
/// Writes patched assemblies without leaving stale debug-symbol sidecars and emits the SHA-256 digest.
/// </summary>
internal static class AssemblyWriter
{
    public static string Write(
        AssemblyDefinition assembly,
        string outputPath,
        bool writeSymbols)
    {
        if (!writeSymbols)
        {
            File.Delete(Path.ChangeExtension(outputPath, ".pdb"));
        }

        string hash;
        using (var fileStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 65536))
        {
            assembly.Write(fileStream, new WriterParameters { WriteSymbols = writeSymbols });
            fileStream.Position = 0;
            byte[] digest = SHA256.HashData(fileStream);
            hash = "sha256:" + Convert.ToHexStringLower(digest);
        }

        Console.WriteLine($"PATCHED SHA256: {hash}");
        return hash;
    }
}
