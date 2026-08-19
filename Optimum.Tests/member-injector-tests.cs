using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class MemberInjectorTests
{
    [Fact]
    public void SameArityOverloadsRequireTheirParameterTypesToMatch()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("overloads", new Version(1, 0)),
            "overloads",
            ModuleKind.Dll);
        TypeDefinition type = new(
            "Fixture.Overloads",
            "Overloads",
            TypeAttributes.Public | TypeAttributes.Class,
            assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);

        MethodDefinition pathNodeEquals = new(
            "Equals",
            MethodAttributes.Public,
            assembly.MainModule.TypeSystem.Boolean);
        pathNodeEquals.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, type));
        type.Methods.Add(pathNodeEquals);

        MethodDefinition objectEquals = new(
            "Equals",
            MethodAttributes.Public,
            assembly.MainModule.TypeSystem.Boolean);
        objectEquals.Parameters.Add(new ParameterDefinition(
            "value",
            ParameterAttributes.None,
            assembly.MainModule.TypeSystem.Object));
        type.Methods.Add(objectEquals);

        Assert.True(MethodSignature.Matches(pathNodeEquals, pathNodeEquals));
        Assert.False(MethodSignature.Matches(pathNodeEquals, objectEquals));

        var sameSignatureOnAnotherType = new MethodReference(
            pathNodeEquals.Name,
            pathNodeEquals.ReturnType,
            pathNodeEquals.Module.TypeSystem.Object)
        {
            HasThis = pathNodeEquals.HasThis,
            ExplicitThis = pathNodeEquals.ExplicitThis,
            CallingConvention = pathNodeEquals.CallingConvention
        };
        foreach (var parameter in pathNodeEquals.Parameters)
        {
            sameSignatureOnAnotherType.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }
        Assert.False(MethodSignature.Matches(pathNodeEquals, sameSignatureOnAnotherType));

        Assert.Throws<InvalidOperationException>(() => MethodSignature.FindUnique(type, "Equals", 1));
        Assert.True(MethodSignature.Matches(
            pathNodeEquals,
            pathNodeEquals.DeclaringType.FullName,
            pathNodeEquals.Name,
            [pathNodeEquals.Parameters[0].ParameterType.FullName],
            pathNodeEquals.ReturnType.FullName,
            pathNodeEquals.HasThis,
            pathNodeEquals.ExplicitThis,
            pathNodeEquals.CallingConvention,
            pathNodeEquals.GenericParameters.Count));
        Assert.False(MethodSignature.Matches(
            pathNodeEquals,
            pathNodeEquals.DeclaringType.FullName,
            pathNodeEquals.Name,
            [pathNodeEquals.Parameters[0].ParameterType.FullName],
            "System.Object",
            pathNodeEquals.HasThis,
            pathNodeEquals.ExplicitThis,
            pathNodeEquals.CallingConvention,
            pathNodeEquals.GenericParameters.Count));
    }

    [Fact]
    public void MissingRequiredTypeFailsThePatch()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("vanilla", new Version(1, 0)), "vanilla", ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("compiled", new Version(1, 0)), "compiled", ModuleKind.Dll);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MemberInjector.InjectTypes(vanilla, compiled, new List<string> { "Optimum.RequiredType" }));

        Assert.Contains("Optimum.RequiredType", exception.Message);
    }

    [Fact]
    public void InjectedHelperDependenciesBringTheirFieldsIntoTheTargetAssembly()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

        TypeDefinition vanillaType = AddGearRenderer(vanilla.MainModule, includeOptimumMembers: false);
        TypeDefinition compiledType = AddGearRenderer(compiled.MainModule, includeOptimumMembers: true);

        int injected = MemberInjector.InjectStaticMembers(
            vanilla,
            compiled,
            "Vintagestory.GameContent.GearRenderer",
            new List<string> { "DisableOptimumGearRenderer" });

        Assert.Equal(3, injected);
        Assert.Contains(vanillaType.Fields, field => field.Name == "optimumGearRendererDisabled");
        Assert.Contains(vanillaType.Fields, field => field.Name == "optimumGearRendererFailureLogged");
        Assert.Contains(vanillaType.Methods, method => method.Name == "DisableOptimumGearRenderer");
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(vanilla.MainModule));
    }

    // Server worldgen/chunk-pool wiring plan, Gap A: field injection used to carry
    // over only FieldAttributes (visibility/static flags), silently dropping real
    // .NET attributes like [ThreadStatic] - turning a per-thread slot into one
    // shared-and-racing static with no error at patch time or at runtime.
    [Fact]
    public void InjectedFieldsPreserveThreadStaticAttribute()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

        TypeDefinition vanillaType = new(
            "Vintagestory.Server", "ServerSystemSupplyChunks",
            TypeAttributes.Public | TypeAttributes.Class, vanilla.MainModule.TypeSystem.Object);
        vanilla.MainModule.Types.Add(vanillaType);

        TypeDefinition compiledType = new(
            "Vintagestory.Server", "ServerSystemSupplyChunks",
            TypeAttributes.Public | TypeAttributes.Class, compiled.MainModule.TypeSystem.Object);
        compiled.MainModule.Types.Add(compiledType);

        FieldDefinition srcField = new(
            "optimumWorkerIndex",
            FieldAttributes.Private | FieldAttributes.Static,
            compiled.MainModule.TypeSystem.Int32);
        MethodReference threadStaticCtor = compiled.MainModule.ImportReference(
            typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes));
        srcField.CustomAttributes.Add(new CustomAttribute(threadStaticCtor));
        compiledType.Fields.Add(srcField);

        MemberInjector.InjectStaticMembers(
            vanilla, compiled, "Vintagestory.Server.ServerSystemSupplyChunks",
            new List<string> { "optimumWorkerIndex" });

        FieldDefinition injected = Assert.Single(vanillaType.Fields, f => f.Name == "optimumWorkerIndex");
        CustomAttribute attr = Assert.Single(injected.CustomAttributes);
        Assert.Equal("System.ThreadStaticAttribute", attr.Constructor.DeclaringType.FullName);
    }

    private static TypeDefinition AddGearRenderer(ModuleDefinition module, bool includeOptimumMembers)
    {
        TypeDefinition type = new(
            "Vintagestory.GameContent",
            "GearRenderer",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);

        if (!includeOptimumMembers)
            return type;

        FieldDefinition disabled = new(
            "optimumGearRendererDisabled",
            FieldAttributes.Private,
            module.TypeSystem.Boolean);
        FieldDefinition failureLogged = new(
            "optimumGearRendererFailureLogged",
            FieldAttributes.Private,
            module.TypeSystem.Boolean);
        type.Fields.Add(disabled);
        type.Fields.Add(failureLogged);

        MethodDefinition helper = new(
            "DisableOptimumGearRenderer",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        type.Methods.Add(helper);

        ILProcessor il = helper.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, disabled));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, failureLogged));
        il.Append(Instruction.Create(OpCodes.Ret));

        return type;
    }
}
