using System.Linq;
using Cairo;
using Mono.Cecil;
using Vintagestory.API.Config;
using Xunit;

public sealed class ApiPatcherMaterializationObserverTests
{
    [Fact]
    public void ModuleInitializerSubscribesOnceToTheActualCairoReplayBoundary()
    {
        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumGuiMetrics).Assembly.Location);
        using var cairo = AssemblyDefinition.ReadAssembly(typeof(Surface).Assembly.Location);
        using var target = ModuleDefinition.CreateModule("VintagestoryAPI-Observer-Fixture", ModuleKind.Dll);

        Assert.Equal(1, Optimum.Patcher.ApiPatcher.PatchMaterializationObserver(
            target, contracts.MainModule, cairo.MainModule));
        Assert.Equal(0, Optimum.Patcher.ApiPatcher.PatchMaterializationObserver(
            target, contracts.MainModule, cairo.MainModule));
        Assert.Equal(1, Optimum.Patcher.ApiPatcher.CountMaterializationObserverHooks(target));
        Assert.Equal(1, Optimum.Patcher.ApiPatcher.CountMaterializationObserverCallbacks(target));

        var initializer = target.Types.Single(type => type.Name == "<Module>")
            .Methods.Single(method => method.Name == ".cctor");
        Assert.Equal("add_MaterializationCompleted", ((MethodReference)initializer.Body.Instructions
            .Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Call).Operand).Name);
        Assert.Contains(initializer.Body.Instructions,
            instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldftn &&
                instruction.Operand is MethodReference callback && callback.Name == "RecordMaterialization");
        Assert.Empty(Optimum.Patcher.IlStackVerifier.VerifyModule(target));
    }
}
