using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

// Issue #580: Any patch added under patches/VintagestoryApi/ only alters the decompiled donor fork
// used for compilation/tests unless Optimum.Patcher/api-patcher.cs delivers it into the shipped
// VintagestoryAPI.dll. This coverage test ensures every VintagestoryApi patch is explicitly
// classified and every shipped entry maps to an active Patch* call inside ApiPatcher.Patch.
public sealed class VintagestoryApiPatchDeliveryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ShippedPatchMethods =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Client/API/ICoreClientAPI.cs.patch"] = ["PatchClientApiThreadContract"],
            ["Client/MeshPool/MeshDataPoolMasterManager.cs.patch"] = ["PatchMeshDataPoolLifecycle"],
            ["Client/MeshPool/MeshDataRecycler.cs.patch"] = ["PatchMeshDataRecycler"],
            ["Client/Render/FrustumCulling.cs.patch"] = ["PatchFrustumCulling"],
            ["Client/Texture/LoadedTexture.cs.patch"] = ["PatchLoadedTextureDisposal"],
            ["Client/UI/CairoFont.cs.patch"] = ["PatchFontMeasurementMetrics"],
            ["Client/UI/Elements/Impl/Interactive/GuiElementConfigList.cs.patch"] = ["PatchGuiTextureDisposal"],
            ["Client/UI/Elements/Impl/Interactive/GuiElementStatbar.cs.patch"] = ["PatchGuiStatbarOnDemand", "PatchQueuedGuiContexts"],
            ["Client/UI/Elements/Impl/Interactive/Inventory/GuiElementItemSlotGridBase.cs.patch"] = ["PatchGuiTextureDisposal", "PatchGuiSlotNotifiedSubscription", "PatchInventorySearch"],
            ["Client/UI/Elements/Impl/Interactive/Text/GuiElementDynamicText.cs.patch"] = ["PatchQueuedGuiContexts"],
            ["Client/UI/Elements/Impl/Interactive/Text/GuiElementHoverText.cs.patch"] = ["PatchGuiHoverTextSameText"],
            ["Client/UI/Elements/Impl/Interactive/Text/GuiElementItemstackInfo.cs.patch"] = ["PatchQueuedGuiContexts"],
            ["Client/UI/Elements/Impl/Interactive/Text/Richtext/TextLinkComponent.cs.patch"] = ["PatchLinkTextGpuUploads"],
            ["Client/UI/Elements/Impl/Static/GuiElementCustomDraw.cs.patch"] = ["PatchGuiTextureDisposal"],
            ["Client/UI/GuiComposer.cs.patch"] = ["PatchGuiCompositionMetrics"],
            ["Client/UI/IconUtil.cs.patch"] = ["PatchSvgIconTarget"],
            ["Client/UI/TextDrawUtil.cs.patch"] = ["PatchTextDrawUtilFontOverloads"],
            ["Common/Assets/AssetLocation.cs.patch"] = ["PatchAssetLocation"],
            ["Common/Collectible/ItemStack.cs.patch"] = ["PatchInventorySearch"],
            ["Common/Collectible/JsonItemStack.cs.patch"] = ["PatchJsonLineInfo"],
            ["Common/Crafting/CraftingRecipeIngredient.cs.patch"] = ["PatchJsonLineInfo"],
            ["Common/Inventory/InventoryBase.cs.patch"] = ["PatchInventoryDirtyHooks"],
            ["Common/Model/Animation/AnimationKeyFrame.cs.patch"] = ["PatchAnimationKeyFrame"],
            ["Common/Model/Animation/AnimationMetaData.cs.patch"] = ["PatchJsonLineInfo"],
            ["Common/Model/Animation/ClientAnimator.cs.patch"] = ["PatchClientAnimator", "PatchClientAnimatorGetMaxDepth", "PatchClientAnimatorFrameLerp"],
            ["Common/Model/Animation/EntityHeadController.cs.patch"] = ["PatchHeadControllerPoseFallback"],
            ["Common/Registry/JsonConverters.cs.patch"] = ["PatchJsonLineInfo"],
            ["Common/Registry/RegistryObject.cs.patch"] = ["PatchRegistryObjectCodeParts", "PatchRegistryObjectFillPlaceHolder"],
            ["Common/TyronThreadPool.cs.patch"] = ["PatchTyronThreadPoolDiagnostics"],
            ["Datastructures/JsonObject.cs.patch"] = ["PatchJsonLineInfo"],
            ["Math/Ascii85.cs.patch"] = ["PatchAscii85"],
            ["Math/ColorUtil.cs.patch"] = ["PatchColorUtil"],
            ["Math/EvolvingNatFloat.cs.patch"] = ["PatchEvolvingNatFloat"],
            ["Math/Matrix/Mat4f.cs.patch"] = ["PatchMat4fInlining"],
            ["Util/ObjectCacheUtil.cs.patch"] = ["PatchObjectCacheUtil"],
            ["Util/WildcardUtil.cs.patch"] = ["PatchWildcardUtil"],
        };

    // Patches whose diff is strictly C# compilation / decompiler formatting alignment with no runtime change vs vanilla IL.
    private static readonly IReadOnlyDictionary<string, string> CompileTimeOnlyFixups =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Client/API/IAmbientManager.cs.patch"] = "Qualifies OrderedDictionary<,> against System.Collections.Generic in .NET 10.",
            ["Client/API/IInputAPI.cs.patch"] = "Qualifies OrderedDictionary<,> against System.Collections.Generic in .NET 10.",
            ["Client/Render/PerceptionEffects/DrunkPerceptionEffect.cs.patch"] = "Decompiler syntax normalization only.",
            ["Client/Render/PerceptionEffects/PerceptionEffect.cs.patch"] = "Decompiler syntax normalization only.",
            ["Client/Render/PerceptionEffects/PsychedelicPerceptionEffect.cs.patch"] = "Decompiler syntax normalization only.",
            ["Common/Entity/EntityProperties.cs.patch"] = "Qualifies OrderedDictionary<,> against System.Collections.Generic in .NET 10.",
            ["Common/Entity/EntityStats.cs.patch"] = "Qualifies OrderedDictionary<,> against System.Collections.Generic in .NET 10.",
            ["Datastructures/IMergeable.cs.patch"] = "File-scoped namespace syntax cleanup only.",
            ["Server/API/IServerWorldAccessor.cs.patch"] = "Qualifies OrderedDictionary<,> against System.Collections.Generic in .NET 10.",
        };

    // Audited legacy patches that predate ApiPatcher enforcement and still lack Cecil delivery in api-patcher.cs.
    // New patches MUST NOT be added here; implement them in api-patcher.cs instead.
    private static readonly IReadOnlyDictionary<string, string> AuditedLegacyUnshippedGaps =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Client/MeshPool/MeshDataPool.cs.patch"] = "Legacy unshipped hunks (#539/#542/#547 pool bounds/cullData/telemetry); #613 TryAdd/Dispose and #606 front-to-back sort shipped via ApiPatcher.",
            ["Client/MeshPool/MeshDataPoolManager.cs.patch"] = "Legacy unshipped hunks (render pass diagnostics); #613 manager binding and #606 front-to-back sort shipped via ApiPatcher.",
            ["Client/Model/Mesh/MeshData.cs.patch"] = "Legacy unshipped patch (#494 custom attribute stride/indexing).",
            ["Client/UI/Dialog/GuiDialog.cs.patch"] = "Legacy unshipped patch (skip disabled composers).",
            ["Client/UI/Elements/Impl/Interactive/Controls/GuiElementHorizontalTabs.cs.patch"] = "Legacy unshipped patch (Font.GetTextExtents migration).",
            ["Client/UI/Elements/Impl/Interactive/Controls/GuiElementVerticalTabs.cs.patch"] = "Legacy unshipped patch (Font.GetTextExtents migration).",
            ["Client/UI/Elements/Impl/Interactive/Text/GuiElementEditableTextBase.cs.patch"] = "Legacy unshipped patch (Font.GetTextExtents migration).",
            ["Client/UI/Elements/Impl/Interactive/Text/Richtext/SlideshowGridRecipeTextComponent.cs.patch"] = "Legacy unshipped patch (scissor bounds cache).",
            ["Client/UI/Elements/Impl/Static/GuiElementEngravedText.cs.patch"] = "Legacy unshipped patch (Font.GetTextExtents migration).",
            ["Client/UI/TextTextureUtil.cs.patch"] = "Legacy unshipped patch (cache Font.GetFontExtents).",
            ["Common/Collectible/Block/Block.cs.patch"] = "Legacy unshipped patch (#231 ThreadStatic GetDrops list reuse).",
            ["Common/Crafting/CollectiblePreSearchResultsCache.cs.patch"] = "Legacy unshipped patch (#500 domain-indexed search cache).",
            ["Common/Crafting/RecipeBase.cs.patch"] = "Legacy unshipped hunk (#500 wildcard/regex cache); #635 JsonLineInfo shipped via PatchJsonLineInfo.",
            ["Common/Entity/EntityPlayer.cs.patch"] = "Legacy unshipped patch (Animator null guard in OnTesselated).",
            ["Common/Entity/EntityPos.cs.patch"] = "Legacy unshipped patch (non-allocating GetViewVector overload).",
            ["Common/Model/Animation/AnimatableRenderer.cs.patch"] = "Legacy unshipped patch (#420 frustum cull guard).",
            ["Common/Model/Animation/AnimationManager.cs.patch"] = "Legacy unshipped patch (LINQ elimination and animation diagnostics).",
            ["Common/Model/Animation/AnimationUtil.cs.patch"] = "Legacy unshipped patch (#320/#396 animation LOD).",
            ["Common/Model/Animation/AnimatorBase.cs.patch"] = "Deliberately excluded from runtime patching; see CrashRecoveryCoverageTests.SharedAnimatorComparerRemainsOutsideTheRuntimePatch.",
            ["Common/Model/Shape/CompositeShape.cs.patch"] = "Legacy unshipped patch (#498 ResolveAlternates fast path).",
            ["Common/Model/Shape/Shape.cs.patch"] = "Legacy unshipped patch (#301/#312 thread-safe ResolveAndFindJoints).",
            ["Datastructures/AttributeTree/TreeAttribute.cs.patch"] = "Legacy unshipped hunk (#491 direct built-in attribute switch); #635 JsonLineInfo shipped via PatchJsonLineInfo.",
            ["Datastructures/FastMemoryStream.cs.patch"] = "Legacy unshipped patch (#298 Buffer.BlockCopy in Read).",
            ["Datastructures/SortableQueue.cs.patch"] = "Legacy unshipped patch (#562 age-aware priority/merge).",
            ["Datastructures/UniqueQueue.cs.patch"] = "Legacy unshipped patch (#411 capacity pre-sizing).",
            ["Localization/TranslationService.cs.patch"] = "Legacy unshipped patch (#284/#305 active-language loading).",
            ["Math/CollisionTester.cs.patch"] = "Legacy unshipped patch (#408 cached WalkBlocks delegate).",
            ["Math/Noise/NewNormalizedSimplexFractalNoise.cs.patch"] = "Legacy unshipped patch (#289 thread-static noise scratch).",
            ["Math/ShapeUtil.cs.patch"] = "Legacy unshipped patch (#495 fixed Vec2i array).",
        };

    [Fact]
    public void EveryVintagestoryApiPatch_IsClassifiedAndShippedEntriesAreActiveInApiPatcher()
    {
        string apiPatcherPath = PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs");
        string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(apiPatcherPath)!)!;
        string patchesRoot = Path.Combine(repoRoot, "patches", "VintagestoryApi");
        string[] actualPatches = Directory.GetFiles(patchesRoot, "*.patch", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(patchesRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string apiPatcherSource = File.ReadAllText(apiPatcherPath);
        var patchEntryBody = ExtractApiPatcherEntryBody(apiPatcherSource);
        var patcherTypeMethods = typeof(ApiPatcher)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var allClassified = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in ShippedPatchMethods.Keys) Assert.True(allClassified.Add(key), $"Duplicate classification for {key}");
        foreach (string key in CompileTimeOnlyFixups.Keys) Assert.True(allClassified.Add(key), $"Duplicate classification for {key}");
        foreach (string key in AuditedLegacyUnshippedGaps.Keys) Assert.True(allClassified.Add(key), $"Duplicate classification for {key}");

        string[] missingFromRegistry = actualPatches.Where(p => !allClassified.Contains(p)).ToArray();
        Assert.True(
            missingFromRegistry.Length == 0,
            "Every patches/VintagestoryApi patch must have a shipped counterpart in Optimum.Patcher/api-patcher.cs (or explicit classification). Unclassified patches: " +
            string.Join(", ", missingFromRegistry));

        string[] staleRegistryEntries = allClassified.Where(p => !actualPatches.Contains(p)).ToArray();
        Assert.Empty(staleRegistryEntries);

        foreach (var (patchPath, methodNames) in ShippedPatchMethods)
        {
            Assert.NotEmpty(methodNames);
            foreach (string methodName in methodNames)
            {
                Assert.Contains(methodName, patcherTypeMethods);
                Assert.Matches($@"\b{Regex.Escape(methodName)}\s*\(", patchEntryBody);
            }
        }
    }

    [Fact]
    public void AuditedLegacyUnshippedList_OnlyShrinksOverTime()
    {
        // Guardrail: new VintagestoryApi patches must be delivered in ApiPatcher, never appended here.
        Assert.True(AuditedLegacyUnshippedGaps.Count <= 31,
            $"Legacy unshipped backlog grew beyond 31 ({AuditedLegacyUnshippedGaps.Count}); ship new API patches in api-patcher.cs.");
    }

    private static string ExtractApiPatcherEntryBody(string source)
    {
        int start = source.IndexOf("public static bool Patch(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApiPatcher.Patch entry point missing.");
        int nextMethod = source.IndexOf("internal static int PatchColorUtil(", start, StringComparison.Ordinal);
        Assert.True(nextMethod > start, "Could not bound ApiPatcher.Patch method body.");
        return source[start..nextMethod];
    }
}
