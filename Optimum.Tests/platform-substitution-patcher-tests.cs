using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Synthetic-module tests for the patcher's platform substitution step (typesToUnseal,
/// methodsToVirtualize and the call-versus-callvirt dispatch verifier). Same fixture pattern
/// as member-injector-tests.cs: in-memory Cecil modules named VintagestoryLib.
/// </summary>
public sealed class PlatformSubstitutionPatcherTests
{
    private const string PlatformName = "Vintagestory.Client.NoObf.ClientPlatformWindows";
    private const string CallerName = "Vintagestory.Client.ClientProgram";

    private static readonly List<MethodTarget> Virtualize = new()
    {
        new(PlatformName, "SetupDefaultFrameBuffers", 0),
    };

    [Fact]
    public void UnsealClearsOnlySealed()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeAttributes before = platform.Attributes;
        Assert.True(platform.IsSealed);

        int unsealed = PlatformSubstitution.UnsealTypes(assembly.MainModule, new[] { PlatformName });

        Assert.Equal(1, unsealed);
        Assert.False(platform.IsSealed);
        Assert.Equal(before & ~TypeAttributes.Sealed, platform.Attributes);
    }

    [Fact]
    public void VirtualizeSetsVirtualNewSlotHideBySigAndKeepsVisibility()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public);
        MethodDefinition protectedMethod = AddVoidMethod(platform, "RenderFullscreenTriangle", MethodAttributes.Family);

        PlatformSubstitution.VirtualizeMethods(assembly.MainModule, new List<MethodTarget>
        {
            new(PlatformName, "SetupDefaultFrameBuffers", 0),
            new(PlatformName, "RenderFullscreenTriangle", 0),
        });

        MethodDefinition setup = Find(platform, "SetupDefaultFrameBuffers");
        Assert.Equal(
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            setup.Attributes);
        Assert.True(setup.IsPublic);
        Assert.Equal(
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            protectedMethod.Attributes);
    }

    [Fact]
    public void PrivateEntryFailsThePatch()
    {
        using AssemblyDefinition assembly = CreateModule();
        AddPlatform(assembly.MainModule, MethodAttributes.Private | MethodAttributes.HideBySig);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PlatformSubstitution.VirtualizeMethods(assembly.MainModule, Virtualize));

        Assert.Contains("private", error.Message);
        Assert.Contains("SetupDefaultFrameBuffers", error.Message);
    }

    [Fact]
    public void CallToVirtualizedMethodFailsTheVerifierNamingTheCaller()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(assembly.MainModule, platform, OpCodes.Call);
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out int virtualSites);

        string error = Assert.Single(errors);
        Assert.Contains(CallerName + "::Start", error);
        Assert.Contains("SetupDefaultFrameBuffers", error);
        Assert.Contains(" call ", error);
        Assert.Equal(0, virtualSites);
    }

    [Fact]
    public void CallvirtToVirtualizedMethodPassesTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(assembly.MainModule, platform, OpCodes.Callvirt);
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out int virtualSites);

        Assert.Empty(errors);
        Assert.Equal(1, virtualSites);
    }

    [Fact]
    public void LdftnInANestedTypeFailsTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeDefinition caller = AddCaller(assembly.MainModule, platform, OpCodes.Callvirt);
        TypeDefinition closure = new("", "<>c", TypeAttributes.NestedPrivate | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        caller.NestedTypes.Add(closure);
        MethodDefinition lambda = new("<Start>b__0", MethodAttributes.Assembly | MethodAttributes.HideBySig, assembly.MainModule.TypeSystem.Void);
        closure.Methods.Add(lambda);
        ILProcessor il = lambda.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldftn, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out _);

        string error = Assert.Single(errors);
        Assert.Contains(CallerName + "/<>c::<Start>b__0", error);
        Assert.Contains("ldftn", error);
    }

    [Fact]
    public void BaseCallFromASubclassPassesTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeDefinition derived = new("Fixture", "DerivedPlatform", TypeAttributes.Public | TypeAttributes.Class, platform);
        assembly.MainModule.Types.Add(derived);
        MethodDefinition overrideMethod = new(
            "SetupDefaultFrameBuffers",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            assembly.MainModule.TypeSystem.Void);
        derived.Methods.Add(overrideMethod);
        ILProcessor il = overrideMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Ret));
        Apply(assembly.MainModule);

        Assert.Empty(Verify(assembly.MainModule, out _));
    }

    [Fact]
    public void VerifierFailsWhenTheFlagsWereNotApplied()
    {
        using AssemblyDefinition assembly = CreateModule();
        AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);

        List<string> errors = Verify(assembly.MainModule, out _);

        Assert.Contains(errors, error => error.Contains("still carries TypeAttributes.Sealed"));
        Assert.Contains(errors, error => error.Contains("is not virtual"));
    }

    [Fact]
    public void PatchWithInjectionKeepsFlagsOnTransplantedMethodsAndRefusesAStrayCall()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-platform-substitution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "vanilla"));
        Directory.CreateDirectory(Path.Combine(directory, "compiled"));
        try
        {
            string compiledPath = Path.Combine(directory, "compiled", "VintagestoryLib.dll");
            using (AssemblyDefinition compiled = CreateModule())
            {
                TypeDefinition platform = AddPlatform(
                    compiled.MainModule,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                    sealedType: false);
                AddCaller(compiled.MainModule, platform, OpCodes.Callvirt);
                compiled.Write(compiledPath);
            }

            var targets = new List<MethodTarget>
            {
                new(PlatformName, "SetupDefaultFrameBuffers", 0),
                new(CallerName, "Start", 0),
            };

            // 1. Both the virtualized method and its caller are transplanted: flags survive, output written.
            string vanillaPath = WriteVanilla(directory, "vanilla-ok.dll", OpCodes.Call);
            string outputPath = Path.Combine(directory, "patched-ok.dll");
            int result = ILPatcher.PatchWithInjection(
                vanillaPath, compiledPath, outputPath, new List<string>(), new Dictionary<string, List<string>>(), targets,
                typesToUnseal: new List<string> { PlatformName }, methodsToVirtualize: Virtualize);

            Assert.True(result > 0);
            using (AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(outputPath))
            {
                TypeDefinition platform = patched.MainModule.GetType(PlatformName);
                Assert.False(platform.IsSealed);
                MethodDefinition setup = Find(platform, "SetupDefaultFrameBuffers");
                Assert.True(setup.IsVirtual && setup.IsNewSlot && setup.IsHideBySig && setup.IsPublic);
                Assert.Contains(
                    Find(patched.MainModule.GetType(CallerName), "Start").Body.Instructions,
                    instruction => instruction.OpCode == OpCodes.Callvirt);
            }

            // 2. The caller is not transplanted and keeps its vanilla `call`: the patch is refused.
            string strayPath = WriteVanilla(directory, "vanilla-stray.dll", OpCodes.Call);
            string strayOutput = Path.Combine(directory, "patched-stray.dll");
            int refused = ILPatcher.PatchWithInjection(
                strayPath, compiledPath, strayOutput, new List<string>(), new Dictionary<string, List<string>>(),
                new List<MethodTarget> { targets[0] },
                typesToUnseal: new List<string> { PlatformName }, methodsToVirtualize: Virtualize);

            Assert.Equal(-1, refused);
            Assert.False(File.Exists(strayOutput));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteVanilla(string directory, string fileName, OpCode callOpCode)
    {
        string path = Path.Combine(directory, "vanilla", fileName);
        using AssemblyDefinition vanilla = CreateModule();
        TypeDefinition platform = AddPlatform(vanilla.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(vanilla.MainModule, platform, callOpCode);
        vanilla.Write(path);
        return path;
    }

    private static void Apply(ModuleDefinition module)
    {
        PlatformSubstitution.UnsealTypes(module, new[] { PlatformName });
        PlatformSubstitution.VirtualizeMethods(module, Virtualize);
    }

    private static List<string> Verify(ModuleDefinition module, out int virtualSites) =>
        PlatformSubstitution.VerifyVirtualDispatch(module, new[] { PlatformName }, Virtualize, out virtualSites);

    private static AssemblyDefinition CreateModule() =>
        AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

    private static TypeDefinition AddPlatform(ModuleDefinition module, MethodAttributes setupAttributes, bool sealedType = true)
    {
        TypeAttributes attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        if (sealedType) attributes |= TypeAttributes.Sealed;
        TypeDefinition platform = new("Vintagestory.Client.NoObf", "ClientPlatformWindows", attributes, module.TypeSystem.Object);
        module.Types.Add(platform);
        AddVoidMethod(platform, "SetupDefaultFrameBuffers", setupAttributes);
        return platform;
    }

    private static MethodDefinition AddVoidMethod(TypeDefinition type, string name, MethodAttributes attributes)
    {
        MethodDefinition method = new(name, attributes, type.Module.TypeSystem.Void);
        type.Methods.Add(method);
        method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        return method;
    }

    private static TypeDefinition AddCaller(ModuleDefinition module, TypeDefinition platform, OpCode callOpCode)
    {
        TypeDefinition caller = new("Vintagestory.Client", "ClientProgram", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(caller);
        MethodDefinition start = new("Start", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
        start.Parameters.Clear();
        caller.Methods.Add(start);
        start.Body.Variables.Add(new VariableDefinition(platform));
        ILProcessor il = start.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(callOpCode, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Ret));
        return caller;
    }

    private static MethodDefinition Find(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name == name) return method;
        }
        throw new InvalidOperationException($"{type.FullName}::{name} not found");
    }
}
