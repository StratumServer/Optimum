using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Reflection = System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class ChunkReadPoolLifecycleTests
{
    [Fact]
    public void ConstructorClosesConnectionWhenInitializationFailsAfterOpen()
    {
        string? donorPath = TryFindRepositoryFile(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (donorPath is null) return;

        InitializeSqliteProvider();
        Reflection.Assembly donor = Reflection.Assembly.LoadFrom(donorPath);
        Type poolType = donor.GetType("Vintagestory.Server.OptimumChunkReadPool")!;
        Reflection.ConstructorInfo constructor = poolType
            .GetConstructors(Reflection.BindingFlags.Instance | Reflection.BindingFlags.Public | Reflection.BindingFlags.NonPublic)
            .Single(candidate =>
            {
                Reflection.ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                    parameters[3].ParameterType.IsGenericType &&
                    parameters[3].ParameterType.GetGenericTypeDefinition() == typeof(Action<>);
            });

        Type connectionType = constructor.GetParameters()[3].ParameterType.GetGenericArguments()[0];
        Type callbackType = typeof(Action<>).MakeGenericType(connectionType);
        var openedConnections = new List<object>();
        Delegate failAfterOpen = CreateFailingCallback(callbackType, connectionType, openedConnections);
        string databasePath = Path.Combine(
            Path.GetTempPath(), $"optimum-pool-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(databasePath, Array.Empty<byte>());

        try
        {
            Reflection.TargetInvocationException exception = Assert.Throws<Reflection.TargetInvocationException>(() =>
                constructor.Invoke(new object[] { databasePath, 2, false, failAfterOpen }));

            Assert.True(
                exception.InnerException is InvalidOperationException,
                exception.InnerException?.ToString());
            Assert.Single(openedConnections);
            Reflection.PropertyInfo state = openedConnections[0].GetType().GetProperty("State")!;
            Assert.Equal("Closed", state.GetValue(openedConnections[0])!.ToString());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

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

    private static Delegate CreateFailingCallback(
        Type callbackType,
        Type connectionType,
        List<object> openedConnections)
    {
        ParameterExpression connection = Expression.Parameter(connectionType, "connection");
        Reflection.MethodInfo add = typeof(List<object>).GetMethod(nameof(List<object>.Add))!;
        Expression recordConnection = Expression.Call(
            Expression.Constant(openedConnections),
            add,
            Expression.Convert(connection, typeof(object)));
        Expression throwFailure = Expression.Throw(
            Expression.New(typeof(InvalidOperationException)),
            typeof(void));
        return Expression.Lambda(
            callbackType,
            Expression.Block(recordConnection, throwFailure),
            connection).Compile();
    }

    private static void InitializeSqliteProvider()
    {
        string? batteriesPath = TryFindRepositoryFile(
            OperatingSystem.IsWindows()
                ? ".vanilla/win-x64/vintagestory/Lib/SQLitePCLRaw.batteries_v2.dll"
                : ".vanilla/linux-x64/vintagestory/Lib/SQLitePCLRaw.batteries_v2.dll");
        batteriesPath ??= TryFindRepositoryFile(
            ".vanilla/win-x64/vintagestory/Lib/SQLitePCLRaw.batteries_v2.dll");
        if (batteriesPath is null) return;

        string providerDirectory = Path.GetDirectoryName(batteriesPath)!;
        string providerPath = Path.Combine(providerDirectory, "SQLitePCLRaw.provider.e_sqlite3.dll");
        string[] nativeNames = OperatingSystem.IsWindows()
            ? ["e_sqlite3.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libe_sqlite3.dylib", "libe_sqlite3.so"]
                : ["libe_sqlite3.so"];
        string? nativePath = nativeNames
            .Select(name => Path.Combine(providerDirectory, name))
            .FirstOrDefault(File.Exists);
        if (!File.Exists(providerPath) || nativePath is null) return;

        Reflection.Assembly provider = Reflection.Assembly.LoadFrom(providerPath);
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            provider,
            (name, _, _) => name is "e_sqlite3" or "e_sqlite3.dll"
                ? System.Runtime.InteropServices.NativeLibrary.Load(nativePath)
                : IntPtr.Zero);
        Reflection.Assembly batteries = Reflection.Assembly.LoadFrom(batteriesPath);
        batteries.GetType("SQLitePCL.Batteries_V2")!.GetMethod("Init")!.Invoke(null, null);
    }

    private static string? TryFindRepositoryFile(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

}
