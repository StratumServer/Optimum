using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Coverage tests for the shader reload lifecycle in OptimumMapPageRenderer.
/// Validates that the renderer subscribes to shader reload events and recreates
/// its program when the engine triggers ReloadShaders(), which disposes all
/// registered shader programs.
/// </summary>
public sealed class MapShaderReloadCoverageTests
{
    [Fact]
    public void PageRendererSubscribesToReloadShaderEvent()
    {
        string source = Read("sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs");

        Assert.Contains("_capi.Event.ReloadShader += OnReloadShader", source);
    }

    [Fact]
    public void PageRendererUnsubscribesOnDispose()
    {
        string source = Read("sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs");

        Assert.Contains("_capi.Event.ReloadShader -= OnReloadShader", source);
    }

    [Fact]
    public void OnReloadShaderRecreatesTheProgram()
    {
        string source = Read("sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs");

        Assert.Contains("private bool OnReloadShader()", source);
        Assert.Contains("_shader?.Dispose();", source);
        Assert.Contains("CreateShader();", source);
        Assert.Contains("return true;", source);
    }

    [Fact]
    public void OnReloadShaderSkipsWhenDisposed()
    {
        string source = Read("sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs");

        Assert.Contains("if (_disposed) return true;", source);
    }

    [Fact]
    public void ReadyPropertyChecksShaderDisposedState()
    {
        string source = Read("sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs");

        Assert.Contains("!_shader.Disposed", source);
    }

    [Fact]
    public void ChunkMapLayerRendersAllComponentsAfterInstancedPass()
    {
        string patch = PatchReader.ReadPatch(
            "patches/runtime/VSEssentials/Vintagestory/GameContent/ChunkMapLayer.cs.patch");

        // The instanced pass renders pages as a background layer at Z=50.01.
        // All loaded MultiChunkMapComponent instances render afterward at Z=50
        // (in front) so Harmony patches from mods like TerraTag still fire.
        Assert.Contains("pageRenderer.EndFrame(capi.Render.FrameWidth, capi.Render.FrameHeight);", patch);
        Assert.Contains("foreach (KeyValuePair<FastVec2i, MultiChunkMapComponent> item in loadedMapData)", patch);
        Assert.Contains("item.Value.Render(mapElem, dt);", patch);

        // Confirm no selective skip (old coverage-based culling removed)
        Assert.DoesNotContain("allCovered", patch);
        Assert.DoesNotContain("coveredPages", patch);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
