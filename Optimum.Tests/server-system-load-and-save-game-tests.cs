using System;
using System.IO;
using System.Linq;
using Xunit;
#if !NO_DONOR
using Mono.Cecil;
using Mono.Cecil.Cil;
#endif

namespace Optimum.Tests;

public sealed class ServerSystemLoadAndSaveGameTests
{
    [Fact]
    public void ServerSystemLoadAndSaveGamePatch_ClearsChunksCopyInFinallyBlock()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch");
        Assert.True(File.Exists(patchPath), "ServerSystemLoadAndSaveGame.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("finally", patched);
        Assert.Contains("chunksCopy.Clear();", patched);
    }

#if !NO_DONOR
    [Fact]
    public void SaveAllDirtyLoadedChunks_ClearsChunksCopyUponCompletion()
    {
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "VintagestoryLib.dll");
        if (!File.Exists(assemblyPath))
        {
            assemblyPath = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        }
        if (!File.Exists(assemblyPath)) return;

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var type = assembly.MainModule.GetType("Vintagestory.Server.ServerSystemLoadAndSaveGame");
        Assert.NotNull(type);

        var method = type.Methods.FirstOrDefault(m => m.Name == "SaveAllDirtyLoadedChunks");
        Assert.NotNull(method);
        Assert.True(method.HasBody);

        // Verify exception handler exists for finally block
        Assert.NotEmpty(method.Body.ExceptionHandlers);
        var finallyHandler = method.Body.ExceptionHandlers.FirstOrDefault(h => h.HandlerType == ExceptionHandlerType.Finally);
        Assert.NotNull(finallyHandler);

        // Verify chunksCopy.Clear() is invoked inside the method
        bool clearsDictionary = method.Body.Instructions.Any(inst =>
            inst.OpCode == OpCodes.Callvirt &&
            inst.Operand is MethodReference target &&
            target.Name == "Clear" &&
            target.DeclaringType.Name.StartsWith("Dictionary"));

        Assert.True(clearsDictionary, "SaveAllDirtyLoadedChunks must call Clear() on chunksCopy");
    }
#endif
}
