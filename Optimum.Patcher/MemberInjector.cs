using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Injects new types, fields, and methods from the compiled assembly into the
/// vanilla assembly. Must run BEFORE method body transplants that reference
/// the injected members.
/// </summary>
public static class MemberInjector
{
    /// <summary>
    /// Inject entire types that exist in compiled but not in vanilla.
    /// </summary>
    public static int InjectTypes(AssemblyDefinition vanilla, AssemblyDefinition compiled, List<string> typeNames)
    {
        int injected = 0;
        foreach (var typeName in typeNames)
        {
            var existing = vanilla.MainModule.GetType(typeName);
            if (existing != null)
            {
                Console.WriteLine($"  TYPE EXISTS: {typeName}");
                continue;
            }

            var srcType = compiled.MainModule.GetType(typeName);
            if (srcType == null)
            {
                Console.Error.WriteLine($"  TYPE NOT FOUND in compiled: {typeName}");
                continue;
            }

            var newType = CloneType(srcType, vanilla.MainModule);
            vanilla.MainModule.Types.Add(newType);
            injected++;
            Console.WriteLine($"  INJECTED TYPE: {typeName}");
        }
        return injected;
    }

    /// <summary>
    /// Inject static fields/properties into an existing type.
    /// </summary>
    public static int InjectStaticMembers(AssemblyDefinition vanilla, AssemblyDefinition compiled, string typeName, List<string> memberNames)
    {
        var vanillaType = vanilla.MainModule.GetType(typeName);
        var compiledType = compiled.MainModule.GetType(typeName);
        if (vanillaType == null || compiledType == null)
        {
            Console.Error.WriteLine($"  TYPE NOT FOUND: {typeName}");
            return 0;
        }

        int injected = 0;
        foreach (var name in memberNames)
        {
            // Try as field first
            var srcField = compiledType.Fields.FirstOrDefault(f => f.Name == name);
            if (srcField != null && !vanillaType.Fields.Any(f => f.Name == name))
            {
                var newField = new FieldDefinition(
                    srcField.Name,
                    srcField.Attributes,
                    vanilla.MainModule.ImportReference(srcField.FieldType));
                if (srcField.HasConstant) newField.Constant = srcField.Constant;
                if (srcField.HasDefault) newField.Constant = srcField.Constant;
                vanillaType.Fields.Add(newField);
                injected++;
                Console.WriteLine($"  INJECTED FIELD: {typeName}::{name}");
                continue;
            }

            // Try as property (inject backing field + property + getter/setter)
            var srcProp = compiledType.Properties.FirstOrDefault(p => p.Name == name);
            if (srcProp != null && !vanillaType.Properties.Any(p => p.Name == name))
            {
                InjectProperty(vanillaType, srcProp, vanilla.MainModule, compiled);
                injected++;
                Console.WriteLine($"  INJECTED PROPERTY: {typeName}::{name}");
                continue;
            }

            // Try as method
            var srcMethod = compiledType.Methods.FirstOrDefault(m => m.Name == name);
            if (srcMethod != null && !vanillaType.Methods.Any(m => m.Name == name && m.Parameters.Count == srcMethod.Parameters.Count))
            {
                InjectMethod(vanillaType, srcMethod, vanilla.MainModule);
                injected++;
                Console.WriteLine($"  INJECTED METHOD: {typeName}::{name}");
                continue;
            }

            Console.Error.WriteLine($"  NOT FOUND: {typeName}::{name}");
        }
        return injected;
    }

    private static TypeDefinition CloneType(TypeDefinition src, ModuleDefinition targetModule)
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

        // Clone methods
        foreach (var method in src.Methods)
        {
            var newMethod = new MethodDefinition(
                method.Name,
                method.Attributes,
                targetModule.ImportReference(method.ReturnType));

            foreach (var param in method.Parameters)
            {
                newMethod.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    param.Attributes,
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
                    var newInstr = CloneInstructionForInjection(instr, targetModule);
                    instrMap[instr] = newInstr;
                    il.Append(newInstr);
                }

                // Fix branches
                foreach (var instr in newMethod.Body.Instructions)
                {
                    if (instr.Operand is Instruction t && instrMap.TryGetValue(t, out var m))
                        instr.Operand = m;
                    else if (instr.Operand is Instruction[] ts)
                        instr.Operand = ts.Select(x => instrMap.TryGetValue(x, out var mx) ? mx : x).ToArray();
                }

                // Exception handlers
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

    private static void InjectProperty(TypeDefinition target, PropertyDefinition src, ModuleDefinition targetModule, AssemblyDefinition compiledAsm)
    {
        var propType = targetModule.ImportReference(src.PropertyType);

        // Backing field (compiler-generated)
        var backingFieldName = $"<{src.Name}>k__BackingField";
        var srcBacking = src.DeclaringType.Fields.FirstOrDefault(f => f.Name == backingFieldName);

        if (srcBacking != null && !target.Fields.Any(f => f.Name == backingFieldName))
        {
            var newBacking = new FieldDefinition(backingFieldName, srcBacking.Attributes, propType);
            if (srcBacking.HasConstant) newBacking.Constant = srcBacking.Constant;
            target.Fields.Add(newBacking);
        }

        // Getter
        if (src.GetMethod != null && !target.Methods.Any(m => m.Name == src.GetMethod.Name))
        {
            InjectMethod(target, src.GetMethod, targetModule);
        }

        // Setter
        if (src.SetMethod != null && !target.Methods.Any(m => m.Name == src.SetMethod.Name))
        {
            InjectMethod(target, src.SetMethod, targetModule);
        }

        // Property definition
        var newProp = new PropertyDefinition(src.Name, src.Attributes, propType)
        {
            GetMethod = target.Methods.FirstOrDefault(m => m.Name == src.GetMethod?.Name),
            SetMethod = target.Methods.FirstOrDefault(m => m.Name == src.SetMethod?.Name),
        };
        target.Properties.Add(newProp);
    }

    private static void InjectMethod(TypeDefinition target, MethodDefinition src, ModuleDefinition targetModule)
    {
        var newMethod = new MethodDefinition(
            src.Name,
            src.Attributes,
            targetModule.ImportReference(src.ReturnType));

        foreach (var param in src.Parameters)
        {
            newMethod.Parameters.Add(new ParameterDefinition(
                param.Name,
                param.Attributes,
                targetModule.ImportReference(param.ParameterType)));
        }

        // extern P/Invoke methods (e.g. [DllImport]) have no IL body: MethodAttributes.PInvokeImpl
        // is already copied above via src.Attributes, but without a matching PInvokeInfo/ImplMap
        // row the method's flags claim PInvoke with nothing backing it. The CLR loader then treats
        // it as an internal ECall, which is only legal in system-trusted assemblies and throws
        // "ECall methods must be packaged into a system module" at runtime. Reconstruct the
        // PInvokeInfo (target-module ModuleReference + entry point + calling convention/charset)
        // and ImplAttributes so the injected method round-trips as a real P/Invoke.
        if (src.IsPInvokeImpl && src.PInvokeInfo != null)
        {
            var srcModuleRef = src.PInvokeInfo.Module;
            var targetModuleRef = targetModule.ModuleReferences.FirstOrDefault(m => m.Name == srcModuleRef.Name);
            if (targetModuleRef == null)
            {
                targetModuleRef = new ModuleReference(srcModuleRef.Name);
                targetModule.ModuleReferences.Add(targetModuleRef);
            }
            newMethod.PInvokeInfo = new PInvokeInfo(src.PInvokeInfo.Attributes, src.PInvokeInfo.EntryPoint, targetModuleRef);
            newMethod.ImplAttributes = src.ImplAttributes;
        }
        else if (src.HasBody)
        {
            newMethod.Body.InitLocals = src.Body.InitLocals;
            newMethod.Body.MaxStackSize = src.Body.MaxStackSize;

            foreach (var v in src.Body.Variables)
                newMethod.Body.Variables.Add(new VariableDefinition(targetModule.ImportReference(v.VariableType)));

            var instrMap = new Dictionary<Instruction, Instruction>();
            var il = newMethod.Body.GetILProcessor();
            foreach (var instr in src.Body.Instructions)
            {
                var newInstr = CloneInstructionForInjection(instr, targetModule);
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

            foreach (var h in src.Body.ExceptionHandlers)
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

        target.Methods.Add(newMethod);
    }

    private static Instruction CloneInstructionForInjection(Instruction src, ModuleDefinition targetModule)
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
        if (op is VariableDefinition) return Instruction.Create(src.OpCode, (VariableDefinition)op);
        if (op is ParameterDefinition) return Instruction.Create(src.OpCode, (ParameterDefinition)op);
        return Instruction.Create(src.OpCode);
    }
}
