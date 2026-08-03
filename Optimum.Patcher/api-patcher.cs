using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Applies Optimum changes that must live inside the game API while preserving
/// the owned 1.22.5 assembly as the binary-compatible base.
/// </summary>
public static class ApiPatcher
{
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

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(vanillaPath)!);
        resolver.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(vanillaPath)!, "Lib"));
        resolver.AddSearchDirectory(Path.GetDirectoryName(contractsPath)!);

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

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaPath, vanillaReaderParameters);
        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath, contractsReaderParameters);

        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")
            ?? throw new InvalidOperationException("OptimumApiBridge is missing from the contracts assembly.");
        var markInventoryDirty = bridge.Methods.Single(method =>
            method.Name == "MarkInventoryDirty" && method.Parameters.Count == 0);
        var optimumFrustumCheck = bridge.Methods.Single(method =>
            method.Name == "InFrustumAndRange" && method.Parameters.Count == 5);
        var optimumShadowCheck = bridge.Methods.Single(method =>
            method.Name == "InFrustumShadowPass" && method.Parameters.Count == 3);
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
        vanilla.Write(outputPath, new WriterParameters { WriteSymbols = preserveSymbols });
        if (preserveSymbols)
        {
            Console.WriteLine($"  Wrote matching symbols: {Path.ChangeExtension(outputPath, ".pdb")}");
        }
        Console.WriteLine(
            $"API patch complete: {inventoryHooks} inventory hooks, {chiselHooks} chisel LOD hook, " +
            $"{chiselShadowHooks} chisel LOD shadow hooks, " +
            $"{loggerInitializers} symbol-independent logger initializer, " +
            $"{gameVersionLabels} game version label, {mat4fInlined} Mat4f inlined, " +
            $"{headControllerFallback} head controller pose fallback.");
        return true;
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
}
