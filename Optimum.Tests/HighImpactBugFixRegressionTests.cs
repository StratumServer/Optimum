using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cairo;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;
using Xunit;
#if !NO_DONOR
using Vintagestory.Client.NoObf;
using Vintagestory.Server;
#endif

namespace Optimum.Tests;

[Collection("GUI metrics")]
public sealed class HighImpactBugFixRegressionTests : IDisposable
{
    private readonly bool originalPrepReuse = RecordedPreparationCache.Enabled;
    private readonly bool originalGuiRenderEnabled = OptimumConfig.GuiGpuRenderEnabled;

    public HighImpactBugFixRegressionTests()
    {
        _ = CairoAPI.Version;
        RecordedPreparationCache.Enabled = true;
        RecordedPreparationCache.Clear();
        RecordedPreparationCache.ResetCounters();
    }

    public void Dispose()
    {
        RecordedPreparationCache.Clear();
        RecordedPreparationCache.ResetCounters();
        RecordedPreparationCache.Enabled = originalPrepReuse;
        OptimumConfig.SetGuiGpuCertificationPassed(false, null, null, null, 0, 0, 0, 0);
        OptimumConfig.SetGuiGpuBackendProbePassed(false);
        OptimumConfig.GuiGpuRenderEnabled = originalGuiRenderEnabled;
    }

#if !NO_DONOR
    [Fact]
    public void OptimumGuiSvgRecorder_TryRecord_ReturnsFalseInsteadOfThrowingOnInvalidOrUnsupportedInputs()
    {
        OptimumConfig.GuiGpuRenderEnabled = true;

        const string validSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><rect width='16' height='16' fill='#3585cd'/></svg>";
        const string gradientSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><defs><linearGradient id='g'><stop offset='0' stop-color='#3585cd'/><stop offset='1' stop-color='#d7396b'/></linearGradient></defs><rect width='16' height='16' fill='url(#g)'/></svg>";

        IAsset validAsset = new StubAsset(validSvg);
        IAsset gradientAsset = new StubAsset(gradientSvg);
        IAsset emptyDataAsset = new StubAsset(null);

        using var unrecordedSurface = new ImageSurface(Format.Argb32, 32, 32);
        using var recordingSurface = new ImageSurface(Format.Argb32, 32, 32);
        recordingSurface.BeginRecording();

        // Null target, unrecorded surface, default 0x0 size, negative origin, out-of-bounds, and null asset data must return false without throwing.
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, validAsset, null, 0, 0, 16, 16, null));
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, validAsset, unrecordedSurface, 0, 0, 16, 16, null));
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, validAsset, recordingSurface, 0, 0, 0, 0, null));
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, validAsset, recordingSurface, -1, 0, 16, 16, null));
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, validAsset, recordingSurface, 20, 0, 16, 16, null));
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, emptyDataAsset, recordingSurface, 0, 0, 16, 16, null));

        // Unsupported gradient SVG must return false without throwing so SvgLoader.DrawSvg falls back to rasterizeSvg.
        Assert.False(OptimumGuiSvgRecorder.TryRecord(null, gradientAsset, recordingSurface, 0, 0, 16, 16, null));
        Assert.Equal(SurfaceRecordingState.Recording, recordingSurface.RecordingState);

        // Supported solid SVG records and reuses cached geometry across calls.
        RecordedPreparationCache.ResetCounters();
        Assert.True(OptimumGuiSvgRecorder.TryRecord(null, validAsset, recordingSurface, 0, 0, 16, 16, null));
        Assert.True(OptimumGuiSvgRecorder.TryRecord(null, validAsset, recordingSurface, 8, 8, 16, 16, null));
        Assert.Equal(1, RecordedPreparationCache.PictureHits);
    }

    [Fact]
    public void OptimumGuiSvgRecorder_TryLoad_ReturnsFalseInsteadOfThrowingOnUnsupportedInputs()
    {
        OptimumConfig.GuiGpuRenderEnabled = true;

        const string gradientSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><defs><linearGradient id='g'><stop offset='0' stop-color='#3585cd'/><stop offset='1' stop-color='#d7396b'/></linearGradient></defs><rect width='16' height='16' fill='url(#g)'/></svg>";
        IAsset gradientAsset = new StubAsset(gradientSvg);

        Assert.False(OptimumGuiSvgRecorder.TryLoad(null, null, gradientAsset, 16, 16, 16, 16, null, out LoadedTexture texture));
        Assert.Null(texture);
        Assert.False(OptimumGuiSvgRecorder.TryLoad(null, null, gradientAsset, 0, 16, 16, 16, null, out texture));
        Assert.Null(texture);
    }

    [Fact]
    public void OptimumGuiGpuProbeBackend_ReusesCachedRecordedGpuBlurAcrossUploads()
    {
        FieldInfo cachedBlurField = typeof(OptimumGuiGpuProbeBackend).GetField("cachedBlur", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(cachedBlurField);

        using var module = ModuleDefinition.ReadModule(typeof(OptimumGuiGpuProbeBackend).Assembly.Location);
        var type = module.GetType("Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend");
        var method = Assert.Single(type.Methods, m => m.Name == "TryCreateCandidateTexture");

        Assert.Contains(method.Body.Instructions, i =>
            i.Operand is MethodReference m &&
            m.DeclaringType.FullName == "Cairo.RecordedGpuBlur" &&
            m.Name == "ResetPerUploadState");
    }

    [Fact]
    public void ServerSystemBlockSimulation_DoesNotPoolEnqueuedBlockPosInstances()
    {
        FieldInfo[] fields = typeof(ServerSystemBlockSimulation).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f => f.Name.Contains("PosPool", StringComparison.Ordinal));

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.DoesNotContain("optimumPosPool", programSource);
        Assert.DoesNotContain("OptimumGetPooledPos", programSource);
        Assert.DoesNotContain("OptimumGetPooledFluidPos", programSource);

        var fluidPos = new FluidBlockPos(10, 64, -20, 2);
        BlockPos copy = fluidPos.Copy();
        Assert.NotSame(fluidPos, copy);
        Assert.IsType<FluidBlockPos>(copy);
        Assert.Equal(10, copy.X);
        Assert.Equal(64, copy.Y);
        Assert.Equal(-20, copy.Z);
        Assert.Equal(2, copy.dimension);
    }
#endif

    [Fact]
    public void Block_GetDrops_RestoresThreadStaticDepthAndClearsPoolsWhenBehaviorThrows()
    {
        FieldInfo depthField = typeof(Block).GetField("_optimumGetDropsDepth", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo behaviorDropsField = typeof(Block).GetField("_optimumBehaviorDrops", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(depthField);
        Assert.NotNull(behaviorDropsField);

        var throwingBlock = new Block
        {
            BlockBehaviors = new BlockBehavior[]
            {
                new DelegatingDropsBehavior(() => new[] { new ItemStack() }),
                new DelegatingDropsBehavior(() => throw new InvalidOperationException("Simulated behavior exception")),
            },
        };

        Assert.Throws<InvalidOperationException>(() => throwingBlock.GetDrops(null, new BlockPos(0, 64, 0), null));
        Assert.Equal(0, (int)depthField.GetValue(null)!);

        var behaviorDrops = (List<ItemStack>)behaviorDropsField.GetValue(null);
        Assert.NotNull(behaviorDrops);
        Assert.Empty(behaviorDrops);

        var normalBlock = new Block
        {
            BlockBehaviors = new BlockBehavior[]
            {
                new DelegatingDropsBehavior(() => new[] { new ItemStack() }, EnumHandling.PreventDefault),
            },
        };

        ItemStack[] drops = normalBlock.GetDrops(null, new BlockPos(0, 64, 0), null);
        Assert.Single(drops);
        Assert.Equal(0, (int)depthField.GetValue(null)!);
        Assert.Empty(behaviorDrops);
    }

    [Fact]
    public void OptimumApiBridge_And_InventoryBase_RoundTripDirtySignal()
    {
        _ = OptimumApiBridge.ConsumeAnySlotDirty();
        Assert.False(OptimumApiBridge.ConsumeAnySlotDirty());

        OptimumApiBridge.MarkInventoryDirty();
        Assert.True(OptimumApiBridge.ConsumeAnySlotDirty());
        Assert.False(OptimumApiBridge.ConsumeAnySlotDirty());

        var inventory = new TestInventory();
        inventory.MarkSlotDirty(0);
        Assert.True(OptimumApiBridge.ConsumeAnySlotDirty());
        Assert.False(OptimumApiBridge.ConsumeAnySlotDirty());

        inventory.DiscardAll();
        Assert.True(OptimumApiBridge.ConsumeAnySlotDirty());
        Assert.False(OptimumApiBridge.ConsumeAnySlotDirty());

        string serverInventoryPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Server/ServerSystemInventory.cs.patch"));
        Assert.Contains("hasSkippedDirtyClient", serverInventoryPatch);
        Assert.Contains("OptimumApiBridge.MarkInventoryDirty();", serverInventoryPatch);
    }

    [Fact]
    public void AStar_ScratchNodeEliminatesRingBufferPathNodeCorruption()
    {
        FieldInfo[] fields = typeof(AStar).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Contains(fields, f => f.Name == "scratchNode" && f.FieldType == typeof(PathNode));
        Assert.DoesNotContain(fields, f => f.Name == "optimumNodePool");

        string modPatcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"scratchNode\"", modPatcher);
        Assert.DoesNotContain("\"optimumNodePool\"", modPatcher);
        Assert.DoesNotContain("\"OptimumRentNode\"", modPatcher);

        var pathfinder = new AStar(CreateStubServerApi());
        var box = new Cuboidf(-0.3f, 0f, -0.3f, 0.3f, 1.5f, 0.3f);
        List<PathNode> firstPath = pathfinder.FindPath(new BlockPos(0, 64, 0), new BlockPos(12, 64, 12), 4, 1f, box);
        Assert.NotNull(firstPath);
        Assert.NotEmpty(firstPath);

        var expectedCoords = firstPath.Select(n => (n.X, n.Y, n.Z, n.Parent)).ToArray();

        // Execute enough subsequent searches to exceed the old 4096-node ring buffer capacity.
        for (int i = 0; i < 80; i++)
        {
            List<PathNode> other = pathfinder.FindPath(new BlockPos(100 + i, 64, 100), new BlockPos(110 + i, 64, 110), 4, 1f, box);
            Assert.NotNull(other);
        }

        for (int i = 0; i < firstPath.Count; i++)
        {
            Assert.Equal(expectedCoords[i].X, firstPath[i].X);
            Assert.Equal(expectedCoords[i].Y, firstPath[i].Y);
            Assert.Equal(expectedCoords[i].Z, firstPath[i].Z);
            Assert.Same(expectedCoords[i].Parent, firstPath[i].Parent);
        }
    }

#if !NO_DONOR
    [Fact]
    public void ChunkTesselatorManager_SortsNewlyEnqueuedChunksWhenPlayerStandsStill()
    {
        FieldInfo queueNeedsSortField = typeof(ChunkTesselatorManager).GetField("queueNeedsSort", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(queueNeedsSortField);
        Assert.Equal(typeof(bool), queueNeedsSortField.FieldType);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"queueNeedsSort\"", programSource);

        string patchSource = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");
        Assert.Contains("if (moved || (plr != null && queueNeedsSort))", patchSource);
        Assert.Contains("queueNeedsSort = false;", patchSource);
        Assert.Contains("queueNeedsSort = true;", patchSource);
    }

    [Fact]
    public void SystemRenderEntities_ScalesShadowCullThresholdAndFrustumRadiusByEntitySphereRadius()
    {
        string patchSource = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch");
        Assert.Contains("double shadowRadius = Math.Max(3.0, (double)entity.FrustumSphereRadius);", patchSource);
        Assert.Contains("double radiusScale = shadowRadius / 3.0;", patchSource);
        Assert.Contains("* (radiusScale * radiusScale)", patchSource);
        Assert.Contains("SphereInFrustum((float)entity.Pos.X, (float)entity.Pos.InternalY, (float)entity.Pos.Z, shadowRadius)", patchSource);
    }

    [Fact]
    public void SystemSoundEngine_TracksVerticalViewComponentAndUsesDeltaTimeRefreshInterval()
    {
        FieldInfo dirYField = typeof(SystemSoundEngine).GetField("_lastListenerDirY", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo timeField = typeof(SystemSoundEngine).GetField("_listenerTimeSinceUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dirYField);
        Assert.NotNull(timeField);
        Assert.Equal(typeof(float), dirYField.FieldType);
        Assert.Equal(typeof(float), timeField.FieldType);

        string patchSource = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs.patch");
        Assert.Contains("float ddy = viewVector.Y - _lastListenerDirY;", patchSource);
        Assert.Contains("ddx * ddx + ddy * ddy + ddz * ddz >= ListenerDirThresholdSq", patchSource);
        Assert.Contains("_listenerTimeSinceUpdate += deltaTime;", patchSource);
        Assert.Contains("_listenerTimeSinceUpdate >= ListenerRefreshInterval", patchSource);
    }
#endif

    [Fact]
    public void EntityShapeRenderer_CachesReflectionPerTypeAndBypassesBudgetForLocalPlayer()
    {
        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityShapeRenderer.cs.patch"));
        Assert.Contains("private static ConcurrentDictionary<Type, bool> optimumShaderCompatibleByType;", runtimePatch);
        Assert.Contains("private static bool IsOptimumShaderStateCompatible(Type rendererType)", runtimePatch);
        Assert.Contains("optimumShaderStateCompatible = IsOptimumShaderStateCompatible(GetType());", runtimePatch);
        Assert.Contains("entity.EntityId == capi.World.Player?.Entity?.EntityId || OptimumDiagnostics.TryConsumeEntityTesselationBudget()", runtimePatch);

        string modPatcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"optimumShaderCompatibleByType\"", modPatcher);
        Assert.Contains("\"IsOptimumShaderStateCompatible\"", modPatcher);
    }

    [Fact]
    public void WeatherSystemClient_UsesDeltaTimeAccumulatorForWindThrottle()
    {
        string sourcePatch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemClient.cs.patch");
        Assert.Contains("private float optimumWindAccum;", sourcePatch);
        Assert.Contains("private const float WindUpdateInterval = 1f / 15f;", sourcePatch);
        Assert.Contains("optimumWindAccum += dt;", sourcePatch);
        Assert.Contains("optimumWindAccum = 0f;", sourcePatch);

        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSystemClient.cs.patch"));
        Assert.Contains("private float optimumWindAccum;", runtimePatch);
        Assert.Contains("optimumWindAccum += dt;", runtimePatch);
        Assert.Contains("optimumWindAccum >= 1f / 15f", runtimePatch);
        Assert.Contains("optimumWindAccum = 0f;", runtimePatch);

        string modPatcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"optimumWindAccum\"", modPatcher);
        Assert.DoesNotContain("\"optimumWindFrameCounter\"", modPatcher);
    }

    [Fact]
    public void BlockEntityParticleEmitter_ScalesDistanceCutoffByParticleSizeUpTo128Blocks()
    {
        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/BlockEntityParticleEmitter.cs.patch"));
        Assert.Contains("if (!plrPos.InRangeOf(Pos, 16384f))", runtimePatch);
        Assert.Contains("advancedParticleProperties.Size.avg + Math.Abs(advancedParticleProperties.Size.var)", runtimePatch);
        Assert.Contains("float maxDist = Math.Min(128f, 48f * maxParticleSize);", runtimePatch);
        Assert.Contains("if (!plrPos.InRangeOf(Pos, maxDist * maxDist))", runtimePatch);
    }

#if !NO_DONOR
    [Fact]
    public void OptimumGuiGpuProbeBackend_DrainsPreexistingGlErrorsWithoutAbortingCandidateRender()
    {
        string backendSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryLib/Vintagestory.Client.NoObf/OptimumGuiGpuProbeBackend.cs"));
        Assert.Contains("DrainPreexistingGlErrors();", backendSource);
        Assert.DoesNotContain("string preexistingErrors = DrainPreexistingGlErrors();\n\t\t\t\t\tif (preexistingErrors != null)", backendSource);
        Assert.DoesNotContain("failureReason = \"preexisting-gl-errors:\"", backendSource);
    }
#endif

    [Fact]
    public void CollisionTester_PreservesVerticalCollisionOnStationaryRestingEntitiesWithSolidGround()
    {
        string patchSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Math/CollisionTester.cs.patch"));
        Assert.Contains("if (entity.CollidedVertically)", patchSource);
        Assert.Contains("Block blockBelow = blockAccessor.GetBlock(tmpPos);", patchSource);
        Assert.Contains("if (blockBelow != null && blockBelow.SideSolid[BlockFacing.UP.Index])", patchSource);
        Assert.Contains("entity.CollidedVertically = true;", patchSource);
        Assert.DoesNotContain("entity.CollidedVertically = false;", patchSource);
    }

    [Fact]
    public void EntityBehaviorRepulseAgents_PrecalculatesRepulsionRangeInUpdateColSelBoxes()
    {
        string modPatcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"isWithinRepulseRange\"", modPatcher);
        Assert.Contains("new(\"Vintagestory.GameContent.EntityBehaviorRepulseAgents\", \"UpdateColSelBoxes\", 0)", modPatcher);

        string sourcePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Behavior/BehaviorRepulseAgents.cs.patch"));
        Assert.Contains("isWithinRepulseRange = (dx * dx + dz * dz <= maxRepulseDistSq);", sourcePatch);
        Assert.Contains("if (!isWithinRepulseRange && entity != cworld.Player?.Entity)", sourcePatch);
        Assert.Contains("entity.World?.Side == EnumAppSide.Client", sourcePatch);

        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityBehaviorRepulseAgents.cs.patch"));
        Assert.Contains("private bool isWithinRepulseRange;", runtimePatch);
        Assert.Contains("isWithinRepulseRange = (dx * dx + dz * dz <= maxRepulseDistSq);", runtimePatch);
        Assert.Contains("if (!isWithinRepulseRange)", runtimePatch);
        Assert.Contains("entity.World?.Side == EnumAppSide.Client", runtimePatch);
    }

    [Fact]
    public void EntityBehaviorCollectEntities_UsesSpatialPartitioningWithoutWaitTicksBackoff()
    {
        string modPatcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"partitionUtil\"", modPatcher);

        string sourcePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Behavior/BehaviorCollectEntities.cs.patch"));
        Assert.Contains("partitionUtil.WalkEntities(tmp, 1.5", sourcePatch);
        Assert.Contains("EnumEntitySearchType.Inanimate", sourcePatch);
        Assert.DoesNotContain("waitTicks = 2;", sourcePatch);

        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityBehaviorCollectEntities.cs.patch"));
        Assert.Contains("partitionUtil.WalkEntities(tmp, 1.5", runtimePatch);
        Assert.Contains("EnumEntitySearchType.Inanimate", runtimePatch);
        Assert.DoesNotContain("waitTicks = 2;", runtimePatch);
    }

    [Fact]
    public void ICoreClientAPI_ExposesFirstClassIsTesselationThreadProperty()
    {
        MethodInfo method = typeof(ICoreClientAPI).GetMethod("IsTesselationThread", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(int), parameters[0].ParameterType);

        string cookingPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSSurvivalMod/Systems/Cooking/MealMeshCache.cs.patch"));
        Assert.Contains("capi.IsTesselationThread(Environment.CurrentManagedThreadId)", cookingPatch);
    }

    [Fact]
    public void OptimumOutfitCache_InvalidatesOnWorldLeaveAndIncludesOverlaysInKey()
    {
        string prewarmerSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs"));
        Assert.Contains("api.Event.LeaveWorld += ClearCaches;", prewarmerSource);
        Assert.Contains("public override void Dispose()", prewarmerSource);
        Assert.Contains("OptimumOutfitShapeCache.Clear();", prewarmerSource);
        Assert.Contains("OptimumOutfitAnimatorCache.Clear();", prewarmerSource);

        string animatorSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs"));
        Assert.Contains("public static string BuildKey(string outfitKey, CompositeShape[] overlays, string[] willDisableElements)", animatorSource);

        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch"));
        Assert.Contains("optimumAnimatorCacheKey = OptimumOutfitAnimatorCache.BuildKey(optimumAnimatorCacheKey, cshape?.Overlays, willDisableElements);", runtimePatch);
    }

    [Fact]
    public void WeatherSimulationParticles_ResetsHeightmapOnBlockChangeAndLeaveWorld()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationParticles.cs.patch"));
        Assert.Contains("capi.Event.BlockChanged += OnBlockChanged;", patch);
        Assert.Contains("capi.Event.LeaveWorld += ResetHeightMapCache;", patch);
        Assert.Contains("optimumLastHeightmapCenterX = int.MinValue;", patch);
        Assert.Contains("Array.Clear(lowResRainHeightMap, 0, lowResRainHeightMap.Length);", patch);
    }

    [Fact]
    public void AnimationUtil_DoesNotFreezeAt48mAndAccumulatesDeltaTimeAt15Hz()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Model/Animation/AnimationUtil.cs.patch"));
        Assert.Contains("if (distSq > AnimLodNearSq)", patch);
        Assert.Contains("_animDeferredDt += deltaTime;", patch);
        Assert.Contains("if (_animDeferredDt < 1f / 15f)", patch);
        Assert.DoesNotContain("if (distSq > AnimLodFarSq)", patch);
    }

    [Fact]
    public void EntityItemRenderer_ScalesCullDistanceWithViewportResolution()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityItemRenderer.cs.patch"));
        Assert.Contains("Math.Max(64.0, 64.0 * ((double)capi.Render.FrameHeight / 1080.0))", patch);
        Assert.Contains("dx * dx + dz * dz > maxDist * maxDist", patch);
    }

    [Fact]
    public void GlStateSnapshot_EliminatesPerCaptureAllocationsViaThreadStaticReuse()
    {
        string source = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryLib/Vintagestory.Client.NoObf/OptimumGuiGpuProbeBackend.cs"));
        Assert.Contains("[ThreadStatic]", source);
        Assert.Contains("private static GlStateSnapshot pooledInstance;", source);
        Assert.Contains("GlStateSnapshot snapshot = pooledInstance ??= new GlStateSnapshot();", source);
        Assert.Contains("snapshot.CaptureState();", source);
    }

    [Fact]
    public void SurfaceRecorder_SharesClipSnapshotAcrossConsecutiveDrawCommands()
    {
        string source = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/Cairo/wrapper/SurfaceRecorder.cs"));
        Assert.Contains("internal RecordedGuiClip[] CachedClipSnapshot;", source);
        Assert.Contains("RecordedGuiClip[] GetClipSnapshot()", source);
        Assert.Contains("currentState.CachedClipSnapshot = (clips.Count == 0 ? Array.Empty<RecordedGuiClip>() : clips.ToArray());", source);
        Assert.Contains("RecordedGuiDrawing.Capture(graphicsState, width, height, stroke, paintAll, GetClipSnapshot()", source);
    }

    [Fact]
    public void SystemRenderPlayerEffects_UsesAllocFreeSpatialChunkWalkAndRestoresVanilla120mRadius()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerEffects.cs.patch");
        Assert.Contains("blockAccessor.GetChunk(cx, cy, cz)", patch);
        Assert.Contains("float lightRadius = 120f;", patch);
        Assert.Contains("Array.Clear(_lightScratch, 0, _lightScratchCount);", patch);
        Assert.DoesNotContain("LightScanRefreshFrames", patch);
        Assert.DoesNotContain("_lightScanFramesSinceRefresh", patch);
    }

    [Fact]
    public void RecordedCairoGradient_UsesNativeSkiaGradientShadersWithoutCpuBitmapOrRuntimeEffect()
    {
        string source = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/Cairo/wrapper/RecordedCairoGradient.cs"));
        Assert.Contains("SKShader.CreateLinearGradient(", source);
        Assert.Contains("SKShader.CreateTwoPointConicalGradient(", source);
        Assert.Contains("SKShader.CreateRadialGradient(", source);
        Assert.DoesNotContain("new SKBitmap(256", source);
        Assert.DoesNotContain("SKRuntimeEffect.CreateShader", source);
        Assert.DoesNotContain("bitmap.SetPixel", source);
    }

    [Fact]
    public void RecordedCairoIntegerBlend_UsesHardwareBlendModes()
    {
        string source = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/Cairo/wrapper/RecordedCairoIntegerBlend.cs"));
        Assert.Contains("For(Operator op) => null;", source);
        Assert.DoesNotContain("SKRuntimeEffect.CreateBlender", source);
    }

    [Fact]
    public void RecordedGuiDrawing_EliminatesCpuScanlineRasterizerAndGeometryCoverage()
    {
        string source = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/Cairo/wrapper/RecordedGuiDrawing.cs"));
        Assert.DoesNotContain("RecordedGeometryCoverage", source);
        Assert.DoesNotContain("RecordedCoarseGeometry", source);
        Assert.DoesNotContain("geometryCoverage", source);
        Assert.DoesNotContain("geometrySourceEffect", source);
        Assert.DoesNotContain("DrawGeometrySourceOrClear", source);
    }

    private sealed class DelegatingDropsBehavior : BlockBehavior
    {
        private readonly System.Func<ItemStack[]> provider;
        private readonly EnumHandling resultHandling;

        public DelegatingDropsBehavior(System.Func<ItemStack[]> provider, EnumHandling resultHandling = EnumHandling.PassThrough)
            : base(null)
        {
            this.provider = provider;
            this.resultHandling = resultHandling;
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropChanceMultiplier, ref EnumHandling handling)
        {
            handling = resultHandling;
            return provider();
        }
    }

    private sealed class TestInventory : InventoryBase
    {
        private readonly ItemSlot[] slots = { new ItemSlot(null) };

        public TestInventory() : base("test-1", null)
        {
            slots[0] = new ItemSlot(this);
        }

        public override int Count => slots.Length;

        public override ItemSlot this[int slotId]
        {
            get => slots[slotId];
            set => slots[slotId] = value;
        }

        public override void FromTreeAttributes(Vintagestory.API.Datastructures.ITreeAttribute tree) { }

        public override void ToTreeAttributes(Vintagestory.API.Datastructures.ITreeAttribute tree) { }
    }

    private static Vintagestory.API.Server.ICoreServerAPI CreateStubServerApi()
    {
        var airBlock = new Block
        {
            BlockId = 0,
            Code = new AssetLocation("game", "air"),
            CanStep = true,
            BlockMaterial = EnumBlockMaterial.Air,
            CollisionBoxes = Array.Empty<Cuboidf>(),
        };
        var solidGroundBlock = new Block
        {
            BlockId = 1,
            Code = new AssetLocation("game", "soil"),
            CanStep = true,
            BlockMaterial = EnumBlockMaterial.Soil,
            CollisionBoxes = new[] { new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f) },
        };

        var blockAccessor = StubProxy<ICachingBlockAccessor>.Create((method, args) =>
        {
            if (method.Name == "GetBlock" && args?.Length >= 1 && args[0] is BlockPos pos)
            {
                int layer = args.Length >= 2 && args[1] is int l ? l : BlockLayersAccess.Default;
                if (layer == BlockLayersAccess.Fluid)
                {
                    return airBlock;
                }
                return pos.Y < 64 ? solidGroundBlock : airBlock;
            }
            return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        });

        var world = StubProxy<Vintagestory.API.Server.IServerWorldAccessor>.Create((method, _) =>
        {
            if (method.Name == "GetCachingBlockAccessor")
            {
                return blockAccessor;
            }
            if (method.Name == "get_Rand")
            {
                return new Random(42);
            }
            return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        });

        return StubProxy<Vintagestory.API.Server.ICoreServerAPI>.Create((method, _) =>
        {
            if (method.Name == "get_World")
            {
                return world;
            }
            return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        });
    }

    public class StubProxy<T> : DispatchProxy where T : class
    {
        private System.Func<MethodInfo, object[], object> handler;

        public static T Create(System.Func<MethodInfo, object[], object> handler)
        {
            object proxy = Create<T, StubProxy<T>>();
            ((StubProxy<T>)proxy).handler = handler;
            return (T)proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return handler != null ? handler(targetMethod, args) : null;
        }
    }

    private sealed class StubAsset : IAsset
    {
        private readonly string text;

        public StubAsset(string text)
        {
            this.text = text;
            Data = text == null ? null : Encoding.UTF8.GetBytes(text);
            Location = new AssetLocation("game", "textures/icons/test.svg");
        }

        public string Name => "test.svg";
        public AssetLocation Location { get; }
        public IAssetOrigin Origin { get; set; }
        public byte[] Data { get; set; }
        public bool IsPatched { get; set; }
        public bool IsLoaded() => Data != null;
        public BitmapRef ToBitmap(ICoreClientAPI api) => null;
        public T ToObject<T>(Newtonsoft.Json.JsonSerializerSettings settings = null) => default;
        public string ToText() => text;
    }
}
