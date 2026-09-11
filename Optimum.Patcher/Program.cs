using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Optimum.Patcher;

// Verification mode for check-vanilla-compat.sh: report decompiler
// type-misbinding artifacts (same-named cast target bound to the wrong
// namespace, the EventHelper System.Func/API Func class of bug).
if (args.Length == 3 && args[0] == "--compare-casts")
{
    if (!File.Exists(args[1])) { Console.Error.WriteLine($"Not found: {args[1]}"); return 1; }
    if (!File.Exists(args[2])) { Console.Error.WriteLine($"Not found: {args[2]}"); return 1; }
    var divergences = CastComparer.Compare(args[1], args[2]);
    foreach (var d in divergences)
        Console.Error.WriteLine($"  CAST DIVERGENCE: {d}");
    Console.WriteLine($"{divergences.Count} cast divergence(s) between {Path.GetFileName(args[1])} and {Path.GetFileName(args[2])}");
    return divergences.Count == 0 ? 0 : 1;
}

if (args.Length == 4 && args[0] == "--api")
{
    return ApiPatcher.Patch(args[1], args[2], args[3]) ? 0 : 1;
}

if (args.Length == 5 && args[0] == "--mod")
{
    return ModPatcher.Patch(args[1], args[2], args[3], args[4]) ? 0 : 1;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Optimum.Patcher <vanilla.dll> <compiled.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --compare-casts <vanilla.dll> <compiled.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --api <vanilla.dll> <contracts.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --mod <name> <vanilla.dll> <donor.dll> <output.dll>");
    return 1;
}

string vanillaPath = args[0];
string compiledPath = args[1];
string outputPath = args[2];

if (!File.Exists(vanillaPath)) { Console.Error.WriteLine($"Not found: {vanillaPath}"); return 1; }
if (!File.Exists(compiledPath)) { Console.Error.WriteLine($"Not found: {compiledPath}"); return 1; }

Console.WriteLine($"Patching {Path.GetFileName(vanillaPath)}...");

// --- Phase 2a: Types to inject ---
var typesToInject = new List<string>
{
    "Optimum.OptimumInfo",
    "Optimum.OptimumUpdateChecker",
    "Optimum.EntityLightBatchBuffer",
    "Optimum.OptimumOptiTimeGuard",
    "Vintagestory.Client.NoObf.OptimumGreedyMeshEmitter",
    // Server-side worldgen scheduler + chunk read pool (see
    // docs/implementation-plans/server-worldgen-chunk-pool-cecil-wiring-plan-2026-08-11.md):
    // parallel SQLite read pool used by ChunkServerThread/ServerSystemSupplyChunks.
    "Vintagestory.Server.OptimumChunkReadPool",
};

// --- Phase 2b: Members to inject into existing types ---
var membersToInject = new Dictionary<string, List<string>>
{
    ["Vintagestory.Client.NoObf.ClientSettings"] = new()
    {
        "OptimumEntityShadowCull",
        "OptimumShadowCullDistance",
        "OptimumDynamicLightScale",
        "OptimumBackgroundFpsLimit",
        "OptimumPreciseFramePacing",
        "OptimumRepulsionGate",
        "OptimumRepulsionDistance",
        "OptimumAnimBlockLod",
        "OptimumShadowFarVegetation",
        "OptimumWeatherWindThrottle",
        "OptimumParticleDistanceGate",
        "OptimumChiselLod",
        "OptimumChiselLodDistance",
        "OptimumDynamicLightCache",
        "OptimumRenderScale",
    },
    // TAA P4: the cube-particle motion writer's previous-frame uniforms.
    ["Vintagestory.Client.NoObf.SystemRenderParticles"] = new()
    {
        "SetOptimumMotionUniforms",
    },
    // TAA P4: the decal motion writer's previous-frame uniforms.
    ["Vintagestory.Client.NoObf.SystemRenderDecals"] = new()
    {
        "SetOptimumMotionUniforms",
    },
    ["Vintagestory.Client.NoObf.SystemRenderPlayerEffects"] = new()
    {
        "GetOptimumLightRadius",
        "HasLight",
    },
    ["Vintagestory.Client.NoObf.SystemRenderEntities"] = new()
    {
        "ShadowCullDistanceSq",
        "OptimumEntityLightBatchSize",
        "OptimumEntityLightMinimumSamples",
        "optimumEntityLightBatchBuffer",
        "optimumEntityLightPreviousSampleCount",
        "optimumEntityLightBatchDisabled",
        "optimumEntityShaderCacheDisabled",
        "optimumEntityShaderFailureLogged",
        "PublishOptimumEntityLightSample",
        "PrepareOptimumEntityLights",
        "BeginOptimumEntityShaderSegment",
        "EndOptimumEntityShaderSegment",
        // TAA review fix: per-renderer motion-window gate.
        "optimumMotionWriterTypes",
        "OptimumIsMotionWriter",
    },
    ["Vintagestory.Client.NoObf.ClientChunk"] = new()
    {
        "OptimumReadLightBatch",
    },
    ["Vintagestory.Client.NoObf.ClientPlatformWindows"] = new()
    {
        "_optimumSettingsInitialized",
        "EnsureOptimumDefaults",
        "_optimumTimerResolutionRaised",
        "OptimumTimeBeginPeriod",
        "OptimumTimeEndPeriod",
        "EnsureOptimumTimerResolution",
        "OptimumOnProcessExit",
        "_optimumFocusLostStopwatch",
        "optimumFsrDisabled",
        "DisableOptimumFsr",
        // Vulkan backend: the device-path framebuffer setup and its helpers.
        "SetupOptimumFrameBuffers",
        "CreateOptimumColorTarget",
        "SetupOptimumTextureSampler",
        "CreateOptimumDepthTarget",
        "CreateOptimumPlaceholderTarget",
        "CreateOptimumFramebuffer",
        // Vulkan backend: GL state the device takes as call arguments instead,
        // so the routed bodies need somewhere to remember it.
        "optimumClearR",
        "optimumClearG",
        "optimumClearB",
        "optimumClearA",
        "optimumBoundTexture2d",
        "optimumScissorEnabled",
        // TAA: motion attachment, history/aux/prev-depth targets, and the
        // debug-view blit path (P1).
        "OptimumTaaHistoryIndexA",
        "OptimumTaaHistoryIndexB",
        "OptimumGlR32f",
        "MotionAttachmentIndex",
        "TaaTargetsReady",
        "optimumTaaDisabled",
        "TaaHistory",
        "CreateOptimumHistoryTarget",
        "CreateOptimumHistoryTargetGl",
        "DisableOptimumTaa",
        "optimumTaaShaderReloadPending",
        "OptimumRunPendingTaaShaderReload",
        "_taaFrameParity",
        "_taaHistoryValid",
        "taaResolvedColorTexture",
        "taaResolvedGlowTexture",
        "TaaResolvedThisFrame",
        "RenderOptimumTaaResolve",
        // TAA P3: the motion-attachment draw-buffer window the terrain (and
        // later entity/standard/instanced) writers open around their draws.
        "OptimumMotionWriteActive",
        "BeginMotionWrite",
        "EndMotionWrite",
        "ApplyOptimumMotionBlendState",
        "InstallOptimumMotionWriteHooks",
        "optimumMotionDrawBuffersOn",
        "optimumMotionDrawBuffersOff",
        // TAA P4: the motion-only window the liquid velocity pass opens - the
        // motion attachment alone, every other colour attachment masked out.
        "BeginMotionOnlyWrite",
        "EndMotionOnlyWrite",
        "optimumMotionOnlyDrawBuffers",
        // TAA P4: additive blending on the motion attachment for the OIT merge,
        // which contributes the transparent layer's coverage to the reactive
        // channel without touching the vector or the writer depth under it.
        "ApplyOptimumMotionAccumulateBlendState",
        // TAA P4: the sky / volumetric-cloud motion and reactive pass and the
        // reactive constant it stamps.
        "RenderOptimumSkyMotion",
        "OptimumCloudReactive",
        // TAA P5: the post-resolve sharpen pass, its dedicated target slot and
        // the shared "is FSR's RCAS going to run this frame" test the pass and
        // BlitPrimaryToDefault both ask so the two never sharpen the same
        // pixels twice.
        "OptimumTaaSharpenIndex",
        "OptimumFsrBlitActive",
        "RenderOptimumTaaSharpen",
    },
    // TAA P3: the uniform block a buffer feeds and the point it is bound to.
    // Vanilla had one block per program and Bind() hard-coded binding point 0;
    // the entity motion writer adds a second ("AnimationPrev") beside it, and
    // Update routes the bone upload through OptimumEntityMotion by block name.
    ["Vintagestory.Client.NoObf.UBO"] = new()
    {
        "BlockName",
        "BindingPoint",
    },
    ["Vintagestory.Client.NoObf.ShaderPrograms"] = new()
    {
        "FsrEasu",
        "FsrRcas",
        "TaaDebug",
        "TaaResolve",
        // TAA P5: the post-resolve sharpen pass program.
        "TaaSharpen",
        // TAA P4: the liquid velocity pass program.
        "ChunkLiquidMotion",
        // TAA P4: the sky / volumetric-cloud motion pass program.
        "TaaSkyMotion",
    },
    ["Vintagestory.Client.NoObf.ShaderRegistry"] = new()
    {
        "RegisterOptimumShaderProgram",
        // TAA: shared per-program post-compile handling extracted out of
        // loadRegisteredShaderPrograms (both the parallel-preprocess and
        // vanilla single-threaded paths call it); treats taa-debug as
        // optional exactly like the two FSR programs.
        "CompileAndTrackShaderProgram",
        // TAA P5 review: the terrain sampler objects' LOD bias, reachable from
        // ChunkRenderer so the TAA mip-bias row applies without a shader reload.
        // A bound sampler object overrides the atlas texture parameter, so this
        // is the only place chunkopaque/chunktopsoil mip selection changes.
        "ApplyOptimumTerrainSamplerLodBias",
        "ApplyOptimumSamplerLodBias",
    },
    ["Vintagestory.Client.NoObf.SystemRenderOITLayers"] = new()
    {
        "optimumOitDisabled",
        "optimumOitFailureLogged",
        "RestoreVanillaTransparentState",
        "DisableOptimumOit",
    },
    // Vulkan backend: the shared sampling setup for the two OIT targets.
    ["Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT"] = new()
    {
        "SetOptimumOitSampling",
    },
    // Vulkan backend: shadow maps are sampled as plain depth by the debug
    // overlay, which means toggling the compare mode off and back on.
    ["Vintagestory.Client.NoObf.SystemRenderFrameBufferDebug"] = new()
    {
        "SetOptimumDepthCompare",
    },
    // Settings tab: inject the field, callbacks, and hook helper
    ["Vintagestory.Client.NoObf.GuiCompositeSettings"] = new()
    {
        "oButtonBounds",
        "OnOptimumOptions",
        "_AddOptimumTab",
        "onOptimumBackgroundFpsChanged",
        "onOptimumFramePacingChanged",
        "onOptimumShadowCullChanged",
        "onOptimumShadowDistChanged",
        "onOptimumRepulsionChanged",
        "onOptimumRepulsionDistChanged",
        "onOptimumDynLightChanged",
        "onOptimumAnimBlockLodChanged",
        "onOptimumShadowFarVegChanged",
        "onOptimumWeatherWindChanged",
        "onOptimumParticleDistChanged",
        "onOptimumChiselLodChanged",
        "onOptimumChiselLodDistChanged",
        "onOptimumOcclusionScaleChanged",
        "onOptimumDynLightCacheChanged",
        "onOptimumEntityLightBatchChanged",
        "onOptimumEntityShaderCacheChanged",
        "onOptimumRenderScaleChanged",
        "onOptimumGodRaysCapChanged",
        "onOptimumTaaChanged",
        "onOptimumTaaSharpnessChanged",
        "onOptimumTaaMipBiasChanged",
#if OPTIMUM_GREEDY_MESH
        "onOptimumGreedyMeshChanged",
        "onOptimumGreedySpanChanged",
        "onOptimumGreedyLightTolChanged",
        "onOptimumGreedyFarDistChanged",
#endif
    },
    // GuiManager: reusable scratch buffers replacing per-call .ToList() snapshots
    ["Vintagestory.Client.NoObf.GuiManager"] = new()
    {
        "_scratchBlockTexturesLoaded",
        "_scratchLevelFinalize",
        "_scratchOwnPlayerData",
        "_scratchFinalizeFrame",
        "_scratchKeyDownOpened",
        "_scratchKeyUp",
        "_scratchKeyPress",
        "_scratchMouseDown",
        "_scratchMouseUp",
        "_scratchMouseMove",
    },
    // AmbientManager: reusable scratch buffers replacing per-frame array/BlockPos allocations
    ["Vintagestory.Client.NoObf.AmbientManager"] = new()
    {
        "_updateAmbientFogColorScratch",
        "_updateAmbientAmbientColorScratch",
        "_waterColorHsvScratch",
        "_waterColorRgbScratch",
        "_daylightBlockPosScratch",
        "_colorGradingBlockPosScratch",
    },
    // SystemRenderSkyColor: reusable scratch vectors replacing per-frame Vec3f allocations
    ["Vintagestory.Client.NoObf.SystemRenderSkyColor"] = new()
    {
        "_scratchViewVector",
        "_scratchPlayerPos",
    },
    // ChunkRenderer: scratch chunk-origin vector and pool-location lists reused per chunk upload
    ["Vintagestory.Client.NoObf.ChunkRenderer"] = new()
    {
        "chunkOriginScratch",
        "centerPoolLocationsScratch",
        "edgePoolLocationsScratch",
        "optimumTextureLodBias",
        "ApplyOptimumTextureLodBias",
        "SetOptimumTextureLodBias",
        // TAA P3: previous-frame transforms for the terrain motion writers.
        "SetOptimumMotionUniforms",
        // TAA P4: the liquid velocity pass and its reactive constant.
        "RenderLiquidMotion",
        "OptimumLiquidReactive",
    },
    // ChunkTesselatorManager: skip RecalcPriority+Sort when the player hasn't moved
    // (_lastSortPlayerPos/_lastSortYaw), plus the multi-tesselator worker pool and
    // upload-handoff backpressure fields the wiring plan ships alongside them.
    ["Vintagestory.Client.NoObf.ChunkTesselatorManager"] = new()
    {
        "_lastSortPlayerPos",
        "_lastSortYaw",
        "tesselators",
        "uploadHandoff",
        "PrimaryTesselator",
        "_optimumRecalcPriority",
        "OptimumRecalcPriority",
    },
    // ChunkTesselator: chisel LOD pool routing helpers and fields
    ["Vintagestory.Client.NoObf.ChunkTesselator"] = new()
    {
        "currentOptimumChiselModeldataByRenderPassByLodLevel",
        "centerOptimumChiselModeldataByRenderPassByLodLevel",
        "edgeOptimumChiselModeldataByRenderPassByLodLevel",
        "MergeTesselatedChunkParts",
        "populateTesselatedChunkPart",
    },
    // TesselatedChunkPart: carry chisel LOD distance choice into pool locations
    ["Vintagestory.Client.NoObf.TesselatedChunkPart"] = new()
    {
        "optimumUseChiselLodDistance",
    },
    // ICoreClientAPI.IsTesselationThread implementations (patches/VintagestoryApi/Client/API/
    // ICoreClientAPI.cs.patch adds the interface member; every implementer needs a body or the
    // type fails to load at runtime - this is what crashed the server at GameReady, T-TypeLoad
    // on ClientCoreAPI, caught by scripts/tests/run-server-smoke.sh). ClientCoreAPI forwards to
    // ClientMain, which needs the field its body reads. Now that ClientMain::Start (below) is
    // transplanted and registers the tesselation worker thread, this returns true on that
    // thread instead of always false - flips VSSurvivalMod/Systems/Cooking/MealMeshCache.cs's
    // guard from "build the meal mesh inline" to "defer to the main thread", the intended fix.
    ["Vintagestory.Client.NoObf.ClientCoreAPI"] = new()
    {
        "IsTesselationThread",
    },
    ["Vintagestory.Client.Gui.MainMenuAPI"] = new()
    {
        "IsTesselationThread",
    },
    ["Vintagestory.Client.NoObf.ClientMain"] = new()
    {
        "tesselationWorkers",
        "IsTesselationThread",
        "RegisterTesselationThread",
        "GetTesselationWorkerSlot",
        "ChunkTesselatorManager",
        // TAA P1: unjittered projection companion to CurrentProjectionMatrix
        // (new property; the jittered getter itself is an existing transplant
        // target below).
        "CurrentProjectionMatrixUnjittered",
        // TAA P5: OPTIMUM_FPS_LOG per-second frame-time line, read by
        // scripts/dev/perf-capture.sh. Called from MainRenderLoop (a transplant
        // target below); inert unless the env var names a file.
        "optimumFpsLogPath",
        "optimumFpsLogResolved",
        "optimumFpsLogSamples",
        "optimumFpsLogFrames",
        "optimumFpsLogSeconds",
        "OptimumLogFrameTime",
    },
    ["Vintagestory.Client.NoObf.RenderAPIGame"] = new()
    {
        "CurrentProjectionMatrixUnjittered",
        "TemporalContext",
    },

    // Load-bearing dependency, wire before ServerSystemSupplyChunks: dispatchClaim's
    // field initializer only runs once .ctor is a transplant target (see targets below).
    ["Vintagestory.Server.ChunkColumnLoadRequest"] = new()
    {
        "dispatchClaim",
        "TryClaimDispatch",
        "ReleaseDispatch",
    },
    // Load-bearing dependency, wire before ServerSystemSupplyChunks/ServerSystemLoadAndSaveGame:
    // both reference chunkthread.optimumReadPool.
    ["Vintagestory.Server.ChunkServerThread"] = new()
    {
        "optimumReadPool",
        "optimumWorldgenFootprints",
        "TryAcquireOptimumWorldgenFootprint",
    },
    ["Vintagestory.Server.ServerSystemSupplyChunks"] = new()
    {
        "optimumWorldgenPassCaps",
        "optimumWorldgenPassInFlight",
        "optimumGeneratorTypeLocks",
        "optimumGeneratorMethodLocks",
        "optimumGeneratorLockRegistry",
        "optimumNextStage",
        "optimumWorldgenFaulted",
        "optimumWorldgenAuditReady",
        "optimumWorldgenWorkersStarted",
        "optimumWorldgenDispatchesInFlight",
        "optimumLightConflictLock",
        "optimumAdaptiveController",
        "optimumAdaptiveRadiusController",
        "optimumPostSpawnRaised",
        "optimumSafetyChecked",
        "optimumWorkerIndex",
        "CheckWorldgenConcurrencySafety",
        "OptimumInitializeWorldgenWorkers",
        "IsWorldgenHandlerAllowedForParallelPass",
        "IsWorldgenHandlerDirectlySafe",
        "GetWorldgenGeneratorLock",
        "RunWorldgenGenerator",
        "RunWorldgenGeneratorsUnlocked",
        "TryClaimWorldgenPass",
        "ReleaseWorldgenPass",
        "OptimumWorldgenWorkersActive",
        "OptimumWorldgenWorkersDrained",
    },
    ["Vintagestory.Server.ServerSystemLoadAndSaveGame"] = new()
    {
        "optimumSaveBatch",
        "TryStartOptimumChunkReadPool",
        "DisposeOptimumChunkReadPool",
    },
    ["Vintagestory.Server.ServerSystemBlockSimulation"] = new()
    {
        "optimumPosPool",
        "optimumPosPoolIndex",
        "optimumFluidPosPool",
        "optimumFluidPosPoolIndex",
        "optimumTickSliceIndex",
        "OptimumGetPooledPos",
        "OptimumGetPooledFluidPos",
    },
    ["Vintagestory.Server.ServerSystemUnloadChunks"] = new()
    {
        "optimumUnloadCandidateSet",
        "optimumOutOfRangeList",
        "optimumInRangeSet",
        "optimumUnloadGenRequests",
        "optimumUnloadGenChunks",
        "optimumUnloadGenMapChunks",
        "optimumUnloadGenLeases",
    },
    ["Vintagestory.Server.ServerSystemCompressChunks"] = new()
    {
        "optimumPlayerChunkPositions",
    },
    ["Vintagestory.Server.ServerSystemNotifyPing"] = new()
    {
        "optimumPingTimeouts",
    },
    ["Vintagestory.Server.ServerSystemRelight"] = new()
    {
        "optimumLightingDirtyChunks",
    },
    ["Vintagestory.Common.GameDatabase"] = new()
    {
        "GetChunk",
    },
    ["Vintagestory.Common.EventManager"] = new()
    {
        "singleDelayedCallbackBlockKeys",
        "optimumCachedClimateDelegate",
        "optimumCachedClimateInvocations",
        "optimumCachedWindDelegate",
        "optimumCachedWindInvocations",
    },
    // ServerPackets.cs.patch: 14th file of cecil-owned.list's server section,
    // discovered during the wiring plan's triage but not audited there. Verified
    // separately: zero <>c references in either target method (the class-level
    // <>c is from an unrelated, untouched method), so no Gap A/B risk. Both new
    // fields are [ThreadStatic] - exercises the Gap A fix (CustomAttributes
    // copy) from the same pass.
    ["Vintagestory.Server.ServerPackets"] = new()
    {
        "t_bulkEntityAttributes",
        "t_bulkEntityAttributesWrapper",
        "t_bulkEntityDebugAttributes",
        "t_bulkEntityDebugAttributesWrapper",
    },
    // HandlePlayerIdentification/CreatePacketIdentification (a version-string bump
    // each) and Launch() (needs PlayerAntiAbuseMonitor, itself gated on ServerConfig's
    // still-unshipped .ctor, see the wiring plan doc) are deliberately not wired.
    ["Vintagestory.Server.ServerMain"] = new()
    {
        "optimumCachedOnlinePlayers",
        "optimumCachedOnlinePlayersTick",
        "optimumCachedAllPlayers",
        "optimumCachedAllPlayersTick",
        "OptimumShouldSkipClient",
    },
};

// --- Phase 2c: existing vanilla fields to retype in place (object -> Lock) ---
// Worker-pool wiring plan Step 8: the object-to-Lock retype capability
// (MemberInjector.RetypeFields/ILPatcher.RetargetFieldInitializers/
// RetypedFieldReaderVerifier) unlocks the 3 dirtyChunks*Lock fields and the
// MainThreadTasksLock field. Retyping them requires transplanting every reader
// whose donor body emits Lock.EnterScope() instead of Monitor.Enter/Exit.
// ChunkTesselatorManager::OnSeperateThreadGameTick is already a worker-pool
// target and needs no extra listing.
var fieldsToRetype = new Dictionary<string, List<string>>
{
    ["Vintagestory.Client.NoObf.TextureAtlasManager"] = new()
    {
        "atlasCreationQueued",
    },
    ["Vintagestory.Client.NoObf.ClientMain"] = new()
    {
        "dirtyChunksLock",
        "dirtyChunksPriorityLock",
        "dirtyChunksLastLock",
        "MainThreadTasksLock",
    },
};

// --- Phase 1: Method bodies to transplant ---
var targets = new List<MethodTarget>
{
    // Entity render distance cull + shadow cull (reads injected ClientSettings.Optimum* props)
    new("Vintagestory.Client.NoObf.SystemRenderEntities", "OnRenderOpaque3D", 1),
    new("Vintagestory.Client.NoObf.SystemRenderEntities", "OnBeforeRender", 1),
    // Entity shadow cull: the actual distance gate lives here, not OnBeforeRender.
    // Found missing from this list while wiring diagnostics counters (2026-07-03);
    // ShadowCullDistanceSq was injected but nothing ever called it.
    new("Vintagestory.Client.NoObf.SystemRenderEntities", "OnRenderFrameShadows", 1),
    // HudEntityNameTags: IsRendered reuse (vanilla fields only)
    new("Vintagestory.Client.NoObf.HudEntityNameTags", "OnRenderGUI", 1),
    // ChunkRenderer: shadow far vegetation skip (reads injected OptimumShadowFarVegetation)
    new("Vintagestory.Client.NoObf.ChunkRenderer", "RenderOpaque", 1),
    // FSR mip bias: refresh block atlas texture state after scale or atlas changes.
    new("Vintagestory.Client.NoObf.ChunkRenderer", "OnBeforeRenderOpaque", 1),
    new("Vintagestory.Client.NoObf.ChunkRenderer", "RuntimeAddBlockTextureAtlas", 1),
    // TAA P3: terrain motion-vector writers - the opaque pass, the AfterOIT
    // terrain overlay (pass 7) and the LiquidDepth prepass comment that records
    // why it stays jittered but writes no motion.
    new("Vintagestory.Client.NoObf.ChunkRenderer", "RenderAfterOIT", 1),
    new("Vintagestory.Client.NoObf.ChunkRenderer", "OnRenderBefore", 1),
    // TAA P1: temporal frame contract - Advance()/JitterActive wiring in the
    // render loop, the jittered projection getter, its capture at both
    // Set3DProjection call sites, and the resets (FOV change, resize, world
    // load already listed below as Start, shader reload).
    new("Vintagestory.Client.NoObf.ClientMain", "MainRenderLoop", 1),
    new("Vintagestory.Client.NoObf.ClientMain", "Set3DProjection", 2),
    new("Vintagestory.Client.NoObf.ClientMain", "get_CurrentProjectionMatrix", 0),
    new("Vintagestory.Client.NoObf.ClientMain", "OnFowChanged", 1),
    new("Vintagestory.Client.NoObf.ClientMain", "OnResize", 0),
    new("Vintagestory.Client.NoObf.ClientMain", "RenderAfterPostProcessing", 1),
    new("Vintagestory.Client.NoObf.ClientEventManager", "TriggerReloadShaders", 0),
    // ClientMain: mouse wheel fix (vanilla fields only)
    new("Vintagestory.Client.NoObf.ClientMain", "OnMouseWheel", 1),
    // ClientMain: single-pass OpenedGuis scan instead of two LINQ calls (vanilla fields only)
    new("Vintagestory.Client.NoObf.ClientMain", "UpdateFreeMouse", 0),
    // ClientSystemStartup: atlas pipeline per-stage timing instrumentation.
    new("Vintagestory.Client.NoObf.ClientSystemStartup", "FinaliseTextureAtlas", 3),
    new("Vintagestory.Client.NoObf.ClientSystemStartup", "FinaliseTextureAtlas_StageB", 2),
    new("Vintagestory.Client.NoObf.ClientSystemStartup", "FinaliseTextureAtlas_StageC", 3),
    // ClientMain: measure the existing one-launch-task-per-frame policy before
    // any pacing change. The donor keeps the vanilla queue order and fallback.
    new("Vintagestory.Client.NoObf.ClientMain", "ExecuteMainThreadTasks", 1),
    new("Vintagestory.Client.NoObf.ClientMain", "requeueTasks", 0),
    new("Vintagestory.Client.NoObf.ClientMain", "EnqueueMainThreadTask", 2),
    // ClientMain::Start: transplanted together with the rest of the tesselation
    // worker pool below - constructs ChunkTesselatorManager, assigns
    // TerrainChunkTesselator from it (Option B, a real field again), and registers
    // the tesselation worker thread. See
    // docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md.
    new("Vintagestory.Client.NoObf.ClientMain", "Start", 0),
    // SystemRenderPlayerEffects: dynamic light radius (lambda-free rewrite)
    new("Vintagestory.Client.NoObf.SystemRenderPlayerEffects", "onBeforeRender", 1),
    // ClientPlatformWindows: persistent mapped VBO and index uploads. ParameterTypes
    // disambiguates the five updateVAO overloads so the bulk-copy bodies reach the
    // shipped vanilla DLL instead of remaining only in the donor assembly.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateVAO", 6,
        new[] { "System.Single[]", "System.Int32", "System.Int32", "System.Int32", "System.IntPtr", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateVAO", 6,
        new[] { "System.Int32[]", "System.Int32", "System.Int32", "System.Int32", "System.IntPtr", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateVAO", 6,
        new[] { "System.Int16[]", "System.Int32", "System.Int32", "System.Int32", "System.IntPtr", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateVAO", 6,
        new[] { "System.UInt16[]", "System.Int32", "System.Int32", "System.Int32", "System.IntPtr", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateVAO", 6,
        new[] { "System.Byte[]", "System.Int32", "System.Int32", "System.Int32", "System.IntPtr", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "updateIndices", 5,
        new[] { "System.Int32[]", "System.Int32", "System.Int32", "Vintagestory.Client.NoObf.VAO", "System.Boolean" }),
    // ClientPlatformWindows: frame pacing + background FPS (inline in window_RenderFrame, no lambdas)
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "window_RenderFrame", 1),
    // The shared index buffer is freed at shutdown, after the GL binding is gone
    // on the device path; the raw call throws there instead of freeing it.
    new("Vintagestory.Client.NoObf.ClientPlatformAbstract", "DisposeIndexBuffer", 0),
    // FSR: allocate the native intermediate and replace the final bilinear blit.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "SetupDefaultFrameBuffers", 0),
    // TAA P2: a framebuffer rebuild throws the history away - the flag and the
    // temporal contract's reset reason are both set where the swap completes.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RebuildFrameBuffers", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "BlitPrimaryToDefault", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "DisableOptimumFsr", 1),
    // R4: pass the configured god-rays sample limit to the post-process shader.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderPostprocessingEffects", 1),
    // Vulkan backend: fixed-function state routes to OptimumRender.Device when a
    // device is installed, and runs the untouched vanilla GL body when it is not.
    // See VULKAN-BACKEND-PLAN.md section 3.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GLWireframes", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlViewport", 4),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlScissor", 4),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlScissorFlag", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlEnableDepthTest", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlDisableDepthTest", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlToggleBlend", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlDisableCullFace", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlEnableCullFace", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GLLineWidth", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlDepthMask", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlDepthFunc", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlCullFaceBack", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlCullFaceFront", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlEnableStencilTest", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlDisableStencilTest", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlStencilMask", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlStencilFunc", 3),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlStencilOp", 3),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlColorMask", 4),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlClearStencil", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GetGLShaderVersionString", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GenSampler", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "BindTexture2d", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "BindTextureCubeMap", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GLDeleteTexture", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlGetMaxTextureSize", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GetGraphicsCardRenderer", 0),
    // Vulkan backend: shader staging and linking. CompileShader only stages a
    // stage on the device path, because GL resolves uniforms and varyings by name
    // across the whole program and nothing is final until link time.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GetUniformLocation", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CompileShader", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CreateShaderProgram", 1),
    // Vulkan backend: the mod-facing uniform and texture-binding surface. A
    // uniform location here is a byte offset into the generated block rather than
    // a GL location, which callers never see.
    // Uniform has seven two-parameter overloads, so each needs its signature.
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "System.Single" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "System.Int32" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "Vintagestory.API.MathTools.Vec2f" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "Vintagestory.API.MathTools.Vec2i" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "Vintagestory.API.MathTools.Vec3f" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "Vintagestory.API.MathTools.Vec3i" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 2,
        new[] { "System.String", "Vintagestory.API.MathTools.Vec4f" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 3,
        new[] { "System.String", "System.Int32", "System.Single[]" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 3,
        new[] { "System.String", "System.Single", "System.Single" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 4,
        new[] { "System.String", "System.Single", "System.Single", "System.Single" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniform", 5,
        new[] { "System.String", "System.Single", "System.Single", "System.Single", "System.Single" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniforms2", 3),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniforms3", 3),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Uniforms4", 3),
    // UniformMatrix has a float[] and a by-ref Matrix4 overload.
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "UniformMatrix", 2,
        new[] { "System.String", "System.Single[]" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "UniformMatrix", 2,
        new[] { "System.String", "OpenTK.Mathematics.Matrix4&" }),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "UniformMatrices", 3),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "UniformMatrices4x3", 3),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "BindTexture2D", 3),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "BindTextureCube", 3),
    // Vulkan backend: program lifecycle. ProgramId is the device's handle.
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Use", 0),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Stop", 0),
    new("Vintagestory.Client.NoObf.ShaderProgramBase", "Dispose", 0),
    // Vulkan backend: mesh allocation, upload and draw. VAO.VaoId carries the
    // device's mesh handle so MeshRef, which mods hold, stays unchanged.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderMesh", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderFullscreenTriangle", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderMesh", 5),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderMeshInstanced", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "UploadMesh", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "UpdateMesh", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "DeleteMesh", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "AllocateEmptyMesh", 12),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "AllocateEmptySSBOMesh", 12),
    new("Vintagestory.Client.NoObf.VAO", "Dispose", 0),
    // Vulkan backend: the window's own clear-and-swap has no GL binding to call
    // when the window was opened with no graphics API.
    new("Vintagestory.Client.NoObf.GameWindowNative", ".ctor", 2),
    // Vulkan backend: framebuffer binding, lifecycle and per-pass state. The
    // two CurrentFrameBuffer properties bind on assignment, so their setters
    // are the seam for every render target the client selects.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "set_CurrentFrameBuffer", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "set_CurrentFrameBufferKeepVw", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "set_GlDebugMode", 1),
    // The scissor flag is read back by the runtime atlas upload; the device
    // keeps no queryable state, so the routed setter remembers it.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "get_GlScissorFlagEnabled", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CreateFramebuffer", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "DisposeFrameBuffer", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "DisposeFrameBuffers", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "ClearFrameBuffer", 4),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "ClearFrameBuffer", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadFrameBuffer", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadFrameBuffer", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "UnloadFrameBuffer", 1,
        new[] { "Vintagestory.API.Client.EnumFrameBuffer" }),
    // Vulkan backend: startup capability reporting, which cannot ask GL.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LogAndTestHardwareInfosStage2", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GetGraphicCardInfos", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "Start", 0),
    // Vulkan backend: error reporting comes from the validation layer.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CheckGlError", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CheckGlErrorAlways", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlGetError", 0),
    // Vulkan backend: texture creation, upload and mipmapping.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadCairoTexture", 2),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadOrUpdateCairoTexture", 3),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GenTexture", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadIntoTexture", 5),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadTexture", 4,
        new[] { "Vintagestory.API.Common.IBitmap", "System.Boolean", "System.Int32", "System.Boolean" }),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "BuildMipMaps", 1),
    // The texture atlas upload path. Private, so it only reaches the shipped
    // assembly as an explicit target - its three public wrappers delegate here
    // and carry no GL of their own, which is how it was missed: nothing on the
    // menu reaches it, and TextureAtlas.Upload only runs once a world loads.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "LoadOrUpdateTextureFromPixels", 6),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "Load3DTextureCube", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlGenerateTex2DMipmaps", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "UnBindTextureCubeMap", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GlClearColorRgbaf", 4),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "SmoothLines", 1),
    // Vulkan backend: uniform buffers, whose handles UBO carries across.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "CreateUBO", 4),
    new("Vintagestory.Client.NoObf.UBO", "Bind", 0),
    new("Vintagestory.Client.NoObf.UBO", "Unbind", 0),
    new("Vintagestory.Client.NoObf.UBO", "Dispose", 0),
    new("Vintagestory.Client.NoObf.UBO", "Update", 3,
        new[] { "System.Object", "System.Int32", "System.Int32" }),
    // TAA P3: the second "AnimationPrev" uniform block for the skinned-entity
    // motion writer, created beside "Animation" while TAA is on.
    new("Vintagestory.Client.NoObf.ShaderProgramEntityanimated", "initUbos", 0),
    // Vulkan backend: the packed-face storage buffer the SSBO chunk path uses.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "UpdateSSBOMesh", 2),
    // Vulkan backend, world rendering: the render systems that reach past
    // ClientPlatformWindows to GL directly. Each keeps its GL body behind a
    // device check, the same shape as the platform routing.
    new("Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT", "rebuild", 0),
    new("Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT", "freeResources", 0),
    new("Vintagestory.Client.NoObf.SystemRenderSunMoon", ".ctor", 1),
    new("Vintagestory.Client.NoObf.SystemRenderSunMoon", "OnRenderFrame3DPost", 1),
    new("Vintagestory.Client.NoObf.SystemRenderSunMoon", "Dispose", 1),
    new("Vintagestory.Client.NoObf.SystemRenderFrameBufferDebug", "OnRenderFrame2DOverlay", 1),
    new("Vintagestory.Client.NoObf.SvgLoader", "LoadSvg", 6),
    new("Vintagestory.Client.NoObf.ClientMain", "OrthoMode", 3),
    new("Vintagestory.Client.NoObf.ClientMain", "PerspectiveMode", 0),
    new("Vintagestory.Client.NoObf.InventoryItemRenderer", "RenderItemStackToFrameBuffer", 3),
    new("Vintagestory.Client.NoObf.ClientSystemStartup", "HandleLevelFinalize", 1),
    new("Vintagestory.ClientNative.Screenshot", "GrabScreenshot", 4),
    // Vulkan backend: the GUI depth clear between the world and the interface,
    // the only raw GL left in the screen loop.
    new("Vintagestory.Client.ScreenManager", "Render", 1),
    // Vulkan backend: the post-process chain's remaining direct GL - viewport,
    // draw-buffer selection, depth toggle and the SSAO clear.
    // Vulkan backend: the device's default target and swapchain follow the
    // window only if something tells them to.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "Window_Resize", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "MergeTransparentRenderPass", 0),
    // TAA P4: the cube-particle motion window and its uniforms.
    new("Vintagestory.Client.NoObf.SystemRenderParticles", "OnRenderFrame3D", 1),
    // TAA P4: the decal motion window.
    new("Vintagestory.Client.NoObf.SystemRenderDecals", "OnRenderFrame3D", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderFinalComposition", 0),
    // GuiCompositeMainMenuLeft: Optimum link in main menu (no lambdas)
    new("Vintagestory.Client.GuiCompositeMainMenuLeft", "Compose", 0),
    // E3: particle spawn distance gate, before the per-particle revive loop
    new("Vintagestory.Client.NoObf.ParticlePoolQuads", "SpawnParticles", 1),
    // GuiManager: reusable scratch buffers instead of .ToList() snapshots.
    // RequestFocus is NOT here: its other two FindIndex lambdas (unrelated
    // to this fix) stay in the compiled body regardless, so it can't be
    // transplanted without a larger, separate lambda-removal pass, and it
    // only runs on focus-change events, not a hot path.
    new("Vintagestory.Client.NoObf.GuiManager", "OnBlockTexturesLoaded", 0),
    new("Vintagestory.Client.NoObf.GuiManager", "OnLevelFinalize", 0),
    new("Vintagestory.Client.NoObf.GuiManager", "OnOwnPlayerDataReceived", 0),
    new("Vintagestory.Client.NoObf.GuiManager", "OnFinalizeFrame", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnKeyDown", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnKeyUp", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnKeyPress", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnMouseDown", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnMouseUp", 1),
    new("Vintagestory.Client.NoObf.GuiManager", "OnMouseMove", 1),
    // R3: scale the occlusion-culling engagement threshold by view distance
    new("Vintagestory.Client.NoObf.ChunkCuller", "CullInvisibleChunks", 0),
    // AmbientManager: reusable scratch buffers instead of per-frame array/BlockPos allocations.
    // All four run every frame from the UpdateAmbient renderer registration.
    new("Vintagestory.Client.NoObf.AmbientManager", "UpdateAmbient", 1),
    new("Vintagestory.Client.NoObf.AmbientManager", "setWaterColors", 0),
    new("Vintagestory.Client.NoObf.AmbientManager", "UpdateDaylight", 1),
    new("Vintagestory.Client.NoObf.AmbientManager", "updateColorGradingValues", 1),
    // SystemRenderSkyColor: reusable scratch vectors instead of per-frame Vec3f allocations
    new("Vintagestory.Client.NoObf.SystemRenderSkyColor", "OnRenderFrame3D", 1),
    // SystemSoundEngine: audio listener update threshold + periodic refresh
    new("Vintagestory.Client.NoObf.SystemSoundEngine", "OnRenderFrame", 2),
    // RenderAPIBase: skip disposed meshrefs instead of rendering freed GL handles (#8881/#8950/#8982-class crash)
    new("Vintagestory.Client.RenderAPIBase", "RenderMultiTextureMesh", 3),
    // Chunk tesselator worker pool: .ctor constructs the tesselators/uploadHandoff
    // worker-pool state (Option B - TerrainChunkTesselator stays a real field, assigned
    // from ClientMain::Start above), OnBlockTexturesLoaded/OnSeperateThreadGameTick/
    // TesselateChunk are the pool's three other vanilla-reader sites, and OnBeforeFrame
    // (C1: skip RecalcPriority+Sort when the player hasn't moved) reads uploadHandoff.Reserved,
    // now safe because the constructor that assigns it is transplanted here too.
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", ".ctor", 1),
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", "OnBlockTexturesLoaded", 0),
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", "OnBeforeFrame", 1),
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", "OnSeperateThreadGameTick", 1),
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", "TesselateChunk", 6),
    // Texture atlas overflow recovery and decode worker scaling.
    new("Vintagestory.Client.NoObf.TextureAtlasManager", "RuntimeCreateNewAtlas", 1),
    new("Vintagestory.Client.NoObf.TextureAtlasManager", "PopulateTextureAtlassesFromTextures", 0),
    // Step 8's collateral: the complete non-ctor, non-ChunkTesselatorManager reader
    // set of the 3 dirtyChunks*Lock fields retyped above (fieldsToRetype) - verified
    // by an IL scan of the real vanilla module (no other reader exists). Left
    // untransplanted, these would keep using Monitor.Enter/Exit on a field that
    // OnSeperateThreadGameTick now locks with Lock.EnterScope() - two locking
    // primitives on the same field object provide no mutual exclusion against
    // each other, silently (see RetypedFieldReaderVerifier's doc comment).
    new("Vintagestory.Client.NoObf.ClientWorldMap", "SetChunkDirty", 4),
    new("Vintagestory.Client.NoObf.ClientWorldMap", "MarkChunkDirty", 8),
    new("Vintagestory.Client.NoObf.SystemRenderTerrain", "OnPlayerLeaveChunk", 2),
    // C3+C5: reused chunk-origin vector and pool-location lists per chunk upload
    new("Vintagestory.Client.NoObf.ChunkRenderer", "AddTesselatedChunk", 2),
    new("Vintagestory.Client.NoObf.TesselatedChunk", "AddCenterToPools", 5),
    new("Vintagestory.Client.NoObf.TesselatedChunk", "AddEdgeToPools", 5),
    // Chisel LOD: route microblock meshes into separate LOD pools and propagate distance flags
    new("Vintagestory.Client.NoObf.ChunkTesselator", "UpdateForAtlasses", 1),
    new("Vintagestory.Client.NoObf.ChunkTesselator", "NowProcessChunk", 5),
    new("Vintagestory.Client.NoObf.ChunkTesselator", "BuildBlockPolygons", 3),
    new("Vintagestory.Client.NoObf.ChunkTesselator", "BuildBlockPolygons_EdgeOnly", 3),
    new("Vintagestory.Client.NoObf.ChunkTesselator", "BuildDecorPolygons", 5),
    new("Vintagestory.Client.NoObf.ChunkTesselator", "GetMeshPoolForPass", 3),
    new("Vintagestory.Client.NoObf.TesselatedChunkPart", "AddModelAndStoreLocation", 8),
    // Eco Machina anchors its tapered-tree transpiler on this method's local slots.
    new("Vintagestory.Client.NoObf.ChunkTesselator", "CalculateVisibleFaces", 4),
    // Greedy mesh V0.1: stamps #define GREEDYMESH 0/1 into every shader's
    // prefix code from OptimumConfig.GreedyMeshEnabled (same mechanism as
    // vanilla's USESSBO stamp in this same method), so the chunkopaque
    // tile-decode compiles out entirely when the feature is off. Also sets
    // OptimumConfig.GreedyMeshShadersCompiledOn so the emitter never emits
    // sentinel bits a live shader can't decode.
    new("Vintagestory.Client.NoObf.ShaderRegistry", "registerDefaultShaderCodePrefixes", 2),
    new("Vintagestory.Client.NoObf.ShaderRegistry", "registerDefaultShaderProgramsPre", 0),
    new("Vintagestory.Client.NoObf.ShaderRegistry", "loadRegisteredShaderPrograms", 0),
    // OIT: skip disposed shaders and restore render state after a failed OIT frame.
    new("Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT", "OnRenderFrame", 2),
    new("Vintagestory.Client.NoObf.SystemRenderOITLayers/AfterOIT", "OnRenderFrame", 2),
    // datapath.cfg support: entry shims (ClientLinux/ClientWindows/ClientMac) all
    // funnel into this Main, so the arg injection lives here (lambda-free)
    new("Vintagestory.Client.ClientProgram", "Main", 1),
    new("Vintagestory.Client.ClientProgram", "Start", 2),
    // Mod-crash containment: a mod exception in GetHeldItemInfo during the
    // background search-cache build otherwise kills the client (SmithingPlus
    // shutdown race, unhandled on the TyronThreadPool thread).
    new("Vintagestory.Common.CreativeTab", "CreateSearchCache", 1),
    new("Vintagestory.Common.EventManager", "TriggerOnGetClimate", 4),
    new("Vintagestory.Common.EventManager", "TriggerOnGetWindSpeed", 2),
    new("Vintagestory.Common.EventManager", "TriggerGameTick", 2),
    new("Vintagestory.Common.EventManager", "TriggerGameTickDebug", 2),
    new("Vintagestory.Common.Compression", "CompressAndCombine", 3),
    new("Vintagestory.Common.Compression", "DecompressCombined", 4),
    new("Vintagestory.Common.LoadBalancer", "CreateDedicatedWorkerThread", 3),
    // SvgLoader: reload SVG asset data when a mod holds a stale IAsset ref
    // after the textures category is unloaded (waypoint icon packs, etc.).
    // Vanilla throws; this reloads from Origin, keeping icons drawing.
    new("Vintagestory.Client.NoObf.SvgLoader", "rasterizeSvg", 6),

    // --- Server-side worldgen scheduler + chunk read pool ---
    // See docs/implementation-plans/server-worldgen-chunk-pool-cecil-wiring-plan-2026-08-11.md
    // for the full IL-level audit behind every target below (nested-type-set
    // diffs, exact param counts, why the skipped methods are skipped).
    new("Vintagestory.Server.ChunkColumnLoadRequest", ".ctor", 6),
    new("Vintagestory.Server.ChunkServerThread", ".ctor", 3),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "OnBeginGameReady", 1),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "OnSeparateThreadTick", 0),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "tryLoadOrGenerateChunkColumnsInQueue", 0),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "loadChunkAreaBlocking", 6),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "GenerateChunkColumns_OnSeparateThread", 2),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "runGenerators", 2),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "GetOrCreateMapChunk", 2),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "TryLoadChunkColumn", 1),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "InitWorldgenAndSpawnChunks", 0),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "CreateAdditionalWorldGenThread", 3),
    new("Vintagestory.Server.ServerSystemSupplyChunks", "GeneratorThreadLoop", 2),
    // OptimumGeneratorThreadEntry is dead code (CreateAdditionalWorldGenThread
    // uses its own inline lambda instead) - deliberately not a target.
    new("Vintagestory.Server.ServerSystemLoadAndSaveGame", "OnBeginConfiguration", 0),
    new("Vintagestory.Server.ServerSystemLoadAndSaveGame", "SaveAllDirtyMapRegions", 1),
    new("Vintagestory.Server.ServerSystemLoadAndSaveGame", "SaveAllDirtyMapChunks", 1),
    new("Vintagestory.Server.ServerSystemLoadAndSaveGame", "SaveAllDirtyLoadedChunks", 2),
    new("Vintagestory.Server.ServerSystemLoadAndSaveGame", "SaveAllDirtyGeneratingChunks", 1),
    // OnSeperateThreadShutDown is deliberately not a target: its pre-existing
    // body references the class's shared <>c (SaveGameWorld's cached delegate).
    // DisposeOptimumChunkReadPool is injected and called by an IL hook immediately
    // before the vanilla GameDatabase.Dispose call instead.
    new("Vintagestory.Server.ServerSystemSendChunks", "sendAndEnqueueChunks", 1),
    new("Vintagestory.Server.ServerSystemBlockSimulation", "GetUpdateInterval", 0),
    new("Vintagestory.Server.ServerSystemBlockSimulation", "OnSeparateThreadTick", 0),
    new("Vintagestory.Server.ServerSystemBlockSimulation", "tryTickBlock", 2),
    new("Vintagestory.Server.ServerSystemUnloadChunks", "UnloadGeneratingChunkColumns", 1),
    new("Vintagestory.Server.ServerSystemUnloadChunks", "FindUnloadableChunkColumnCandidates", 0),
    new("Vintagestory.Server.ServerSystemUnloadChunks", "SendOutOfRangeChunkUnloads", 1),
    new("Vintagestory.Server.PhysicsManager", "ServerTick", 1),
    new("Vintagestory.Server.PhysicsManager", "BuildClientList", 1),
    new("Vintagestory.Server.PhysicsManager", "BuildPositionPacket", 4),
    new("Vintagestory.Server.PhysicsManager", "SendPositionsAndAnimations", 4),
    new("Vintagestory.Server.BlockAccessorWorldGen", "AddEntity", 1),
    new("Vintagestory.Server.ServerSystemCompressChunks", "FindFreeableMemory", 0),
    new("Vintagestory.Server.ServerSystemNotifyPing", "PingTimerTick", 0),
    new("Vintagestory.Server.ServerSystemInventory", "SendDirtySlots", 1),
    new("Vintagestory.Server.ServerSystemRelight", "ProcessLightingTask", 2),
    new("Vintagestory.Server.ServerMain", "get_AllOnlinePlayers", 0),
    new("Vintagestory.Server.ServerMain", "get_AllPlayers", 0),
    // Two BroadcastArbitraryPacket overloads share a name and param count -
    // ParameterTypes disambiguates which one each target binds to.
    new("Vintagestory.Server.ServerMain", "BroadcastArbitraryPacket", 2,
        new[] { "System.Byte[]", "Vintagestory.API.Server.IServerPlayer[]" }),
    new("Vintagestory.Server.ServerMain", "BroadcastArbitraryPacket", 2,
        new[] { "Packet_Server", "Vintagestory.API.Server.IServerPlayer[]" }),
    new("Vintagestory.Server.ServerMain", "BroadcastArbitraryUdpPacket", 2),
    new("Vintagestory.Server.ServerPackets", "GetBulkEntityAttributesPacket", 2),
    new("Vintagestory.Server.ServerPackets", "GetBulkEntityDebugAttributesPacket", 1),
};

// --- Platform substitution (Vulkan-native plan, Phase 0) ---
// Optimum.Render.Vulkan ships VulkanClientPlatform : ClientPlatformWindows and overrides
// these members. ILPatcher applies both lists after every body transplant and hook, so a
// transplant of the same methods cannot drop the flags, then fails the patch if any body
// still reaches a virtualized method with `call`/`ldftn` (a caller that would bypass the
// override). Entries must be public or protected: a private virtual cannot be overridden
// from another assembly.
var typesToUnseal = new List<string>
{
    "Vintagestory.Client.NoObf.ClientPlatformWindows",
};

var methodsToVirtualize = new List<MethodTarget>
{
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "SetupDefaultFrameBuffers", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "DisposeFrameBuffers", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderFullscreenTriangle", 1),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "GetGraphicsCardRenderer", 0),
};

int total = ILPatcher.PatchWithInjection(
    vanillaPath, compiledPath, outputPath,
    typesToInject, membersToInject, targets,
    // IL hooks: insert call AFTER EndIf in ComposerHeader to add the Extra tab button
    new List<HookTarget>
    {
        new(
            "Vintagestory.Client.NoObf.GuiCompositeSettings",
            "ComposerHeader",
            2,
            "_AddOptimumTab",
            "EndIf",
            TargetDeclaringType: "Vintagestory.API.Client.GuiComposer",
            TargetParameterTypes: [],
            TargetReturnType: "Vintagestory.API.Client.GuiComposer",
            TargetHasThis: true,
            TargetExplicitThis: false,
            TargetCallingConvention: MethodCallingConvention.Default,
            TargetGenericArity: 0),
        // The shutdown method cannot be transplanted because its pre-existing
        // body references the shared <>c nested type. Clear and dispose the
        // read-only SQLite pool immediately before the vanilla database closes.
        new(
            "Vintagestory.Server.ServerSystemLoadAndSaveGame",
            "OnSeperateThreadShutDown",
            0,
            "DisposeOptimumChunkReadPool",
            "Dispose",
            TargetDeclaringType: "Vintagestory.Common.GameDatabase",
            TargetParameterTypes: [],
            TargetReturnType: "System.Void",
            TargetHasThis: true,
            TargetExplicitThis: false,
            TargetCallingConvention: MethodCallingConvention.Default,
            TargetGenericArity: 0,
            InsertBeforeTarget: true),
    },
    fieldsToRetype: fieldsToRetype,
    typesToUnseal: typesToUnseal,
    methodsToVirtualize: methodsToVirtualize);

Console.WriteLine($"\nDone.");
return total > 0 ? 0 : 1;
