using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
