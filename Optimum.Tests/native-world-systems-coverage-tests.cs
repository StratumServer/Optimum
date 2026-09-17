using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The world systems on the native device API (docs/vulkan-native-render-systems.md, decision 5
/// stage 2 onwards). Each system that moves gets a seam in the library whose neutral body is the
/// OpenGL body's own draw, a patcher listing for that seam, and a Vulkan override that records a
/// native pass with the old route kept reachable behind a switch.
///
/// The sky dome was the first; the night sky box, the moon, the cube particle pool and the decal
/// pool followed. Later stages (chunks, entities, GUI) add their seams to the same lists here
/// rather than to a file named after the stage.
/// </summary>
public class NativeWorldSystemsCoverageTests
{
    /// <summary>The double quote the patcher's listings are spelled with, so assertions can name them.</summary>
    private const string Q = "\"";

    private const string SkyPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSky.cs";
    private const string ChunkPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeChunks.cs";
    private const string WorldPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs";
    private const string GuiPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs";
    private const string DeviceMeshFile = "Optimum.Render.Vulkan/VulkanDevice.NativeMesh.cs";
    private const string DeviceNativeFile = "Optimum.Render.Vulkan/VulkanDevice.Native.cs";

    private const string EntityPlatformFile =
        "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeEntities.cs";

    /// <summary>
    /// The seam exists on the platform abstraction, and its neutral body is exactly the
    /// RenderMesh call it replaced - which is what makes "OFF is vanilla" true for OpenGL,
    /// because ClientPlatformWindows does not override it at all.
    /// </summary>
    [Fact]
    public void TheSkyDomeHasASeamWhoseNeutralBodyIsTheDrawItReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual void RenderSkyDome(MeshRef skyDome, int skyTextureId, int glowTextureId, float[] modelViewMatrix)",
            platform);
        Assert.Contains("RenderMesh(skyDome);", platform);

        // The OpenGL platform leaves it alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("RenderSkyDome", windows);
    }

    /// <summary>
    /// SystemRenderSkyColor draws through the seam and hands it the values a native pass cannot
    /// read off the GL state: the two textures and the model-view matrix.
    /// </summary>
    [Fact]
    public void TheSkyRendererDrawsThroughTheSeam()
    {
        string system = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs");

        Assert.Contains(
            "game.Platform.RenderSkyDome(skyIcosahedron, game.skyTextureId, game.skyGlowTextureId, game.CurrentModelViewMatrix);",
            system);
        Assert.DoesNotContain("game.Platform.RenderMesh(skyIcosahedron);", system);
    }

    /// <summary>Every new or changed lib member is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheSeamAndItsCallerAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderSkyDome\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderSkyColor\", \"OnRenderFrame3D\", 1", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the sky as a native pass, states its own fixed state rather
    /// than reading the tracker's, and keeps the neutral body reachable behind a switch in the
    /// pattern of NativeBlitEnabled.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheSkyNativelyAndKeepsTheOldRoute()
    {
        string sky = Read(SkyPlatformFile);

        // On by default; OPTIMUM_VK_NATIVE_SKY=0 turns the route off in the real client.
        Assert.Contains("internal bool NativeSkyEnabled { get; set; } = Environment.GetEnvironmentVariable(\"OPTIMUM_VK_NATIVE_SKY\") != \"0\";", sky);
        Assert.Contains("public override void RenderSkyDome(", sky);
        Assert.Contains("base.RenderSkyDome(", sky);
        Assert.Contains("device.BeginNativePass(", sky);
        Assert.Contains("device.DrawNativeMesh(", sky);
        Assert.Contains("device.EndNativePass();", sky);

        // The state the pass states outright, and the per-draw write.
        Assert.Contains("DepthTest = false", sky);
        Assert.Contains("DepthWrite = false", sky);
        Assert.Contains("Cull = CullModeFlags.None", sky);
        Assert.Contains("device.WriteNative(pipeline, nativeSky.Uniforms[0]", sky);

        // The pipeline is built for the mesh's own vertex layout, not the fullscreen one.
        Assert.Contains("device.NativeMeshLayoutId(", sky);
        Assert.Contains("VertexLayoutId = layoutId", sky);
    }

    /// <summary>
    /// The device records mesh draws through the mesh manager it already has - indexed,
    /// non-indexed, instanced and multi-draw through the per-slot indirect ring - and never
    /// builds a second mesh path.
    /// </summary>
    [Fact]
    public void TheDeviceRecordsEveryMeshDrawKindThroughTheExistingMeshPath()
    {
        string mesh = Read(DeviceMeshFile);

        foreach (string entry in new[]
                 {
                     "internal bool DrawNativeMesh(",
                     "internal bool DrawNativeMeshInstanced(",
                     "internal bool DrawNativeMeshArrays(",
                     "internal bool DrawNativeMeshMulti(",
                 })
        {
            Assert.Contains(entry, mesh);
        }

        // The existing machinery, reused: the mesh manager binds and draws, and the multi-draw
        // allocates from the same indirect ring the emulated DrawMeshMulti allocates from.
        Assert.Contains("_meshes.Bind(commandBuffer, mesh!);", mesh);
        Assert.Contains("_meshes.DrawMulti(commandBuffer, meshId,", mesh);
        Assert.Contains("AllocateIndirect(groupCount, out ulong indirectOffset)", mesh);

        // The real mesh id reaches BindProgramSets, which is what makes a chunk's storage-buffer
        // vertex fetch and an entity's animation block resolve per draw.
        Assert.Contains("BeginNativeDraw(pipeline, textures, meshId,", mesh);

        // Counted apart from fullscreen draws.
        foreach (string counter in new[]
                 {
                     "NoteNativeFullscreenDraw", "NoteNativeMeshDraw",
                     "NoteNativeInstancedDraw", "NoteNativeIndirectDraw",
                 })
        {
            Assert.Contains(counter, mesh);
        }

        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("native_fullscreen_draws=", stats);
        Assert.Contains("native_mesh_draws=", stats);
        Assert.Contains("native_instanced_draws=", stats);
        Assert.Contains("native_indirect_draws=", stats);
    }

    /// <summary>
    /// The pipeline description carries what a mesh draw needs and a fullscreen draw did not,
    /// and every one of those dimensions is in the key, so a mesh pipeline can never be handed
    /// out for a fullscreen request or the other way round.
    /// </summary>
    [Fact]
    public void ThePipelineDescriptionAndKeyCarryTheMeshDrawState()
    {
        string native = Read(DeviceNativeFile);

        foreach (string field in new[]
                 {
                     "public FrontFace FrontFace = RenderLimits.FrontFace;",
                     "public PolygonMode PolygonMode = PolygonMode.Fill;",
                     "public float LineWidth = 1.0f;",
                     "public int VertexLayoutId = MeshManager.EmptyLayoutId;",
                     "public bool SamplesBoundDepth;",
                 })
        {
            Assert.Contains(field, native);
        }

        string key = Section(native, "private readonly record struct NativePipelineCacheKey(", ");");
        foreach (string dimension in new[]
                 {
                     "VertexLayoutId", "PolygonMode", "FrontFace", "LineWidth", "SamplesBoundDepth",
                 })
        {
            Assert.Contains(dimension, key);
        }

        // The pipeline-cache key takes the layout and polygon mode from the description too,
        // rather than the fullscreen constants stage 1 baked in.
        Assert.Contains("VertexLayoutId: description.VertexLayoutId,", native);
        Assert.Contains("PolygonMode: description.PolygonMode,", native);
        Assert.Contains("_meshes.LayoutOf(description.VertexLayoutId)", native);
    }

    /// <summary>
    /// The chunk groups have a seam of their own, and its neutral bodies do nothing at all -
    /// which is what keeps the OpenGL path drawing exactly the bodies it drew before, with its
    /// GlToggleBlend / depth / cull calls still in place.
    /// </summary>
    [Fact]
    public void TheChunkGroupsHaveAScopeSeamWhoseNeutralBodyDoesNothing()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual bool BeginChunkPass(string chunkPass, bool blend, bool depthTest, bool depthWrite, bool cullFace)",
            platform);
        Assert.Contains("public virtual void EndChunkPass()", platform);

        // The OpenGL platform leaves both alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("BeginChunkPass", windows);
        Assert.DoesNotContain("EndChunkPass", windows);
    }

    /// <summary>
    /// Every ChunkRenderer draw group brackets its pools with the seam and states the fixed
    /// state that group runs under - and still makes the GL state calls the OpenGL path needs,
    /// because those are what the GL body draws with.
    /// </summary>
    [Fact]
    public void EveryChunkDrawGroupDrawsInsideTheScope()
    {
        string renderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        foreach (string group in new[]
                 {
                     "chunk-shadow-opaque", "chunk-shadow-topsoil", "chunk-shadow-vegetation",
                     "chunk-shadow-blendnocull", "chunk-opaque", "chunk-topsoil", "chunk-vegetation",
                     "chunk-blendnocull", "chunk-decorative", "chunk-oit-liquid", "chunk-oit-transparent",
                     "chunk-liquid-motion", "chunk-overlay",
                 })
        {
            Assert.Contains("platform.BeginChunkPass(\"" + group + "\"", renderer);
        }

        // One close per open, and each in a finally, so a throwing pool draw cannot leave a
        // pass open for the rest of the frame.
        int opens = Count(renderer, "platform.BeginChunkPass(");
        int closes = Count(renderer, "platform.EndChunkPass();");
        Assert.Equal(13, opens);
        Assert.Equal(opens, closes);

        // "OFF is vanilla": the GL state the OpenGL body draws under is still set.
        Assert.Contains("platform.GlToggleBlend(on: false);", renderer);
        Assert.Contains("platform.GlEnableCullFace();", renderer);
        Assert.Contains("platform.GlDepthMask(flag: true);", renderer);
    }

    /// <summary>Every new or changed lib member of the chunk port is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheChunkSeamAndItsCallersAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"BeginChunkPass\"", patcher);
        Assert.Contains("\"EndChunkPass\"", patcher);
        foreach (string method in new[] { "RenderShadow", "RenderOpaque", "RenderOIT", "RenderAfterOIT" })
        {
            Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"" + method + "\", 1", patcher);
        }
        // RenderLiquidMotion is an injected member rather than a transplanted vanilla one.
        Assert.Contains("\"RenderLiquidMotion\"", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the chunk groups as native passes with indirect multi-draws,
    /// states its own fixed state, expresses the motion window as a colour-write mask, and keeps
    /// the old route reachable behind a switch.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheChunkGroupsNativelyAndKeepsTheOldRoute()
    {
        string chunks = Read(ChunkPlatformFile);

        // On by default; OPTIMUM_VK_NATIVE_CHUNKS=0 turns the route off in the real client.
        Assert.Contains("internal bool NativeChunksEnabled { get; set; } = Environment.GetEnvironmentVariable(\"OPTIMUM_VK_NATIVE_CHUNKS\") != \"0\";", chunks);
        Assert.Contains("public override bool BeginChunkPass(", chunks);
        Assert.Contains("public override void EndChunkPass()", chunks);
        Assert.Contains("device.BeginNativePass(", chunks);
        Assert.Contains("device.EndNativePass();", chunks);

        // The multi-draw stays a multi-draw, over the mesh's own vertex layout.
        Assert.Contains("device.DrawNativeMeshMulti(", chunks);
        Assert.Contains("VertexLayoutId = layoutId", chunks);
        Assert.Contains("device.NativeMeshLayoutId(", chunks);

        // The motion window is a write mask, never a draw-buffer toggle.
        Assert.Contains("if (chunkScopeMotionOnly && slot != motion) entry.WriteMask = 0;", chunks);
        Assert.DoesNotContain("SetDrawBuffers", chunks);

        // The state is stated, not read back off the tracker.
        Assert.Contains("DepthTest = chunkScopeDepthTest", chunks);
        Assert.Contains("DepthWrite = chunkScopeDepthWrite", chunks);
        Assert.Contains("Cull = chunkScopeCull ? CullModeFlags.BackBit : CullModeFlags.None", chunks);
        Assert.Contains("SamplesBoundDepth = samplesBoundDepth", chunks);

        // The route in: the pool's multi-draw seam takes the native path only inside a scope.
        string meshes = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs");
        Assert.Contains("if (TryDrawChunkPoolNative(vAO, indices, indicesSizes, groupCount)) return;", meshes);
        Assert.Contains("TryDrawStated(vAO, 1, indices, indicesSizes, groupCount);", meshes);
    }

    /// <summary>
    /// The values a native chunk pass cannot read off GL state are recorded where the client
    /// states them: the texture behind each sampler, and the Transparent target's blend contract.
    /// </summary>
    [Fact]
    public void TheClientStateANativeChunkPassNeedsIsRecordedAtItsOwnSeam()
    {
        string shaders = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Shaders.cs");
        Assert.Contains("NoteNativeProgramTexture(program.ProgramId, samplerName, textureId);", shaders);
        // A relinked program's cached interface and pipelines go with it.
        Assert.Contains("ForgetNativeChunkProgram(program.ProgramId);", shaders);

        string leaf = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Leaf.cs");
        Assert.Contains("NoteNativeTransparentBlend(0, 32774, 774, 0, 774, 0);", leaf);

        string buffers = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("NoteNativeTransparentBlend(2, 32774, 770, 771, 770, 771);", buffers);
    }

    // ------------------------------------------------------------- entities (stage 2)

    /// <summary>
    /// The entity draw seam exists on the platform abstraction, its neutral body is exactly the
    /// RenderMesh call it replaced, and ClientPlatformWindows does not override it - which is what
    /// makes "OFF is vanilla" true for OpenGL.
    /// </summary>
    [Fact]
    public void TheEntityDrawHasASeamWhoseNeutralBodyIsTheDrawItReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual void RenderEntityMesh(MeshRef mesh, string samplerName, int textureId)",
            platform);
        Assert.Contains("RenderMesh(mesh);", platform);

        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("RenderEntityMesh", windows);
    }

    /// <summary>
    /// The entity renderers reach the seam where they already were: RenderMultiTextureMesh draws
    /// each sub-mesh through it and hands it the sampler name and texture id it just bound, which
    /// is what a native pass needs to resolve the draw's texture from a handle.
    /// </summary>
    [Fact]
    public void TheMultiTextureDrawGoesThroughTheSeam()
    {
        string api = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs");

        Assert.Contains("plat.RenderEntityMesh(vao, textureSampleName, mmr.textureids[i]);", api);
        Assert.DoesNotContain("plat.RenderMesh(vao);", api);
    }

    /// <summary>The seam and its caller are listed for the Cecil transplant.</summary>
    [Fact]
    public void TheEntitySeamAndItsCallerAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderEntityMesh\"", patcher);
        Assert.Contains("\"Vintagestory.Client.RenderAPIBase\", \"RenderMultiTextureMesh\", 3", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the entity draws natively for the two programs it owns, states
    /// its own fixed state rather than reading the tracker's, treats the motion window as a colour
    /// write mask, and keeps the neutral body reachable behind a switch.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsEntitiesNativelyAndKeepsTheOldRoute()
    {
        string entities = Read(EntityPlatformFile);

        // On by default; OPTIMUM_VK_NATIVE_ENTITIES=0 turns the route off in the real client.
        // Vanilla entity programs only (decision 1): a mod program under the same pass name stays on the adapter.
        Assert.Contains("!ReferenceEquals(program, ShaderPrograms.Entityanimated)", entities);
        // The registry's own program under the pass name (the first-person hands), looked up only
        // for a program the registry registered.
        Assert.Contains("!IsRegistryProgram(program)", entities);
        Assert.Contains("program.PassId > 0 && program.PassName != null", entities);
        Assert.Contains("!ReferenceEquals(program, ShaderPrograms.Shadowmapentityanimated)", entities);
        Assert.Contains("internal bool NativeEntitiesEnabled { get; set; } = Environment.GetEnvironmentVariable(\"OPTIMUM_VK_NATIVE_ENTITIES\") != \"0\";", entities);
        Assert.Contains("public override void RenderEntityMesh(", entities);
        Assert.Contains("base.RenderEntityMesh(", entities);
        Assert.Contains("device.BeginNativePass(", entities);
        Assert.Contains("device.DrawNativeMesh(", entities);

        // The two programs it owns, and nothing else.
        Assert.Contains("private const string EntityAnimatedPass = \"entityanimated\";", entities);
        Assert.Contains("private const string EntityShadowPass = \"shadowmapentityanimated\";", entities);

        // The fixed state stated outright, from the values SystemRenderEntities sets.
        Assert.Contains("DepthTest = true", entities);
        Assert.Contains("DepthWrite = true", entities);
        Assert.Contains("DepthCompare = CompareOp.Less", entities);
        Assert.Contains("Cull = CullModeFlags.None", entities);
        Assert.Contains("VertexLayoutId = layoutId", entities);

        // The motion window is a write mask on the pipeline, never a draw-buffer toggle.
        Assert.Contains("OptimumMotionWriteActive", entities);
        Assert.Contains("attachment.WriteMask = 0;", entities);
        Assert.DoesNotContain("SetDrawBuffers", entities);
    }

    /// <summary>
    /// The native draw is recorded inside the stage's own declared pass, so a loop of hundreds of
    /// entities does not end and restart the rendering scope once per entity, and the device has
    /// the close that makes that safe.
    /// </summary>
    [Fact]
    public void TheEntityDrawsShareTheStagesPassInsteadOfOnePassPerEntity()
    {
        string entities = Read(EntityPlatformFile);
        Assert.Contains("Name = BoundPassName(),", entities);
        Assert.Contains("ColorSlots = uint.MaxValue,", entities);
        Assert.Contains("device.EndNativePass(keepScope: true);", entities);

        string native = Read(DeviceNativeFile);
        Assert.Contains("internal void EndNativePass(bool keepScope)", native);
        Assert.Contains("if (!_frameActive || keepScope) return;", native);
    }

    /// <summary>
    /// A native draw resolves every sampler its program declares, from what the client declared
    /// for it by name - not from a texture unit, which decision 3 forbids and which the emulated
    /// resolve (never run for a program whose draws are all native) would otherwise have filled.
    /// </summary>
    [Fact]
    public void ANativeDrawResolvesEverySamplerTheProgramDeclares()
    {
        string entities = Read(EntityPlatformFile);
        Assert.Contains("string[] names = pipeline.SamplerNames;", entities);
        Assert.Contains("DeclaredProgramTexture(programId, names[i])", entities);

        string shaders = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Shaders.cs");
        Assert.Contains("NoteNativeProgramTexture(program.ProgramId, samplerName, textureId);", shaders);

        string native = Read(DeviceNativeFile);
        Assert.Contains("internal string[] SamplerNames { get; }", native);
    }

    // ------------------------------------- the night sky, the moon, the particles, the decals

    /// <summary>
    /// The four seams of the second wave exist on the platform abstraction, each with the
    /// neutral body of the draw it replaced - which is what makes "OFF is vanilla" true for
    /// OpenGL, because ClientPlatformWindows overrides none of them.
    /// </summary>
    [Fact]
    public void TheWorldSeamsHaveNeutralBodiesThatAreTheDrawsTheyReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual void RenderNightSkyBox(MeshRef nightSkyBox, int cubeTextureId)", platform);
        Assert.Contains("RenderMesh(nightSkyBox);", platform);

        Assert.Contains(
            "public virtual void RenderCelestialQuad(MeshRef quad, int bodyTextureId, int skyTextureId, int glowTextureId)",
            platform);
        Assert.Contains("RenderMesh(quad);", platform);

        Assert.Contains("public virtual void RenderSunQuad(MeshRef quad, int sunTextureId)", platform);

        Assert.Contains("public virtual void RenderParticles(MeshRef model, int quantity, int particleTextureId)",
            platform);
        Assert.Contains("RenderMeshInstanced(model, quantity);", platform);

        // The decals are a scope seam, not a draw seam: the mesh handle lives in MeshDataPool,
        // which is internal in the vanilla API, so it can never be a seam parameter. Both neutral
        // bodies are empty and the lib runs the vanilla MeshDataPool.Draw between them.
        Assert.Contains("public virtual void BeginDecalPass(int decalTextureId, int blockTextureId)", platform);
        Assert.Contains("public virtual void EndDecalPass()", platform);
        Assert.DoesNotContain("RenderDecalPool", platform);

        // The OpenGL platform leaves every one of them alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderSunQuad", "RenderParticles",
                     "BeginDecalPass", "EndDecalPass",
                 })
        {
            Assert.DoesNotContain(seam, windows);
        }
    }

    /// <summary>
    /// Each render system draws through its seam and hands it the values a native pass cannot
    /// read off the GL state: the textures it samples, and for the decals the cull results the
    /// pool produced.
    /// </summary>
    [Fact]
    public void TheWorldRenderersDrawThroughTheirSeams()
    {
        string nightSky = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs");
        Assert.Contains("game.Platform.RenderNightSkyBox(nightSkyBox, textureId);", nightSky);
        Assert.DoesNotContain("game.Platform.RenderMesh(nightSkyBox);", nightSky);

        string sunMoon = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs");
        Assert.Contains(
            "platform.RenderCelestialQuad(quadModel, moontextureIds[4], game.skyTextureId, game.skyGlowTextureId);",
            sunMoon);
        // The visible sun draws through its seam; the occlusion-query probe keeps RenderMesh,
        // because a Vulkan occlusion query has to begin and end inside one render pass.
        Assert.Contains("platform.RenderSunQuad(quadModel, suntextureId);", sunMoon);

        string particles = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs");
        Assert.Contains("game.Platform.RenderParticles(particlePool.Model, particlePool.QuantityAlive, 0);", particles);
        Assert.Contains("game.Platform.RenderParticles(particlePool2.Model, particlePool2.QuantityAlive, 0);", particles);
        Assert.DoesNotContain("game.Platform.RenderMeshInstanced(", particles);

        // The motion window still wraps the draw: the cube pool writes the motion attachment
        // through the one writer include, and the window is what puts that attachment in the
        // colour set on both routes.
        Assert.Contains("optimumPlatform.BeginMotionWrite()", particles);
        Assert.Contains("optimumPlatform.EndMotionWrite();", particles);

        string decals = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs");
        // The scope, then the VANILLA MeshDataPool.Draw inside it: both routes cull and draw the
        // same ranges and only the draw command differs. Nothing here may reach a member that
        // exists only in the API fork - MeshDataPool.ModelRef did, and the shipped client threw
        // MissingMethodException on both backends because a new public member on a vanilla API
        // type never ships (the shipped API dll is vanilla plus api-patcher.cs's hooks only).
        Assert.Contains(
            "game.Platform.BeginDecalPass(decalTextureAtlas.TextureId, game.BlockAtlasManager.AtlasTextures[0].TextureId);",
            decals);
        Assert.Contains("decalPool.Draw(game.api, game.frustumCuller, EnumFrustumCullMode.CullInstant);", decals);
        Assert.Contains("game.Platform.EndDecalPass();", decals);
        Assert.DoesNotContain(".ModelRef", decals);

        // And the API fork itself no longer declares it, so the lib cannot start depending on it
        // again. The fork is git-ignored, so the shipped truth is its patch.
        Assert.DoesNotContain("ModelRef", Read("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch"));
        Assert.Contains("optimumPlatform.BeginMotionWrite()", decals);
    }

    /// <summary>Every new or changed lib member of this wave is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheWorldSeamsAndTheirCallersAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderSunQuad", "RenderParticles",
                     "BeginDecalPass", "EndDecalPass",
                 })
        {
            Assert.Contains(Q + seam + Q, patcher);
        }

        foreach (string caller in new[]
                 {
                     "SystemRenderNightSky" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                     "SystemRenderSunMoon" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                     "SystemRenderParticles" + Q + ", " + Q + "Render" + Q + ", 2",
                     "SystemRenderDecals" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                 })
        {
            Assert.Contains(caller, patcher);
        }
    }

    /// <summary>
    /// The Vulkan platform records all four systems as native passes, states their fixed state
    /// rather than reading the tracker's, and keeps every neutral body reachable behind one
    /// switch in the pattern of NativeSkyEnabled.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheWorldSystemsNativelyAndKeepsTheOldRoutes()
    {
        string world = Read(WorldPlatformFile);

        // On by default; OPTIMUM_VK_NATIVE_WORLD=0 turns the route off in the real client.
        Assert.Contains("internal bool NativeWorldEnabled { get; set; } = Environment.GetEnvironmentVariable(\"OPTIMUM_VK_NATIVE_WORLD\") != \"0\";", world);
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderSunQuad", "RenderParticles",
                 })
        {
            Assert.Contains("public override void " + seam + "(", world);
            Assert.Contains("base." + seam + "(", world);
        }

        // The decal scope seam: Begin/End on the platform, and the pool's multi-draw taken
        // natively from the mesh seam while the scope is open. Its "old route" is falling out of
        // TryDrawDecalPoolNative into the emulated multi-draw RenderMesh would have made anyway.
        Assert.Contains("public override void BeginDecalPass(int decalTextureId, int blockTextureId)", world);
        Assert.Contains("public override void EndDecalPass()", world);
        Assert.Contains("internal bool TryDrawDecalPoolNative(", world);
        Assert.Contains("TryDrawDecalPoolNative(modelRef, indices, indicesSizes, groupCount)",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs"));

        // Each mesh-draw kind the device API grew for world systems is used by the system whose
        // shape needs it: a single mesh, an instanced pool, an indirect multi-draw.
        Assert.Contains("device.DrawNativeMesh(", world);
        Assert.Contains("device.DrawNativeMeshInstanced(", world);
        Assert.Contains("device.DrawNativeMeshMulti(", world);
        Assert.Contains("device.BeginNativePass(", world);
        Assert.Contains("device.EndNativePass();", world);

        // The pipelines are built for each mesh's own vertex layout rather than the fullscreen
        // one: NativeWorldPrepare resolves it and hands it to the shared NativeMeshPipelineFor,
        // whose VertexLayoutId wiring is pinned by TheVulkanPlatformRecordsTheSkyNativelyAndKeepsTheOldRoute.
        Assert.Contains("device.NativeMeshLayoutId(", world);
    }

    /// <summary>
    /// The colour slots and the per-attachment blend of a native world pass come from the
    /// platform's own motion-window state, not from the GL state tracker (decision 3), and the
    /// motion attachment replaces rather than blends inside the window - what
    /// ApplyOptimumMotionBlendState does for an emulated draw.
    /// </summary>
    [Fact]
    public void TheWorldPassesDeriveTheirSlotsAndBlendFromTheMotionWindowNotTheTracker()
    {
        string world = Read(WorldPlatformFile);

        Assert.Contains("private uint NativeWorldPassColorSlots(FrameBufferRef target)", world);
        Assert.Contains("OptimumMotionWriteActive", world);
        Assert.Contains("MotionAttachmentIndex", world);
        Assert.Contains("(1u << (motion + 1)) - 1u", world);
        Assert.Contains("(1u << motion) - 1u", world);

        string blend = Section(world, "private AttachmentBlend[] NativeWorldBlend(", "return blend;");
        Assert.Contains("BlendFactor.One", blend);
        Assert.Contains("BlendFactor.Zero", blend);

        // Nothing in a native world pass asks the tracker what state it is in.
        Assert.DoesNotContain("GlStateTracker.", world);
    }

    // ------------------------------------------------------- GUI and text (stage 2)

    /// <summary>
    /// Both GUI seams exist on the platform abstraction with the neutral body that is exactly
    /// the RenderMesh call they replaced, and the OpenGL platform overrides neither, so nothing
    /// about the GL path changes.
    /// </summary>
    [Fact]
    public void TheGuiSeamsHaveNeutralBodiesThatAreTheDrawsTheyReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual void RenderTextureQuad(MeshRef quad, int textureId, bool blend)", platform);
        Assert.Contains("RenderMesh(quad);", platform);
        Assert.Contains(
            "public virtual void RenderOverlayLines(MeshRef lines, int textureId, float lineWidth, bool blend)",
            platform);
        Assert.Contains("RenderMesh(lines);", platform);

        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("RenderTextureQuad", windows);
        Assert.DoesNotContain("RenderOverlayLines", windows);
    }

    /// <summary>
    /// The texture-into-texture blit draws through its seam and hands it the two values a
    /// native pass may not read back off tracked GL state: the texture the program samples and
    /// the blend state this very method computed from its alphaTest argument.
    /// </summary>
    [Fact]
    public void TheTextureBlitDrawsThroughTheSeamAndCarriesItsOwnBlendState()
    {
        string client = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        Assert.Contains(
            "Platform.RenderTextureQuad(quadModel, fromTexture.TextureId, alphaTest >= 0f);",
            client);
        // The seam replaced the draw and nothing else: the RenderMesh call is gone from this
        // method, and the state calls that bracket it are untouched for the OpenGL path.
        Assert.DoesNotContain("Platform.RenderMesh(quadModel);\n\t\t\tPlatform.GlEnableDepthTest();", client);
    }

    /// <summary>
    /// The aiming reticle draws through its seam and passes the line width and blend state it
    /// sets itself - 0.5 for the accuracy rectangle and 1 for the four crosshair lines.
    /// </summary>
    [Fact]
    public void TheAimOverlayDrawsThroughTheSeamWithBothLineWidths()
    {
        string aim = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerAimAcc.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerAimAcc.cs");

        Assert.Contains("game.Platform.RenderOverlayLines(aimRectangleRef, 0, 0.5f, blend: true);", aim);
        for (int i = 0; i < 4; i++)
        {
            Assert.Contains("game.Platform.RenderOverlayLines(aimLinesRef[" + i + "], 0, 1f, blend: true);", aim);
        }
        Assert.DoesNotContain("game.Platform.RenderMesh(", aim);
    }

    /// <summary>Every new or changed lib member of the GUI stage is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheGuiSeamsAndTheirCallersAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderTextureQuad\"", patcher);
        Assert.Contains("\"RenderOverlayLines\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"RenderTextureIntoFrameBuffer\", 10", patcher);
        Assert.Contains(
            "\"Vintagestory.Client.NoObf.SystemRenderPlayerAimAcc\", \"OnRenderFrame2DOverlay\", 1", patcher);

        // Both are declared virtuals the Vulkan platform expects on the patched host, so a lib
        // that lost the transplant is caught at startup rather than at the first GUI draw.
        string expected = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("new(true, \"RenderTextureQuad\"", expected);
        Assert.Contains("new(true, \"RenderOverlayLines\"", expected);
    }

    /// <summary>
    /// The Vulkan platform records both GUI systems as native passes with their fixed state
    /// stated outright, takes the topology and the vertex layout from the mesh rather than from
    /// tracked state, and keeps the neutral bodies reachable behind one switch.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheGuiSystemsNativelyAndKeepsTheOldRoute()
    {
        string gui = Read(GuiPlatformFile);

        // On by default; OPTIMUM_VK_NATIVE_GUI=0 turns the route off in the real client.
        Assert.Contains("internal bool NativeGuiEnabled { get; set; } = Environment.GetEnvironmentVariable(\"OPTIMUM_VK_NATIVE_GUI\") != \"0\";", gui);
        Assert.Contains("public override void RenderTextureQuad(", gui);
        Assert.Contains("public override void RenderOverlayLines(", gui);
        Assert.Contains("base.RenderTextureQuad(", gui);
        Assert.Contains("base.RenderOverlayLines(", gui);
        Assert.Contains("device.BeginNativePass(", gui);
        Assert.Contains("device.DrawNativeMeshInstanced(pipeline, vao.VaoId, instanceCount, textures)", gui);
        Assert.Contains("device.EndNativePass();", gui);

        // Fixed state the pass states, never reads back: the caller's blend through the one
        // factor table, the caller's line width, and the mesh's own topology and layout.
        // The caller's blend mode, Standard unless a seam states another (the GUI quads do).
        Assert.Contains("AttachmentBlend.For(blend, mode)", gui);
        Assert.Contains("EnumBlendMode mode = EnumBlendMode.Standard", gui);
        Assert.Contains("LineWidth = lineWidth", gui);
        Assert.Contains("Topology = device.NativeMeshTopology(vao.VaoId)", gui);
        Assert.Contains("device.NativeMeshLayoutId(", gui);
        // The reticle and the texture blit state no depth; the GUI quads pass the caller's.
        Assert.Contains("depthTest: false, depthWrite: false, CompareOp.Less, scissor: null", gui);
        Assert.Contains("DepthTest = depthTest", gui);
    }

    /// <summary>
    /// The named blend modes have exactly one factor table, which the stated state and every native
    /// system that states "blend on, standard" both read - so the two can never drift.
    /// </summary>
    [Fact]
    public void TheNamedBlendModesHaveOneFactorTable()
    {
        string tracker = Read("Optimum.Render.Vulkan/Core/PipelineState.cs");

        Assert.Contains("public static AttachmentBlend For(bool enabled, EnumBlendMode mode)", tracker);
        Assert.Contains("FactorsFor(EnumBlendMode mode) => mode switch", tracker);
        Assert.Contains("= FactorsFor(mode);", tracker);
        Assert.Contains("AttachmentBlend.FactorsFor(mode);", Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs"));

        // One table only: the premultiplied-alpha pair appears once in the file.
        int first = tracker.IndexOf("EnumBlendMode.PremultipliedAlpha =>", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, tracker.IndexOf("EnumBlendMode.PremultipliedAlpha =>", first + 1, StringComparison.Ordinal));
    }

    private static int Count(string source, string needle)
    {
        int count = 0;
        int at = source.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // ------------------------------------------------------------------------ helpers

    private static string Section(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + from);
        int end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, "end not found after: " + from);
        return source.Substring(start, end - start);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string? TryFind(string relativePath)
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
    /// <summary>
    /// The quad particle pool draws natively in the OIT stage under the Transparent target's
    /// recorded blend contract, with depth writes off, and only for the vanilla program.
    /// </summary>
    [Fact]
    public void TheQuadParticlePoolDrawsNativelyUnderTheTransparentContract()
    {
        string world = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs");
        Assert.Contains("ReferenceEquals(ShaderProgramBase.CurrentShaderProgram, ShaderPrograms.Particlesquad)", world);
        Assert.Contains("AttachmentBlend[]? contract = nativeTransparentBlend;", world);
        Assert.Contains("!IsTransparentTarget(bound)", world);
        Assert.Contains("depthWrite: false", world);
        Assert.Contains("NativeWorldBeginPass(\"ParticlesOit\"", world);
    }
    /// <summary>
    /// Render2DTexture's quads draw through RenderGuiQuad, and the native route takes the blend,
    /// depth and scissor the client stated through the platform's virtuals, never the tracker.
    /// </summary>
    [Fact]
    public void TheGuiQuadsDrawThroughTheirSeamUnderTheStatedState()
    {
        string main = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("Platform.RenderGuiQuad(quadModel, textureid);", main);
        Assert.Contains("Platform.RenderGuiQuad(vao, meshRef.textureids[i]);", main);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderGuiQuad\"", patcher);
        Assert.Contains("\"Render2DTextureFlipped\", 7", patcher);

        string gui = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs");
        Assert.Contains("!ReferenceEquals(program, ShaderPrograms.Gui)", gui);
        Assert.Contains("statedBlendOn, statedBlendMode, statedDepthTest, statedDepthWrite", gui);
        Assert.Contains("scissorEnabled ? statedScissor : null", gui);

        string state = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.State.cs");
        Assert.Contains("statedBlendMode = blendMode;", state);
        Assert.Contains("statedDepthWrite = flag;", state);
    }
    /// <summary>
    /// The Transparent target's colour slots are the set the client selected, not the target's
    /// texture count: the OIT accumulation set keeps its colour accumulation on slots 3-5, which
    /// the FrameBufferRef does not list. Dropping them made every native OIT draw add no colour.
    /// </summary>
    [Fact]
    public void NativeOitPassesUseTheTransparentSlotSetTheClientSelected()
    {
        Assert.Contains("nativeTransparentSlots = 7;", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs"));
        Assert.Contains("nativeTransparentSlots = 0x3F;", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Leaf.cs"));
        string chunks = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeChunks.cs");
        Assert.Contains("if (IsTransparentTarget(target) && nativeTransparentSlots != 0) return nativeTransparentSlots;", chunks);
        string world = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs");
        Assert.Contains("if (IsTransparentTarget(target) && nativeTransparentSlots != 0) return nativeTransparentSlots;", world);
    }
    /// <summary>
    /// Every other draw under the vanilla gui program goes native from the platform's own
    /// RenderMesh, under the client-stated state including cull and line width, and the route
    /// switch still sends it back to the emulated draw.
    /// </summary>
    [Fact]
    public void PlainGuiProgramDrawsGoNativeFromRenderMesh()
    {
        string meshes = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs");
        Assert.Contains("if (TryRenderGuiMeshNative(modelRef))", meshes);
        string gui = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs");
        Assert.Contains("private bool TryRenderGuiMeshNative(MeshRef mesh)", gui);
        Assert.Contains("if (!NativeGuiEnabled || device == null || mesh == null || program == null", gui);
        Assert.Contains("statedLineWidth, statedBlendOn, statedBlendMode", gui);
        string state = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.State.cs");
        Assert.Contains("statedCull = true;", state);
        Assert.Contains("statedLineWidth = width;", state);
    }
    /// <summary>
    /// Plain draws under the vanilla standard program go native from RenderMesh under the stated
    /// state (colour mask included), on world targets and on the default framebuffer, and the sun's
    /// occlusion probe with them: the query rides the target manager's scope hooks.
    /// </summary>
    [Fact]
    public void PlainStandardProgramDrawsGoNativeOnEveryTarget()
    {
        Assert.Contains("if (TryRenderStandardMeshNative(modelRef)) return;",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs"));
        string world = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs");
        Assert.DoesNotContain("occlusionQueryOpen", world);
        Assert.Contains("return TryRenderStandardMeshToDefault(program, mesh, cull);", world);
        Assert.Contains("blend[i].WriteMask &= ~statedColorMaskOff;", world);
        Assert.Contains("private AttachmentBlend[] StatedWorldBlend(FrameBufferRef target, int count)", world);
        Assert.Contains("statedColorMaskOff =",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.State.cs"));
        Assert.Contains("_targets.ScopeClosing = _queryRing.OnScopeClosing;",
            Read("Optimum.Render.Vulkan/VulkanDevice.cs"));
    }
    /// <summary>
    /// The early loading screen's quads under the platform's hardcoded ShaderProgramMinimalGui
    /// (no pass name) and the menu's 2D particles go native from RenderMesh / RenderMeshInstanced.
    /// </summary>
    [Fact]
    public void LoadingScreenAndMenuParticleDrawsGoNative()
    {
        string meshes = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs");
        Assert.Contains("if (TryRenderMinimalGuiNative(modelRef))", meshes);
        Assert.Contains("if (TryRenderParticles2dNative(modelRef, quantity))", meshes);
        string gui = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs");
        Assert.Contains("ReferenceEquals(program, MinimalGuiShader)", gui);
        Assert.Contains("ReferenceEquals(program, ShaderPrograms.Guigear)", gui);
        Assert.Contains("string.Equals(program.PassName ?? \"\", pass.PassName, StringComparison.Ordinal)", gui);
        Assert.Contains("!ReferenceEquals(program, ShaderPrograms.Particlesquad2d)", gui);
    }
    /// <summary>
    /// The forked cloud renderers draw natively: their OptimumForkGraphics state is recorded on
    /// the platform, cloudmap draws into the framebuffer the fork bound, and cloudvolumetric
    /// samples Primary's depth as its bound depth and resolves liquidDepth to the LiquidDepth
    /// target instead of a placeholder.
    /// </summary>
    [Fact]
    public void TheForkCloudRenderersDrawNativelyUnderTheStateTheForkStated()
    {
        string fork = Read("Optimum.Render.Vulkan/Platform/VulkanForkGraphics.cs");
        Assert.Contains("platform.NoteForkFramebuffer(framebufferId);", fork);
        Assert.Contains("platform.NoteForkDepthTest(enabled);", fork);
        Assert.Contains("platform.NoteForkBlend(enabled);", fork);
        Assert.Contains("new VulkanForkGraphics(this, device)",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs"));
        Assert.Contains("if (TryRenderCloudsNative(modelRef))",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs"));
        string clouds = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeClouds.cs");
        Assert.Contains("if (!IsRegistryProgram(program)) return false;", clouds);
        Assert.Contains("depthWrite: false, samplesBoundDepth: true", clouds);
        Assert.Contains("FrameBuffers[(int)EnumFrameBuffer.LiquidDepth]", clouds);
        Assert.Contains("OPTIMUM_VK_NATIVE_CLOUDS", clouds);
    }
    /// <summary>
    /// The generic native draw (removal of the emulation layer): every mesh, instanced, multi-draw
    /// and fullscreen draw the dedicated routes do not take is recorded natively from the state
    /// the client stated - there is no other route left. The state is recorded with OpenGL's
    /// semantics at the platform's own virtuals, the fork bridge's included, and the pass declares
    /// every colour slot attached on the device - the OIT accumulation slots included.
    /// </summary>
    [Fact]
    public void EveryRemainingDrawTakesTheGenericStatedRoute()
    {
        string meshes = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs");
        Assert.Contains("TryDrawStated(vAO, 1, null, null, 0);", meshes);
        Assert.Contains("TryDrawStated(null, 1, null, null, 0);", meshes);
        Assert.Contains("TryDrawStated(vAO, 1, indices, indicesSizes, groupCount);", meshes);
        Assert.Contains("TryDrawStated(vAO, quantity, null, null, 0)", meshes);

        string route = Read("Optimum.Render.Vulkan/Platform/StatedDraw.cs");
        Assert.Contains("RenderTargetFormats? all = device.NativeTargetFormats(framebufferId, uint.MaxValue);", route);
        Assert.Contains("units[i] = device.NativeSamplerUnit(programId, names[i]);", route);
        Assert.Contains("reads[i] = stated.TextureAt(units[i]);", route);
        Assert.Contains("StatedDraw.Record(device, stated, programId, framebufferId,",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs"));

        // The GL state machine is gone from the device: no tracker, no state setters, no bound
        // target, no unit tables, no draw that is not a native one.
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!,
            "Optimum.Render.Vulkan", "Core", "GlStateTracker.cs")));
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        foreach (string removed in new[]
                 {
                     "public void UseProgram(", "public void SetBlend(", "public void SetDepthTest(", "public void SetViewport(",
                     "public void BindTexture(", "public void BindSampler(", "public void SetDrawBuffers(",
                     "public void BindFramebuffer(", "public void ClearColor(", "public void ClearDepth(",
                     "public void DrawMesh(", "public void DrawMeshMulti(", "public void DrawFullscreenTriangle(",
                     "private bool PrepareDraw(", "_boundTextures", "_unitSamplerOverrides", "GlStateTracker",
                 })
        {
            Assert.DoesNotContain(removed, device);
        }

        string state = Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs");
        Assert.Contains("public void SetBlendEnabled(bool enabled) => BlendEnabled = enabled;", state);
        Assert.Contains("_drawBuffers.TryGetValue(framebufferId, out uint mask) ? mask : 1u;", state);

        // Recorded where the client states it: no draw-buffer or per-slot blend call bypasses the record.
        foreach (string file in new[] { "FrameBuffers", "Taa", "Leaf" })
        {
            string source = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform." + file + ".cs");
            Assert.DoesNotContain("device.SetDrawBuffers(", source);
            Assert.DoesNotContain("device.SetBlendFuncSeparate(", source);
        }
        string platformState = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.State.cs");
        Assert.Contains("stated.SetBlendEnabled(on);", platformState);
        Assert.Contains("stated.LineWidth = width;", platformState);
        Assert.Contains("stated.LineWidth = 1.5f;", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs"));
        string fork = Read("Optimum.Render.Vulkan/Platform/VulkanForkGraphics.cs");
        Assert.Contains("platform.NoteForkTexture(unit, textureId);", fork);
        Assert.Contains("platform.NoteForkDrawBuffers(framebufferId, attachmentMask);", fork);
        Assert.Contains("platform.NoteForkViewport(x, y, width, height);", fork);
        string clouds = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeClouds.cs");
        Assert.Contains("stated.DepthTest = enabled;", clouds);
        Assert.Contains("stated.SetBlendEnabled(enabled);", clouds);
    }
}
