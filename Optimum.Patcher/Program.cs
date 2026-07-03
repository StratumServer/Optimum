using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Optimum.Patcher;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Optimum.Patcher <vanilla.dll> <compiled.dll> <output.dll>");
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
    },
    ["Vintagestory.Client.NoObf.SystemRenderPlayerEffects"] = new()
    {
        "GetOptimumLightRadius",
        "HasLight",
        "SortByDistance",
        "_sortOrigin",
    },
    ["Vintagestory.Client.NoObf.SystemRenderEntities"] = new()
    {
        "ShadowCullDistanceSq",
    },
    ["Vintagestory.Client.NoObf.ClientPlatformWindows"] = new()
    {
        "_optimumSettingsInitialized",
        "EnsureOptimumDefaults",
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
    },
};

// --- Phase 1: Method bodies to transplant ---
var targets = new List<MethodTarget>
{
    // Entity render distance cull + shadow cull (reads injected ClientSettings.Optimum* props)
    new("Vintagestory.Client.NoObf.SystemRenderEntities", "OnRenderOpaque3D", 1),
    new("Vintagestory.Client.NoObf.SystemRenderEntities", "OnBeforeRender", 1),
    // HudEntityNameTags: IsRendered reuse (vanilla fields only)
    new("Vintagestory.Client.NoObf.HudEntityNameTags", "OnRenderGUI", 1),
    // ChunkRenderer: shadow far vegetation skip (reads injected OptimumShadowFarVegetation)
    new("Vintagestory.Client.NoObf.ChunkRenderer", "RenderOpaque", 1),
    // ClientMain: mouse wheel fix (vanilla fields only)
    new("Vintagestory.Client.NoObf.ClientMain", "OnMouseWheel", 1),
    // SystemRenderPlayerEffects: dynamic light radius (lambda-free rewrite)
    new("Vintagestory.Client.NoObf.SystemRenderPlayerEffects", "onBeforeRender", 1),
    // ClientPlatformWindows: frame pacing + background FPS (inline in window_RenderFrame, no lambdas)
    new("Vintagestory.Client.NoObf.ClientPlatformWindows", "window_RenderFrame", 1),
    // GuiCompositeMainMenuLeft: Optimum link in main menu (no lambdas)
    new("Vintagestory.Client.GuiCompositeMainMenuLeft", "Compose", 0),
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
