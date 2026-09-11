using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Contract-stability tests for <c>docs/temporal-frame-contract.md</c> (v1).
///
/// The document is the frozen specification every temporal consumer is written
/// against - the in-house TAA resolve today, FSR 3.1 / XeSS 2 / DLSS next, frame
/// generation and ray reconstruction after that. These tests are its tripwire:
/// they pin the public surface of the input record, and the conventions the
/// document states as fact, to the code that implements them. Every failure
/// message names the document, because a change here is a change to the contract
/// and has to be written down there (and versioned) before it is trusted.
///
/// None of this is behaviour coverage. It is "the document still describes the
/// code", which is the only thing that makes freezing a contract worth anything.
/// </summary>
public class TemporalContractTests
{
    private const string Doc = "docs/temporal-frame-contract.md";
    private const string ContractVersion = "v1";

    // ---------------------------------------------------------------------
    // 1. The public surface of the input record
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every public member of IOptimumTemporalContext, as
    /// <c>kind returnType Name(parameterTypes)</c>, ordinal-sorted. This is the
    /// checked-in list section 1.1 of the contract documents member by member.
    /// </summary>
    private static readonly string[] ExpectedContextSurface =
    {
        "method EnumTemporalResetReason get_ResetReason()",
        "method EnumTemporalView get_ActiveView()",
        "method Boolean IsViewCaptured(EnumTemporalView)",
        "method Boolean get_JitterActive()",
        "method Boolean get_Reset()",
        "method Int32 get_RenderHeight()",
        "method Int32 get_RenderWidth()",
        "method Int64 get_FrameIndex()",
        "method OptimumWarpState get_PrevWarp()",
        "method OptimumWarpState get_Warp()",
        "method Single get_DeltaTimeMs()",
        "method Single get_Fov()",
        "method Single get_ZFar()",
        "method Single get_ZNear()",
        "method Single[] GetPrevProjection(EnumTemporalView)",
        "method Single[] GetProjection(EnumTemporalView)",
        "method Single[] get_CameraMatrix()",
        "method Single[] get_CameraMatrixOrigin()",
        "method Single[] get_PrevCameraMatrix()",
        "method Single[] get_PrevCameraMatrixOrigin()",
        "method Vec2f get_JitterPx()",
        "method Vec2f get_JitterSequencePx()",
        "method Vec2f get_PrevJitterPx()",
        "method Vec3f get_CameraPosDelta()",
        "method Vec3f get_Playerpos()",
        "method Vec3f get_PrevPlayerpos()",
        "property EnumTemporalResetReason ResetReason get",
        "property EnumTemporalView ActiveView get",
        "property Boolean JitterActive get",
        "property Boolean Reset get",
        "property Int32 RenderHeight get",
        "property Int32 RenderWidth get",
        "property Int64 FrameIndex get",
        "property OptimumWarpState PrevWarp get",
        "property OptimumWarpState Warp get",
        "property Single DeltaTimeMs get",
        "property Single Fov get",
        "property Single ZFar get",
        "property Single ZNear get",
        "property Single[] CameraMatrix get",
        "property Single[] CameraMatrixOrigin get",
        "property Single[] PrevCameraMatrix get",
        "property Single[] PrevCameraMatrixOrigin get",
        "property Vec2f JitterPx get",
        "property Vec2f JitterSequencePx get",
        "property Vec2f PrevJitterPx get",
        "property Vec3f CameraPosDelta get",
        "property Vec3f Playerpos get",
        "property Vec3f PrevPlayerpos get",
    };

    /// <summary>
    /// The same for the concrete frame. It carries the mutators the client owns -
    /// Advance, the two captures, RecordProjection - which consumers must never
    /// call; the contract says so, and this list is why a new one cannot appear
    /// without the document being edited.
    /// </summary>
    private static readonly string[] ExpectedFrameSurface =
    {
        "ctor .ctor()",
        "field Double TeleportThresholdBlocks",
        "method Boolean IsViewCaptured(EnumTemporalView)",
        "method Boolean WasViewCaptured(EnumTemporalView)",
        "method Boolean get_JitterActive()",
        "method EnumTemporalResetReason get_ResetReason()",
        "method EnumTemporalView get_ActiveView()",
        "method Boolean get_Reset()",
        "method Int32 get_RenderHeight()",
        "method Int32 get_RenderWidth()",
        "method Int64 get_FrameIndex()",
        "method OptimumWarpState get_PrevWarp()",
        "method OptimumWarpState get_Warp()",
        "method Single get_DeltaTimeMs()",
        "method Single get_Fov()",
        "method Single get_ZFar()",
        "method Single get_ZNear()",
        "method Single[] ApplyJitterCopy(Double[])",
        "method Single[] GetPrevProjection(EnumTemporalView)",
        "method Single[] GetProjection(EnumTemporalView)",
        "method Single[] get_CameraMatrix()",
        "method Single[] get_CameraMatrixOrigin()",
        "method Single[] get_PrevCameraMatrix()",
        "method Single[] get_PrevCameraMatrixOrigin()",
        "method Vec2f get_JitterPx()",
        "method Vec2f get_JitterSequencePx()",
        "method Vec2f get_PrevJitterPx()",
        "method Vec3f get_CameraPosDelta()",
        "method Vec3f get_Playerpos()",
        "method Vec3f get_PrevPlayerpos()",
        "method Void Advance(Single, Int32, Int32, Single, Single, Single, Single, DefaultShaderUniforms)",
        "method Void ApplyMotionUniforms(IShaderProgram)",
        "method Void CaptureCamera(Double[], Double[])",
        "method Void CaptureCameraPosition(Vec3d, DefaultShaderUniforms)",
        "method Void RecordProjection(EnumTemporalView, Double[])",
        "method Void RequestReset(EnumTemporalResetReason)",
        "method Void set_JitterActive(Boolean)",
        "property EnumTemporalResetReason ResetReason get",
        "property EnumTemporalView ActiveView get",
        "property Boolean JitterActive get set",
        "property Boolean Reset get",
        "property Int32 RenderHeight get",
        "property Int32 RenderWidth get",
        "property Int64 FrameIndex get",
        "property OptimumWarpState PrevWarp get",
        "property OptimumWarpState Warp get",
        "property Single DeltaTimeMs get",
        "property Single Fov get",
        "property Single ZFar get",
        "property Single ZNear get",
        "property Single[] CameraMatrix get",
        "property Single[] CameraMatrixOrigin get",
        "property Single[] PrevCameraMatrix get",
        "property Single[] PrevCameraMatrixOrigin get",
        "property Vec2f JitterPx get",
        "property Vec2f JitterSequencePx get",
        "property Vec2f PrevJitterPx get",
        "property Vec3f CameraPosDelta get",
        "property Vec3f Playerpos get",
        "property Vec3f PrevPlayerpos get",
    };

    [Fact]
    public void TheTemporalContextSurfaceIsFrozen()
    {
        AssertSurface(typeof(IOptimumTemporalContext), ExpectedContextSurface, nameof(ExpectedContextSurface));
    }

    [Fact]
    public void TheTemporalFrameSurfaceIsFrozen()
    {
        AssertSurface(typeof(OptimumTemporalFrame), ExpectedFrameSurface, nameof(ExpectedFrameSurface));
    }

    [Fact]
    public void TheFrameIsTheContext()
    {
        Assert.True(
            typeof(IOptimumTemporalContext).IsAssignableFrom(typeof(OptimumTemporalFrame)),
            $"{Doc} specifies OptimumTemporal.Context as the read-only view of OptimumTemporal.Frame.");
        Assert.Same(OptimumTemporal.Frame, OptimumTemporal.Context);
    }

    /// <summary>
    /// The warp snapshot is the whole reason a writer can evaluate the vertex warp
    /// twice; section 1.2 lists its fields one by one.
    /// </summary>
    [Fact]
    public void TheWarpStateFieldsAreFrozen()
    {
        string[] expected =
        {
            "field Single GlitchWaviness",
            "field Single GlobalWarpIntensity",
            "field Int32 PerceptionEffectId",
            "field Single PerceptionEffectIntensity",
            "field Single TimeCounter",
            "field Single WaterWaveCounter",
            "field Single WaterWaveIntensity",
            "field Single WindSpeed",
            "field Single WindWaveCounter",
            "field Single WindWaveCounterHighFreq",
            "field Single WindWaveIntensity",
        };

        string[] actual = Surface(typeof(OptimumWarpState))
            .Where(entry => entry.StartsWith("field ", StringComparison.Ordinal))
            .ToArray();

        AssertSetsMatch(expected, actual, "OptimumWarpState", nameof(TheWarpStateFieldsAreFrozen));
    }

    /// <summary>
    /// Section 5 lists the reset reasons in declaration order, with a trigger for
    /// each. A new reason means a new trigger row and a contract version bump.
    /// </summary>
    [Fact]
    public void TheResetReasonsAreFrozenAndDocumented()
    {
        string[] expected =
        {
            "None", "WorldLoad", "Dimension", "Teleport", "Rebase", "Resize",
            "ShaderReload", "FovChange", "RenderScale", "Toggle", "Screenshot",
            "CameraHistoryLost",
        };

        string[] actual = Enum.GetNames(typeof(EnumTemporalResetReason));
        Assert.True(
            expected.SequenceEqual(actual),
            $"EnumTemporalResetReason changed. {Doc} section 5 lists the reasons in declaration "
            + $"order with their triggers; update it and bump the contract version.\nexpected: "
            + $"{string.Join(", ", expected)}\nactual:   {string.Join(", ", actual)}");

        string doc = ReadDoc();
        foreach (string reason in actual)
        {
            Assert.True(doc.Contains("`" + reason + "`", StringComparison.Ordinal),
                $"{Doc} section 5 does not document the reset reason {reason}.");
        }

        string[] views = Enum.GetNames(typeof(EnumTemporalView));
        Assert.True(new[] { "World", "Hand" }.SequenceEqual(views),
            $"EnumTemporalView changed; {Doc} section 1.1 documents exactly the World and Hand views.");
    }

    // ---------------------------------------------------------------------
    // 2. Jitter: the shear formula and the sequence
    // ---------------------------------------------------------------------

    /// <summary>
    /// Section 2 states the shear as P[8] -= 2*jx/W, P[9] -= 2*jy/H, and states
    /// what the number means: JitterPx is the raster displacement of a static
    /// point. The formula is pinned in the one implementation, in the document,
    /// and numerically - because a sign flip here silently inverts every motion
    /// vector's jitter removal.
    /// </summary>
    [Fact]
    public void TheJitterShearFormulaIsTheOneTheContractStates()
    {
        string math = Read("VintagestoryApi/Client/Render/OptimumTemporalMath.cs");
        Assert.True(math.Contains("projection[8] -= 2.0 * jitterX / renderWidth;", StringComparison.Ordinal),
            $"OptimumTemporalMath.ApplyProjectionJitter no longer matches the shear {Doc} section 2 freezes.");
        Assert.True(math.Contains("projection[9] -= 2.0 * jitterY / renderHeight;", StringComparison.Ordinal),
            $"OptimumTemporalMath.ApplyProjectionJitter no longer matches the shear {Doc} section 2 freezes.");

        string frame = Read("VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        Assert.True(frame.Contains("jitteredScratch[8] -= (float)(2.0 * JitterPx.X / RenderWidth);", StringComparison.Ordinal),
            $"OptimumTemporalFrame.ApplyJitterCopy diverged from the shear {Doc} section 2 freezes.");
        Assert.True(frame.Contains("jitteredScratch[9] -= (float)(2.0 * JitterPx.Y / RenderHeight);", StringComparison.Ordinal),
            $"OptimumTemporalFrame.ApplyJitterCopy diverged from the shear {Doc} section 2 freezes.");

        string doc = ReadDoc();
        Assert.True(doc.Contains("P[8] -= 2 * jx / renderWidth;", StringComparison.Ordinal),
            $"{Doc} section 2 no longer states the shear formula.");
        Assert.True(doc.Contains("P[9] -= 2 * jy / renderHeight;", StringComparison.Ordinal),
            $"{Doc} section 2 no longer states the shear formula.");

        // And the meaning of the sign: a static point moves by +jx raster pixels.
        const double width = 1920.0;
        const double height = 1080.0;
        double[] unjittered = Perspective(width, height);
        double[] jittered = (double[])unjittered.Clone();
        OptimumTemporalMath.ApplyProjectionJitter(jittered, 0.37, -0.21, width, height);

        (double x0, double y0) = ProjectToPixel(unjittered, 2.5, -1.25, -12.0, width, height);
        (double x1, double y1) = ProjectToPixel(jittered, 2.5, -1.25, -12.0, width, height);
        Assert.Equal(0.37, x1 - x0, 9);
        Assert.Equal(-0.21, y1 - y0, 9);
    }

    /// <summary>
    /// Section 2's phase count: max(1, ceil(8 * upscale^2)), 8 at native and 32 at
    /// render scale 0.5. The vendor adapters may read their own phase count from
    /// the SDK instead, which is why the shape is written down.
    /// </summary>
    [Fact]
    public void TheJitterPhaseCountIsTheOneTheContractStates()
    {
        Assert.Equal(8, OptimumTemporalMath.JitterPhaseCount(1f));
        Assert.Equal(32, OptimumTemporalMath.JitterPhaseCount(2f)); // render scale 0.5 -> upscale 2

        string doc = ReadDoc();
        Assert.True(doc.Contains("ceil(8 * upscale^2)", StringComparison.Ordinal),
            $"{Doc} section 2 no longer states the jitter phase count.");
    }

    // ---------------------------------------------------------------------
    // 3. Motion vector adapters
    // ---------------------------------------------------------------------

    /// <summary>
    /// Section 7.1: the stored vector is previousPixel - currentPixel in render
    /// pixels, and each adapter's scale and sign. The sign is the part a vendor
    /// integration cannot discover from a smeared image, so each adapter is
    /// checked for direction as well as magnitude.
    /// </summary>
    [Theory]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Fsr, 1.0f, 1.0f)]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Xess, 1.0f, 1.0f)]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Dlss, 1.0f / 1920f, 1.0f / 1080f)]
    public void EachAdapterScalesAndSignsTheMotionVectorAsTheContractStates(
        OptimumTemporalMath.MotionVectorAdapter adapter, float scaleX, float scaleY)
    {
        const int width = 1920;
        const int height = 1080;

        // A surface that moved right and down on screen: it WAS three pixels left
        // and two pixels below, so the stored vector (previous - current) is
        // (-3, -2). No adapter flips that sign; only the scale differs.
        (float x, float y) = OptimumTemporalMath.AdaptMotionVector(-3f, -2f, width, height, adapter);
        Assert.Equal(-3f * scaleX, x, 6);
        Assert.Equal(-2f * scaleY, y, 6);

        (float px, float py) = OptimumTemporalMath.AdaptMotionVector(4f, 5f, width, height, adapter);
        Assert.Equal(4f * scaleX, px, 6);
        Assert.Equal(5f * scaleY, py, 6);

        Assert.True(x < 0f && y < 0f,
            $"{Doc} section 7.1 states that no adapter flips the motion vector's sign.");
    }

    [Fact]
    public void TheAdapterSetIsFrozenAndDocumented()
    {
        string[] expected = { "Fsr", "Dlss", "Xess" };
        string[] actual = Enum.GetNames(typeof(OptimumTemporalMath.MotionVectorAdapter));
        Assert.True(expected.OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(actual.OrderBy(n => n, StringComparer.Ordinal)),
            $"The adapter set changed; {Doc} section 7 has one row per consumer and must be updated.");

        string doc = ReadDoc();
        Assert.True(doc.Contains("motionVectorScale = (1, 1)", StringComparison.Ordinal),
            $"{Doc} section 7.1 no longer states FSR 3.1's motion vector scale.");
        Assert.True(doc.Contains("mvecScale = (1 / renderWidth, 1 / renderHeight)", StringComparison.Ordinal),
            $"{Doc} section 7.1 no longer states DLSS's motion vector scale.");
        Assert.True(doc.Contains("XESS_INIT_FLAG_USE_NDC_VELOCITY", StringComparison.Ordinal),
            $"{Doc} section 7.1 no longer states XeSS 2's velocity units.");
        Assert.True(doc.Contains("depthInverted = false", StringComparison.Ordinal),
            $"{Doc} section 7.3 no longer states the depth convention handed to DLSS.");
    }

    // ---------------------------------------------------------------------
    // 4. The writer-depth validity tolerance
    // ---------------------------------------------------------------------

    /// <summary>
    /// Section 3.2's validity rule, verbatim. This one expression decides whether
    /// a pixel uses its writer's vector or the camera fallback, so it is the
    /// single most load-bearing line in the whole contract: loosening it accepts
    /// a writer that belongs to hidden geometry, tightening it demoted every
    /// close decal to the fallback (P4 finding (o)).
    /// </summary>
    [Fact]
    public void TheWriterDepthToleranceIsTheOneTheContractStates()
    {
        const string tolerance =
            "bool written = motion.a > 0.0 && abs(motion.a - depth) <= max(2e-4, 8e-4 * depth);";

        string resolve = Read("sources/shaders/taa-resolve.fsh");
        Assert.True(resolve.Contains(tolerance, StringComparison.Ordinal),
            $"taa-resolve.fsh no longer carries the writer-depth validity test {Doc} section 3.2 freezes. "
            + "Change the document and bump the contract version before changing this line.");

        string doc = ReadDoc();
        Assert.True(doc.Contains(tolerance, StringComparison.Ordinal),
            $"{Doc} section 3.2 no longer quotes the writer-depth validity test.");

        // And the channel semantics it rests on.
        Assert.True(resolve.Contains("uniform sampler2D motionTex;", StringComparison.Ordinal),
            $"taa-resolve.fsh no longer reads the motion attachment {Doc} section 3.2 describes.");
        Assert.True(resolve.Contains("float reactive = clamp(motion.b, 0.0, 1.0);", StringComparison.Ordinal),
            $"taa-resolve.fsh no longer reads reactive from motion.b, which {Doc} section 3.2 freezes.");
        Assert.True(resolve.Contains("vec2 historyUv = (pixelCentre + mv) * invSize;", StringComparison.Ordinal),
            $"taa-resolve.fsh no longer anchors the history lookup where {Doc} section 3.2 says it does.");
    }

    // ---------------------------------------------------------------------
    // 5. Resources: history slots, sharpen slot, attachment formats
    // ---------------------------------------------------------------------

    /// <summary>
    /// Section 3.3 and 3.4: history slot indices 19 and 20, the sharpen target 21,
    /// and the parity rule that decides which of the two is written. A consumer
    /// that wants the resolved image reads the slot the parity names.
    /// </summary>
    [Fact]
    public void TheHistoryAndSharpenSlotIndicesAreFrozen()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.True(platform.Contains("private const int OptimumTaaHistoryIndexA = 19;", StringComparison.Ordinal),
            $"The TAA history slot A index moved; {Doc} section 3.3 names it.");
        Assert.True(platform.Contains("private const int OptimumTaaHistoryIndexB = 20;", StringComparison.Ordinal),
            $"The TAA history slot B index moved; {Doc} section 3.3 names it.");
        Assert.True(platform.Contains("private const int OptimumTaaSharpenIndex = 21;", StringComparison.Ordinal),
            $"The TAA sharpen target index moved; {Doc} section 3.4 names it.");
        Assert.True(platform.Contains(
                "return frameBuffers[(parity & 1) == 0 ? OptimumTaaHistoryIndexA : OptimumTaaHistoryIndexB];",
                StringComparison.Ordinal),
            $"The history parity rule changed; {Doc} section 3.3 states it.");
        Assert.True(platform.Contains("FrameBufferRef write = TaaHistory(_taaFrameParity);", StringComparison.Ordinal)
            && platform.Contains("FrameBufferRef read = TaaHistory(_taaFrameParity + 1);", StringComparison.Ordinal),
            $"The resolve's read/write slot selection changed; {Doc} section 3.3 states it.");

        string doc = ReadDoc();
        Assert.True(doc.Contains("frame buffer slots 19 and 20", StringComparison.Ordinal),
            $"{Doc} section 3.3 no longer names the two history slots by index.");
        Assert.True(doc.Contains("frame buffer slot 21", StringComparison.Ordinal),
            $"{Doc} section 3.4 no longer names the sharpen slot by index.");
    }

    /// <summary>
    /// Section 3.1 and 3.3: the attachment formats and the sampler state that goes
    /// with them, on BOTH backends. The filters are load-bearing - history colour
    /// and glow are read at a fractional reprojected offset, and an interpolated
    /// linear depth across a silhouette belongs to neither surface. GL_R32F going
    /// missing from the device enum map once turned the depth history into RGBA8
    /// in silence (P2 finding (a)), which is why the formats are pinned and not
    /// left to a comment.
    /// </summary>
    [Fact]
    public void TheAttachmentFormatsAreFrozenOnBothBackends()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Motion attachment: RGBA16F on Primary, appended after every existing
        // attachment, on both paths.
        Assert.True(platform.Contains("int motionTextureId = device.CreateTexture2D(width, height,", StringComparison.Ordinal)
            && platform.Contains("EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);", StringComparison.Ordinal),
            $"The device path no longer creates the motion attachment as RGBA16F; {Doc} section 3.1 freezes the format.");
        // 34842 = GL_RGBA16F, the GL path's raw token for the same thing.
        Assert.True(platform.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)34842, num, num2, 0, val, (PixelType)5126, (IntPtr)IntPtr.Zero);", StringComparison.Ordinal),
            $"The GL path no longer creates the motion attachment as RGBA16F (34842); {Doc} section 3.1 freezes the format.");
        Assert.True(platform.Contains("public override int MotionAttachmentIndex", StringComparison.Ordinal)
            && platform.Contains("private int optimumMotionAttachmentIndex = -1;", StringComparison.Ordinal),
            $"MotionAttachmentIndex changed shape; {Doc} section 3.1 describes it as 2 without SSAO, 4 with, -1 when off.");

        // Primary depth: DepthComponent32 on the device path, 33191 = GL_DEPTH_COMPONENT32 on GL,
        // NEAREST + CLAMP_TO_EDGE on both.
        Assert.True(platform.Contains("EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);", StringComparison.Ordinal),
            $"Primary's depth format changed; {Doc} section 3.1 freezes it at 32-bit, 0 = near.");
        Assert.True(platform.Contains("SetupOptimumTextureSampler(device, primary.DepthTextureId, 9728, 33071);", StringComparison.Ordinal),
            $"Primary's depth sampler changed; {Doc} section 3.1 freezes NEAREST + CLAMP_TO_EDGE.");
        Assert.True(platform.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)33191, num, num2, 0, (PixelFormat)6402, (PixelType)5126, (IntPtr)IntPtr.Zero);", StringComparison.Ordinal),
            $"The GL path's Primary depth format changed; {Doc} section 3.1 freezes GL_DEPTH_COMPONENT32.");

        // History slot: RGBA16F colour (LINEAR), RGBA8 aux (LINEAR), R32F linear
        // depth (NEAREST), CLAMP_TO_EDGE throughout - device path.
        Assert.True(platform.Contains("private const int OptimumGlR32f = 0x822E;", StringComparison.Ordinal),
            $"The R32F token used for the linear depth history moved; {Doc} section 3.3 freezes the format.");
        Assert.True(platform.Contains("target.ColorTextureIds[2] = device.CreateTexture2DRaw(width, height, OptimumGlR32f, IntPtr.Zero, 4);", StringComparison.Ordinal),
            $"The device path's linear depth history is no longer R32F; {Doc} section 3.3 freezes it.");
        Assert.True(platform.Contains("SetupOptimumTextureSampler(device, target.ColorTextureIds[0], 9729, 33071);", StringComparison.Ordinal)
            && platform.Contains("SetupOptimumTextureSampler(device, target.ColorTextureIds[1], 9729, 33071);", StringComparison.Ordinal)
            && platform.Contains("SetupOptimumTextureSampler(device, target.ColorTextureIds[2], 9728, 33071);", StringComparison.Ordinal),
            $"The history slot's sampler state changed on the device path; {Doc} section 3.3 freezes "
            + "LINEAR colour, LINEAR glow, NEAREST linear depth, all CLAMP_TO_EDGE.");

        // Same slot, GL path: 34842 = RGBA16F, 32856 = RGBA8, OptimumGlR32f, with
        // matching filters (9729 LINEAR / 9728 NEAREST) and 33071 CLAMP_TO_EDGE.
        Assert.True(platform.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)34842, width, height, 0, (PixelFormat)6408, (PixelType)5126, (IntPtr)IntPtr.Zero);", StringComparison.Ordinal),
            $"The GL history colour attachment is no longer RGBA16F; {Doc} section 3.3 freezes it.");
        Assert.True(platform.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)32856, width, height, 0, (PixelFormat)6408, (PixelType)5121, (IntPtr)IntPtr.Zero);", StringComparison.Ordinal),
            $"The GL history aux attachment is no longer RGBA8; {Doc} section 3.3 freezes it.");
        Assert.True(platform.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)OptimumGlR32f, width, height, 0, (PixelFormat)6403, (PixelType)5126, (IntPtr)IntPtr.Zero);", StringComparison.Ordinal),
            $"The GL linear depth history is no longer R32F; {Doc} section 3.3 freezes it.");

        // Sharpen target: RGBA16F at render resolution, both paths.
        Assert.True(platform.Contains("list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(device, width, height,", StringComparison.Ordinal)
            && platform.Contains("EnumTextureInternalFormat.Rgba16f);", StringComparison.Ordinal),
            $"The sharpen target is no longer RGBA16F on the device path; {Doc} section 3.4 freezes it.");
        Assert.True(platform.Contains("setupAttachment(optimumSharpen, num, num2, 0, val, (PixelInternalFormat)34842);", StringComparison.Ordinal),
            $"The sharpen target is no longer RGBA16F on the GL path; {Doc} section 3.4 freezes it.");
    }

    /// <summary>
    /// Section 4: the resolve consumes exactly the contract. If a new input
    /// appears in the resolve that the contract does not describe, the contract
    /// is no longer the superset it claims to be.
    /// </summary>
    [Fact]
    public void TheResolveConsumesOnlyWhatTheContractDescribes()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");
        string[] samplers =
        {
            "sceneTex", "glowTex", "motionTex", "depthTex",
            "historyColor", "historyGlow", "historyDepth",
        };
        string[] uniforms =
        {
            "renderSize", "jitterPx", "invViewProjJittered", "prevViewProj",
            "viewMatrix", "cameraDelta", "resetHistory", "blendAlpha", "varianceGamma",
        };

        string doc = ReadDoc();
        foreach (string name in samplers.Concat(uniforms))
        {
            Assert.True(resolve.Contains(name, StringComparison.Ordinal),
                $"taa-resolve.fsh lost the input {name}; {Doc} section 4 tabulates it.");
            Assert.True(doc.Contains("`" + name + "`", StringComparison.Ordinal),
                $"{Doc} section 4 does not document the resolve input {name}.");
        }

        Assert.True(resolve.Contains("layout(location = 0) out vec4 outColor;", StringComparison.Ordinal)
            && resolve.Contains("layout(location = 1) out vec4 outGlow;", StringComparison.Ordinal)
            && resolve.Contains("layout(location = 2) out vec4 outDepth;", StringComparison.Ordinal),
            $"The resolve's MRT layout changed; {Doc} section 4 maps the three outputs onto the history slot.");
    }

    // ---------------------------------------------------------------------
    // 6. The document itself
    // ---------------------------------------------------------------------

    [Fact]
    public void TheContractDocumentIsVersionedAndComplete()
    {
        string doc = ReadDoc();

        Assert.True(doc.Contains("**Version:** `" + ContractVersion + "`", StringComparison.Ordinal),
            $"{Doc} no longer declares its version. A contract without a version cannot be frozen.");
        Assert.True(doc.Contains("Optimum.Tests/temporal-contract-tests.cs", StringComparison.Ordinal),
            $"{Doc} no longer names its own stability test.");

        string[] sections =
        {
            "## 1. The per-frame input record",
            "## 2. Jitter",
            "## 3. Resources",
            "## 4. The resolve's own inputs",
            "## 5. Reset",
            "## 6. Per-class motion status",
            "## 7. Adapters",
            "## 8. Reserved for the vendor plan",
        };
        foreach (string section in sections)
        {
            Assert.True(doc.Contains(section, StringComparison.Ordinal),
                $"{Doc} lost the section \"{section}\".");
        }

        // The reservations are the boundary of the contract; losing one would let
        // a vendor integration assume the engine owns something it does not.
        foreach (string reserved in new[]
                 {
                     "Native handles", "Extension negotiation", "Presentation lifetime",
                     "Ray-reconstruction guides",
                 })
        {
            Assert.True(doc.Contains(reserved, StringComparison.Ordinal),
                $"{Doc} section 8 no longer reserves \"{reserved}\" for the vendor plan.");
        }
    }

    /// <summary>
    /// The plan points at the contract, and says which version. Without this the
    /// document is just another file in docs/.
    /// </summary>
    [Fact]
    public void ThePlanPointsAtTheContract()
    {
        string plan = Read("TAA-PLAN.md");

        Assert.True(plan.Contains("docs/temporal-frame-contract.md", StringComparison.Ordinal),
            $"TAA-PLAN.md P6 no longer points at {Doc}.");
        Assert.True(plan.Contains("**Contract**", StringComparison.Ordinal),
            "TAA-PLAN.md P6 no longer carries the Contract section pointer.");
        Assert.True(plan.Contains(ContractVersion, StringComparison.Ordinal),
            $"TAA-PLAN.md P6 no longer names the frozen contract version ({ContractVersion}).");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static void AssertSurface(Type type, string[] expected, string listName)
    {
        string[] actual = Surface(type);
        AssertSetsMatch(expected, actual, type.Name, listName);
    }

    private static void AssertSetsMatch(string[] expected, string[] actual, string typeName, string listName)
    {
        string[] added = actual.Except(expected, StringComparer.Ordinal).ToArray();
        string[] removed = expected.Except(actual, StringComparer.Ordinal).ToArray();

        if (added.Length == 0 && removed.Length == 0) return;

        string message =
            $"The public surface of {typeName} changed. It is frozen by {Doc} (contract {ContractVersion}).\n"
            + "Update the document, bump its version, and replace "
            + $"{listName} in Optimum.Tests/temporal-contract-tests.cs with the actual list below.\n"
            + (added.Length > 0 ? "added:\n  " + string.Join("\n  ", added) + "\n" : "")
            + (removed.Length > 0 ? "removed:\n  " + string.Join("\n  ", removed) + "\n" : "")
            + "actual:\n"
            + string.Join("\n", actual.Select(entry => "        \"" + entry + "\","));

        Assert.Fail(message);
    }

    /// <summary>
    /// The public surface of a type as stable strings: every declared public
    /// member with its kind, its type and its parameter types, ordinal-sorted.
    /// Parameter names are deliberately excluded - renaming a parameter is not a
    /// contract change, adding one is.
    /// </summary>
    private static string[] Surface(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        List<string> entries = new();

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            string accessors = (property.GetMethod is { IsPublic: true } ? " get" : "")
                + (property.SetMethod is { IsPublic: true } ? " set" : "");
            entries.Add($"property {Name(property.PropertyType)} {property.Name}{accessors}");
        }

        foreach (FieldInfo field in type.GetFields(flags))
        {
            entries.Add($"field {Name(field.FieldType)} {field.Name}");
        }

        foreach (MethodInfo method in type.GetMethods(flags))
        {
            entries.Add($"method {Name(method.ReturnType)} {method.Name}("
                + string.Join(", ", method.GetParameters().Select(p => Name(p.ParameterType))) + ")");
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(flags))
        {
            entries.Add("ctor .ctor("
                + string.Join(", ", constructor.GetParameters().Select(p => Name(p.ParameterType))) + ")");
        }

        entries.Sort(StringComparer.Ordinal);
        return entries.ToArray();
    }

    private static string Name(Type type) => type.Name;

    private static string ReadDoc() => Read(Doc);

    private static string Read(string relativePath)
        => File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    /// <summary>
    /// A column-major perspective matrix exactly as Mat4d.Perspective builds one
    /// (clip.w = -z_view), so the shear check is against the real convention.
    /// </summary>
    private static double[] Perspective(double width, double height)
    {
        double fovy = 70.0 * Math.PI / 180.0;
        double aspect = width / height;
        double near = 0.1;
        double far = 1000.0;

        double f = 1.0 / Math.Tan(fovy / 2.0);
        double nf = 1.0 / (near - far);

        double[] output = new double[16];
        output[0] = f / aspect;
        output[5] = f;
        output[10] = (far + near) * nf;
        output[11] = -1;
        output[14] = (2 * far * near) * nf;
        return output;
    }

    private static (double X, double Y) ProjectToPixel(double[] m, double x, double y, double z, double width, double height)
    {
        double clipX = m[0] * x + m[4] * y + m[8] * z + m[12];
        double clipY = m[1] * x + m[5] * y + m[9] * z + m[13];
        double clipW = m[3] * x + m[7] * y + m[11] * z + m[15];

        double ndcX = clipX / clipW;
        double ndcY = clipY / clipW;

        return ((ndcX * 0.5 + 0.5) * width, (ndcY * 0.5 + 0.5) * height);
    }
}
