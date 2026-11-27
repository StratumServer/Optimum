using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// Issue #778: a window resize on the loading screen (a saved size below the 600x400 minimum, or dragging the
// window) rebuilt the frame buffers before shaders loaded. Reading ShaderRegistry there ran its static
// constructor early, which published an uncompiled ShaderPrograms.Gui, and the loading screen crashed with
// KeyNotFoundException on its first GUI draw. Vanilla 1.22.7 crashes the same way.
public sealed class LoadingScreenResizeShaderTests
{
    [Fact]
    public void RebuildFrameBuffers_ChecksGuiBeforeTouchingShaderRegistry()
    {
        string path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        using AssemblyDefinition lib = AssemblyDefinition.ReadAssembly(path);
        MethodDefinition rebuild = lib.MainModule.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows")
            .Methods.Single(m => m.Name == "RebuildFrameBuffers" && !m.HasParameters);

        var loads = rebuild.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld).ToList();
        int gui = loads.FindIndex(i => Is(i, "ShaderPrograms", "Gui"));
        int suppress = loads.FindIndex(i => Is(i, "ShaderRegistry", "SupressShaderAndBufferReloads"));

        Assert.True(gui >= 0, "RebuildFrameBuffers must check ShaderPrograms.Gui");
        Assert.True(suppress > gui, "ShaderPrograms.Gui must be checked before ShaderRegistry is touched");
        Assert.Equal(FlowControl.Cond_Branch, loads[gui].Next.OpCode.FlowControl);
    }

    private static bool Is(Instruction load, string type, string field) =>
        load.Operand is FieldReference f && f.DeclaringType.Name == type && f.Name == field;
}
