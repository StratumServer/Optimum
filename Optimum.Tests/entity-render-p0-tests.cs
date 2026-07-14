using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public class EntityRenderP0Tests
{
    [Fact]
    public void PackedLightConversionMatchesWorldMapFormulaForEveryValue()
    {
        byte[] hueLevels = new byte[64];
        byte[] saturationLevels = new byte[8];
        float[] blockLightLevels = new float[32];
        float[] sunlightLevels = new float[32];
        for (int i = 0; i < hueLevels.Length; i++) hueLevels[i] = (byte)(i * 251 / 63);
        for (int i = 0; i < saturationLevels.Length; i++) saturationLevels[i] = (byte)(i * 253 / 7);
        for (int i = 0; i < blockLightLevels.Length; i++) blockLightLevels[i] = i * i / (31f * 31f);
        for (int i = 0; i < sunlightLevels.Length; i++) sunlightLevels[i] = i / 31f;

        for (int saturation = 0; saturation < 8; saturation++)
        {
            for (int packedValue = 0; packedValue <= ushort.MaxValue; packedValue++)
            {
                ushort packedLight = (ushort)packedValue;
                OptimumEntityLightConverter.FromPackedLight(packedLight, saturation, hueLevels, saturationLevels, blockLightLevels, sunlightLevels, out float red, out float green, out float blue, out float sunlight);

                int packedHsv = (packedLight & 0x1F) | ((packedLight & 0x3E0) << 3) | ((packedLight & 0xFC00) << 6) | (saturation << 24);
                byte hue = hueLevels[(packedHsv >> 16) & 0xFF];
                int sat = saturationLevels[(packedHsv >> 24) & 0xFF];
                int value = (int)(blockLightLevels[(packedHsv >> 8) & 0xFF] * 255f);
                int rgb = ColorUtil.HsvToRgb(hue, sat, value);
                float expectedRed = (float)(rgb >> 16) / 255f;
                float expectedGreen = (float)((rgb >> 8) & 0xFF) / 255f;
                float expectedBlue = (float)(rgb & 0xFF) / 255f;
                float expectedSunlight = sunlightLevels[packedHsv & 0xFF];

                Assert.True(
                    BitConverter.SingleToInt32Bits(expectedRed) == BitConverter.SingleToInt32Bits(red)
                    && BitConverter.SingleToInt32Bits(expectedGreen) == BitConverter.SingleToInt32Bits(green)
                    && BitConverter.SingleToInt32Bits(expectedBlue) == BitConverter.SingleToInt32Bits(blue)
                    && BitConverter.SingleToInt32Bits(expectedSunlight) == BitConverter.SingleToInt32Bits(sunlight),
                    $"packed={packedValue}, saturation={saturation}");
            }
        }
    }

    [Fact]
    public void PreparedBaseLightRequiresMatchingCoordinatesAndConsumesOnce()
    {
        EntityShapeRenderer renderer = CreateUninitializedShapeRenderer();
        IOptimumEntityLightSampler sampler = renderer;
        sampler.SetOptimumLightSample(17, 0, 10, 20, 30, null, 0.1f, 0.2f, 0.3f, 0.4f);
        sampler.ActivateOptimumLightBatch(17);

        Assert.True(TryUsePreparedLight(renderer, 10, 20, 30, needsUpperSample: false));
        AssertLight(renderer, 0.1f, 0.2f, 0.3f, 0.4f);
        Assert.False(TryUsePreparedLight(renderer, 10, 20, 30, needsUpperSample: false));
    }

    [Fact]
    public void PreparedTallLightSelectsUpperOnlyWhenItsSunlightIsGreater()
    {
        EntityShapeRenderer renderer = CreateUninitializedShapeRenderer();
        IOptimumEntityLightSampler sampler = renderer;
        sampler.SetOptimumLightSample(23, 0, 4, 5, 6, null, 0.1f, 0.2f, 0.3f, 0.4f);
        sampler.SetOptimumLightSample(23, 1, 4, 6, 6, null, 0.7f, 0.8f, 0.9f, 0.6f);
        sampler.ActivateOptimumLightBatch(23);

        Assert.True(TryUsePreparedLight(renderer, 4, 5, 6, needsUpperSample: true));
        AssertLight(renderer, 0.7f, 0.8f, 0.9f, 0.6f);

        sampler.SetOptimumLightSample(24, 0, 4, 5, 6, null, 0.1f, 0.2f, 0.3f, 0.6f);
        sampler.SetOptimumLightSample(24, 1, 4, 6, 6, null, 0.7f, 0.8f, 0.9f, 0.6f);
        sampler.ActivateOptimumLightBatch(24);

        Assert.True(TryUsePreparedLight(renderer, 4, 5, 6, needsUpperSample: true));
        AssertLight(renderer, 0.1f, 0.2f, 0.3f, 0.6f);
    }

    [Fact]
    public void CoordinateMismatchInvalidatesTheActivatedBatch()
    {
        EntityShapeRenderer renderer = CreateUninitializedShapeRenderer();
        IOptimumEntityLightSampler sampler = renderer;
        sampler.SetOptimumLightSample(31, 0, 1, 2, 3, null, 0.1f, 0.2f, 0.3f, 0.4f);
        sampler.ActivateOptimumLightBatch(31);

        Assert.False(TryUsePreparedLight(renderer, 2, 2, 3, needsUpperSample: false));
        Assert.False(TryUsePreparedLight(renderer, 1, 2, 3, needsUpperSample: false));
    }

    [Fact]
    public void DisposedSourceChunkRejectsPreparedLight()
    {
        EntityShapeRenderer renderer = CreateUninitializedShapeRenderer();
        IOptimumEntityLightSampler sampler = renderer;
        IWorldChunk chunk = DispatchProxy.Create<IWorldChunk, WorldChunkProxy>();
        ((WorldChunkProxy)(object)chunk).DisposedValue = true;
        sampler.SetOptimumLightSample(37, 0, 1, 2, 3, chunk, 0.1f, 0.2f, 0.3f, 0.4f);
        sampler.ActivateOptimumLightBatch(37);

        Assert.False(TryUsePreparedLight(renderer, 1, 2, 3, needsUpperSample: false));
    }

    [Fact]
    public void ShaderStateCountsMatchingUsesAndClearsItsScope()
    {
        IShaderProgram shader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        IShaderProgram otherShader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        var ubo = new TestUboRef();
        OptimumEntityShaderState.End();

        try
        {
            OptimumEntityShaderState.Begin(shader, ubo);
            Assert.True(OptimumEntityShaderState.TryGetAnimationUbo(shader, out UBORef first));
            Assert.Same(ubo, first);
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(otherShader, out _));
            Assert.True(OptimumEntityShaderState.TryGetAnimationUbo(shader, out UBORef second));
            Assert.Same(ubo, second);
            Assert.Equal(2, OptimumEntityShaderState.End());
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(shader, out _));
        }
        finally
        {
            OptimumEntityShaderState.End();
        }
    }

    [Fact]
    public void ShaderStateRejectsDisposedAnimationBuffer()
    {
        IShaderProgram shader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        var ubo = new TestUboRef();
        ubo.Dispose();
        OptimumEntityShaderState.Begin(shader, ubo);

        try
        {
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(shader, out _));
            Assert.Equal(0, OptimumEntityShaderState.End());
        }
        finally
        {
            OptimumEntityShaderState.End();
        }
    }

    [Fact]
    public void EntityRenderDiagnosticsReportAndResetAggregateCounts()
    {
        OptimumDiagnostics.ResetAllCounters();
        OptimumDiagnostics.RecordEntityLightBatch(samples: 7, preparedSamples: 5, chunkGroups: 3, failedChunkGroups: 1, lockBatches: 2, maxBatchSize: 4, elapsedTicks: Stopwatch.Frequency / 1000);
        OptimumDiagnostics.RecordEntityLightCoordinateMismatch();
        OptimumDiagnostics.RecordEntityLightChunkInvalidation();
        OptimumDiagnostics.RecordEntityShaderSegment(useCount: 4);

        string summary = OptimumDiagnostics.GetCountersSummary();
        Assert.Contains("frames=1, samples=7, prepared=5, chunkGroups=3, failedChunkGroups=1, coordinateMismatches=1", summary);
        Assert.Contains("chunkInvalidations=1, lockBatches=2, maxBatchSize=4, timedFrames=1, sampledBatchMs=1", summary);
        Assert.Contains("segments=1, uses=4, uniformUploadsAvoided=6, uboLookupsAvoided=3", summary);

        OptimumDiagnostics.ResetAllCounters();
        summary = OptimumDiagnostics.GetCountersSummary();
        Assert.Contains("frames=0, samples=0, prepared=0", summary);
        Assert.Contains("chunkInvalidations=0, lockBatches=0, maxBatchSize=0, timedFrames=0, sampledBatchMs=0", summary);
        Assert.Contains("segments=0, uses=0, uniformUploadsAvoided=0, uboLookupsAvoided=0", summary);
    }

    [Fact]
    public void SourceCoveragePinsFallbacksAndClientOwnership()
    {
        string system = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch");
        string clientChunk = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientChunk.cs.patch");
        string renderer = PatchReader.ReadPatch("patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch");
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("OptimumConfig.EntityLightBatchEnabled && !optimumEntityLightBatchDisabled && optimumEntityLightPreviousSampleCount >= OptimumEntityLightMinimumSamples ? PrepareOptimumEntityLights() : 0", system);
        Assert.Contains("OptimumConfig.EntityShaderStateCacheEnabled", system);
        Assert.Contains("!optimumEntityShaderCacheDisabled", system);
        Assert.Contains("OptimumEntityLightBatchSize = 256", system);
        Assert.Contains("OptimumEntityLightMinimumSamples = 4", system);
        Assert.Contains("optimumEntityLightPreviousSampleCount >= OptimumEntityLightMinimumSamples", system);
        Assert.Contains("worldChunk is ClientChunk", system);
        Assert.Contains("finally", system);
        Assert.Contains("batchId = 0;", system);
        Assert.Contains("optimumEntityShaderFailureLogged", system);
        Assert.Contains("lock (packUnpackLock)", clientChunk);
        Assert.Contains("Unpack_ReadOnly();", clientChunk);
        Assert.Contains("if (Disposed)", clientChunk);
        Assert.Contains("TryUseOptimumLightSamples", renderer);
        Assert.Contains("GetLightRGBs(lightX, lightY, lightZ)", renderer);
        Assert.Contains("optimumUpperLightSun > optimumBaseLightSun", renderer);
        Assert.Contains("shaderRenderMethod?.DeclaringType == typeof(EntityShapeRenderer)", renderer);
        Assert.Contains("shaderRenderer.OptimumShaderStateCompatible", system);
        Assert.Contains("!useOptimumShaderState", renderer);
        Assert.Contains("Optimum.EntityLightBatchBuffer", patcher);
        Assert.Contains("OptimumReadLightBatch", patcher);

        string clientContract = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Client/optimum-entity-render-batch.cs"));
        Assert.Contains("namespace Vintagestory.API.Client", clientContract);
        Assert.DoesNotContain("temporalStability", clientContract);

        string serverRoot = Path.Combine(Path.GetDirectoryName(PatchReader.FindRepositoryFile("patches/cecil-owned.list"))!, "VintagestoryLib", "Vintagestory.Server");
        if (Directory.Exists(serverRoot))
        {
            foreach (string path in Directory.GetFiles(serverRoot, "*.patch", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("OptimumReadLightBatch", File.ReadAllText(path));
                Assert.DoesNotContain("OptimumEntityShaderState", File.ReadAllText(path));
            }
        }
    }

    [Fact]
    public void BothEntityRenderControlsDefaultOnAndReachTheSettingsPage()
    {
        Assert.True(OptimumConfig.EntityLightBatchEnabled);
        Assert.True(OptimumConfig.EntityShaderStateCacheEnabled);

        string config = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/OptimumConfig.cs"));
        string gui = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");
        Assert.Contains("public bool EntityLightBatch { get; set; } = true;", config);
        Assert.Contains("public bool EntityShaderStateCache { get; set; } = true;", config);
        Assert.Contains("onOptimumEntityLightBatchChanged", gui);
        Assert.Contains("onOptimumEntityShaderCacheChanged", gui);
    }

    public class ShaderProxy : DispatchProxy
    {
        public ShaderProxy() { }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    public class WorldChunkProxy : DispatchProxy
    {
        public bool DisposedValue { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Disposed")
            {
                return DisposedValue;
            }
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private static EntityShapeRenderer CreateUninitializedShapeRenderer()
    {
        return (EntityShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityShapeRenderer));
    }

    private static bool TryUsePreparedLight(EntityShapeRenderer renderer, int x, int y, int z, bool needsUpperSample)
    {
        MethodInfo method = typeof(EntityShapeRenderer).GetMethod("TryUseOptimumLightSamples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(renderer, [x, y, z, needsUpperSample])!;
    }

    private static void AssertLight(EntityShapeRenderer renderer, float red, float green, float blue, float sunlight)
    {
        FieldInfo field = typeof(EntityShapeRenderer).GetField("lightrgbs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Vec4f light = (Vec4f)field.GetValue(renderer)!;
        Assert.Equal(red, light.R);
        Assert.Equal(green, light.G);
        Assert.Equal(blue, light.B);
        Assert.Equal(sunlight, light.W);
    }

    private sealed class TestUboRef : UBORef
    {
        public override void Bind() { }
        public override void Unbind() { }
        public override void Update<T>(T data) { }
        public override void Update<T>(T data, int offset, int size) { }
        public override void Update(object data, int offset, int size) { }
    }
}
