using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The patcher reports rejected validation checks on Console.Error, which test runners and CI logs
/// do not capture. Issue #564 had a failing runner test whose only message was "the patcher
/// rejected the generated assembly". The checks now travel in the exception message.
/// </summary>
public sealed class PatcherValidationMessageTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("optimum-patcher-validation-");

    public void Dispose() => temp.Delete(recursive: true);

    [Fact]
    public void PatchWithInjectionReportsTheRejectedCheckAndWritesNoOutput()
    {
        string vanilla = Path.Combine(temp.FullName, "vanilla", "Fixture.dll");
        string donor = Path.Combine(temp.FullName, "donor", "Fixture.dll");
        string output = Path.Combine(temp.FullName, "out", "Fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        Directory.CreateDirectory(Path.GetDirectoryName(donor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BuildFixture(vanilla, withInjectedField: false);
        BuildFixture(donor, withInjectedField: true);

        var validationErrors = new List<string>();
        int total = ILPatcher.PatchWithInjection(
            vanilla,
            donor,
            output,
            new(),
            new() { ["Fixture.Holder"] = ["optimumValue"] },
            new(),
            validationErrors: validationErrors);

        Assert.Equal(-1, total);
        string error = Assert.Single(validationErrors);
        Assert.Contains("injected-field initializer", error);
        Assert.Contains("Fixture.Holder::optimumValue", error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PatchWithInjectionRejectsAStaticFieldInitializedByADonorStaticConstructor()
    {
        string vanilla = Path.Combine(temp.FullName, "vanilla", "StaticFixture.dll");
        string donor = Path.Combine(temp.FullName, "donor", "StaticFixture.dll");
        string output = Path.Combine(temp.FullName, "out", "StaticFixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        Directory.CreateDirectory(Path.GetDirectoryName(donor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BuildStaticFixture(vanilla, withInjectedStaticField: false, initializeInStaticConstructor: false);
        BuildStaticFixture(donor, withInjectedStaticField: true, initializeInStaticConstructor: true);

        var validationErrors = new List<string>();
        int total = ILPatcher.PatchWithInjection(
            vanilla,
            donor,
            output,
            new(),
            new() { ["Fixture.Holder"] = ["optimumNames"] },
            new(),
            validationErrors: validationErrors,
            verifyStaticFieldInitializers: true);

        Assert.Equal(-1, total);
        string error = Assert.Single(validationErrors);
        Assert.Contains("Fixture.Holder::optimumNames", error);
        Assert.Contains("static", error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PatchWithInjectionAcceptsAStaticFieldThatIsAssignedWhereItIsRead()
    {
        string vanilla = Path.Combine(temp.FullName, "vanilla", "StaticFixture.dll");
        string donor = Path.Combine(temp.FullName, "donor", "StaticFixture.dll");
        string output = Path.Combine(temp.FullName, "out", "StaticFixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        Directory.CreateDirectory(Path.GetDirectoryName(donor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BuildStaticFixture(vanilla, withInjectedStaticField: false, initializeInStaticConstructor: false);
        BuildStaticFixture(donor, withInjectedStaticField: true, initializeInStaticConstructor: false);

        var validationErrors = new List<string>();
        int total = ILPatcher.PatchWithInjection(
            vanilla,
            donor,
            output,
            new(),
            new() { ["Fixture.Holder"] = ["optimumNames"] },
            new(),
            validationErrors: validationErrors,
            verifyStaticFieldInitializers: true);

        Assert.True(total >= 0, string.Join("; ", validationErrors));
        Assert.Empty(validationErrors);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void PatchWithInjectionAcceptsAnAllowListedStaticFieldInitializer()
    {
        var validationErrors = new List<string>();
        string output = PatchStaticFixture(new HashSet<string> { "Fixture.Holder::optimumNames" }, validationErrors);

        Assert.Empty(validationErrors);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void AnAllowListDoesNotHideOtherStaticFields()
    {
        var validationErrors = new List<string>();
        string output = PatchStaticFixture(new HashSet<string> { "Fixture.Holder::somethingElse" }, validationErrors);

        string error = Assert.Single(validationErrors);
        Assert.Contains("Fixture.Holder::optimumNames", error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void LibStaticInitializerAllowListHasWellFormedUniqueEntries()
    {
        Assert.NotEmpty(LibStaticInitializerAllowList.Names);
        foreach (string name in LibStaticInitializerAllowList.Names)
        {
            string[] parts = name.Split("::");
            Assert.Equal(2, parts.Length);
            Assert.StartsWith("Vintagestory.", parts[0]);
            Assert.NotEmpty(parts[1]);
        }
    }

    private string PatchStaticFixture(IReadOnlySet<string> allowed, List<string> validationErrors)
    {
        string vanilla = Path.Combine(temp.FullName, "vanilla", "StaticFixture.dll");
        string donor = Path.Combine(temp.FullName, "donor", "StaticFixture.dll");
        string output = Path.Combine(temp.FullName, "out", "StaticFixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        Directory.CreateDirectory(Path.GetDirectoryName(donor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BuildStaticFixture(vanilla, withInjectedStaticField: false, initializeInStaticConstructor: false);
        BuildStaticFixture(donor, withInjectedStaticField: true, initializeInStaticConstructor: true);

        ILPatcher.PatchWithInjection(
            vanilla,
            donor,
            output,
            new(),
            new() { ["Fixture.Holder"] = ["optimumNames"] },
            new(),
            validationErrors: validationErrors,
            verifyStaticFieldInitializers: true,
            allowedStaticFieldInitializers: allowed);
        return output;
    }

    [Fact]
    public void ValidationFailureMessageNamesTheChecksAndCapsTheList()
    {
        var errors = new List<string>();
        for (int i = 0; i < ModPatcher.MaxReportedValidationErrors + 3; i++)
        {
            errors.Add($"invalid IL: error {i}");
        }

        string message = ModPatcher.FormatValidationFailure("vsessentials", errors);

        Assert.StartsWith("vsessentials patch failed output validation.", message);
        Assert.Contains("invalid IL: error 0", message);
        Assert.Contains($"invalid IL: error {ModPatcher.MaxReportedValidationErrors - 1}", message);
        Assert.DoesNotContain($"invalid IL: error {ModPatcher.MaxReportedValidationErrors}", message);
        Assert.EndsWith("(+3 more)", message);
    }

    [Fact]
    public void ValidationFailureMessageWithoutErrorsStaysGeneric()
    {
        string message = ModPatcher.FormatValidationFailure("vssurvivalmod", []);

        Assert.Equal(
            "vssurvivalmod patch failed output validation. The patcher rejected the generated assembly.",
            message);
    }

    // The donor's constructor initializes the injected field, but vanilla's constructor is not a
    // transplant target, so the field would silently stay at its CLR default.
    private static void BuildFixture(string path, bool withInjectedField)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var holder = new TypeDefinition(
            "Fixture", "Holder",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(holder);

        FieldDefinition? field = null;
        if (withInjectedField)
        {
            field = new FieldDefinition("optimumValue", Mono.Cecil.FieldAttributes.Private, module.TypeSystem.Int32);
            holder.Fields.Add(field);
        }

        var ctor = new MethodDefinition(
            ".ctor",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
            Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        var objectCtor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object)
        {
            HasThis = true,
        };
        il.Emit(OpCodes.Call, objectCtor);
        if (field is not null)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, int.MinValue);
            il.Emit(OpCodes.Stfld, field);
        }
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(ctor);

        assembly.Write(path);
    }

    // Static counterpart of BuildFixture: the donor's static constructor assigns the injected static
    // field, vanilla has no static constructor at all, so nothing would ever run the assignment.
    private static void BuildStaticFixture(string path, bool withInjectedStaticField, bool initializeInStaticConstructor)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("StaticFixture", new Version(1, 0)), "StaticFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var holder = new TypeDefinition(
            "Fixture", "Holder",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(holder);

        var objectCtor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object)
        {
            HasThis = true,
        };
        var ctor = new MethodDefinition(
            ".ctor",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
            Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var ctorIl = ctor.Body.GetILProcessor();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, objectCtor);
        ctorIl.Emit(OpCodes.Ret);
        holder.Methods.Add(ctor);

        if (withInjectedStaticField)
        {
            var field = new FieldDefinition(
                "optimumNames",
                Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                new ArrayType(module.TypeSystem.String));
            holder.Fields.Add(field);

            if (initializeInStaticConstructor)
            {
                var cctor = new MethodDefinition(
                    ".cctor",
                    Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static |
                    Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName |
                    Mono.Cecil.MethodAttributes.RTSpecialName,
                    module.TypeSystem.Void);
                var il = cctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Newarr, module.TypeSystem.String);
                il.Emit(OpCodes.Stsfld, field);
                il.Emit(OpCodes.Ret);
                holder.Methods.Add(cctor);
            }
        }

        assembly.Write(path);
    }
}
