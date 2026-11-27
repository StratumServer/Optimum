using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// While the game was shutting down, sound sources and shared buffers were released without deleting them from
// OpenAL, so the driver reported leaked sources and buffers at exit. Vanilla deletes them; the context generation
// check already skips deletion once the context is gone.
public sealed class OpenAlShutdownCleanupTests
{
    [Theory]
    [InlineData("Vintagestory.Client.LoadedSoundNative", "disposeSoundSource", "DeleteSource")]
    [InlineData("Vintagestory.Client.NoObf.AudioMetaData", "ReleaseSharedBuffer", "DeleteBuffer")]
    [InlineData("Vintagestory.Client.NoObf.AudioMetaData", "EvictIdleSharedBuffers", "DeleteBuffer")]
    public void ReleasePaths_DeleteWhileShuttingDownAndGuardOnlyOnContextGeneration(string type, string method, string delete)
    {
        string path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        using AssemblyDefinition lib = AssemblyDefinition.ReadAssembly(path);
        var il = lib.MainModule.GetType(type).Methods.Single(m => m.Name == method).Body.Instructions.ToList();

        Assert.Contains(il, i => i.Operand is MethodReference m && m.Name == delete);
        Assert.Contains(il, i => i.Operand is FieldReference f && f.Name == "ContextGeneration");
        Assert.DoesNotContain(il, i => i.Operand is MethodReference m && m.Name == "get_IsShuttingDown");
    }
}
