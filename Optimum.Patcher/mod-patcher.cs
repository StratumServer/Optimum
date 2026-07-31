using System;
using System.Collections.Generic;
using System.IO;

namespace Optimum.Patcher;

public static class ModPatcher
{
    public static bool Patch(
        string modName,
        string vanillaPath,
        string compiledPath,
        string outputPath)
    {
        var manifest = modName.ToLowerInvariant() switch
        {
            "vsessentials" => EssentialsManifest(),
            "vssurvivalmod" => SurvivalManifest(),
            "vscreativemod" => CreativeManifest(),
            _ => throw new ArgumentException($"Unknown mod patch manifest: {modName}", nameof(modName)),
        };

        int total = ILPatcher.PatchWithInjection(
            vanillaPath,
            compiledPath,
            outputPath,
            manifest.Types,
            manifest.Members,
            manifest.Methods,
            interfacesToInject: manifest.Interfaces,
            requireAllTargets: false);

        if (total <= 0)
        {
            throw new InvalidOperationException($"{modName} patch produced no changes.");
        }
        return true;
    }

    private static Manifest EssentialsManifest()
    {
        return new Manifest(
            Types:
            [
                "Vintagestory.GameContent.OptimumStatusModSystem",
            ],
            Members: new()
            {
                ["Vintagestory.GameContent.EntityBehaviorCollectEntities"] =
                [
                    "OptimumCollectStrideInterval",
                    "_optimumAccumulatedDelta",
                ],
                ["Vintagestory.Essentials.AStar"] =
                [
                    "optimumNodePool",
                    "optimumNodePoolIndex",
                    "OptimumRentNode",
                ],
                ["Vintagestory.GameContent.EntityShapeRenderer"] =
                [
                    "optimumShaderStateCompatible",
                    "optimumActiveLightBatchId",
                    "optimumBaseLightBatchId",
                    "optimumBaseLightX",
                    "optimumBaseLightY",
                    "optimumBaseLightZ",
                    "optimumBaseLightChunk",
                    "optimumBaseLightRed",
                    "optimumBaseLightGreen",
                    "optimumBaseLightBlue",
                    "optimumBaseLightSun",
                    "optimumUpperLightBatchId",
                    "optimumUpperLightX",
                    "optimumUpperLightY",
                    "optimumUpperLightZ",
                    "optimumUpperLightChunk",
                    "optimumUpperLightRed",
                    "optimumUpperLightGreen",
                    "optimumUpperLightBlue",
                    "optimumUpperLightSun",
                    "OptimumLightSampleCount",
                    "OptimumShaderStateCompatible",
                    "GetOptimumLightSampleCoordinates",
                    "SetOptimumLightSample",
                    "ActivateOptimumLightBatch",
                    "ClearOptimumLightSamples",
                    "TryUseOptimumLightSamples",
                ],
                ["Vintagestory.GameContent.WeatherSimulationParticles"] =
                [
                    "optimumLastHeightmapCenterX",
                    "optimumLastHeightmapCenterZ",
                ],
                ["Vintagestory.GameContent.WeatherSystemClient"] =
                [
                    "optimumWindFrameCounter",
                ],
                ["Vintagestory.GameContent.WeatherSimulationSound"] =
                [
                    "lastSetWindVolumeLeafy",
                    "lastSetWindVolumeLeafless",
                    "lastSetRainVolumeLeafy",
                    "lastSetRainVolumeLeafless",
                ],
                ["Vintagestory.GameContent.ChunkMapLayer"] =
                [
                    "pageCache",
                    "terrainSampler",
                    "pageTextureArray",
                    "pageRenderer",
                    "loadQueue",
                    "coveredPages",
                    "UploadChunkToPageArray",
                    "RenderWithPageArray",
                ],
                ["Vintagestory.ServerMods.TreeGen"] =
                [
                    "vineScratchPos",
                    "positionStack",
                ],
            },
            Interfaces: new()
            {
                ["Vintagestory.GameContent.EntityShapeRenderer"] =
                [
                    "Vintagestory.API.Client.IOptimumEntityLightSampler",
                    "Vintagestory.API.Client.IOptimumEntityShaderRenderer",
                ],
            },
            Methods:
            [
                new("Vintagestory.GameContent.BlockEntityParticleEmitter", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorCollectEntities", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorRepulseAgents", "OnGameTick", 1),
                new("Vintagestory.Essentials.AStar", "FindPathOrEscapePath", 9),
                new("Vintagestory.Essentials.PathNode", "Equals", 1),
                new("Vintagestory.GameContent.EntityItemRenderer", "DoRender3DOpaque", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", ".ctor", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", "BeforeRender", 1),
                new("Vintagestory.GameContent.EntityShapeRenderer", "DoRender3DOpaqueBatched", 2),
                new("Vintagestory.GameContent.WeatherSimulationParticles", "asyncParticleSpawn", 2),
                new("Vintagestory.GameContent.WeatherSystemClient", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.WeatherSimulationSound", "updateSounds", 1),
                new("Vintagestory.GameContent.ChunkMapLayer", ".ctor", 2),
                new("Vintagestory.GameContent.ChunkMapLayer", "Event_OnChunkDirty", 3),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnMapOpenedClient", 0),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnMapClosedClient", 0),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnShutDown", 0),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnOffThreadTick", 1),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnTick", 1),
                new("Vintagestory.GameContent.ChunkMapLayer", "Render", 2),
                new("Vintagestory.GameContent.ChunkMapLayer", "loadFromChunkPixels", 2),
                new("Vintagestory.GameContent.ChunkMapLayer", "OnViewChangedClient", 2),
                new("Vintagestory.ServerMods.TreeGen", "growBranch", 13),
                new("Vintagestory.ServerMods.TreeGen", "PlaceBlockEtc", 5),
            ]);
    }

    private static Manifest SurvivalManifest()
    {
        return new Manifest(
            Types:
            [
                "Vintagestory.GameContent.CrucibleInFirepitRenderer",
            ],
            Members: new()
            {
                ["Vintagestory.GameContent.BlockEntityContainer"] =
                [
                    "optimumNonEmptyStacks",
                    "optimumNonEmptyDepth",
                ],
                ["Vintagestory.GameContent.ProPickWorkSpace"] =
                [
                    "optimumReusableChunks",
                    "optimumReusableChunksHeight",
                ],
                ["Vintagestory.GameContent.ItemProspectingPick"] =
                [
                    "optimumNodeQuantityFound",
                    "optimumNodeResultsSorted",
                    "CompareNodeResults",
                ],
                ["Vintagestory.GameContent.BlockCookingContainer"] =
                [
                    "optimumCookingStacks",
                    "optimumCookingStacksDepth",
                ],
                ["Vintagestory.GameContent.Mechanics.MechanicalPowerMod"] =
                [
                    "optimumTickNetworks",
                ],
                ["Vintagestory.GameContent.BlockSmeltingContainer"] =
                [
                    "GetRendererWhenInFirepit",
                    "GetDesiredFirepitModel",
                ],
            },
            Interfaces: new()
            {
                ["Vintagestory.GameContent.BlockSmeltingContainer"] =
                [
                    "Vintagestory.GameContent.IInFirepitRendererSupplier",
                ],
            },
            Methods:
            [
                new("Vintagestory.GameContent.BlockEntityMicroBlock", "OnTesselation", 2),
                new("Vintagestory.GameContent.BlockEntityContainer", "GetNonEmptyContentStacks", 1),
                new("Vintagestory.GameContent.ProPickWorkSpace", "GetRockColumn", 2),
                new("Vintagestory.GameContent.ItemProspectingPick", "ProbeBlockNodeMode", 5),
                new("Vintagestory.GameContent.BlockCookingContainer", "GetCookingStacks", 2),
                new("Vintagestory.GameContent.Mechanics.MechanicalPowerMod", "OnServerGameTick", 1),
            ]);
    }

    private static Manifest CreativeManifest()
    {
        return new Manifest(
            Types: [],
            Members: new(),
            Interfaces: new(),
            Methods: []);
    }

    private sealed record Manifest(
        List<string> Types,
        Dictionary<string, List<string>> Members,
        Dictionary<string, List<string>> Interfaces,
        List<MethodTarget> Methods);
}
