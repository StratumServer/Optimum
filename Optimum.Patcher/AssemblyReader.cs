using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Reads patch inputs while treating debug symbols as optional metadata.
/// </summary>
internal static class AssemblyReader
{
    public static AssemblyDefinition Read(
        string path,
        ReaderParameters parameters,
        out bool symbolsLoaded)
    {
        symbolsLoaded = false;

        if (!parameters.ReadSymbols)
        {
            return AssemblyDefinition.ReadAssembly(path, parameters);
        }

        try
        {
            AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, parameters);
            symbolsLoaded = true;
            return assembly;
        }
        catch (SymbolsNotFoundException ex)
        {
            return ReadWithoutSymbols(path, parameters, ex);
        }
        catch (SymbolsNotMatchingException ex)
        {
            return ReadWithoutSymbols(path, parameters, ex);
        }
        catch (Exception ex)
        {
            // A PDB can be present but unreadable: a truncated download, a
            // wrong-format file, or disk corruption. Cecil reports missing and
            // mismatched symbols with the two dedicated exceptions above, but a
            // corrupt native or portable PDB surfaces the underlying reader's
            // own exception (for example Microsoft.Cci.Pdb.PdbException), which
            // lives in the Mono.Cecil.Pdb assembly and shares no public base
            // with those two. Symbols are optional metadata, so any symbol-read
            // failure falls back to a no-symbol read. A genuinely broken
            // assembly still throws, because the retry below reads the same
            // file without symbols.
            return ReadWithoutSymbols(path, parameters, ex);
        }
    }

    private static AssemblyDefinition ReadWithoutSymbols(
        string path,
        ReaderParameters parameters,
        Exception symbolError)
    {
        Console.Error.WriteLine(
            $"  WARNING: Ignoring symbols for {Path.GetFileName(path)}: {symbolError.Message}");
        parameters.ReadSymbols = false;
        return AssemblyDefinition.ReadAssembly(path, parameters);
    }
}
