using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class ApiVersionLabelTests
{
    [Fact]
    public void PatchGameVersionLabelAppendsOptimumVersionToVanillaValue()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
        var gameVersion = new TypeDefinition(
            "Vintagestory.API.Config",
            "GameVersion",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(gameVersion);

        var longGameVersion = new FieldDefinition(
            "LongGameVersion",
            FieldAttributes.Public | FieldAttributes.Static,
            module.TypeSystem.String);
        gameVersion.Fields.Add(longGameVersion);

        var initializer = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private |
            MethodAttributes.Static |
            MethodAttributes.HideBySig |
            MethodAttributes.SpecialName |
            MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        gameVersion.Methods.Add(initializer);
        ILProcessor processor = initializer.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldstr, "v1.22.7 (Stable)"));
        processor.Append(Instruction.Create(OpCodes.Stsfld, longGameVersion));
        processor.Append(Instruction.Create(OpCodes.Ret));

        int patched = ApiPatcher.PatchGameVersionLabel(module, "0.3.3");

        Assert.Equal(1, patched);
        Instruction[] instructions = initializer.Body.Instructions.ToArray();
        Assert.Equal(OpCodes.Ldsfld, instructions[^5].OpCode);
        Assert.Same(longGameVersion, instructions[^5].Operand);
        Assert.Equal(OpCodes.Ldstr, instructions[^4].OpCode);
        Assert.Equal(" + Optimum v0.3.3", instructions[^4].Operand);
        Assert.Equal(OpCodes.Call, instructions[^3].OpCode);
        Assert.Equal("System.String System.String::Concat(System.String,System.String)", instructions[^3].Operand.ToString());
        Assert.Equal(OpCodes.Stsfld, instructions[^2].OpCode);
        Assert.Same(longGameVersion, instructions[^2].Operand);
        Assert.Equal(OpCodes.Ret, instructions[^1].OpCode);
    }
}
