using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The per-instance previous-transform store behind the TAA P3 instanced writer,
/// driven directly. This is the half the GPU test cannot reach: the shader is
/// handed a previous transform and a validity flag per instance, and everything
/// that can go wrong upstream of it - matching an instance to the wrong device,
/// believing a transform from two frames ago, trusting a slot that another gear
/// occupied last frame - looks identical on the GPU side.
///
/// The mechanical-power renderers rebuild the whole instance buffer every frame
/// from a dictionary whose enumeration order changes as blocks are placed, broken
/// and streamed in, so "slot 3 last frame" is not this instance's previous
/// transform. These tests state that in the terms the renderer uses.
///
/// They drive the process-wide OptimumTemporal.Frame, which no other test touches
/// (TemporalFrameTests deliberately uses its own instance), and restore the
/// writer's enable flag afterwards.
/// </summary>
public class TaaInstancedMotionHistoryTests
{
    private static readonly double[] IdentityProjection =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Transform(float x) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        x,  0f, 0f, 1f,
    };

    private static void AdvanceFrame()
    {
        OptimumTemporalFrame frame = OptimumTemporal.Frame;
        frame.Advance(16.6f, 1920, 1080, 1f, 0.1f, 3000f, 1.2f, new Vec3d(0, 0, 0), new DefaultShaderUniforms());
        // A view is only usable as "previous" once it has been captured, which is
        // what Set3DProjection does in the client.
        frame.RecordProjection(EnumTemporalView.World, IdentityProjection);
    }

    private static float PrevX(float[] values, int index)
    {
        // Column-major mat4: the translation's x is component 12.
        return values[index * OptimumInstanceMotion.InstanceFloats + OptimumInstanceMotion.PrevTransformOffset + 12];
    }

    private static float Valid(float[] values, int index)
    {
        return values[index * OptimumInstanceMotion.InstanceFloats + OptimumInstanceMotion.MetaOffset];
    }

    private static float Reactive(float[] values, int index)
    {
        return values[index * OptimumInstanceMotion.InstanceFloats + OptimumInstanceMotion.MetaOffset + 1];
    }

    private static void Write(float[] buffer, int index, object device, float x)
    {
        OptimumInstanceMotion.NoteDevice(device);
        OptimumInstanceMotion.WriteInstance(buffer, index, new Vec4f(1, 1, 1, 1), Transform(x));
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// The instance layout is a contract with instanced.vsh: rgbaLightIn at
    /// location 4, transform at 5..8, prevTransform at 9..12 and the metadata at
    /// 13, which the attribute assignment produces from ten interleaved vec4s.
    /// </summary>
    [Fact]
    public void TheInstanceLayoutMatchesTheShadersAttributeNumbering()
    {
        CustomMeshDataPartFloat part = OptimumInstanceMotion.CreateInstanceFloats(3);

        Assert.Equal(40, OptimumInstanceMotion.InstanceFloats);
        Assert.Equal(120, part.Values.Length);
        Assert.Equal(120, part.AllocationSize);
        Assert.Equal(160, part.InterleaveStride);
        Assert.Equal(new[] { 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 }, part.InterleaveSizes);
        Assert.Equal(new[] { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144 }, part.InterleaveOffsets);
        Assert.True(part.Instanced);
        Assert.False(part.StaticDraw);

        // The offsets the writer uses have to be the same ones, in floats.
        Assert.Equal(0, OptimumInstanceMotion.LightOffset);
        Assert.Equal(4, OptimumInstanceMotion.TransformOffset);
        Assert.Equal(20, OptimumInstanceMotion.PrevTransformOffset);
        Assert.Equal(36, OptimumInstanceMotion.MetaOffset);
    }

    // ----------------------------------------------------------------- history

    [Fact]
    public void ADeviceDrawnForTheFirstTimeGetsNoHistory()
    {
        OptimumEntityMotion.Enabled = true;
        try
        {
            var buffer = new float[OptimumInstanceMotion.InstanceFloats];
            var device = new object();

            AdvanceFrame();
            AdvanceFrame();
            Write(buffer, 0, device, 5f);

            Assert.Equal(0f, Valid(buffer, 0));
            Assert.Equal(1f, Reactive(buffer, 0));
            // The previous transform still has to be a sane matrix rather than
            // zeros, because a zero matrix would put prevClip.w at 0 and lose the
            // fragment to the "unwritten" branch instead of the camera fallback.
            Assert.Equal(5f, PrevX(buffer, 0));
        }
        finally
        {
            OptimumEntityMotion.Enabled = false;
        }
    }

    [Fact]
    public void TheSameDeviceGetsTheTransformItWasDrawnWithLastFrame()
    {
        OptimumEntityMotion.Enabled = true;
        try
        {
            var buffer = new float[OptimumInstanceMotion.InstanceFloats];
            var device = new object();

            AdvanceFrame();
            AdvanceFrame();
            Write(buffer, 0, device, 1f);

            AdvanceFrame();
            Write(buffer, 0, device, 2f);

            Assert.Equal(1f, Valid(buffer, 0));
            Assert.Equal(0f, Reactive(buffer, 0));
            Assert.Equal(1f, PrevX(buffer, 0));
        }
        finally
        {
            OptimumEntityMotion.Enabled = false;
        }
    }

    /// <summary>
    /// The failure the whole design exists to prevent: the buffer is rebuilt every
    /// frame and its order is the order of a dictionary, so two devices can swap
    /// slots between frames without anything moving on screen. Keyed on the slot,
    /// both gears would be reprojected by the other one's matrix.
    /// </summary>
    [Fact]
    public void ReorderedInstancesStillGetTheirOwnPreviousTransform()
    {
        OptimumEntityMotion.Enabled = true;
        try
        {
            var buffer = new float[2 * OptimumInstanceMotion.InstanceFloats];
            var deviceA = new object();
            var deviceB = new object();

            AdvanceFrame();
            AdvanceFrame();
            Write(buffer, 0, deviceA, 1f);
            Write(buffer, 1, deviceB, 10f);

            AdvanceFrame();
            Write(buffer, 0, deviceB, 20f);
            Write(buffer, 1, deviceA, 2f);

            Assert.Equal(1f, Valid(buffer, 0));
            Assert.Equal(10f, PrevX(buffer, 0));
            Assert.Equal(1f, Valid(buffer, 1));
            Assert.Equal(1f, PrevX(buffer, 1));
        }
        finally
        {
            OptimumEntityMotion.Enabled = false;
        }
    }

    /// <summary>
    /// A device that was not drawn in the previous frame - the chunk was out of
    /// range, the network was rebuilt, the block was just placed - has no previous
    /// position in this camera's space, so it must not be given one two frames old.
    /// </summary>
    [Fact]
    public void ADeviceThatMissedAFrameGetsNoHistory()
    {
        OptimumEntityMotion.Enabled = true;
        try
        {
            var buffer = new float[OptimumInstanceMotion.InstanceFloats];
            var device = new object();

            AdvanceFrame();
            AdvanceFrame();
            Write(buffer, 0, device, 1f);

            AdvanceFrame();   // drawn nowhere this frame
            AdvanceFrame();
            Write(buffer, 0, device, 3f);

            Assert.Equal(0f, Valid(buffer, 0));
            Assert.Equal(1f, Reactive(buffer, 0));
            Assert.Equal(3f, PrevX(buffer, 0));
        }
        finally
        {
            OptimumEntityMotion.Enabled = false;
        }
    }

    /// <summary>
    /// The same device drawn twice into the same buffer in one frame - which the
    /// pulverizer does for its two pounders - still compares against the frame
    /// before, not against its own first write.
    /// </summary>
    [Fact]
    public void TwoWritesInOneFrameBothCompareAgainstTheFrameBefore()
    {
        OptimumEntityMotion.Enabled = true;
        try
        {
            var buffer = new float[2 * OptimumInstanceMotion.InstanceFloats];
            var device = new object();

            AdvanceFrame();
            AdvanceFrame();
            Write(buffer, 0, device, 1f);

            AdvanceFrame();
            Write(buffer, 0, device, 2f);
            Write(buffer, 1, device, 3f);

            Assert.Equal(1f, PrevX(buffer, 0));
            Assert.Equal(1f, PrevX(buffer, 1));
        }
        finally
        {
            OptimumEntityMotion.Enabled = false;
        }
    }

    /// <summary>
    /// With TAA off the writers are not compiled into the shaders at all, so the
    /// store does no bookkeeping - the instance still gets a well-formed previous
    /// transform and a zero validity flag, which is what the unused attributes
    /// carry.
    /// </summary>
    [Fact]
    public void WithTheWriterDisabledNoHistoryIsKept()
    {
        OptimumEntityMotion.Enabled = false;

        var buffer = new float[OptimumInstanceMotion.InstanceFloats];
        var device = new object();

        AdvanceFrame();
        AdvanceFrame();
        Write(buffer, 0, device, 1f);
        AdvanceFrame();
        Write(buffer, 0, device, 2f);

        Assert.Equal(0f, Valid(buffer, 0));
        Assert.Equal(2f, PrevX(buffer, 0));
    }

    /// <summary>
    /// A slot beyond the buffer is dropped rather than throwing: the renderers
    /// size their buffers for a fixed number of devices and a network larger than
    /// that would otherwise take the client down.
    /// </summary>
    [Fact]
    public void AnInstanceBeyondTheBufferIsDropped()
    {
        var buffer = new float[OptimumInstanceMotion.InstanceFloats];
        OptimumInstanceMotion.NoteDevice(new object());
        OptimumInstanceMotion.WriteInstance(buffer, 1, new Vec4f(1, 1, 1, 1), Transform(1f));

        foreach (float value in buffer) Assert.Equal(0f, value);
    }
}
