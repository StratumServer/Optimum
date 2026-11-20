using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Optimum patches the unmodified game file. Fed its own earlier output, ApiPatcher used to apply a few hooks
/// and then fail in PatchChiselLodHook with "Expected one FrustumCulling.InFrustumAndRange call, found 0".
/// It now recognises the calls it adds to OptimumApiBridge and says what happened.
/// </summary>
public sealed class ApiPatcherAlreadyPatchedTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("optimum-api-patched-");

    public void Dispose() => temp.Delete(recursive: true);

    [Fact]
    public void AnApiDllThatCallsOptimumApiBridgeIsRejectedAsAlreadyPatched()
    {
        string vanilla = Path.Combine(temp.FullName, "VintagestoryAPI.dll");
        string contracts = Path.Combine(temp.FullName, "VintagestoryAPI.Contracts.dll");
        BuildApi(vanilla, callsOptimumApiBridge: true);
        File.WriteAllBytes(contracts, [0]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ApiPatcher.Patch(vanilla, contracts, Path.Combine(temp.FullName, "out.dll")));

        Assert.Contains("already patched by Optimum", error.Message);
        Assert.Contains("OptimumApiBridge", error.Message);
    }

    [Fact]
    public void AnApiDllWithoutThoseCallsIsNotTakenForPatched()
    {
        string vanilla = Path.Combine(temp.FullName, "VintagestoryAPI.dll");
        string contracts = Path.Combine(temp.FullName, "VintagestoryAPI.Contracts.dll");
        BuildApi(vanilla, callsOptimumApiBridge: false);
        File.WriteAllBytes(contracts, [0]);

        // The patcher goes on to read the contracts assembly, which is not one here. What matters is that
        // it did not stop on the already patched check.
        var error = Record.Exception(() =>
            ApiPatcher.Patch(vanilla, contracts, Path.Combine(temp.FullName, "out.dll")));

        Assert.False(error is InvalidOperationException { Message: var message } && message.Contains("already patched"));
    }

    private static void BuildApi(string path, bool callsOptimumApiBridge)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryAPI", new Version(1, 0)), "VintagestoryAPI", ModuleKind.Dll);
        var module = assembly.MainModule;
        var holder = new TypeDefinition(
            "Vintagestory.API.Common", "Holder", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(holder);

        var method = new MethodDefinition(
            "Run", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
        var il = method.Body.GetILProcessor();
        if (callsOptimumApiBridge)
        {
            var contractsAssembly = new AssemblyNameReference("VintagestoryAPI.Contracts", new Version(1, 0));
            var bridge = new TypeReference("Vintagestory.API.Config", "OptimumApiBridge", module, contractsAssembly);
            il.Emit(OpCodes.Call, new MethodReference("MarkSlotDirty", module.TypeSystem.Void, bridge));
        }

        il.Emit(OpCodes.Ret);
        holder.Methods.Add(method);
        assembly.Write(path);
    }
}
