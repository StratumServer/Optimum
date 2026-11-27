using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// The OIT disable flags are static. Once a failure turned OIT off, it stayed off in every later world of the same
// process. The renderer now clears them when it is disposed with its world.
public sealed class OitDisablePerSessionTests
{
    [Fact]
    public void BeforeOitDispose_ClearsTheStaticDisableFlags()
    {
        string path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        using AssemblyDefinition lib = AssemblyDefinition.ReadAssembly(path);
        var il = lib.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderOITLayers").NestedTypes
            .Single(t => t.Name == "BeforeOIT").Methods.Single(m => m.Name == "Dispose" && !m.HasParameters)
            .Body.Instructions.ToList();

        foreach (string flag in new[] { "optimumOitDisabled", "optimumOitFailureLogged" })
        {
            int store = il.FindIndex(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == flag);
            Assert.True(store > 0, $"Dispose must reset {flag}");
            Assert.Equal(OpCodes.Ldc_I4_0, il[store - 1].OpCode);
        }
    }

    [Fact]
    public void BeforeOitDisposeShipsInTheLib()
    {
        string patcher = System.IO.File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT\", \"Dispose\", 0)", patcher);
    }
}
