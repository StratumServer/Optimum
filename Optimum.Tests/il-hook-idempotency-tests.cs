using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class IlHookIdempotencyTests
{
    [Fact]
    public void ExistingHookCountsAsSatisfiedWhenPatchingAnAlreadyPatchedAssembly()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Dll);
        ModuleDefinition module = assembly.MainModule;
        TypeDefinition builder = new("Fixture", "Builder", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(builder);
        MethodDefinition build = new("Build", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
        builder.Methods.Add(build);
        build.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "value"));
        build.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        TypeDefinition gate = new("Fixture", "Gate", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(gate);
        MethodDefinition compose = new("Compose", MethodAttributes.Public, module.TypeSystem.String);
        gate.Methods.Add(compose);
        MethodReference buildReference = module.ImportReference(build);
        compose.Body.Instructions.Add(Instruction.Create(OpCodes.Call, buildReference));
        compose.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        MethodDefinition hook = new("AfterBuild", MethodAttributes.Public, module.TypeSystem.String);
        hook.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        gate.Methods.Add(hook);
        hook.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        hook.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        bool firstPass = InsertHook(assembly);
        int hookCallsAfterFirstPass = compose.Body.Instructions.Count(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference method && method.Name == "AfterBuild");
        bool secondPass = InsertHook(assembly);
        int hookCallsAfterSecondPass = compose.Body.Instructions.Count(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference method && method.Name == "AfterBuild");

        Assert.True(firstPass);
        Assert.True(secondPass);
        Assert.Equal(1, hookCallsAfterFirstPass);
        Assert.Equal(hookCallsAfterFirstPass, hookCallsAfterSecondPass);
    }

    private static bool InsertHook(AssemblyDefinition assembly) => ILHook.InsertBeforeCall(
        assembly,
        "Fixture.Gate",
        "Compose",
        0,
        "AfterBuild",
        "Build",
        "Fixture.Builder",
        [],
        "System.String",
        targetHasThis: false,
        targetExplicitThis: false,
        targetCallingConvention: MethodCallingConvention.Default,
        targetGenericArity: 0);
}
