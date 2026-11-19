using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Verifies that the ApiPatcher injects type forwards from VintagestoryAPI.dll
/// to Optimum.Api.Contracts.dll for all Optimum-original types that VintagestoryLib
/// references under the [VintagestoryAPI] assembly scope.
/// </summary>
public class ApiPatcherTypeForwardTests
{
    [Fact]
    public void ApiPatcher_InjectsTypeForwards_ForContractsTypes()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("InjectTypeForwards", apiPatcher);
        Assert.Contains("ExportedTypes.Add", apiPatcher);
        Assert.Contains("AssemblyNameReference", apiPatcher);
        Assert.Contains("Optimum.Api.Contracts", apiPatcher);
    }

    [Fact]
    public void ContractsProject_IncludesCoreManagedTypes()
    {
        string contracts = Read("optimum-api-contracts/optimum-api-contracts.csproj");

        // Core types that are 100% Optimum-original and belong in contracts.
        Assert.Contains("OptimumConfig.cs", contracts);
        Assert.Contains("OptimumAnimLod.cs", contracts);
        Assert.Contains("OptimumGreedyMesher.cs", contracts);
        Assert.Contains("OptimumWorkerInstances.cs", contracts);
    }

    [Fact]
    public void ForkProject_ExcludesContractsTypes()
    {
        string fork = Read("VintagestoryApi/VintagestoryAPI.csproj");

        // The fork excludes types that live in contracts to avoid CS0433.
        Assert.Contains("<Compile Remove=\"Config\\OptimumConfig.cs\"", fork);
        Assert.Contains("<Compile Remove=\"Config\\OptimumWorkerInstances.cs\"", fork);
    }

    [Fact]
    public void TypeForwardMethod_SkipsExistingTypeDefs()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        // The method must skip types already defined in vanilla to avoid conflicts.
        Assert.Contains("vanilla.MainModule.GetType(type.FullName) != null", apiPatcher);
    }

    [Fact]
    public void TypeForwardMethod_HandlesNestedTypes()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("NestedTypes", apiPatcher);
        Assert.Contains("NestedPublic", apiPatcher);
        Assert.Contains("StartsWith(\"<\", StringComparison.Ordinal)", apiPatcher);
        Assert.Contains("DeclaringType = parentExport", apiPatcher);
    }

    [Fact]
    public void PatchedApi_ExcludesCompilerGeneratedForwards_HasNoDuplicateKeys_AndResolvesAllPublicForwardedTypes()
    {
        string? vanillaApiPath = FindFirstExisting(
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        string? contractsPath = FindFirstExisting(
            "bin/Release/net10.0/Optimum.Api.Contracts.dll",
            "bin/Debug/net10.0/Optimum.Api.Contracts.dll");

        if (vanillaApiPath == null || contractsPath == null)
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"optimum-type-forward-681-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string patchedApiPath = Path.Combine(tempDir, "VintagestoryAPI.dll");
        AssemblyLoadContext? alc = null;

        try
        {
            Assert.True(ApiPatcher.Patch(vanillaApiPath, contractsPath, patchedApiPath));
            Assert.True(File.Exists(patchedApiPath));

            var publicContractFullNames = new List<string>();

            using (var vanillaAsm = AssemblyDefinition.ReadAssembly(vanillaApiPath))
            using (var contractsAsm = AssemblyDefinition.ReadAssembly(contractsPath))
            using (var patchedAsm = AssemblyDefinition.ReadAssembly(patchedApiPath))
            {
                var exports = patchedAsm.MainModule.ExportedTypes;
                Assert.NotEmpty(exports);

                // 1. No compiler-generated types (names starting with '<') may be exported.
                Assert.DoesNotContain(exports, e => e.Name.StartsWith("<", StringComparison.Ordinal));

                // 2. Top-level exports must have unique (Namespace, Name) keys (required by crossgen2 EcmaModule.CreateNameLookupCache).
                var topLevelKeys = new HashSet<(string Namespace, string Name)>();
                foreach (var export in exports.Where(e => e.DeclaringType == null))
                {
                    Assert.True(
                        topLevelKeys.Add((export.Namespace ?? string.Empty, export.Name)),
                        $"Duplicate top-level ExportedType key: ({export.Name}, {export.Namespace})");
                }

                // 3. Nested exports must be scoped under their declaring ExportedType with empty Namespace (ECMA-335 II.22.14).
                var nestedKeys = new HashSet<(string Declaring, string Name)>();
                foreach (var export in exports.Where(e => e.DeclaringType != null))
                {
                    Assert.True(string.IsNullOrEmpty(export.Namespace), $"Nested export {export.FullName} must have empty Namespace.");
                    Assert.True(
                        nestedKeys.Add((export.DeclaringType.FullName, export.Name)),
                        $"Duplicate nested ExportedType key: ({export.DeclaringType.FullName}/{export.Name})");
                }

                // 4. Every non-compiler-generated public top-level and public nested type in Optimum.Api.Contracts
                //    (not already defined in vanilla) must be exported.
                var exportedFullNames = new HashSet<string>(exports.Select(e => e.FullName), StringComparer.Ordinal);
                void CollectPublicContractTypes(TypeDefinition td)
                {
                    if (td.Name.StartsWith("<", StringComparison.Ordinal)) return;
                    if (vanillaAsm.MainModule.GetType(td.FullName) == null)
                    {
                        if (td.IsPublic || td.IsNestedPublic)
                        {
                            Assert.Contains(td.FullName, exportedFullNames);
                            publicContractFullNames.Add(td.FullName.Replace('/', '+'));
                        }
                    }
                    foreach (var nested in td.NestedTypes)
                    {
                        CollectPublicContractTypes(nested);
                    }
                }
                foreach (var td in contractsAsm.MainModule.Types)
                {
                    CollectPublicContractTypes(td);
                }
            }

            Assert.NotEmpty(publicContractFullNames);

            // 5. Verify runtime CLR resolution from the patched VintagestoryAPI.dll resolves every forwarded public type
            //    (top-level and nested) into Optimum.Api.Contracts.dll, just as compiled mods do.
            string contractsDir = Path.GetDirectoryName(contractsPath)!;
            string vanillaLibDir = Path.Combine(Path.GetDirectoryName(vanillaApiPath)!, "Lib");
            alc = new AssemblyLoadContext($"TypeForwardResolveTest-{Guid.NewGuid():N}", isCollectible: true);
            alc.Resolving += (ctx, name) =>
            {
                foreach (string dir in new[] { tempDir, contractsDir, vanillaLibDir, AppContext.BaseDirectory })
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
                }
                return null;
            };

            Assembly loadedPatchedApi = alc.LoadFromAssemblyPath(patchedApiPath);
            Assembly loadedContracts = alc.LoadFromAssemblyPath(contractsPath);
            foreach (string clrName in publicContractFullNames)
            {
                Type? resolved = loadedPatchedApi.GetType(clrName, throwOnError: true);
                Assert.NotNull(resolved);
                Assert.Equal(loadedContracts, resolved!.Assembly);
            }
        }
        finally
        {
            alc?.Unload();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static string? FindFirstExisting(params string[] relativePaths)
    {
        foreach (string rel in relativePaths)
        {
            try
            {
                string full = PatchReader.FindRepositoryFile(rel);
                if (File.Exists(full)) return full;
            }
            catch (FileNotFoundException)
            {
            }
        }
        return null;
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
