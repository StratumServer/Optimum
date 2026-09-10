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
            ],
            Members: new()
            {
                ["Vintagestory.GameContent.EntityBehaviorCollectEntities"] =
                [
                    "OptimumCollectStrideInterval",
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
                    "renderedPages",
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
                // Vulkan backend: the cloud renderers call OpenGL directly for
                // their map framebuffer, tile textures and state; on the device
                // path those go through the render seam instead.
                new("FluffyClouds.CloudRendererMap", "FreeGlResources", 0),
                new("FluffyClouds.CloudRendererMap", "OnRenderFrame", 2),
                new("FluffyClouds.CloudRendererMap", "WriteTexture", 0),
                new("FluffyClouds.CloudRendererMap", "makeTexture", 4),
                new("FluffyClouds.CloudRendererMap", "InitCloudTiles", 1),
                new("FluffyClouds.CloudRendererVolumetric", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.BlockEntityParticleEmitter", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorCollectEntities", "OnGameTick", 1),
                new("Vintagestory.GameContent.EntityBehaviorRepulseAgents", "OnGameTick", 1),
                new("Vintagestory.Essentials.AStar", "FindPathOrEscapePath", 9),
                new(
                    "Vintagestory.Essentials.PathNode",
                    "Equals",
                    1,
                    ParameterTypes: ["Vintagestory.Essentials.PathNode"]),
                new("Vintagestory.GameContent.EntityItemRenderer", "DoRender3DOpaque", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", ".ctor", 2),
                new("Vintagestory.GameContent.EntityShapeRenderer", "BeforeRender", 1),
                new("Vintagestory.GameContent.EntityShapeRenderer", "DoRender3DOpaqueBatched", 2),
                // TAA P3: the first-person hands draw entity geometry outside the
                // shared entity pass, with their own program, their own FOV and
                // their own copy of the animation blocks, so both methods carry
                // motion-writer changes.
                new("Vintagestory.GameContent.EntityPlayerShapeRenderer", "DoRender3DOpaque", 2),
                new("Vintagestory.GameContent.ModSystemFpHands", "LoadShaders", 0),
                // TAA P3: the standard-shader motion writer. Held items (both hands,
                // and the first-person item program) get their previous transform in
                // RenderItem; dropped items in EntityItemRenderer.DoRender3DOpaque
                // above. Both also open the motion-attachment window around their draw.
                new("Vintagestory.GameContent.EntityShapeRenderer", "RenderItem", 5),
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
                ["Vintagestory.GameContent.EntityDressedHumanoid"] =
                [
                    "optimumAnimatorCacheKey",
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
                // TAA P3: the echo chamber draws three meshes on the shared
                // entityanimated program from DoRender3DOpaque, so it opens the
                // motion-attachment window itself.
                new("Vintagestory.GameContent.EchoChamberRenderer", "DoRender3DOpaque", 2),
                // TAA P3: the quern top is the block-entity model that actually
                // moves, so it keeps a previous model matrix and writes motion.
                new("Vintagestory.GameContent.QuernTopRenderer", "OnRenderFrame", 2),
                // TAA P3, the instanced writer: every mechanical-power renderer now
                // fills OptimumInstanceMotion's instance layout (light, transform,
                // previous transform, metadata) instead of vanilla's light+transform,
                // so the buffer allocations, the transform writers and the instance
                // counts all move together. MechNetworkRenderer sets the pass uniforms
                // and opens the draw-buffer window around the whole loop.
                new("Vintagestory.GameContent.Mechanics.MechNetworkRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.MechBlockRenderer", "UpdateCustomFloatBuffer", 0),
                new("Vintagestory.GameContent.Mechanics.MechBlockRenderer", "UpdateLightAndTransformMatrix", 7),
                new("Vintagestory.GameContent.Mechanics.GenericMechBlockRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.GenericMechBlockRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.AngledCageGearRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.AngledCageGearRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.AngledGearsBlockRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.AngledGearsBlockRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.TransmissionBlockRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.TransmissionBlockRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.ClutchBlockRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.ClutchBlockRenderer", "UpdateLightAndTransformMatrix", 9),
                new("Vintagestory.GameContent.Mechanics.ClutchBlockRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.CreativeRotorRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.CreativeRotorRenderer", "createCustomFloats", 1),
                new("Vintagestory.GameContent.Mechanics.CreativeRotorRenderer", "UpdateLightAndTransformMatrix", 8),
                new("Vintagestory.GameContent.Mechanics.CreativeRotorRenderer", "OnRenderFrame", 2),
                new("Vintagestory.GameContent.Mechanics.PulverizerRenderer", ".ctor", 4),
                new("Vintagestory.GameContent.Mechanics.PulverizerRenderer", "createCustomFloats", 1),
                new("Vintagestory.GameContent.Mechanics.PulverizerRenderer", "UpdateLightAndTransformMatrix", 8),
                new("Vintagestory.GameContent.Mechanics.PulverizerRenderer", "OnRenderFrame", 2),
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
