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

    public static int PatchWithInjection(
        string vanillaPath, string compiledPath, string outputPath,
        List<string> typesToInject,
        Dictionary<string, List<string>> membersToInject,
        List<MethodTarget> targets,
        List<(string typeName, string methodName, int paramCount, string hookMethod, string targetCall)>? hooks = null)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(vanillaPath)!);
        resolver.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(vanillaPath)!, "Lib"));
        // Also search alongside the compiled DLL (for VintagestoryAPI.dll etc.)
        resolver.AddSearchDirectory(Path.GetDirectoryName(compiledPath)!);

        var readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            ReadSymbols = false
        };

        using var vanillaAsm = AssemblyDefinition.ReadAssembly(vanillaPath, readerParams);
        using var compiledAsm = AssemblyDefinition.ReadAssembly(compiledPath, readerParams);

        // Phase 2a: Inject new types
        int injectedTypes = MemberInjector.InjectTypes(vanillaAsm, compiledAsm, typesToInject);

        // Phase 2b: Inject new members into existing types
        int injectedMembers = 0;
        foreach (var (typeName, members) in membersToInject)
        {
            injectedMembers += MemberInjector.InjectStaticMembers(vanillaAsm, compiledAsm, typeName, members);
        }

        // Phase 1: Transplant method bodies
        int patched = 0;
        foreach (var target in targets)
        {
            var vanillaMethod = FindMethod(vanillaAsm, target);
            var compiledMethod = FindMethod(compiledAsm, target);

            if (vanillaMethod == null)
            {
                Console.Error.WriteLine($"  SKIP (not in vanilla): {target}");
                continue;
            }
            if (compiledMethod == null)
            {
                Console.Error.WriteLine($"  SKIP (not in compiled): {target}");
                continue;
            }
            if (!compiledMethod.HasBody)
            {
                Console.Error.WriteLine($"  SKIP (no body): {target}");
                continue;
            }

            // Auto-inject compiler-generated nested types referenced by this method
            InjectNestedTypesForMethod(compiledMethod, vanillaAsm, compiledAsm);

            // Auto-inject missing fields referenced by this method
            InjectMissingFieldsForMethod(compiledMethod, vanillaAsm);

            TransplantBody(vanillaMethod, compiledMethod, vanillaAsm, compiledAsm);
            patched++;
            Console.WriteLine($"  PATCHED: {target}");
        }

        // Phase 3: IL hooks (insert calls into existing methods)
        int hooked = 0;
        if (hooks != null)
        {
            foreach (var (typeName, methodName, paramCount, hookMethod, targetCall) in hooks)
            {
                if (ILHook.InsertBeforeCall(vanillaAsm, typeName, methodName, paramCount, hookMethod, targetCall))
                    hooked++;
            }
        }

        Console.WriteLine($"\n  Summary: {injectedTypes} types, {injectedMembers} members injected, {patched}/{targets.Count} methods patched, {hooked} hooks.");

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

        vanillaAsm.Write(outputPath);
        return injectedTypes + injectedMembers + patched;
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
            if (vanillaParent.NestedTypes.Any(t => t.Name == nestedName))
                continue;

            var srcNested = parentType.NestedTypes.FirstOrDefault(t => t.Name == nestedName);
            if (srcNested == null) continue;

            var cloned = MemberInjector.InjectTypes(vanillaAsm, compiledAsm,
                new List<string>()); // handled below directly

            // Clone the nested type into vanilla
            var newNested = CloneNestedType(srcNested, vanillaParent, vanillaAsm.MainModule);
            vanillaParent.NestedTypes.Add(newNested);
            Console.WriteLine($"    INJECTED NESTED: {parentType.FullName}/{nestedName}");
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

            // Check if field exists
            if (vanillaType.Fields.Any(f => f.Name == fieldRef.Name)) continue;

            // Also check properties (backing fields get injected with properties)
            if (fieldRef.Name.StartsWith("<") && fieldRef.Name.EndsWith(">k__BackingField"))
            {
                var propName = fieldRef.Name[1..fieldRef.Name.IndexOf('>')];
                if (vanillaType.Properties.Any(p => p.Name == propName)) continue;
            }

            // Inject the field from the compiled type
            var compiledType = compiledMethod.Module.GetType(declaringType.FullName);
            if (compiledType == null) continue;

            var srcField = compiledType.Fields.FirstOrDefault(f => f.Name == fieldRef.Name);
            if (srcField == null) continue;

            var newField = new FieldDefinition(
                srcField.Name,
                srcField.Attributes,
                vanillaAsm.MainModule.ImportReference(srcField.FieldType));
            if (srcField.HasConstant) newField.Constant = srcField.Constant;
            vanillaType.Fields.Add(newField);
            Console.WriteLine($"    INJECTED FIELD: {declaringType.FullName}::{srcField.Name}");
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
            var newMethod = new MethodDefinition(
                method.Name,
                method.Attributes,
                targetModule.ImportReference(method.ReturnType));

            foreach (var param in method.Parameters)
            {
                newMethod.Parameters.Add(new ParameterDefinition(
                    param.Name, param.Attributes,
                    targetModule.ImportReference(param.ParameterType)));
            }

            if (method.HasBody)
            {
                newMethod.Body.InitLocals = method.Body.InitLocals;
                newMethod.Body.MaxStackSize = method.Body.MaxStackSize;

                foreach (var v in method.Body.Variables)
                    newMethod.Body.Variables.Add(new VariableDefinition(targetModule.ImportReference(v.VariableType)));

                var instrMap = new Dictionary<Instruction, Instruction>();
                var il = newMethod.Body.GetILProcessor();
                foreach (var instr in method.Body.Instructions)
                {
                    var newInstr = CloneInstructionSimple(instr, targetModule);
                    instrMap[instr] = newInstr;
                    il.Append(newInstr);
                }

                foreach (var instr in newMethod.Body.Instructions)
                {
                    if (instr.Operand is Instruction t && instrMap.TryGetValue(t, out var m))
                        instr.Operand = m;
                    else if (instr.Operand is Instruction[] ts)
                        instr.Operand = ts.Select(x => instrMap.TryGetValue(x, out var mx) ? mx : x).ToArray();
                }

                foreach (var h in method.Body.ExceptionHandlers)
                {
                    newMethod.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                    {
                        TryStart = h.TryStart != null ? instrMap.GetValueOrDefault(h.TryStart) : null,
                        TryEnd = h.TryEnd != null ? instrMap.GetValueOrDefault(h.TryEnd) : null,
                        HandlerStart = h.HandlerStart != null ? instrMap.GetValueOrDefault(h.HandlerStart) : null,
                        HandlerEnd = h.HandlerEnd != null ? instrMap.GetValueOrDefault(h.HandlerEnd) : null,
                        CatchType = h.CatchType != null ? targetModule.ImportReference(h.CatchType) : null,
                    });
                }
            }

            newType.Methods.Add(newMethod);
        }

        return newType;
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

        return type.Methods.FirstOrDefault(m =>
            m.Name == target.MethodName &&
            m.Parameters.Count == target.ParamCount);
    }

    private static void TransplantBody(
        MethodDefinition vanilla,
        MethodDefinition compiled,
        AssemblyDefinition vanillaAsm,
        AssemblyDefinition compiledAsm)
    {
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
public record MethodTarget(string TypeFullName, string MethodName, int ParamCount)
{
    public override string ToString() => $"{TypeFullName}::{MethodName}({ParamCount} params)";
}
