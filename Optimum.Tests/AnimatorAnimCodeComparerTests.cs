using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// AnimatorBase.animsByCode ships as a plain-ordinal dictionary in the vanilla assembly, so
/// GetAnimationState/OnFrame both allocate a lowercased string on every lookup. The source
/// patch (patches/VintagestoryApi/Common/Model/Animation/AnimatorBase.cs.patch) fixes this in
/// the decompiled tree, but that tree only feeds tests/donor builds - it never reached the
/// player's actual game assembly because ApiPatcher had no Cecil target for it. These tests
/// exercise the Cecil-level fix against a synthetic module shaped like the real vanilla type.
/// </summary>
public sealed class AnimatorAnimCodeComparerTests
{
    private static (ModuleDefinition module, TypeDefinition animatorBase, FieldDefinition animsByCode,
        MethodDefinition ctor, MethodDefinition getAnimationState, MethodDefinition onFrame) BuildSyntheticAnimatorBase()
    {
        var module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtorInt = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(new[] { typeof(int) }));
        var dictTryGetValue = module.ImportReference(
            typeof(Dictionary<string, object>).GetMethod(nameof(Dictionary<string, object>.TryGetValue)));
        var toLowerInvariant = module.ImportReference(typeof(string).GetMethod(nameof(string.ToLowerInvariant), System.Type.EmptyTypes));

        var animatorBase = new TypeDefinition(
            "Vintagestory.API.Common",
            "AnimatorBase",
            TypeAttributes.Public | TypeAttributes.Abstract,
            module.TypeSystem.Object);
        module.Types.Add(animatorBase);

        var animsByCode = new FieldDefinition("animsByCode", FieldAttributes.Private | FieldAttributes.InitOnly, dictType);
        animatorBase.Fields.Add(animsByCode);

        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        animatorBase.Methods.Add(ctor);
        {
            var p = ctor.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p.Append(Instruction.Create(OpCodes.Newobj, dictCtorInt));
            p.Append(Instruction.Create(OpCodes.Stfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        var getAnimationState = new MethodDefinition(
            "GetAnimationState",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Object);
        getAnimationState.Parameters.Add(new ParameterDefinition("code", ParameterAttributes.None, module.TypeSystem.String));
        var anim1 = new VariableDefinition(module.TypeSystem.Object);
        getAnimationState.Body.Variables.Add(anim1);
        animatorBase.Methods.Add(getAnimationState);
        {
            var p = getAnimationState.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ldarg_1));
            p.Append(Instruction.Create(OpCodes.Callvirt, toLowerInvariant));
            p.Append(Instruction.Create(OpCodes.Ldloca_S, anim1));
            p.Append(Instruction.Create(OpCodes.Callvirt, dictTryGetValue));
            p.Append(Instruction.Create(OpCodes.Pop));
            p.Append(Instruction.Create(OpCodes.Ldloc_0));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        var onFrame = new MethodDefinition(
            "OnFrame",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.TypeSystem.Void);
        onFrame.Parameters.Add(new ParameterDefinition("activeAnimationsByAnimCode", ParameterAttributes.None, dictType));
        onFrame.Parameters.Add(new ParameterDefinition("dt", ParameterAttributes.None, module.TypeSystem.Single));
        var code = new VariableDefinition(module.TypeSystem.String);
        var anim2 = new VariableDefinition(module.TypeSystem.Object);
        onFrame.Body.Variables.Add(code);
        onFrame.Body.Variables.Add(anim2);
        animatorBase.Methods.Add(onFrame);
        {
            var p = onFrame.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ldloc_0));
            p.Append(Instruction.Create(OpCodes.Callvirt, toLowerInvariant));
            p.Append(Instruction.Create(OpCodes.Ldloca_S, anim2));
            p.Append(Instruction.Create(OpCodes.Callvirt, dictTryGetValue));
            p.Append(Instruction.Create(OpCodes.Pop));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        return (module, animatorBase, animsByCode, ctor, getAnimationState, onFrame);
    }

    [Fact]
    public void PatchAnimatorAnimCodeComparer_ReturnsThreeSites()
    {
        var (module, _, _, _, _, _) = BuildSyntheticAnimatorBase();

        int patched = ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.Equal(3, patched);
    }

    [Fact]
    public void CtorConstructsDictionaryWithOrdinalIgnoreCaseComparer()
    {
        var (module, _, _, ctor, _, _) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        var newobj = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj);
        var operandRef = (MethodReference)newobj.Operand;
        Assert.Equal(2, operandRef.Parameters.Count);
        Assert.Equal("System.Int32", operandRef.Parameters[0].ParameterType.FullName);
        Assert.Contains("IEqualityComparer", operandRef.Parameters[1].ParameterType.FullName);

        var before = newobj.Previous;
        Assert.Equal(OpCodes.Call, before.OpCode);
        Assert.Contains("OrdinalIgnoreCase", ((MethodReference)before.Operand).FullName);
    }

    [Fact]
    public void GetAnimationStateNoLongerCallsToLowerInvariant()
    {
        var (module, _, _, _, getAnimationState, _) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.DoesNotContain(getAnimationState.Body.Instructions, i =>
            i.Operand is MethodReference m && m.Name == "ToLowerInvariant");
    }

    [Fact]
    public void OnFrameNoLongerCallsToLowerInvariant()
    {
        var (module, _, _, _, _, onFrame) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.DoesNotContain(onFrame.Body.Instructions, i =>
            i.Operand is MethodReference m && m.Name == "ToLowerInvariant");
    }
}
