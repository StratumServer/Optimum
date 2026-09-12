using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Reconstruct a static, analytically jittered scene through real NGX. Keep a
/// complete Halton cycle of outputs on the GPU, presenting between evaluations;
/// read back only after the sequence, so CPU waits cannot hide temporal faults.
/// The Fourier phase measures subpixel displacement independently of sharpness.
/// </summary>
[Collection(NgxCollection.Name)]
public class DlssJitterConventionTests(ITestOutputHelper output, NgxRuntime ngx)
{
    private const int Width = 1280, Height = 744;
    private const double Period = 16;

    [SkippableTheory]
    [InlineData(640, 372, false)]
    [InlineData(426, 248, true)]
    public void StaticSceneStaysRegisteredAcrossTheWholeJitterCycle(int renderWidth, int renderHeight, bool ultra)
    {
        ngx.Require();
        VulkanDevice device = ngx.Device;
        int mark = ngx.MessageMark();
        var settings = new NgxDlssSettings((uint)renderWidth, (uint)renderHeight, Width, Height,
            ultra ? NgxPerfQuality.UltraPerformance : NgxPerfQuality.MaxPerf, NgxDlssSettings.ContractFlags);
        Measurement shipped = Measure(device, settings, negate: false);
        Measurement opposite = Measure(device, settings, negate: true);
        output.WriteLine($"{settings}: shipped {shipped}; opposite {opposite}");
        Console.Error.WriteLine($"[dlss-jitter] {settings}: shipped {shipped}; opposite {opposite}");
        GpuTest.AssertCleanSince(device, mark);
        Assert.True(shipped.WobbleX < 0.02 && shipped.WobbleY < 0.02,
            $"Static reconstruction moves in render pixels: {shipped}");
        Assert.True(shipped.Error < opposite.Error * 0.5,
            $"The supplied jitter must reconstruct the known scene more accurately than its opposite: {shipped}; {opposite}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(-0.4)]
    [InlineData(1.75)]
    public void DisplacementEstimatorRecoversKnownSubpixelShift(double shift)
    {
        var signal = new double[Width];
        for (int i = 0; i < Width; i++) signal[i] = Math.Sin(2 * Math.PI * (i + 0.5 - shift) / Period);
        Assert.Equal(shift, Shift(signal), 5);
    }

    private readonly record struct Measurement(double BiasX, double BiasY, double WobbleX, double WobbleY, double Error)
    {
        public override string ToString() =>
            FormattableString.Invariant($"bias=({BiasX:F5},{BiasY:F5}) wobble=({WobbleX:F5},{WobbleY:F5}) render px, RMSE={Error:F5}");
    }

    private Measurement Measure(VulkanDevice device, NgxDlssSettings settings, bool negate)
    {
        int rw = (int)settings.RenderWidth, rh = (int)settings.RenderHeight;
        float scale = (float)rw / Width;
        int phases = Math.Max(1, OptimumTemporalMath.JitterPhaseCount(1f / scale));
        var temporal = new OptimumTemporalFrame();
        var evaluations = new NgxDlssEvaluation[phases];
        var inputs = new int[phases];
        var outputs = new int[phases];
        var resources = new List<int>();
        NgxDlssFeature? feature = null;
        try
        {
            int Track(int texture) { resources.Add(texture); return texture; }
            // Use the same Advance and adapter as the game. Preparing uploads here
            // keeps both uploads and readbacks out of the multi-frame sequence.
            for (int i = 0; i < phases; i++)
            {
                temporal.Advance(16.667f, rw, rh, scale, 0.1f, 1000f, 1f, null);
                temporal.JitterActive = true;
                evaluations[i] = NgxDlssEvaluation.FromTemporalContext(temporal);
                if (negate) evaluations[i] = evaluations[i] with
                {
                    JitterOffsetX = -evaluations[i].JitterOffsetX,
                    JitterOffsetY = -evaluations[i].JitterOffsetY,
                };
                inputs[i] = Track(Color(device, rw, rh, temporal.JitterPx.X, temporal.JitterPx.Y));
                // Match the game's LDR storage output, not a different float path.
                outputs[i] = Track(device.CreateUpscaleTexture(Width, Height, Format.R8G8B8A8Unorm, storage: true));
            }
            var depths = new float[rw * rh];
            Array.Fill(depths, 0.5f);
            int depth = Track(Upload(device, rw, rh, Format.R32Sfloat, MemoryMarshal.AsBytes<float>(depths).ToArray()));
            int motion = Track(Upload(device, rw, rh, Format.R16G16Sfloat, new byte[rw * rh * 4]));
            // Three complete cycles: warm history twice, retain every phase of the third.
            for (int i = 0; i < phases * 3; i++)
            {
                device.BeginFrame();
                if (feature == null)
                {
                    Assert.Equal(NgxResult.Success, device.CreateDlssFeature(settings, out feature));
                    Assert.NotNull(feature);
                }
                int phase = i % phases;
                Assert.Equal(NgxResult.Success, device.EvaluateDlss(feature!, inputs[phase], depth, motion,
                    outputs[phase], evaluations[phase] with { Reset = i == 0 }));
                device.Present();
            }

            var shiftsX = new List<double>();
            var shiftsY = new List<double>();
            double squaredError = 0;
            long samples = 0;
            device.BeginFrame();
            foreach (int texture in outputs)
            {
                byte[] pixels = device.ReadBackLevel0ForTests(texture);
                var columns = new double[Width - 64];
                // Whole periods in both axes, away from the image's boundary.
                int rowsCount = (Height - 64) / (int)Period * (int)Period;
                var rows = new double[rowsCount];
                for (int y = 0; y < rowsCount; y++)
                {
                    for (int x = 0; x < columns.Length; x++)
                    {
                        double value = pixels[((y + 32) * Width + x + 32) * 4] / 255.0;
                        columns[x] += value;
                        rows[y] += value;
                        double error = value - Scene(x + 32.5, y + 32.5);
                        squaredError += error * error;
                        samples++;
                    }
                }
                shiftsX.Add(Shift(columns) * rw / Width);
                shiftsY.Add(Shift(rows) * rh / Height);
            }
            device.Present();
            return new Measurement(shiftsX.Average(), shiftsY.Average(),
                shiftsX.Max() - shiftsX.Min(), shiftsY.Max() - shiftsY.Min(), Math.Sqrt(squaredError / samples));
        }
        finally
        {
            if (feature != null) device.RetireDlssFeature(feature);
            device.DrainDeferredDeletions();
            foreach (int texture in resources) device.DeleteTexture(texture);
            device.DrainDeferredDeletions();
        }
    }

    private static double Scene(double x, double y) =>
        0.5 + 0.22 * Math.Sin(2 * Math.PI * x / Period) + 0.22 * Math.Sin(2 * Math.PI * y / Period);

    private static int Color(VulkanDevice device, int width, int height, float jx, float jy)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // P[8/9] -= 2*j/renderSize moves static raster content by +j.
            // Therefore texel i samples the unjittered scene at i + 0.5 - j.
            byte value = (byte)Math.Clamp(Math.Round(Scene(
                (x + 0.5 - jx) * Width / width, (y + 0.5 - jy) * Height / height) * 255), 0, 255);
            int offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return Upload(device, width, height, Format.R8G8B8A8Unorm, pixels);
    }

    private static unsafe int Upload(VulkanDevice device, int width, int height, Format format, byte[] pixels)
    {
        fixed (byte* data = pixels)
            return device.CreateUpscaleTexture(width, height, format, storage: false, (IntPtr)data, 4);
    }

    private static double Shift(double[] samples)
    {
        int length = samples.Length / (int)Period * (int)Period;
        double sine = 0, cosine = 0, idealSine = 0, idealCosine = 0;
        for (int i = 0; i < length; i++)
        {
            double angle = 2 * Math.PI * i / Period;
            sine += samples[i] * Math.Sin(angle);
            cosine += samples[i] * Math.Cos(angle);
            double ideal = Math.Sin(2 * Math.PI * (i + 0.5) / Period);
            idealSine += ideal * Math.Sin(angle);
            idealCosine += ideal * Math.Cos(angle);
        }
        double phase = Math.Atan2(sine, cosine) - Math.Atan2(idealSine, idealCosine);
        while (phase > Math.PI) phase -= 2 * Math.PI;
        while (phase < -Math.PI) phase += 2 * Math.PI;
        return phase * Period / (2 * Math.PI);
    }
}
