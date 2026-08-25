using System;
using System.IO;
using Mono.Cecil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class PdbSymbolFallbackTests
{
    [Fact]
    public void MatchingSymbolsArePreserved()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-match-{Guid.NewGuid():N}");
        string assemblyPath = Path.Combine(root, "assembly.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(assemblyPath, "Optimum.Api.Contracts.pdb");

            var parameters = new ReaderParameters { ReadSymbols = true };
            using AssemblyDefinition assembly = AssemblyReader.Read(
                assemblyPath,
                parameters,
                out bool symbolsLoaded);

            Assert.True(symbolsLoaded);
            Assert.Equal("Optimum.Api.Contracts", assembly.Name.Name);
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public void MismatchedSymbolsAreIgnoredAndAssemblyStillLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-test-{Guid.NewGuid():N}");
        string firstPath = Path.Combine(root, "first.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(firstPath, "Optimum.GameContent.pdb");

            var parameters = new ReaderParameters { ReadSymbols = true };
            using AssemblyDefinition assembly = AssemblyReader.Read(
                firstPath,
                parameters,
                out bool symbolsLoaded);

            Assert.False(symbolsLoaded);
            Assert.Equal("Optimum.Api.Contracts", assembly.Name.Name);
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public void MissingSymbolsDoNotEnableSymbolLoading()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-missing-{Guid.NewGuid():N}");
        string assemblyPath = Path.Combine(root, "assembly.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(assemblyPath, pdbName: null);

            var parameters = new ReaderParameters { ReadSymbols = true };
            using AssemblyDefinition assembly = AssemblyReader.Read(
                assemblyPath,
                parameters,
                out bool symbolsLoaded);

            Assert.False(symbolsLoaded);
            Assert.False(parameters.ReadSymbols);
            Assert.Equal("Optimum.Api.Contracts", assembly.Name.Name);
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public void CorruptSymbolsStillFailTheRead()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-corrupt-{Guid.NewGuid():N}");
        string assemblyPath = Path.Combine(root, "assembly.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(assemblyPath, pdbName: null);
            File.WriteAllText(Path.ChangeExtension(assemblyPath, ".pdb"), "not a portable PDB");

            var parameters = new ReaderParameters { ReadSymbols = true };
            Exception? error = Record.Exception(() =>
            {
                using AssemblyDefinition assembly = AssemblyReader.Read(
                    assemblyPath,
                    parameters,
                    out _);
            });

            Assert.NotNull(error);
            Assert.True(parameters.ReadSymbols);
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static void CopyOutputAssembly(string assemblyPath, string? pdbName)
    {
        string outputDirectory = Path.GetDirectoryName(typeof(PdbSymbolFallbackTests).Assembly.Location)!;
        File.Copy(Path.Combine(outputDirectory, "Optimum.Api.Contracts.dll"), assemblyPath);
        if (pdbName is not null)
        {
            File.Copy(Path.Combine(outputDirectory, pdbName), Path.ChangeExtension(assemblyPath, ".pdb"));
        }
    }

    private static void DeleteFixture(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
