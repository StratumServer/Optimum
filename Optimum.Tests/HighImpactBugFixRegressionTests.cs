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
        Assert.Contains("long maxUploadTicks = (System.Diagnostics.Stopwatch.Frequency * 3L) / 1000L;", patch);
        Assert.Contains("if (uploadedAny && (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStartTicks) >= maxUploadTicks)", patch);
        Assert.Contains("processPrioQueue = tessChunksQueuePriority.Count > 0;", patch);
        Assert.Contains("while (tessChunksQueue.Count > 0 && num2 < num3 && (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStartTicks) < maxUploadTicks)", patch);
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
        Assert.Contains("for (int i = 0; i < shadowRenderedEntities.Count; i++)", patch);
        Assert.Contains("entity.Pos.Dimension != dimension", patch);

        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"renderedEntities\",", programSource);
        Assert.Contains("\"shadowRenderedEntities\",", programSource);
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
        Assert.Contains("columnProbeCache.Clear();", runtimeAStar);
        Assert.Contains("columnProbeCache.TryGetValue(probeKey, out OptimumColumnProbeResult probe)", runtimeAStar);
        Assert.Contains("ResolveColumnProbe(node, stepHeight, maxFallHeight, entityCollBox);", runtimeAStar);
        Assert.Contains("fromDir.IsDiagonal", runtimeAStar);

        string sourceAStar = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/Astar/AStar.cs.patch"));
        Assert.Contains("public struct OptimumColumnProbeResult", sourceAStar);
        Assert.Contains("columnProbeCache.Clear();", sourceAStar);
        Assert.Contains("columnProbeCache.TryGetValue(probeKey, out OptimumColumnProbeResult probe)", sourceAStar);
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
    public void GuiCompositeSettings_RelocatesRenderScaleAndChiselLodToGraphicsTab()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));

        // Verify Chisel LOD and Render Scale controls are added to Graphics tab
        Assert.Contains("\"optChiselLod\"", patch);
        Assert.Contains("\"optChiselLodDist\"", patch);
        Assert.Contains("\"optRenderScale\"", patch);
        Assert.Contains("composer.GetDropDown(\"optRenderScale\")", patch);

        // Verify SetGfxValues initializes all three controls
        Assert.Contains("composer.GetSwitch(\"optChiselLod\")?.SetValue(ClientSettings.OptimumChiselLod);", patch);
        Assert.Contains("composer.GetSlider(\"optChiselLodDist\")?.SetValues(ClientSettings.OptimumChiselLodDistance", patch);

        // Verify removed from Optimum tab rows
        Assert.DoesNotContain("+		AddOptimumSwitchRow(\"optimum-chisellod\"", patch);
        Assert.DoesNotContain("+		AddOptimumDropdownRow(\"optimum-renderscale\"", patch);

        // Verify wired as Cecil transplant targets in Program.cs
        string programSource = File.ReadAllText(
            PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Client.NoObf.GuiCompositeSettings\", \"OnGraphicsOptions\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.GuiCompositeSettings\", \"SetGfxValues\", 0)", programSource);
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
        Assert.Contains("composer.GetSlider(\"optFsrSharpness\")?.SetValues", guiPatch);

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
        Assert.Contains("protected SimpleParticleProperties reusableSpawnProps = new SimpleParticleProperties();", managerPatch);
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
