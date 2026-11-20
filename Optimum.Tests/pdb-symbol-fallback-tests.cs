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
    public void CorruptSymbolsAreIgnoredAndAssemblyStillLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-corrupt-{Guid.NewGuid():N}");
        string assemblyPath = Path.Combine(root, "assembly.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(assemblyPath, pdbName: null);
            File.WriteAllText(Path.ChangeExtension(assemblyPath, ".pdb"), "not a portable PDB");

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
    public void TruncatedSymbolsAreIgnoredAndAssemblyStillLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-truncated-{Guid.NewGuid():N}");
        string assemblyPath = Path.Combine(root, "assembly.dll");

        try
        {
            Directory.CreateDirectory(root);
            CopyOutputAssembly(assemblyPath, pdbName: null);
            File.WriteAllBytes(Path.ChangeExtension(assemblyPath, ".pdb"), Array.Empty<byte>());

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
    public void DiscardedSymbolsRemoveAStaleOutputSidecar()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-symbol-output-{Guid.NewGuid():N}");
        string outputPath = Path.Combine(root, "output.dll");
        string outputPdbPath = Path.ChangeExtension(outputPath, ".pdb");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(outputPdbPath, "stale symbols");

            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Optimum.SymbolOutputFixture", new Version(1, 0, 0, 0)),
                "Optimum.SymbolOutputFixture",
                ModuleKind.Dll);

            string writtenHash = AssemblyWriter.Write(assembly, outputPath, writeSymbols: false);

            Assert.True(File.Exists(outputPath));
            Assert.False(File.Exists(outputPdbPath));
            string expectedHash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(outputPath)));
            Assert.Equal(expectedHash, writtenHash);
            Assert.Equal(expectedHash, AssemblyWriter.ConsumeLastWrittenSha256());
            Assert.Null(AssemblyWriter.ConsumeLastWrittenSha256());
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public void ProgramRunOutOverloadReturnsDigestOnSuccessAndClearsStateOnFailure()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Optimum.DigestClearFixture", new Version(1, 0, 0, 0)),
            "Optimum.DigestClearFixture",
            ModuleKind.Dll);

        string root = Path.Combine(Path.GetTempPath(), $"optimum-digest-clear-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            AssemblyWriter.Write(assembly, Path.Combine(root, "seed.dll"), writeSymbols: false);

            int exitCode = Optimum.Patcher.Program.Run(
                [Path.Combine(root, "missing.dll"), Path.Combine(root, "missing2.dll"), Path.Combine(root, "out.dll")],
                out string? patchedSha256);
            Assert.NotEqual(0, exitCode);
            Assert.Null(patchedSha256);
            Assert.Null(AssemblyWriter.ConsumeLastWrittenSha256());

            AssemblyWriter.Write(assembly, Path.Combine(root, "seed2.dll"), writeSymbols: false);
            Assert.Throws<FileNotFoundException>(() =>
                Optimum.Patcher.Program.Run(
                    ["--api", Path.Combine(root, "missing.dll"), Path.Combine(root, "missing2.dll"), Path.Combine(root, "out.dll")],
                    out _));
            Assert.Null(AssemblyWriter.ConsumeLastWrittenSha256());

            string outputDirectory = Path.GetDirectoryName(typeof(PdbSymbolFallbackTests).Assembly.Location)!;
            var dir = new DirectoryInfo(outputDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
            {
                dir = dir.Parent;
            }
            string? vanillaPath = dir != null
                ? Path.Combine(dir.FullName, ".vanilla", "win-x64", "runtime-donors", "VintagestoryAPI.dll")
                : null;
            string contractsPath = Path.Combine(outputDirectory, "Optimum.Api.Contracts.dll");
            if (vanillaPath != null && File.Exists(vanillaPath) && File.Exists(contractsPath))
            {
                string outputPath = Path.Combine(root, "VintagestoryAPI-patched.dll");
                int apiExit = Optimum.Patcher.Program.Run(["--api", vanillaPath, contractsPath, outputPath], out string? apiSha256);
                Assert.Equal(0, apiExit);
                string expectedApiSha256 = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(outputPath)));
                Assert.Equal(expectedApiSha256, apiSha256);
                Assert.Null(AssemblyWriter.ConsumeLastWrittenSha256());
            }
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
