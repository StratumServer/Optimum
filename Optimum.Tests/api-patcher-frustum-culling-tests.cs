using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class ApiPatcherFrustumCullingTests
{
    [Fact]
    public void PatchFrustumCulling_RewritesMethodsToOptimumApiBridge()
    {
        string vanillaApiPath = Path.Combine(AppContext.BaseDirectory, "../../../.vanilla/win-x64/runtime-donors/VintagestoryAPI.dll");
        if (!File.Exists(vanillaApiPath))
        {
            vanillaApiPath = Path.Combine(AppContext.BaseDirectory, "../../../.vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        }
        if (!File.Exists(vanillaApiPath)) return;

        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumApiBridge).Assembly.Location);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);

        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")!;
        var inFrustum = bridge.Methods.Single(m => m.Name == "InFrustum" && m.Parameters.Count == 2);
        var inFrustumShadowPass = bridge.Methods.Single(m => m.Name == "InFrustumShadowPass" && m.Parameters.Count == 2);
        var inFrustumAndRange = bridge.Methods.Single(m => m.Name == "InFrustumAndRange" && m.Parameters.Count == 4);
        var sphereInFrustum = bridge.Methods.Single(m => m.Name == "SphereInFrustum" && m.Parameters.Count == 5);

        int patched = Optimum.Patcher.ApiPatcher.PatchFrustumCulling(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(inFrustum),
            vanilla.MainModule.ImportReference(inFrustumShadowPass),
            vanilla.MainModule.ImportReference(inFrustumAndRange),
            vanilla.MainModule.ImportReference(sphereInFrustum));

        Assert.Equal(4, patched);

        var culler = vanilla.MainModule.GetType("Vintagestory.API.Client.FrustumCulling")!;
        Assert.NotNull(culler);

        var inFrustumMethod = culler.Methods.Single(m => m.Name == "InFrustum" && m.Parameters.Count == 1);
        Assert.Contains(inFrustumMethod.Body.Instructions,
            i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call && ((MethodReference)i.Operand).Name == "InFrustum");

        var sphereInFrustumMethod = culler.Methods.Single(m => m.Name == "SphereInFrustum" && m.Parameters.Count == 4);
        Assert.Contains(sphereInFrustumMethod.Body.Instructions,
            i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call && ((MethodReference)i.Operand).Name == "SphereInFrustum");
    }
}
