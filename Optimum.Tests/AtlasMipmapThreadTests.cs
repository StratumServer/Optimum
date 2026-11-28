using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// Vanilla's "Faster startup" setting (OffThreadMipMapCreation) queued atlas mipmap generation on a thread pool
// worker. That worker has no current GL context, so the GL calls built no mipmaps for the block and item atlases
// and, on some Windows setups, crashed in GL.BindTexture while a world loaded. Mipmaps now always run as a game
// launch task on the main thread.
public sealed class AtlasMipmapThreadTests
{
    private static AssemblyDefinition ReadLib() =>
        AssemblyDefinition.ReadAssembly(PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll"));

    private static TypeDefinition Startup(AssemblyDefinition lib) =>
        lib.MainModule.GetType("Vintagestory.Client.NoObf.ClientSystemStartup");

    private static IEnumerable<MethodDefinition> MethodsWithClosures(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(nested => nested.Methods)).Where(method => method.HasBody);

    private static IEnumerable<string> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
            .Select(i => ((MethodReference)i.Operand).Name);

    [Fact]
    public void FinaliseTextureAtlasIgnoresTheOffThreadSetting()
    {
        using AssemblyDefinition lib = ReadLib();
        MethodDefinition finalise = Startup(lib).Methods.Single(m => m.Name == "FinaliseTextureAtlas");

        Assert.DoesNotContain("get_OffThreadMipMapCreation", Calls(finalise));
        Assert.Equal(1, Calls(finalise).Count(name => name == "EnqueueGameLaunchTask"));
        Assert.Equal(1, Calls(finalise).Count(name => name == "QueueTask"));
    }

    [Fact]
    public void MipmapStageHasOneCallerQueuedOnTheMainThread()
    {
        using AssemblyDefinition lib = ReadLib();
        List<MethodDefinition> callers = MethodsWithClosures(Startup(lib))
            .Where(method => Calls(method).Contains("FinaliseTextureAtlas_StageB"))
            .ToList();

        // One lambda, the one FinaliseTextureAtlas passes to EnqueueGameLaunchTask
        Assert.Single(callers);
        MethodDefinition finalise = Startup(lib).Methods.Single(m => m.Name == "FinaliseTextureAtlas");
        List<Instruction> body = finalise.Body.Instructions.ToList();
        int lambda = body.FindIndex(i => i.OpCode == OpCodes.Ldftn && ((MethodReference)i.Operand).Resolve() == callers[0]);
        Assert.True(lambda >= 0, "FinaliseTextureAtlas does not create the mipmap stage lambda");
        int enqueue = body.FindIndex(lambda, i => i.Operand is MethodReference m && m.Name == "EnqueueGameLaunchTask");
        int queue = body.FindIndex(lambda, i => i.Operand is MethodReference m && m.Name == "QueueTask");
        Assert.True(lambda >= 0 && enqueue > lambda && (queue < 0 || queue > enqueue), $"lambda={lambda} enqueue={enqueue} queue={queue}");
    }

    // Guard, not a regression test: the checks above read the compiled fork, so they only matter while the
    // patcher keeps transplanting this method into the shipped Lib
    [Fact]
    public void FinaliseTextureAtlasStaysATransplantTarget()
    {
        string patcher = System.IO.File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientSystemStartup\", \"FinaliseTextureAtlas\", 3)", patcher);
    }
}
