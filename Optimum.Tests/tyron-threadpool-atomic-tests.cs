using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class TyronThreadPoolAtomicTests
{
    [Fact]
    public void TyronThreadPoolPatch_UsesInterlockedIncrementInMarkStartedAndCreateDedicatedThread()
    {
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryApi/Common/TyronThreadPool.cs.patch"));

        Assert.Contains("+            int key = Interlocked.Increment(ref keyCounter) - 1;", patch);
        Assert.Contains("+            int key = Interlocked.Increment(ref Inst.dedicatedCounter) - 1;", patch);
        Assert.Contains("+            Inst.DedicatedThreads[key] = thread;", patch);
        Assert.Contains("-            Inst.DedicatedThreads[Inst.dedicatedCounter++] = thread;", patch);
    }

    [Fact]
    public void ApiPatcher_PatchTyronThreadPoolDiagnostics_RewritesCountersToInterlockedIncrement()
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("SyntheticVanillaApi", new Version(1, 0, 0, 0)),
            "SyntheticVanillaApi.dll",
            ModuleKind.Dll);
        var module = assembly.MainModule;

        var threadPoolType = new TypeDefinition(
            "Vintagestory.API.Common",
            "TyronThreadPool",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(threadPoolType);

        var instField = new FieldDefinition(
            "Inst",
            FieldAttributes.Public | FieldAttributes.Static,
            threadPoolType);
        var runningTasksType = module.ImportReference(typeof(ConcurrentDictionary<int, string>));
        var dedicatedThreadsType = module.ImportReference(typeof(ConcurrentDictionary<int, Thread>));
        var runningTasksField = new FieldDefinition("RunningTasks", FieldAttributes.Public, runningTasksType);
        var dedicatedThreadsField = new FieldDefinition("DedicatedThreads", FieldAttributes.Public, dedicatedThreadsType);
        var keyCounterField = new FieldDefinition("keyCounter", FieldAttributes.Private, module.TypeSystem.Int32);
        var dedicatedCounterField = new FieldDefinition("dedicatedCounter", FieldAttributes.Private, module.TypeSystem.Int32);

        threadPoolType.Fields.Add(instField);
        threadPoolType.Fields.Add(runningTasksField);
        threadPoolType.Fields.Add(dedicatedThreadsField);
        threadPoolType.Fields.Add(keyCounterField);
        threadPoolType.Fields.Add(dedicatedCounterField);

        var objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
        var setMaxThreads = module.ImportReference(
            typeof(ThreadPool).GetMethod(nameof(ThreadPool.SetMaxThreads), new[] { typeof(int), typeof(int) })!);

        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        {
            var il = ctor.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, objectCtor));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)10));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Call, setMaxThreads));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(Instruction.Create(OpCodes.Ret));
        }
        threadPoolType.Methods.Add(ctor);

        var runningTasksSetItem = module.ImportReference(
            typeof(ConcurrentDictionary<int, string>).GetMethod("set_Item", new[] { typeof(int), typeof(string) })!);
        var markStarted = new MethodDefinition(
            "MarkStarted",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Int32);
        markStarted.Parameters.Add(new ParameterDefinition("caller", ParameterAttributes.None, module.TypeSystem.String));
        markStarted.Body.InitLocals = true;
        markStarted.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        markStarted.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        {
            var il = markStarted.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, keyCounterField));
            il.Append(Instruction.Create(OpCodes.Stloc_1));
            il.Append(Instruction.Create(OpCodes.Ldloc_1));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, keyCounterField));
            il.Append(Instruction.Create(OpCodes.Ldloc_1));
            il.Append(Instruction.Create(OpCodes.Stloc_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldfld, runningTasksField));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Callvirt, runningTasksSetItem));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Ret));
        }
        threadPoolType.Methods.Add(markStarted);

        var threadType = module.ImportReference(typeof(Thread));
        var threadStartType = module.ImportReference(typeof(ThreadStart));
        var threadCtor = module.ImportReference(typeof(Thread).GetConstructor(new[] { typeof(ThreadStart) })!);
        var setIsBackground = module.ImportReference(typeof(Thread).GetProperty(nameof(Thread.IsBackground))!.GetSetMethod()!);
        var setName = module.ImportReference(typeof(Thread).GetProperty(nameof(Thread.Name))!.GetSetMethod()!);
        var dedicatedThreadsSetItem = module.ImportReference(
            typeof(ConcurrentDictionary<int, Thread>).GetMethod("set_Item", new[] { typeof(int), typeof(Thread) })!);

        var createDedicatedThread = new MethodDefinition(
            "CreateDedicatedThread",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            threadType);
        createDedicatedThread.Parameters.Add(new ParameterDefinition("starter", ParameterAttributes.None, threadStartType));
        createDedicatedThread.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        createDedicatedThread.Body.InitLocals = true;
        createDedicatedThread.Body.Variables.Add(new VariableDefinition(threadType));
        createDedicatedThread.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        {
            var il = createDedicatedThread.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Newobj, threadCtor));
            il.Append(Instruction.Create(OpCodes.Stloc_0));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Callvirt, setIsBackground));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Callvirt, setName));
            il.Append(Instruction.Create(OpCodes.Ldsfld, instField));
            il.Append(Instruction.Create(OpCodes.Ldfld, dedicatedThreadsField));
            il.Append(Instruction.Create(OpCodes.Ldsfld, instField));
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldfld, dedicatedCounterField));
            il.Append(Instruction.Create(OpCodes.Stloc_1));
            il.Append(Instruction.Create(OpCodes.Ldloc_1));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stfld, dedicatedCounterField));
            il.Append(Instruction.Create(OpCodes.Ldloc_1));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Callvirt, dedicatedThreadsSetItem));
            il.Append(Instruction.Create(OpCodes.Ldloc_0));
            il.Append(Instruction.Create(OpCodes.Ret));
        }
        threadPoolType.Methods.Add(createDedicatedThread);

        Assert.Equal(1, ApiPatcher.PatchTyronThreadPoolDiagnostics(module));
        Assert.Equal(2, ApiPatcher.PatchTyronThreadPoolAtomicCounters(module));

        Assert.Contains(markStarted.Body.Instructions, i =>
            i.OpCode == OpCodes.Ldflda && i.Operand is FieldReference f && f.Name == "keyCounter");
        Assert.Contains(markStarted.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
            m.DeclaringType.FullName == "System.Threading.Interlocked" && m.Name == "Increment");
        Assert.DoesNotContain(markStarted.Body.Instructions, i =>
            i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "keyCounter");

        Assert.Contains(createDedicatedThread.Body.Instructions, i =>
            i.OpCode == OpCodes.Ldflda && i.Operand is FieldReference f && f.Name == "dedicatedCounter");
        Assert.Contains(createDedicatedThread.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
            m.DeclaringType.FullName == "System.Threading.Interlocked" && m.Name == "Increment");
        Assert.DoesNotContain(createDedicatedThread.Body.Instructions, i =>
            i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "dedicatedCounter");

        Assert.Empty(IlStackVerifier.VerifyModule(module));
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(module));
    }
}
