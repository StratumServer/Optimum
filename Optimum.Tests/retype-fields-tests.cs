using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Covers the worker-pool wiring plan's Step 8: MemberInjector.RetypeFields,
/// ILPatcher.RetargetFieldInitializers, and RetypedFieldReaderVerifier - the
/// object-to-Lock field-retype capability, deferred out of the shipped worker
/// pool because it is the only piece of that plan whose failure mode is silent
/// (see docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md,
/// Step 8).
/// </summary>
public sealed class RetypeFieldsTests
{
    [Fact]
    public void InjectingAGenuinelyNewFieldIsUnaffectedByRetypeSupport()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib");

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        compiledType.Fields.Add(new FieldDefinition(
            "brandNewField", FieldAttributes.Assembly, compiled.MainModule.TypeSystem.Int32));

        int injected = MemberInjector.InjectStaticMembers(
            vanilla, compiled, "Fixture.Gate", new List<string> { "brandNewField" });

        Assert.Equal(1, injected);
        FieldDefinition addedField = Assert.Single(vanillaType.Fields, f => f.Name == "brandNewField");
        Assert.Equal("System.Int32", addedField.FieldType.FullName);
    }

    [Fact]
    public void RetypeFieldsChangesAnExistingFieldTypeInPlace()
    {
        using var resolver = new DefaultAssemblyResolver();
        AddRuntimeSearchDirectories(resolver);
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib", resolver);
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib", resolver);

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        AddObjectGateField(vanillaType, vanilla.MainModule);
        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        AddLockGateField(compiledType, compiled.MainModule);

        // A pre-existing vanilla reader, not itself a transplant target - proves the
        // retype is visible to method bodies that already reference the field, since
        // it must mutate the FieldDefinition in place rather than replace it.
        MethodDefinition reader = AddGateReaderMethod(vanillaType, vanilla.MainModule, GetGateField(vanillaType));
        FieldDefinition fieldBeforeRetype = GetGateField(vanillaType);

        IReadOnlyList<FieldDefinition> retyped = MemberInjector.RetypeFields(
            vanilla, compiled, "Fixture.Gate", new List<string> { "gate" });

        FieldDefinition retypedField = Assert.Single(retyped);
        Assert.Same(fieldBeforeRetype, retypedField);
        Assert.Same(fieldBeforeRetype, GetGateField(vanillaType));
        Assert.Equal("System.Threading.Lock", retypedField.FieldType.FullName);

        var readerFieldRef = (FieldReference)reader.Body.Instructions
            .Single(i => i.OpCode == OpCodes.Ldfld).Operand;
        Assert.Equal("System.Threading.Lock", readerFieldRef.FieldType.FullName);
    }

    [Fact]
    public void RetypeFieldsIsANoOpWhenTypesAlreadyMatch()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib");

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        AddObjectGateField(vanillaType, vanilla.MainModule);
        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        AddObjectGateField(compiledType, compiled.MainModule);

        FieldDefinition fieldBefore = GetGateField(vanillaType);
        IReadOnlyList<FieldDefinition> retyped = MemberInjector.RetypeFields(
            vanilla, compiled, "Fixture.Gate", new List<string> { "gate" });

        FieldDefinition retypedField = Assert.Single(retyped);
        Assert.Same(fieldBefore, retypedField);
        Assert.Equal("System.Object", retypedField.FieldType.FullName);

        // Re-running is safe - still a no-op, still the same instance.
        IReadOnlyList<FieldDefinition> retypedAgain = MemberInjector.RetypeFields(
            vanilla, compiled, "Fixture.Gate", new List<string> { "gate" });
        Assert.Same(fieldBefore, Assert.Single(retypedAgain));
    }

    [Fact]
    public void RetypeFieldsThrowsWhenTheFieldIsMissingFromVanilla()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib");

        AddFixtureType(vanilla.MainModule); // no "gate" field
        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        AddLockGateField(compiledType, compiled.MainModule);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MemberInjector.RetypeFields(vanilla, compiled, "Fixture.Gate", new List<string> { "gate" }));
        Assert.Contains("gate", exception.Message);
        Assert.Contains("vanilla", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetypeFieldsThrowsWhenTheFieldIsMissingFromTheDonor()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib");

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        AddObjectGateField(vanillaType, vanilla.MainModule);
        AddFixtureType(compiled.MainModule); // no "gate" field

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MemberInjector.RetypeFields(vanilla, compiled, "Fixture.Gate", new List<string> { "gate" }));
        Assert.Contains("gate", exception.Message);
        Assert.Contains("donor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FieldInitializerRetargetRewritesTheObjectCtorNewobj()
    {
        using var resolver = new DefaultAssemblyResolver();
        AddRuntimeSearchDirectories(resolver);
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib", resolver);
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib", resolver);

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        AddObjectGateField(vanillaType, vanilla.MainModule);
        AddCtorInitializingGate(vanillaType, vanilla.MainModule, GetGateField(vanillaType));
        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        AddLockGateField(compiledType, compiled.MainModule);

        IReadOnlyList<FieldDefinition> retyped = MemberInjector.RetypeFields(
            vanilla, compiled, "Fixture.Gate", new List<string> { "gate" });

        HashSet<string> retargeted = ILPatcher.RetargetFieldInitializers(vanilla, retyped);

        MethodDefinition ctor = vanillaType.Methods.Single(m => m.Name == ".ctor");
        Assert.Contains(MethodSignature.GetKey(ctor), retargeted);

        Instruction newobj = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj);
        var ctorRef = (MethodReference)newobj.Operand;
        Assert.Equal("System.Threading.Lock", ctorRef.DeclaringType.FullName);
        Assert.Empty(ctorRef.Parameters);
    }

    [Fact]
    public void FieldInitializerRetargetThrowsOnAnUnexpectedPredecessor()
    {
        using var resolver = new DefaultAssemblyResolver();
        AddRuntimeSearchDirectories(resolver);
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib", resolver);
        using AssemblyDefinition compiled = CreateAssembly("VintagestoryLib", resolver);

        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        FieldDefinition gate = AddObjectGateField(vanillaType, vanilla.MainModule);

        // ldarg.0; ldnull; stfld ... - not the expected "newobj System.Object::.ctor()".
        MethodDefinition ctor = new(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName, vanilla.MainModule.TypeSystem.Void);
        vanillaType.Methods.Add(ctor);
        ILProcessor il = ctor.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Stfld, gate));
        il.Append(Instruction.Create(OpCodes.Ret));

        TypeDefinition compiledType = AddFixtureType(compiled.MainModule);
        AddLockGateField(compiledType, compiled.MainModule);

        IReadOnlyList<FieldDefinition> retyped = MemberInjector.RetypeFields(
            vanilla, compiled, "Fixture.Gate", new List<string> { "gate" });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ILPatcher.RetargetFieldInitializers(vanilla, retyped));
        Assert.Contains("gate", exception.Message);
        Assert.Contains("unexpected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetypedFieldReaderVerifierAcceptsTransplantedReaders()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        FieldDefinition gate = AddObjectGateField(vanillaType, vanilla.MainModule);
        MethodDefinition reader = AddGateReaderMethod(vanillaType, vanilla.MainModule, gate);

        var accepted = new HashSet<string> { MethodSignature.GetKey(reader) };
        List<string> errors = RetypedFieldReaderVerifier.Verify(
            vanilla.MainModule, new List<FieldDefinition> { gate }, accepted);

        Assert.Empty(errors);
    }

    [Fact]
    public void RetypedFieldReaderVerifierRejectsAnUntransplantedReader()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        FieldDefinition gate = AddObjectGateField(vanillaType, vanilla.MainModule);
        MethodDefinition reader = AddGateReaderMethod(vanillaType, vanilla.MainModule, gate);

        List<string> errors = RetypedFieldReaderVerifier.Verify(
            vanilla.MainModule, new List<FieldDefinition> { gate }, new HashSet<string>());

        string error = Assert.Single(errors);
        Assert.Contains("Fixture.Gate", error);
        Assert.Contains(reader.Name, error);
        Assert.Contains("gate", error);
    }

    [Fact]
    public void RetypedFieldReaderVerifierIgnoresUnrelatedFields()
    {
        using AssemblyDefinition vanilla = CreateAssembly("VintagestoryLib");
        TypeDefinition vanillaType = AddFixtureType(vanilla.MainModule);
        FieldDefinition gate = AddObjectGateField(vanillaType, vanilla.MainModule);
        FieldDefinition unrelated = new("other", FieldAttributes.Assembly, vanilla.MainModule.TypeSystem.Int32);
        vanillaType.Fields.Add(unrelated);

        MethodDefinition reader = new("ReadUnrelated", MethodAttributes.Public, vanilla.MainModule.TypeSystem.Void);
        vanillaType.Methods.Add(reader);
        ILProcessor il = reader.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, unrelated));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ret));

        List<string> errors = RetypedFieldReaderVerifier.Verify(
            vanilla.MainModule, new List<FieldDefinition> { gate }, new HashSet<string>());

        Assert.Empty(errors);
    }

    private static AssemblyDefinition CreateAssembly(string name, IAssemblyResolver? resolver = null)
    {
        var moduleParameters = new ModuleParameters { Kind = ModuleKind.Dll };
        if (resolver != null)
        {
            moduleParameters.AssemblyResolver = resolver;
        }
        return AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0)), name, moduleParameters);
    }

    /// <summary>
    /// Cecil's DefaultAssemblyResolver only searches directories added explicitly
    /// (plus a few GAC-era defaults) - it does not automatically find the running
    /// process's own shared framework directory, so resolving a real BCL type like
    /// System.Threading.Lock back to its TypeDefinition needs this pointed at it.
    /// </summary>
    private static void AddRuntimeSearchDirectories(DefaultAssemblyResolver resolver)
    {
        string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            resolver.AddSearchDirectory(runtimeDir);
        }
    }

    private static TypeDefinition AddFixtureType(ModuleDefinition module)
    {
        TypeDefinition type = new(
            "Fixture", "Gate", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    private static FieldDefinition AddObjectGateField(TypeDefinition type, ModuleDefinition module)
    {
        FieldDefinition field = new("gate", FieldAttributes.Assembly, module.TypeSystem.Object);
        type.Fields.Add(field);
        return field;
    }

    private static FieldDefinition AddLockGateField(TypeDefinition type, ModuleDefinition module)
    {
        TypeReference lockType = module.ImportReference(typeof(System.Threading.Lock));
        FieldDefinition field = new("gate", FieldAttributes.Assembly | FieldAttributes.InitOnly, lockType);
        type.Fields.Add(field);
        return field;
    }

    private static FieldDefinition GetGateField(TypeDefinition type) =>
        type.Fields.Single(f => f.Name == "gate");

    private static void AddCtorInitializingGate(TypeDefinition type, ModuleDefinition module, FieldDefinition gate)
    {
        MethodDefinition ctor = new(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        type.Methods.Add(ctor);
        ILProcessor il = ctor.Body.GetILProcessor();
        MethodReference objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Newobj, objectCtor));
        il.Append(Instruction.Create(OpCodes.Stfld, gate));
        il.Append(Instruction.Create(OpCodes.Ret));
    }

    private static MethodDefinition AddGateReaderMethod(TypeDefinition type, ModuleDefinition module, FieldDefinition gate)
    {
        MethodDefinition reader = new("ReadGate", MethodAttributes.Public, module.TypeSystem.Void);
        type.Methods.Add(reader);
        ILProcessor il = reader.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, gate));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ret));
        return reader;
    }
}
