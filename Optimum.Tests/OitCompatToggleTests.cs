using System.IO;
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
    private static TypeDefinition ShippedOitLayers(AssemblyDefinition lib) =>
        lib.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderOITLayers");

    private static AssemblyDefinition ReadShippedLib()
    {
        string patchedDll = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"),
            "..", "..", "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"));
        Assert.True(File.Exists(patchedDll), $"Missing {patchedDll}; run make patch-il first.");
        return AssemblyDefinition.ReadAssembly(patchedDll);
    }

    private static int CallIndex(MethodDefinition method, string name) =>
        method.Body.Instructions.ToList().FindIndex(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name);

    [Theory]
    [InlineData("OptimumTryActivateCachedOit")]
    [InlineData("OptimumStoreCachedOit")]
    public void CacheHelpersCheckTheToggleFirst(string helper)
    {
        using AssemblyDefinition lib = ReadShippedLib();
        MethodDefinition method = ShippedOitLayers(lib).Methods.Single(m => m.Name == helper);

        Assert.Equal(0, CallIndex(method, "get_EffectiveOit"));
    }

    [Fact]
    public void RebuildFreesThePreviousPairWhenTheCacheIsOff()
    {
        using AssemblyDefinition lib = ReadShippedLib();
        MethodDefinition rebuild = ShippedOitLayers(lib).NestedTypes.Single(t => t.Name == "BeforeOIT")
            .Methods.Single(m => m.Name == "rebuild");

        int toggle = CallIndex(rebuild, "get_EffectiveOit");
        int free = CallIndex(rebuild, "freeResources");
        int cleanup = CallIndex(rebuild, "OptimumCleanupDisposedOitEntries");
        Assert.Equal(0, toggle);
        Assert.True(free > toggle && free < cleanup, $"toggle={toggle} free={free} cleanup={cleanup}");
    }

    [Fact]
    public void ShippedConstructorStillRegistersBothRenderers()
    {
        using AssemblyDefinition lib = ReadShippedLib();
        MethodDefinition ctor = ShippedOitLayers(lib).Methods.Single(m => m.IsConstructor && !m.IsStatic);

        Assert.Equal(-1, CallIndex(ctor, "get_EffectiveOit"));
        Assert.Equal(2, ctor.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "RegisterRenderer"));
    }
}
