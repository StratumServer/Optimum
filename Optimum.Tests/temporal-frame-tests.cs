using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The per-frame temporal contract: rotation, the jitter sequence, the reset
/// detectors and the one-frame lifetime of the reset flag. All of it runs off a
/// bare OptimumTemporalFrame instance, so none of it needs a render context - the
/// static OptimumTemporal.Frame is deliberately left alone here.
/// </summary>
public class TemporalFrameTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static OptimumTemporalFrame NewFrame(bool jitterActive = true)
    {
        var frame = new OptimumTemporalFrame();
        frame.JitterActive = jitterActive;
        return frame;
    }

    private static DefaultShaderUniforms Uniforms(double refX = 0, double refZ = 0)
    {
        return new DefaultShaderUniforms
        {
            playerReferencePos = new Vec3d(refX, 0, refZ),
            TimeCounter = 1f,
            WindWaveCounter = 2f,
            WindWaveCounterHighFreq = 3f,
            WaterWaveCounter = 4f,
            WindSpeed = 5f,
            GlobalWorldWarp = 6f,
            GlitchWaviness = 7f,
            WindWaveIntensity = 8f,
            WaterWaveIntensity = 9f,
            PerceptionEffectId = 3,
            PerceptionEffectIntensity = 0.5f
        };
    }

    /// <summary>
    /// One whole frame's worth of contract updates in the order ClientMain does
    /// them: Advance rotates and computes the jitter, then - once the Before
    /// render stage has written the camera position and playerpos -
    /// CaptureCameraPosition takes those.
    /// </summary>
    private static void Advance(OptimumTemporalFrame frame, Vec3d cameraPos, DefaultShaderUniforms uniforms, float renderScale = 1f)
    {
        frame.Advance(16.6f, Width, Height, renderScale, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(cameraPos, uniforms);
    }

    // --- rotation ------------------------------------------------------------

    [Fact]
    public void AdvanceIncrementsFrameIndex()
    {
        var frame = NewFrame();
        Assert.Equal(0L, frame.FrameIndex);

        Advance(frame, new Vec3d(0, 0, 0), Uniforms());
        Assert.Equal(1L, frame.FrameIndex);

        Advance(frame, new Vec3d(0, 0, 0), Uniforms());
        Assert.Equal(2L, frame.FrameIndex);
    }

    [Fact]
    public void AdvanceRotatesProjectionsCameraMatricesAndWarpState()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var cameraPos = new Vec3d(10, 64, 10);

        Advance(frame, cameraPos, uniforms);
        frame.RecordProjection(EnumTemporalView.World, Diagonal(1.0));
        frame.RecordProjection(EnumTemporalView.Hand, Diagonal(2.0));
        frame.CaptureCamera(Diagonal(3.0), Diagonal(4.0));

        Assert.True(frame.IsViewCaptured(EnumTemporalView.World));
        Assert.True(frame.IsViewCaptured(EnumTemporalView.Hand));

        // Second frame: the first frame's values must have moved into the previous slots.
        var uniforms2 = Uniforms();
        uniforms2.TimeCounter = 42f;
        Advance(frame, cameraPos, uniforms2);

        Assert.Equal(1f, frame.GetPrevProjection(EnumTemporalView.World)[0]);
        Assert.Equal(2f, frame.GetPrevProjection(EnumTemporalView.Hand)[0]);
        Assert.Equal(3f, frame.PrevCameraMatrix[0]);
        Assert.Equal(4f, frame.PrevCameraMatrixOrigin[0]);
        Assert.Equal(1f, frame.PrevWarp.TimeCounter);
        Assert.Equal(42f, frame.Warp.TimeCounter);

        // The capture flags rotate too: nothing was recorded in the second frame yet.
        Assert.False(frame.IsViewCaptured(EnumTemporalView.World));
        Assert.True(frame.WasViewCaptured(EnumTemporalView.World));
    }

    [Fact]
    public void WarpStateReadsEveryUniformTheVertexWarpConsumes()
    {
        var frame = NewFrame();
        Advance(frame, new Vec3d(0, 0, 0), Uniforms());

        OptimumWarpState warp = frame.Warp;
        Assert.Equal(1f, warp.TimeCounter);
        Assert.Equal(2f, warp.WindWaveCounter);
        Assert.Equal(3f, warp.WindWaveCounterHighFreq);
        Assert.Equal(4f, warp.WaterWaveCounter);
        Assert.Equal(5f, warp.WindSpeed);
        Assert.Equal(6f, warp.GlobalWarpIntensity);
        Assert.Equal(7f, warp.GlitchWaviness);
        Assert.Equal(8f, warp.WindWaveIntensity);
        Assert.Equal(9f, warp.WaterWaveIntensity);
        Assert.Equal(3, warp.PerceptionEffectId);
        Assert.Equal(0.5f, warp.PerceptionEffectIntensity);
    }

    [Fact]
    public void PlayerposRotatesFromTheShaderUniforms()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        uniforms.PlayerPos.Set(1f, 2f, 3f);
        Advance(frame, new Vec3d(0, 0, 0), uniforms);
        uniforms.PlayerPos.Set(4f, 5f, 6f);
        Advance(frame, new Vec3d(0, 0, 0), uniforms);

        Assert.Equal(4f, frame.Playerpos.X);
        Assert.Equal(1f, frame.PrevPlayerpos.X);
        Assert.Equal(3f, frame.PrevPlayerpos.Z);
    }

    // --- jitter ---------------------------------------------------------------

    [Fact]
    public void JitterPhaseCyclesWithPeriodEightAtNativeResolution()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        var first = new (float X, float Y)[8];
        for (int i = 0; i < 8; i++)
        {
            Advance(frame, pos, uniforms);
            first[i] = (frame.JitterPx.X, frame.JitterPx.Y);
        }

        for (int i = 0; i < 8; i++)
        {
            Advance(frame, pos, uniforms);
            Assert.Equal(first[i].X, frame.JitterPx.X, 6);
            Assert.Equal(first[i].Y, frame.JitterPx.Y, 6);
        }

        // Eight distinct offsets, not one value repeated.
        for (int i = 1; i < 8; i++)
        {
            Assert.NotEqual((first[0].X, first[0].Y), (first[i].X, first[i].Y));
        }
    }

    [Fact]
    public void JitterIsNeverZeroAndStaysWithinHalfAPixel()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        for (int i = 0; i < 64; i++)
        {
            Advance(frame, pos, uniforms);
            Assert.False(frame.JitterPx.X == 0f && frame.JitterPx.Y == 0f);
            Assert.InRange(frame.JitterPx.X, -0.5f, 0.5f);
            Assert.InRange(frame.JitterPx.Y, -0.5f, 0.5f);
        }
    }

    [Fact]
    public void ClosedJitterWindowReportsZeroButKeepsTheSequence()
    {
        var frame = NewFrame(jitterActive: false);
        Advance(frame, new Vec3d(0, 0, 0), Uniforms());

        Assert.Equal(0f, frame.JitterPx.X);
        Assert.Equal(0f, frame.JitterPx.Y);
        Assert.False(frame.JitterSequencePx.X == 0f && frame.JitterSequencePx.Y == 0f);

        // Opening the window mid-frame publishes the sequence offset.
        frame.JitterActive = true;
        Assert.Equal(frame.JitterSequencePx.X, frame.JitterPx.X);
    }

    [Fact]
    public void PreviousJitterIsTheOffsetTheLastFrameApplied()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        Advance(frame, pos, uniforms);
        float firstX = frame.JitterPx.X;
        float firstY = frame.JitterPx.Y;

        Advance(frame, pos, uniforms);
        Assert.Equal(firstX, frame.PrevJitterPx.X);
        Assert.Equal(firstY, frame.PrevJitterPx.Y);
    }

    [Fact]
    public void ApplyJitterCopyShearsAndDoesNotTouchTheInput()
    {
        var frame = NewFrame();
        Advance(frame, new Vec3d(0, 0, 0), Uniforms());

        double[] projection = Diagonal(1.0);
        float[] jittered = frame.ApplyJitterCopy(projection);

        Assert.Equal(0.0, projection[8]);
        Assert.Equal((float)(-2.0 * frame.JitterPx.X / Width), jittered[8], 6);
        Assert.Equal((float)(-2.0 * frame.JitterPx.Y / Height), jittered[9], 6);
    }

    // --- reset detection ------------------------------------------------------

    [Fact]
    public void CameraDeltaAboveEightBlocksIsATeleport()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        Advance(frame, new Vec3d(100, 64, 100), uniforms);
        Advance(frame, new Vec3d(100.5, 64, 100), uniforms);
        Assert.False(frame.Reset);
        Assert.Equal(0.5f, frame.CameraPosDelta.X, 4);

        Advance(frame, new Vec3d(400, 64, 100), uniforms);
        Assert.True(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.Teleport, frame.ResetReason);
        // A reset frame has no meaningful camera delta to reproject with.
        Assert.Equal(0f, frame.CameraPosDelta.X);
    }

    [Fact]
    public void CameraDeltaAtTheThresholdIsStillMotion()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        Advance(frame, new Vec3d(0, 0, 0), uniforms);
        Advance(frame, new Vec3d(7.9, 0, 0), uniforms);

        Assert.False(frame.Reset);
        Assert.Equal(7.9f, frame.CameraPosDelta.X, 4);
    }

    [Fact]
    public void ReferencePositionRebaseIsAReset()
    {
        var frame = NewFrame();
        var pos = new Vec3d(0, 0, 0);

        Advance(frame, pos, Uniforms(0, 0));
        Advance(frame, pos, Uniforms(0, 0));
        Assert.False(frame.Reset);

        Advance(frame, pos, Uniforms(512000, 0));
        Assert.True(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.Rebase, frame.ResetReason);
    }

    [Fact]
    public void RenderSizeChangeIsAResizeReset()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        Advance(frame, pos, uniforms);
        frame.Advance(16.6f, 1280, 720, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(pos, uniforms);

        Assert.True(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.Resize, frame.ResetReason);
        Assert.Equal(1280, frame.RenderWidth);
    }

    // --- camera position capture ordering --------------------------------------

    /// <summary>
    /// The camera position is written by PlayerCamera from inside the Before
    /// render stage, which runs AFTER Advance and BEFORE the camera matrices are
    /// frozen. Capturing it in Advance therefore read the previous frame's
    /// position, and every terrain, entity and instanced writer reprojected a
    /// static surface by cam(N-1) - cam(N-2) while its previous view matrix was
    /// the genuine cam(N-1) rotation. The two agree only while the camera moves
    /// at a constant speed; every acceleration showed up as motion on ground
    /// that never moved.
    ///
    /// This drives the contract in the order ClientMain does it, with the camera
    /// moving in between, and demands the delta of the frame being drawn.
    /// </summary>
    [Fact]
    public void CameraDeltaIsTheMovementOfTheFrameBeingDrawnNotThePreviousOne()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        // Frame 1: camera at the origin.
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(0, 64, 0), uniforms);

        // Frame 2: the camera moved one block since frame 1.
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(1, 64, 0), uniforms);
        Assert.Equal(1f, frame.CameraPosDelta.X, 5);

        // Frame 3: the camera accelerated to four blocks per frame. A capture
        // taken before the Before stage would still report the one block of
        // frame 2 here, which is exactly the failure this guards.
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(5, 64, 0), uniforms);
        Assert.Equal(4f, frame.CameraPosDelta.X, 5);

        // Frame 4: the camera stopped. Still exact, not the previous four.
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(5, 64, 0), uniforms);
        Assert.Equal(0f, frame.CameraPosDelta.X, 5);
    }

    /// <summary>
    /// The warp noise is sampled at worldPos + playerpos, so the previous frame's
    /// playerpos has to be the one the previous frame's draws actually used.
    /// PlayerCamera writes it in the same Before stage as the camera position.
    /// </summary>
    [Fact]
    public void PreviousPlayerposIsThePositionTheFrameBeforeReallyDrewWith()
    {
        var frame = NewFrame();

        var first = Uniforms();
        first.PlayerPos.Set(1, 2, 3);
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, first);
        frame.CaptureCameraPosition(new Vec3d(0, 0, 0), first);
        Assert.Equal(1f, frame.Playerpos.X, 5);

        var second = Uniforms();
        second.PlayerPos.Set(4, 5, 6);
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, second);
        frame.CaptureCameraPosition(new Vec3d(0, 0, 0), second);

        Assert.Equal(4f, frame.Playerpos.X, 5);
        Assert.Equal(1f, frame.PrevPlayerpos.X, 5);
        Assert.Equal(2f, frame.PrevPlayerpos.Y, 5);
        Assert.Equal(3f, frame.PrevPlayerpos.Z, 5);
    }

    /// <summary>
    /// Capturing twice in one frame - a second render pass, a debug capture - must
    /// difference against the previous frame both times, never against the first
    /// call's own value, or the second call reports a zero delta.
    /// </summary>
    [Fact]
    public void CapturingTheCameraPositionTwiceInOneFrameKeepsTheSameDelta()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(0, 0, 0), uniforms);

        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(2, 0, 0), uniforms);
        Assert.Equal(2f, frame.CameraPosDelta.X, 5);

        frame.CaptureCameraPosition(new Vec3d(2, 0, 0), uniforms);
        Assert.Equal(2f, frame.CameraPosDelta.X, 5);
    }

    /// <summary>
    /// A frame that never reaches the capture (no world, no player) must report no
    /// camera movement rather than repeating the previous frame's delta, which a
    /// writer would apply to geometry that did not move.
    /// </summary>
    [Fact]
    public void AdvanceWithoutACaptureLeavesNoCameraMovement()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();

        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(0, 0, 0), uniforms);
        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(3, 0, 0), uniforms);
        Assert.Equal(3f, frame.CameraPosDelta.X, 5);

        frame.Advance(16.6f, Width, Height, 1f, 0.1f, 3000f, 1.2f, uniforms);
        Assert.Equal(0f, frame.CameraPosDelta.X, 5);
        Assert.Equal(0f, frame.CameraPosDelta.Y, 5);
        Assert.Equal(0f, frame.CameraPosDelta.Z, 5);
    }

    // --- reset flag lifetime ---------------------------------------------------

    [Fact]
    public void RequestedResetLastsExactlyOneFrame()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        Advance(frame, pos, uniforms);
        Assert.False(frame.Reset);

        frame.RequestReset(EnumTemporalResetReason.FovChange);
        Assert.False(frame.Reset); // not visible until the next Advance

        Advance(frame, pos, uniforms);
        Assert.True(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.FovChange, frame.ResetReason);

        Advance(frame, pos, uniforms);
        Assert.False(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.None, frame.ResetReason);
    }

    [Fact]
    public void TheEarliestRequestedReasonWins()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        var pos = new Vec3d(0, 0, 0);

        frame.RequestReset(EnumTemporalResetReason.WorldLoad);
        frame.RequestReset(EnumTemporalResetReason.ShaderReload);
        Advance(frame, pos, uniforms);

        Assert.Equal(EnumTemporalResetReason.WorldLoad, frame.ResetReason);
    }

    [Fact]
    public void NoneIsNotARequestableReason()
    {
        var frame = NewFrame();
        frame.RequestReset(EnumTemporalResetReason.None);
        Advance(frame, new Vec3d(0, 0, 0), Uniforms());

        Assert.False(frame.Reset);
    }

    // --- config ----------------------------------------------------------------

    [Fact]
    public void TaaDefaultsToOff()
    {
        Assert.False(Vintagestory.API.Config.OptimumConfig.Taa);
        Assert.False(Vintagestory.API.Config.OptimumConfig.TaaJitterDev);
    }

    private static double[] Diagonal(double value)
    {
        double[] m = new double[16];
        m[0] = value;
        m[5] = value;
        m[10] = value;
        m[15] = value;
        return m;
    }

    [Fact]
    public void PreviousJitterIsTheJitterTheLastFrameRenderedWith()
    {
        var frame = NewFrame();
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, Uniforms());
        frame.JitterActive = true;
        float jx = frame.JitterPx.X, jy = frame.JitterPx.Y;
        Assert.False(jx == 0f && jy == 0f);
        // The window closes at the end of the frame, zeroing JitterPx.
        frame.JitterActive = false;
        Assert.Equal(0f, frame.JitterPx.X);
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, Uniforms());
        Assert.Equal(jx, frame.PrevJitterPx.X);
        Assert.Equal(jy, frame.PrevJitterPx.Y);
    }

    [Fact]
    public void AFrameWithoutACameraCaptureDropsTheHistoryOnTheNextAdvance()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(1, 2, 3), uniforms);
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, uniforms);
        frame.CaptureCameraPosition(new Vec3d(1, 2, 3.5), uniforms);
        Assert.False(frame.Reset);
        // This frame never captures.
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, uniforms);
        Assert.False(frame.Reset);
        frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, uniforms);
        Assert.True(frame.Reset);
        Assert.Equal(EnumTemporalResetReason.CameraHistoryLost, frame.ResetReason);
    }

    [Fact]
    public void CapturingEveryFrameNeverDropsTheHistory()
    {
        var frame = NewFrame();
        var uniforms = Uniforms();
        for (int i = 0; i < 5; i++)
        {
            frame.Advance(16f, Width, Height, 1f, 0.1f, 1000f, 70f, uniforms);
            frame.CaptureCameraPosition(new Vec3d(i * 0.1, 0, 0), uniforms);
            Assert.False(frame.Reset && frame.ResetReason == EnumTemporalResetReason.CameraHistoryLost);
        }
    }
}
