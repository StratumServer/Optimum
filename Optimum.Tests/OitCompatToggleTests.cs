using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// When the shader compatibility scan turns "Oit" off, the settings screen says Optimum's OIT is off. The check
// used to sit in the SystemRenderOITLayers constructor, which is not transplanted, so the shipped renderer kept the
// framebuffer texture cache anyway. The check now lives in the transplanted methods: the cache is skipped and
// rebuild frees the previous texture pair the way vanilla does.
public sealed class OitCompatToggleTests
{
    private static TypeDefinition OitLayers(AssemblyDefinition lib) =>
        lib.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderOITLayers");

    private static AssemblyDefinition ReadLib()
    {
        string path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static int CallIndex(MethodDefinition method, string name) =>
        method.Body.Instructions.ToList().FindIndex(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name);

    [Theory]
    [InlineData("OptimumTryActivateCachedOit")]
    [InlineData("OptimumStoreCachedOit")]
    public void CacheHelpersCheckTheToggleFirst(string helper)
    {
        using AssemblyDefinition lib = ReadLib();
        MethodDefinition method = OitLayers(lib).Methods.Single(m => m.Name == helper);

        Assert.Equal(0, CallIndex(method, "get_EffectiveOit"));
    }

    [Fact]
    public void RebuildFreesThePreviousPairWhenTheCacheIsOff()
    {
        using AssemblyDefinition lib = ReadLib();
        MethodDefinition rebuild = OitLayers(lib).NestedTypes.Single(t => t.Name == "BeforeOIT")
            .Methods.Single(m => m.Name == "rebuild");

        int toggle = CallIndex(rebuild, "get_EffectiveOit");
        int free = CallIndex(rebuild, "freeResources");
        int cleanup = CallIndex(rebuild, "OptimumCleanupDisposedOitEntries");
        Assert.Equal(0, toggle);
        Assert.True(free > toggle && free < cleanup, $"toggle={toggle} free={free} cleanup={cleanup}");
    }

    [Fact]
    public void CheckedMethodsShipInTheLib()
    {
        string patcher = System.IO.File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT\", \"rebuild\", 0)", patcher);
        Assert.Contains("\"OptimumTryActivateCachedOit\",", patcher);
        Assert.Contains("\"OptimumStoreCachedOit\",", patcher);
    }

    [Fact]
    public void ConstructorRegistersBothRenderersWithoutTheDeadGate()
    {
        using AssemblyDefinition lib = ReadLib();
        MethodDefinition ctor = OitLayers(lib).Methods.Single(m => m.IsConstructor && !m.IsStatic);

        Assert.Equal(-1, CallIndex(ctor, "get_EffectiveOit"));
        Assert.Equal(2, ctor.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "RegisterRenderer"));
    }
}
