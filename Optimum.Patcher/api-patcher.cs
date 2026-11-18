using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Applies Optimum changes that must live inside the game API while preserving
/// the owned assembly as the binary-compatible base.
/// </summary>
public static class ApiPatcher
{
    internal static DefaultAssemblyResolver CreateResolver(string vanillaPath, string contractsPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(vanillaPath)!);
        resolver.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(vanillaPath)!, "Lib"));
        resolver.AddSearchDirectory(Path.GetDirectoryName(contractsPath)!);

        // The CLI patches from the pristine copy in <game>/.optimum/vanilla, so the
        // game's own folder and Lib (cairo-sharp.dll) are not beside the input.
        // Searched last so the backup's VintagestoryAPI.dll still wins over the
        // game root's already patched copy.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(vanillaPath))!);
             dir?.Parent != null;
             dir = dir.Parent)
        {
            if (dir.Name != ".optimum")
                continue;

            resolver.AddSearchDirectory(dir.Parent.FullName);
            resolver.AddSearchDirectory(Path.Combine(dir.Parent.FullName, "Lib"));
            break;
        }

        return resolver;
    }

    public static bool Patch(string vanillaPath, string contractsPath, string outputPath)
    {
        if (!File.Exists(vanillaPath))
        {
            throw new FileNotFoundException("Vanilla API assembly not found.", vanillaPath);
        }
        if (!File.Exists(contractsPath))
        {
            throw new FileNotFoundException("Optimum contracts assembly not found.", contractsPath);
        }

        var resolver = CreateResolver(vanillaPath, contractsPath);

        bool preserveSymbols = File.Exists(Path.ChangeExtension(vanillaPath, ".pdb"));
        var vanillaReaderParameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = preserveSymbols,
        };
        var contractsReaderParameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = false,
        };

        using var vanilla = AssemblyReader.Read(vanillaPath, vanillaReaderParameters, out preserveSymbols);
        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath, contractsReaderParameters);
        string cairoPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contractsPath))!, "cairo-sharp.dll");
        if (!File.Exists(cairoPath))
        {
            throw new FileNotFoundException("The Cairo assembly required for GUI materialization diagnostics was not found.", cairoPath);
        }
        using var cairo = AssemblyDefinition.ReadAssembly(cairoPath, contractsReaderParameters);

        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")
            ?? throw new InvalidOperationException("OptimumApiBridge is missing from the contracts assembly.");
        var markInventoryDirty = bridge.Methods.Single(method =>
            method.Name == "MarkInventoryDirty" && method.Parameters.Count == 0);
        var optimumFrustumCheck = bridge.Methods.Single(method =>
            method.Name == "InFrustumAndRange" && method.Parameters.Count == 5);
        var optimumShadowCheck = bridge.Methods.Single(method =>
            method.Name == "InFrustumShadowPass" && method.Parameters.Count == 3);
        var frustumInFrustum = bridge.Methods.Single(method =>
            method.Name == "InFrustum" && method.Parameters.Count == 2);
        var frustumInFrustumShadowPass = bridge.Methods.Single(method =>
            method.Name == "InFrustumShadowPass" && method.Parameters.Count == 2);
        var frustumSphereInFrustum = bridge.Methods.Single(method =>
            method.Name == "SphereInFrustum" && method.Parameters.Count == 5);
        var frustumInFrustumAndRange = bridge.Methods.Single(method =>
            method.Name == "InFrustumAndRange" && method.Parameters.Count == 4);
        var ascii85Encode = bridge.Methods.Single(method =>
            method.Name == "Ascii85Encode" && method.Parameters.Count == 1);
        var ascii85Decode = bridge.Methods.Single(method =>
            method.Name == "Ascii85Decode" && method.Parameters.Count == 1);
        var registryObjectFirstCodePart = bridge.Methods.Single(method =>
            method.Name == "RegistryObjectFirstCodePart" && method.Parameters.Count == 2);
        var registryObjectLastCodePart = bridge.Methods.Single(method =>
            method.Name == "RegistryObjectLastCodePart" && method.Parameters.Count == 2);
        var registryObjectFillPlaceHolderAsset = bridge.Methods.Single(method =>
            method.Name == "RegistryObjectFillPlaceHolder" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation");
        var registryObjectFillPlaceHolderString = bridge.Methods.Single(method =>
            method.Name == "RegistryObjectFillPlaceHolder" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        var registryObjectFillPlaceHolderSingle = bridge.Methods.Single(method =>
            method.Name == "RegistryObjectFillPlaceHolder" &&
            method.Parameters.Count == 3);
        var wildcardUtilMatchTwoArg = bridge.Methods.Single(method =>
            method.Name == "WildcardUtilMatch" && method.Parameters.Count == 2);
        var wildcardUtilMatchThreeArg = bridge.Methods.Single(method =>
            method.Name == "WildcardUtilMatch" && method.Parameters.Count == 3);
        var wildcardUtilWildCardReplace = bridge.Methods.Single(method =>
            method.Name == "WildcardUtilWildCardReplace" && method.Parameters.Count == 3);
        var evolvingNatFloatEquals = bridge.Methods.Single(method =>
            method.Name == "EvolvingNatFloatEquals" && method.Parameters.Count == 2);
        var clientAnimatorInitMatrices = bridge.Methods.Single(method =>
            method.Name == "ClientAnimatorInitMatrices" && method.Parameters.Count == 2);
        var clientAnimatorGetMaxDepth = bridge.Methods.Single(method =>
            method.Name == "ClientAnimatorGetMaxDepth" && method.Parameters.Count == 2);
        var clientAnimatorComputeFrameLerps = bridge.Methods.Single(method =>
            method.Name == "ComputeClientAnimatorFrameLerps" && method.Parameters.Count == 5);
        var createKeyFrameElementDict = bridge.Methods.Single(method =>
            method.Name == "CreateKeyFrameElementDictionary" && method.Parameters.Count == 1);
        var assetLocationResolveToDomainAndPath = bridge.Methods.Single(method =>
            method.Name == "AssetLocationResolveToDomainAndPath" && method.Parameters.Count == 3);
        var colorUtilGetIncandescence3 = bridge.Methods.Single(method =>
            method.Name == "GetIncandescenceColorFloats" && method.Parameters.Count == 4);
        var colorUtilGetIncandescence4 = bridge.Methods.Single(method =>
            method.Name == "GetIncandescenceColorFloats" && method.Parameters.Count == 5);
        var colorUtilToRgba = bridge.Methods.Single(method =>
            method.Name == "ToRGBAFloats" && method.Parameters.Count == 5);
        var colorUtilGetIncandescenceLegacy = bridge.Methods.Single(method =>
            method.Name == "GetIncandescenceColorAsColor4f" && method.Parameters.Count == 1);
        var deleteGuiTextures = bridge.Methods.Single(method =>
            method.Name == "DeleteGuiTextures" && method.Parameters.Count == 2);
        var deleteGuiTexture = bridge.Methods.Single(method =>
            method.Name == "DeleteGuiTexture" && method.Parameters.Count == 2);
        var itemStackMatchesSearchText = bridge.Methods.Single(method =>
            method.Name == "ItemStackMatchesSearchText" && method.Parameters.Count == 3);
        var filterSlotGridItems = bridge.Methods.Single(method =>
            method.Name == "FilterSlotGridItems" && method.Parameters.Count == 10);
        var disposeLoadedTexture = bridge.Methods.Single(method =>
            method.Name == "DisposeLoadedTexture" && method.Parameters.Count == 4);
        var finalizeLoadedTexture = bridge.Methods.Single(method =>
            method.Name == "FinalizeLoadedTexture" && method.Parameters.Count == 5);
        var optimumConfig = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumConfig")
            ?? throw new InvalidOperationException("OptimumConfig is missing from the contracts assembly.");
        var versionField = optimumConfig.Fields.Single(field =>
            field.Name == "Version" && field.IsStatic && field.HasConstant);
        string optimumVersion = versionField.Constant as string
            ?? throw new InvalidOperationException("OptimumConfig.Version is not a string constant.");

        int inventoryHooks = PatchInventoryDirtyHooks(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(markInventoryDirty));
        int chiselHooks = PatchChiselLodHook(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(optimumFrustumCheck));
        int chiselShadowHooks = PatchChiselLodShadowHook(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(optimumShadowCheck));
        int loggerInitializers = PatchLoggerInitializer(vanilla.MainModule);
        int gameVersionLabels = PatchGameVersionLabel(vanilla.MainModule, optimumVersion);
        int mat4fInlined = PatchMat4fInlining(vanilla.MainModule);
        int headControllerFallback = PatchHeadControllerPoseFallback(vanilla.MainModule);
        int threadPoolDiagnostics = PatchTyronThreadPoolDiagnostics(vanilla.MainModule);
        int clientApiThreadContract = PatchClientApiThreadContract(vanilla.MainModule);
        int ascii85Methods = PatchAscii85(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(ascii85Encode),
            vanilla.MainModule.ImportReference(ascii85Decode));
        int registryObjectMethods = PatchRegistryObjectCodeParts(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(registryObjectFirstCodePart),
            vanilla.MainModule.ImportReference(registryObjectLastCodePart));
        int registryObjectFillPlaceHolderMethods = PatchRegistryObjectFillPlaceHolder(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(registryObjectFillPlaceHolderAsset),
            vanilla.MainModule.ImportReference(registryObjectFillPlaceHolderString),
            vanilla.MainModule.ImportReference(registryObjectFillPlaceHolderSingle));
        int assetLocationMethods = PatchAssetLocation(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(assetLocationResolveToDomainAndPath));
        int wildcardUtilMethods = PatchWildcardUtil(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(wildcardUtilMatchTwoArg),
            vanilla.MainModule.ImportReference(wildcardUtilMatchThreeArg),
            vanilla.MainModule.ImportReference(wildcardUtilWildCardReplace));
        int evolvingNatFloatOperators = PatchEvolvingNatFloat(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(evolvingNatFloatEquals));
        int clientAnimatorPatched = PatchClientAnimator(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(clientAnimatorInitMatrices));
        int clientAnimatorGetMaxDepthPatched = PatchClientAnimatorGetMaxDepth(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(clientAnimatorGetMaxDepth));
        int clientAnimatorFrameLerpPatched = PatchClientAnimatorFrameLerp(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(clientAnimatorComputeFrameLerps));
        int animationKeyFramePatched = PatchAnimationKeyFrame(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(createKeyFrameElementDict));
        int colorUtilMethods = PatchColorUtil(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(colorUtilGetIncandescence3),
            vanilla.MainModule.ImportReference(colorUtilGetIncandescence4),
            vanilla.MainModule.ImportReference(colorUtilToRgba),
            vanilla.MainModule.ImportReference(colorUtilGetIncandescenceLegacy));
        int guiCompositionMetrics = PatchGuiCompositionMetrics(vanilla.MainModule, contracts.MainModule);
        int guiTextureDisposals = PatchGuiTextureDisposal(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(deleteGuiTextures),
            vanilla.MainModule.ImportReference(deleteGuiTexture));
        int loadedTextureDisposals = PatchLoadedTextureDisposal(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(disposeLoadedTexture),
            vanilla.MainModule.ImportReference(finalizeLoadedTexture));
        if (loadedTextureDisposals != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 LoadedTexture disposal/finalizer patches, applied {loadedTextureDisposals}.");
        }
        int meshPoolLifecyclePatches = PatchMeshDataPoolLifecycle(vanilla.MainModule, contracts.MainModule);
        if (meshPoolLifecyclePatches != 10)
        {
            throw new InvalidOperationException(
                $"Expected 10 MeshDataPool lifecycle patches, applied {meshPoolLifecyclePatches}.");
        }
        int meshPoolSortPatches = PatchMeshDataPoolSort(vanilla.MainModule, contracts.MainModule);
        if (meshPoolSortPatches != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 MeshDataPool front-to-back sort patches, applied {meshPoolSortPatches}.");
        }
        int meshRecyclerPatches = PatchMeshDataRecycler(vanilla.MainModule, contracts.MainModule);
        if (meshRecyclerPatches != 4)
        {
            throw new InvalidOperationException(
                $"Expected 4 MeshDataRecycler thread-safe patches, applied {meshRecyclerPatches}.");
        }
        int objectCachePatches = PatchObjectCacheUtil(vanilla.MainModule, contracts.MainModule);
        if (objectCachePatches != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 ObjectCacheUtil synchronization patches, applied {objectCachePatches}.");
        }
        int guiSlotNotifiedPatches = PatchGuiSlotNotifiedSubscription(vanilla.MainModule);
        int inventorySearchPatches = PatchInventorySearch(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(itemStackMatchesSearchText),
            vanilla.MainModule.ImportReference(filterSlotGridItems));
        int guiSlotGridPerformancePatches = PatchGuiSlotGridPerformance(vanilla.MainModule, contracts.MainModule);
        if (guiSlotGridPerformancePatches != 4)
        {
            throw new InvalidOperationException(
                $"Expected 4 GuiElementItemSlotGridBase performance patches, applied {guiSlotGridPerformancePatches}.");
        }
        int guiHoverTextPatches = PatchGuiHoverTextSameText(vanilla.MainModule);
        int guiStatbarPatches = PatchGuiStatbarOnDemand(vanilla.MainModule);
        int linkTextGpuUploads = PatchLinkTextGpuUploads(vanilla.MainModule);
        int fontMeasurementMetrics = PatchFontMeasurementMetrics(vanilla.MainModule, contracts.MainModule, cairo.MainModule);
        int textLayoutMetrics = PatchTextDrawUtilFontOverloads(vanilla.MainModule);
        int materializationObserver = PatchMaterializationObserver(vanilla.MainModule, contracts.MainModule, cairo.MainModule);
        int queuedGuiContexts = PatchQueuedGuiContexts(vanilla.MainModule, contracts.MainModule);
        if (queuedGuiContexts != 4) throw new InvalidOperationException($"Expected 4 queued GUI context handoffs, applied {queuedGuiContexts}.");
        int jsonLineInfoPatches = PatchJsonLineInfo(vanilla.MainModule, contracts.MainModule);
        if (jsonLineInfoPatches != 8) throw new InvalidOperationException($"Expected 8 JSON LineInfo patches, applied {jsonLineInfoPatches}.");
        int soundAttributePatches = PatchSoundAttributes(vanilla.MainModule, contracts.MainModule);
        if (soundAttributePatches != 3) throw new InvalidOperationException($"Expected 3 SoundAttributes patches, applied {soundAttributePatches}.");
        if (textLayoutMetrics != 2) throw new InvalidOperationException($"Expected 2 TextDrawUtil font-layout patches, applied {textLayoutMetrics}.");
        if (PatchSvgIconTarget(vanilla.MainModule) != 1) throw new InvalidOperationException("Expected 1 recorded SVG icon target hook.");

        int frustumMethods = PatchFrustumCulling(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(frustumInFrustum),
            vanilla.MainModule.ImportReference(frustumInFrustumShadowPass),
            vanilla.MainModule.ImportReference(frustumInFrustumAndRange),
            vanilla.MainModule.ImportReference(frustumSphereInFrustum));
        if (frustumMethods != 4)
            throw new InvalidOperationException($"Expected 4 FrustumCulling methods patched, applied {frustumMethods}.");

        if (inventoryHooks != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 inventory dirty hooks, applied {inventoryHooks}.");
        }
        if (chiselHooks != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 chisel LOD hook, applied {chiselHooks}.");
        }
        if (chiselShadowHooks != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 chisel LOD shadow hooks, applied {chiselShadowHooks}.");
        }
        if (loggerInitializers != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 logger initializer patch, applied {loggerInitializers}.");
        }
        if (gameVersionLabels != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 game version label patch, applied {gameVersionLabels}.");
        }
        if (mat4fInlined < 7)
        {
            throw new InvalidOperationException(
                $"Expected at least 7 Mat4f inlined methods, applied {mat4fInlined}.");
        }
        if (headControllerFallback != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 EntityHeadController pose fallback, applied {headControllerFallback}.");
        }
        if (threadPoolDiagnostics != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 TyronThreadPool diagnostics patch, applied {threadPoolDiagnostics}.");
        }
        if (clientApiThreadContract != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 ICoreClientAPI.IsTesselationThread contract patch, applied {clientApiThreadContract}.");
        }
        if (ascii85Methods != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 Ascii85 methods patched, applied {ascii85Methods}.");
        }
        if (registryObjectMethods != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 RegistryObject methods patched, applied {registryObjectMethods}.");
        }
        if (registryObjectFillPlaceHolderMethods != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 RegistryObject.FillPlaceHolder methods patched, applied {registryObjectFillPlaceHolderMethods}.");
        }
        if (assetLocationMethods != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 AssetLocation method patched, applied {assetLocationMethods}.");
        }
        if (wildcardUtilMethods != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 WildcardUtil methods patched, applied {wildcardUtilMethods}.");
        }
        if (evolvingNatFloatOperators != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 EvolvingNatFloat equality operators patched, applied {evolvingNatFloatOperators}.");
        }
        if (clientAnimatorPatched != 4)
        {
            throw new InvalidOperationException(
                $"Expected 4 ClientAnimator patches (1 initMatrices + 3 clamps), applied {clientAnimatorPatched}.");
        }
        if (clientAnimatorGetMaxDepthPatched != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 ClientAnimator.getMaxDepth patch, applied {clientAnimatorGetMaxDepthPatched}.");
        }
        if (clientAnimatorFrameLerpPatched != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 ClientAnimator.calculateMatrices frame-lerp patch, applied {clientAnimatorFrameLerpPatched}.");
        }
        if (animationKeyFramePatched != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 AnimationKeyFrame.Resolve patch, applied {animationKeyFramePatched}.");
        }
        if (colorUtilMethods != 4)
        {
            throw new InvalidOperationException(
                $"Expected 4 ColorUtil methods patched, applied {colorUtilMethods}.");
        }
        if (guiCompositionMetrics != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 GuiComposer composition metrics patch, applied {guiCompositionMetrics}.");
        }
        if (guiTextureDisposals != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 GUI texture disposal patches, applied {guiTextureDisposals}.");
        }
        if (guiSlotNotifiedPatches != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 slot grid SlotNotified patches, applied {guiSlotNotifiedPatches}.");
        }
        if (inventorySearchPatches != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 inventory search patches, applied {inventorySearchPatches}.");
        }
        if (guiHoverTextPatches != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 hover text same-text patches, applied {guiHoverTextPatches}.");
        }
        if (guiStatbarPatches != 5)
        {
            throw new InvalidOperationException(
                $"Expected 5 statbar on-demand patches, applied {guiStatbarPatches}.");
        }
        if (fontMeasurementMetrics != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 CairoFont measurement metrics patches, applied {fontMeasurementMetrics}.");
        }
        if (CountMaterializationObserverHooks(vanilla.MainModule) != 1 || CountMaterializationObserverCallbacks(vanilla.MainModule) != 1)
        {
            throw new InvalidOperationException("Expected exactly one module-initializer subscription for Cairo materialization diagnostics.");
        }

        int typeForwards = InjectTypeForwards(vanilla, contracts);

        var selfReferenceErrors = SelfConsistencyVerifier.VerifySelfReferences(vanilla.MainModule);
        if (selfReferenceErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Patched API contains invalid self references:\n" +
                string.Join("\n", selfReferenceErrors));
        }

        var ilErrors = IlStackVerifier.VerifyModule(vanilla.MainModule);
        if (ilErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Patched API contains invalid IL:\n" +
                string.Join("\n", ilErrors));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        AssemblyWriter.Write(vanilla, outputPath, preserveSymbols);
        using (var written = AssemblyDefinition.ReadAssembly(outputPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            if (CountMaterializationObserverHooks(written.MainModule) != 1 || CountMaterializationObserverCallbacks(written.MainModule) != 1)
            {
                throw new InvalidOperationException("The patched API output is missing its unique Cairo materialization observer module-initializer hook.");
            }
        }
        if (preserveSymbols)
        {
            Console.WriteLine($"  Wrote matching symbols: {Path.ChangeExtension(outputPath, ".pdb")}");
        }
        Console.WriteLine(
            $"API patch complete: {inventoryHooks} inventory hooks, {chiselHooks} chisel LOD hook, " +
            $"{chiselShadowHooks} chisel LOD shadow hooks, " +
            $"{loggerInitializers} symbol-independent logger initializer, " +
                $"{gameVersionLabels} game version label, {mat4fInlined} Mat4f inlined, " +
                $"{headControllerFallback} head controller pose fallback, " +
                $"{threadPoolDiagnostics} thread pool diagnostics patch, " +
                $"{clientApiThreadContract} client API thread contract, " +
                $"{ascii85Methods} Ascii85 methods, {registryObjectMethods} RegistryObject code part methods, " +
                $"{assetLocationMethods} AssetLocation resolve method, " +
                $"{wildcardUtilMethods} WildcardUtil methods, " +
                $"{evolvingNatFloatOperators} EvolvingNatFloat equality operators, " +
                $"{colorUtilMethods} ColorUtil scalar incandescence + RGBA methods, " +
                $"{guiCompositionMetrics} GUI composition metrics hook, " +
                $"{guiTextureDisposals} GUI texture disposals, " +
                $"{meshPoolLifecyclePatches} MeshDataPool lifecycle hooks, " +
                $"{meshPoolSortPatches} MeshDataPool sort hooks, " +
                $"{guiSlotNotifiedPatches} slot grid SlotNotified subscription fixes, " +
                $"{inventorySearchPatches} inventory search optimizations, " +
                $"{guiHoverTextPatches} hover text same-text skips, " +
                $"{guiStatbarPatches} statbar on-demand edits, " +
                $"{materializationObserver} Cairo materialization observer module initializer, " +
                $"{typeForwards} type forwards.");
        return true;
    }

    internal static int PatchColorUtil(
        ModuleDefinition module,
        MethodReference getIncandescence3Bridge,
        MethodReference getIncandescence4Bridge,
        MethodReference toRgbaBridge,
        MethodReference getIncandescenceLegacyBridge)
    {
        const string typeName = "Vintagestory.API.MathTools.ColorUtil";
        var colorUtil = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        int patched = 0;

        // 1. GetIncandescenceColorFloats(int, out float, out float, out float)
        var mGetInc3 = colorUtil.Methods.FirstOrDefault(m =>
            m.IsStatic &&
            m.Name == "GetIncandescenceColorFloats" &&
            m.Parameters.Count == 4 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            m.Parameters[1].ParameterType is ByReferenceType &&
            m.Parameters[2].ParameterType is ByReferenceType &&
            m.Parameters[3].ParameterType is ByReferenceType);

        if (mGetInc3 == null)
        {
            mGetInc3 = new MethodDefinition(
                "GetIncandescenceColorFloats",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            mGetInc3.Parameters.Add(new ParameterDefinition("temperature", ParameterAttributes.None, module.TypeSystem.Int32));
            mGetInc3.Parameters.Add(new ParameterDefinition("r", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mGetInc3.Parameters.Add(new ParameterDefinition("g", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mGetInc3.Parameters.Add(new ParameterDefinition("b", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            colorUtil.Methods.Add(mGetInc3);
        }
        if (mGetInc3.Body == null) mGetInc3.Body = new MethodBody(mGetInc3) { InitLocals = false };
        RewriteFourArgForwarder(mGetInc3, getIncandescence3Bridge);
        patched++;

        // 2. GetIncandescenceColorFloats(int, out float, out float, out float, out float)
        var mGetInc4 = colorUtil.Methods.FirstOrDefault(m =>
            m.IsStatic &&
            m.Name == "GetIncandescenceColorFloats" &&
            m.Parameters.Count == 5 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            m.Parameters[1].ParameterType is ByReferenceType &&
            m.Parameters[2].ParameterType is ByReferenceType &&
            m.Parameters[3].ParameterType is ByReferenceType &&
            m.Parameters[4].ParameterType is ByReferenceType);

        if (mGetInc4 == null)
        {
            mGetInc4 = new MethodDefinition(
                "GetIncandescenceColorFloats",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            mGetInc4.Parameters.Add(new ParameterDefinition("temperature", ParameterAttributes.None, module.TypeSystem.Int32));
            mGetInc4.Parameters.Add(new ParameterDefinition("r", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mGetInc4.Parameters.Add(new ParameterDefinition("g", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mGetInc4.Parameters.Add(new ParameterDefinition("b", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mGetInc4.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            colorUtil.Methods.Add(mGetInc4);
        }
        if (mGetInc4.Body == null) mGetInc4.Body = new MethodBody(mGetInc4) { InitLocals = false };
        RewriteFiveArgForwarder(mGetInc4, getIncandescence4Bridge);
        patched++;

        // 3. ToRGBAFloats(int, out float, out float, out float, out float)
        var mToRgba = colorUtil.Methods.FirstOrDefault(m =>
            m.IsStatic &&
            m.Name == "ToRGBAFloats" &&
            m.Parameters.Count == 5 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            m.Parameters[1].ParameterType is ByReferenceType &&
            m.Parameters[2].ParameterType is ByReferenceType &&
            m.Parameters[3].ParameterType is ByReferenceType &&
            m.Parameters[4].ParameterType is ByReferenceType);

        if (mToRgba == null)
        {
            mToRgba = new MethodDefinition(
                "ToRGBAFloats",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            mToRgba.Parameters.Add(new ParameterDefinition("color", ParameterAttributes.None, module.TypeSystem.Int32));
            mToRgba.Parameters.Add(new ParameterDefinition("r", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mToRgba.Parameters.Add(new ParameterDefinition("g", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mToRgba.Parameters.Add(new ParameterDefinition("b", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            mToRgba.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Single)));
            colorUtil.Methods.Add(mToRgba);
        }
        if (mToRgba.Body == null) mToRgba.Body = new MethodBody(mToRgba) { InitLocals = false };
        RewriteFiveArgForwarder(mToRgba, toRgbaBridge);
        patched++;

        // 4. GetIncandescenceColorAsColor4f(int)
        var mLegacy = colorUtil.Methods.FirstOrDefault(m =>
            m.IsStatic &&
            m.Name == "GetIncandescenceColorAsColor4f" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);

        if (mLegacy != null)
        {
            RewriteSingleArgForwarder(mLegacy, getIncandescenceLegacyBridge);
            patched++;
        }

        Console.WriteLine($"  API PATCHED: {typeName} scalar incandescence + RGBA methods -> OptimumApiBridge");
        return patched;
    }

    internal static int PatchAscii85(ModuleDefinition module, MethodReference encodeBridge, MethodReference decodeBridge)
    {
        const string typeName = "Vintagestory.API.MathTools.Ascii85";
        var ascii85 = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var encode = ascii85.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "Encode" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType is ArrayType { ElementType.MetadataType: MetadataType.Byte } &&
            method.ReturnType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{typeName}.Encode(byte[]) is missing from the vanilla API.");

        var decode = ascii85.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "Decode" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType is ArrayType { ElementType.MetadataType: MetadataType.Byte })
            ?? throw new InvalidOperationException($"{typeName}.Decode(string) is missing from the vanilla API.");

        RewriteSingleArgForwarder(encode, encodeBridge);
        RewriteSingleArgForwarder(decode, decodeBridge);

        Console.WriteLine($"  API PATCHED: {typeName}.Encode(byte[]) + Decode(string) -> OptimumApiBridge");
        return 2;
    }

    private static void RewriteSingleArgForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    internal static int PatchRegistryObjectCodeParts(
        ModuleDefinition module,
        MethodReference firstCodePartBridge,
        MethodReference lastCodePartBridge)
    {
        const string typeName = "Vintagestory.API.Common.RegistryObject";
        var registryObject = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var firstCodePart = registryObject.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "FirstCodePart" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            method.ReturnType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{typeName}.FirstCodePart(int) is missing from the vanilla API.");

        var lastCodePart = registryObject.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "LastCodePart" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
            method.ReturnType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{typeName}.LastCodePart(int) is missing from the vanilla API.");

        RewriteTwoArgForwarder(firstCodePart, firstCodePartBridge);
        RewriteTwoArgForwarder(lastCodePart, lastCodePartBridge);

        Console.WriteLine($"  API PATCHED: {typeName}.FirstCodePart(int) + LastCodePart(int) -> OptimumApiBridge");
        return 2;
    }

    internal static int PatchRegistryObjectFillPlaceHolder(
        ModuleDefinition module,
        MethodReference fillPlaceHolderAssetBridge,
        MethodReference fillPlaceHolderStringBridge,
        MethodReference fillPlaceHolderSingleBridge)
    {
        const string typeName = "Vintagestory.API.Common.RegistryObject";
        var registryObject = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var fillAsset = registryObject.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "FillPlaceHolder" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
            method.ReturnType.FullName == "Vintagestory.API.Common.AssetLocation")
            ?? throw new InvalidOperationException($"{typeName}.FillPlaceHolder(AssetLocation, OrderedDictionary<string, string>) is missing from the vanilla API.");

        var fillString = registryObject.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "FillPlaceHolder" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{typeName}.FillPlaceHolder(string, OrderedDictionary<string, string>) is missing from the vanilla API.");

        var fillSingle = registryObject.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "FillPlaceHolder" &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{typeName}.FillPlaceHolder(string, string, string) is missing from the vanilla API.");

        RewriteTwoArgForwarder(fillAsset, fillPlaceHolderAssetBridge);
        RewriteTwoArgForwarder(fillString, fillPlaceHolderStringBridge);
        RewriteThreeArgForwarder(fillSingle, fillPlaceHolderSingleBridge);

        Console.WriteLine($"  API PATCHED: {typeName}.FillPlaceHolder (3 overloads) -> OptimumApiBridge");
        return 3;
    }

    internal static int PatchAssetLocation(
        ModuleDefinition module,
        MethodReference resolveBridge)
    {
        const string typeName = "Vintagestory.API.Common.AssetLocation";
        var assetLocation = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var resolveMethod = assetLocation.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "ResolveToDomainAndPath" &&
            method.Parameters.Count == 3)
            ?? throw new InvalidOperationException($"{typeName}.ResolveToDomainAndPath is missing from the vanilla API.");

        RewriteThreeArgForwarder(resolveMethod, resolveBridge);

        Console.WriteLine($"  API PATCHED: {typeName}.ResolveToDomainAndPath -> OptimumApiBridge");
        return 1;
    }

    private static void RewriteTwoArgForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    internal static int PatchWildcardUtil(
        ModuleDefinition module,
        MethodReference matchTwoArgBridge,
        MethodReference matchThreeArgBridge,
        MethodReference? wildCardReplaceBridge = null)
    {
        const string typeName = "Vintagestory.API.Util.WildcardUtil";
        var wildcardUtil = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var matchTwoArg = wildcardUtil.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "Match" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
            method.Parameters[1].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
            method.ReturnType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException($"{typeName}.Match(AssetLocation, AssetLocation) is missing from the vanilla API.");

        var matchThreeArg = wildcardUtil.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "Match" &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
            method.Parameters[1].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
            method.Parameters[2].ParameterType is ArrayType { ElementType.MetadataType: MetadataType.String } &&
            method.ReturnType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException($"{typeName}.Match(AssetLocation, AssetLocation, string[]) is missing from the vanilla API.");

        RewriteTwoArgForwarder(matchTwoArg, matchTwoArgBridge);
        RewriteThreeArgForwarder(matchThreeArg, matchThreeArgBridge);

        int count = 2;
        if (wildCardReplaceBridge != null)
        {
            var wildCardReplace = wildcardUtil.Methods.SingleOrDefault(method =>
                method.IsStatic &&
                method.Name == "WildCardReplace" &&
                method.Parameters.Count == 3 &&
                method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
                method.Parameters[1].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation" &&
                method.Parameters[2].ParameterType.FullName == "Vintagestory.API.Common.AssetLocation");

            if (wildCardReplace != null)
            {
                RewriteThreeArgForwarder(wildCardReplace, wildCardReplaceBridge);
                count++;
            }
        }

        Console.WriteLine($"  API PATCHED: {typeName}.Match(AssetLocation, AssetLocation) + Match(..., string[]) + {count - 2} additional methods -> OptimumApiBridge");
        return count;
    }

    internal static int PatchEvolvingNatFloat(ModuleDefinition module, MethodReference equalsBridge)
    {
        const string typeName = "Vintagestory.API.MathTools.EvolvingNatFloat";
        var evolvingNatFloat = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var opEquality = evolvingNatFloat.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "op_Equality" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == typeName &&
            method.Parameters[1].ParameterType.FullName == typeName &&
            method.ReturnType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException($"{typeName}.op_Equality is missing from the vanilla API.");

        var opInequality = evolvingNatFloat.Methods.SingleOrDefault(method =>
            method.IsStatic &&
            method.Name == "op_Inequality" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == typeName &&
            method.Parameters[1].ParameterType.FullName == typeName &&
            method.ReturnType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException($"{typeName}.op_Inequality is missing from the vanilla API.");

        RewriteTwoArgForwarder(opEquality, equalsBridge);
        RewriteTwoArgNegatedForwarder(opInequality, equalsBridge);

        var iequatableOpen = module.ImportReference(typeof(IEquatable<>));
        var iequatableOfSelf = new GenericInstanceType(iequatableOpen);
        iequatableOfSelf.GenericArguments.Add(evolvingNatFloat);

        if (!evolvingNatFloat.Interfaces.Any(i => i.InterfaceType.FullName == iequatableOfSelf.FullName))
        {
            evolvingNatFloat.Interfaces.Add(new InterfaceImplementation(iequatableOfSelf));
        }

        var equalsMethod = evolvingNatFloat.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "Equals" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == typeName &&
            method.ReturnType.MetadataType == MetadataType.Boolean);

        if (equalsMethod == null)
        {
            equalsMethod = new MethodDefinition(
                "Equals",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final,
                module.TypeSystem.Boolean);
            equalsMethod.Parameters.Add(new ParameterDefinition("other", ParameterAttributes.None, evolvingNatFloat));
            evolvingNatFloat.Methods.Add(equalsMethod);
        }

        ClearDebugInformation(equalsMethod);
        var body = new MethodBody(equalsMethod) { InitLocals = false };
        var il = body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldobj, evolvingNatFloat));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Call, equalsBridge));
        il.Append(Instruction.Create(OpCodes.Ret));
        equalsMethod.Body = body;

        Console.WriteLine($"  API PATCHED: {typeName}.op_Equality + op_Inequality + Equals(EvolvingNatFloat) -> OptimumApiBridge");
        return 2;
    }

    internal static int PatchClientAnimator(ModuleDefinition module, MethodReference initMatricesBridge)
    {
        const string typeName = "Vintagestory.API.Common.ClientAnimator";
        var clientAnimator = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var initMatrices = clientAnimator.Methods.SingleOrDefault(m =>
            m.Name == "initMatrices" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{typeName}.initMatrices(int) is missing from the vanilla API.");

        RewriteTwoArgForwarder(initMatrices, initMatricesBridge);

        var maxConcurrentAnimationsField = clientAnimator.Fields.SingleOrDefault(f =>
            f.Name == "MaxConcurrentAnimations" && f.IsStatic)
            ?? throw new InvalidOperationException($"{typeName}.MaxConcurrentAnimations is missing from the vanilla API.");

        var mathMinMethod = module.ImportReference(
            typeof(Math).GetMethod(nameof(Math.Min), new[] { typeof(int), typeof(int) })!)
            ?? throw new InvalidOperationException("System.Math.Min(int, int) is missing.");

        int clampedSites = 0;
        foreach (var method in clientAnimator.Methods.Where(m => m.Name == "calculateMatrices"))
        {
            if (!method.HasBody) continue;
            var instructions = method.Body.Instructions;
            var processor = method.Body.GetILProcessor();

            for (int i = 0; i < instructions.Count; i++)
            {
                var ins = instructions[i];
                if (ins.OpCode == OpCodes.Ldfld &&
                    ins.Operand is FieldReference fr &&
                    fr.Name == "activeAnimCount")
                {
                    var ldMax = Instruction.Create(OpCodes.Ldsfld, maxConcurrentAnimationsField);
                    var callMin = Instruction.Create(OpCodes.Call, mathMinMethod);

                    processor.InsertAfter(ins, ldMax);
                    processor.InsertAfter(ldMax, callMin);
                    i += 2;
                    clampedSites++;
                }
            }
        }

        Console.WriteLine($"  API PATCHED: {typeName}.initMatrices -> OptimumApiBridge + {clampedSites} activeAnimCount clamps");
        return 1 + clampedSites;
    }

    internal static int PatchClientAnimatorGetMaxDepth(ModuleDefinition module, MethodReference getMaxDepthBridge)
    {
        const string typeName = "Vintagestory.API.Common.ClientAnimator";
        var clientAnimator = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var getMaxDepth = clientAnimator.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "getMaxDepth" &&
            method.Parameters.Count == 2 &&
            method.ReturnType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{typeName}.getMaxDepth(List<ElementPose>, int) is missing from the vanilla API.");

        ClearDebugInformation(getMaxDepth);
        var body = new MethodBody(getMaxDepth)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldarg_2));
        processor.Append(Instruction.Create(OpCodes.Call, getMaxDepthBridge));
        processor.Append(Instruction.Create(OpCodes.Ret));
        getMaxDepth.Body = body;

        Console.WriteLine($"  API PATCHED: {typeName}.getMaxDepth -> OptimumApiBridge");
        return 1;
    }

    internal static int PatchAnimationKeyFrame(ModuleDefinition module, MethodReference createMapBridge)
    {
        const string typeName = "Vintagestory.API.Common.AnimationKeyFrame";
        var keyFrame = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var resolveDict = keyFrame.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "Resolve" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.Name.StartsWith("Dictionary", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{typeName}.Resolve(Dictionary<string, ShapeElement>) is missing from the vanilla API.");

        if (!resolveDict.HasBody)
        {
            throw new InvalidOperationException($"{resolveDict.FullName} has no method body.");
        }

        ClearDebugInformation(resolveDict);
        var instructions = resolveDict.Body.Instructions;
        if (instructions.Any(ins =>
            ins.OpCode == OpCodes.Call &&
            ins.Operand is MethodReference existing &&
            existing.FullName == createMapBridge.FullName))
        {
            return 1;
        }

        var fastDictCtors = instructions
            .Where(ins =>
                ins.OpCode == OpCodes.Newobj &&
                ins.Operand is MethodReference ctor &&
                ctor.Name == ".ctor" &&
                ctor.Parameters.Count == 1 &&
                ctor.DeclaringType.FullName.StartsWith("Vintagestory.API.Datastructures.FastSmallDictionary", StringComparison.Ordinal))
            .ToArray();

        if (fastDictCtors.Length != 1)
        {
            throw new InvalidOperationException(
                $"{resolveDict.FullName}: expected 1 FastSmallDictionary(int) construction, found {fastDictCtors.Length}.");
        }

        fastDictCtors[0].OpCode = OpCodes.Call;
        fastDictCtors[0].Operand = createMapBridge;

        Console.WriteLine($"  API PATCHED: {typeName}.Resolve(Dictionary<string, ShapeElement>) -> OptimumApiBridge");
        return 1;
    }

    internal static int PatchClientAnimatorFrameLerp(ModuleDefinition module, MethodReference computeLerpsBridge)
    {
        const string typeName = "Vintagestory.API.Common.ClientAnimator";
        var clientAnimator = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");
        var animatorBase = clientAnimator.BaseType?.Resolve()
            ?? throw new InvalidOperationException($"{typeName} base type could not be resolved.");

        var floatArrayType = new ArrayType(module.TypeSystem.Single);
        var frameLerpsField = clientAnimator.Fields.FirstOrDefault(field => field.Name == "frameLerps");
        if (frameLerpsField == null)
        {
            frameLerpsField = new FieldDefinition("frameLerps", FieldAttributes.Private, floatArrayType);
            clientAnimator.Fields.Add(frameLerpsField);
        }

        var prevFrameField = clientAnimator.Fields.SingleOrDefault(f => f.Name == "prevFrame")
            ?? throw new InvalidOperationException($"{typeName}.prevFrame is missing from the vanilla API.");
        var nextFrameField = clientAnimator.Fields.SingleOrDefault(f => f.Name == "nextFrame")
            ?? throw new InvalidOperationException($"{typeName}.nextFrame is missing from the vanilla API.");
        var curAnimsField = animatorBase.Fields.SingleOrDefault(f => f.Name == "CurAnims")
            ?? throw new InvalidOperationException("AnimatorBase.CurAnims is missing from the vanilla API.");

        var outerCalc = clientAnimator.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "calculateMatrices" && m.Parameters.Count == 1)
            ?? throw new InvalidOperationException($"{typeName}.calculateMatrices(float) is missing from the vanilla API.");
        var innerCalc = clientAnimator.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "calculateMatrices" && m.Parameters.Count == 8)
            ?? throw new InvalidOperationException($"{typeName}.calculateMatrices(8 params) is missing from the vanilla API.");

        if (!outerCalc.HasBody || !innerCalc.HasBody)
        {
            throw new InvalidOperationException($"{typeName}.calculateMatrices has no method body.");
        }

        ClearDebugInformation(outerCalc);
        var outerIns = outerCalc.Body.Instructions;
        if (!outerIns.Any(ins =>
            ins.OpCode == OpCodes.Call &&
            ins.Operand is MethodReference mr &&
            mr.FullName == computeLerpsBridge.FullName))
        {
            int recursiveIdx = -1;
            for (int i = 0; i < outerIns.Count; i++)
            {
                if (outerIns[i].OpCode == OpCodes.Call &&
                    outerIns[i].Operand is MethodReference mr &&
                    mr.Name == "calculateMatrices" &&
                    mr.Parameters.Count == 8)
                {
                    recursiveIdx = i;
                    break;
                }
            }
            if (recursiveIdx < 1)
            {
                throw new InvalidOperationException($"{outerCalc.FullName}: recursive calculateMatrices call not found.");
            }

            int loopBranchIdx = -1;
            for (int i = recursiveIdx - 1; i >= 0; i--)
            {
                if (outerIns[i].OpCode.Code is Code.Blt or Code.Blt_S)
                {
                    loopBranchIdx = i;
                    break;
                }
            }
            if (loopBranchIdx < 0 || loopBranchIdx + 1 >= outerIns.Count)
            {
                throw new InvalidOperationException($"{outerCalc.FullName}: activeAnimCount loop branch not found.");
            }

            VariableDefinition? loopCountVar = null;
            for (int i = loopBranchIdx - 1; i >= 2; i--)
            {
                if (outerIns[i].OpCode == OpCodes.Ldfld &&
                    outerIns[i].Operand is FieldReference fr &&
                    fr.Name == "activeAnimCount" &&
                    outerIns[i - 1].OpCode == OpCodes.Ldarg_0)
                {
                    loopCountVar = ResolveLdlocVariable(outerCalc.Body, outerIns[i - 2]);
                    break;
                }
            }
            if (loopCountVar == null)
            {
                throw new InvalidOperationException($"{outerCalc.FullName}: activeAnimCount loop counter variable not found.");
            }

            Instruction insertTarget = outerIns[loopBranchIdx + 1];
            Instruction[] precompute =
            [
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, module.ImportReference(curAnimsField)),
                CreateLdloc(loopCountVar),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, prevFrameField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, nextFrameField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldflda, frameLerpsField),
                Instruction.Create(OpCodes.Call, computeLerpsBridge),
            ];
            var outerProc = outerCalc.Body.GetILProcessor();
            for (int i = 0; i < precompute.Length; i++)
            {
                outerProc.InsertBefore(insertTarget, precompute[i]);
            }
            RetargetJumpTargets(outerIns, insertTarget, precompute[0]);
        }

        ClearDebugInformation(innerCalc);
        var innerIns = innerCalc.Body.Instructions;
        if (!innerIns.Any(ins =>
            ins.OpCode == OpCodes.Ldfld &&
            ins.Operand is FieldReference fr &&
            fr.Name == "frameLerps"))
        {
            var prevLoads = innerIns.Where(ins =>
                ins.OpCode == OpCodes.Ldfld &&
                ins.Operand is FieldReference fr &&
                fr.Name == "prevFrame").ToArray();
            if (prevLoads.Length != 1)
            {
                throw new InvalidOperationException($"{innerCalc.FullName}: expected 1 prevFrame read, found {prevLoads.Length}.");
            }

            int prevLoadIdx = innerIns.IndexOf(prevLoads[0]);
            int startIdx = prevLoadIdx - 1;
            if (startIdx < 0 || innerIns[startIdx].OpCode != OpCodes.Ldarg_0)
            {
                throw new InvalidOperationException($"{innerCalc.FullName}: expected ldarg.0 before prevFrame read.");
            }

            VariableDefinition animIndexVar = ResolveLdlocVariable(innerCalc.Body, innerIns[prevLoadIdx + 1])
                ?? throw new InvalidOperationException($"{innerCalc.FullName}: expected animIndex ldloc after prevFrame.");

            int addIdx = -1;
            for (int i = prevLoadIdx + 1; i < Math.Min(innerIns.Count, prevLoadIdx + 64); i++)
            {
                if (innerIns[i].OpCode == OpCodes.Callvirt &&
                    innerIns[i].Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "Vintagestory.API.Common.ElementPose" &&
                    mr.Name == "Add")
                {
                    addIdx = i;
                    break;
                }
            }
            if (addIdx < 0)
            {
                throw new InvalidOperationException($"{innerCalc.FullName}: ElementPose.Add call not found after prevFrame.");
            }

            int stLerpIdx = -1;
            VariableDefinition? lerpVar = null;
            for (int i = prevLoadIdx + 2; i < addIdx; i++)
            {
                if (innerIns[i].OpCode == OpCodes.Div && i + 1 < addIdx)
                {
                    VariableDefinition? v = ResolveStlocVariable(innerCalc.Body, innerIns[i + 1]);
                    if (v != null)
                    {
                        stLerpIdx = i + 1;
                        lerpVar = v;
                        break;
                    }
                }
            }
            if (stLerpIdx < 0 || lerpVar == null)
            {
                throw new InvalidOperationException($"{innerCalc.FullName}: div + stloc lerp pattern not found.");
            }

            var removedInnerSet = new HashSet<Instruction>();
            for (int i = startIdx + 1; i <= stLerpIdx; i++)
            {
                removedInnerSet.Add(innerIns[i]);
            }
            for (int i = 0; i < innerIns.Count; i++)
            {
                if (i >= startIdx && i <= stLerpIdx) continue;
                object? op = innerIns[i].Operand;
                if (op is Instruction target && removedInnerSet.Contains(target))
                {
                    throw new InvalidOperationException($"{innerCalc.FullName}: unexpected external branch into lerp block.");
                }
                if (op is Instruction[] targets && targets.Any(removedInnerSet.Contains))
                {
                    throw new InvalidOperationException($"{innerCalc.FullName}: unexpected external switch into lerp block.");
                }
            }

            Instruction startIns = innerIns[startIdx];
            Instruction[] replacement =
            [
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, frameLerpsField),
                CreateLdloc(animIndexVar),
                Instruction.Create(OpCodes.Ldelem_R4),
                CreateStloc(lerpVar),
            ];
            var innerProc = innerCalc.Body.GetILProcessor();
            for (int i = 0; i < replacement.Length; i++)
            {
                innerProc.InsertBefore(startIns, replacement[i]);
            }
            RetargetJumpTargets(innerIns, startIns, replacement[0]);
            for (int i = stLerpIdx + replacement.Length; i >= startIdx + replacement.Length; i--)
            {
                innerProc.Remove(innerIns[i]);
            }
        }

        Console.WriteLine($"  API PATCHED: {typeName}.calculateMatrices precomputed frameLerps");
        return 1;
    }

    private static void RetargetJumpTargets(
        Mono.Collections.Generic.Collection<Instruction> instructions,
        Instruction oldTarget,
        Instruction newTarget)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            var ins = instructions[i];
            if (ReferenceEquals(ins.Operand, oldTarget))
            {
                ins.Operand = newTarget;
            }
            else if (ins.Operand is Instruction[] targets)
            {
                for (int t = 0; t < targets.Length; t++)
                {
                    if (ReferenceEquals(targets[t], oldTarget))
                    {
                        targets[t] = newTarget;
                    }
                }
            }
        }
    }

    private static VariableDefinition? ResolveLdlocVariable(MethodBody body, Instruction ins)
    {
        return ins.OpCode.Code switch
        {
            Code.Ldloc_0 => body.Variables[0],
            Code.Ldloc_1 => body.Variables[1],
            Code.Ldloc_2 => body.Variables[2],
            Code.Ldloc_3 => body.Variables[3],
            Code.Ldloc_S or Code.Ldloc => ins.Operand as VariableDefinition,
            _ => null,
        };
    }

    private static VariableDefinition? ResolveStlocVariable(MethodBody body, Instruction ins)
    {
        return ins.OpCode.Code switch
        {
            Code.Stloc_0 => body.Variables[0],
            Code.Stloc_1 => body.Variables[1],
            Code.Stloc_2 => body.Variables[2],
            Code.Stloc_3 => body.Variables[3],
            Code.Stloc_S or Code.Stloc => ins.Operand as VariableDefinition,
            _ => null,
        };
    }

    private static Instruction CreateLdloc(VariableDefinition variable)
    {
        return variable.Index switch
        {
            0 => Instruction.Create(OpCodes.Ldloc_0),
            1 => Instruction.Create(OpCodes.Ldloc_1),
            2 => Instruction.Create(OpCodes.Ldloc_2),
            3 => Instruction.Create(OpCodes.Ldloc_3),
            <= byte.MaxValue => Instruction.Create(OpCodes.Ldloc_S, variable),
            _ => Instruction.Create(OpCodes.Ldloc, variable),
        };
    }

    private static Instruction CreateStloc(VariableDefinition variable)
    {
        return variable.Index switch
        {
            0 => Instruction.Create(OpCodes.Stloc_0),
            1 => Instruction.Create(OpCodes.Stloc_1),
            2 => Instruction.Create(OpCodes.Stloc_2),
            3 => Instruction.Create(OpCodes.Stloc_3),
            <= byte.MaxValue => Instruction.Create(OpCodes.Stloc_S, variable),
            _ => Instruction.Create(OpCodes.Stloc, variable),
        };
    }

    private static void RewriteTwoArgNegatedForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        processor.Append(Instruction.Create(OpCodes.Ceq));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    private static void RewriteThreeArgForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldarg_2));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    internal static int PatchFrustumCulling(
        ModuleDefinition module,
        MethodReference inFrustumBridge,
        MethodReference inFrustumShadowPassBridge,
        MethodReference inFrustumAndRangeBridge,
        MethodReference sphereInFrustumBridge)
    {
        const string typeName = "Vintagestory.API.Client.FrustumCulling";
        var culler = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        var inFrustum = culler.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "InFrustum" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.Name == "Sphere")
            ?? throw new InvalidOperationException($"{typeName}.InFrustum(Sphere) is missing from the vanilla API.");

        var inFrustumShadowPass = culler.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "InFrustumShadowPass" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.Name == "Sphere")
            ?? throw new InvalidOperationException($"{typeName}.InFrustumShadowPass(Sphere) is missing from the vanilla API.");

        var inFrustumAndRange = culler.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "InFrustumAndRange" &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.Name == "Sphere")
            ?? throw new InvalidOperationException($"{typeName}.InFrustumAndRange(Sphere, bool, int) is missing from the vanilla API.");

        var sphereInFrustum = culler.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "SphereInFrustum" &&
            method.Parameters.Count == 4 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Double)
            ?? throw new InvalidOperationException($"{typeName}.SphereInFrustum(double, double, double, double) is missing from the vanilla API.");

        RewriteTwoArgForwarder(inFrustum, inFrustumBridge);
        RewriteTwoArgForwarder(inFrustumShadowPass, inFrustumShadowPassBridge);
        RewriteFourArgForwarder(inFrustumAndRange, inFrustumAndRangeBridge);
        RewriteFiveArgForwarder(sphereInFrustum, sphereInFrustumBridge);

        Console.WriteLine($"  API PATCHED: {typeName} SIMD forwarders (InFrustum, InFrustumShadowPass, InFrustumAndRange, SphereInFrustum)");
        return 4;
    }

    private static void RewriteFourArgForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldarg_2));
        processor.Append(Instruction.Create(OpCodes.Ldarg_3));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    private static void RewriteFiveArgForwarder(MethodDefinition target, MethodReference bridgeMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldarg_2));
        processor.Append(Instruction.Create(OpCodes.Ldarg_3));
        processor.Append(Instruction.Create(OpCodes.Ldarg_S, target.Parameters[3]));
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    internal static int PatchMaterializationObserver(ModuleDefinition module, ModuleDefinition contracts, ModuleDefinition cairo)
    {
        const string metricsTypeName = "Vintagestory.API.Config.OptimumGuiMetrics";
        const string recorderTypeName = "Cairo.SurfaceRecorder";
        const string callbackName = "RecordMaterialization";
        const string eventAdderName = "add_MaterializationCompleted";

        var metrics = contracts.GetType(metricsTypeName)
            ?? throw new InvalidOperationException($"{metricsTypeName} is missing from the contracts assembly.");
        var callback = metrics.Methods.SingleOrDefault(method =>
            method.Name == callbackName && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException("OptimumGuiMetrics.RecordMaterialization(string, bool) is missing from contracts.");
        var recorder = cairo.GetType(recorderTypeName)
            ?? throw new InvalidOperationException($"{recorderTypeName} is missing from cairo-sharp.");
        var eventAdder = recorder.Methods.SingleOrDefault(method =>
            method.Name == eventAdderName && method.IsStatic && method.Parameters.Count == 1)
            ?? throw new InvalidOperationException("SurfaceRecorder.MaterializationCompleted event accessor is missing from cairo-sharp.");

        var moduleType = module.Types.SingleOrDefault(type => type.Name == "<Module>")
            ?? throw new InvalidOperationException("The target API module type is missing.");
        var initializer = moduleType.Methods.SingleOrDefault(method => method.Name == ".cctor");
        if (initializer == null)
        {
            initializer = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            moduleType.Methods.Add(initializer);
        }

        int existingHooks = CountMaterializationObserverHooks(module);
        int existingCallbacks = CountMaterializationObserverCallbacks(module);
        if (existingHooks == 1 && existingCallbacks == 1) return 0;
        if (existingHooks != 0)
            throw new InvalidOperationException($"Expected no or one existing Cairo materialization observer hook, found {existingHooks}.");
        if (existingCallbacks != 0)
            throw new InvalidOperationException($"Expected no or one existing materialization observer callback, found {existingCallbacks}.");

        var adderReference = module.ImportReference(eventAdder);
        var callbackReference = module.ImportReference(callback);
        var delegateType = module.ImportReference(eventAdder.Parameters[0].ParameterType);
        var delegateConstructor = new MethodReference(".ctor", module.TypeSystem.Void, delegateType)
        {
            HasThis = true,
            CallingConvention = MethodCallingConvention.Default
        };
        delegateConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        delegateConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));

        var instructions = initializer.Body.Instructions;
        int insertionIndex = 0;
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Ldnull));
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Ldftn, callbackReference));
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Newobj, delegateConstructor));
        instructions.Insert(insertionIndex, Instruction.Create(OpCodes.Call, adderReference));
        return 1;
    }

    internal static int CountMaterializationObserverHooks(ModuleDefinition module)
    {
        var initializer = module.Types.SingleOrDefault(type => type.Name == "<Module>")?.Methods
            .SingleOrDefault(method => method.Name == ".cctor");
        if (initializer?.HasBody != true) return 0;
        return initializer.Body.Instructions.Count(instruction =>
            instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference method &&
            method.Name == "add_MaterializationCompleted" && method.DeclaringType.FullName == "Cairo.SurfaceRecorder");
    }

    internal static int CountMaterializationObserverCallbacks(ModuleDefinition module)
    {
        var initializer = module.Types.SingleOrDefault(type => type.Name == "<Module>")?.Methods
            .SingleOrDefault(method => method.Name == ".cctor");
        if (initializer?.HasBody != true) return 0;
        return initializer.Body.Instructions.Count(instruction =>
            instruction.OpCode == OpCodes.Ldftn && instruction.Operand is MethodReference method &&
            method.Name == "RecordMaterialization" && method.DeclaringType.FullName == "Vintagestory.API.Config.OptimumGuiMetrics");
    }

    internal static int PatchSvgIconTarget(ModuleDefinition module)
    {
        var icon = module.GetType("Vintagestory.API.Client.IconUtil") ?? throw new InvalidOperationException("IconUtil is missing from the vanilla API.");
        IEnumerable<TypeDefinition> Descendants(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Descendants));
        int count = 0;
        foreach (var method in Descendants(icon).SelectMany(type => type.Methods).Where(method => method.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference called || called.Name != "GetTarget" || called.DeclaringType.FullName != "Cairo.Context") continue;
            instruction.Operand = new MethodReference("GetTargetForRecordedDrawing", called.ReturnType, called.DeclaringType) { HasThis = true };
            ClearDebugInformation(method);
            count++;
        }
        Console.WriteLine($"  API PATCHED: IconUtil recorded SVG target ({count} call site)");
        return count;
    }

    internal static int PatchQueuedGuiContexts(ModuleDefinition module, ModuleDefinition contracts)
    {
        int patched = 0;
        IEnumerable<TypeDefinition> Descendants(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Descendants));
        var types = new[] { "GuiElementStatbar", "GuiElementDynamicText", "GuiElementItemstackInfo" }
            .SelectMany(name => Descendants(module.GetType("Vintagestory.API.Client." + name)
                ?? throw new InvalidOperationException(name + " is missing from the vanilla API.")));
        foreach (var method in types.SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            var enqueue = method.Body.Instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference called && called.Name == "EnqueueMainThreadTask");
            if (enqueue == null) continue;
            var delegateTarget = enqueue.Previous;
            while (delegateTarget != null && delegateTarget.OpCode != OpCodes.Ldftn) delegateTarget = delegateTarget.Previous;
            if (delegateTarget == null) throw new InvalidOperationException($"Missing queued callback in {method.FullName}.");
            var callback = ((MethodReference)delegateTarget.Operand).Resolve();
            var fields = callback.DeclaringType.Fields.Where(field => field.FieldType.FullName == "Cairo.Context").ToArray();
            if (fields.Length is < 1 or > 2)
                throw new InvalidOperationException($"Unexpected Cairo context captures in {method.FullName}.");
            ClearDebugInformation(method);
            foreach (var instruction in method.Body.Instructions)
                instruction.OpCode = instruction.OpCode.Code switch {
                    Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                    Code.Beq_S => OpCodes.Beq, Code.Bne_Un_S => OpCodes.Bne_Un,
                    Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                    Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un,
                    Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un,
                    Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                    Code.Leave_S => OpCodes.Leave, _ => instruction.OpCode
                };
            var processor = method.Body.GetILProcessor();
            // Release completed writer contexts before enqueueing, even while GPU
            // certification is still pending. Managed-only CPU fallback uses the
            // same render-thread texture upload and also needs this thread handoff.
            foreach (var field in fields)
            {
                var dispose = new MethodReference("Dispose", module.TypeSystem.Void, module.ImportReference(field.FieldType)) { HasThis = true };
                var nullContext = Instruction.Create(OpCodes.Pop);
                var done = Instruction.Create(OpCodes.Nop);
                foreach (var instruction in new[] {
                    Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, module.ImportReference(field)),
                    Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Brfalse, nullContext),
                    Instruction.Create(OpCodes.Callvirt, dispose), Instruction.Create(OpCodes.Br, done), nullContext, done
                }) processor.InsertBefore(delegateTarget, instruction);
            }
            method.Body.MaxStackSize += 2;
            patched++;
            Console.WriteLine($"  API PATCHED: {method.FullName} queued GPU context handoff");
        }
        return patched;
    }

    internal static int PatchClientApiThreadContract(ModuleDefinition module)
    {
        const string typeName = "Vintagestory.API.Client.ICoreClientAPI";
        const string methodName = "IsTesselationThread";
        var clientApi = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");

        if (clientApi.Methods.Any(method =>
                method.Name == methodName &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
                method.ReturnType.MetadataType == MetadataType.Boolean))
        {
            return 0;
        }

        var method = new MethodDefinition(
            methodName,
            MethodAttributes.Public |
            MethodAttributes.Abstract |
            MethodAttributes.Virtual |
            MethodAttributes.HideBySig |
            MethodAttributes.NewSlot,
            module.TypeSystem.Boolean);
        method.Parameters.Add(new ParameterDefinition("threadId", ParameterAttributes.None, module.TypeSystem.Int32));
        clientApi.Methods.Add(method);
        Console.WriteLine($"  API PATCHED: {typeName}.{methodName}(int)");
        return 1;
    }

    /// <summary>
    /// Vanilla never deletes the GL textures that GuiElementItemSlotGridBase caches per background icon and
    /// color, the one GuiElementCustomDraw.Redraw generates, or the one GuiElementConfigList.Refresh generates.
    /// Adds the deletion to the grid's and the config list's Dispose and gives the custom draw a Dispose override.
    /// The grid element is reused after Dispose, so its cache is cleared; the config list texture id is reset to 0
    /// because a disposed composer can be disposed or recomposed again.
    /// </summary>
    internal static int PatchLoadedTextureDisposal(
        ModuleDefinition module,
        MethodReference disposeBridge,
        MethodReference finalizeBridge)
    {
        const string textureTypeName = "Vintagestory.API.Client.LoadedTexture";
        var textureType = module.GetType(textureTypeName)
            ?? throw new InvalidOperationException($"{textureTypeName} is missing from the vanilla API.");
        var disposedField = textureType.Fields.SingleOrDefault(field => field.Name == "disposed")
            ?? throw new InvalidOperationException($"{textureTypeName}.disposed is missing from the vanilla API.");
        var textureIdField = textureType.Fields.SingleOrDefault(field => field.Name == "TextureId")
            ?? throw new InvalidOperationException($"{textureTypeName}.TextureId is missing from the vanilla API.");
        var traceField = textureType.Fields.SingleOrDefault(field => field.Name == "trace")
            ?? throw new InvalidOperationException($"{textureTypeName}.trace is missing from the vanilla API.");
        var capiField = textureType.Fields.SingleOrDefault(field => field.Name == "capi")
            ?? throw new InvalidOperationException($"{textureTypeName}.capi is missing from the vanilla API.");
        var getIgnoreUndisposed = textureType.Methods.SingleOrDefault(method =>
            method.Name == "get_IgnoreUndisposed" && method.Parameters.Count == 0)
            ?? throw new InvalidOperationException($"{textureTypeName}.get_IgnoreUndisposed is missing from the vanilla API.");
        var disposeMethod = textureType.Methods.SingleOrDefault(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{textureTypeName}.Dispose is missing from the vanilla API.");
        var finalizeMethod = textureType.Methods.SingleOrDefault(method =>
            method.Name == "Finalize" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{textureTypeName}.Finalize is missing from the vanilla API.");

        ClearDebugInformation(disposeMethod);
        var disposeBody = new MethodBody(disposeMethod) { InitLocals = false };
        var dil = disposeBody.GetILProcessor();
        dil.Append(Instruction.Create(OpCodes.Ldarg_0));
        dil.Append(Instruction.Create(OpCodes.Ldarg_0));
        dil.Append(Instruction.Create(OpCodes.Ldflda, disposedField));
        dil.Append(Instruction.Create(OpCodes.Ldarg_0));
        dil.Append(Instruction.Create(OpCodes.Ldflda, textureIdField));
        dil.Append(Instruction.Create(OpCodes.Ldarg_0));
        dil.Append(Instruction.Create(OpCodes.Ldfld, capiField));
        dil.Append(Instruction.Create(OpCodes.Call, disposeBridge));
        dil.Append(Instruction.Create(OpCodes.Ret));
        disposeMethod.Body = disposeBody;

        var objectFinalize = finalizeMethod.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .FirstOrDefault(reference =>
                reference.Name == "Finalize" &&
                reference.DeclaringType.FullName == "System.Object")
            ?? module.ImportReference(
                typeof(object).GetMethod(
                    "Finalize",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("System.Object.Finalize is missing."));

        ClearDebugInformation(finalizeMethod);
        var finalizeBody = new MethodBody(finalizeMethod) { InitLocals = false };
        var fil = finalizeBody.GetILProcessor();
        var tryStart = Instruction.Create(OpCodes.Ldarg_0);
        var handlerStart = Instruction.Create(OpCodes.Ldarg_0);
        var handlerEnd = Instruction.Create(OpCodes.Ret);

        fil.Append(tryStart);
        fil.Append(Instruction.Create(OpCodes.Ldflda, disposedField));
        fil.Append(Instruction.Create(OpCodes.Ldarg_0));
        fil.Append(Instruction.Create(OpCodes.Ldflda, textureIdField));
        fil.Append(Instruction.Create(OpCodes.Ldarg_0));
        fil.Append(Instruction.Create(OpCodes.Call, getIgnoreUndisposed));
        fil.Append(Instruction.Create(OpCodes.Ldarg_0));
        fil.Append(Instruction.Create(OpCodes.Ldfld, traceField));
        fil.Append(Instruction.Create(OpCodes.Ldarg_0));
        fil.Append(Instruction.Create(OpCodes.Ldfld, capiField));
        fil.Append(Instruction.Create(OpCodes.Call, finalizeBridge));
        fil.Append(Instruction.Create(OpCodes.Leave_S, handlerEnd));

        fil.Append(handlerStart);
        fil.Append(Instruction.Create(OpCodes.Call, objectFinalize));
        fil.Append(Instruction.Create(OpCodes.Endfinally));
        fil.Append(handlerEnd);

        finalizeBody.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = handlerEnd,
        });
        finalizeMethod.Body = finalizeBody;

        Console.WriteLine($"  API PATCHED: {textureTypeName}.Dispose + Finalize -> OptimumApiBridge");
        return 2;
    }

    internal static int PatchMeshDataPoolLifecycle(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string lifecycleTypeName = "Vintagestory.API.Config.OptimumMeshPoolLifecycle";
        const string masterTypeName = "Vintagestory.API.Client.MeshDataPoolMasterManager";
        const string managerTypeName = "Vintagestory.API.Client.MeshDataPoolManager";
        const string poolTypeName = "Vintagestory.API.Client.MeshDataPool";

        var lifecycle = contracts.GetType(lifecycleTypeName)
            ?? throw new InvalidOperationException($"{lifecycleTypeName} is missing from the contracts assembly.");
        var masterType = module.GetType(masterTypeName)
            ?? throw new InvalidOperationException($"{masterTypeName} is missing from the vanilla API.");
        var managerType = module.GetType(managerTypeName)
            ?? throw new InvalidOperationException($"{managerTypeName} is missing from the vanilla API.");
        var poolType = module.GetType(poolTypeName)
            ?? throw new InvalidOperationException($"{poolTypeName} is missing from the vanilla API.");

        int patched = 0;

        string[] masterForwarders =
        [
            "AddModelDataPool",
            "RemoveDataPoolLocations",
            "OnFrame",
            "RemoveLocationsNow",
            "DisposeAllPools",
            "CalcFragmentation",
            "QuantityModelDataPools",
        ];

        foreach (string methodName in masterForwarders)
        {
            var targetMethod = masterType.Methods.SingleOrDefault(m => m.Name == methodName)
                ?? throw new InvalidOperationException($"{masterTypeName}.{methodName} is missing from the vanilla API.");
            var bridgeMethod = lifecycle.Methods.SingleOrDefault(m =>
                m.IsStatic && m.Name == methodName && m.Parameters.Count == targetMethod.Parameters.Count + 1)
                ?? throw new InvalidOperationException($"{lifecycleTypeName}.{methodName} helper is missing.");
            if (methodName == "OnFrame")
            {
                var diagnosticsType = contracts.GetType("Vintagestory.API.Config.OptimumDiagnostics")
                    ?? throw new InvalidOperationException("Vintagestory.API.Config.OptimumDiagnostics is missing from the contracts assembly.");
                var recordFrame = diagnosticsType.Methods.SingleOrDefault(m =>
                    m.IsStatic && m.Name == "RecordChunkRenderFrame" && m.Parameters.Count == 0)
                    ?? throw new InvalidOperationException("OptimumDiagnostics.RecordChunkRenderFrame() helper is missing.");
                RewriteInstanceForwarder(targetMethod, module.ImportReference(bridgeMethod), module.ImportReference(recordFrame));
            }
            else
            {
                RewriteInstanceForwarder(targetMethod, module.ImportReference(bridgeMethod));
            }
            patched++;
        }

        // MeshDataPool.Dispose(ICoreClientAPI) -> OptimumMeshPoolLifecycle.DisposePool
        var poolDispose = poolType.Methods.SingleOrDefault(m => m.Name == "Dispose" && m.Parameters.Count == 1)
            ?? throw new InvalidOperationException($"{poolTypeName}.Dispose(ICoreClientAPI) is missing from the vanilla API.");
        var disposeBridge = lifecycle.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "DisposePool" && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException($"{lifecycleTypeName}.DisposePool helper is missing.");
        RewriteInstanceForwarder(poolDispose, module.ImportReference(disposeBridge));
        patched++;

        // MeshDataPool.TryAdd(capi, modeldata, modelOrigin, dimension, frustumCullSphere)
        var tryAdd = poolType.Methods.SingleOrDefault(m => m.Name == "TryAdd" && m.Parameters.Count == 5)
            ?? throw new InvalidOperationException($"{poolTypeName}.TryAdd is missing from the vanilla API.");
        var canAcceptBridge = module.ImportReference(lifecycle.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "CanAcceptAndPrepareDimension" && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException($"{lifecycleTypeName}.CanAcceptAndPrepareDimension helper is missing."));
        patched += PatchMeshDataPoolTryAdd(tryAdd, canAcceptBridge);

        // MeshDataPoolManager.AddModel -> BindPoolManager after pools.Add(pool)
        var addModel = managerType.Methods.SingleOrDefault(m => m.Name == "AddModel" && m.Parameters.Count == 4)
            ?? throw new InvalidOperationException($"{managerTypeName}.AddModel is missing from the vanilla API.");
        var masterPoolField = managerType.Fields.SingleOrDefault(f => f.Name == "masterPool")
            ?? throw new InvalidOperationException($"{managerTypeName}.masterPool field is missing.");
        var bindPoolBridge = module.ImportReference(lifecycle.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "BindPoolManager" && m.Parameters.Count == 3)
            ?? throw new InvalidOperationException($"{lifecycleTypeName}.BindPoolManager helper is missing."));
        patched += PatchMeshDataPoolManagerAddModel(addModel, masterPoolField, bindPoolBridge);

        Console.WriteLine(
            $"  API PATCHED: {masterTypeName}/{managerTypeName}/{poolTypeName} lifecycle ({patched} patches) -> OptimumMeshPoolLifecycle");
        return patched;
    }

    private static void RewriteInstanceForwarder(
        MethodDefinition target,
        MethodReference bridgeMethod,
        MethodReference? prologueCall = null)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        ClearDebugInformation(target);
        var body = new MethodBody(target)
        {
            InitLocals = false,
        };
        var processor = body.GetILProcessor();
        if (prologueCall != null)
        {
            processor.Append(Instruction.Create(OpCodes.Call, prologueCall));
        }
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        for (int i = 0; i < target.Parameters.Count; i++)
        {
            processor.Append(Instruction.Create(OpCodes.Ldarg, target.Parameters[i]));
        }
        processor.Append(Instruction.Create(OpCodes.Call, bridgeMethod));
        processor.Append(Instruction.Create(OpCodes.Ret));
        target.Body = body;
    }

    private static int PatchMeshDataPoolTryAdd(MethodDefinition tryAdd, MethodReference canAcceptBridge)
    {
        if (!tryAdd.HasBody)
        {
            throw new InvalidOperationException($"{tryAdd.FullName} has no method body.");
        }

        var instructions = tryAdd.Body.Instructions;
        if (instructions.Any(ins =>
                ins.OpCode == OpCodes.Call &&
                ins.Operand is MethodReference callee &&
                callee.Name == canAcceptBridge.Name &&
                callee.DeclaringType.FullName == canAcceptBridge.DeclaringType.FullName))
        {
            return 1;
        }

        ClearDebugInformation(tryAdd);

        int beqIndex = -1;
        for (int i = 1; i + 2 < instructions.Count; i++)
        {
            if ((instructions[i].OpCode == OpCodes.Beq || instructions[i].OpCode == OpCodes.Beq_S) &&
                instructions[i - 1].OpCode == OpCodes.Ldfld &&
                instructions[i - 1].Operand is FieldReference dimField &&
                dimField.Name == "dimensionId" &&
                instructions[i + 1].OpCode == OpCodes.Ldnull &&
                instructions[i + 2].OpCode == OpCodes.Ret)
            {
                beqIndex = i;
                break;
            }
        }

        if (beqIndex < 0 || instructions[beqIndex].Operand is not Instruction continueTarget)
        {
            throw new InvalidOperationException(
                $"{tryAdd.FullName}: could not locate initial poolLocations/dimensionId guard.");
        }

        var dimensionParam = tryAdd.Parameters[3];
        if (dimensionParam.ParameterType.MetadataType != MetadataType.Int32)
        {
            throw new InvalidOperationException(
                $"{tryAdd.FullName}: expected int32 dimension parameter at index 3, found {dimensionParam.ParameterType.FullName}.");
        }

        var toRemove = new Instruction[beqIndex + 1];
        for (int i = 0; i <= beqIndex; i++)
        {
            toRemove[i] = instructions[i];
        }

        var processor = tryAdd.Body.GetILProcessor();
        var anchor = toRemove[0];
        processor.InsertBefore(anchor, Instruction.Create(OpCodes.Ldarg_0));
        processor.InsertBefore(anchor, Instruction.Create(OpCodes.Ldarg, dimensionParam));
        processor.InsertBefore(anchor, Instruction.Create(OpCodes.Call, canAcceptBridge));
        processor.InsertBefore(anchor, Instruction.Create(OpCodes.Brtrue, continueTarget));

        foreach (var ins in toRemove)
        {
            processor.Remove(ins);
        }

        return 1;
    }

    private static int PatchMeshDataPoolManagerAddModel(
        MethodDefinition addModel,
        FieldDefinition masterPoolField,
        MethodReference bindPoolBridge)
    {
        if (!addModel.HasBody)
        {
            throw new InvalidOperationException($"{addModel.FullName} has no method body.");
        }

        var instructions = addModel.Body.Instructions;
        if (instructions.Any(ins =>
                ins.OpCode == OpCodes.Call &&
                ins.Operand is MethodReference callee &&
                callee.Name == bindPoolBridge.Name &&
                callee.DeclaringType.FullName == bindPoolBridge.DeclaringType.FullName))
        {
            return 1;
        }

        ClearDebugInformation(addModel);

        int poolsAddIndex = -1;
        VariableDefinition? poolLocal = null;
        for (int i = 2; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Callvirt &&
                instructions[i].Operand is MethodReference callee &&
                callee.Name == "Add" &&
                callee.DeclaringType.FullName.Contains("List`1<Vintagestory.API.Client.MeshDataPool>", StringComparison.Ordinal) &&
                TryReadLdloc(instructions[i - 1], addModel, out var localVar))
            {
                poolsAddIndex = i;
                poolLocal = localVar;
                break;
            }
        }

        if (poolsAddIndex < 0 || poolLocal == null)
        {
            throw new InvalidOperationException(
                $"{addModel.FullName}: could not locate pools.Add(MeshDataPool) site.");
        }

        var processor = addModel.Body.GetILProcessor();
        var addInstruction = instructions[poolsAddIndex];
        var callBind = Instruction.Create(OpCodes.Call, bindPoolBridge);
        processor.InsertAfter(addInstruction, callBind);
        processor.InsertBefore(callBind, Instruction.Create(OpCodes.Ldarg_0));
        processor.InsertBefore(callBind, Instruction.Create(OpCodes.Ldfld, masterPoolField));
        processor.InsertBefore(callBind, Instruction.Create(OpCodes.Ldloc, poolLocal));
        processor.InsertBefore(callBind, Instruction.Create(OpCodes.Ldarg_0));

        return 1;
    }

    private static bool TryReadLdloc(Instruction instruction, MethodDefinition method, out VariableDefinition? variable)
    {
        var vars = method.Body.Variables;
        if (instruction.OpCode == OpCodes.Ldloc_0 && vars.Count > 0) { variable = vars[0]; return true; }
        if (instruction.OpCode == OpCodes.Ldloc_1 && vars.Count > 1) { variable = vars[1]; return true; }
        if (instruction.OpCode == OpCodes.Ldloc_2 && vars.Count > 2) { variable = vars[2]; return true; }
        if (instruction.OpCode == OpCodes.Ldloc_3 && vars.Count > 3) { variable = vars[3]; return true; }
        if ((instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S) &&
            instruction.Operand is VariableDefinition def)
        {
            variable = def;
            return true;
        }
        variable = null;
        return false;
    }

    internal static int PatchMeshDataPoolSort(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string sortTypeName = "Vintagestory.API.Config.OptimumMeshPoolSort";
        const string managerTypeName = "Vintagestory.API.Client.MeshDataPoolManager";
        const string poolTypeName = "Vintagestory.API.Client.MeshDataPool";

        var sortType = contracts.GetType(sortTypeName)
            ?? throw new InvalidOperationException($"{sortTypeName} is missing from the contracts assembly.");
        var managerType = module.GetType(managerTypeName)
            ?? throw new InvalidOperationException($"{managerTypeName} is missing from the vanilla API.");
        var poolType = module.GetType(poolTypeName)
            ?? throw new InvalidOperationException($"{poolTypeName} is missing from the vanilla API.");

        var renderMethod = managerType.Methods.SingleOrDefault(m => m.Name == "Render" && m.Parameters.Count == 3)
            ?? throw new InvalidOperationException($"{managerTypeName}.Render is missing from the vanilla API.");
        var tryRenderSorted = sortType.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "TryRenderSorted" && m.Parameters.Count == 4)
            ?? throw new InvalidOperationException($"{sortTypeName}.TryRenderSorted helper is missing.");

        var frustumCullMethod = poolType.Methods.SingleOrDefault(m => m.Name == "FrustumCull" && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException($"{poolTypeName}.FrustumCull is missing from the vanilla API.");
        var tryFrustumCullSorted = sortType.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "TryFrustumCullSortedOpaque" && m.Parameters.Count == 3)
            ?? throw new InvalidOperationException($"{sortTypeName}.TryFrustumCullSortedOpaque helper is missing.");

        int patched = 0;
        patched += PrependTryFastPath(renderMethod, module.ImportReference(tryRenderSorted));
        patched += PrependTryFastPath(frustumCullMethod, module.ImportReference(tryFrustumCullSorted));

        Console.WriteLine(
            $"  API PATCHED: {managerTypeName}.Render + {poolTypeName}.FrustumCull front-to-back sort ({patched} hooks) -> OptimumMeshPoolSort");
        return patched;
    }

    private static int PrependTryFastPath(MethodDefinition target, MethodReference fastPathMethod)
    {
        if (!target.HasBody)
        {
            throw new InvalidOperationException($"{target.FullName} has no method body.");
        }

        var instructions = target.Body.Instructions;
        if (instructions.Any(ins =>
                ins.OpCode == OpCodes.Call &&
                ins.Operand is MethodReference callee &&
                callee.Name == fastPathMethod.Name &&
                callee.DeclaringType.FullName == fastPathMethod.DeclaringType.FullName))
        {
            return 1;
        }

        ClearDebugInformation(target);
        var originalFirst = instructions[0];
        var processor = target.Body.GetILProcessor();

        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Ldarg_0));
        for (int i = 0; i < target.Parameters.Count; i++)
        {
            processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Ldarg, target.Parameters[i]));
        }
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Call, fastPathMethod));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Brfalse, originalFirst));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Ret));

        return 1;
    }

    internal static int PatchMeshDataRecycler(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string recyclerTypeName = "Vintagestory.API.Client.MeshDataRecycler";
        const string bridgeTypeName = "Vintagestory.API.Config.OptimumMeshRecyclerBridge";

        var recyclerType = module.GetType(recyclerTypeName)
            ?? throw new InvalidOperationException($"{recyclerTypeName} is missing from the vanilla API.");
        var bridgeType = contracts.GetType(bridgeTypeName)
            ?? throw new InvalidOperationException($"{bridgeTypeName} is missing from the contracts assembly.");

        string[] methodNames = ["GetOrCreateMesh", "DoRecycling", "Recycle", "Dispose"];
        int patched = 0;
        foreach (string methodName in methodNames)
        {
            var targetMethod = recyclerType.Methods.SingleOrDefault(m => m.Name == methodName && m.HasBody)
                ?? throw new InvalidOperationException($"{recyclerTypeName}.{methodName} is missing from the vanilla API.");
            var bridgeMethod = bridgeType.Methods.SingleOrDefault(m =>
                m.IsStatic && m.Name == methodName && m.Parameters.Count == targetMethod.Parameters.Count + 1)
                ?? throw new InvalidOperationException($"{bridgeTypeName}.{methodName} helper is missing.");
            RewriteInstanceForwarder(targetMethod, module.ImportReference(bridgeMethod));
            patched++;
        }
        return patched;
    }

    internal static int PatchObjectCacheUtil(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string utilTypeName = "Vintagestory.API.Util.ObjectCacheUtil";
        const string bridgeTypeName = "Vintagestory.API.Config.OptimumObjectCacheBridge";

        var utilType = module.GetType(utilTypeName)
            ?? throw new InvalidOperationException($"{utilTypeName} is missing from the vanilla API.");
        var bridgeType = contracts.GetType(bridgeTypeName)
            ?? throw new InvalidOperationException($"{bridgeTypeName} is missing from the contracts assembly.");

        int patched = 0;
        var targets = new (string Name, int ParamCount, int GenericCount)[]
        {
            ("TryGet", 2, 1),
            ("GetOrCreate", 3, 1),
            ("Delete", 2, 0),
        };
        foreach (var spec in targets)
        {
            var targetMethod = utilType.Methods.SingleOrDefault(m =>
                m.IsStatic
                && m.Name == spec.Name
                && m.Parameters.Count == spec.ParamCount
                && m.GenericParameters.Count == spec.GenericCount
                && m.HasBody)
                ?? throw new InvalidOperationException($"{utilTypeName}.{spec.Name} is missing from the vanilla API.");
            var bridgeMethod = bridgeType.Methods.SingleOrDefault(m =>
                m.IsStatic
                && m.Name == spec.Name
                && m.Parameters.Count == spec.ParamCount
                && m.GenericParameters.Count == spec.GenericCount)
                ?? throw new InvalidOperationException($"{bridgeTypeName}.{spec.Name} helper is missing.");

            var imported = module.ImportReference(bridgeMethod);
            MethodReference callTarget = imported;
            if (spec.GenericCount > 0)
            {
                var genericCall = new GenericInstanceMethod(imported);
                for (int g = 0; g < spec.GenericCount; g++)
                {
                    genericCall.GenericArguments.Add(targetMethod.GenericParameters[g]);
                }
                callTarget = genericCall;
            }

            var body = targetMethod.Body;
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            var processor = body.GetILProcessor();
            processor.Clear();
            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Ldarg_1);
            if (spec.ParamCount >= 3)
            {
                processor.Emit(OpCodes.Ldarg_2);
            }
            processor.Emit(OpCodes.Call, callTarget);
            processor.Emit(OpCodes.Ret);
            patched++;
        }
        return patched;
    }

    internal static int PatchGuiTextureDisposal(
        ModuleDefinition module,
        MethodReference deleteTexturesBridge,
        MethodReference deleteTextureBridge)
    {
        const string gridTypeName = "Vintagestory.API.Client.GuiElementItemSlotGridBase";
        const string drawTypeName = "Vintagestory.API.Client.GuiElementCustomDraw";
        const string elementTypeName = "Vintagestory.API.Client.GuiElement";
        var grid = module.GetType(gridTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName} is missing from the vanilla API.");
        var draw = module.GetType(drawTypeName)
            ?? throw new InvalidOperationException($"{drawTypeName} is missing from the vanilla API.");
        var element = module.GetType(elementTypeName)
            ?? throw new InvalidOperationException($"{elementTypeName} is missing from the vanilla API.");

        var apiField = element.Fields.SingleOrDefault(field => field.Name == "api")
            ?? throw new InvalidOperationException($"{elementTypeName}.api is missing from the vanilla API.");
        var textureCache = grid.Fields.SingleOrDefault(field => field.Name == "slotTextureIdsByBgIconAndColor")
            ?? throw new InvalidOperationException($"{gridTypeName}.slotTextureIdsByBgIconAndColor is missing from the vanilla API.");
        var textureId = draw.Fields.SingleOrDefault(field => field.Name == "texId" && field.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{drawTypeName}.texId is missing from the vanilla API.");
        var elementDispose = element.Methods.SingleOrDefault(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0 && method.IsVirtual)
            ?? throw new InvalidOperationException($"{elementTypeName}.Dispose is missing from the vanilla API.");

        const string configListTypeName = "Vintagestory.API.Client.GuiElementConfigList";
        var configList = module.GetType(configListTypeName)
            ?? throw new InvalidOperationException($"{configListTypeName} is missing from the vanilla API.");
        var configListTextureId = configList.Fields.SingleOrDefault(field => field.Name == "textureId" && field.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{configListTypeName}.textureId is missing from the vanilla API.");
        var configListDispose = configList.Methods.SingleOrDefault(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{configListTypeName}.Dispose is missing from the vanilla API.");

        var gridDispose = grid.Methods.SingleOrDefault(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.Dispose is missing from the vanilla API.");
        if (draw.Methods.Any(method => method.Name == "Dispose" && method.Parameters.Count == 0))
        {
            throw new InvalidOperationException($"{drawTypeName} already declares Dispose.");
        }

        // Right after base.Dispose(): the crossedOutTexture null check below it compiles to an early ret,
        // so anything placed at the end of the method would be skipped on that path.
        var baseCall = gridDispose.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference called &&
            called.Name == "Dispose" &&
            called.DeclaringType.FullName == elementTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName}.Dispose does not call GuiElement.Dispose.");
        ClearDebugInformation(gridDispose);
        var gridIl = gridDispose.Body.GetILProcessor();
        var cursor = baseCall;
        foreach (var instruction in new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, textureCache),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, apiField),
            Instruction.Create(OpCodes.Call, deleteTexturesBridge),
        })
        {
            gridIl.InsertAfter(cursor, instruction);
            cursor = instruction;
        }

        var drawDispose = new MethodDefinition(
            "Dispose",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        var drawBody = new MethodBody(drawDispose) { InitLocals = false };
        var drawIl = drawBody.GetILProcessor();
        drawIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        drawIl.Append(Instruction.Create(OpCodes.Call, elementDispose));
        drawIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        drawIl.Append(Instruction.Create(OpCodes.Ldflda, textureId));
        drawIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        drawIl.Append(Instruction.Create(OpCodes.Ldfld, apiField));
        drawIl.Append(Instruction.Create(OpCodes.Call, deleteTextureBridge));
        drawIl.Append(Instruction.Create(OpCodes.Ret));
        drawDispose.Body = drawBody;
        draw.Methods.Add(drawDispose);

        // Right after base.Dispose(), as in the grid, so a later early return in vanilla cannot skip the deletion.
        var configListBaseCall = configListDispose.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference called &&
            called.Name == "Dispose" &&
            called.DeclaringType.FullName == elementTypeName)
            ?? throw new InvalidOperationException($"{configListTypeName}.Dispose does not call GuiElement.Dispose.");
        ClearDebugInformation(configListDispose);
        var configListIl = configListDispose.Body.GetILProcessor();
        var configListCursor = configListBaseCall;
        foreach (var instruction in new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, configListTextureId),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, apiField),
            Instruction.Create(OpCodes.Call, deleteTextureBridge),
        })
        {
            configListIl.InsertAfter(configListCursor, instruction);
            configListCursor = instruction;
        }

        Console.WriteLine($"  API PATCHED: {gridTypeName}.Dispose deletes the cached slot background textures");
        Console.WriteLine($"  API PATCHED: {drawTypeName}.Dispose deletes the redrawn texture");
        Console.WriteLine($"  API PATCHED: {configListTypeName}.Dispose deletes the list texture");
        return 3;
    }

    /// <summary>
    /// GuiElementItemSlotGridBase subscribes to inventory.SlotNotified in its constructor and never unsubscribes.
    /// The inventory outlives the dialog, so the delegate keeps every closed dialog alive, and a grid built by a
    /// skipped AddIf (never composed, never disposed) stays subscribed for good. Dispose now removes the
    /// subscription, the constructor no longer subscribes, and ComposeInteractiveElements subscribes on every compose.
    /// </summary>
    internal static int PatchGuiSlotNotifiedSubscription(ModuleDefinition module)
    {
        const string gridTypeName = "Vintagestory.API.Client.GuiElementItemSlotGridBase";
        const string elementTypeName = "Vintagestory.API.Client.GuiElement";
        const string inventoryTypeName = "Vintagestory.API.Common.IInventory";
        var grid = module.GetType(gridTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName} is missing from the vanilla API.");
        var inventoryInterface = module.GetType(inventoryTypeName)
            ?? throw new InvalidOperationException($"{inventoryTypeName} is missing from the vanilla API.");

        var inventoryField = grid.Fields.SingleOrDefault(field => field.Name == "inventory" && field.FieldType.FullName == inventoryTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName}.inventory is missing from the vanilla API.");
        var handler = grid.Methods.SingleOrDefault(method =>
            method.Name == "OnSlotNotified" && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{gridTypeName}.OnSlotNotified is missing from the vanilla API.");
        var addAccessor = inventoryInterface.Methods.SingleOrDefault(method => method.Name == "add_SlotNotified")
            ?? throw new InvalidOperationException($"{inventoryTypeName}.SlotNotified is missing from the vanilla API.");
        var removeAccessor = inventoryInterface.Methods.SingleOrDefault(method => method.Name == "remove_SlotNotified")
            ?? throw new InvalidOperationException($"{inventoryTypeName}.SlotNotified is missing from the vanilla API.");
        // Reuse the constructor's own Action<int> reference so the new code binds to the same facade assembly as vanilla.
        var actionConstructor = grid.Methods
            .Where(method => method.IsConstructor && !method.IsStatic && method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .FirstOrDefault(reference => reference.Name == ".ctor" && reference.DeclaringType.FullName == "System.Action`1<System.Int32>")
            ?? module.ImportReference(
                typeof(Action<int>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })
                ?? throw new InvalidOperationException("Action<int>(object, IntPtr) is missing."));

        Instruction[] SubscriptionCall(MethodDefinition accessor) => new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, inventoryField),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldftn, handler),
            Instruction.Create(OpCodes.Newobj, actionConstructor),
            Instruction.Create(OpCodes.Callvirt, accessor),
        };

        // 1. Dispose: right after base.Dispose(), before the early ret that crossedOutTexture?.Dispose() compiles to.
        var dispose = grid.Methods.SingleOrDefault(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.Dispose is missing from the vanilla API.");
        var baseDispose = dispose.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference called &&
            called.Name == "Dispose" &&
            called.DeclaringType.FullName == elementTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName}.Dispose does not call GuiElement.Dispose.");
        ClearDebugInformation(dispose);
        var disposeIl = dispose.Body.GetILProcessor();
        var cursor = baseDispose;
        foreach (var instruction in SubscriptionCall(removeAccessor))
        {
            disposeIl.InsertAfter(cursor, instruction);
            cursor = instruction;
        }

        // 2. Constructor: drop "inventory.SlotNotified += OnSlotNotified" (ldarg.2, ldarg.0, ldftn, newobj, callvirt).
        var constructor = grid.Methods.SingleOrDefault(method =>
            method.IsConstructor && !method.IsStatic && method.HasBody &&
            method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Callvirt &&
                instruction.Operand is MethodReference called && called.Name == "add_SlotNotified"))
            ?? throw new InvalidOperationException($"{gridTypeName} has no constructor that subscribes to SlotNotified.");
        var constructorInstructions = constructor.Body.Instructions;
        int subscribe = constructorInstructions.IndexOf(constructorInstructions.Single(instruction =>
            instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference called && called.Name == "add_SlotNotified"));
        if (subscribe < 4 ||
            constructorInstructions[subscribe - 4].OpCode != OpCodes.Ldarg_2 ||
            constructorInstructions[subscribe - 3].OpCode != OpCodes.Ldarg_0 ||
            constructorInstructions[subscribe - 2].OpCode != OpCodes.Ldftn ||
            constructorInstructions[subscribe - 1].OpCode != OpCodes.Newobj)
        {
            throw new InvalidOperationException($"{gridTypeName} constructor SlotNotified subscription has an unexpected shape.");
        }
        ClearDebugInformation(constructor);
        var constructorIl = constructor.Body.GetILProcessor();
        for (int i = 0; i < 5; i++) constructorIl.Remove(constructorInstructions[subscribe - 4]);

        // 3. ComposeInteractiveElements: subscribe on every compose, after the "if (slotQuantityTextures != null) Dispose();".
        // The branch over that Dispose() targets the next instruction, so it is retargeted to the new code.
        var compose = grid.Methods.SingleOrDefault(method =>
            method.Name == "ComposeInteractiveElements" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeInteractiveElements is missing from the vanilla API.");
        var reuseDispose = compose.Body.Instructions.FirstOrDefault(instruction =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Operand is MethodReference called &&
            called.Name == "Dispose" &&
            called.Parameters.Count == 0 &&
            (called.DeclaringType.FullName == gridTypeName || called.DeclaringType.FullName == elementTypeName))
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeInteractiveElements does not call Dispose().");
        var afterDispose = reuseDispose.Next
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeInteractiveElements ends after Dispose().");
        ClearDebugInformation(compose);
        var composeIl = compose.Body.GetILProcessor();
        var added = SubscriptionCall(addAccessor);
        foreach (var instruction in added) composeIl.InsertBefore(afterDispose, instruction);
        foreach (var instruction in compose.Body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, afterDispose)) instruction.Operand = added[0];
        }

        Console.WriteLine($"  API PATCHED: {gridTypeName}.Dispose removes the SlotNotified subscription");
        Console.WriteLine($"  API PATCHED: {gridTypeName} constructor no longer subscribes to SlotNotified");
        Console.WriteLine($"  API PATCHED: {gridTypeName}.ComposeInteractiveElements subscribes on every compose");
        return 3;
    }

    /// <summary>
    /// Issue #580 / #512: ships tiered name short-circuiting and thread-local DummySlot reuse for
    /// ItemStack.MatchesSearchText plus allocation-free stable weighted slot sorting for
    /// GuiElementItemSlotGridBase.FilterItemsBySearchText through OptimumApiBridge.
    /// </summary>
    internal static int PatchInventorySearch(
        ModuleDefinition module,
        MethodReference matchesSearchBridge,
        MethodReference filterItemsBridge)
    {
        const string itemStackTypeName = "Vintagestory.API.Common.ItemStack";
        const string gridTypeName = "Vintagestory.API.Client.GuiElementItemSlotGridBase";
        const string elementTypeName = "Vintagestory.API.Client.GuiElement";

        var itemStack = module.GetType(itemStackTypeName)
            ?? throw new InvalidOperationException($"{itemStackTypeName} is missing from the vanilla API.");
        var matchesMethod = itemStack.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "MatchesSearchText" &&
            method.Parameters.Count == 2 &&
            method.HasBody)
            ?? throw new InvalidOperationException($"{itemStackTypeName}.MatchesSearchText(IWorldAccessor, string) is missing from the vanilla API.");

        if (matchesMethod.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == "Vintagestory.API.Config.OptimumApiBridge"))
        {
            throw new InvalidOperationException($"{itemStackTypeName}.MatchesSearchText is already patched.");
        }

        var grid = module.GetType(gridTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName} is missing from the vanilla API.");
        var element = module.GetType(elementTypeName)
            ?? throw new InvalidOperationException($"{elementTypeName} is missing from the vanilla API.");

        var filterMethod = grid.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "FilterItemsBySearchText" &&
            method.Parameters.Count == 3 &&
            method.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.FilterItemsBySearchText is missing from the vanilla API.");

        if (filterMethod.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == "Vintagestory.API.Config.OptimumApiBridge"))
        {
            throw new InvalidOperationException($"{gridTypeName}.FilterItemsBySearchText is already patched.");
        }

        var searchTextField = grid.Fields.SingleOrDefault(field => field.Name == "searchText" && field.FieldType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{gridTypeName}.searchText is missing from the vanilla API.");
        var availableSlotsField = grid.Fields.SingleOrDefault(field => field.Name == "availableSlots")
            ?? throw new InvalidOperationException($"{gridTypeName}.availableSlots is missing from the vanilla API.");
        var renderedSlotsField = grid.Fields.SingleOrDefault(field => field.Name == "renderedSlots")
            ?? throw new InvalidOperationException($"{gridTypeName}.renderedSlots is missing from the vanilla API.");
        var inventoryField = grid.Fields.SingleOrDefault(field => field.Name == "inventory")
            ?? throw new InvalidOperationException($"{gridTypeName}.inventory is missing from the vanilla API.");
        var apiField = element.Fields.SingleOrDefault(field => field.Name == "api")
            ?? throw new InvalidOperationException($"{elementTypeName}.api is missing from the vanilla API.");
        var colsField = grid.Fields.SingleOrDefault(field => field.Name == "cols" && field.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{gridTypeName}.cols is missing from the vanilla API.");
        var rowsField = grid.Fields.SingleOrDefault(field => field.Name == "rows" && field.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{gridTypeName}.rows is missing from the vanilla API.");
        var composeInteractive = grid.Methods.SingleOrDefault(method =>
            !method.IsStatic &&
            method.Name == "ComposeInteractiveElements" &&
            method.Parameters.Count == 0 &&
            method.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeInteractiveElements is missing from the vanilla API.");

        RewriteThreeArgForwarder(matchesMethod, matchesSearchBridge);

        ClearDebugInformation(filterMethod);
        var filterBody = new MethodBody(filterMethod)
        {
            InitLocals = false,
            MaxStackSize = 16,
        };
        var il = filterBody.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldflda, searchTextField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, availableSlotsField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, renderedSlotsField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, inventoryField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, apiField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, colsField));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldflda, rowsField));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_2));
        il.Append(Instruction.Create(OpCodes.Ldarg_3));
        il.Append(Instruction.Create(OpCodes.Call, filterItemsBridge));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, composeInteractive));
        il.Append(Instruction.Create(OpCodes.Ret));
        filterMethod.Body = filterBody;

        Console.WriteLine($"  API PATCHED: {itemStackTypeName}.MatchesSearchText -> OptimumApiBridge");
        Console.WriteLine($"  API PATCHED: {gridTypeName}.FilterItemsBySearchText -> OptimumApiBridge");
        return 2;
    }

    /// <summary>
    /// GuiElementHoverText.SetNewText recomposes unconditionally: it re-parses the rich text, rebuilds a blurred
    /// background and uploads a texture. The hotbar gear tooltip calls it every frame with the same text. Skips the
    /// recompose when the text is unchanged, a texture exists, and it was composed at the current GUI scale. The scale
    /// is part of the key because a scale change recomposes the composer in place and keeps this element and its
    /// texture, so a non-zero texture id alone would leave a stale texture.
    /// </summary>
    internal static int PatchGuiHoverTextSameText(ModuleDefinition module)
    {
        const string hoverTypeName = "Vintagestory.API.Client.GuiElementHoverText";
        const string textBaseTypeName = "Vintagestory.API.Client.GuiElementTextBase";
        const string textureTypeName = "Vintagestory.API.Client.LoadedTexture";
        const string runtimeEnvTypeName = "Vintagestory.API.Config.RuntimeEnv";
        var hover = module.GetType(hoverTypeName)
            ?? throw new InvalidOperationException($"{hoverTypeName} is missing from the vanilla API.");
        var textBase = module.GetType(textBaseTypeName)
            ?? throw new InvalidOperationException($"{textBaseTypeName} is missing from the vanilla API.");
        var texture = module.GetType(textureTypeName)
            ?? throw new InvalidOperationException($"{textureTypeName} is missing from the vanilla API.");
        var runtimeEnv = module.GetType(runtimeEnvTypeName)
            ?? throw new InvalidOperationException($"{runtimeEnvTypeName} is missing from the vanilla API.");

        var textField = textBase.Fields.SingleOrDefault(field => field.Name == "text" && field.FieldType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{textBaseTypeName}.text is missing from the vanilla API.");
        var hoverTexture = hover.Fields.SingleOrDefault(field => field.Name == "hoverTexture" && field.FieldType.FullName == textureTypeName)
            ?? throw new InvalidOperationException($"{hoverTypeName}.hoverTexture is missing from the vanilla API.");
        var textureId = texture.Fields.SingleOrDefault(field => field.Name == "TextureId" && field.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{textureTypeName}.TextureId is missing from the vanilla API.");
        var guiScale = runtimeEnv.Fields.SingleOrDefault(field => field.Name == "GUIScale" && field.IsStatic && field.FieldType.MetadataType == MetadataType.Single)
            ?? throw new InvalidOperationException($"{runtimeEnvTypeName}.GUIScale is missing from the vanilla API.");
        if (hover.Fields.Any(field => field.Name == "composedGuiScale"))
        {
            throw new InvalidOperationException($"{hoverTypeName}.composedGuiScale already exists.");
        }

        var recompose = hover.Methods.SingleOrDefault(method => method.Name == "Recompose" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{hoverTypeName}.Recompose is missing from the vanilla API.");
        var setNewText = hover.Methods.SingleOrDefault(method =>
            method.Name == "SetNewText" && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String && method.HasBody)
            ?? throw new InvalidOperationException($"{hoverTypeName}.SetNewText(string) is missing from the vanilla API.");

        // SetNewText must be exactly "this.text = text; Recompose();" so the guard cannot skip anything else.
        var original = setNewText.Body.Instructions;
        if (original.Count != 6 ||
            original[0].OpCode != OpCodes.Ldarg_0 || original[1].OpCode != OpCodes.Ldarg_1 ||
            original[2].OpCode != OpCodes.Stfld || original[3].OpCode != OpCodes.Ldarg_0 ||
            original[4].OpCode != OpCodes.Call || original[5].OpCode != OpCodes.Ret ||
            original[4].Operand is not MethodReference recomposeCall || recomposeCall.Name != "Recompose")
        {
            throw new InvalidOperationException($"{hoverTypeName}.SetNewText(string) has an unexpected shape.");
        }

        // Reuse the string equality reference the vanilla module already uses so it binds to the same facade assembly.
        MethodReference? stringEquality = null;
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference reference && reference.Name == "op_Equality" &&
                        reference.DeclaringType.FullName == "System.String" && reference.Parameters.Count == 2)
                    {
                        stringEquality = reference;
                        break;
                    }
                }

                if (stringEquality != null) break;
            }

            if (stringEquality != null) break;
        }

        stringEquality ??= module.ImportReference(
            typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })
            ?? throw new InvalidOperationException("string.op_Equality is missing."));

        var composedGuiScale = new FieldDefinition("composedGuiScale", FieldAttributes.Private, module.TypeSystem.Single);
        hover.Fields.Add(composedGuiScale);

        // Recompose: stamp the scale first. Nothing branches to its first instruction, but retarget in case it ever does.
        ClearDebugInformation(recompose);
        var recomposeIl = recompose.Body.GetILProcessor();
        var recomposeFirst = recompose.Body.Instructions[0];
        var stamp = new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldsfld, guiScale),
            Instruction.Create(OpCodes.Stfld, composedGuiScale),
        };
        foreach (var instruction in stamp) recomposeIl.InsertBefore(recomposeFirst, instruction);
        foreach (var instruction in recompose.Body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, recomposeFirst)) instruction.Operand = stamp[0];
        }

        // SetNewText: return early when text, texture and scale all match; otherwise run the original body.
        ClearDebugInformation(setNewText);
        var setIl = setNewText.Body.GetILProcessor();
        var first = original[0];
        var guard = new[]
        {
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, textField),
            Instruction.Create(OpCodes.Call, stringEquality),
            Instruction.Create(OpCodes.Brfalse, first),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, hoverTexture),
            Instruction.Create(OpCodes.Ldfld, textureId),
            Instruction.Create(OpCodes.Brfalse, first),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, composedGuiScale),
            Instruction.Create(OpCodes.Ldsfld, guiScale),
            Instruction.Create(OpCodes.Bne_Un, first),
            Instruction.Create(OpCodes.Ret),
        };
        foreach (var instruction in guard) setIl.InsertBefore(first, instruction);

        Console.WriteLine($"  API PATCHED: {hoverTypeName}.Recompose records the GUI scale it composed at");
        Console.WriteLine($"  API PATCHED: {hoverTypeName}.SetNewText skips an unchanged text with a current texture");
        return 2;
    }

    /// <summary>
    /// GuiElementStatbar.recomposeOverlays runs on every value change (several times per second while diving). It
    /// rasterized the hover text synchronously on the main thread, although the text is only visible while the mouse is
    /// over the bar, and queued the flash overlay again, although that overlay only depends on the bounds, the GUI scale
    /// and the color. Keeps the hover string in pendingValueText and rasterizes it, with the original instructions, in a
    /// new buildPendingValueText method that RenderInteractiveElements calls from its hover branch, and composes the flash
    /// overlay from ComposeElements, the lifecycle point that already composes the base texture.
    /// </summary>
    internal static int PatchGuiStatbarOnDemand(ModuleDefinition module)
    {
        const string typeName = "Vintagestory.API.Client.GuiElementStatbar";
        var statbar = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");
        if (statbar.Fields.Any(field => field.Name == "pendingValueText"))
        {
            throw new InvalidOperationException($"{typeName}.pendingValueText already exists.");
        }

        FieldDefinition Field(string name) => statbar.Fields.SingleOrDefault(field => field.Name == name)
            ?? throw new InvalidOperationException($"{typeName}.{name} is missing from the vanilla API.");
        var showValueOnHover = Field("ShowValueOnHover");
        var onGetStatbarValue = Field("onGetStatbarValue");
        _ = Field("valueTexture");
        _ = Field("valueFont");

        var recompose = statbar.Methods.SingleOrDefault(method => method.Name == "recomposeOverlays" && method.Parameters.Count == 0 && method.HasBody)
            ?? throw new InvalidOperationException($"{typeName}.recomposeOverlays is missing from the vanilla API.");
        var render = statbar.Methods.SingleOrDefault(method => method.Name == "RenderInteractiveElements" && method.Parameters.Count == 1 && method.HasBody)
            ?? throw new InvalidOperationException($"{typeName}.RenderInteractiveElements is missing from the vanilla API.");

        // recomposeOverlays: "if (ShowValueOnHover) { GenOrUpdateTextTexture(onGetStatbarValue(), valueFont, ref valueTexture, ...); }"
        var original = recompose.Body.Instructions.ToList();
        int show = original.FindIndex(instruction => instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field && field.Name == "ShowValueOnHover");
        int invoke = original.FindIndex(instruction => instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference called && called.Name == "Invoke" && called.DeclaringType.Name == "StatbarValueDelegate");
        int generate = original.FindIndex(instruction => instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference called && called.Name == "GenOrUpdateTextTexture");
        if (show < 1 || invoke <= show + 5 || generate <= invoke ||
            original.Count(instruction => instruction.Operand is FieldReference field && field.Name == "ShowValueOnHover") != 1 ||
            original.Count(instruction => instruction.Operand is MethodReference called && called.Name == "GenOrUpdateTextTexture") != 1 ||
            original[show + 1].Operand is not Instruction ||
            original[generate + 1].OpCode != OpCodes.Ret || original.Count != generate + 2 ||
            original[show + 2].OpCode != OpCodes.Ldarg_0 || original[show + 5].OpCode != OpCodes.Callvirt)
        {
            throw new InvalidOperationException($"{typeName}.recomposeOverlays has an unexpected shape.");
        }

        Instruction Clone(Instruction source) => source.Operand switch
        {
            null => Instruction.Create(source.OpCode),
            FieldReference field => Instruction.Create(source.OpCode, field),
            MethodReference method => Instruction.Create(source.OpCode, method),
            TypeReference type => Instruction.Create(source.OpCode, type),
            double value => Instruction.Create(source.OpCode, value),
            float value => Instruction.Create(source.OpCode, value),
            int value => Instruction.Create(source.OpCode, value),
            sbyte value => Instruction.Create(source.OpCode, value),
            string value => Instruction.Create(source.OpCode, value),
            _ => throw new InvalidOperationException($"{typeName}.recomposeOverlays has an unsupported operand {source.Operand.GetType().Name}."),
        };

        var pendingValueText = new FieldDefinition("pendingValueText", FieldAttributes.Private, module.TypeSystem.String);
        statbar.Fields.Add(pendingValueText);

        // New method: if (pendingValueText == null) return; GenOrUpdateTextTexture(pendingValueText, valueFont, ref valueTexture, ...); pendingValueText = null;
        var build = new MethodDefinition("buildPendingValueText", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
        var buildBody = new MethodBody(build) { InitLocals = false, MaxStackSize = 8 };
        var buildIl = buildBody.GetILProcessor();
        var end = Instruction.Create(OpCodes.Ret);
        buildIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        buildIl.Append(Instruction.Create(OpCodes.Ldfld, pendingValueText));
        buildIl.Append(Instruction.Create(OpCodes.Brfalse, end));
        for (int i = show + 2; i <= show + 5; i++) buildIl.Append(Clone(original[i]));          // this.api.Gui.TextTexture
        buildIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        buildIl.Append(Instruction.Create(OpCodes.Ldfld, pendingValueText));
        for (int i = invoke + 1; i <= generate; i++) buildIl.Append(Clone(original[i]));       // valueFont, ref valueTexture, new TextBackground { ... }, GenOrUpdateTextTexture
        buildIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        buildIl.Append(Instruction.Create(OpCodes.Ldnull));
        buildIl.Append(Instruction.Create(OpCodes.Stfld, pendingValueText));
        buildIl.Append(end);
        build.Body = buildBody;
        statbar.Methods.Add(build);

        // recomposeOverlays now only keeps the string. The brfalse over the block keeps targeting the final ret.
        ClearDebugInformation(recompose);
        var recomposeIl = recompose.Body.GetILProcessor();
        var branch = original[show + 1];
        for (int i = show + 2; i <= generate; i++) recomposeIl.Remove(original[i]);
        var cursor = branch;
        foreach (var instruction in new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, onGetStatbarValue),
            Clone(original[invoke]),
            Instruction.Create(OpCodes.Stfld, pendingValueText),
        })
        {
            recomposeIl.InsertAfter(cursor, instruction);
            cursor = instruction;
        }

        // RenderInteractiveElements: build the text right after the "ShowValueOnHover && Bounds.PointInside(...)" test passes.
        var renderBody = render.Body.Instructions.ToList();
        var pointInside = renderBody.Where(instruction => instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference called && called.Name == "PointInside" && called.Parameters.Count == 2).ToList();
        if (pointInside.Count != 1 || pointInside[0].Next == null || pointInside[0].Next.Operand is not Instruction ||
            (pointInside[0].Next.OpCode != OpCodes.Brfalse && pointInside[0].Next.OpCode != OpCodes.Brfalse_S))
        {
            throw new InvalidOperationException($"{typeName}.RenderInteractiveElements has an unexpected hover branch.");
        }

        ClearDebugInformation(render);
        // The added call grows the method between its short branches and their targets, so widen them first.
        foreach (var instruction in render.Body.Instructions)
            instruction.OpCode = instruction.OpCode.Code switch {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bne_Un_S => OpCodes.Bne_Un,
                Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un,
                Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Leave_S => OpCodes.Leave, _ => instruction.OpCode
            };
        var renderIl = render.Body.GetILProcessor();
        var hoverBranch = pointInside[0].Next;
        var call = Instruction.Create(OpCodes.Call, build);
        renderIl.InsertAfter(hoverBranch, Instruction.Create(OpCodes.Ldarg_0));
        renderIl.InsertAfter(hoverBranch.Next, call);

        // The flash overlay does not depend on the value: compose it with the base texture and drop it from the recompose task.
        var flashCompose = statbar.Methods.SingleOrDefault(method => method.Name == "ComposeFlashOverlay" && method.Parameters.Count == 0)
            ?? throw new InvalidOperationException($"{typeName}.ComposeFlashOverlay is missing from the vanilla API.");
        var composeElements = statbar.Methods.SingleOrDefault(method => method.Name == "ComposeElements" && method.Parameters.Count == 2 && method.HasBody)
            ?? throw new InvalidOperationException($"{typeName}.ComposeElements is missing from the vanilla API.");
        var valuesSetLoads = composeElements.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldfld &&
            instruction.Operand is FieldReference field && field.Name == "valuesSet").ToList();
        if (valuesSetLoads.Count != 1 || valuesSetLoads[0].Previous?.OpCode != OpCodes.Ldarg_0 ||
            composeElements.Body.Instructions.Any(instruction => ReferenceEquals(instruction.Operand, valuesSetLoads[0].Previous)))
        {
            throw new InvalidOperationException($"{typeName}.ComposeElements has an unexpected valuesSet check.");
        }

        var lambdaLoad = recompose.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldftn).ToList();
        if (lambdaLoad.Count != 1)
        {
            throw new InvalidOperationException($"{typeName}.recomposeOverlays has an unexpected recompose task.");
        }

        var lambda = ((MethodReference)lambdaLoad[0].Operand).Resolve();
        var lambdaBody = lambda.Body.Instructions.ToList();
        if (lambdaBody.Count != 5 || lambdaBody[0].OpCode != OpCodes.Ldarg_0 || lambdaBody[1].Operand is not MethodReference valueCall ||
            valueCall.Name != "ComposeValueOverlay" || lambdaBody[2].OpCode != OpCodes.Ldarg_0 ||
            lambdaBody[3].Operand is not MethodReference flashCall || flashCall.Name != "ComposeFlashOverlay" || lambdaBody[4].OpCode != OpCodes.Ret)
        {
            throw new InvalidOperationException($"{typeName}.recomposeOverlays task has an unexpected shape.");
        }

        ClearDebugInformation(lambda);
        var lambdaIl = lambda.Body.GetILProcessor();
        lambdaIl.Remove(lambdaBody[3]);
        lambdaIl.Remove(lambdaBody[2]);

        ClearDebugInformation(composeElements);
        var composeIl = composeElements.Body.GetILProcessor();
        var anchor = valuesSetLoads[0].Previous;
        composeIl.InsertBefore(anchor, Instruction.Create(OpCodes.Ldarg_0));
        composeIl.InsertBefore(anchor, Instruction.Create(OpCodes.Call, flashCompose));

        Console.WriteLine($"  API PATCHED: {typeName}.recomposeOverlays keeps the hover string instead of rasterizing it");
        Console.WriteLine($"  API PATCHED: {typeName}.buildPendingValueText rasterizes the hover string on demand");
        Console.WriteLine($"  API PATCHED: {typeName}.RenderInteractiveElements builds the hover text on the first hovered frame");
        Console.WriteLine($"  API PATCHED: {typeName}.recomposeOverlays no longer queues the flash overlay");
        Console.WriteLine($"  API PATCHED: {typeName}.ComposeElements composes the flash overlay with the base texture");
        return 5;
    }

    internal static int PatchGuiCompositionMetrics(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string composerTypeName = "Vintagestory.API.Client.GuiComposer";
        const string pendingFieldName = "optimumGuiMetricsRecomposePending";
        var composer = module.GetType(composerTypeName)
            ?? throw new InvalidOperationException($"{composerTypeName} is missing from the vanilla API.");
        var compose = composer.Methods.Single(method =>
            method.Name == "Compose" && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        var recompose = composer.Methods.Single(method => method.Name == "ReCompose" && method.Parameters.Count == 0);
        if (!compose.HasBody || !recompose.HasBody)
        {
            throw new InvalidOperationException("GuiComposer.Compose or ReCompose has no method body.");
        }

        if (composer.Fields.Any(field => field.Name == pendingFieldName))
        {
            throw new InvalidOperationException($"{composerTypeName}.{pendingFieldName} already exists.");
        }

        var metrics = contracts.GetType("Vintagestory.API.Config.OptimumGuiMetrics")
            ?? throw new InvalidOperationException("OptimumGuiMetrics is missing from the contracts assembly.");
        var guiGpuProbe = contracts.GetType("Vintagestory.API.Client.OptimumGuiGpuProbe")
            ?? throw new InvalidOperationException("OptimumGuiGpuProbe is missing from the contracts assembly.");
        var enabledGetter = metrics.Methods.Single(method => method.Name == "get_Enabled" && method.Parameters.Count == 0);
        var startTimestamp = metrics.Methods.Single(method => method.Name == "StartTimestamp" && method.Parameters.Count == 0);
        var recordComposition = metrics.Methods.Single(method => method.Name == "RecordComposition" && method.Parameters.Count == 5);
        var recordGpuRecordingStart = metrics.Methods.Single(method => method.Name == "RecordGpuRecordingSurfaceStart" && method.Parameters.Count == 0);
        var renderingRequestedGetter = guiGpuProbe.Methods.Single(method => method.Name == "get_RenderingRequested" && method.Parameters.Count == 0);

        var pendingField = new FieldDefinition(pendingFieldName, FieldAttributes.Private, module.TypeSystem.Boolean);
        composer.Fields.Add(pendingField);

        ClearDebugInformation(recompose);
        var recomposeProcessor = recompose.Body.GetILProcessor();
        Instruction firstRecomposeInstruction = recompose.Body.Instructions[0];
        recomposeProcessor.InsertBefore(firstRecomposeInstruction, Instruction.Create(OpCodes.Ldarg_0));
        recomposeProcessor.InsertBefore(firstRecomposeInstruction, Instruction.Create(OpCodes.Ldc_I4_1));
        recomposeProcessor.InsertBefore(firstRecomposeInstruction, Instruction.Create(OpCodes.Stfld, pendingField));

        ClearDebugInformation(compose);
        compose.Body.InitLocals = true;
        var enabledLocal = new VariableDefinition(module.TypeSystem.Boolean);
        var timestampLocal = new VariableDefinition(module.TypeSystem.Int64);
        var recomposeLocal = new VariableDefinition(module.TypeSystem.Boolean);
        var surfaceLocal = compose.Body.Variables.SingleOrDefault(variable =>
            variable.VariableType.FullName == "Cairo.ImageSurface")
            ?? throw new InvalidOperationException($"{compose.FullName} has no Cairo.ImageSurface local.");
        if (compose.Body.Instructions.Any(instruction => instruction.Operand is MethodReference called && called.Name == "BeginRecording"))
            throw new InvalidOperationException($"{compose.FullName} already has a GPU recording hook.");
        compose.Body.Variables.Add(enabledLocal);
        compose.Body.Variables.Add(timestampLocal);
        compose.Body.Variables.Add(recomposeLocal);

        var composeProcessor = compose.Body.GetILProcessor();

        var imageSurfaceConstructor = compose.Body.Instructions.SingleOrDefault(instruction =>
            (instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Call) &&
            instruction.Operand is MethodReference called &&
            called.Name == ".ctor" && called.DeclaringType.FullName == "Cairo.ImageSurface" &&
            instruction.Next != null && StoresVariable(instruction.Next, surfaceLocal))
            ?? throw new InvalidOperationException($"{compose.FullName} has no Cairo.ImageSurface construction stored in its surface local.");
        var cairoReference = module.AssemblyReferences.Single(reference => reference.Name == "cairo-sharp");
        var cairoSurfaceType = new TypeReference("Cairo", "Surface", module, cairoReference);
        var cairoRecorderType = new TypeReference("Cairo", "SurfaceRecorder", module, cairoReference);
        var automaticRecordingMethod = new MethodReference("ConfigureAutomaticRecording", module.TypeSystem.Void, cairoSurfaceType)
        {
            HasThis = false,
            CallingConvention = MethodCallingConvention.Default
        };
        automaticRecordingMethod.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        var beginRecording = new MethodReference("BeginRecording", cairoRecorderType, cairoSurfaceType)
        {
            HasThis = true,
            CallingConvention = MethodCallingConvention.Default,
        };
        Instruction afterRecordingHook = Instruction.Create(OpCodes.Nop);
        Instruction anchor = imageSurfaceConstructor.Next!;
        foreach (Instruction recordingConfigurationInstruction in new[]
        {
            Instruction.Create(OpCodes.Call, module.ImportReference(renderingRequestedGetter)),
            Instruction.Create(OpCodes.Call, module.ImportReference(automaticRecordingMethod))
        })
        {
            composeProcessor.InsertBefore(imageSurfaceConstructor, recordingConfigurationInstruction);
        }
        foreach (Instruction hookInstruction in new[]
        {
            Instruction.Create(OpCodes.Call, module.ImportReference(renderingRequestedGetter)),
            Instruction.Create(OpCodes.Brfalse, afterRecordingHook),
            Instruction.Create(OpCodes.Ldloc, surfaceLocal),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(beginRecording)),
            Instruction.Create(OpCodes.Pop),
            Instruction.Create(OpCodes.Call, module.ImportReference(enabledGetter)),
            Instruction.Create(OpCodes.Brfalse, afterRecordingHook),
            Instruction.Create(OpCodes.Call, module.ImportReference(recordGpuRecordingStart)),
            afterRecordingHook
        })
        {
            composeProcessor.InsertAfter(anchor, hookInstruction);
            anchor = hookInstruction;
        }

        Instruction firstComposeInstruction = compose.Body.Instructions[0];
        Instruction skipTimestamp = Instruction.Create(OpCodes.Nop);
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Call, module.ImportReference(enabledGetter)));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Stloc, enabledLocal));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Ldloc, enabledLocal));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Brfalse, skipTimestamp));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Call, module.ImportReference(startTimestamp)));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Stloc, timestampLocal));
        composeProcessor.InsertBefore(firstComposeInstruction, skipTimestamp);
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Ldarg_0));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Ldfld, pendingField));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Stloc, recomposeLocal));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Ldarg_0));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Ldc_I4_0));
        composeProcessor.InsertBefore(firstComposeInstruction, Instruction.Create(OpCodes.Stfld, pendingField));

        var textureUpload = compose.Body.Instructions.SingleOrDefault(instruction =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Operand is MethodReference called && called.Name == "LoadOrUpdateCairoTexture")
            ?? throw new InvalidOperationException($"{compose.FullName} has no Cairo texture upload call.");
        Instruction skipRecord = Instruction.Create(OpCodes.Nop);
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, enabledLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Brfalse, skipRecord));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, timestampLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, surfaceLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Callvirt,
            module.ImportReference(surfaceLocal.VariableType.Resolve()!.Properties.Single(property => property.Name == "Width").GetMethod!)));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, surfaceLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Callvirt,
            module.ImportReference(surfaceLocal.VariableType.Resolve()!.Properties.Single(property => property.Name == "Height").GetMethod!)));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, surfaceLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Callvirt,
            module.ImportReference(surfaceLocal.VariableType.Resolve()!.Properties.Single(property => property.Name == "Stride").GetMethod!)));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Ldloc, recomposeLocal));
        composeProcessor.InsertBefore(textureUpload, Instruction.Create(OpCodes.Call, module.ImportReference(recordComposition)));
        composeProcessor.InsertBefore(textureUpload, skipRecord);

        Console.WriteLine($"  API PATCHED: {composerTypeName}.Compose GUI metrics and opt-in GPU recording");
        var listMenu = module.GetType("Vintagestory.API.Client.GuiElementListMenu")
            ?? throw new InvalidOperationException("GuiElementListMenu is missing.");
        var composeMenu = listMenu.Methods.Single(method => method.Name == "ComposeDynamicElements" && method.Parameters.Count == 0);
        var menuSurfaces = composeMenu.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj &&
            instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Cairo.ImageSurface" && called.Parameters.Count == 3).ToArray();
        if (menuSurfaces.Length != 3) throw new InvalidOperationException($"Expected three list-menu surfaces, found {menuSurfaces.Length}.");
        foreach (var instruction in menuSurfaces)
        {
            var constructor = (MethodReference)instruction.Operand;
            var factory = new MethodReference("CreateForRecordedGui", constructor.DeclaringType, constructor.DeclaringType);
            foreach (var parameter in constructor.Parameters) factory.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = module.ImportReference(factory);
        }
        ClearDebugInformation(composeMenu);
        Console.WriteLine("  API PATCHED: list-menu empty GPU surface normalization (3 call sites)");
        return 1;
    }

    internal static int PatchLinkTextGpuUploads(ModuleDefinition module)
    {
        const string typeName = "Vintagestory.API.Client.LinkTextComponent";
        var type = module.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is missing from the vanilla API.");
        var compose = type.Methods.Single(method => method.Name == "ComposeElements" &&
            method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == "Cairo.Context" &&
            method.Parameters[1].ParameterType.FullName == "Cairo.ImageSurface");
        var uploadCalls = compose.Body.Instructions.Where(instruction =>
            instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
            .Where(instruction => instruction.Operand is MethodReference called && called.Name == "LoadOrUpdateCairoTexture")
            .ToArray();
        if (uploadCalls.Length != 2)
            throw new InvalidOperationException($"Expected two link-text texture uploads, found {uploadCalls.Length}.");
        var contextLocal = compose.Body.Variables.SingleOrDefault(variable => variable.VariableType.FullName == "Cairo.Context")
            ?? throw new InvalidOperationException($"{compose.FullName} has no Cairo.Context local.");
        var leftMostX = type.Fields.Single(field => field.Name == "leftMostX");
        var topMostY = type.Fields.Single(field => field.Name == "topMostY");
        var cairoReference = module.AssemblyReferences.Single(reference => reference.Name == "cairo-sharp");
        var contextType = new TypeReference("Cairo", "Context", module, cairoReference);
        var save = new MethodReference("Save", module.TypeSystem.Void, contextType) { HasThis = true };
        var restore = new MethodReference("Restore", module.TypeSystem.Void, contextType) { HasThis = true };
        var translate = new MethodReference("Translate", module.TypeSystem.Void, contextType) { HasThis = true };
        translate.Parameters.Add(new ParameterDefinition(module.TypeSystem.Double));
        translate.Parameters.Add(new ParameterDefinition(module.TypeSystem.Double));

        var processor = compose.Body.GetILProcessor();
        var firstUploadSetup = uploadCalls[0];
        while (firstUploadSetup.Previous != null)
        {
            if (firstUploadSetup.Previous.Operand is MethodReference called && called.Name == "get_Gui")
            {
                firstUploadSetup = firstUploadSetup.Previous;
                break;
            }
            firstUploadSetup = firstUploadSetup.Previous;
        }
        if (!(firstUploadSetup.Operand is MethodReference guiGetter) || guiGetter.Name != "get_Gui")
            throw new InvalidOperationException("Could not locate the first link-text GUI texture upload setup.");

        processor.InsertBefore(firstUploadSetup, Instruction.Create(OpCodes.Ldloc, contextLocal));
        processor.InsertBefore(firstUploadSetup, Instruction.Create(OpCodes.Callvirt, module.ImportReference(restore)));

        Instruction anchor = uploadCalls[0];
        foreach (Instruction instruction in new[]
        {
            Instruction.Create(OpCodes.Ldloc, contextLocal),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(save)),
            Instruction.Create(OpCodes.Ldloc, contextLocal),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, leftMostX),
            Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Neg),
            Instruction.Create(OpCodes.Conv_R8),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, topMostY),
            Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Neg),
            Instruction.Create(OpCodes.Conv_R8),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(translate))
        })
        {
            processor.InsertAfter(anchor, instruction);
            anchor = instruction;
        }

        ClearDebugInformation(compose);
        Console.WriteLine($"  API PATCHED: {typeName}.ComposeElements balances the normal-text upload before hover composition");
        return 1;
    }

    internal static int PatchFontMeasurementMetrics(ModuleDefinition module, ModuleDefinition contracts, ModuleDefinition cairo)
    {
        const string fontTypeName = "Vintagestory.API.Client.CairoFont";
        var font = module.GetType(fontTypeName)
            ?? throw new InvalidOperationException($"{fontTypeName} is missing from the vanilla API.");
        var metrics = contracts.GetType("Vintagestory.API.Config.OptimumGuiMetrics")
            ?? throw new InvalidOperationException("OptimumGuiMetrics is missing from the contracts assembly.");
        var enabledGetter = metrics.Methods.Single(method => method.Name == "get_Enabled" && method.Parameters.Count == 0);
        var startTimestamp = metrics.Methods.Single(method => method.Name == "StartTimestamp" && method.Parameters.Count == 0);
        var recordTextExtents = metrics.Methods.Single(method => method.Name == "RecordTextExtentMeasurement" && method.Parameters.Count == 1);
        var recordFontExtents = metrics.Methods.Single(method => method.Name == "RecordFontExtentMeasurement" && method.Parameters.Count == 1);

        var textExtents = font.Methods.Single(method => method.Name == "GetTextExtents" && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        var fontExtents = font.Methods.Single(method => method.Name == "GetFontExtents" && method.Parameters.Count == 0);
        RewriteFontMeasurementsWithSkia(module, cairo, font, textExtents, fontExtents);
        PatchFontMeasurementMethod(module, textExtents, enabledGetter, startTimestamp, recordTextExtents);
        PatchFontMeasurementMethod(module, fontExtents, enabledGetter, startTimestamp, recordFontExtents);
        DisableNativeFontMeasuringContextInitializer(module, font);
        Console.WriteLine($"  API PATCHED: {fontTypeName}.GetTextExtents/GetFontExtents Skia metrics and null-safe disposal");
        return 2;
    }

    private static void DisableNativeFontMeasuringContextInitializer(ModuleDefinition module, TypeDefinition font)
    {
        var initializer = font.Methods.SingleOrDefault(method => method.Name == ".cctor" && method.IsStatic && method.Parameters.Count == 0);
        if (initializer == null) return;
        if (!initializer.HasBody)
            throw new InvalidOperationException("CairoFont's static measuring-context initializer has no body.");

        var surface = font.Fields.SingleOrDefault(field => field.Name == "surface" && field.IsStatic && field.FieldType.FullName == "Cairo.ImageSurface")
            ?? throw new InvalidOperationException("CairoFont's native measuring surface field is missing.");
        var measuringContext = font.Fields.SingleOrDefault(field => field.Name == "FontMeasuringContext" && field.IsStatic && field.FieldType.FullName == "Cairo.Context")
            ?? throw new InvalidOperationException("CairoFont.FontMeasuringContext is missing.");
        Instruction[] instructions = initializer.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        bool matchesVanillaInitializer = instructions.Length == 9 &&
            instructions[0].OpCode == OpCodes.Ldc_I4_0 &&
            instructions[1].OpCode == OpCodes.Ldc_I4_1 &&
            instructions[2].OpCode == OpCodes.Ldc_I4_1 &&
            IsConstructor(instructions[3], "Cairo.ImageSurface", "Cairo.Format", "System.Int32", "System.Int32") &&
            IsField(instructions[4], OpCodes.Stsfld, surface) &&
            IsField(instructions[5], OpCodes.Ldsfld, surface) &&
            IsConstructor(instructions[6], "Cairo.Context", "Cairo.Surface") &&
            IsField(instructions[7], OpCodes.Stsfld, measuringContext) &&
            instructions[8].OpCode == OpCodes.Ret;
        if (!matchesVanillaInitializer)
            throw new InvalidOperationException("CairoFont's static initializer no longer matches the known native measuring-context construction.");

        initializer.Body = new MethodBody(initializer);
        initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        initializer.DebugInformation.SequencePoints.Clear();
        initializer.DebugInformation.Scope = null;
        Console.WriteLine("  API PATCHED: CairoFont static initialization no longer creates a Cairo surface/context");

        static bool IsConstructor(Instruction instruction, string declaringType, params string[] parameters) =>
            instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference method &&
            method.Name == ".ctor" && method.DeclaringType.FullName == declaringType &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters);

        static bool IsField(Instruction instruction, OpCode opcode, FieldDefinition field) =>
            instruction.OpCode == opcode && instruction.Operand is FieldReference reference && reference.FullName == field.FullName;
    }

    private static void RewriteFontMeasurementsWithSkia(ModuleDefinition module, ModuleDefinition cairo,
        TypeDefinition font, MethodDefinition textExtents, MethodDefinition fontExtents)
    {
        var helper = cairo.GetType("Cairo.OptimumSkiaTextMetrics")
            ?? throw new InvalidOperationException("Cairo.OptimumSkiaTextMetrics is missing from the candidate Cairo assembly.");
        if (!helper.IsPublic)
            throw new InvalidOperationException("Cairo.OptimumSkiaTextMetrics must be public for calls from the API assembly.");
        if (textExtents.IsStatic || fontExtents.IsStatic || textExtents.ReturnType.FullName != "Cairo.TextExtents" ||
            fontExtents.ReturnType.FullName != "Cairo.FontExtents")
            throw new InvalidOperationException("CairoFont measurement signatures do not match the supported vanilla API.");
        var options = font.Fields.SingleOrDefault(field => field.Name == "CairoFontOptions" && !field.IsStatic && field.FieldType.FullName == "Cairo.FontOptions")
            ?? throw new InvalidOperationException("CairoFont.CairoFontOptions is missing from the vanilla API.");
        var dispose = font.Methods.SingleOrDefault(method => method.Name == "Dispose" && !method.IsStatic && method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Void")
            ?? throw new InvalidOperationException("CairoFont.Dispose() is missing from the vanilla API.");
        var disposeInstructions = dispose.HasBody ? dispose.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray() : Array.Empty<Instruction>();
        if (disposeInstructions.Length != 4 || disposeInstructions[0].OpCode != OpCodes.Ldarg_0 ||
            disposeInstructions[1].OpCode != OpCodes.Ldfld || disposeInstructions[1].Operand is not FieldReference disposeField || disposeField.FullName != options.FullName ||
            disposeInstructions[2].OpCode != OpCodes.Callvirt || disposeInstructions[2].Operand is not MethodReference disposeCall ||
            disposeCall.Name != "Dispose" || disposeCall.DeclaringType.FullName != "Cairo.FontOptions" || disposeInstructions[3].OpCode != OpCodes.Ret)
            throw new InvalidOperationException("CairoFont.Dispose() does not match the supported vanilla font-options disposal body.");
        var measureText = FindHelper("GetTextExtents", "Cairo.TextExtents", true);
        var measureFont = FindHelper("GetFontExtents", "Cairo.FontExtents", false);
        var family = FindField("Fontname", "System.String");
        var weight = FindField("FontWeight", "Cairo.FontWeight");
        var slant = FindField("Slant", "Cairo.FontSlant");
        var size = FindField("UnscaledFontsize", "System.Double");
        var scaled = module.GetType("Vintagestory.API.Client.GuiElement")?.Methods.SingleOrDefault(method =>
            method.Name == "scaled" && method.IsStatic && method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "System.Double" && method.ReturnType.FullName == "System.Double")
            ?? throw new InvalidOperationException("GuiElement.scaled(double) is missing from the vanilla API.");

        // Validate both methods before changing either body. Do not silently reapply
        // on top of an API that already carries the GUI measurement hooks.
        foreach (var method in new[] { textExtents, fontExtents })
        {
            if (!method.HasBody || method.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Vintagestory.API.Config.OptimumGuiMetrics"))
                throw new InvalidOperationException($"{method.FullName} is missing its vanilla body or already has GUI measurement hooks.");
        }
        Rewrite(textExtents, measureText, true);
        Rewrite(fontExtents, measureFont, false);
        // Measuring through Skia no longer calls SetupContext, so font options
        // can remain null throughout a font's lifetime.
        var disposeProcessor = dispose.Body.GetILProcessor();
        Instruction originalDispose = dispose.Body.Instructions[0];
        disposeProcessor.InsertBefore(originalDispose, Instruction.Create(OpCodes.Ldarg_0));
        disposeProcessor.InsertBefore(originalDispose, Instruction.Create(OpCodes.Ldfld, module.ImportReference(options)));
        disposeProcessor.InsertBefore(originalDispose, Instruction.Create(OpCodes.Brtrue, originalDispose));
        disposeProcessor.InsertBefore(originalDispose, Instruction.Create(OpCodes.Ret));

        MethodDefinition FindHelper(string name, string result, bool hasText)
        {
            string[] parameters = hasText
                ? new[] { "System.String", "Cairo.FontWeight", "Cairo.FontSlant", "System.Double", "System.String" }
                : new[] { "System.String", "Cairo.FontWeight", "Cairo.FontSlant", "System.Double" };
            return helper.Methods.SingleOrDefault(method => method.Name == name && method.IsPublic && method.IsStatic &&
                method.ReturnType.FullName == result && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException($"The candidate Cairo assembly lacks the expected {name} Skia helper signature.");
        }

        FieldDefinition FindField(string name, string type)
        {
            for (TypeDefinition? current = font; current != null; current = current.BaseType?.Resolve())
            {
                var field = current.Fields.SingleOrDefault(candidate => candidate.Name == name && !candidate.IsStatic && candidate.FieldType.FullName == type);
                if (field != null) return field;
            }
            throw new InvalidOperationException($"{font.FullName} is missing its {type} {name} field.");
        }

        void Rewrite(MethodDefinition method, MethodDefinition helperMethod, bool hasText)
        {
            method.Body = new MethodBody(method);
            method.DebugInformation.SequencePoints.Clear();
            method.DebugInformation.Scope = null;
            var processor = method.Body.GetILProcessor();
            foreach (var field in new[] { family, weight, slant, size })
            {
                processor.Append(Instruction.Create(OpCodes.Ldarg_0));
                processor.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(field)));
            }
            processor.Append(Instruction.Create(OpCodes.Call, module.ImportReference(scaled)));
            if (hasText) processor.Append(Instruction.Create(OpCodes.Ldarg_1));
            processor.Append(Instruction.Create(OpCodes.Call, module.ImportReference(helperMethod)));
            processor.Append(Instruction.Create(OpCodes.Ret));
        }
    }

    private static void PatchFontMeasurementMethod(ModuleDefinition module, MethodDefinition method,
        MethodDefinition enabledGetter, MethodDefinition startTimestamp, MethodDefinition recordMeasurement)
    {
        if (!method.HasBody) throw new InvalidOperationException($"{method.FullName} has no method body.");
        if (method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference called &&
            called.Name == recordMeasurement.Name && called.DeclaringType.FullName == recordMeasurement.DeclaringType.FullName))
            throw new InvalidOperationException($"{method.FullName} already has a GUI text measurement hook.");

        var returns = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();
        if (returns.Length != 1)
            throw new InvalidOperationException($"Expected one return in {method.FullName}, found {returns.Length}.");

        method.Body.InitLocals = true;
        var enabledLocal = new VariableDefinition(module.TypeSystem.Boolean);
        var timestampLocal = new VariableDefinition(module.TypeSystem.Int64);
        method.Body.Variables.Add(enabledLocal);
        method.Body.Variables.Add(timestampLocal);
        var processor = method.Body.GetILProcessor();

        Instruction originalFirst = method.Body.Instructions[0];
        Instruction skipStart = Instruction.Create(OpCodes.Nop);
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Call, module.ImportReference(enabledGetter)));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Stloc, enabledLocal));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Ldloc, enabledLocal));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Brfalse, skipStart));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Call, module.ImportReference(startTimestamp)));
        processor.InsertBefore(originalFirst, Instruction.Create(OpCodes.Stloc, timestampLocal));
        processor.InsertBefore(originalFirst, skipStart);

        Instruction returnInstruction = returns[0];
        Instruction recordStart = Instruction.Create(OpCodes.Ldloc, enabledLocal);
        Instruction skipRecord = Instruction.Create(OpCodes.Nop);
        processor.InsertBefore(returnInstruction, recordStart);
        processor.InsertBefore(returnInstruction, Instruction.Create(OpCodes.Brfalse, skipRecord));
        processor.InsertBefore(returnInstruction, Instruction.Create(OpCodes.Ldloc, timestampLocal));
        processor.InsertBefore(returnInstruction, Instruction.Create(OpCodes.Call, module.ImportReference(recordMeasurement)));
        processor.InsertBefore(returnInstruction, skipRecord);

        // Retarget existing branches so every successful return passes through the metric hook.
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction == recordStart) break;
            if (instruction.Operand is Instruction target && target == returnInstruction)
                instruction.Operand = recordStart;
            else if (instruction.Operand is Instruction[] targets)
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] == returnInstruction) targets[i] = recordStart;
        }
    }

    internal static int PatchTextDrawUtilFontOverloads(ModuleDefinition module)
    {
        const string utilTypeName = "Vintagestory.API.Client.TextDrawUtil";
        const string fontTypeName = "Vintagestory.API.Client.CairoFont";
        var util = module.GetType(utilTypeName)
            ?? throw new InvalidOperationException($"{utilTypeName} is missing from the vanilla API.");
        var font = module.GetType(fontTypeName)
            ?? throw new InvalidOperationException($"{fontTypeName} is missing from the vanilla API.");
        var flowPath = module.GetType("Vintagestory.API.Client.TextFlowPath")
            ?? throw new InvalidOperationException("TextFlowPath is missing from the vanilla API.");
        var lineType = module.GetType("Vintagestory.API.Client.TextLine")
            ?? throw new InvalidOperationException("TextLine is missing from the vanilla API.");
        var behavior = module.GetType("Vintagestory.API.Client.EnumLinebreakBehavior")
            ?? throw new InvalidOperationException("EnumLinebreakBehavior is missing from the vanilla API.");

        MethodDefinition Find(string name, string result, params string[] parameters) =>
            util.Methods.SingleOrDefault(method => method.Name == name && method.ReturnType.FullName == result &&
                method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException($"{utilTypeName}.{name}({string.Join(", ", parameters)}) is missing from the vanilla API.");

        var contextLineize = Find("Lineize", $"{lineType.FullName}[]", "Cairo.Context", "System.String", behavior.FullName,
            $"{flowPath.FullName}[]", "System.Double", "System.Double", "System.Double", "System.Boolean");
        var context = contextLineize.Parameters[0].ParameterType.Resolve()
            ?? throw new InvalidOperationException("Cairo.Context is missing from the vanilla API references.");
        var fontLineize = Find("Lineize", $"{lineType.FullName}[]", font.FullName, "System.String", behavior.FullName,
            $"{flowPath.FullName}[]", "System.Double", "System.Double", "System.Boolean");
        var fontQuantity = Find("GetQuantityTextLines", "System.Int32", font.FullName, "System.String", behavior.FullName,
            $"{flowPath.FullName}[]", "System.Double");

        if (!contextLineize.HasBody || !fontLineize.HasBody || !fontQuantity.HasBody)
            throw new InvalidOperationException("TextDrawUtil font-layout entrypoints must have managed method bodies.");
        if (util.Methods.Any(method => method.Name == "LineizeWithSkiaMetrics"))
            throw new InvalidOperationException("TextDrawUtil already contains the Skia metrics layout helper.");

        var fontTextExtents = font.Methods.SingleOrDefault(method => method.Name == "GetTextExtents" &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.FullName == "Cairo.TextExtents")
            ?? throw new InvalidOperationException("CairoFont.GetTextExtents(string) is missing from the vanilla API.");
        var fontExtents = font.Methods.SingleOrDefault(method => method.Name == "GetFontExtents" &&
            method.Parameters.Count == 0 && method.ReturnType.FullName == "Cairo.FontExtents")
            ?? throw new InvalidOperationException("CairoFont.GetFontExtents() is missing from the vanilla API.");
        var lineHeight = FindField(font, "LineHeightMultiplier", "System.Double");
        var textExtents = context.Methods.SingleOrDefault(method => method.Name == "TextExtents" &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.FullName == "Cairo.TextExtents")
            ?? throw new InvalidOperationException("Cairo.Context.TextExtents(string) is missing from the vanilla API.");
        var contextFontExtents = context.Methods.SingleOrDefault(method => method.Name == "get_FontExtents" &&
            method.Parameters.Count == 0 && method.ReturnType.FullName == "Cairo.FontExtents")
            ?? throw new InvalidOperationException("Cairo.Context.FontExtents is missing from the vanilla API.");

        MethodDefinition helper = CloneLineizeWithSkiaMetrics(module, util, contextLineize, font, textExtents,
            contextFontExtents, fontTextExtents, fontExtents);
        RewriteFontLineize(module, fontLineize, helper, lineHeight);
        RewriteFontQuantity(module, fontQuantity, helper, lineHeight);

        Console.WriteLine($"  API PATCHED: {utilTypeName}.Lineize/GetQuantityTextLines(CairoFont) Skia metrics, no temporary Cairo context");
        return 2;

        static FieldDefinition FindField(TypeDefinition type, string name, string fieldType)
        {
            for (TypeDefinition? current = type; current != null; current = current.BaseType?.Resolve())
            {
                var field = current.Fields.SingleOrDefault(candidate => candidate.Name == name && !candidate.IsStatic &&
                    candidate.FieldType.FullName == fieldType);
                if (field != null) return field;
            }
            throw new InvalidOperationException($"{type.FullName} is missing its {fieldType} {name} field.");
        }
    }

    private static MethodDefinition CloneLineizeWithSkiaMetrics(ModuleDefinition module, TypeDefinition util,
        MethodDefinition source, TypeDefinition font, MethodDefinition contextTextExtents,
        MethodDefinition contextFontExtents, MethodDefinition fontTextExtents, MethodDefinition fontExtents)
    {
        var helper = new MethodDefinition("LineizeWithSkiaMetrics",
            MethodAttributes.Private | MethodAttributes.HideBySig, module.ImportReference(source.ReturnType));
        foreach (var parameter in source.Parameters)
        {
            TypeReference parameterType = parameter.Index == 0
                ? module.ImportReference(font)
                : module.ImportReference(parameter.ParameterType);
            helper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameterType));
        }

        helper.Body.InitLocals = source.Body.InitLocals;
        helper.Body.MaxStackSize = source.Body.MaxStackSize;
        var variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in source.Body.Variables)
        {
            var cloned = new VariableDefinition(module.ImportReference(variable.VariableType));
            helper.Body.Variables.Add(cloned);
            variables.Add(variable, cloned);
        }

        var instructions = new Dictionary<Instruction, Instruction>();
        ILProcessor processor = helper.Body.GetILProcessor();
        int replacedTextMetrics = 0;
        int replacedFontMetrics = 0;
        foreach (Instruction original in source.Body.Instructions)
        {
            Instruction instruction;
            if (original.Operand is MethodReference method && method.DeclaringType.FullName == "Cairo.Context")
            {
                if (method.Name == contextTextExtents.Name && method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String && method.ReturnType.FullName == contextTextExtents.ReturnType.FullName)
                {
                    instruction = CloneCall(original, fontTextExtents);
                    replacedTextMetrics++;
                }
                else if (method.Name == contextFontExtents.Name && method.Parameters.Count == 0 &&
                    method.ReturnType.FullName == contextFontExtents.ReturnType.FullName)
                {
                    instruction = CloneCall(original, fontExtents);
                    replacedFontMetrics++;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected Cairo.Context call in {source.FullName}: {method.FullName}.");
                }
            }
            else
            {
                instruction = CloneInstruction(original);
            }
            instructions.Add(original, instruction);
            processor.Append(instruction);
        }

        foreach (Instruction instruction in helper.Body.Instructions)
        {
            if (instruction.Operand is Instruction branch && instructions.TryGetValue(branch, out Instruction? mapped))
                instruction.Operand = mapped;
            else if (instruction.Operand is Instruction[] branches)
                instruction.Operand = branches.Select(branch => instructions.TryGetValue(branch, out Instruction? mappedBranch) ? mappedBranch : branch).ToArray();
            else if (instruction.Operand is VariableDefinition variable && variables.TryGetValue(variable, out VariableDefinition? mappedVariable))
                instruction.Operand = mappedVariable;
            else if (instruction.Operand is ParameterDefinition parameter && parameter.Index >= 0 && parameter.Index < helper.Parameters.Count)
                instruction.Operand = helper.Parameters[parameter.Index];
        }

        foreach (ExceptionHandler handler in source.Body.ExceptionHandlers)
        {
            helper.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = handler.TryStart != null ? instructions.GetValueOrDefault(handler.TryStart) : null,
                TryEnd = handler.TryEnd != null ? instructions.GetValueOrDefault(handler.TryEnd) : null,
                HandlerStart = handler.HandlerStart != null ? instructions.GetValueOrDefault(handler.HandlerStart) : null,
                HandlerEnd = handler.HandlerEnd != null ? instructions.GetValueOrDefault(handler.HandlerEnd) : null,
                CatchType = handler.CatchType != null ? module.ImportReference(handler.CatchType) : null,
            });
        }

        if (replacedTextMetrics == 0 || replacedFontMetrics == 0)
            throw new InvalidOperationException($"{source.FullName} did not contain both expected Cairo text measurement calls.");
        util.Methods.Add(helper);
        return helper;

        static Instruction CloneCall(Instruction sourceInstruction, MethodDefinition target)
        {
            OpCode opcode = sourceInstruction.OpCode == OpCodes.Ldvirtftn ? OpCodes.Ldftn : sourceInstruction.OpCode;
            return Instruction.Create(opcode, target);
        }

        Instruction CloneInstruction(Instruction instruction)
        {
            object? operand = instruction.Operand;
            if (operand == null) return Instruction.Create(instruction.OpCode);
            if (operand is MethodReference method) return Instruction.Create(instruction.OpCode, module.ImportReference(method));
            if (operand is TypeReference type) return Instruction.Create(instruction.OpCode, module.ImportReference(type));
            if (operand is FieldReference field) return Instruction.Create(instruction.OpCode, module.ImportReference(field));
            if (operand is string text) return Instruction.Create(instruction.OpCode, text);
            if (operand is int i) return Instruction.Create(instruction.OpCode, i);
            if (operand is long l) return Instruction.Create(instruction.OpCode, l);
            if (operand is float f) return Instruction.Create(instruction.OpCode, f);
            if (operand is double d) return Instruction.Create(instruction.OpCode, d);
            if (operand is byte b) return Instruction.Create(instruction.OpCode, b);
            if (operand is sbyte sb) return Instruction.Create(instruction.OpCode, sb);
            if (operand is Instruction targetInstruction) return Instruction.Create(instruction.OpCode, targetInstruction);
            if (operand is Instruction[] targets) return Instruction.Create(instruction.OpCode, targets);
            if (operand is VariableDefinition variable) return Instruction.Create(instruction.OpCode, variables[variable]);
            if (operand is ParameterDefinition parameter) return Instruction.Create(instruction.OpCode, helper.Parameters[parameter.Index]);
            throw new InvalidOperationException($"Unsupported IL operand {operand.GetType().FullName} in {source.FullName}.");
        }
    }

    private static void RewriteFontLineize(ModuleDefinition module, MethodDefinition method, MethodDefinition helper, FieldDefinition lineHeight)
    {
        ClearDebugInformation(method);
        method.Body = new MethodBody(method);
        ILProcessor processor = method.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        foreach (int argument in new[] { 0, 1, 2, 3, 4, 5 })
            processor.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[argument]));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(lineHeight)));
        processor.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[6]));
        processor.Append(Instruction.Create(OpCodes.Call, module.ImportReference(helper)));
        processor.Append(Instruction.Create(OpCodes.Ret));
    }

    private static void RewriteFontQuantity(ModuleDefinition module, MethodDefinition method, MethodDefinition helper, FieldDefinition lineHeight)
    {
        ClearDebugInformation(method);
        method.Body = new MethodBody(method);
        ILProcessor processor = method.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        foreach (int argument in new[] { 0, 1, 2, 3 })
            processor.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[argument]));
        processor.Append(Instruction.Create(OpCodes.Ldc_R8, 0d));
        processor.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[4]));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(lineHeight)));
        processor.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        processor.Append(Instruction.Create(OpCodes.Call, module.ImportReference(helper)));
        processor.Append(Instruction.Create(OpCodes.Ldlen));
        processor.Append(Instruction.Create(OpCodes.Conv_I4));
        processor.Append(Instruction.Create(OpCodes.Ret));
    }

    internal static int PatchGameVersionLabel(ModuleDefinition module, string optimumVersion)
    {
        if (string.IsNullOrWhiteSpace(optimumVersion))
        {
            throw new ArgumentException("Optimum version cannot be empty.", nameof(optimumVersion));
        }

        var gameVersion = module.GetType("Vintagestory.API.Config.GameVersion")
            ?? throw new InvalidOperationException("GameVersion is missing from the vanilla API.");
        var longGameVersion = gameVersion.Fields.Single(field =>
            field.Name == "LongGameVersion" && field.IsStatic &&
            field.FieldType.MetadataType == MetadataType.String);
        var initializer = gameVersion.Methods.Single(method =>
            method.Name == ".cctor" && method.Parameters.Count == 0);
        if (!initializer.HasBody)
        {
            throw new InvalidOperationException($"{initializer.FullName} has no method body.");
        }

        ClearDebugInformation(initializer);
        var returns = initializer.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Ret)
            .ToArray();
        if (returns.Length != 1)
        {
            throw new InvalidOperationException(
                $"{initializer.FullName} has {returns.Length} returns; expected exactly one.");
        }

        string suffix = $" + Optimum v{optimumVersion}";
        if (initializer.Body.Instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, suffix)))
        {
            throw new InvalidOperationException("GameVersion.LongGameVersion already contains the Optimum suffix.");
        }

        var concat = module.ImportReference(
            typeof(string).GetMethod(
                nameof(string.Concat),
                new[] { typeof(string), typeof(string) })!);
        var processor = initializer.Body.GetILProcessor();
        Instruction cursor = returns[0];
        cursor.OpCode = OpCodes.Ldsfld;
        cursor.Operand = longGameVersion;

        var loadSuffix = Instruction.Create(OpCodes.Ldstr, suffix);
        processor.InsertAfter(cursor, loadSuffix);
        cursor = loadSuffix;
        var appendSuffix = Instruction.Create(OpCodes.Call, concat);
        processor.InsertAfter(cursor, appendSuffix);
        cursor = appendSuffix;
        var storeVersion = Instruction.Create(OpCodes.Stsfld, longGameVersion);
        processor.InsertAfter(cursor, storeVersion);
        cursor = storeVersion;
        processor.InsertAfter(cursor, Instruction.Create(OpCodes.Ret));

        Console.WriteLine($"  API PATCHED: {gameVersion.FullName}.LongGameVersion ({suffix})");
        return 1;
    }

    private static int PatchLoggerInitializer(ModuleDefinition module)
    {
        var loggerBase = module.GetType("Vintagestory.API.Common.LoggerBase")
            ?? throw new InvalidOperationException("LoggerBase is missing from the vanilla API.");
        var initializer = loggerBase.Methods.Single(method =>
            method.Name == ".cctor" && method.Parameters.Count == 0);
        ClearDebugInformation(initializer);
        var emptyArgs = loggerBase.Fields.Single(field =>
            field.Name == "_emptyArgs" && field.IsStatic);
        var sourcePath = loggerBase.Fields.Single(field =>
            field.Name == "SourcePath" && field.IsStatic);

        initializer.Body = new MethodBody(initializer);
        var processor = initializer.Body.GetILProcessor();
        var arrayEmpty = module.ImportReference(
            typeof(Array).GetMethod(nameof(Array.Empty))!.MakeGenericMethod(typeof(object)));

        processor.Append(Instruction.Create(OpCodes.Call, arrayEmpty));
        processor.Append(Instruction.Create(OpCodes.Stsfld, emptyArgs));
        // LoggerBase.CleanStackTrace calls string.Replace(SourcePath, "").
        // An empty SourcePath makes that method throw and recursively re-enter
        // Error(Exception). Use a sentinel that cannot occur in a managed path.
        processor.Append(Instruction.Create(OpCodes.Ldstr, "\0"));
        processor.Append(Instruction.Create(OpCodes.Stsfld, sourcePath));
        processor.Append(Instruction.Create(OpCodes.Ret));
        Console.WriteLine($"  API PATCHED: {initializer.FullName}");
        return 1;
    }

    private static int PatchInventoryDirtyHooks(ModuleDefinition module, MethodReference markDirty)
    {
        var inventoryBase = module.GetType("Vintagestory.API.Common.InventoryBase")
            ?? throw new InvalidOperationException("InventoryBase is missing from the vanilla API.");

        var methods = inventoryBase.Methods.Where(method =>
            (method.Name == "MarkSlotDirty" && method.Parameters.Count == 1) ||
            (method.Name == "DiscardAll" && method.Parameters.Count == 0));

        int patched = 0;
        foreach (var method in methods)
        {
            if (!method.HasBody)
            {
                throw new InvalidOperationException($"{method.FullName} has no method body.");
            }
            ClearDebugInformation(method);

            var returns = method.Body.Instructions
                .Where(instruction => instruction.OpCode == OpCodes.Ret)
                .ToArray();
            if (returns.Length != 1)
            {
                throw new InvalidOperationException(
                    $"{method.FullName} has {returns.Length} returns; expected exactly one.");
            }

            method.Body.GetILProcessor().InsertBefore(
                returns[0],
                Instruction.Create(OpCodes.Call, markDirty));
            patched++;
            Console.WriteLine($"  API HOOKED: {method.FullName}");
        }

        return patched;
    }

    private static int PatchChiselLodHook(ModuleDefinition module, MethodReference optimumCheck)
    {
        var locationType = module.GetType("Vintagestory.API.Client.ModelDataPoolLocation")
            ?? throw new InvalidOperationException(
                "ModelDataPoolLocation is missing from the vanilla API.");
        var isVisible = locationType.Methods.Single(method =>
            method.Name == "IsVisible" && method.Parameters.Count == 2);
        ClearDebugInformation(isVisible);

        var targetCalls = isVisible.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference method &&
                method.DeclaringType.FullName == "Vintagestory.API.Client.FrustumCulling" &&
                method.Name == "InFrustumAndRange" &&
                method.Parameters.Count == 3)
            .ToArray();

        if (targetCalls.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one FrustumCulling.InFrustumAndRange call, found {targetCalls.Length}.");
        }

        var processor = isVisible.Body.GetILProcessor();
        processor.InsertBefore(targetCalls[0], Instruction.Create(OpCodes.Ldarg_0));
        targetCalls[0].OpCode = OpCodes.Call;
        targetCalls[0].Operand = optimumCheck;
        Console.WriteLine($"  API HOOKED: {isVisible.FullName}");
        return 1;
    }

    /// <summary>
    /// Vanilla's CullInstantShadowPassNear/Far cases in ModelDataPoolLocation.IsVisible have no
    /// chisel-LOD distance awareness, so the LOD 3 cube proxy always entered the depth map and a
    /// carved block cast a full-block shadow at any distance. There is no InFrustumAndRange call to
    /// redirect in those cases, so instead every FrustumCulling.InFrustumShadowPass result is fed
    /// through OptimumApiBridge.InFrustumShadowPass(result, culler, location), which applies the
    /// same LOD 2 (near) / LOD 3 (far) split used by the normal render pass.
    /// </summary>
    private static int PatchChiselLodShadowHook(ModuleDefinition module, MethodReference optimumShadowCheck)
    {
        var locationType = module.GetType("Vintagestory.API.Client.ModelDataPoolLocation")
            ?? throw new InvalidOperationException(
                "ModelDataPoolLocation is missing from the vanilla API.");
        var isVisible = locationType.Methods.Single(method =>
            method.Name == "IsVisible" && method.Parameters.Count == 2);
        ClearDebugInformation(isVisible);

        var targetCalls = isVisible.Body.Instructions
            .Where(instruction =>
                (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                instruction.Operand is MethodReference method &&
                method.DeclaringType.FullName == "Vintagestory.API.Client.FrustumCulling" &&
                method.Name == "InFrustumShadowPass" &&
                method.Parameters.Count == 1)
            .ToArray();

        if (targetCalls.Length == 0)
        {
            throw new InvalidOperationException(
                "Expected at least one FrustumCulling.InFrustumShadowPass call in " +
                $"{isVisible.FullName}, found none.");
        }

        var processor = isVisible.Body.GetILProcessor();
        foreach (var call in targetCalls)
        {
            // Stack after the original call: [bool baseResult].
            // Push the culler (arg 2) and the location (this), then fold through the bridge.
            var loadCuller = Instruction.Create(OpCodes.Ldarg_2);
            var loadLocation = Instruction.Create(OpCodes.Ldarg_0);
            var bridgeCall = Instruction.Create(OpCodes.Call, optimumShadowCheck);
            processor.InsertAfter(call, loadCuller);
            processor.InsertAfter(loadCuller, loadLocation);
            processor.InsertAfter(loadLocation, bridgeCall);
        }

        Console.WriteLine(
            $"  API HOOKED: {isVisible.FullName} shadow pass ({targetCalls.Length} call sites)");
        return targetCalls.Length;
    }

    private static readonly string[] Mat4fInlineMethods =
    [
        "Multiply", "Mul", "Translate", "Scale",
        "RotateX", "RotateY", "RotateZ",
        "Invert", "Transpose", "Identity",
        "Create", "FromTranslation", "FromValues",
    ];

    internal static int PatchMat4fInlining(ModuleDefinition module)
    {
        var mat4f = module.GetType("Vintagestory.API.MathTools.Mat4f")
            ?? throw new InvalidOperationException("Mat4f is missing from the vanilla API.");

        var aggressiveInlining = module.ImportReference(
            typeof(MethodImplAttribute).GetConstructor([typeof(MethodImplOptions)])!);

        int patched = 0;
        foreach (var method in mat4f.Methods)
        {
            if (!method.IsStatic || !method.IsPublic) continue;
            if (!Mat4fInlineMethods.Contains(method.Name)) continue;
            if (method.CustomAttributes.Any(a =>
                a.AttributeType.Name == nameof(MethodImplAttribute))) continue;

            var attr = new CustomAttribute(aggressiveInlining);
            attr.ConstructorArguments.Add(
                new CustomAttributeArgument(
                    module.ImportReference(typeof(MethodImplOptions)),
                    MethodImplOptions.AggressiveInlining));
            method.CustomAttributes.Add(attr);
            patched++;
        }

        if (patched > 0)
            Console.WriteLine($"  API PATCHED: Mat4f [{patched} methods] → AggressiveInlining");
        return patched;
    }

    /// <summary>
    /// AnimatorBase.animsByCode uses the default ordinal comparer, so
    /// GetAnimationState and OnFrame both call code.ToLowerInvariant() on every lookup - an
    /// allocation per active animation, per frame. Rebuilding the dictionary with
    /// StringComparer.OrdinalIgnoreCase makes the lowercasing unnecessary; this patch does
    /// both: the constructor picks up the comparer, and the two lookup sites drop the now
    /// redundant ToLowerInvariant() call. This mirrors patches/VintagestoryApi/Common/Model/
    /// Animation/AnimatorBase.cs.patch. The source donor keeps this experiment for comparison.
    /// The runtime API patcher leaves the shared API assembly unchanged for server use.
    /// </summary>
    internal static int PatchAnimatorAnimCodeComparer(ModuleDefinition module)
    {
        var animatorBase = module.GetType("Vintagestory.API.Common.AnimatorBase")
            ?? throw new InvalidOperationException("AnimatorBase is missing from the vanilla API.");

        var ctor = animatorBase.Methods.Single(method =>
            method.IsConstructor && !method.IsStatic && method.Parameters.Count == 3);
        ClearDebugInformation(ctor);

        var dictCtorCall = ctor.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Newobj &&
            instruction.Operand is MethodReference method &&
            method.Name == ".ctor" &&
            method.DeclaringType.Name == "Dictionary`2" &&
            method.Parameters.Count == 1);

        var dictionaryType = (GenericInstanceType)((MethodReference)dictCtorCall.Operand).DeclaringType;
        var keyType = dictionaryType.GenericArguments[0];

        var equalityComparerOpen = module.ImportReference(typeof(IEqualityComparer<>));
        var equalityComparerOfKey = new GenericInstanceType(equalityComparerOpen);
        equalityComparerOfKey.GenericArguments.Add(keyType);

        var comparerCtor = new MethodReference(".ctor", module.TypeSystem.Void, dictionaryType)
        {
            HasThis = true,
        };
        comparerCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        comparerCtor.Parameters.Add(new ParameterDefinition(equalityComparerOfKey));

        var ordinalIgnoreCaseGetter = module.ImportReference(
            typeof(StringComparer).GetProperty(nameof(StringComparer.OrdinalIgnoreCase))!.GetGetMethod());

        var ctorProcessor = ctor.Body.GetILProcessor();
        ctorProcessor.InsertBefore(dictCtorCall, Instruction.Create(OpCodes.Call, ordinalIgnoreCaseGetter));
        dictCtorCall.Operand = comparerCtor;

        var getAnimationState = animatorBase.Methods.Single(method =>
            method.Name == "GetAnimationState" && method.Parameters.Count == 1);
        var onFrame = animatorBase.Methods.Single(method =>
            method.Name == "OnFrame" && method.Parameters.Count == 2);

        int sites = 0;
        sites += RemoveToLowerInvariantBeforeTryGetValue(getAnimationState);
        sites += RemoveToLowerInvariantBeforeTryGetValue(onFrame);

        Console.WriteLine(
            $"  API PATCHED: {animatorBase.FullName} animsByCode OrdinalIgnoreCase comparer " +
            $"(ctor + {sites} lookup sites)");
        return 1 + sites;
    }

    /// <summary>
    /// Keeps head controllers inert when a shape failed to create an animator or
    /// lacks a named pose. Entity tessellation can recover from that state and
    /// the render loop can continue without changing valid animation data.
    /// </summary>
    internal static int PatchHeadControllerPoseFallback(ModuleDefinition module)
    {
        var controller = module.GetType("Vintagestory.API.Common.EntityHeadController")
            ?? throw new InvalidOperationException("EntityHeadController is missing from the vanilla API.");
        var getPose = controller.Methods.Single(method =>
            method.Name == "GetPose" && method.Parameters.Count == 1);
        var animationManagerField = controller.Fields.Single(field => field.Name == "animationManager");

        var animationManager = module.GetType("Vintagestory.API.Common.IAnimationManager")
            ?? throw new InvalidOperationException("IAnimationManager is missing from the vanilla API.");
        var animatorGetter = animationManager.Properties.Single(property => property.Name == "Animator").GetMethod
            ?? throw new InvalidOperationException("IAnimationManager.Animator getter is missing from the vanilla API.");

        var animator = module.GetType("Vintagestory.API.Common.IAnimator")
            ?? throw new InvalidOperationException("IAnimator is missing from the vanilla API.");
        var getPoseByName = animator.Methods.Single(method =>
            method.Name == "GetPosebyName" && method.Parameters.Count == 2);

        var elementPose = module.GetType("Vintagestory.API.Common.ElementPose")
            ?? throw new InvalidOperationException("ElementPose is missing from the vanilla API.");
        var elementPoseCtor = elementPose.Methods.Single(method =>
            method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);

        ClearDebugInformation(getPose);

        var body = new MethodBody(getPose)
        {
            InitLocals = true,
        };
        var animationManagerLocal = new VariableDefinition(module.ImportReference(animationManager));
        var animatorLocal = new VariableDefinition(module.ImportReference(animator));
        body.Variables.Add(animationManagerLocal);
        body.Variables.Add(animatorLocal);
        var processor = body.GetILProcessor();
        var fallback = Instruction.Create(OpCodes.Newobj, module.ImportReference(elementPoseCtor));
        var returnExistingPose = Instruction.Create(OpCodes.Ret);

        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(animationManagerField)));
        processor.Append(Instruction.Create(OpCodes.Stloc, animationManagerLocal));
        processor.Append(Instruction.Create(OpCodes.Ldloc, animationManagerLocal));
        processor.Append(Instruction.Create(OpCodes.Brfalse, fallback));
        processor.Append(Instruction.Create(OpCodes.Ldloc, animationManagerLocal));
        processor.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(animatorGetter)));
        processor.Append(Instruction.Create(OpCodes.Stloc, animatorLocal));
        processor.Append(Instruction.Create(OpCodes.Ldloc, animatorLocal));
        processor.Append(Instruction.Create(OpCodes.Brfalse, fallback));
        processor.Append(Instruction.Create(OpCodes.Ldloc, animatorLocal));
        processor.Append(Instruction.Create(OpCodes.Ldarg_1));
        processor.Append(Instruction.Create(OpCodes.Ldc_I4, (int)StringComparison.InvariantCultureIgnoreCase));
        processor.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(getPoseByName)));
        processor.Append(Instruction.Create(OpCodes.Dup));
        processor.Append(Instruction.Create(OpCodes.Brtrue, returnExistingPose));
        processor.Append(Instruction.Create(OpCodes.Pop));
        processor.Append(Instruction.Create(OpCodes.Br, fallback));
        processor.Append(returnExistingPose);
        processor.Append(fallback);
        processor.Append(Instruction.Create(OpCodes.Ret));

        getPose.Body = body;
        Console.WriteLine(
            $"  API PATCHED: {controller.FullName}.GetPose null-safe fallback");
        return 1;
    }

    /// <summary>
    /// Vanilla's TyronThreadPool constructor hardcodes ThreadPool.SetMaxThreads(10, 1),
    /// which under-provisions the worker pool on modern many-core machines. This mirrors
    /// patches/VintagestoryApi/Common/TyronThreadPool.cs.patch (used to compile the
    /// donor VintagestoryAPI.dll and the optimized client): scale the caps with
    /// Environment.ProcessorCount, and expose the before/after thread counts plus the
    /// SetMaxThreads result as new static properties so OptimumStatusModSystem.BuildStatus
    /// (sources/VSEssentials/Systems/OptimumStatus.cs) can report them - that type is
    /// injected wholesale into VSEssentials.dll and calls these properties directly, so
    /// without this patch the live-patched vanilla API throws
    /// MissingMethodException: TyronThreadPool.get_SetMaxThreadsResult() the first time
    /// the status command runs its JIT-validation pass.
    /// </summary>
    internal static int PatchTyronThreadPoolDiagnostics(ModuleDefinition module)
    {
        var threadPool = module.GetType("Vintagestory.API.Common.TyronThreadPool")
            ?? throw new InvalidOperationException("TyronThreadPool is missing from the vanilla API.");

        var intType = module.TypeSystem.Int32;
        var boolType = module.TypeSystem.Boolean;

        FieldDefinition AddStaticAutoProperty(string name, TypeReference type)
        {
            var field = new FieldDefinition(
                $"<{name}>k__BackingField", FieldAttributes.Private | FieldAttributes.Static, type);
            threadPool.Fields.Add(field);

            var getter = new MethodDefinition(
                $"get_{name}",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                type);
            var getterIl = getter.Body.GetILProcessor();
            getterIl.Append(Instruction.Create(OpCodes.Ldsfld, field));
            getterIl.Append(Instruction.Create(OpCodes.Ret));
            threadPool.Methods.Add(getter);
            threadPool.Properties.Add(
                new PropertyDefinition(name, PropertyAttributes.None, type) { GetMethod = getter });
            return field;
        }

        var workerBeforeField = AddStaticAutoProperty("SetMaxThreadsWorkerBefore", intType);
        var workerAfterField = AddStaticAutoProperty("SetMaxThreadsWorkerAfter", intType);
        var ioBeforeField = AddStaticAutoProperty("SetMaxThreadsIoBefore", intType);
        var ioAfterField = AddStaticAutoProperty("SetMaxThreadsIoAfter", intType);
        var resultField = AddStaticAutoProperty("SetMaxThreadsResult", boolType);

        var ctor = threadPool.Methods.Single(method => method.IsConstructor && !method.IsStatic &&
            method.Parameters.Count == 0);
        ClearDebugInformation(ctor);

        var body = ctor.Body;
        var instructions = body.Instructions;
        var setMaxThreadsCall = instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference callee &&
            callee.Name == "SetMaxThreads" &&
            callee.DeclaringType.FullName == "System.Threading.ThreadPool" &&
            callee.Parameters.Count == 2);
        var setMaxThreadsRef = (MethodReference)setMaxThreadsCall.Operand;

        int callIndex = instructions.IndexOf(setMaxThreadsCall);
        if (callIndex < 2)
        {
            throw new InvalidOperationException(
                $"{ctor.FullName}: expected at least two instructions before the SetMaxThreads call.");
        }
        var loadWorkerArg = instructions[callIndex - 2];
        var loadIoArg = instructions[callIndex - 1];
        var popInstruction = instructions[callIndex + 1];
        if (popInstruction.OpCode != OpCodes.Pop)
        {
            throw new InvalidOperationException(
                $"{ctor.FullName}: expected Pop right after ThreadPool.SetMaxThreads, found " +
                $"{popInstruction.OpCode}. Vanilla shape changed - this patch needs updating.");
        }

        var getMaxThreads = module.ImportReference(
            typeof(ThreadPool).GetMethod(
                nameof(ThreadPool.GetMaxThreads),
                new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }));
        var processorCountGetter = module.ImportReference(
            typeof(Environment).GetProperty(nameof(Environment.ProcessorCount))!.GetGetMethod());
        var mathMax = module.ImportReference(
            typeof(Math).GetMethod(nameof(Math.Max), new[] { typeof(int), typeof(int) }));

        var workerBeforeLocal = new VariableDefinition(intType);
        var ioBeforeLocal = new VariableDefinition(intType);
        var workerMaxLocal = new VariableDefinition(intType);
        var ioMaxLocal = new VariableDefinition(intType);
        var workerAfterLocal = new VariableDefinition(intType);
        var ioAfterLocal = new VariableDefinition(intType);
        body.Variables.Add(workerBeforeLocal);
        body.Variables.Add(ioBeforeLocal);
        body.Variables.Add(workerMaxLocal);
        body.Variables.Add(ioMaxLocal);
        body.Variables.Add(workerAfterLocal);
        body.Variables.Add(ioAfterLocal);
        body.InitLocals = true;

        var replacement = new[]
        {
            // ThreadPool.GetMaxThreads(out workerBefore, out ioBefore);
            Instruction.Create(OpCodes.Ldloca, workerBeforeLocal),
            Instruction.Create(OpCodes.Ldloca, ioBeforeLocal),
            Instruction.Create(OpCodes.Call, getMaxThreads),
            Instruction.Create(OpCodes.Ldloc, workerBeforeLocal),
            Instruction.Create(OpCodes.Stsfld, workerBeforeField),
            Instruction.Create(OpCodes.Ldloc, ioBeforeLocal),
            Instruction.Create(OpCodes.Stsfld, ioBeforeField),

            // int workerMax = Math.Max(10, Environment.ProcessorCount * 2);
            Instruction.Create(OpCodes.Ldc_I4, 10),
            Instruction.Create(OpCodes.Call, processorCountGetter),
            Instruction.Create(OpCodes.Ldc_I4_2),
            Instruction.Create(OpCodes.Mul),
            Instruction.Create(OpCodes.Call, mathMax),
            Instruction.Create(OpCodes.Stloc, workerMaxLocal),

            // int ioMax = Math.Max(1, Environment.ProcessorCount);
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Call, processorCountGetter),
            Instruction.Create(OpCodes.Call, mathMax),
            Instruction.Create(OpCodes.Stloc, ioMaxLocal),

            // SetMaxThreadsResult = ThreadPool.SetMaxThreads(workerMax, ioMax);
            Instruction.Create(OpCodes.Ldloc, workerMaxLocal),
            Instruction.Create(OpCodes.Ldloc, ioMaxLocal),
            Instruction.Create(OpCodes.Call, setMaxThreadsRef),
            Instruction.Create(OpCodes.Stsfld, resultField),

            // ThreadPool.GetMaxThreads(out workerAfter, out ioAfter);
            Instruction.Create(OpCodes.Ldloca, workerAfterLocal),
            Instruction.Create(OpCodes.Ldloca, ioAfterLocal),
            Instruction.Create(OpCodes.Call, getMaxThreads),
            Instruction.Create(OpCodes.Ldloc, workerAfterLocal),
            Instruction.Create(OpCodes.Stsfld, workerAfterField),
            Instruction.Create(OpCodes.Ldloc, ioAfterLocal),
            Instruction.Create(OpCodes.Stsfld, ioAfterField),
        };

        var processor = body.GetILProcessor();
        foreach (var instruction in replacement)
        {
            processor.InsertBefore(loadWorkerArg, instruction);
        }
        processor.Remove(loadWorkerArg);
        processor.Remove(loadIoArg);
        processor.Remove(setMaxThreadsCall);
        processor.Remove(popInstruction);

        Console.WriteLine(
            $"  API PATCHED: {ctor.FullName} CPU-scaled thread pool sizing + diagnostics capture " +
            "(SetMaxThreadsWorkerBefore/After, SetMaxThreadsIoBefore/After, SetMaxThreadsResult)");

        int atomicCounters = PatchTyronThreadPoolAtomicCounters(module);
        if (atomicCounters != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 TyronThreadPool atomic counter patches, applied {atomicCounters}.");
        }
        return 1;
    }

    internal static int PatchTyronThreadPoolAtomicCounters(ModuleDefinition module)
    {
        var threadPool = module.GetType("Vintagestory.API.Common.TyronThreadPool")
            ?? throw new InvalidOperationException("TyronThreadPool is missing from the vanilla API.");

        var keyCounterField = threadPool.Fields.Single(field => field.Name == "keyCounter");
        var dedicatedCounterField = threadPool.Fields.Single(field => field.Name == "dedicatedCounter");
        var markStarted = threadPool.Methods.Single(method => method.Name == "MarkStarted");
        var createDedicatedThread = threadPool.Methods.Single(method => method.Name == "CreateDedicatedThread");

        var interlockedIncrement = module.ImportReference(
            typeof(Interlocked).GetMethod(
                nameof(Interlocked.Increment),
                new[] { typeof(int).MakeByRefType() })!);

        int patched = 0;
        patched += PatchAtomicPostIncrement(markStarted, keyCounterField, interlockedIncrement);
        patched += PatchAtomicPostIncrement(createDedicatedThread, dedicatedCounterField, interlockedIncrement);
        return patched;
    }

    private static int PatchAtomicPostIncrement(
        MethodDefinition method,
        FieldDefinition counterField,
        MethodReference interlockedIncrement)
    {
        ClearDebugInformation(method);

        var instructions = method.Body.Instructions;
        var ldfld = instructions.SingleOrDefault(instruction =>
            instruction.OpCode == OpCodes.Ldfld &&
            instruction.Operand is FieldReference field &&
            field.Name == counterField.Name &&
            field.DeclaringType.FullName == counterField.DeclaringType.FullName);

        if (ldfld is null)
        {
            bool alreadyPatched = instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Ldflda &&
                instruction.Operand is FieldReference field &&
                field.Name == counterField.Name &&
                field.DeclaringType.FullName == counterField.DeclaringType.FullName);
            if (alreadyPatched)
            {
                return 1;
            }

            throw new InvalidOperationException(
                $"{method.FullName}: expected ldfld {counterField.Name} for atomic post-increment rewrite.");
        }

        int ldfldIndex = instructions.IndexOf(ldfld);
        if (ldfldIndex < 2 || ldfldIndex + 6 >= instructions.Count)
        {
            throw new InvalidOperationException(
                $"{method.FullName}: unexpected instruction layout around ldfld {counterField.Name} at index {ldfldIndex}.");
        }

        var stfld = instructions[ldfldIndex + 5];
        if (stfld.OpCode != OpCodes.Stfld ||
            stfld.Operand is not FieldReference storedField ||
            storedField.Name != counterField.Name ||
            storedField.DeclaringType.FullName != counterField.DeclaringType.FullName)
        {
            throw new InvalidOperationException(
                $"{method.FullName}: expected stfld {counterField.Name} at offset +5 from ldfld, found {stfld.OpCode}.");
        }

        var duplicateInstanceLoad = instructions[ldfldIndex - 1];
        if (duplicateInstanceLoad.OpCode != OpCodes.Ldarg_0 && duplicateInstanceLoad.OpCode != OpCodes.Dup)
        {
            throw new InvalidOperationException(
                $"{method.FullName}: expected Ldarg_0 or Dup before ldfld {counterField.Name}, found {duplicateInstanceLoad.OpCode}.");
        }

        var toRemove = new Instruction[8];
        for (int i = 0; i < 8; i++)
        {
            toRemove[i] = instructions[ldfldIndex - 1 + i];
        }

        var processor = method.Body.GetILProcessor();
        processor.InsertBefore(duplicateInstanceLoad, Instruction.Create(OpCodes.Ldflda, counterField));
        processor.InsertBefore(duplicateInstanceLoad, Instruction.Create(OpCodes.Call, interlockedIncrement));
        processor.InsertBefore(duplicateInstanceLoad, Instruction.Create(OpCodes.Ldc_I4_1));
        processor.InsertBefore(duplicateInstanceLoad, Instruction.Create(OpCodes.Sub));

        foreach (var instruction in toRemove)
        {
            processor.Remove(instruction);
        }

        Console.WriteLine(
            $"  API PATCHED: {method.FullName} atomic Interlocked.Increment on {counterField.Name}");
        return 1;
    }

    private static int RemoveToLowerInvariantBeforeTryGetValue(MethodDefinition method)
    {
        ClearDebugInformation(method);

        var toLowerCalls = method.Body.Instructions.Where(instruction =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Operand is MethodReference callee &&
            callee.Name == "ToLowerInvariant" &&
            callee.DeclaringType.FullName == "System.String" &&
            callee.Parameters.Count == 0).ToArray();

        var processor = method.Body.GetILProcessor();
        foreach (var call in toLowerCalls)
        {
            processor.Remove(call);
        }
        return toLowerCalls.Length;
    }

    private static void ClearDebugInformation(MethodDefinition method)
    {
        method.DebugInformation.SequencePoints.Clear();
        method.DebugInformation.Scope = null;
    }

    private static bool StoresVariable(Instruction instruction, VariableDefinition variable) =>
        instruction.OpCode == OpCodes.Stloc_0 && variable.Index == 0 ||
        instruction.OpCode == OpCodes.Stloc_1 && variable.Index == 1 ||
        instruction.OpCode == OpCodes.Stloc_2 && variable.Index == 2 ||
        instruction.OpCode == OpCodes.Stloc_3 && variable.Index == 3 ||
        (instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S) &&
        instruction.Operand is VariableDefinition storedVariable && storedVariable == variable;

    /// <summary>
    /// Issue #651: Hooks lazy inventory mesh preparation into GuiElementItemSlotGridBase.RenderInteractiveElements,
    /// fast-paths filtered re-composition in FilterItemsBySearchText through OptimumApiBridge.TryComposeFilteredSlotGrid,
    /// aligns ComposeSlotOverlays/PostRenderInteractiveElements to renderedSlots with bounds checking, and skips
    /// empty-texture Dispose/reallocation when slotQuantityTextures[slotIndex].TextureId == 0.
    /// </summary>
    internal static int PatchGuiSlotGridPerformance(ModuleDefinition module, ModuleDefinition contractsModule)
    {
        const string gridTypeName = "Vintagestory.API.Client.GuiElementItemSlotGridBase";
        const string elementTypeName = "Vintagestory.API.Client.GuiElement";
        const string textureTypeName = "Vintagestory.API.Client.LoadedTexture";
        const string bridgeTypeName = "Vintagestory.API.Config.OptimumApiBridge";

        var grid = module.GetType(gridTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName} is missing from the vanilla API.");
        var element = module.GetType(elementTypeName)
            ?? throw new InvalidOperationException($"{elementTypeName} is missing from the vanilla API.");
        var textureType = module.GetType(textureTypeName)
            ?? throw new InvalidOperationException($"{textureTypeName} is missing from the vanilla API.");
        var bridgeType = contractsModule.GetType(bridgeTypeName)
            ?? throw new InvalidOperationException($"{bridgeTypeName} is missing from Optimum.Api.Contracts.");

        var prepareMethodDef = bridgeType.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "PrepareSlotGridInventoryMeshes" && m.Parameters.Count == 4)
            ?? throw new InvalidOperationException($"{bridgeTypeName}.PrepareSlotGridInventoryMeshes is missing.");
        var tryComposeFastDef = bridgeType.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "TryComposeFilteredSlotGrid" && m.Parameters.Count == 1)
            ?? throw new InvalidOperationException($"{bridgeTypeName}.TryComposeFilteredSlotGrid is missing.");

        var prepareMethodRef = module.ImportReference(prepareMethodDef);
        var tryComposeFastRef = module.ImportReference(tryComposeFastDef);

        var apiField = element.Fields.SingleOrDefault(f => f.Name == "api")
            ?? throw new InvalidOperationException($"{elementTypeName}.api is missing.");
        var boundsField = element.Fields.SingleOrDefault(f => f.Name == "Bounds")
            ?? throw new InvalidOperationException($"{elementTypeName}.Bounds is missing.");
        var renderedSlotsField = grid.Fields.SingleOrDefault(f => f.Name == "renderedSlots")
            ?? throw new InvalidOperationException($"{gridTypeName}.renderedSlots is missing.");
        var availableSlotsField = grid.Fields.SingleOrDefault(f => f.Name == "availableSlots")
            ?? throw new InvalidOperationException($"{gridTypeName}.availableSlots is missing.");
        var slotBoundsField = grid.Fields.SingleOrDefault(f => f.Name == "SlotBounds")
            ?? throw new InvalidOperationException($"{gridTypeName}.SlotBounds is missing.");
        var slotQuantityTexturesField = grid.Fields.SingleOrDefault(f => f.Name == "slotQuantityTextures")
            ?? throw new InvalidOperationException($"{gridTypeName}.slotQuantityTextures is missing.");
        var textureIdField = textureType.Fields.SingleOrDefault(f => f.Name == "TextureId" && f.FieldType.MetadataType == MetadataType.Int32)
            ?? throw new InvalidOperationException($"{textureTypeName}.TextureId is missing.");

        int patchesApplied = 0;

        // 1. RenderInteractiveElements: prepare visible + lookahead slot meshes before drawing.
        var renderMethod = grid.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "RenderInteractiveElements" && m.Parameters.Count == 1 && m.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.RenderInteractiveElements is missing.");
        if (!renderMethod.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "PrepareSlotGridInventoryMeshes"))
        {
            ClearDebugInformation(renderMethod);
            renderMethod.Body.MaxStackSize = Math.Max(renderMethod.Body.MaxStackSize, 8);
            var il = renderMethod.Body.GetILProcessor();
            var first = renderMethod.Body.Instructions[0];
            var prepInstrs = new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, apiField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, renderedSlotsField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, boundsField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, slotBoundsField),
                Instruction.Create(OpCodes.Call, prepareMethodRef),
            };
            RetargetBranches(renderMethod.Body.Instructions, first, prepInstrs[0]);
            foreach (var instr in prepInstrs)
            {
                il.InsertBefore(first, instr);
            }
            patchesApplied++;
        }

        // 2. FilterItemsBySearchText: skip full ComposeInteractiveElements re-composition when fast compose succeeds.
        var filterMethod = grid.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "FilterItemsBySearchText" && m.Parameters.Count == 3 && m.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.FilterItemsBySearchText is missing.");
        if (!filterMethod.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "TryComposeFilteredSlotGrid"))
        {
            var composeCall = filterMethod.Body.Instructions.FirstOrDefault(i =>
                i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference mr &&
                mr.Name == "ComposeInteractiveElements" &&
                mr.Parameters.Count == 0)
                ?? throw new InvalidOperationException($"{gridTypeName}.FilterItemsBySearchText does not call ComposeInteractiveElements.");
            var ldArgBeforeCall = composeCall.Previous;
            var retAfterCall = composeCall.Next;
            if (ldArgBeforeCall == null || ldArgBeforeCall.OpCode != OpCodes.Ldarg_0 ||
                retAfterCall == null || retAfterCall.OpCode != OpCodes.Ret)
            {
                throw new InvalidOperationException($"{gridTypeName}.FilterItemsBySearchText ComposeInteractiveElements tail shape mismatch.");
            }

            ClearDebugInformation(filterMethod);
            var il = filterMethod.Body.GetILProcessor();
            var fastInstrs = new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, tryComposeFastRef),
                Instruction.Create(OpCodes.Brtrue, retAfterCall),
            };
            foreach (var instr in fastInstrs)
            {
                il.InsertBefore(ldArgBeforeCall, instr);
            }
            patchesApplied++;
        }

        // 3. PostRenderInteractiveElements: look up dirty slots in renderedSlots so filtered grids never index out of bounds.
        var postRenderMethod = grid.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "PostRenderInteractiveElements" && m.Parameters.Count == 1 && m.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.PostRenderInteractiveElements is missing.");
        ClearDebugInformation(postRenderMethod);
        int postReplaced = 0;
        foreach (var instr in postRenderMethod.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Ldfld &&
                instr.Operand is FieldReference fr &&
                fr.Name == availableSlotsField.Name &&
                instr.Next?.Next?.Operand is MethodReference nextCall &&
                nextCall.Name == "IndexOfKey")
            {
                instr.Operand = renderedSlotsField;
                postReplaced++;
            }
        }
        if (postReplaced == 0)
        {
            throw new InvalidOperationException($"{gridTypeName}.PostRenderInteractiveElements availableSlots.IndexOfKey site not found.");
        }
        patchesApplied++;

        // 4. ComposeSlotOverlays: check renderedSlots + bounds guard, and skip Dispose/new LoadedTexture when TextureId == 0.
        var composeOverlays = grid.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "ComposeSlotOverlays" && m.Parameters.Count == 3 && m.HasBody)
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeSlotOverlays is missing.");
        ClearDebugInformation(composeOverlays);
        int overlayFieldReplaced = 0;
        foreach (var instr in composeOverlays.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Ldfld &&
                instr.Operand is FieldReference fr &&
                fr.Name == availableSlotsField.Name)
            {
                instr.Operand = renderedSlotsField;
                overlayFieldReplaced++;
            }
        }
        if (overlayFieldReplaced == 0)
        {
            throw new InvalidOperationException($"{gridTypeName}.ComposeSlotOverlays availableSlots lookup site not found.");
        }

        var disposeCall = composeOverlays.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == "Dispose" &&
            mr.DeclaringType.FullName == textureTypeName)
            ?? throw new InvalidOperationException($"{gridTypeName}.ComposeSlotOverlays LoadedTexture.Dispose call not found.");

        var loadElem = disposeCall.Previous;
        var loadArg3 = loadElem?.Previous;
        var loadQtyArr = loadArg3?.Previous;
        var loadArg0 = loadQtyArr?.Previous;
        if (loadArg0 == null || loadArg0.OpCode != OpCodes.Ldarg_0 ||
            loadQtyArr!.OpCode != OpCodes.Ldfld ||
            loadArg3!.OpCode != OpCodes.Ldarg_3 ||
            loadElem!.OpCode != OpCodes.Ldelem_Ref)
        {
            throw new InvalidOperationException($"{gridTypeName}.ComposeSlotOverlays Dispose sequence mismatch.");
        }

        var earlyRet = disposeCall.Next;
        while (earlyRet != null && earlyRet.OpCode != OpCodes.Ret)
        {
            earlyRet = earlyRet.Next;
        }
        if (earlyRet?.Previous == null || earlyRet.Previous.OpCode != OpCodes.Ldc_I4_1)
        {
            throw new InvalidOperationException($"{gridTypeName}.ComposeSlotOverlays early return after Dispose not found.");
        }
        var earlyTrue = earlyRet.Previous;

        var ovIl = composeOverlays.Body.GetILProcessor();
        var skipEmptyInstrs = new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, slotQuantityTexturesField),
            Instruction.Create(OpCodes.Ldarg_3),
            Instruction.Create(OpCodes.Ldelem_Ref),
            Instruction.Create(OpCodes.Ldfld, textureIdField),
            Instruction.Create(OpCodes.Brfalse, earlyTrue),
        };
        RetargetBranches(composeOverlays.Body.Instructions, loadArg0, skipEmptyInstrs[0]);
        foreach (var instr in skipEmptyInstrs)
        {
            ovIl.InsertBefore(loadArg0, instr);
        }

        var ovFirst = composeOverlays.Body.Instructions[0];
        var boundsGuardInstrs = new[]
        {
            Instruction.Create(OpCodes.Ldarg_3),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, slotQuantityTexturesField),
            Instruction.Create(OpCodes.Ldlen),
            Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Blt_Un, ovFirst),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Ret),
        };
        RetargetBranches(composeOverlays.Body.Instructions, ovFirst, boundsGuardInstrs[0]);
        foreach (var instr in boundsGuardInstrs)
        {
            ovIl.InsertBefore(ovFirst, instr);
        }
        patchesApplied++;

        Console.WriteLine($"  API PATCHED: {gridTypeName} window mesh prep, fast filtered compose, renderedSlots alignment, empty overlay skip");
        return patchesApplied;
    }

    private static void RetargetBranches(
        Mono.Collections.Generic.Collection<Instruction> instructions,
        Instruction oldTarget,
        Instruction newTarget)
    {
        foreach (var instruction in instructions)
        {
            if (ReferenceEquals(instruction.Operand, oldTarget))
            {
                instruction.Operand = newTarget;
            }
        }
    }

    /// <summary>
    /// Injects type-forwarding entries (ExportedType rows) into the vanilla API assembly
    /// for every public/internal type defined in the contracts assembly whose namespace
    /// matches one that VintagestoryLib references as [VintagestoryAPI]. This allows the
    /// CLR to resolve TypeRefs scoped to VintagestoryAPI that point at Optimum-original
    /// types living in Optimum.Api.Contracts.dll.
    /// </summary>
    private static int InjectTypeForwards(AssemblyDefinition vanilla, AssemblyDefinition contracts)
    {
        var contractsName = contracts.Name;
        var existingRef = vanilla.MainModule.AssemblyReferences
            .FirstOrDefault(r => r.Name == contractsName.Name);
        if (existingRef == null)
        {
            existingRef = new AssemblyNameReference(contractsName.Name, contractsName.Version)
            {
                PublicKeyToken = contractsName.PublicKeyToken,
                Culture = contractsName.Culture,
            };
            vanilla.MainModule.AssemblyReferences.Add(existingRef);
        }

        var existingForwards = new HashSet<string>(
            vanilla.MainModule.ExportedTypes.Select(e => e.FullName));

        int count = 0;
        foreach (var type in contracts.MainModule.Types)
        {
            if (type.Name == "<Module>") continue;
            if (existingForwards.Contains(type.FullName)) continue;
            // Skip types already defined in the vanilla assembly (e.g. if they
            // were injected by a prior phase or exist in the original vanilla).
            if (vanilla.MainModule.GetType(type.FullName) != null) continue;

            vanilla.MainModule.ExportedTypes.Add(
                new ExportedType(type.Namespace, type.Name, vanilla.MainModule, existingRef));
            count++;

            // Forward nested types as well.
            foreach (var nested in type.NestedTypes)
            {
                string nestedFullName = $"{type.FullName}/{nested.Name}";
                if (existingForwards.Contains(nestedFullName)) continue;
                if (vanilla.MainModule.GetType(nestedFullName) != null) continue;

                var nestedExport = new ExportedType(type.Namespace, nested.Name, vanilla.MainModule, existingRef);
                nestedExport.Attributes = TypeAttributes.NestedPublic;
                vanilla.MainModule.ExportedTypes.Add(nestedExport);
                count++;
            }
        }

        if (count > 0)
            Console.WriteLine($"  API PATCHED: {count} type forward(s) to {contractsName.Name}");
        return count;
    }

    internal static int PatchJsonLineInfo(ModuleDefinition module, ModuleDefinition contracts)
    {
        var bridge = contracts.GetType("Vintagestory.API.Config.OptimumApiBridge")
            ?? throw new InvalidOperationException("OptimumApiBridge is missing from the contracts assembly.");
        var parseTokenRef = module.ImportReference(bridge.Methods.Single(m =>
            m.IsStatic && m.Name == "ParseJsonToken" && m.Parameters.Count == 1));
        var readTokenRef = module.ImportReference(bridge.Methods.Single(m =>
            m.IsStatic && m.Name == "ReadJsonToken" && m.Parameters.Count == 1));

        int patched = 0;

        var parseTargets = new (string TypeName, string MethodName, int ParamCount)[]
        {
            ("Vintagestory.API.Datastructures.JsonObject", "FromJson", 1),
            ("Vintagestory.API.Common.CraftingRecipeIngredient", "FromBytes", 2),
            ("Vintagestory.API.Common.RecipeBase", "FromBytes", 2),
            ("Vintagestory.API.Common.JsonItemStack", "FromBytes", 2),
            ("Vintagestory.API.Common.AnimationMetaData", "FromBytes", 2),
            ("Vintagestory.API.Datastructures.TreeAttribute", "FromJson", 1),
        };

        foreach (var spec in parseTargets)
        {
            var type = module.GetType(spec.TypeName)
                ?? throw new InvalidOperationException($"{spec.TypeName} is missing from the vanilla API.");
            var methods = type.Methods.Where(m =>
                m.Name == spec.MethodName && m.Parameters.Count == spec.ParamCount && m.HasBody).ToList();
            if (methods.Count == 0)
                throw new InvalidOperationException($"{spec.TypeName}.{spec.MethodName} is missing.");

            foreach (var method in methods)
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                        && mr.DeclaringType.FullName == "Newtonsoft.Json.Linq.JToken"
                        && mr.Name == "Parse"
                        && mr.Parameters.Count == 1
                        && mr.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                    {
                        instr.Operand = parseTokenRef;
                        patched++;
                    }
                }
            }
        }

        var converterType = module.GetType("Vintagestory.API.Common.JsonAttributesConverter")
            ?? throw new InvalidOperationException("JsonAttributesConverter is missing from the vanilla API.");
        var readJsonMethod = converterType.Methods.SingleOrDefault(m => m.Name == "ReadJson" && m.HasBody)
            ?? throw new InvalidOperationException("JsonAttributesConverter.ReadJson is missing.");
        foreach (var instr in readJsonMethod.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Call && instr.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Newtonsoft.Json.Linq.JToken"
                && mr.Name == "ReadFrom"
                && mr.Parameters.Count == 1)
            {
                instr.Operand = readTokenRef;
                patched++;
            }
        }

        return patched;
    }

    /// <summary>
    /// Issue #640: Routes SoundAttributes.Clone, SoundAttributes.FromBytes, and
    /// SoundAttributeConverter.ReadJson through OptimumApiBridge to reuse
    /// SoundAttributes.NatFloatOne and SoundAttributes.RandomPitch while preserving ABI.
    /// </summary>
    internal static int PatchSoundAttributes(ModuleDefinition module, ModuleDefinition contracts)
    {
        const string soundTypeName = "Vintagestory.API.Common.SoundAttributes";
        const string converterTypeName = "Vintagestory.API.Common.SoundAttributeConverter";
        const string natFloatTypeName = "Vintagestory.API.MathTools.NatFloat";
        const string bridgeTypeName = "Vintagestory.API.Config.OptimumApiBridge";

        var bridge = contracts.GetType(bridgeTypeName)
            ?? throw new InvalidOperationException($"{bridgeTypeName} is missing from Optimum.Api.Contracts.");

        var cloneOrReuseRef = module.ImportReference(bridge.Methods.Single(m =>
            m.IsStatic && m.Name == "CloneOrReuseNatFloat" && m.Parameters.Count == 1));
        var readFromBytesRef = module.ImportReference(bridge.Methods.Single(m =>
            m.IsStatic && m.Name == "ReadCommonNatFloatFromBytes" && m.Parameters.Count == 1));
        var readJsonRef = module.ImportReference(bridge.Methods.Single(m =>
            m.IsStatic && m.Name == "ReadSoundAttributesJson" && m.Parameters.Count == 3));

        var soundType = module.GetType(soundTypeName)
            ?? throw new InvalidOperationException($"{soundTypeName} is missing from the vanilla API.");
        var converterType = module.GetType(converterTypeName)
            ?? throw new InvalidOperationException($"{converterTypeName} is missing from the vanilla API.");

        int patched = 0;

        // 1. SoundAttributes.Clone(): replace Pitch.Clone() / Volume.Clone() calls.
        var cloneMethod = soundType.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "Clone" && m.Parameters.Count == 0 && m.HasBody)
            ?? throw new InvalidOperationException($"{soundTypeName}.Clone() is missing.");
        ClearDebugInformation(cloneMethod);
        int existingCloneCalls = cloneMethod.Body.Instructions.Count(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.FullName == cloneOrReuseRef.FullName);
        if (existingCloneCalls == 2)
        {
            patched++;
        }
        else
        {
            var natCloneCalls = cloneMethod.Body.Instructions.Where(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == natFloatTypeName &&
                mr.Name == "Clone" &&
                mr.Parameters.Count == 0).ToArray();
            if (natCloneCalls.Length != 2)
            {
                throw new InvalidOperationException(
                    $"{cloneMethod.FullName}: expected 2 NatFloat.Clone calls, found {natCloneCalls.Length}.");
            }
            foreach (var instr in natCloneCalls)
            {
                instr.OpCode = OpCodes.Call;
                instr.Operand = cloneOrReuseRef;
            }
            patched++;
        }

        // 2. SoundAttributes.FromBytes(BinaryReader, string): replace NatFloat.createFromBytes(reader) calls.
        var fromBytesMethod = soundType.Methods.SingleOrDefault(m =>
            m.IsStatic && m.Name == "FromBytes" && m.Parameters.Count == 2 && m.HasBody)
            ?? throw new InvalidOperationException($"{soundTypeName}.FromBytes(BinaryReader, string) is missing.");
        ClearDebugInformation(fromBytesMethod);
        int existingFromBytesCalls = fromBytesMethod.Body.Instructions.Count(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.FullName == readFromBytesRef.FullName);
        if (existingFromBytesCalls == 2)
        {
            patched++;
        }
        else
        {
            var createFromBytesCalls = fromBytesMethod.Body.Instructions.Where(i =>
                i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == natFloatTypeName &&
                mr.Name == "createFromBytes" &&
                mr.Parameters.Count == 1).ToArray();
            if (createFromBytesCalls.Length != 2)
            {
                throw new InvalidOperationException(
                    $"{fromBytesMethod.FullName}: expected 2 NatFloat.createFromBytes calls, found {createFromBytesCalls.Length}.");
            }
            foreach (var instr in createFromBytesCalls)
            {
                instr.Operand = readFromBytesRef;
            }
            patched++;
        }

        // 3. SoundAttributeConverter.ReadJson(...): delegate to OptimumApiBridge.ReadSoundAttributesJson.
        var readJsonMethod = converterType.Methods.SingleOrDefault(m =>
            !m.IsStatic && m.Name == "ReadJson" && m.Parameters.Count == 5 && m.HasBody)
            ?? throw new InvalidOperationException($"{converterTypeName}.ReadJson is missing.");
        ClearDebugInformation(readJsonMethod);
        if (readJsonMethod.Body.Instructions.Count == 5 &&
            readJsonMethod.Body.Instructions[3].OpCode == OpCodes.Call &&
            readJsonMethod.Body.Instructions[3].Operand is MethodReference existingJson &&
            existingJson.FullName == readJsonRef.FullName)
        {
            patched++;
        }
        else
        {
            readJsonMethod.Body.ExceptionHandlers.Clear();
            readJsonMethod.Body.Variables.Clear();
            readJsonMethod.Body.InitLocals = false;
            var il = readJsonMethod.Body.GetILProcessor();
            readJsonMethod.Body.Instructions.Clear();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg, readJsonMethod.Parameters[4]);
            il.Emit(OpCodes.Call, readJsonRef);
            il.Emit(OpCodes.Ret);
            patched++;
        }

        Console.WriteLine($"  API PATCHED: {soundTypeName} + {converterTypeName} common NatFloat reuse ({patched}/3)");
        return patched;
    }
}
