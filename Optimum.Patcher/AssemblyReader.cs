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
