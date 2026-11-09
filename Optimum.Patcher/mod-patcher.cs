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
            requireAllTargets: true);

        if (total < 0)
        {
            throw new InvalidOperationException(
                $"{modName} patch failed output validation. The patcher rejected the generated assembly.");
        }

        if (total == 0)
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
                "Vintagestory.Essentials.OptimumColumnProbeResult",
            ],
            Members: new()
            {
                ["Vintagestory.GameContent.EntityBehaviorCollectEntities"] =
                [
                    "OptimumCollectStrideInterval",
                    "partitionUtil",
                ],
                ["Vintagestory.Essentials.AStar"] =
                [
                    "scratchNode",
                    "columnProbeCache",
                    "ResolveColumnProbe",
                    "GetColumnProbeKey",
                ],
                ["Vintagestory.GameContent.EntityShapeRenderer"] =
                [
                    "optimumShaderCompatibleByType",
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
                    "IsOptimumShaderStateCompatible",
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
                    "optimumWindAccum",
                    "optimumTargetWindX",
                    "optimumTargetWindY",
                    "optimumTargetWindZ",
                    "optimumTargetSurfaceWindX",
                    "optimumTargetSurfaceWindY",
                    "optimumTargetSurfaceWindZ",
                    "optimumHasWind",
                ],
                ["Vintagestory.GameContent.EntityBehaviorRepulseAgents"] =
                [
                    "isWithinRepulseRange",
                ],
                ["Vintagestory.GameContent.ChunkMapLayer"] =
                [
                    "pageCache",
                    "terrainSampler",
                    "pageTextureArray",
                    "pageRenderer",
                    "loadQueue",
                    "renderedPages",
                    "UploadChunkToPageArray",
                    "RenderWithPageArray",
                ],
                ["Vintagestory.ServerMods.TreeGen"] =
                [
                    "vineScratchPos",
                    "positionStack",
                ],
                ["Vintagestory.Essentials.PathNodeSet"] =
                [
                    "heap",
                    "sequences",
                    "nodeMap",
                    "sequenceCounter",
                    "ToKey",
                    "HasHigherPriority",
                    "Swap",
                    "SiftUp",
                    "SiftDown",
                    "UpdateNodePriority",
                ],
                ["Vintagestory.ServerMods.NoObf.RegistryObjectType"] =
                [
                    "optimumAlreadyResolved",
                    "t_dynamicTokens",
                    "GetOrCreateDynamicSet",
                    "MarkDynamicTokens",
                    "BuildDynamicSet",
                    "Resolve",
                ],
                ["Vintagestory.ServerMods.NoObf.ModRegistryObjectTypeLoader"] =
                [
                    "reusableDynamicNodes",
                ],
                ["Vintagestory.GameContent.CloudRenderer"] =
                [
                    "reusableTileOffset",
                    "reusableCloudTilePos",
                ],
                ["Vintagestory.GameContent.WeatherSystemBase"] =
                [
                    "GetWeatherSimLock",
                    "OnMapRegionUnloaded",
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
                new("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast", "genMesh", 1),
                new("Vintagestory.Essentials.PathNodeSet", ".ctor", 0),
                new("Vintagestory.Essentials.PathNodeSet", "Clear", 0),
                new("Vintagestory.Essentials.PathNodeSet", "Add", 1),
                new("Vintagestory.Essentials.PathNodeSet", "RemoveNearest", 0),
                new("Vintagestory.Essentials.PathNodeSet", "Remove", 1),
                new("Vintagestory.Essentials.PathNodeSet", "TryFindValue", 1),
                new("Vintagestory.Essentials.PathNodeSet", "GetEnumerator", 0),
                new("Vintagestory.GameContent.BlockEntityParticleEmitter", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorCollectEntities", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorRepulseAgents", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorRepulseAgents", "UpdateColSelBoxes", 0),
                new("Vintagestory.Essentials.AStar", "FindPathOrEscapePath", 9),
                new("Vintagestory.Essentials.AStar", "traversable", 6),
                new(
                    "Vintagestory.Essentials.PathNode",
                    "Equals",
                    1,
                    ParameterTypes: ["Vintagestory.Essentials.PathNode"]),
                new("Vintagestory.GameContent.EntityItemRenderer", "DoRender3DOpaque", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", ".ctor", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", "BeforeRender", 1),
                new("Vintagestory.GameContent.EntityShapeRenderer", "DoRender3DOpaqueBatched", 2),
                new("Vintagestory.GameContent.WeatherSimulationParticles", "asyncParticleSpawn", 2),
                new("Vintagestory.GameContent.WeatherSystemClient", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.WeatherSystemClient", "OnAssetsPacket", 1),
                new("Vintagestory.GameContent.WeatherSystemBase", "Start", 1),
                new("Vintagestory.GameContent.WeatherSystemServer", "OnSaveGameSaving", 0),
                new("Vintagestory.GameContent.CloudRenderer", "UpdateCloudTilesOffThread", 1),
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
                new("Vintagestory.ServerMods.GenCaves", "SetBlocks", 11),
                new("Vintagestory.ServerMods.NoObf.BlockPatch", "Generate", 7),
                new("Vintagestory.ServerMods.LerpedWeightedIndex2DMap", "WeightsAt", 3),
                new("Vintagestory.ServerMods.NoObf.RegistryObjectType", "solveByType", 3),
                new("Vintagestory.ServerMods.NoObf.ModRegistryObjectTypeLoader", "GatherVariantsAndPopulate", 2),
            ]);
    }

    private static Manifest SurvivalManifest()
    {
        return new Manifest(
            Types:
            [
                "Vintagestory.GameContent.CrucibleInFirepitRenderer",
                "Vintagestory.GameContent.GearRenderer",
                "Vintagestory.GameContent.OptimumOutfitShapeCache",
                "Vintagestory.GameContent.OptimumOutfitAnimatorCache",
                "Vintagestory.GameContent.OptimumOutfitTexturePrewarmerModSystem",
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
                ["Vintagestory.GameContent.BlockEntityFirepit"] =
                [
                    "GetPotStaticMesh",
                ],
                ["Vintagestory.GameContent.EntityDressedHumanoid"] =
                [
                    "optimumAnimatorCacheKey",
                ],
                ["Vintagestory.GameContent.BEBehaviorMicroblockSnowCover"] =
                [
                    "TrySnowableSurfaceGrowX",
                    "TrySnowableSurfaceGrowZ",
                ],
                ["Vintagestory.GameContent.EntityBehaviorBodyTemperature"] =
                [
                    "ShiverAnimationNames",
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
                new("Vintagestory.GameContent.EntityDressedHumanoid", "OnTesselation", 2),
                new("Vintagestory.GameContent.EntityDressedHumanoid", "OnTesselation", 3, Optional: true),
                new("Vintagestory.GameContent.GearRenderer", "Init", 0),
                new("Vintagestory.GameContent.GearRenderer", "LoadShader", 0),
                new("Vintagestory.GameContent.GearRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.GearRenderer", "updateSuperMechState", 2),
                new("Vintagestory.GameContent.Mechanics.MechBlockRenderer", "UpdateCustomFloatBuffer", 0),
                new("Vintagestory.GameContent.BEBehaviorMicroblockSnowCover", "buildSnowCuboids", 1),
                new("Vintagestory.GameContent.GenStoryStructures", "InitWorldGen", 0),
                new("Vintagestory.GameContent.GenStoryStructures", "OnChunkColumnGen", 1),
                new("Vintagestory.GameContent.EntityBehaviorBodyTemperature", "updateFreezingAnimState", 0),
                new("Vintagestory.GameContent.BlockEntityFirepit", "UpdateRenderer", 0),
                new("Vintagestory.GameContent.BlockEntityFirepit", "FromTreeAttributes", 2),
                new("Vintagestory.GameContent.BlockEntityFirepit", "getContentMesh", 2),
                new("Vintagestory.GameContent.PotInFirepitRenderer", "OnRenderFrame", 2),
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
