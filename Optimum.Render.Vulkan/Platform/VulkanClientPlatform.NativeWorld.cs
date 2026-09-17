using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan-native-render-systems.md), Phase 3b decision 5
// stage 2: the sky systems that are not the dome, the particle pools and the decal pool, all
// on the native device API the sky dome proved.
//
// What they draw, and where the other side is:
//   - the night sky box   - SystemRenderNightSky's 75-unit star cube, seam
//                           ClientPlatformAbstract.RenderNightSkyBox, neutral body RenderMesh;
//   - the moon            - SystemRenderSunMoon's quad under celestialobject, seam
//                           ClientPlatformAbstract.RenderCelestialQuad, neutral body RenderMesh;
//   - the cube particles  - SystemRenderParticles' instanced pool draw on Primary, seam
//                           ClientPlatformAbstract.RenderParticles, neutral body
//                           RenderMeshInstanced;
//   - the decals          - SystemRenderDecals' pooled multi-draw, scope seam
//                           ClientPlatformAbstract.BeginDecalPass / EndDecalPass with empty
//                           neutral bodies: the lib runs the vanilla MeshDataPool.Draw between
//                           them and the pool's RenderMesh multi-draw is taken natively while
//                           the scope is open (the mesh handle is internal in the vanilla API,
//                           so it can never be a seam parameter).
// NativeWorldEnabled false takes the neutral body on the Vulkan device too, which is the route
// the differential tests compare against.
//
// Target and slots: every one of them draws into the framebuffer its stage has bound - Primary
// for all four. The colour slots are NativeWorldPassColorSlots: the same set the emulated route's
// draw-buffer mask would hold, derived from the platform's own motion-window state
// (MotionAttachmentIndex, OptimumMotionWriteActive) rather than from the GL state tracker
// (decision 3). That is what makes a cube particle's and a decal's motion vector land through
// the one writer include exactly while their caller's window is open, and keeps the motion
// attachment out of the night sky's and the moon's scope entirely.
//
// State that is not obvious, per system, and where it comes from:
//   - the night sky and the moon run with the depth test off, because their callers call
//     GlDisableDepthTest; GL writes no depth with the test off, so depth writes are off too.
//     The night sky also disables culling itself. The moon inherits the cull state the night
//     sky left, which is off - no vanilla Opaque renderer between them turns it back on.
//   - the cube particles and the decals run with the depth test and depth writes on: the Opaque
//     stage is entered with ChunkRenderer.RenderOpaque's depth mask and test, and the AfterOIT
//     stage is entered with ClientMain's own GlDepthMask/GlEnableDepthTest. Culling is off for
//     both: SystemRenderNightSky leaves it off for the rest of the Opaque stage, and
//     SystemRenderDecals calls GlDisableCullFace itself.
//   - blending is on in the standard mode for the moon, the cube particles and the decals
//     (their callers call GlToggleBlend(on: true)), and off for the night sky.
//   - the motion attachment never blends. Inside a motion window the native pass states
//     replace-blending on that one attachment per attachment, which is what
//     ApplyOptimumMotionBlendState does for an emulated draw.
//
// What pins them: NativeWorldSystemsTests (old route against native route, including the motion
// attachment bit for bit) and Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs each seam's neutral body - the OpenGL body's own draw - on the Vulkan device
    /// instead of the native pass: the old route the differential tests compare against, in the
    /// pattern of <see cref="NativeSkyEnabled" />.
    /// </summary>
    internal bool NativeWorldEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_WORLD") != "0";

    /// <summary>The star cube's pipeline: no per-draw uniform, one samplerCube.</summary>
    private readonly NativeMeshPass nativeNightSky =
        new("nightsky", Array.Empty<string>(), new[] { "ctex" });

    /// <summary>
    /// The moon's pipeline. "tex" is the body's own texture; "sky" and "glow" are the frame
    /// textures skycolor.fsh reads to shade the body against the sky behind it, and they are
    /// passed here because a native draw resolves what it samples from handles and nothing else
    /// in this pass would refresh the frame table's entries for them.
    /// </summary>
    private readonly NativeMeshPass nativeCelestial =
        new("celestialobject", Array.Empty<string>(), new[] { "tex", "sky", "glow" });

    /// <summary>
    /// The sun's pipeline, through the standard program. Its samplers are resolved from the
    /// pipeline's own declaration (<see cref="RenderSunQuad" />), because standard also reads frame
    /// textures a native draw has to name by handle.
    /// </summary>
    private readonly NativeMeshPass nativeSun =
        new("standard", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The quad particle pool's pipeline: drawn in the OIT stage onto the Transparent target, under
    /// the blend contract the client applied to that target (the chunk route records it), with
    /// depth test on and depth writes off - what LoadFrameBuffer(Transparent) sets.
    /// </summary>
    private readonly NativeMeshPass nativeParticlesQuad =
        new("particlesquad", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Any plain RenderMesh under the vanilla standard program (RenderStandardMeshNative).</summary>
    private readonly NativeMeshPass nativeStandardGui =
        new("standard", Array.Empty<string>(), Array.Empty<string>());

    private readonly NativeMeshPass nativeStandardMesh =
        new("standard", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>The cube particle pool's pipeline: no per-draw uniform and no sampler at all.</summary>
    private readonly NativeMeshPass nativeParticlesCube =
        new("particlescube", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The decal pool's pipeline. origin and modelViewMatrix are DRAW uniforms the client system
    /// already set through the program's own setters, so they ride in the program's push shadow
    /// and the pass writes nothing per draw; the two atlases are its samplers.
    /// </summary>
    private readonly NativeMeshPass nativeDecals =
        new("decals", Array.Empty<string>(), new[] { "decalTexture", "blockTexture" });

    // ------------------------------------------------------------------ shared derivations

    /// <summary>
    /// The colour slots a world pass writes: the set the emulated route's draw-buffer mask would
    /// hold at this point in the frame, computed from the platform's own motion-window state
    /// rather than read back out of the tracker.
    ///
    /// Outside Primary, and with TAA off (<see cref="ClientPlatformWindows.MotionAttachmentIndex" />
    /// negative), that is every bound colour slot. On Primary with TAA on it is Primary's default
    /// colour set, plus the motion attachment exactly while a motion window is open - the two
    /// sets <see cref="EnableMotionDrawBuffers" /> and <see cref="RestorePrimaryDrawBuffers" />
    /// switch between.
    /// </summary>
    private uint NativeWorldPassColorSlots(FrameBufferRef target)
    {
        if (IsTransparentTarget(target) && nativeTransparentSlots != 0) return nativeTransparentSlots;
        uint all = NativeAllColorSlots(target);
        int motion = MotionAttachmentIndex;
        if (motion < 0 || motion >= 32) return all;

        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count == 0 || !ReferenceEquals(target, buffers[0])) return all;

        uint mask = OptimumMotionWriteActive
            ? (1u << (motion + 1)) - 1u
            : (1u << motion) - 1u;
        return mask & all;
    }

    /// <summary>
    /// A world pass's per-attachment blend: the caller's blend mode on every colour attachment,
    /// except the motion attachment inside an open motion window, which replaces rather than
    /// blends. A blended motion vector is a weighted average of two surfaces' displacements and
    /// belongs to neither, which is why <see cref="ApplyOptimumMotionBlendState" /> forces
    /// (ONE, ZERO) with FUNC_ADD there on the emulated route; this states the same thing on the
    /// pipeline instead of through a tracked toggle.
    /// </summary>
    private AttachmentBlend[] NativeWorldBlend(RenderTargetFormats formats, bool blending)
    {
        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++)
        {
            blend[i] = AttachmentBlend.Default;
            blend[i].Enabled = blending;
        }

        int motion = MotionAttachmentIndex;
        if (OptimumMotionWriteActive && motion >= 0 && motion < blend.Length)
        {
            blend[motion] = AttachmentBlend.Default;
            blend[motion].Enabled = blending;
            blend[motion].SrcColor = BlendFactor.One;
            blend[motion].DstColor = BlendFactor.Zero;
            blend[motion].SrcAlpha = BlendFactor.One;
            blend[motion].DstAlpha = BlendFactor.Zero;
        }
        return blend;
    }

    /// <summary>
    /// Everything a native world draw needs before it can be recorded: the target the stage
    /// bound, the program it is drawing with, the mesh and its vertex layout, the colour slots
    /// and their formats, and the pipeline for that combination. False means the caller takes
    /// its seam's neutral body, which is always a legal answer.
    /// </summary>
    private bool NativeWorldPrepare(NativeMeshPass pass, MeshRef mesh, bool blending, bool depth,
        out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline,
        Func<int, AttachmentBlend[]>? blendFor = null, bool? depthWrite = null,
        CompareOp? depthCompare = null, CullModeFlags? cull = null, bool samplesBoundDepth = false)
    {
        target = null!;
        vao = null!;
        slots = 0;
        pipeline = null!;

        if (!NativeWorldEnabled || device == null || mesh == null) return false;

        FrameBufferRef bound = CurrentFrameBuffer;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        var buffers = mesh as VAO;
        if (bound == null || program == null || buffers == null || buffers.VaoId == 0 || buffers.Disposed)
        {
            return false;
        }

        int layoutId = device.NativeMeshLayoutId(buffers.VaoId);
        if (layoutId < 0) return false;

        uint colorSlots = NativeWorldPassColorSlots(bound);
        if (colorSlots == 0) return false;

        RenderTargetFormats? formats = device.NativeTargetFormats(bound.FboId, colorSlots);
        if (formats == null) return false;

        NativePipeline? built = NativeMeshPipelineFor(pass, program, bound.FboId, colorSlots, layoutId,
            new NativePipelineDescription
            {
                Blend = blendFor != null ? blendFor(formats.ColorFormats.Length) : NativeWorldBlend(formats, blending),
                DepthTest = depth,
                DepthWrite = depthWrite ?? depth,
                DepthCompare = depthCompare ?? CompareOp.Less,
                Cull = cull ?? CullModeFlags.None,
                Topology = PrimitiveTopology.TriangleList,
                SamplesBoundDepth = samplesBoundDepth,
            });
        if (built == null) return false;

        target = bound;
        vao = buffers;
        slots = colorSlots;
        pipeline = built;
        return true;
    }

    /// <summary>
    /// Opens the native pass for one world draw on the target its stage bound, with the reads it
    /// samples declared outright.
    /// </summary>
    private bool NativeWorldBeginPass(string name, FrameBufferRef target, uint slots, int[] reads)
    {
        Rect2D viewport = StatedViewport();
        return device.BeginNativePass(new NativePassDescription
        {
            Name = name + "/" + target.FboId,
            FramebufferId = target.FboId,
            ColorSlots = slots,
            Reads = reads,
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
        });
    }

    /// <summary>
    /// Closes the native pass and declares the stage's own pass context again, because every
    /// renderer after this one draws into the same target through the generic stated route - the same
    /// restoration the sky dome's pass and the TAA resolve's do.
    /// </summary>
    private void NativeWorldEndPass(FrameBufferRef target, string outer, PassFlags outerFlags)
    {
        device.EndNativePass();
        SetPassContext(outer, outerFlags);
    }

    // ------------------------------------------------------------------------ the seams

    /// <summary>The star cube's draw: the native pass, or the seam's neutral body.</summary>
    public override void RenderNightSkyBox(MeshRef nightSkyBox, int cubeTextureId)
    {
        // No depth and no blending: SystemRenderNightSky has called GlDisableDepthTest and
        // GlDisableCullFace, and nightsky.fsh writes opaque colour into slot 0.
        if (!NativeWorldPrepare(nativeNightSky, nightSkyBox, blending: false, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderNightSkyBox(nightSkyBox, cubeTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("NightSky", target, slots, new[] { cubeTextureId }))
        {
            // The cube map resolves into the bindless table's cube array, which the sampler's
            // own kind selects - a 2D texture bound here would be refused rather than sampled.
            device.DrawNativeMesh(pipeline, vao.VaoId, new[]
            {
                new NativeTexture(nativeNightSky.Samplers[0], cubeTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>The moon's draw: the native pass, or the seam's neutral body.</summary>
    public override void RenderCelestialQuad(MeshRef quad, int bodyTextureId, int skyTextureId, int glowTextureId)
    {
        // Blending on in the standard mode and no depth: SystemRenderSunMoon has called
        // GlToggleBlend(on: true), GlDisableCullFace and GlDisableDepthTest before both bodies.
        if (!NativeWorldPrepare(nativeCelestial, quad, blending: true, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderCelestialQuad(quad, bodyTextureId, skyTextureId, glowTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("Celestial", target, slots, new[] { bodyTextureId, skyTextureId, glowTextureId }))
        {
            device.DrawNativeMesh(pipeline, vao.VaoId, new[]
            {
                new NativeTexture(nativeCelestial.Samplers[0], bodyTextureId),
                new NativeTexture(nativeCelestial.Samplers[1], skyTextureId),
                new NativeTexture(nativeCelestial.Samplers[2], glowTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>
    /// The sun's visible quad: the native pass, or the seam's neutral body.
    /// What it draws: the sun disc of SystemRenderSunMoon.OnRenderFrame3D. The other side:
    /// ClientPlatformAbstract.RenderSunQuad, whose neutral body is the RenderMesh it replaced.
    /// Target and slots: the stage's bound target and <see cref="NativeWorldPassColorSlots" />.
    /// State: blended in the standard mode, no depth test, no culling - SystemRenderSunMoon's
    /// GlToggleBlend(on: true), GlDisableDepthTest and GlDisableCullFace, stated on the pipeline.
    /// Only the registered vanilla standard program is taken: a mod can register its own program
    /// under the same pass name (VSEssentials' first-person item shader does).
    /// What pins it: NativeWorldSystemsTests.TheSunMatchesTheSeamsNeutralBody.
    /// </summary>
    public override void RenderSunQuad(MeshRef quad, int sunTextureId)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || !ReferenceEquals(program, ShaderPrograms.Standard) ||
            !NativeWorldPrepare(nativeSun, quad, blending: true, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderSunQuad(quad, sunTextureId);
            return;
        }

        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length];
        var reads = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = names[i] == "tex" ? sunTextureId : DeclaredProgramTexture(program.ProgramId, names[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            reads[i] = id;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("Sun", target, slots, reads))
        {
            device.DrawNativeMesh(pipeline, vao.VaoId, textures);
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>
    /// One particle pool's instanced draw: the native pass, or the seam's neutral body.
    ///
    /// Both pools take the native route. The cube pool draws on Primary here; the quad pool draws
    /// in the OIT stage onto the Transparent target and goes through <see cref="RenderQuadParticles" />,
    /// which states the Transparent blend contract the client applied. A draw under any other
    /// program falls through to the neutral body: the pipeline request names the vanilla program.
    /// </summary>
    public override void RenderParticles(MeshRef model, int quantity, int particleTextureId)
    {
        if (quantity <= 0)
        {
            base.RenderParticles(model, quantity, particleTextureId);
            return;
        }

        if (ReferenceEquals(ShaderProgramBase.CurrentShaderProgram, ShaderPrograms.Particlesquad))
        {
            RenderQuadParticles(model, quantity, particleTextureId);
            return;
        }

        // Blending on in the standard mode (the caller's GlToggleBlend) and the Opaque stage's
        // depth test and depth writes, which ChunkRenderer.RenderOpaque established and no
        // renderer between it and the particles turns off again.
        if (!NativeWorldPrepare(nativeParticlesCube, model, blending: true, depth: true,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderParticles(model, quantity, particleTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        // Inside the caller's motion window the motion attachment is one of the pass's colour
        // slots (NativeWorldPassColorSlots) and replaces rather than blends (NativeWorldBlend), so
        // particlescube.fsh's motion.glsl writer lands exactly what it lands on the GL path.
        if (NativeWorldBeginPass("Particles", target, slots, Array.Empty<int>()))
        {
            device.DrawNativeMeshInstanced(pipeline, vao.VaoId, quantity, ReadOnlySpan<NativeTexture>.Empty);
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>
    /// The quad particle pool's instanced draw in the OIT stage: the native pass, or the seam's
    /// neutral body. The other side is ClientPlatformAbstract.RenderParticles' neutral body, drawn
    /// under the state LoadFrameBuffer(Transparent) and ApplyTransparentPassBlendState left.
    /// Target and slots: the Transparent target, every bound slot; the pipeline masks the outputs
    /// particlesquad does not write. State: the Transparent blend contract the client last applied
    /// (weighted accumulation, revealage, glow), depth test on, depth writes off, no culling.
    /// Falls back while that contract has not been recorded or the bound target is not
    /// Transparent. particleTex resolves from the program's declared texture: the lib binds it
    /// through the program's setter and hands the seam 0.
    /// </summary>
    private void RenderQuadParticles(MeshRef model, int quantity, int particleTextureId)
    {
        FrameBufferRef bound = CurrentFrameBuffer;
        AttachmentBlend[]? contract = nativeTransparentBlend;
        if (bound == null || contract == null || !IsTransparentTarget(bound) ||
            !NativeWorldPrepare(nativeParticlesQuad, model, blending: true, depth: true,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline,
                count =>
                {
                    var blend = new AttachmentBlend[Math.Max(count, 1)];
                    for (int i = 0; i < blend.Length; i++)
                    {
                        blend[i] = i < contract.Length ? contract[i] : AttachmentBlend.Default;
                        blend[i].Enabled = true;
                    }
                    return blend;
                },
                depthWrite: false))
        {
            base.RenderParticles(model, quantity, particleTextureId);
            return;
        }

        ShaderProgramBase program = ShaderProgramBase.CurrentShaderProgram!;
        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length];
        var reads = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = names[i] == "particleTex" && particleTextureId != 0
                ? particleTextureId
                : DeclaredProgramTexture(program.ProgramId, names[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            reads[i] = id;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("ParticlesOit", target, slots, reads))
        {
            device.DrawNativeMeshInstanced(pipeline, vao.VaoId, quantity, textures);
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>
    /// The per-attachment blend a world draw inherits from what the client stated: the stated
    /// mode on every slot, with GlToggleBlend's own exceptions - the SSAO G-buffer slots and the
    /// open motion attachment replace rather than blend - and on the Transparent target the
    /// recorded OIT contract with the stated enable.
    /// </summary>
    private AttachmentBlend[] StatedWorldBlend(FrameBufferRef target, int count)
    {
        var blend = new AttachmentBlend[Math.Max(count, 1)];
        AttachmentBlend[]? contract = nativeTransparentBlend;
        bool transparent = contract != null && IsTransparentTarget(target);
        bool primary = IsPrimaryTarget(target);
        int motion = primary && OptimumMotionWriteActive ? MotionAttachmentIndex : -1;
        for (int i = 0; i < blend.Length; i++)
        {
            if (transparent)
            {
                blend[i] = i < contract!.Length ? contract[i] : AttachmentBlend.Default;
                blend[i].Enabled = statedBlendOn;
            }
            else if (primary && statedBlendOn && OptimumRenderSsao && (i == 2 || i == 3))
            {
                blend[i] = ReplaceBlend(true);
            }
            else
            {
                blend[i] = AttachmentBlend.For(statedBlendOn, statedBlendMode);
                // World/UI separation: a world-program draw into the UI image (held items in
                // a dialog, the reticle's disc) accumulates coverage like the GUI does.
                if (stated.IsUiImage(target?.FboId ?? PassDeclaration.DefaultFramebuffer))
                {
                    blend[i] = blend[i].ForUiImage();
                }
            }
            if (i == motion) blend[i] = ReplaceBlend(statedBlendOn);
            blend[i].WriteMask &= ~statedColorMaskOff;
        }
        return blend;
    }

    /// <summary>
    /// A plain RenderMesh under the vanilla standard program - held and dropped items, block
    /// entity models, the sun's disc outside its seam - recorded natively under the state the
    /// client stated: blend, depth test, depth mask, depth function, cull. Every sampler the
    /// pipeline declares resolves from the program's declared textures. False: the caller runs the
    /// emulated draw. The colour mask the client stated is applied per slot. An open occlusion
    /// query (the sun probe) is carried across the native pass: the pass opens its scope through
    /// the target manager, whose scope hooks suspend the query in the closing scope and resume it
    /// in the native one. The default framebuffer (GUI item icons) goes to
    /// <see cref="TryRenderStandardMeshToDefault" />.
    /// </summary>
    private bool TryRenderStandardMeshNative(MeshRef mesh)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeWorldEnabled || device == null || mesh == null || program == null ||
            !ReferenceEquals(program, ShaderPrograms.Standard))
        {
            return false;
        }

        FrameBufferRef bound = CurrentFrameBuffer;
        CullModeFlags cull = statedCull
            ? (statedCullBack ? CullModeFlags.BackBit : CullModeFlags.FrontBit)
            : CullModeFlags.None;
        if (bound == null)
        {
            return TryRenderStandardMeshToDefault(program, mesh, cull);
        }
        if (!NativeWorldPrepare(nativeStandardMesh, mesh, blending: statedBlendOn, depth: statedDepthTest,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline,
                count => StatedWorldBlend(bound, count), depthWrite: statedDepthWrite,
                depthCompare: GlEnums.CompareOpFrom(statedDepthFunc), cull: cull))
        {
            return false;
        }

        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length];
        var reads = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = DeclaredProgramTexture(program.ProgramId, names[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            reads[i] = id;
        }

        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        bool drawn = false;
        if (NativeWorldBeginPass("Standard", target, slots, reads))
        {
            drawn = device.DrawNativeMesh(pipeline, vao.VaoId, textures);
        }
        NativeWorldEndPass(target, outer, outerFlags);
        return drawn;
    }

    /// <summary>
    /// A standard-program draw into the default framebuffer: the GUI's item icons (hotbar,
    /// inventory, held-item slots), which InventoryItemRenderer draws in the Ortho stage with
    /// CurrentFrameBuffer null. The OpenGL side is ClientPlatformWindows.RenderMesh. Slot 0 takes
    /// the stated blend through the tracker's factor table; depth test, mask, function, cull and
    /// scissor are what the client stated (item icons use depth to sort their own faces).
    /// </summary>
    private AttachmentBlend[] StatedGuiSlots(RenderTargetFormats formats)
    {
        AttachmentBlend[] slots = GuiSlots(formats, statedBlendOn, PassDeclaration.DefaultFramebuffer, statedBlendMode);
        slots[0].WriteMask &= ~statedColorMaskOff;
        return slots;
    }

    private bool TryRenderStandardMeshToDefault(ShaderProgramBase program, MeshRef mesh, CullModeFlags cull)
    {
        var vao = mesh as VAO;
        if (vao == null || vao.VaoId == 0 || vao.Disposed) return false;
        int framebufferId = PassDeclaration.DefaultFramebuffer;
        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0) return false;
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, 1u);
        if (formats == null) return false;

        NativePipeline? pipeline = NativeMeshPipelineFor(nativeStandardGui, program, framebufferId, 1u, layoutId,
            new NativePipelineDescription
            {
                Blend = StatedGuiSlots(formats),
                DepthTest = statedDepthTest,
                DepthWrite = statedDepthWrite,
                DepthCompare = GlEnums.CompareOpFrom(statedDepthFunc),
                Cull = cull,
                Topology = device.NativeMeshTopology(vao.VaoId),
            });
        if (pipeline == null) return false;

        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length];
        var reads = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = DeclaredProgramTexture(program.ProgramId, names[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            reads[i] = id;
        }

        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        Rect2D viewport = StatedViewport();
        bool drawn = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = "StandardGui/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = 1u,
            Reads = reads,
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
            Scissor = scissorEnabled ? statedScissor : null,
        }))
        {
            drawn = device.DrawNativeMesh(pipeline, vao.VaoId, textures);
        }
        device.EndNativePass();
        SetPassContext(outer, outerFlags);
        return drawn;
    }

    // ------------------------------------------------------------------- the decal scope

    /// <summary>True between <see cref="BeginDecalPass" /> and <see cref="EndDecalPass" />.</summary>
    private bool decalScopeActive;

    /// <summary>The decal atlas handle the open scope passed in.</summary>
    private int decalScopeDecalTextureId;

    /// <summary>The block atlas handle the open scope passed in.</summary>
    private int decalScopeBlockTextureId;

    /// <summary>
    /// Opens the decal pool's scope: the two atlas handles, which a native pass resolves what it
    /// samples from, instead of from the units ShaderProgramDecals' setters bound them to.
    ///
    /// The mesh handle is deliberately not a parameter. MeshDataPool.modelRef is internal in the
    /// vanilla API and the shipped VintagestoryAPI-patched.dll is vanilla plus api-patcher.cs's
    /// hooks only, so a new public member on MeshDataPool would never reach the running client
    /// (it did not, and the shipped client threw MissingMethodException on both backends). The
    /// lib therefore runs the vanilla MeshDataPool.Draw, whose own
    /// <c>capi.Render.RenderMesh(modelRef, starts, sizes, count)</c> lands in this platform's
    /// <see cref="RenderMesh(MeshRef, int[], int[], int, bool)" /> override, and that override
    /// routes to <see cref="TryDrawDecalPoolNative" /> while this scope is open. The caller's
    /// Draw runs the pool's cull first on both routes, so both draw the same ranges and only the
    /// draw command differs.
    ///
    /// Where the other side is: ClientPlatformAbstract.BeginDecalPass / EndDecalPass have empty
    /// neutral bodies, so the OpenGL path is vanilla MeshDataPool.Draw into
    /// ClientPlatformWindows.RenderMesh -> GL.MultiDrawElements, exactly as before the seam.
    /// Target and slots: Primary, inside SystemRenderDecals' motion window - a decal nudges the
    /// depth buffer in front of the block it sits on and writes that surface's motion vector
    /// itself, so the motion attachment is one of NativeWorldPassColorSlots and replaces rather
    /// than blends (NativeWorldBlend).
    /// State that is not obvious: standard blending on and the AfterOIT stage's depth test and
    /// depth writes, which ClientMain sets before the stage; SystemRenderDecals turns culling
    /// off itself.
    /// What pins it: NativeWorldSystemsTests (old route against native route, including the
    /// motion attachment bit for bit) and Optimum.Tests/native-world-systems-coverage-tests.cs.
    /// </summary>
    public override void BeginDecalPass(int decalTextureId, int blockTextureId)
    {
        decalScopeActive = true;
        decalScopeDecalTextureId = decalTextureId;
        decalScopeBlockTextureId = blockTextureId;
    }

    /// <summary>Closes the scope <see cref="BeginDecalPass" /> opened.</summary>
    public override void EndDecalPass()
    {
        decalScopeActive = false;
        decalScopeDecalTextureId = 0;
        decalScopeBlockTextureId = 0;
    }

    /// <summary>
    /// The decal pool's multi-draw, recorded natively, when it arrives through
    /// <see cref="RenderMesh(MeshRef, int[], int[], int, bool)" /> inside an open decal scope.
    /// False means the scope is closed, the native route is off, or the pass could not be
    /// prepared, and the caller takes the generic stated multi-draw.
    /// </summary>
    internal bool TryDrawDecalPoolNative(MeshRef decalMesh, int[] indicesStarts, int[] indicesSizes, int groupCount)
    {
        if (!decalScopeActive) return false;
        if (groupCount <= 0 || indicesStarts == null || indicesSizes == null) return false;

        if (!NativeWorldPrepare(nativeDecals, decalMesh, blending: true, depth: true,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            return false;
        }

        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("Decals", target, slots,
                new[] { decalScopeDecalTextureId, decalScopeBlockTextureId }))
        {
            device.DrawNativeMeshMulti(pipeline, vao.VaoId, indicesStarts, indicesSizes, groupCount, new[]
            {
                new NativeTexture(nativeDecals.Samplers[0], decalScopeDecalTextureId),
                new NativeTexture(nativeDecals.Samplers[1], decalScopeBlockTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
        return true;
    }
}
