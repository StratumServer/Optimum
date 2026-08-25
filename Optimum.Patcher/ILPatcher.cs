using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Transplants method bodies from a compiled (source-patched) assembly into the
/// vanilla assembly. This preserves all vanilla metadata (FieldRVA, inline array
/// data, type layouts) while injecting optimized method bodies.
/// </summary>
public static class ILPatcher
{
    public static int Patch(string vanillaPath, string compiledPath, string outputPath, List<MethodTarget> targets)
    {
        return PatchWithInjection(vanillaPath, compiledPath, outputPath, new(), new(), targets);
    }

    /// <summary>
    /// Adds the first ancestor directory of <paramref name="startPath"/> that contains
    /// VintagestoryAPI.dll (plus its Lib folder) to the resolver's search path. Returns
    /// true if such a directory was found.
    /// </summary>
    internal static bool AddGameRootSearchDirectories(BaseAssemblyResolver resolver, string startPath)
    {
        string? current;
        try
        {
            current = Path.GetDirectoryName(Path.GetFullPath(startPath));
        }
        catch
        {
            return false;
        }

        for (int depth = 0; current != null && depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(current, "VintagestoryAPI.dll")))
            {
                resolver.AddSearchDirectory(current);
                AddSearchDirectorySafe(resolver, Path.Combine(current, "Lib"));
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static void AddSearchDirectorySafe(BaseAssemblyResolver resolver, string? directory)
    {
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            resolver.AddSearchDirectory(directory);
        }
    }

    public static int PatchWithInjection(
        string vanillaPath, string compiledPath, string outputPath,
        List<string> typesToInject,
        Dictionary<string, List<string>> membersToInject,
        List<MethodTarget> targets,
        List<HookTarget>? hooks = null,
        Dictionary<string, List<string>>? interfacesToInject = null,
        bool requireAllTargets = true,
        Dictionary<string, List<string>>? fieldsToRetype = null)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(vanillaPath)!);
        resolver.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(vanillaPath)!, "Lib"));
        // Also search alongside the compiled DLL (for VintagestoryAPI.dll etc.)
        resolver.AddSearchDirectory(Path.GetDirectoryName(compiledPath)!);
        // ...and alongside the output, which for the launcher's own patch loop is
        // the cache dir holding the already-patched VintagestoryAPI.dll.
        AddSearchDirectorySafe(resolver, Path.GetDirectoryName(outputPath));
        // The mod pass reads its input from <game>/.optimum/vanilla/Mods and its
        // donor from <game>/.optimum/donors - neither directory holds a
        // VintagestoryAPI.dll, so Cecil cannot resolve the game assemblies from
        // any of the directories above. That stays invisible until Cecil has to
        // resolve a type to write it out (MetadataBuilder.GetConstantType, for a
        // parameter default value on an injected member), at which point
        // AssemblyDefinition.Write throws AssemblyResolutionException and the
        // launcher aborts the launch. Walk up from both inputs to the first
        // ancestor that actually contains VintagestoryAPI.dll - the game root.
        AddGameRootSearchDirectories(resolver, vanillaPath);
        AddGameRootSearchDirectories(resolver, compiledPath);

        string vanillaPdbPath = Path.ChangeExtension(vanillaPath, ".pdb");
        bool preserveSymbols = File.Exists(vanillaPdbPath);
        var vanillaReaderParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            ReadSymbols = preserveSymbols
        };
        var compiledReaderParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            ReadSymbols = false
        };

        using var vanillaAsm = AssemblyReader.Read(vanillaPath, vanillaReaderParams, out preserveSymbols);
        using var compiledAsm = AssemblyDefinition.ReadAssembly(compiledPath, compiledReaderParams);

        int injectedInterfaces = interfacesToInject == null
            ? 0
            : MemberInjector.InjectInterfaces(vanillaAsm, compiledAsm, interfacesToInject);

        // Phase 2a: Inject new types
        int injectedTypes = MemberInjector.InjectTypes(vanillaAsm, compiledAsm, typesToInject);

        // Phase 2b: Inject new members into existing types
        int injectedMembers = 0;
        foreach (var (typeName, members) in membersToInject)
        {
            injectedMembers += MemberInjector.InjectStaticMembers(vanillaAsm, compiledAsm, typeName, members);
        }

        // Phase 2c: Retype existing vanilla fields in place to match the donor's
        // declared type (e.g. object -> System.Threading.Lock), preserving the
        // FieldDefinition's metadata token so pre-existing vanilla method bodies
        // that already reference it keep resolving. Must run after Phase 2a/2b
        // (so the type/members it retypes already exist) and before Phase 1 (so
        // InjectMissingFieldsForMethod's type-mismatch check sees the corrected
        // type as agreement, not a mismatch, for every transplanted reader).
        var retypedFields = new List<FieldDefinition>();
        if (fieldsToRetype != null)
        {
            foreach (var (typeName, fieldNames) in fieldsToRetype)
            {
                retypedFields.AddRange(MemberInjector.RetypeFields(vanillaAsm, compiledAsm, typeName, fieldNames));
            }
        }
        var retargetedInitializerKeys = retypedFields.Count > 0
            ? RetargetFieldInitializers(vanillaAsm, retypedFields)
            : new HashSet<string>();

        // Phase 1: Transplant method bodies
        int patched = 0;
        int optionalSkipped = 0;
        var transplantedMethodKeys = new HashSet<string>();
        foreach (var target in targets)
        {
            var compiledMethod = FindMethod(compiledAsm, target);
            var vanillaMethod = compiledMethod is null
                ? FindMethod(vanillaAsm, target)
                : FindMatchingMethod(vanillaAsm, target, compiledMethod);

            if (vanillaMethod == null)
            {
                if (requireAllTargets && !target.Optional)
                    throw new InvalidOperationException($"Required vanilla method not found: {target}");
                if (target.Optional) optionalSkipped++;
                Console.Error.WriteLine($"  OPTIONAL SKIP (not in vanilla): {target}");
                continue;
            }
            if (compiledMethod == null)
            {
                if (requireAllTargets && !target.Optional)
                    throw new InvalidOperationException($"Required compiled method not found: {target}");
                if (target.Optional) optionalSkipped++;
                Console.Error.WriteLine($"  OPTIONAL SKIP (not in compiled): {target}");
                continue;
            }
            if (!compiledMethod.HasBody)
            {
                if (requireAllTargets && !target.Optional)
                    throw new InvalidOperationException($"Required compiled method has no body: {target}");
                if (target.Optional) optionalSkipped++;
                Console.Error.WriteLine($"  SKIP (no body): {target}");
                continue;
            }

            // Auto-inject compiler-generated nested types referenced by this method
            InjectNestedTypesForMethod(compiledMethod, vanillaAsm, compiledAsm);

            // Auto-inject missing fields referenced by this method
            InjectMissingFieldsForMethod(compiledMethod, vanillaAsm);

            // Auto-inject compiler-generated helper methods (typically lambdas)
            // referenced by a transplanted method but absent from the exact
            // vanilla type because the compiler-generated ordinal changed.
            InjectMissingMethodsForMethod(compiledMethod, vanillaAsm, compiledAsm);

            TransplantBody(vanillaMethod, compiledMethod, vanillaAsm, compiledAsm);
            patched++;
            transplantedMethodKeys.Add(MethodSignature.GetKey(vanillaMethod));
            Console.WriteLine($"  PATCHED: {target}");
        }

        // Phase 3: IL hooks (insert calls into existing methods)
        int hooked = 0;
        if (hooks != null)
        {
            foreach (var hook in hooks)
            {
                bool inserted = ILHook.InsertBeforeCall(
                    vanillaAsm,
                    hook.TypeFullName,
                    hook.MethodName,
                    hook.ParamCount,
                    hook.HookMethod,
                    hook.TargetCall,
                    hook.TargetDeclaringType,
                    hook.TargetParameterTypes,
                    hook.TargetReturnType,
                    hook.TargetHasThis,
                    hook.TargetExplicitThis,
                    hook.TargetCallingConvention,
                    hook.TargetGenericArity);
                if (!inserted && !hook.Optional)
                {
                    throw new InvalidOperationException($"Required IL hook was not applied: {hook}");
                }
                if (inserted)
                    hooked++;
            }
        }

        int requiredTargetCount = targets.Count(target => !target.Optional);
        Console.WriteLine(
            $"\n  Summary: {injectedTypes} types, {injectedMembers} members, " +
            $"{injectedInterfaces} interfaces injected, {patched}/{requiredTargetCount} required methods patched, " +
            $"{optionalSkipped} optional methods skipped, {hooked} hooks.");

        var selfRefErrors = SelfConsistencyVerifier.VerifySelfReferences(vanillaAsm.MainModule);
        if (selfRefErrors.Count > 0)
        {
            Console.Error.WriteLine($"\n  {selfRefErrors.Count} self-reference error(s), output not written:");
            foreach (var err in selfRefErrors)
                Console.Error.WriteLine($"    {err}");
            return -1;
        }

        var pinvokeErrors = SelfConsistencyVerifier.VerifyPInvokeIntegrity(vanillaAsm.MainModule);
        if (pinvokeErrors.Count > 0)
        {
            Console.Error.WriteLine($"\n  {pinvokeErrors.Count} PInvoke integrity error(s), output not written:");
            foreach (var err in pinvokeErrors)
                Console.Error.WriteLine($"    {err}");
            return -1;
        }

        if (retypedFields.Count > 0)
        {
            var acceptedReaderKeys = new HashSet<string>(transplantedMethodKeys);
            acceptedReaderKeys.UnionWith(retargetedInitializerKeys);
            var retypeErrors = RetypedFieldReaderVerifier.Verify(vanillaAsm.MainModule, retypedFields, acceptedReaderKeys);
            if (retypeErrors.Count > 0)
            {
                Console.Error.WriteLine($"\n  {retypeErrors.Count} retyped-field reader error(s), output not written:");
                foreach (var err in retypeErrors)
                    Console.Error.WriteLine($"    {err}");
                return -1;
            }
        }

        var ilErrors = IlStackVerifier.VerifyModule(vanillaAsm.MainModule);
        if (ilErrors.Count > 0)
        {
            Console.Error.WriteLine($"\n  {ilErrors.Count} invalid IL error(s), output not written:");
            foreach (var err in ilErrors)
                Console.Error.WriteLine($"    {err}");
            return -1;
        }

        vanillaAsm.Write(outputPath, new WriterParameters { WriteSymbols = preserveSymbols });
        if (preserveSymbols)
        {
            Console.WriteLine($"  Wrote matching symbols: {Path.ChangeExtension(outputPath, ".pdb")}");
        }
        return injectedTypes + injectedMembers + injectedInterfaces + patched;
    }

    /// <summary>
    /// Fixes up pre-existing vanilla field-initializer sites for fields
    /// <see cref="MemberInjector.RetypeFields"/> just retyped in place. A C#
    /// field initializer like <c>object gate = new object();</c> compiles to
    /// <c>ldarg.0; newobj System.Object::.ctor(); stfld object T::gate</c> in
    /// every constructor of the declaring type - after the field is retyped to
    /// e.g. System.Threading.Lock, that newobj still constructs a bare object,
    /// so the CLR would store an object where a Lock is declared. Full trust
    /// does not verify stfld operand types at load time, so this is silent
    /// undefined behaviour, not a load error, the first time a transplanted
    /// reader calls Lock-specific IL (Lock.EnterScope()) on it. The retyped
    /// field is never itself transplanted - the declaring type's constructor
    /// carries the `&lt;&gt;c`-cached-lambda blocker like every other ClientMain
    /// method - so this rewrites the pre-existing vanilla IL directly rather
    /// than replacing the whole method body.
    /// Returns the <see cref="MethodSignature.GetKey"/> of every method whose
    /// initializer was retargeted, so <see cref="RetypedFieldReaderVerifier"/>
    /// can accept those sites even though they were never Cecil-transplanted.
    /// </summary>
    internal static HashSet<string> RetargetFieldInitializers(
        AssemblyDefinition vanillaAsm, IReadOnlyList<FieldDefinition> retypedFields)
    {
        var retargetedMethodKeys = new HashSet<string>();
        if (retypedFields.Count == 0) return retargetedMethodKeys;

        foreach (var declaringType in retypedFields.Select(f => f.DeclaringType).Distinct())
        {
            var fieldsOnThisType = retypedFields.Where(f => f.DeclaringType == declaringType).ToList();
            foreach (var method in declaringType.Methods)
            {
                if (!method.HasBody) continue;
                if (!method.IsConstructor) continue;
                var instructions = method.Body.Instructions;
                for (int i = 1; i < instructions.Count; i++)
                {
                    if (instructions[i].OpCode != OpCodes.Stfld) continue;
                    if (instructions[i].Operand is not FieldReference fieldRef) continue;
                    var retypedField = fieldsOnThisType.FirstOrDefault(f => f.Name == fieldRef.Name);
                    if (retypedField == null) continue;

                    var predecessor = instructions[i - 1];
                    bool isPlainObjectCtorCall =
                        predecessor.OpCode == OpCodes.Newobj &&
                        predecessor.Operand is MethodReference ctorRef &&
                        ctorRef.DeclaringType.FullName == "System.Object" &&
                        ctorRef.Name == ".ctor";
                    if (!isPlainObjectCtorCall)
                    {
                        throw new InvalidOperationException(
                            $"{declaringType.FullName}::{method.Name} initializes retyped field " +
                            $"{fieldRef.DeclaringType.FullName}::{fieldRef.Name} with an unexpected " +
                            $"predecessor instruction ({predecessor.OpCode}) at IL_{predecessor.Offset:X4}; " +
                            "expected 'newobj System.Object::.ctor()'.");
                    }

                    predecessor.Operand = ResolveParameterlessConstructor(retypedField.FieldType, vanillaAsm.MainModule);
                    retargetedMethodKeys.Add(MethodSignature.GetKey(method));
                    Console.WriteLine(
                        $"    RETARGETED INITIALIZER: {declaringType.FullName}::{method.Name} field {fieldRef.Name} " +
                        $"newobj -> {retypedField.FieldType.FullName}::.ctor()");
                }
            }
        }
        return retargetedMethodKeys;
    }

    private static MethodReference ResolveParameterlessConstructor(TypeReference fieldType, ModuleDefinition module)
    {
        TypeDefinition resolved = fieldType.Resolve()
            ?? throw new InvalidOperationException(
                $"Cannot resolve retyped field type to find its parameterless constructor: {fieldType.FullName}");
        MethodDefinition ctor = resolved.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException(
                $"Retyped field type has no accessible parameterless constructor: {fieldType.FullName}");
        return module.ImportReference(ctor);
    }

    /// <summary>
    /// Scans a method body for references to compiler-generated nested types
    /// (DisplayClass, <>c) and injects them into the vanilla assembly if missing.
    /// </summary>
    private static void InjectNestedTypesForMethod(
        MethodDefinition compiledMethod,
        AssemblyDefinition vanillaAsm,
        AssemblyDefinition compiledAsm)
    {
        if (!compiledMethod.HasBody) return;

        var parentType = compiledMethod.DeclaringType;
        var vanillaParent = vanillaAsm.MainModule.GetType(parentType.FullName);
        if (vanillaParent == null) return;

        // Collect all type references from instructions
        var referencedTypes = new HashSet<string>();
        foreach (var instr in compiledMethod.Body.Instructions)
        {
            TypeReference? typeRef = instr.Operand switch
            {
                TypeReference tr => tr,
                MethodReference mr => mr.DeclaringType,
                FieldReference fr => fr.DeclaringType,
                _ => null
            };

            if (typeRef != null && IsCompilerGenerated(typeRef.Name) &&
                typeRef.FullName.StartsWith(parentType.FullName + "/"))
            {
                referencedTypes.Add(typeRef.Name);
            }
        }

        // Also check variable types
        foreach (var v in compiledMethod.Body.Variables)
        {
            if (IsCompilerGenerated(v.VariableType.Name) &&
                v.VariableType.FullName.StartsWith(parentType.FullName + "/"))
            {
                referencedTypes.Add(v.VariableType.Name);
            }
        }

        // Inject missing nested types
        foreach (var nestedName in referencedTypes)
        {
            var srcNested = parentType.NestedTypes.FirstOrDefault(t => t.Name == nestedName);
            if (srcNested == null) continue;

            var existingNested = vanillaParent.NestedTypes.FirstOrDefault(t => t.Name == nestedName);
            if (existingNested == null)
            {
                // Clone the nested type into vanilla.
                var newNested = CloneNestedType(srcNested, vanillaParent, vanillaAsm.MainModule);
                vanillaParent.NestedTypes.Add(newNested);
                Console.WriteLine($"    INJECTED NESTED: {parentType.FullName}/{nestedName}");
            }
            else
            {
                // Compiler-generated ordinals can collide when the donor gains
                // methods or closures. Keep vanilla's existing nested type and
                // merge the donor members that the transplanted body references.
                // Replacing the type would invalidate vanilla methods that still
                // reference its original fields or lambda helpers.
                MergeNestedTypeMembers(srcNested, existingNested, vanillaAsm.MainModule);
            }
        }
    }


    /// <summary>
    /// Scans a method body for field references. If a referenced field belongs to a type
    /// that exists in the vanilla assembly but the field itself is missing, inject it.
    /// </summary>
    private static void InjectMissingFieldsForMethod(MethodDefinition compiledMethod, AssemblyDefinition vanillaAsm)
    {
        if (!compiledMethod.HasBody) return;

        foreach (var instr in compiledMethod.Body.Instructions)
        {
            if (instr.Operand is not FieldReference fieldRef) continue;

            // Only handle fields in types that belong to the same assembly
            var declaringType = fieldRef.DeclaringType;
            var vanillaType = vanillaAsm.MainModule.GetType(declaringType.FullName);
            if (vanillaType == null) continue;

            // Check if field exists with the complete field signature.
            var existingField = vanillaType.Fields.FirstOrDefault(f => f.Name == fieldRef.Name);
            if (existingField is not null)
            {
                if (existingField.FieldType.FullName != fieldRef.FieldType.FullName)
                {
                    throw new InvalidOperationException(
                        $"Field signature mismatch for {declaringType.FullName}::{fieldRef.Name}: " +
                        $"reference uses {fieldRef.FieldType.FullName}, vanilla defines {existingField.FieldType.FullName}");
                }
                continue;
            }

            // Also check properties (backing fields get injected with properties)
            if (fieldRef.Name.StartsWith("<") && fieldRef.Name.EndsWith(">k__BackingField"))
            {
                var propName = fieldRef.Name[1..fieldRef.Name.IndexOf('>')];
                if (vanillaType.Properties.Any(p => p.Name == propName)) continue;
            }

            // Inject the field from the compiled type
            var compiledType = compiledMethod.Module.GetType(declaringType.FullName);
            if (compiledType == null)
            {
                throw new InvalidOperationException(
                    $"Required donor field type not found: {declaringType.FullName}");
            }

            var srcField = compiledType.Fields.FirstOrDefault(f =>
                f.Name == fieldRef.Name &&
                f.FieldType.FullName == fieldRef.FieldType.FullName);
            if (srcField == null)
            {
                throw new InvalidOperationException(
                    $"Required donor field not found: {declaringType.FullName}::{fieldRef.Name} " +
                    $"({fieldRef.FieldType.FullName})");
            }

            var newField = new FieldDefinition(
                srcField.Name,
                srcField.Attributes,
                vanillaAsm.MainModule.ImportReference(srcField.FieldType));
            if (srcField.HasConstant) newField.Constant = srcField.Constant;
            vanillaType.Fields.Add(newField);
            Console.WriteLine($"    INJECTED FIELD: {declaringType.FullName}::{srcField.Name}");
        }
    }

    private static void InjectMissingMethodsForMethod(
        MethodDefinition compiledMethod,
        AssemblyDefinition vanillaAsm,
        AssemblyDefinition compiledAsm)
    {
        if (!compiledMethod.HasBody) return;

        var missingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in compiledMethod.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference methodRef) continue;
            if (methodRef.DeclaringType.FullName != compiledMethod.DeclaringType.FullName) continue;

            var targetType = vanillaAsm.MainModule.GetType(methodRef.DeclaringType.FullName);
            if (targetType == null) continue;
            bool exists = targetType.Methods.Any(method =>
                MethodSignature.Matches(method, methodRef));
            if (!exists)
            {
                missingNames.Add(methodRef.Name);
            }
        }

        foreach (string name in missingNames)
        {
            MemberInjector.InjectStaticMembers(
                vanillaAsm,
                compiledAsm,
                compiledMethod.DeclaringType.FullName,
                new List<string> { name });
        }
    }

    private static bool IsCompilerGenerated(string name)
    {
        return name.Contains("<>c") || name.Contains("DisplayClass") ||
               name.StartsWith("<") || name.Contains("__");
    }

    private static TypeDefinition CloneNestedType(TypeDefinition src, TypeDefinition parent, ModuleDefinition targetModule)
    {
        var newType = new TypeDefinition(
            src.Namespace,
            src.Name,
            src.Attributes,
            src.BaseType != null ? targetModule.ImportReference(src.BaseType) : null);

        // Clone fields
        foreach (var field in src.Fields)
        {
            var newField = new FieldDefinition(
                field.Name,
                field.Attributes,
                targetModule.ImportReference(field.FieldType));
            if (field.HasConstant) newField.Constant = field.Constant;
            newType.Fields.Add(newField);
        }

        // Clone methods (with bodies)
        foreach (var method in src.Methods)
        {
            newType.Methods.Add(CloneNestedMethod(method, targetModule));
        }

        return newType;
    }

    private static void MergeNestedTypeMembers(
        TypeDefinition source,
        TypeDefinition target,
        ModuleDefinition targetModule)
    {
        foreach (var sourceField in source.Fields)
        {
            var targetField = target.Fields.FirstOrDefault(field => field.Name == sourceField.Name);
            if (targetField != null)
            {
                if (targetField.FieldType.FullName != sourceField.FieldType.FullName)
                {
                    throw new InvalidOperationException(
                        $"Compiler-generated nested field collision for {target.FullName}::{sourceField.Name}: " +
                        $"vanilla uses {targetField.FieldType.FullName}, donor uses {sourceField.FieldType.FullName}");
                }
                continue;
            }

            var newField = new FieldDefinition(
                sourceField.Name,
                sourceField.Attributes,
                targetModule.ImportReference(sourceField.FieldType));
            if (sourceField.HasConstant) newField.Constant = sourceField.Constant;
            target.Fields.Add(newField);
            Console.WriteLine($"    INJECTED NESTED FIELD: {target.FullName}::{sourceField.Name}");
        }

        foreach (var sourceMethod in source.Methods)
        {
            if (target.Methods.Any(method => MethodSignature.Matches(method, sourceMethod)))
            {
                continue;
            }

            target.Methods.Add(CloneNestedMethod(sourceMethod, targetModule));
            Console.WriteLine($"    INJECTED NESTED METHOD: {target.FullName}::{sourceMethod.Name}");
        }
    }

    private static MethodDefinition CloneNestedMethod(MethodDefinition source, ModuleDefinition targetModule)
    {
        var target = new MethodDefinition(
            source.Name,
            source.Attributes,
            targetModule.ImportReference(source.ReturnType));

        foreach (var parameter in source.Parameters)
        {
            target.Parameters.Add(new ParameterDefinition(
                parameter.Name,
                parameter.Attributes,
                targetModule.ImportReference(parameter.ParameterType)));
        }

        if (!source.HasBody)
        {
            return target;
        }

        target.Body.InitLocals = source.Body.InitLocals;
        target.Body.MaxStackSize = source.Body.MaxStackSize;

        foreach (var variable in source.Body.Variables)
        {
            target.Body.Variables.Add(new VariableDefinition(targetModule.ImportReference(variable.VariableType)));
        }

        var instructionMap = new Dictionary<Instruction, Instruction>();
        var il = target.Body.GetILProcessor();
        foreach (var instruction in source.Body.Instructions)
        {
            var cloned = CloneInstructionSimple(instruction, targetModule);
            instructionMap[instruction] = cloned;
            il.Append(cloned);
        }

        foreach (var instruction in target.Body.Instructions)
        {
            if (instruction.Operand is Instruction branch && instructionMap.TryGetValue(branch, out var mapped))
            {
                instruction.Operand = mapped;
            }
            else if (instruction.Operand is Instruction[] branches)
            {
                instruction.Operand = branches.Select(branch =>
                    instructionMap.TryGetValue(branch, out var mappedBranch) ? mappedBranch : branch).ToArray();
            }
        }

        foreach (var handler in source.Body.ExceptionHandlers)
        {
            target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = handler.TryStart != null ? instructionMap.GetValueOrDefault(handler.TryStart) : null,
                TryEnd = handler.TryEnd != null ? instructionMap.GetValueOrDefault(handler.TryEnd) : null,
                HandlerStart = handler.HandlerStart != null ? instructionMap.GetValueOrDefault(handler.HandlerStart) : null,
                HandlerEnd = handler.HandlerEnd != null ? instructionMap.GetValueOrDefault(handler.HandlerEnd) : null,
                CatchType = handler.CatchType != null ? targetModule.ImportReference(handler.CatchType) : null,
            });
        }

        return target;
    }

    private static Instruction CloneInstructionSimple(Instruction src, ModuleDefinition targetModule)
    {
        var op = src.Operand;
        if (op == null) return Instruction.Create(src.OpCode);
        if (op is MethodReference mr) return Instruction.Create(src.OpCode, targetModule.ImportReference(mr));
        if (op is TypeReference tr) return Instruction.Create(src.OpCode, targetModule.ImportReference(tr));
        if (op is FieldReference fr) return Instruction.Create(src.OpCode, targetModule.ImportReference(fr));
        if (op is string s) return Instruction.Create(src.OpCode, s);
        if (op is int i) return Instruction.Create(src.OpCode, i);
        if (op is long l) return Instruction.Create(src.OpCode, l);
        if (op is float f) return Instruction.Create(src.OpCode, f);
        if (op is double d) return Instruction.Create(src.OpCode, d);
        if (op is byte b) return Instruction.Create(src.OpCode, b);
        if (op is sbyte sb) return Instruction.Create(src.OpCode, sb);
        if (op is Instruction target) return Instruction.Create(src.OpCode, target);
        if (op is Instruction[] targets) return Instruction.Create(src.OpCode, targets);
        if (op is VariableDefinition vd) return Instruction.Create(src.OpCode, vd);
        if (op is ParameterDefinition pd) return Instruction.Create(src.OpCode, pd);
        return Instruction.Create(src.OpCode);
    }

    private static MethodDefinition? FindMethod(AssemblyDefinition asm, MethodTarget target)
    {
        var type = asm.MainModule.GetType(target.TypeFullName);
        if (type == null) return null;

        var candidates = type.Methods
            .Where(method => method.Name == target.MethodName &&
                method.Parameters.Count == target.ParamCount)
            .Where(target.Matches)
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous method target {target}: " +
                string.Join(", ", candidates.Select(MethodSignature.GetKey)));
        }
        return candidates.SingleOrDefault();
    }

    private static MethodDefinition? FindMatchingMethod(
        AssemblyDefinition asm,
        MethodTarget target,
        MethodDefinition compiledMethod)
    {
        var type = asm.MainModule.GetType(target.TypeFullName);
        if (type == null) return null;

        var candidates = type.Methods
            .Where(method => method.Name == target.MethodName &&
                method.Parameters.Count == target.ParamCount &&
                target.Matches(method) &&
                MethodSignature.Matches(method, compiledMethod))
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous vanilla signature for {target}: " +
                string.Join(", ", candidates.Select(MethodSignature.GetKey)));
        }
        return candidates.SingleOrDefault();
    }

    private static void TransplantBody(
        MethodDefinition vanilla,
        MethodDefinition compiled,
        AssemblyDefinition vanillaAsm,
        AssemblyDefinition compiledAsm)
    {
        vanilla.DebugInformation.SequencePoints.Clear();
        vanilla.DebugInformation.Scope = null;
        var body = vanilla.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        // Copy variables (create NEW definitions in the target body)
        var variableMap = new Dictionary<int, VariableDefinition>();
        foreach (var v in compiled.Body.Variables)
        {
            var importedType = vanillaAsm.MainModule.ImportReference(v.VariableType);
            var newVar = new VariableDefinition(importedType);
            body.Variables.Add(newVar);
            variableMap[v.Index] = newVar;
        }

        body.MaxStackSize = compiled.Body.MaxStackSize;
        body.InitLocals = compiled.Body.InitLocals;

        // Copy instructions (import all references into vanilla module)
        var ilProcessor = body.GetILProcessor();
        var instructionMap = new Dictionary<Instruction, Instruction>();

        foreach (var srcInstr in compiled.Body.Instructions)
        {
            var newInstr = CloneInstruction(srcInstr, vanillaAsm.MainModule, variableMap, vanilla);
            instructionMap[srcInstr] = newInstr;
            ilProcessor.Append(newInstr);
        }

        // Fix branch targets
        foreach (var instr in body.Instructions)
        {
            if (instr.Operand is Instruction targetInstr && instructionMap.TryGetValue(targetInstr, out var mapped))
            {
                instr.Operand = mapped;
            }
            else if (instr.Operand is Instruction[] targets2)
            {
                instr.Operand = targets2.Select(t => instructionMap.TryGetValue(t, out var m) ? m : t).ToArray();
            }
        }

        // Copy exception handlers
        foreach (var handler in compiled.Body.ExceptionHandlers)
        {
            var newHandler = new ExceptionHandler(handler.HandlerType)
            {
                TryStart = handler.TryStart != null ? instructionMap.GetValueOrDefault(handler.TryStart) : null,
                TryEnd = handler.TryEnd != null ? instructionMap.GetValueOrDefault(handler.TryEnd) : null,
                HandlerStart = handler.HandlerStart != null ? instructionMap.GetValueOrDefault(handler.HandlerStart) : null,
                HandlerEnd = handler.HandlerEnd != null ? instructionMap.GetValueOrDefault(handler.HandlerEnd) : null,
                FilterStart = handler.FilterStart != null ? instructionMap.GetValueOrDefault(handler.FilterStart) : null,
            };
            if (handler.CatchType != null)
                newHandler.CatchType = vanillaAsm.MainModule.ImportReference(handler.CatchType);
            body.ExceptionHandlers.Add(newHandler);
        }
    }

    private static Instruction CloneInstruction(
        Instruction src,
        ModuleDefinition targetModule,
        Dictionary<int, VariableDefinition> variableMap,
        MethodDefinition targetMethod)
    {
        var operand = src.Operand;

        if (operand == null)
            return Instruction.Create(src.OpCode);

        // Import references into target module
        if (operand is MethodReference methodRef)
            return Instruction.Create(src.OpCode, targetModule.ImportReference(methodRef));
        if (operand is TypeReference typeRef)
            return Instruction.Create(src.OpCode, targetModule.ImportReference(typeRef));
        if (operand is FieldReference fieldRef)
            return Instruction.Create(src.OpCode, targetModule.ImportReference(fieldRef));
        if (operand is string s)
            return Instruction.Create(src.OpCode, s);
        if (operand is int i)
            return Instruction.Create(src.OpCode, i);
        if (operand is long l)
            return Instruction.Create(src.OpCode, l);
        if (operand is float f)
            return Instruction.Create(src.OpCode, f);
        if (operand is double d)
            return Instruction.Create(src.OpCode, d);
        if (operand is byte b)
            return Instruction.Create(src.OpCode, b);
        if (operand is sbyte sb)
            return Instruction.Create(src.OpCode, sb);
        if (operand is Instruction target)
            return Instruction.Create(src.OpCode, target); // fixed up later
        if (operand is Instruction[] targets)
            return Instruction.Create(src.OpCode, targets); // fixed up later
        // VariableDefinition: remap by index to the new body's variables
        if (operand is VariableDefinition varDef)
        {
            if (variableMap.TryGetValue(varDef.Index, out var newVar))
                return Instruction.Create(src.OpCode, newVar);
            return Instruction.Create(src.OpCode, varDef);
        }
        // ParameterDefinition: remap by index to the target method's parameters
        if (operand is ParameterDefinition paramDef)
        {
            var targetParam = targetMethod.Parameters.Count > paramDef.Index
                ? targetMethod.Parameters[paramDef.Index]
                : paramDef;
            return Instruction.Create(src.OpCode, targetParam);
        }
        if (operand is CallSite callSite)
            return Instruction.Create(src.OpCode, callSite);

        // Fallback: create without operand (shouldn't happen)
        Console.Error.WriteLine($"  WARNING: unhandled operand type {operand.GetType().Name} for {src.OpCode}");
        return Instruction.Create(src.OpCode);
    }
}

/// <summary>
/// Identifies a method to transplant.
/// </summary>
public record MethodTarget(
    string TypeFullName,
    string MethodName,
    int ParamCount,
    IReadOnlyList<string>? ParameterTypes = null,
    bool Optional = false)
{
    public bool Matches(MethodDefinition method)
    {
        if (ParameterTypes is null) return true;
        return method.Parameters.Select(parameter => parameter.ParameterType.FullName)
            .SequenceEqual(ParameterTypes, StringComparer.Ordinal);
    }

    public override string ToString() =>
        $"{TypeFullName}::{MethodName}({ParamCount} params){(Optional ? " [optional]" : "")}";
}

public record HookTarget(
    string TypeFullName,
    string MethodName,
    int ParamCount,
    string HookMethod,
    string TargetCall,
    string TargetDeclaringType,
    IReadOnlyList<string> TargetParameterTypes,
    string TargetReturnType,
    bool TargetHasThis,
    bool TargetExplicitThis,
    MethodCallingConvention TargetCallingConvention,
    int TargetGenericArity,
    bool Optional = false)
{
    public override string ToString() =>
        $"{TypeFullName}::{MethodName} -> {HookMethod} before {TargetCall}";
}
