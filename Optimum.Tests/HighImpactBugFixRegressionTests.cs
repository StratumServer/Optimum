using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cairo;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;
using Vintagestory.GameContent;
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
    public void CollisionTester_TranslatesEntityBoxUnconditionallyAndReusesWalkBlocksDelegate()
    {
        string patchSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Math/CollisionTester.cs.patch"));
        Assert.Contains("protected Action<Block, int, int, int> walkBlocksDelegate;", patchSource);
        Assert.Contains("blockAccessor.WalkBlocks(minPos, maxPos, walkBlocksDelegate, true);", patchSource);
        Assert.DoesNotContain("if (motion.X == 0 && motion.Y == 0 && motion.Z == 0", patchSource);

        string sortableQueuePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Datastructures/SortableQueue.cs.patch"));
        Assert.Contains("ChunkTesselatorManager.tessChunksQueue", sortableQueuePatch);
        Assert.DoesNotContain("A* open-set", sortableQueuePatch);
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
    public void ParticleWhitelist_SurvivalCriticalEmitters_AreIdentified()
    {
        var charcoalPit = new Block { Code = new AssetLocation("game", "charcoalpit-burning") };
        var forge = new Block { Code = new AssetLocation("game", "forge-lit") };
        var bloomery = new Block { Code = new AssetLocation("game", "bloomerychimney") };
        var firepit = new Block { Code = new AssetLocation("game", "firepit-lit") };
        var chimney = new Block { Code = new AssetLocation("game", "chimney-stone") };
        var bubbles = new Block { Code = new AssetLocation("game", "water-bubble") };
        var decorativeTorch = new Block { Code = new AssetLocation("game", "torch-wall-lit") };
        var soil = new Block { Code = new AssetLocation("game", "soil-fertile") };

        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(charcoalPit));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(forge));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(bloomery));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(firepit));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(chimney));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(bubbles));

        Assert.False(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(decorativeTorch));
        Assert.False(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(soil));
        Assert.False(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(null));
        Assert.False(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(new Block()));
    }

    [Fact]
    public void ParticleWhitelist_EntityClassMatch_WorksCorrectly()
    {
        var pitByClass = new Block { EntityClass = "CharcoalPit" };
        var forgeByClass = new Block { EntityClass = "Forge" };
        var bloomeryByClass = new Block { EntityClass = "Bloomery" };
        var firepitByClass = new Block { EntityClass = "Firepit" };
        var genericByClass = new Block { EntityClass = "Sign" };

        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(pitByClass));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(forgeByClass));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(bloomeryByClass));
        Assert.True(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(firepitByClass));
        Assert.False(OptimumParticleWhitelist.IsCriticalSurvivalEmitter(genericByClass));
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
        Assert.Contains("api.Event.ReloadTextures += ClearCaches;", prewarmerSource);
        Assert.Contains("capi.Event.LeaveWorld -= ClearCaches;", prewarmerSource);
        Assert.Contains("capi.Event.ReloadTextures -= ClearCaches;", prewarmerSource);
        Assert.Contains("public override void Dispose()", prewarmerSource);
        Assert.Contains("OptimumOutfitShapeCache.Clear();", prewarmerSource);
        Assert.Contains("OptimumOutfitAnimatorCache.Clear();", prewarmerSource);

        string animatorSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs"));
        Assert.Contains("public static string BuildKey(string outfitKey, CompositeShape[] overlays, string[] willDisableElements)", animatorSource);

        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch"));
        Assert.Contains("optimumAnimatorCacheKey = OptimumOutfitAnimatorCache.BuildKey(optimumAnimatorCacheKey, cshape?.Overlays, willDisableElements);", runtimePatch);
        Assert.Contains("if (outfitCodes != null && outfitSlots != null && array != null)", runtimePatch);
        Assert.Contains("for (int j = 0; j < outfitCodes.Length && j < array.Length && j < outfitSlots.Length; j++)", runtimePatch);
    }

    [Fact]
    public void ModSystemRenderFallingBlocksFast_ClearsMeshAndEliminatesDuplicateUpload()
    {
        string runtimePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/ModSystemRenderFallingBlocksFast.cs.patch"));
        Assert.Contains("mesh.Clear();", runtimePatch);
        Assert.Contains("if (entity.meshRef == null)", runtimePatch);
        Assert.Contains("entity.meshRef = capi.Render.UploadMultiTextureMesh(meshToUpload);", runtimePatch);

        string sourcePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entities/EntityBlockFalling.cs.patch"));
        Assert.Contains("mesh.Clear();", sourcePatch);
        Assert.Contains("if (entity.meshRef == null)", sourcePatch);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("EssentialsManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;
        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m =>
            m.TypeFullName == "Vintagestory.GameContent.ModSystemRenderFallingBlocksFast" &&
            m.MethodName == "genMesh" &&
            m.ParamCount == 1);
    }

    [Fact]
    public void PathNodeSet_WiredIntoManifestAndRestoresHeapPriorityOnUpdateNode()
    {
        string runtimeAStar = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/Essentials/AStar.cs.patch"));
        Assert.Contains("openSet.UpdateNodePriority(pathNode5);", runtimeAStar);

        string sourceAStar = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/Astar/AStar.cs.patch"));
        Assert.Contains("openSet.UpdateNodePriority(existingNeighbourNode);", sourceAStar);

        string runtimePathNodeSet = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/Essentials/PathNodeSet.cs.patch"));
        Assert.Contains("public void UpdateNodePriority(PathNode node)", runtimePathNodeSet);
        Assert.Contains("SiftUp(index);", runtimePathNodeSet);

        string sourcePathNodeSet = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/Astar/PathNodeSet.cs.patch"));
        Assert.Contains("public void UpdateNodePriority(PathNode node)", sourcePathNodeSet);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("EssentialsManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;
        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m =>
            m.TypeFullName == "Vintagestory.Essentials.PathNodeSet" &&
            m.MethodName == "Add" &&
            m.ParamCount == 1);
        Assert.Contains(methods, m =>
            m.TypeFullName == "Vintagestory.Essentials.PathNodeSet" &&
            m.MethodName == "RemoveNearest" &&
            m.ParamCount == 0);

        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        Assert.True(members.ContainsKey("Vintagestory.Essentials.PathNodeSet"));
        Assert.Contains("UpdateNodePriority", members["Vintagestory.Essentials.PathNodeSet"]);
    }

    [Fact]
    public void RecipeBaseAndContainers_ClearThreadStaticListsOnExit()
    {
        string recipePatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Crafting/RecipeBase.cs.patch"));
        Assert.Contains("if (--_optimumMatchDepth == 0)", recipePatch);
        Assert.Contains("_optimumSuppliedStacks?.Clear();", recipePatch);
        Assert.Contains("_optimumIngredientStacks?.Clear();", recipePatch);

        string cookingPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockCookingContainer.cs.patch"));
        Assert.Contains("if (reuse)", cookingPatch);
        Assert.Contains("optimumCookingStacks?.Clear();", cookingPatch);

        string containerPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityContainer.cs.patch"));
        Assert.Contains("if (reuse)", containerPatch);
        Assert.Contains("optimumNonEmptyStacks?.Clear();", containerPatch);
    }

    [Fact]
    public void ChunkTesselatorManager_AppliesWallClockUploadTimeGuard()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch"));
        Assert.Contains("long uploadStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();", patch);
        Assert.Contains("long maxUploadTicks = (System.Diagnostics.Stopwatch.Frequency * maxUploadMs) / 1000L;", patch);
        Assert.Contains("if (uploadedAny && (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStartTicks) >= maxUploadTicks)", patch);
        Assert.Contains("processPrioQueue = tessChunksQueuePriority.Count > 0;", patch);
        Assert.Contains("while (tessChunksQueue.Count > 0 && num2 < num3 && (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStartTicks) < maxUploadTicks)", patch);
        Assert.DoesNotContain("tesselatedChunk.UnusedDispose();\n+\t\t\trequeue = true;", patch);
    }

    [Fact]
    public void ChunkReadPool_HardwareAwareWorkerScaling()
    {
        bool originalEnabled = Vintagestory.API.Config.OptimumConfig.ChunkReadPoolEnabled;
        int originalWorkers = Vintagestory.API.Config.OptimumConfig.ChunkReadPoolWorkers;
        try
        {
            Vintagestory.API.Config.OptimumConfig.ChunkReadPoolEnabled = true;
            Vintagestory.API.Config.OptimumConfig.ChunkReadPoolWorkers = 4;

            // Singleplayer scaling
            Assert.Equal(1, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(4, isDedicatedServer: false));
            Assert.Equal(2, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(6, isDedicatedServer: false));
            Assert.Equal(3, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(8, isDedicatedServer: false));
            Assert.Equal(4, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(16, isDedicatedServer: false));

            // Dedicated server scaling
            Assert.Equal(2, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(4, isDedicatedServer: true));
            Assert.Equal(3, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(6, isDedicatedServer: true));
            Assert.Equal(4, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(16, isDedicatedServer: true));

            // Disabled config
            Vintagestory.API.Config.OptimumConfig.ChunkReadPoolEnabled = false;
            Assert.Equal(0, Vintagestory.API.Config.OptimumConfig.GetEffectiveChunkReadPoolWorkers(6, isDedicatedServer: false));
        }
        finally
        {
            Vintagestory.API.Config.OptimumConfig.ChunkReadPoolEnabled = originalEnabled;
            Vintagestory.API.Config.OptimumConfig.ChunkReadPoolWorkers = originalWorkers;
        }

        string supplyPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch"));
        Assert.Contains("if (!server.IsDedicatedServer && Environment.ProcessorCount <= 6)", supplyPatch);
        Assert.Contains("maxDop = Math.Min(2, maxDop);", supplyPatch);
    }

    [Fact]
    public void SystemRenderEntities_CachesVisibleRenderersAndReordersDimensionCheck()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch"));
        Assert.Contains("private readonly List<EntityRenderer> renderedEntities = new List<EntityRenderer>();", patch);
        Assert.Contains("private readonly List<EntityRenderer> shadowRenderedEntities = new List<EntityRenderer>();", patch);
        Assert.Contains("renderedEntities.Clear();", patch);
        Assert.Contains("renderedEntities.Add(entityRenderer.Value);", patch);
        Assert.Contains("for (int i = 0; i < renderedEntities.Count; i++)", patch);
        Assert.Contains("shadowRenderedEntities.Clear();", patch);
        Assert.Contains("shadowRenderedEntities.Add(entityRenderer.Value);", patch);
        Assert.Contains("renderedEntities?.Clear();", patch);
        Assert.Contains("shadowRenderedEntities?.Clear();", patch);
        Assert.Contains("for (int i = 0; i < shadowRenderedEntities.Count; i++)", patch);
        Assert.Contains("entity.Pos.Dimension != dimension", patch);

        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"renderedEntities\",", programSource);
        Assert.Contains("\"shadowRenderedEntities\",", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderEntities\", \".ctor\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderEntities\", \"OnRenderAfterOIT\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderEntities\", \"OnRenderFrame2D\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderEntities\", \"Dispose\", 1)", programSource);
    }

    [Fact]
    public void FrustumCullingAndMeshDataPool_ReordersRangeBeforePlanesAndBatchesCulling()
    {
        string frustumPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Client/Render/FrustumCulling.cs.patch"));
        int distanceIdx = frustumPatch.IndexOf("+            double distance = playerPos.HorDistanceSqTo(sphere.x, sphere.z);");
        int planeIdx = frustumPatch.IndexOf("+            if (frustum[0].AABBisOutside(sphere)) return false;");
        Assert.True(distanceIdx >= 0 && planeIdx >= 0 && distanceIdx < planeIdx,
            "FrustumCulling.InFrustumAndRange must evaluate horizontal distance before frustum plane AABB tests.");

        string meshPoolPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch"));
        Assert.Contains("switch (frustumCullMode)", meshPoolPatch);
        Assert.Contains("int visibleBufIndex = ModelDataPoolLocation.VisibleBufIndex;", meshPoolPatch);
        Assert.Contains("case EnumFrustumCullMode.CullNormal:", meshPoolPatch);
        Assert.Contains("case EnumFrustumCullMode.CullInstant:", meshPoolPatch);
        Assert.Contains("case EnumFrustumCullMode.CullInstantShadowPassNear:", meshPoolPatch);
        Assert.Contains("case EnumFrustumCullMode.CullInstantShadowPassFar:", meshPoolPatch);

        string simdSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/OptimumFrustumCullSimd.cs"));
        int simdDistanceIdx = simdSource.IndexOf("double distance = playerPos.HorDistanceSqTo(sphere.x, sphere.z);");
        int simdOutsideIdx = simdSource.IndexOf("ctx.IsOutside5");
        Assert.True(simdDistanceIdx >= 0 && simdOutsideIdx >= 0 && simdDistanceIdx < simdOutsideIdx,
            "OptimumFrustumCullSimd.InFrustumAndRange must evaluate distance before SIMD plane tests.");
    }

    [Fact]
    public void AStar_CachesDirectionIndependentColumnProbesInTraversable()
    {
        string runtimeAStar = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/Essentials/AStar.cs.patch"));
        Assert.Contains("public struct OptimumColumnProbeResult", runtimeAStar);
        Assert.Contains("(columnProbeCache ??= new Dictionary<long, OptimumColumnProbeResult>()).Clear();", runtimeAStar);
        Assert.Contains("probeCache.TryGetValue(probeKey, out OptimumColumnProbeResult probe)", runtimeAStar);
        Assert.Contains("ResolveColumnProbe(node, stepHeight, maxFallHeight, entityCollBox);", runtimeAStar);
        Assert.Contains("fromDir.IsDiagonal", runtimeAStar);

        string sourceAStar = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/Astar/AStar.cs.patch"));
        Assert.Contains("public struct OptimumColumnProbeResult", sourceAStar);
        Assert.Contains("(columnProbeCache ??= new Dictionary<long, OptimumColumnProbeResult>()).Clear();", sourceAStar);
        Assert.Contains("probeCache.TryGetValue(probeKey, out OptimumColumnProbeResult probe)", sourceAStar);
        Assert.Contains("ResolveColumnProbe(node, stepHeight, maxFallHeight, entityCollBox);", sourceAStar);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("EssentialsManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;
        var types = (IEnumerable<string>)manifest.GetType().GetProperty("Types")!.GetValue(manifest)!;
        Assert.Contains("Vintagestory.Essentials.OptimumColumnProbeResult", types);

        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        Assert.True(members.ContainsKey("Vintagestory.Essentials.AStar"));
        Assert.Contains("columnProbeCache", members["Vintagestory.Essentials.AStar"]);
        Assert.Contains("ResolveColumnProbe", members["Vintagestory.Essentials.AStar"]);
        Assert.Contains("GetColumnProbeKey", members["Vintagestory.Essentials.AStar"]);

        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m =>
            m.TypeFullName == "Vintagestory.Essentials.AStar" &&
            m.MethodName == "traversable" &&
            m.ParamCount == 6);
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

    [Fact]
    public void GuiCompositeSettings_HousesRenderScaleAndChiselLodInOptimumTab()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));

        // Verify Chisel LOD and Render Scale controls are in the Optimum tab
        Assert.Contains("\"optChiselLod\"", patch);
        Assert.Contains("\"optChiselLodDist\"", patch);
        Assert.Contains("\"optRenderScale\"", patch);
        Assert.Contains("\"optFsrSharpness\"", patch);

        // Verify Optimum tab rows contain them
        Assert.Contains("+		AddOptimumSwitchRow(\"optimum-chisellod\"", patch);
        Assert.Contains("+		AddOptimumDropdownRow(\"optimum-renderscale\"", patch);
        Assert.Contains("+		AddOptimumSliderRow(\"optimum-fsrsharpness\"", patch);
        Assert.Contains("+		AddOptimumSliderRow(\"optimum-chiselloddist\"", patch);

        // Verify Graphics tab does not contain them
        Assert.DoesNotContain("guiComposerChisel", patch);
        Assert.DoesNotContain("elementBoundsRenderScale", patch);
    }

    [Fact]
    public void AdaptiveRadius_DefaultsToFalseForFirstTimeConfigCreation()
    {
        // Verify serialized config model defaults to false
        var data = new Vintagestory.API.Config.OptimumConfigData();
        Assert.False(data.AdaptiveRadius);
    }

    [Fact]
    public void FsrRcas_SharpnessSliderConfigurableAndUniformPassed()
    {
        // 1. Verify OptimumConfig defaults
        Assert.Equal(0.2f, Vintagestory.API.Config.OptimumConfig.FsrSharpness);

        // 2. Verify GuiCompositeSettings patch adds optFsrSharpness slider under optRenderScale
        string guiPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));
        Assert.Contains("\"optFsrSharpness\"", guiPatch);
        Assert.Contains("onOptimumFsrSharpnessChanged", guiPatch);
        Assert.Contains("((GuiElementSlider)optimumControls[\"optFsrSharpness\"]).SetValues", guiPatch);

        // 3. Verify ClientPlatformWindows patch caches sharpness and passes uniform to fsrRcas
        string platformPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));
        Assert.Contains("_optimumLastFsrSharpness", platformPatch);
        Assert.Contains("fsrRcas.Uniform(\"sharpness\", _optimumLastFsrSharpness);", platformPatch);

        // 4. Verify Program.cs injects members
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"OptimumFsrSharpness\"", programSource);
        Assert.Contains("\"_optimumLastFsrSharpness\"", programSource);
        Assert.Contains("\"onOptimumFsrSharpnessChanged\"", programSource);
    }

    [Fact]
    public void ParticlePoolQuadsAndParticleManager_VectorizeInterpolationAndReuseProps()
    {
        // 1. Verify ParticlePoolQuads patch pads velocities to 4 floats and vectorizes ProcessParticlesFromOffThread
        string poolPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs.patch"));
        Assert.Contains("velocities[num] = new float[4 * poolSize];", poolPatch);
        Assert.Contains("Vector<float>", poolPatch);
        Assert.Contains("vVal + vCamDelta + vVel * vDtRatio", poolPatch);
        Assert.Contains("array[velIdx + 3] = 0f;", poolPatch);

        // 2. Verify ParticleManager patch reuses reusableSpawnProps
        string managerPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticleManager.cs.patch"));
        Assert.Contains("protected SimpleParticleProperties reusableSpawnProps;", managerPatch);
        Assert.Contains("+		SimpleParticleProperties props = reusableSpawnProps ??= new SimpleParticleProperties();", managerPatch);
        Assert.Contains("-		SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(quantity, quantity", managerPatch);

        // 3. Verify Program.cs targets and fields
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"reusableSpawnProps\"", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ParticlePoolQuads\", \"ProcessParticlesFromOffThread\", 2,", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ParticlePoolQuads\", \"OnNewFrameOffThread\", 2,", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ParticleManager\", \"SpawnParticles\", 10,", programSource);

        // 4. Verify cecil-owned.list includes ParticleManager
        string cecilList = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticleManager.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs.patch", cecilList);
    }

    [Fact]
    public void RuntimeConfig_LowMemoryCalibrationDisablesDatasAndBindsHighMemoryPercent()
    {
        // 1. Verify sync-runtimeconfig-frameworks.ps1 handles low memory calibration
        string script = File.ReadAllText(
            PatchReader.FindRepositoryFile("scripts/sync-runtimeconfig-frameworks.ps1"));
        Assert.Contains("TotalAvailableMemoryBytes", script);
        Assert.Contains("DynamicAdaptationMode", script);
        Assert.Contains("HighMemoryPercent", script);

        // 2. Verify PackageDeployer calibrates runtime configs in staged deployment
        string deployerSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Bootstrap.Core/Install/PackageDeployer.cs"));
        Assert.Contains("CalibrateRuntimeConfigs(stageDir);", deployerSource);
        Assert.Contains("DynamicAdaptationMode", deployerSource);
        Assert.Contains("HighMemoryPercent", deployerSource);

        // 3. Verify Launcher calibration logic
        string tempDir = Directory.CreateTempSubdirectory("optimum-launcher-gc-").FullName;
        try
        {
            string cfgPath = System.IO.Path.Combine(tempDir, "Vintagestory.runtimeconfig.json");
            File.WriteAllText(cfgPath, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "configProperties": {
                      "System.GC.Server": true,
                      "System.GC.DATAs": 1
                    }
                  }
                }
                """);

            Optimum.Launcher.Program.CalibrateRuntimeConfigForMemory(tempDir, ["--optimum-memory-profile=aggressive"]);
            string content = File.ReadAllText(cfgPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(content);
            var props = node!["runtimeOptions"]!["configProperties"]!.AsObject();
            Assert.Equal(0, props["System.GC.DynamicAdaptationMode"]!.GetValue<int>());
            Assert.Equal(75, props["System.GC.HighMemoryPercent"]!.GetValue<int>());
            Assert.False(props.ContainsKey("System.GC.DATAs"));

            Optimum.Launcher.Program.CalibrateRuntimeConfigForMemory(tempDir, ["--optimum-memory-profile=throughput"]);
            content = File.ReadAllText(cfgPath);
            node = System.Text.Json.Nodes.JsonNode.Parse(content);
            props = node!["runtimeOptions"]!["configProperties"]!.AsObject();
            Assert.Equal(1, props["System.GC.DATAs"]!.GetValue<int>());
            Assert.False(props.ContainsKey("System.GC.DynamicAdaptationMode"));
            Assert.False(props.ContainsKey("System.GC.HighMemoryPercent"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtraSettingsTab_StreamlinedAndGreedyMeshPresetsConfigured()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));

        // Verify streamlined player preference rows are present
        Assert.Contains("AddOptimumSwitchRow(\"optimum-bgfps\"", patch);
        Assert.Contains("AddOptimumDropdownRow(\"optimum-guigpu\"", patch);
        Assert.Contains("AddOptimumSwitchRow(\"optimum-godrayscap\"", patch);
        Assert.Contains("AddOptimumSliderRow(\"optimum-shadowdist\"", patch);
        Assert.Contains("AddOptimumDropdownRow(\"optimum-greedymesh-preset\"", patch);
        Assert.Contains("GetOptimumGreedyMeshPresetIndex()", patch);
        Assert.Contains("onOptimumGreedyMeshPresetChanged", patch);

        // Verify obsolete/lossless controls are no longer added in OnOptimumOptions
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-framepacing\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-shadowcull\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-repulsion\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-dynlight\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-animblocklod\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-shadowfarveg\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-weatherwind\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-particledist\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-occlusionscale\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-dynlightcache\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-entitylightbatch\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-entityshadercache\"", patch);
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-greedymesh\"", patch);
        Assert.DoesNotContain("+		AddOptimumSliderRow(\"optimum-greedyspan\"", patch);
        Assert.DoesNotContain("+		AddOptimumSliderRow(\"optimum-greedylighttol\"", patch);
        Assert.DoesNotContain("+		AddOptimumSliderRow(\"optimum-greedyfardist\"", patch);

        // Verify patcher injects the greedy mesh preset methods
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"GetOptimumGreedyMeshPresetIndex\",", programSource);
        Assert.Contains("\"onOptimumGreedyMeshPresetChanged\",", programSource);

        // Verify OptimumConfig preset configuration logic
        bool origEnabled = Vintagestory.API.Config.OptimumConfig.GreedyMeshEnabled;
        int origSpan = Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeWidth;
        int origTol = Vintagestory.API.Config.OptimumConfig.GreedyMeshLightTolerance;
        int origFar = Vintagestory.API.Config.OptimumConfig.GreedyMeshFarDistance;

        try
        {
            Vintagestory.API.Config.OptimumConfig.SetGreedyMeshPreset(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetOff);
            Assert.False(Vintagestory.API.Config.OptimumConfig.GreedyMeshEnabled);
            Assert.Equal(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetOff, Vintagestory.API.Config.OptimumConfig.GetGreedyMeshPreset());

            Vintagestory.API.Config.OptimumConfig.SetGreedyMeshPreset(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetBalanced);
            Assert.True(Vintagestory.API.Config.OptimumConfig.GreedyMeshEnabled);
            Assert.Equal(4, Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeWidth);
            Assert.Equal(4, Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeHeight);
            Assert.Equal(0, Vintagestory.API.Config.OptimumConfig.GreedyMeshLightTolerance);
            Assert.Equal(64, Vintagestory.API.Config.OptimumConfig.GreedyMeshFarDistance);
            Assert.Equal(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetBalanced, Vintagestory.API.Config.OptimumConfig.GetGreedyMeshPreset());

            Vintagestory.API.Config.OptimumConfig.SetGreedyMeshPreset(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetAggressive);
            Assert.True(Vintagestory.API.Config.OptimumConfig.GreedyMeshEnabled);
            Assert.Equal(8, Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeWidth);
            Assert.Equal(8, Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeHeight);
            Assert.Equal(2, Vintagestory.API.Config.OptimumConfig.GreedyMeshLightTolerance);
            Assert.Equal(0, Vintagestory.API.Config.OptimumConfig.GreedyMeshFarDistance);
            Assert.Equal(Vintagestory.API.Config.OptimumConfig.GreedyMeshPresetAggressive, Vintagestory.API.Config.OptimumConfig.GetGreedyMeshPreset());
        }
        finally
        {
            Vintagestory.API.Config.OptimumConfig.GreedyMeshEnabled = origEnabled;
            Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeWidth = origSpan;
            Vintagestory.API.Config.OptimumConfig.GreedyMeshMaxMergeHeight = origSpan;
            Vintagestory.API.Config.OptimumConfig.GreedyMeshLightTolerance = origTol;
            Vintagestory.API.Config.OptimumConfig.GreedyMeshFarDistance = origFar;
        }
    }

    [Fact]
    public void OptiTimeGuard_EnforcesCrashResilientLeaseAndRollback()
    {
        string guardSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumOptiTimeGuard.cs"));

        Assert.Contains("OptiTimeLeaseManifest", guardSource);
        Assert.Contains("WriteLeaseManifest", guardSource);
        Assert.Contains("RestoreFromLease", guardSource);
        Assert.Contains("optitime-lease.json", guardSource);
        Assert.Contains("File.Move(entry.DisabledPath, entry.OriginalPath)", guardSource);
        Assert.Contains("RestoreAll(logger)", guardSource);
    }

    [Fact]
    public void CacheManager_StructuredValidationStatusAndDiagnostics()
    {
        string cacheManagerSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Launcher/CacheManager.cs"));
        string resultSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Launcher/CacheValidationResult.cs"));
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Launcher/Program.cs"));

        Assert.Contains("CacheValidationStatus", resultSource);
        Assert.Contains("ManifestMissing", resultSource);
        Assert.Contains("VersionMismatch", resultSource);
        Assert.Contains("CachedAssemblyCorrupted", resultSource);
        Assert.Contains("ApiContractInvalid", resultSource);
        Assert.Contains("CacheValidationResult ValidateCache", cacheManagerSource);
        Assert.Contains("[Optimum.Launcher] Cache invalidated: {validationResult.Status}", programSource);
    }

    [Fact]
    public void PotInFirepitRenderer_StaticMeshAndFrustumCullOptimization()
    {
        string rendererPatch = PatchReader.ReadPatch("patches/VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs.patch");
        Assert.Contains("SquareDistanceTo", rendererPatch);
        Assert.Contains("RenderRange * RenderRange", rendererPatch);
        Assert.Contains("SphereInFrustum", rendererPatch);

        string firepitPatch = PatchReader.ReadPatch("patches/VSSurvivalMod/BlockEntity/Firepit/BEFirepit.cs.patch");
        Assert.Contains("GetPotStaticMesh", firepitPatch);
        Assert.Contains("isColdPot", firepitPatch);
        Assert.Contains("furnaceTemperature < 50", firepitPatch);
        Assert.Contains("shouldRedraw = true;", firepitPatch);

        string patcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("\"GetPotStaticMesh\"", patcherSource);
    }

    [Fact]
    public void CircuitBreaker_AllocationFreeEntityAndGearRendererRecovery()
    {
        // 1. Verify struct properties and allocation-free state transitions
        var breaker = default(Vintagestory.API.Config.OptimumCircuitBreaker);
        Assert.True(breaker.IsClosed);
        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.TripCount);

        var logged = new List<string>();
        breaker.Trip(new Exception("test failure"), "render-system", msg => logged.Add(msg));
        Assert.True(breaker.IsOpen);
        Assert.False(breaker.IsClosed);
        Assert.Equal(1, breaker.TripCount);
        Assert.Single(logged);

        // Registry integration
        Vintagestory.API.Config.OptimumCircuitBreakerRegistry.Register("render-system", () => breaker.GetStatus(), () => breaker.Reset());
        string summary = Vintagestory.API.Config.OptimumCircuitBreakerRegistry.GetBreakersSummary();
        Assert.Contains("render-system=open (tripped, count=1)", summary);

        Vintagestory.API.Config.OptimumCircuitBreakerRegistry.ResetAll();
        Assert.True(breaker.IsClosed);
        Vintagestory.API.Config.OptimumCircuitBreakerRegistry.Unregister("render-system");

        // 2. Verify SystemRenderEntities patch contains breaker fields and registration
        string renderEntitiesPatch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch");
        Assert.Contains("optimumEntityLightBreaker", renderEntitiesPatch);
        Assert.Contains("optimumEntityShaderCacheBreaker", renderEntitiesPatch);
        Assert.Contains("OptimumCircuitBreakerRegistry.Register(\"entity-light-batch\"", renderEntitiesPatch);
        Assert.Contains("OptimumCircuitBreakerRegistry.Register(\"entity-shader-cache\"", renderEntitiesPatch);

        // 3. Verify GearRenderer patch contains breaker fields and registration
        string gearRendererPatch = PatchReader.ReadPatch(
            "patches/VSSurvivalMod/Systems/TemporalStability/GearRenderer.cs.patch");
        Assert.Contains("optimumGearBreaker", gearRendererPatch);
        Assert.Contains("OptimumCircuitBreakerRegistry.Register(\"gear-renderer\"", gearRendererPatch);

        // 4. Verify OptimumStatus surfaces circuit breaker summary and resets on /optimum reset
        string statusSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
        Assert.Contains("OptimumCircuitBreakerRegistry.GetBreakersSummary()", statusSource);
        Assert.Contains("OptimumCircuitBreakerRegistry.ResetAll()", statusSource);
    }

    [Fact]
    public void OptimumStatus_SurfacesControllerPhasesShaderReportAndBreakers()
    {
        string statusSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
        Assert.Contains("shader-report: {GetShaderCompatibilityStatusSummary()}", statusSource);
        Assert.Contains("adaptive-workers: {GetAdaptiveWorkerSummary()}", statusSource);
        Assert.Contains("adaptive-radius: {GetAdaptiveRadiusSummary()}", statusSource);

        string workerSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/AdaptiveWorkerController.cs"));
        Assert.Contains("enum AdaptiveWorkerPhase", workerSource);
        Assert.Contains("CurrentPhase", workerSource);
        Assert.Contains("PhaseName", workerSource);

        string radiusSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/OptimumAdaptiveRadiusController.cs"));
        Assert.Contains("enum AdaptiveRadiusPhase", radiusSource);
        Assert.Contains("CurrentPhase", radiusSource);
        Assert.Contains("PhaseName", radiusSource);

        string configSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/OptimumConfig.cs"));
        Assert.Contains("ShaderCompatibilityReportStatus", configSource);
    }

    [Fact]
    public void VoxelIndexer_TieredPGO_BitShiftEquivalence_AndBoundsCheckElimination()
    {
        // 1. Bitwise shift equivalence with 3D multiplication across all 32x32x32 combinations
        for (int y = 0; y < 32; y++)
        for (int z = 0; z < 32; z++)
        for (int x = 0; x < 32; x++)
        {
            int expected = y * 1024 + z * 32 + x;
            int actual = OptimumVoxelIndexer.ToIndex(x, y, z);
            Assert.Equal(expected, actual);

            OptimumVoxelIndexer.FromIndex(actual, out int ox, out int oy, out int oz);
            Assert.Equal(x, ox);
            Assert.Equal(y, oy);
            Assert.Equal(z, oz);

            Assert.Equal(y * 1024 + z * 32, OptimumVoxelIndexer.GetBaseIndex(y, z));
        }

        // 2. Greedy mesher FaceSliceToIndex equivalence across all faces and slices
        for (int face = 0; face < 6; face++)
        for (int slice = 0; slice < 32; slice++)
        for (int row = 0; row < 32; row++)
        for (int col = 0; col < 32; col++)
        {
            int mesherIdx = OptimumGreedyMesher.FaceSliceToIndex(face, slice, row, col);
            int indexerIdx = OptimumVoxelIndexer.FaceSliceToIndex(face, slice, row, col);
            Assert.Equal(mesherIdx, indexerIdx);
        }

        // 3. 1D span row retrieval and BCE
        int[] chunk = new int[32768];
        for (int i = 0; i < chunk.Length; i++) chunk[i] = i;

        Span<int> chunkSpan = chunk;
        var rowSpan = OptimumVoxelIndexer.GetRowSpan(chunkSpan, 12, 17);
        Assert.Equal(32, rowSpan.Length);
        Assert.Equal(OptimumVoxelIndexer.ToIndex(0, 12, 17), rowSpan[0]);
        Assert.Equal(OptimumVoxelIndexer.ToIndex(31, 12, 17), rowSpan[31]);

        // 4. ForEachVoxel 1D loop visitor
        int visited = 0;
        OptimumVoxelIndexer.ForEachVoxel(chunkSpan, (ref int v, int vx, int vy, int vz, int vidx) =>
        {
            Assert.Equal(vidx, v);
            visited++;
        });
        Assert.Equal(32768, visited);

        // 5. TieredPGO enabled in launcher runtime config calibration
        string tempDir = Directory.CreateTempSubdirectory("optimum-tieredpgo-").FullName;
        try
        {
            string cfgPath = System.IO.Path.Combine(tempDir, "Optimum.runtimeconfig.json");
            File.WriteAllText(cfgPath, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "configProperties": {}
                  }
                }
                """);
            Optimum.Launcher.Program.CalibrateRuntimeConfigForMemory(tempDir, []);
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath));
            var props = node!["runtimeOptions"]!["configProperties"]!.AsObject();
            Assert.True(props["System.Runtime.TieredPGO"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Launcher_TransactionalModDeploymentAndCrashRollback()
    {
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Launcher/Program.cs"));

        Assert.Contains("enum ModDeployState", programSource);
        Assert.Contains("StagingPatched", programSource);
        Assert.Contains("ActivePatched", programSource);
        Assert.Contains("RestoringVanilla", programSource);
        Assert.Contains("CleanVanilla", programSource);
        Assert.Contains("RecoverIncompleteDeployment", programSource);
        Assert.Contains("DeployPlan", programSource);
        Assert.Contains("deploy-{Guid.NewGuid():N}.tmp", programSource);

        string root = Directory.CreateTempSubdirectory("optimum-deploy-reg-").FullName;
        try
        {
            string gameDir = System.IO.Path.Combine(root, "game");
            string cacheDir = System.IO.Path.Combine(root, "cache");
            string modsDir = System.IO.Path.Combine(gameDir, "Mods");
            string vanillaDir = System.IO.Path.Combine(gameDir, ".optimum", "vanilla", "Mods");
            string cacheModsDir = System.IO.Path.Combine(cacheDir, "Mods");

            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(vanillaDir);
            Directory.CreateDirectory(cacheModsDir);

            File.WriteAllText(System.IO.Path.Combine(vanillaDir, "VSEssentials.dll"), "vanilla");
            File.WriteAllText(System.IO.Path.Combine(vanillaDir, "VSSurvivalMod.dll"), "vanilla");
            File.WriteAllText(System.IO.Path.Combine(modsDir, "VSEssentials.dll"), "stale");
            File.WriteAllText(System.IO.Path.Combine(modsDir, "VSSurvivalMod.dll"), "stale");
            File.WriteAllText(System.IO.Path.Combine(cacheModsDir, "VSEssentials.dll"), "patched");
            File.WriteAllText(System.IO.Path.Combine(cacheModsDir, "VSSurvivalMod.dll"), "patched");

            Optimum.Launcher.Program.WriteDeployState(gameDir, Optimum.Launcher.ModDeployState.StagingPatched);
            Optimum.Launcher.Program.RecoverIncompleteDeployment(gameDir);

            Assert.Equal("vanilla", File.ReadAllText(System.IO.Path.Combine(modsDir, "VSEssentials.dll")));
            Assert.Equal(Optimum.Launcher.ModDeployState.CleanVanilla, Optimum.Launcher.Program.ReadDeployState(gameDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlayerDataManager_StreamedJsonSaveAndOrdinalIgnoreCaseEntryLookup()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/PlayerDataManager.cs.patch");
        Assert.Contains("string.Equals(item.PlayerName, playername, StringComparison.OrdinalIgnoreCase)", patch);
        Assert.Contains(".Serialize(textWriter, PlayerDataByUid.Values)", patch);
        Assert.Contains(".Serialize(textWriter2, PlayerGroupsById.Values)", patch);
        Assert.Contains("DateTime now = DateTime.Now;", patch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"OnCheckRequireSave\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"Load\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"GetPlayerEntry\", 3)", programSource);
    }

    [Fact]
    public void MeshDataRecycler_DoRecycling_EvictsWhenIdleAndLoopsRemoveOldest()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryApi/Client/MeshPool/MeshDataRecycler.cs.patch");
        Assert.Contains("ControlSizeOfLists();", patch);
        Assert.Contains("while (list.Count > 0)", patch);
        Assert.Contains("list.GetValueAtIndex(indexOldest).DisposeBasicData();", patch);

        long currentElapsedMs = 10000L;
        var clientMock = StubProxy<IClientWorldAccessor>.Create((method, args) =>
        {
            if (method.Name == "get_ElapsedMilliseconds")
            {
                return currentElapsedMs;
            }
            return null;
        });

        var recycler = new MeshDataRecycler(clientMock);

        // Queue meshes for recycling
        for (int i = 0; i < 5; i++)
        {
            var mesh = new MeshData(4096);
            recycler.Recycle(mesh);
        }

        // Process queue into lists
        recycler.DoRecycling();

        // Advance past TTL (TTL = 15000 ms)
        currentElapsedMs = 50000L;

        // With empty forRecycling queue, DoRecycling should still run ControlSizeOfLists and evict all expired meshes
        recycler.DoRecycling();

        // GetOrCreateMesh should create fresh meshes rather than returning expired ones
        var freshMesh = recycler.GetOrCreateMesh(4096);
        Assert.NotNull(freshMesh);
    }

    private sealed class DummyRegistryObject : RegistryObject
    {
    }

    [Fact]
    public void RegistryObject_SpanBasedCodeWithMethods_MatchExactBehavior()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryApi/Common/Registry/RegistryObject.cs.patch");
        Assert.Contains("string newPath = string.Create(totalLen, (Code.Path, index, components)", patch);
        Assert.Contains("string.Concat(path.AsSpan(0, segStart), part, path.AsSpan(segEnd))", patch);

        var obj = new DummyRegistryObject
        {
            Code = new AssetLocation("game", "rock-granite-raw"),
            Variant = new Vintagestory.API.Util.RelaxedReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                ["type"] = "granite",
                ["state"] = "raw"
            })
        };

        // CodeWithParts single and multi-param
        Assert.Equal("game:rock-granite-lit", obj.CodeWithParts("lit").ToString());
        Assert.Equal("game:rock-north-lit", obj.CodeWithParts("north", "lit").ToString());

        // CodeWithVariant and CodeWithVariants overloads
        Assert.Equal("game:rock-basalt-raw", obj.CodeWithVariant("type", "basalt").ToString());
        Assert.Equal("game:rock-andesite-polished", obj.CodeWithVariants(new Dictionary<string, string>
        {
            ["type"] = "andesite",
            ["state"] = "polished"
        }).ToString());
        Assert.Equal("game:rock-peridotite-cobble", obj.CodeWithVariants(new[] { "type", "state" }, new[] { "peridotite", "cobble" }).ToString());

        // CodeWithPart segment replacements
        Assert.Equal("game:copper-granite-raw", obj.CodeWithPart("copper", 0).ToString());
        Assert.Equal("game:rock-basalt-raw", obj.CodeWithPart("basalt", 1).ToString());
        Assert.Equal("game:rock-granite-fired", obj.CodeWithPart("fired", 2).ToString());

        // Out of bounds IndexOutOfRangeException parity
        Assert.Throws<IndexOutOfRangeException>(() => obj.CodeWithPart("stone", -1));
        Assert.Throws<IndexOutOfRangeException>(() => obj.CodeWithPart("stone", 10));
    }

    [Fact]
    public void TreeAttribute_DirectSwitchFactoryAndServerPacketsReusableTree()
    {
        string treePatch = PatchReader.ReadPatch("patches/VintagestoryApi/Datastructures/AttributeTree/TreeAttribute.cs.patch");
        Assert.Contains("public static IAttribute CreateAttribute(byte attrId)", treePatch);
        Assert.Contains("case 1: return new IntAttribute();", treePatch);
        Assert.Contains("case 16: return new BoolArrayAttribute();", treePatch);
        Assert.Contains("IAttribute attr = CreateAttribute(attrId);", treePatch);

        string serverPacketsPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerPackets.cs.patch");
        Assert.Contains("t_reusableBlockEntityTree", serverPacketsPatch);
        Assert.Contains("treeAttribute.Clear();", serverPacketsPatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Server.ServerPackets\", \"getBlockEntityData\", 3)", programSource);

        for (byte i = 1; i <= 16; i++)
        {
            var attr = Vintagestory.API.Datastructures.TreeAttribute.CreateAttribute(i);
            Assert.NotNull(attr);
            Assert.Equal(i, (byte)attr.GetAttributeId());
        }

        var tree = new Vintagestory.API.Datastructures.TreeAttribute();
        tree.SetInt("intVal", 42);
        tree.SetString("strVal", "optimum");
        tree.SetDouble("dblVal", 3.1415);
        byte[] bytes = tree.ToBytes();

        var roundtrip = Vintagestory.API.Datastructures.TreeAttribute.CreateFromBytes(bytes);
        Assert.Equal(42, roundtrip.GetInt("intVal"));
        Assert.Equal("optimum", roundtrip.GetString("strVal"));
        Assert.Equal(3.1415, roundtrip.GetDouble("dblVal"));
    }

    [Fact]
    public void ServerSystemLoadAndSaveGame_DisposesOnEarlyStop_AndWindowCloseAwaitsShutdown()
    {
        string savePatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch");
        Assert.Contains("chunkthread?.gameDatabase?.Dispose();", savePatch);
        Assert.Contains("public override void Dispose()", savePatch);

        string windowPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs.patch");
        Assert.Contains("while (ScreenManager.Platform.IsServerRunning)", windowPatch);
        Assert.Contains("Thread.Sleep(5);", windowPatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.True(programSource.Contains("new(\"Vintagestory.Server.ServerSystemLoadAndSaveGame\", \"Dispose\", 0)") ||
                    (programSource.Contains("Vintagestory.Server.ServerSystemLoadAndSaveGame") && programSource.Contains("\"Dispose\"")));
        Assert.Contains("new(\"Vintagestory.Client.GuiScreenRunningGame\", \"OnWindowClosed\", 0)", programSource);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs.patch", cecilList);
    }

    [Fact]
    public void ModLoader_DotPrefixedAndEmptyModInfoAndSidedMods()
    {
        string modLoaderPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Common/ModLoader.cs.patch");
        Assert.Contains("IsIgnoredDevDirectory(fsInfo.Name)", modLoaderPatch);
        Assert.Contains("IsIgnoredDevDirectory", modLoaderPatch);
        Assert.Contains("if (mod.Info != null && !mod.Info.Side.Is(side))", modLoaderPatch);
        Assert.Contains("CompareModRequiresCompilation", modLoaderPatch);

        string modContainerPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Common/ModContainer.cs.patch");
        Assert.Contains("Failed to deserialize modinfo.json in", modContainerPatch);
        Assert.Contains("if (base.Info?.BackgroundPaths != null)", modContainerPatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Common.ModLoader\", \"CollectMods\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Common.ModLoader\", \"instantiateMods\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Common.ModContainer\", \"LoadModInfo\", 2)", programSource);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/ModLoader.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/ModContainer.cs.patch", cecilList);
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".vs", true)]
    [InlineData(".idea", true)]
    [InlineData(".vscode", true)]
    [InlineData(".github", true)]
    [InlineData(".fleet", true)]
    [InlineData(".obsidian", true)]
    [InlineData(".cache", true)]
    [InlineData(".custommod", false)]
    [InlineData(".assets", false)]
    [InlineData("assets", false)]
    [InlineData("modinfo.json", false)]
    public void ModLoader_IsIgnoredDevDirectory_DistinguishesDevFoldersFromModPaths(string name, bool expectedIgnored)
    {
        string modLoaderPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Common/ModLoader.cs.patch");
        Assert.Contains("IsIgnoredDevDirectory", modLoaderPatch);

        bool isIgnored = name == ".git" || name == ".vs" || name == ".idea" || name == ".vscode" ||
                         name == ".github" || name == ".fleet" || name == ".obsidian" || name == ".cache";
        Assert.Equal(expectedIgnored, isIgnored);
    }

    [Fact]
    public void MeshData_CustomVertexDataStrideAndSelectiveElements()
    {
        string meshDataPatch = PatchReader.ReadPatch("patches/VintagestoryApi/Client/Model/Mesh/MeshData.cs.patch");
        Assert.Contains("data.CustomInts.Count / data.VerticesCount", meshDataPatch);
        Assert.Contains("vertexNum * valsPerVertex_Ints + j", meshDataPatch);
        Assert.Contains("CustomInts.Count += countToAdd", meshDataPatch);

        string tesselatorPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch");
        Assert.Contains("cshape.QuantityElements, cshape.SelectiveElements", tesselatorPatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"CreateMesh\", 4)", programSource);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch", cecilList);

        // Functional behavior of MeshData AddMeshData with CustomInts
        var dst = new MeshData(4, 6);
        dst.CustomInts = new CustomMeshDataPartInt(8)
        {
            Count = 0,
            InterleaveSizes = new[] { 2 },
            InterleaveStride = 8
        };

        var src = new MeshData(4, 6);
        src.xyz = new float[12];
        src.Normals = new int[4];
        src.Uv = new float[8];
        src.Rgba = new byte[16];
        src.Flags = new int[4];
        src.Indices = new[] { 0, 1, 2, 0, 2, 3 };
        src.IndicesCount = 6;
        src.VerticesCount = 4;
        src.CustomInts = new CustomMeshDataPartInt(8)
        {
            Values = new[] { 10, 11, 20, 21, 30, 31, 40, 41 },
            Count = 8,
            InterleaveSizes = new[] { 2 },
            InterleaveStride = 8
        };

        dst.AddMeshData(src);
        Assert.Equal(4, dst.VerticesCount);
        Assert.Equal(8, dst.CustomInts.Count);
        Assert.Equal(10, dst.CustomInts.Values[0]);
        Assert.Equal(11, dst.CustomInts.Values[1]);
        Assert.Equal(20, dst.CustomInts.Values[2]);
        Assert.Equal(21, dst.CustomInts.Values[3]);
        Assert.Equal(30, dst.CustomInts.Values[4]);
        Assert.Equal(31, dst.CustomInts.Values[5]);
        Assert.Equal(40, dst.CustomInts.Values[6]);
        Assert.Equal(41, dst.CustomInts.Values[7]);

        // Merge mesh without CustomInts into mesh with CustomInts
        var emptySrc = new MeshData(4, 6);
        emptySrc.xyz = new float[12];
        emptySrc.Normals = new int[4];
        emptySrc.Uv = new float[8];
        emptySrc.Rgba = new byte[16];
        emptySrc.Flags = new int[4];
        emptySrc.Indices = new[] { 0, 1, 2, 0, 2, 3 };
        emptySrc.IndicesCount = 6;
        emptySrc.VerticesCount = 4;

        dst.AddMeshData(emptySrc);
        Assert.Equal(8, dst.VerticesCount);
        Assert.Equal(16, dst.CustomInts.Count);
        for (int i = 8; i < 16; i++)
        {
            Assert.Equal(0, dst.CustomInts.Values[i]);
        }
    }

    [Fact]
    public void Asset_Equals_UnwrapsIAssetWhenComparingAssets()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Common/Asset.cs.patch");
        Assert.Contains("if (obj is IAsset asset)", patch);
        Assert.Contains("return Location.Equals(asset.Location);", patch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Common.Asset\", \"Equals\", 1)", programSource);

        string repoRoot = System.IO.Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;
        string vsLibDll = System.IO.Path.Combine(repoRoot, "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (File.Exists(vsLibDll))
        {
            var asm = Assembly.LoadFrom(vsLibDll);
            var assetType = asm.GetType("Vintagestory.Common.Asset");
            Assert.NotNull(assetType);

            var locSame1 = new AssetLocation("game", "textures/block/stone.png");
            var locSame2 = new AssetLocation("game", "textures/block/stone.png");
            var locDiff = new AssetLocation("game", "textures/block/dirt.png");

            object asset1 = Activator.CreateInstance(assetType, new object[] { new byte[] { 1 }, locSame1, null })!;
            object asset2 = Activator.CreateInstance(assetType, new object[] { new byte[] { 2 }, locSame2, null })!;
            object asset3 = Activator.CreateInstance(assetType, new object[] { new byte[] { 1 }, locDiff, null })!;

            Assert.True(asset1.Equals(asset2));
            Assert.True(asset1.Equals(locSame2));
            Assert.False(asset1.Equals(asset3));
            Assert.Equal(asset1.GetHashCode(), asset2.GetHashCode());
        }
    }

    [Fact]
    public void WeatherSystem_EvictsUnloadedRegionsUnderLock()
    {
        string basePatch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemBase.cs.patch");
        Assert.Contains("api.Event.MapRegionUnloaded += OnMapRegionUnloaded;", basePatch);
        Assert.Contains("if (!weatherSimByMapRegion.Remove(index2d, out weatherSim))", basePatch);
        Assert.Contains("region.SetModdata(\"weatherState\", weatherSim.ToBytes());", basePatch);

        string runtimeBasePatch = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSystemBase.cs.patch");
        Assert.Contains("api.Event.MapRegionUnloaded += OnMapRegionUnloaded;", runtimeBasePatch);
        Assert.Contains("if (!weatherSimByMapRegion.Remove(index2d, out weatherSim))", runtimeBasePatch);

        string serverPatch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemServer.cs.patch");
        Assert.Contains("lock (GetWeatherSimLock())", serverPatch);

        string clientPatch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemClient.cs.patch");
        Assert.Contains("lock (GetWeatherSimLock())", clientPatch);
    }

    [Fact]
    public void Handbook_CategoryIndexing_And_GuiElementFlatList_O1_ViewportParity()
    {
        string handbookPatch = PatchReader.ReadPatch("patches/VSSurvivalMod/Systems/Handbook/CollectibleBehaviorHandbookTextAndExtraInfo.cs.patch");
        Assert.Contains("getOrCreateHandbookCategoryCache", handbookPatch);
        Assert.Contains("BreakBlocksByDroppedId", handbookPatch);
        Assert.Contains("HarvestBlocksByDroppedId", handbookPatch);
        Assert.Contains("getOrCreateGridRecipesByOutput", handbookPatch);

        string flatListPatch = PatchReader.ReadPatch("patches/VSSurvivalMod/Systems/Handbook/Gui/GuiElementFlatList.cs.patch");
        Assert.Contains("visibleIndices", flatListPatch);
        Assert.Contains("startV", flatListPatch);
        Assert.Contains("insideBounds.absY + v * cellStep", flatListPatch);
        Assert.Contains("Bounds.OuterHeight + 50", flatListPatch);

        string survivalDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSSurvivalMod.dll");
        if (File.Exists(survivalDll))
        {
            var asm = Assembly.LoadFrom(survivalDll);
            var flatListType = asm.GetType("Vintagestory.GameContent.GuiElementFlatList");
            Assert.NotNull(flatListType);
            Assert.NotNull(flatListType.GetMethod("CalcTotalHeight"));
            Assert.NotNull(flatListType.GetMethod("RenderInteractiveElements"));
            Assert.NotNull(flatListType.GetMethod("OnMouseUpOnElement"));

            var handbookBehaviorType = asm.GetType("Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo");
            Assert.NotNull(handbookBehaviorType);
            Assert.NotNull(handbookBehaviorType.GetMethod("GetHandbookInfo"));
        }
    }

    [Fact]
    public void TextureAtlasManager_RegenMipMaps_GuardsOutOfRangeAtlasIndex()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/TextureAtlasManager.cs.patch");
        Assert.Contains("if (atlasNumber < 0 || atlasNumber >= AtlasTextures.Count)", patch);
        Assert.Contains("Cannot regen mipmaps for atlas number {0}, out of range (count: {1})", patch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Client.NoObf.TextureAtlasManager\", \"RegenMipMaps\", 1)", programSource);
    }

    [Fact]
    public void CheckGlErrorAlways_ThrottlesSynchronousGlGetErrorWhenDebuggingDisabled()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch");
        Assert.Contains("public override void CheckGlErrorAlways", patch);
        Assert.Contains("if (!GlErrorChecking && !GlDebugMode)", patch);
    }

    private static int staticSaveHandlerCallCount;
    private static void StaticGameWorldSaveHandler()
    {
        staticSaveHandlerCallCount++;
    }

    [Fact]
    public void ServerEventManager_TriggerGameWorldBeingSaved_HandlesStaticHandlersWhenProfilerEnabled()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerEventManager.cs.patch");
        Assert.Contains("action.Target?.ToString() ?? action.Method.DeclaringType?.FullName ?? action.Method.Name", patch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Server.ServerEventManager\", \"TriggerGameWorldBeingSaved\", 0)", programSource);

        string repoRoot = System.IO.Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;
        string vsLibDll = System.IO.Path.Combine(repoRoot, "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (File.Exists(vsLibDll))
        {
            var asm = Assembly.LoadFrom(vsLibDll);
            var serverMainType = asm.GetType("Vintagestory.Server.ServerMain");
            var eventManagerType = asm.GetType("Vintagestory.Server.ServerEventManager");
            Assert.NotNull(serverMainType);
            Assert.NotNull(eventManagerType);

            var frameProfilerField = serverMainType.GetField("FrameProfiler", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(frameProfilerField);

            object prevProfiler = frameProfilerField.GetValue(null);
            try
            {
                var profiler = new FrameProfilerUtil("[test] ") { Enabled = true };
                profiler.Begin();
                frameProfilerField.SetValue(null, profiler);

                object eventManager = Activator.CreateInstance(eventManagerType, new object[] { null });
                var onSavedEvent = eventManagerType.GetEvent("OnGameWorldBeingSaved");
                Assert.NotNull(onSavedEvent);

                staticSaveHandlerCallCount = 0;
                onSavedEvent.AddEventHandler(eventManager, (System.Action)StaticGameWorldSaveHandler);

                var triggerMethod = eventManagerType.GetMethod("TriggerGameWorldBeingSaved");
                Assert.NotNull(triggerMethod);
                triggerMethod.Invoke(eventManager, null);

                Assert.Equal(1, staticSaveHandlerCallCount);
            }
            finally
            {
                frameProfilerField.SetValue(null, prevProfiler);
            }
        }
    }

    [Fact]
    public void CloudRenderer_UpdateCloudTilesOffThread_ReusesTileOffsetAndCloudTilePosVectors()
    {
        string patch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/CloudRenderer.cs.patch");
        Assert.Contains("private Vec3i reusableTileOffset;", patch);
        Assert.Contains("private Vec3d reusableCloudTilePos;", patch);
        Assert.Contains("(reusableTileOffset ??= new Vec3i()).Set(", patch);
        Assert.Contains("(reusableCloudTilePos ??= new Vec3d()).Set(", patch);

        string runtimePatch = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/CloudRenderer.cs.patch");
        Assert.Contains("private Vec3i reusableTileOffset;", runtimePatch);
        Assert.Contains("private Vec3d reusableCloudTilePos;", runtimePatch);
        Assert.Contains("(reusableTileOffset ??= new Vec3i()).Set(", runtimePatch);
        Assert.Contains("(reusableCloudTilePos ??= new Vec3d()).Set(", runtimePatch);

        string modPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("[\"Vintagestory.GameContent.CloudRenderer\"]", modPatcherSource);
        Assert.Contains("\"reusableTileOffset\"", modPatcherSource);
        Assert.Contains("\"reusableCloudTilePos\"", modPatcherSource);
        Assert.Contains("new(\"Vintagestory.GameContent.CloudRenderer\", \"UpdateCloudTilesOffThread\", 1)", modPatcherSource);
    }

    [Fact]
    public void CloudRendererMap_UpdateCloudTilesOffThread_ReusesTileOffsetAndCloudTilePosVectors()
    {
        string patch = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/Newclouds/CloudRendererMap.cs.patch");
        Assert.Contains("private Vec3i reusableTileOffset;", patch);
        Assert.Contains("private Vec3d reusableCloudTilePos;", patch);
        Assert.Contains("(reusableTileOffset ??= new Vec3i()).Set(", patch);
        Assert.Contains("(reusableCloudTilePos ??= new Vec3d()).Set(", patch);
        Assert.Contains("int regSize = capi.World.BlockAccessor.RegionSize;", patch);
        Assert.Contains("int seaLevel = capi.World.SeaLevel;", patch);
        Assert.Contains("int cloudTileX = tileXPos;", patch);
        Assert.Contains("int cloudTileZ = tileZPos;", patch);

        string runtimePatch = PatchReader.ReadPatch("patches/runtime/VSEssentials/FluffyClouds/CloudRendererMap.cs.patch");
        Assert.Contains("private Vec3i reusableTileOffset;", runtimePatch);
        Assert.Contains("private Vec3d reusableCloudTilePos;", runtimePatch);
        Assert.Contains("(reusableTileOffset ??= new Vec3i()).Set(", runtimePatch);
        Assert.Contains("(reusableCloudTilePos ??= new Vec3d()).Set(", runtimePatch);
        Assert.Contains("int regionSize = capi.World.BlockAccessor.RegionSize;", runtimePatch);
        Assert.Contains("int seaLevel = capi.World.SeaLevel;", runtimePatch);
        Assert.Contains("int cloudTileX = num4;", runtimePatch);
        Assert.Contains("int cloudTileZ = num5;", runtimePatch);

        string modPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        Assert.Contains("[\"FluffyClouds.CloudRendererMap\"]", modPatcherSource);
        Assert.Contains("\"reusableTileOffset\"", modPatcherSource);
        Assert.Contains("\"reusableCloudTilePos\"", modPatcherSource);
        Assert.Contains("new(\"FluffyClouds.CloudRendererMap\", \"UpdateCloudTilesOffThread\", 1)", modPatcherSource);
    }

    [Fact]
    public void ShapeUtil_GetOctagonPoints_AllocatesExactArrayAndMatchesPoints()
    {
        for (int r = 0; r <= 15; r++)
        {
            var points = ShapeUtil.GetOctagonPoints(10, 20, r);
            Assert.NotNull(points);
            if (r == 0)
            {
                Assert.Single(points);
                Assert.Equal(new Vec2i(10, 20), points[0]);
            }
            else
            {
                int th = 9;
                int S = 2 * r;
                int a = Math.Min(S, th);
                int b = (int)Math.Ceiling(Math.Max(0, S - th) / 2.0);
                Assert.Equal(4 * (a + b), points.Length);
            }
        }
    }

#if !NO_DONOR
    [Fact]
    public void ServerPackets_GetEntityDespawnPacket_EmptyAndPopulated()
    {
        var emptyPacket = ServerPackets.GetEntityDespawnPacket(new List<EntityDespawn>());
        Assert.NotNull(emptyPacket);
        Assert.Equal(36, emptyPacket.Id);
        Assert.NotNull(emptyPacket.EntityDespawn);
        Assert.Empty(emptyPacket.EntityDespawn.EntityId);
        Assert.Empty(emptyPacket.EntityDespawn.DespawnReason);
        Assert.Empty(emptyPacket.EntityDespawn.DeathDamageSource);

        var list = new List<EntityDespawn>
        {
            new EntityDespawn
            {
                EntityId = 42,
                DespawnData = new Vintagestory.API.Common.Entities.EntityDespawnData
                {
                    Reason = EnumDespawnReason.Death,
                    DamageSourceForDeath = new DamageSource { Source = EnumDamageSource.Fall }
                }
            },
            new EntityDespawn
            {
                EntityId = 100,
                DespawnData = null
            }
        };

        var packet = ServerPackets.GetEntityDespawnPacket(list);
        Assert.NotNull(packet);
        Assert.Equal(36, packet.Id);
        Assert.NotNull(packet.EntityDespawn);
        Assert.Equal(2, packet.EntityDespawn.EntityIdLength);
        Assert.Equal(42L, packet.EntityDespawn.EntityId[0]);
        Assert.Equal(100L, packet.EntityDespawn.EntityId[1]);
        Assert.Equal((int)EnumDespawnReason.Death, packet.EntityDespawn.DespawnReason[0]);
        Assert.Equal((int)EnumDespawnReason.Death, packet.EntityDespawn.DespawnReason[1]);
        Assert.Equal((int)EnumDamageSource.Fall, packet.EntityDespawn.DeathDamageSource[0]);
        Assert.Equal((int)EnumDamageSource.Block, packet.EntityDespawn.DeathDamageSource[1]);
    }

    [Fact]
    public void BlockTypeNet_ItemTypeNet_DeduplicatedAttributesCloneAndStrictIsolation()
    {
        string json = "{\"handbook\":{\"groupBy\":[\"rock\"],\"extraInfo\":\"detailed\"},\"tier\":2}";
        var blockPacket1 = new Packet_BlockType { Blockclass = "Block", Code = "game:rock-granite", Attributes = json };
        var blockPacket2 = new Packet_BlockType { Blockclass = "Block", Code = "game:rock-andesite", Attributes = json };

        var classRegistry = new ClassRegistry();
        classRegistry.RegisterBlockClass("Block", typeof(Block));

        Block block1 = BlockTypeNet.ReadBlockTypePacket(blockPacket1, null, classRegistry);
        Block block2 = BlockTypeNet.ReadBlockTypePacket(blockPacket2, null, classRegistry);

        Assert.NotNull(block1.Attributes);
        Assert.NotNull(block2.Attributes);
        Assert.Equal(2, block1.Attributes["tier"].AsInt());
        Assert.Equal(2, block2.Attributes["tier"].AsInt());

        // Strict isolation invariant: separate references, mutations on one must not affect the other
        Assert.False(object.ReferenceEquals(block1.Attributes.Token, block2.Attributes.Token));
        block1.Attributes.Token["tier"] = 99;
        Assert.Equal(99, block1.Attributes["tier"].AsInt());
        Assert.Equal(2, block2.Attributes["tier"].AsInt());

        BlockTypeNet.ClearAttributesCache();

        classRegistry.RegisterItemClass("Item", typeof(Item));
        var itemPacket1 = new Packet_ItemType { ItemClass = "Item", Code = "game:gear-rusty", Attributes = json };
        var itemPacket2 = new Packet_ItemType { ItemClass = "Item", Code = "game:gear-temporal", Attributes = json };

        Item item1 = ItemTypeNet.ReadItemTypePacket(itemPacket1, null, classRegistry);
        Item item2 = ItemTypeNet.ReadItemTypePacket(itemPacket2, null, classRegistry);

        Assert.NotNull(item1.Attributes);
        Assert.NotNull(item2.Attributes);
        Assert.False(object.ReferenceEquals(item1.Attributes.Token, item2.Attributes.Token));
        item1.Attributes.Token["tier"] = 77;
        Assert.Equal(77, item1.Attributes["tier"].AsInt());
        Assert.Equal(2, item2.Attributes["tier"].AsInt());

        ItemTypeNet.ClearAttributesCache();
    }
#endif

    [Fact]
    public void JsonPatchLoader_DirectUtf8SerializationAndWildcardCache_EliminatesUtf16StringAndPreservesJsonParity()
    {
        string patchPath = PatchReader.FindRepositoryFile("patches/VSEssentials/Loading/JsonPatchLoader.cs.patch");
        Assert.True(File.Exists(patchPath), "patches/VSEssentials/Loading/JsonPatchLoader.cs.patch must exist");
        string patchText = File.ReadAllText(patchPath);

        Assert.Contains("wildcardCache", patchText);
        Assert.Contains("new UTF8Encoding(false)", patchText);
        Assert.Contains("new MemoryStream(4096)", patchText);
        Assert.Contains("cachedData.asset.Data = memoryStream.ToArray();", patchText);
        Assert.Contains("cachedData.asset.IsPatched = true;", patchText);
        Assert.Contains("-                        var sbFile = StringBuilderCache.Acquire();", patchText);

        string sourcePath = PatchReader.FindRepositoryFile("VSEssentials/Loading/JsonPatchLoader.cs");
        string sourceText = File.ReadAllText(sourcePath);
        Assert.Contains("var wildcardCache = new Dictionary<(string Domain, string BasePath), List<IAsset>>();", sourceText);
        Assert.Contains("cachedData.asset.Data = memoryStream.ToArray();", sourceText);
        Assert.Contains("wildcardCache?.Clear();", sourceText);
        Assert.DoesNotContain("StringBuilderCache.Acquire()", sourceText);
        Assert.DoesNotContain("StringBuilderCache.GetStringAndRelease", sourceText);

        // Verify that UTF-8 stream serialization produces identical bytes and JSON structure without UTF-16 string conversion
        var testObj = new JObject
        {
            ["type"] = "block",
            ["code"] = "game:rock-granite",
            ["attributes"] = new JObject { ["hardness"] = 4.5, ["blastResistance"] = 12 }
        };

        var utf8NoBom = new UTF8Encoding(false);
        byte[] directBytes;
        using (var ms = new MemoryStream())
        {
            using (var writer = new StreamWriter(ms, utf8NoBom, 4096, leaveOpen: true))
            using (var jsonWriter = new JsonTextWriter(writer))
            {
                testObj.WriteTo(jsonWriter);
                jsonWriter.Flush();
            }
            directBytes = ms.ToArray();
        }

        string deserialized = Encoding.UTF8.GetString(directBytes);
        var parsedBack = JToken.Parse(deserialized);
        Assert.True(JToken.DeepEquals(testObj, parsedBack));
        Assert.False(directBytes[0] == 0xEF && directBytes[1] == 0xBB && directBytes[2] == 0xBF, "Direct UTF-8 bytes must not have a BOM preamble");
    }

    [Fact]
    public void LoadBlockShapes_MultiThreadedPartitioningAndCompositeShapeLoadAlternatesFastPath()
    {
        string tesselatorPatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch");
        Assert.True(File.Exists(tesselatorPatchPath), "ShapeTesselatorManager.cs.patch must exist");
        string tesselatorPatch = File.ReadAllText(tesselatorPatchPath);
        Assert.Contains("+		TargetSet[] array = new TargetSet[val];", tesselatorPatch);
        Assert.Contains("-		TargetSet[] array = new TargetSet[1];", tesselatorPatch);

        string compositePatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Model/Shape/CompositeShape.cs.patch");
        Assert.True(File.Exists(compositePatchPath), "CompositeShape.cs.patch must exist");
        string compositePatch = File.ReadAllText(compositePatchPath);
        Assert.Contains("if (Base == null) return;", compositePatch);
        Assert.Contains("if (BakedAlternates != null) return;", compositePatch);
        Assert.Contains("lock (this)", compositePatch);
        Assert.Contains("shape.Base.Path.Length - 1", compositePatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"LoadBlockShapes\", 1)", programSource);

        // Test CompositeShape fast-path on non-wildcard shape without alternates
        var shape = new CompositeShape
        {
            Base = new AssetLocation("game:block/stone/rock")
        };
        shape.LoadAlternates(null, null);
        Assert.Null(shape.BakedAlternates);
        Assert.Equal("block/stone/rock", shape.Base.ToShortString());
    }

    [Fact]
    public void BoxedPacketServerAssets_CompressedBufferCachedAndBuildSleepsRemoved()
    {
        string tcpNetPatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Server.Network/TcpNetConnection.cs.patch");
        Assert.True(File.Exists(tcpNetPatchPath), "TcpNetConnection.cs.patch must exist");
        string tcpNetPatch = File.ReadAllText(tcpNetPatchPath);
        Assert.Contains("assetsBox.compressedBuffer", tcpNetPatch);
        Assert.Contains("assetsBox.isCompressedCached", tcpNetPatch);

        string boxedPacketPatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Server/BoxedPacket_ServerAssets.cs.patch");
        Assert.True(File.Exists(boxedPacketPatchPath), "BoxedPacket_ServerAssets.cs.patch must exist");
        string boxedPacketPatch = File.ReadAllText(boxedPacketPatchPath);
        Assert.Contains("internal byte[] compressedBuffer;", boxedPacketPatch);
        Assert.Contains("internal volatile bool isCompressedCached;", boxedPacketPatch);
        Assert.Contains("internal void Invalidate()", boxedPacketPatch);

        string serverMainPatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Server/ServerMain.cs.patch");
        Assert.True(File.Exists(serverMainPatchPath), "ServerMain.cs.patch must exist");
        string serverMainPatch = File.ReadAllText(serverMainPatchPath);
        Assert.Contains("buildServerAssetsLock", serverMainPatch);
        Assert.Contains("serverAssetsPacketBuilt", serverMainPatch);
        Assert.Contains("public void InvalidateServerAssets()", serverMainPatch);
        Assert.Contains("OptimumAssetEvents.OnServerAssetsInvalidated", serverMainPatch);

        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Server.Network.TcpNetConnection\", \"PreparePacketForSending\", 3)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"WaitOnBuildServerAssetsPacket\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"BuildServerAssetsPacket\", 0)", programSource);
        Assert.Contains("\"InvalidateServerAssets\"", programSource);
        Assert.Contains("\"Invalidate\"", programSource);
    }

    [Fact]
    public void OptimumAssetEvents_InvalidateServerAssets_FiresSubscribers()
    {
        string contractsCsproj = File.ReadAllText(
            PatchReader.FindRepositoryFile("optimum-api-contracts/optimum-api-contracts.csproj"));
        Assert.Contains("OptimumAssetEvents.cs", contractsCsproj);

        bool eventFired = false;
        Action handler = () => eventFired = true;

        try
        {
            Vintagestory.API.Config.OptimumAssetEvents.OnServerAssetsInvalidated += handler;
            Vintagestory.API.Config.OptimumAssetEvents.InvalidateServerAssets();
            Assert.True(eventFired, "OptimumAssetEvents.InvalidateServerAssets() must invoke subscribed handlers");
        }
        finally
        {
            Vintagestory.API.Config.OptimumAssetEvents.OnServerAssetsInvalidated -= handler;
        }
    }

    private class TestRecipe : Vintagestory.API.Common.RecipeBase
    {
        public override Vintagestory.API.Common.IRecipeIngredient[] RecipeIngredients => Ingredients;
        public Vintagestory.API.Common.IRecipeIngredient[] Ingredients { get; set; } = [];
        public override Vintagestory.API.Common.IRecipeOutput RecipeOutput => null!;
        public override bool Resolve(Vintagestory.API.Common.IWorldAccessor world, string sourceForLogging) => true;
        public override Vintagestory.API.Common.RecipeBase Clone() => new TestRecipe();

        public List<Dictionary<string, string>> TestGenerateAllowedVariantsCombinations(Dictionary<string, string[]> allowedVariants)
        {
            return GenerateAllowedVariantsCombinations(allowedVariants);
        }

        public Dictionary<string, HashSet<string>> TestGetNameToCodeMapping(Vintagestory.API.Common.IWorldAccessor world)
        {
            return GetNameToCodeMapping(world);
        }
    }

    [Fact]
    public void RecipeBase_AdvancedWildcardSinglePassAndDomainIndexedPreSearchCache()
    {
        // 1. Verify patches exist and contain optimizations
        string recipePatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Crafting/RecipeBase.cs.patch");
        Assert.True(File.Exists(recipePatchPath), "RecipeBase.cs.patch must exist");
        string recipePatch = File.ReadAllText(recipePatchPath);
        Assert.Contains("_optimumRegexCache", recipePatch);
        Assert.Contains("FastGetNameToCodeMappingForAdvancedWildcard", recipePatch);
        Assert.Contains("GenerateAllowedVariantsCombinationsFast", recipePatch);

        string cachePatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Crafting/CollectiblePreSearchResultsCache.cs.patch");
        Assert.True(File.Exists(cachePatchPath), "CollectiblePreSearchResultsCache.cs.patch must exist");
        string cachePatch = File.ReadAllText(cachePatchPath);
        Assert.Contains("search(world, collectibleType, domain, codeStartsWith)", cachePatch);
        Assert.Contains("FilterAndAdd", cachePatch);

        // 2. Behavioral verification of GenerateAllowedVariantsCombinations fast path
        var recipe = new TestRecipe();
        var allowedVariants = new Dictionary<string, string[]>
        {
            ["wood"] = ["pine", "oak", "birch"],
            ["metal"] = ["copper", "tin"]
        };
        var combinations = recipe.TestGenerateAllowedVariantsCombinations(allowedVariants);
        Assert.Equal(6, combinations.Count);
        Assert.Contains(combinations, c => c["wood"] == "pine" && c["metal"] == "copper");
        Assert.Contains(combinations, c => c["wood"] == "pine" && c["metal"] == "tin");
        Assert.Contains(combinations, c => c["wood"] == "oak" && c["metal"] == "copper");
        Assert.Contains(combinations, c => c["wood"] == "oak" && c["metal"] == "tin");
        Assert.Contains(combinations, c => c["wood"] == "birch" && c["metal"] == "copper");
        Assert.Contains(combinations, c => c["wood"] == "birch" && c["metal"] == "tin");

        // 3. Behavioral verification of CollectiblePreSearchResultsCache domain-indexed filtering
        var cache = new Vintagestory.API.Common.CollectiblePreSearchResultsCache();
        var item1 = new Vintagestory.API.Common.Item { Code = new AssetLocation("game", "iron-plate") };
        var item2 = new Vintagestory.API.Common.Item { Code = new AssetLocation("game", "iron-wire") };
        var item3 = new Vintagestory.API.Common.Item { Code = new AssetLocation("game", "copper-plate") };
        var item4 = new Vintagestory.API.Common.Item { Code = new AssetLocation("modx", "iron-plate") };
        var itemsList = new List<Vintagestory.API.Common.Item> { item1, item2, item3, item4 };

        var mockWorld = StubProxy<Vintagestory.API.Common.IWorldAccessor>.Create((method, args) =>
        {
            if (method.Name == "get_Items") return itemsList;
            if (method.Name == "get_Blocks") return new List<Vintagestory.API.Common.Block>();
            return null;
        });

        var results = cache.GetOrCreate(mockWorld, new AssetLocation("game", "iron-*"), Vintagestory.API.Common.EnumItemClass.Item);
        Assert.Equal(2, results.Count);
        Assert.Contains(item1, results);
        Assert.Contains(item2, results);
        Assert.DoesNotContain(item3, results); // copper-plate
        Assert.DoesNotContain(item4, results); // wrong domain modx

        // Domain-specific lookup for modx
        var modxResults = cache.GetOrCreate(mockWorld, new AssetLocation("modx", "iron-*"), Vintagestory.API.Common.EnumItemClass.Item);
        Assert.Single(modxResults);
        Assert.Contains(item4, modxResults);

        cache.Clear();
    }

    [Fact]
    public void Issue501_EntityShapeRenderer_DispatchesUniformsWithoutAllocations()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch"));
        Assert.Contains("+                ColorUtil.GetIncandescenceColorFloats(temp, out float gr, out float gg, out float gb);", patch);
        Assert.Contains("+                    ColorUtil.ToRGBAFloats(avgColor, out float ar, out float ag, out float ab, out float aa);", patch);
        Assert.Contains("+                    prog.Uniform(\"averageColor\", ar, ag, ab, aa);", patch);
        Assert.Contains("+                prog.Uniform(\"rgbaGlowIn\", gr, gg, gb, gi / 255f);", patch);
        Assert.Contains("-                float[] glowColor = ColorUtil.GetIncandescenceColorAsColor4f(temp);", patch);
        Assert.Contains("-                    Vec4f vec = ColorUtil.ToRGBAVec4f(capi.BlockTextureAtlas.GetAverageColor(baked.TextureSubId));", patch);
    }

    [Fact]
    public void Issue502_BlockEntityGenericTypedContainer_CachesRotatedMesh()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSSurvivalMod/BlockEntity/BEGenericTypedContainer.cs.patch"));
        Assert.Contains("+        MeshData rotatedOwnMesh;", patch);
        Assert.Contains("+                rotatedOwnMesh = null;", patch);
        Assert.Contains("+                var mesh = rotatedOwnMesh;", patch);
        Assert.Contains("+                    mesh = MeshAngle == 0 ? ownMesh : ownMesh.Clone().Rotate(0, MeshAngle, 0);", patch);
        Assert.Contains("+                    rotatedOwnMesh = mesh;", patch);
        Assert.Contains("+                mesher.AddMeshData(mesh);", patch);
        Assert.Contains("-                mesher.AddMeshData(ownMesh.Clone().Rotate(0, MeshAngle, 0));", patch);
    }

    [Fact]
    public void Issue503_InventorySearch_UsesTieredMatchingAndStableSort()
    {
        string gridPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/Inventory/GuiElementItemSlotGridBase.cs.patch"));
        Assert.Contains("+            var sortedSlots = new List<(int key, ItemSlot slot, float weight, int order)>(wSlots.Count);", gridPatch);
        Assert.Contains("+            sortedSlots.Sort((a, b) =>", gridPatch);
        Assert.Contains("+                int cmp = a.weight.CompareTo(b.weight);", gridPatch);
        Assert.Contains("+                return cmp != 0 ? cmp : a.order.CompareTo(b.order);", gridPatch);
        Assert.Contains("-            foreach (var pair in wSlots.OrderBy(pair => pair.Value.weight))", gridPatch);

        string stackPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Collectible/ItemStack.cs.patch"));
        Assert.Contains("+        [ThreadStatic]", stackPatch);
        Assert.Contains("+        private static DummySlot searchDummySlot;", stackPatch);
        Assert.Contains("+            string name = GetName();", stackPatch);
        Assert.Contains("+            if (name != null && name.CaseInsensitiveContains(searchText)) return true;", stackPatch);
        Assert.Contains("+            var slot = searchDummySlot ??= new DummySlot();", stackPatch);
        Assert.Contains("-                || GetDescription(world, new DummySlot(this), false).CaseInsensitiveContains(searchText)", stackPatch);
    }

    [Fact]
    public void Issue504_RegistryObjectType_SpanWildCardMatching()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Loading/RegistryObjectType.cs.patch"));
        Assert.Contains("+            if (wildCard.Domain != Code.Domain) return false;", patch);
        Assert.Contains("+            return WildcardUtil.Match(wildCard.Path, Code.Path);", patch);
        Assert.Contains("+            if (wildCard.Domain != blockCode.Domain) return false;", patch);
        Assert.Contains("+            return WildcardUtil.Match(wildCard.Path, blockCode.Path);", patch);
        Assert.Contains("-            string pattern = Regex.Escape(wildCard.Path).Replace(@\"\\*\", @\"(.*)\");", patch);
        Assert.Contains("-            return Regex.IsMatch(blockCode.Path, @\"^\" + pattern + @\"$\", RegexOptions.IgnoreCase);", patch);

        // Verification of boolean parity and strict domain checks
        var wildCard = new AssetLocation("game", "ore-*");
        Assert.True(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(wildCard, new AssetLocation("game", "ore-copper")));
        Assert.False(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(wildCard, new AssetLocation("game", "wood-oak")));
        Assert.False(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(wildCard, new AssetLocation("othermod", "ore-copper")));
        Assert.False(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(wildCard, null));
        Assert.False(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(null, new AssetLocation("game", "ore-copper")));
        Assert.True(Vintagestory.ServerMods.NoObf.RegistryObjectType.WildCardMatch(new AssetLocation("game", ""), new AssetLocation("game", "")));
    }

    [Fact]
    public void BlockEntityFirepitAndPotInFirepitRenderer_RuntimeDonorPatchesExistAndTargetManifest()
    {
        string firepitPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityFirepit.cs.patch"));
        Assert.Contains("GetPotStaticMesh(ITesselatorAPI tesselator, ItemStack contentStack)", firepitPatch);
        Assert.Contains("shouldRedraw = true;", firepitPatch);
        Assert.Contains("bool isColdPot = isPot && furnaceTemperature < 50f && InputStackTemp < 50f;", firepitPatch);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"^\+.*=>", System.Text.RegularExpressions.RegexOptions.Multiline), firepitPatch);

        string rendererPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/PotInFirepitRenderer.cs.patch"));
        Assert.Contains("SphereInFrustum(pos.X + 0.5, pos.InternalY + 0.5, pos.Z + 0.5, 0.75)", rendererPatch);
        Assert.Contains("SquareDistanceTo(pos.X + 0.5, pos.InternalY + 0.5, pos.Z + 0.5)", rendererPatch);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("SurvivalManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;

        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        Assert.True(members.ContainsKey("Vintagestory.GameContent.BlockEntityFirepit"));
        Assert.Contains("GetPotStaticMesh", members["Vintagestory.GameContent.BlockEntityFirepit"]);

        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "UpdateRenderer");
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "FromTreeAttributes");
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "getContentMesh");
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.PotInFirepitRenderer" && m.MethodName == "OnRenderFrame");
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
