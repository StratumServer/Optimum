using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class ChunkReadPoolLifecycleTests
{
    [Fact]
    public void ShutdownHookInsertsThePoolDisposeBeforeTheDatabaseDispose()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("OptimumLifecycleFixture", new Version(1, 0, 0, 0)),
            "OptimumLifecycleFixture",
            ModuleKind.Dll);
        ModuleDefinition module = assembly.MainModule;

        var gameDatabase = new TypeDefinition(
            "Vintagestory.Common",
            "GameDatabase",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(gameDatabase);

        var databaseDispose = new MethodDefinition(
            "Dispose",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        gameDatabase.Methods.Add(databaseDispose);
        databaseDispose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        var loadSave = new TypeDefinition(
            "Vintagestory.Server",
            "ServerSystemLoadAndSaveGame",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(loadSave);

        var databaseField = new FieldDefinition(
            "gameDatabase",
            FieldAttributes.Private,
            gameDatabase);
        loadSave.Fields.Add(databaseField);

        var poolDispose = new MethodDefinition(
            "DisposeOptimumChunkReadPool",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        loadSave.Methods.Add(poolDispose);
        poolDispose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        var shutdown = new MethodDefinition(
            "OnSeperateThreadShutDown",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        loadSave.Methods.Add(shutdown);
        var processor = shutdown.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldfld, databaseField));
        processor.Append(Instruction.Create(OpCodes.Callvirt, databaseDispose));
        processor.Append(Instruction.Create(OpCodes.Ret));

        bool inserted = ILHook.InsertInstanceVoidCallBefore(
            assembly,
            "Vintagestory.Server.ServerSystemLoadAndSaveGame",
            "OnSeperateThreadShutDown",
            0,
            "DisposeOptimumChunkReadPool",
            "Dispose",
            "Vintagestory.Common.GameDatabase",
            Array.Empty<string>(),
            "System.Void",
            targetHasThis: true,
            targetExplicitThis: false,
            MethodCallingConvention.Default,
            targetGenericArity: 0);

        Assert.True(inserted);

        int databaseDisposeIndex = shutdown.Body.Instructions
            .Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.Operand is MethodReference method && method.Name == "Dispose")
            .index;

        Assert.Equal(OpCodes.Ldarg_0, shutdown.Body.Instructions[databaseDisposeIndex - 2].OpCode);
        Assert.Equal(OpCodes.Call, shutdown.Body.Instructions[databaseDisposeIndex - 1].OpCode);
        Assert.Equal("DisposeOptimumChunkReadPool", ((MethodReference)shutdown.Body.Instructions[databaseDisposeIndex - 1].Operand!).Name);
        Assert.Equal(OpCodes.Callvirt, shutdown.Body.Instructions[databaseDisposeIndex].OpCode);

        // The patcher should be safe if a caller retries the same hook against
        // an already-modified module.
        Assert.True(ILHook.InsertInstanceVoidCallBefore(
            assembly,
            "Vintagestory.Server.ServerSystemLoadAndSaveGame",
            "OnSeperateThreadShutDown",
            0,
            "DisposeOptimumChunkReadPool",
            "Dispose",
            "Vintagestory.Common.GameDatabase",
            Array.Empty<string>(),
            "System.Void",
            targetHasThis: true,
            targetExplicitThis: false,
            MethodCallingConvention.Default,
            targetGenericArity: 0));

        Assert.Equal(1, shutdown.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference method && method.Name == "DisposeOptimumChunkReadPool"));
    }

    [Fact]
    public void OptimumPatchOwnsPoolShutdownBeforeClosingTheSaveDatabase()
    {
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch");
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string ilHook = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/ILHook.cs"));

        Assert.Contains("DisposeOptimumChunkReadPool();", patch);
        Assert.Contains("chunkthread.optimumReadPool = null;", patch);
        Assert.Contains("pool?.Dispose();", patch);
        Assert.True(
            patch.IndexOf("chunkthread.optimumReadPool = null;", StringComparison.Ordinal) <
            patch.IndexOf("pool?.Dispose();", StringComparison.Ordinal));
        Assert.Contains("\"DisposeOptimumChunkReadPool\"", program);
        Assert.Contains("InsertBeforeTarget: true", program);
        Assert.Contains("InsertInstanceVoidCallBefore", ilHook);
        Assert.Contains("expected exactly one call", ilHook);
    }
}
