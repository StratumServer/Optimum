using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace Optimum.Tests;

/// <summary>
/// A transplanted method that creates a lambda shares the C# compiler's closure class
/// (&lt;&gt;c__DisplayClassN_0) with vanilla. The patcher merged the donor closure into
/// vanilla's but skipped any donor lambda whose signature already existed, so vanilla's lambda
/// body survived and read a closure field the transplanted owner no longer wrote.
/// BlockEntityGroundStorage.getOrCreateMesh shipped that way in v0.4.0 to v0.4.2: the owner wrote
/// the donor field mesh2, the vanilla lambda read the vanilla field mesh, and
/// UploadMultiTextureMesh(null) threw a NullReferenceException on the first main-thread task.
/// </summary>
public sealed class PatcherClosureLambdaTransplantTests : IDisposable
{
    private const string ClosureName = "<>c__DisplayClass0_0";
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("optimum-closure-lambda-");

    public void Dispose() => temp.Delete(recursive: true);

    [Fact]
    public void ADonorLambdaOfTheTransplantedMethodReplacesVanillasLambdaBody()
    {
        using var output = PatchFixtures();
        var closure = output.MainModule.GetType("Fixture.Holder").NestedTypes.Single(t => t.Name == ClosureName);

        var lambda = closure.Methods.Single(m => m.Name == "<Run>b__0");
        string[] fieldsRead = lambda.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name).ToArray();

        Assert.Equal(new[] { "donorValue" }, fieldsRead);
    }

    [Fact]
    public void ALambdaOfAnotherMethodKeepsVanillasBody()
    {
        using var output = PatchFixtures();
        var closure = output.MainModule.GetType("Fixture.Holder").NestedTypes.Single(t => t.Name == ClosureName);

        var lambda = closure.Methods.Single(m => m.Name == "<Other>b__1");
        string[] fieldsRead = lambda.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name).ToArray();

        Assert.Equal(new[] { "value" }, fieldsRead);
    }

    [Fact]
    public void EveryClosureFieldAPatchedLambdaReadsIsWrittenByTheTransplantedOwner()
    {
        using var output = PatchFixtures();
        var holder = output.MainModule.GetType("Fixture.Holder");
        var closure = holder.NestedTypes.Single(t => t.Name == ClosureName);
        var owner = holder.Methods.Single(m => m.Name == "Run");

        string[] written = owner.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Stfld).Select(i => ((FieldReference)i.Operand).Name).ToArray();
        string[] read = closure.Methods.Single(m => m.Name == "<Run>b__0").Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name).ToArray();

        Assert.All(read, field => Assert.Contains(field, written));
    }

    private AssemblyDefinition PatchFixtures()
    {
        string vanilla = Path.Combine(temp.FullName, "vanilla", "Fixture.dll");
        string donor = Path.Combine(temp.FullName, "donor", "Fixture.dll");
        string output = Path.Combine(temp.FullName, "out", "Fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        Directory.CreateDirectory(Path.GetDirectoryName(donor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BuildFixture(vanilla, vanillaSide: true);
        BuildFixture(donor, vanillaSide: false);

        var errors = new System.Collections.Generic.List<string>();
        int total = ILPatcher.PatchWithInjection(
            vanilla, donor, output, new(), new(),
            new() { new MethodTarget("Fixture.Holder", "Run", 0) },
            validationErrors: errors);

        Assert.True(total >= 0, string.Join("; ", errors));
        return AssemblyDefinition.ReadAssembly(output);
    }

    // Vanilla: closure field `value`, <Run>b__0 and <Other>b__1 both read it.
    // Donor: Run writes `donorValue` and <Run>b__0 reads it; <Other>b__1 reads `donorValue` too.
    private static void BuildFixture(string path, bool vanillaSide)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var holder = new TypeDefinition("Fixture", "Holder",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(holder);

        var closure = new TypeDefinition("", ClosureName,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        holder.NestedTypes.Add(closure);

        string fieldName = vanillaSide ? "value" : "donorValue";
        var field = new FieldDefinition(fieldName, FieldAttributes.Public, module.TypeSystem.Int32);
        closure.Fields.Add(field);

        var objectCtor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true };
        var closureCtor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var ctorIl = closureCtor.Body.GetILProcessor();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, objectCtor);
        ctorIl.Emit(OpCodes.Ret);
        closure.Methods.Add(closureCtor);

        var runLambda = ReadFieldLambda("<Run>b__0", module, field);
        var otherLambda = ReadFieldLambda("<Other>b__1", module, field);
        closure.Methods.Add(runLambda);
        closure.Methods.Add(otherLambda);

        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Int32);
        var il = run.Body.GetILProcessor();
        if (vanillaSide)
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.Emit(OpCodes.Newobj, closureCtor);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_5);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Call, runLambda);
            il.Emit(OpCodes.Ret);
        }
        holder.Methods.Add(run);

        assembly.Write(path);
    }

    private static MethodDefinition ReadFieldLambda(string name, ModuleDefinition module, FieldDefinition field)
    {
        var lambda = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Int32);
        var il = lambda.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return lambda;
    }
}
