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

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Optimum.Patcher <vanilla.dll> <compiled.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --compare-casts <vanilla.dll> <compiled.dll>");
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
    "Vintagestory.Client.NoObf.OptimumGreedyMeshEmitter",
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
    },
    ["Vintagestory.Client.NoObf.ShaderPrograms"] = new()
    {
        "FsrEasu",
        "FsrRcas",
    },
    ["Vintagestory.Client.NoObf.ShaderRegistry"] = new()
    {
        "RegisterOptimumShaderProgram",
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
    },
    // ChunkTesselatorManager: skip RecalcPriority+Sort when the player hasn't moved
    ["Vintagestory.Client.NoObf.ChunkTesselatorManager"] = new()
    {
        "_lastSortPlayerPos",
        "_lastSortYaw",
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
    // ClientMain: mouse wheel fix (vanilla fields only)
    new("Vintagestory.Client.NoObf.ClientMain", "OnMouseWheel", 1),
    // ClientMain: single-pass OpenedGuis scan instead of two LINQ calls (vanilla fields only)
    new("Vintagestory.Client.NoObf.ClientMain", "UpdateFreeMouse", 0),
    // SystemRenderPlayerEffects: dynamic light radius (lambda-free rewrite)
    new("Vintagestory.Client.NoObf.SystemRenderPlayerEffects", "onBeforeRender", 1),
    // ClientPlatformWindows: frame pacing + background FPS (inline in window_RenderFrame, no lambdas)
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "window_RenderFrame", 1),
    // FSR: allocate the native intermediate and replace the final bilinear blit.
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "SetupDefaultFrameBuffers", 0),
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "BlitPrimaryToDefault", 0),
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
    // C1: skip RecalcPriority+Sort when the player hasn't moved (lambda-free rewrite)
    new("Vintagestory.Client.NoObf.ChunkTesselatorManager", "OnBeforeFrame", 1),
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
    // datapath.cfg support: entry shims (ClientLinux/ClientWindows/ClientMac) all
    // funnel into this Main, so the arg injection lives here (lambda-free)
    new("Vintagestory.Client.ClientProgram", "Main", 1),
    // Mod-crash containment: a mod exception in GetHeldItemInfo during the
    // background search-cache build otherwise kills the client (SmithingPlus
    // shutdown race, unhandled on the TyronThreadPool thread).
    new("Vintagestory.Common.CreativeTab", "CreateSearchCache", 1),
};

int total = ILPatcher.PatchWithInjection(
    vanillaPath, compiledPath, outputPath,
    typesToInject, membersToInject, targets,
    // IL hooks: insert call AFTER EndIf in ComposerHeader to add the Extra tab button
    new List<(string typeName, string methodName, int paramCount, string hookMethod, string targetCall)>
    {
        ("Vintagestory.Client.NoObf.GuiCompositeSettings", "ComposerHeader", 2, "_AddOptimumTab", "EndIf"),
    });

Console.WriteLine($"\nDone.");
return total > 0 ? 0 : 1;
