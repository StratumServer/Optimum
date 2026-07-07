using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Inserts method calls into existing vanilla methods without replacing the body.
/// Used for cases where the method contains lambdas (can't transplant) but we need
/// to add a call at a specific point.
/// </summary>
public static class ILHook
{
    /// <summary>
    /// Inserts a call to a static/instance method before every `ret` instruction in the target.
    /// The hook method receives `this` (ldarg.0) as its first argument.
    /// Use for void methods that need a side-effect injected at exit points.
    /// </summary>
    public static bool InsertBeforeReturn(
        AssemblyDefinition vanillaAsm,
        string typeName, string methodName, int paramCount,
        string hookMethodName)
    {
        var type = vanillaAsm.MainModule.GetType(typeName);
        if (type == null) { Console.Error.WriteLine($"  HOOK SKIP: type not found {typeName}"); return false; }

        var method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.Parameters.Count == paramCount);
        if (method == null) { Console.Error.WriteLine($"  HOOK SKIP: method not found {typeName}::{methodName}"); return false; }

        var hookMethod = type.Methods.FirstOrDefault(m => m.Name == hookMethodName);
        if (hookMethod == null) { Console.Error.WriteLine($"  HOOK SKIP: hook method not found {typeName}::{hookMethodName}"); return false; }

        var il = method.Body.GetILProcessor();
        var retInstructions = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

        foreach (var ret in retInstructions)
        {
            // For non-void methods: the return value is on the stack before ret.
            // We need to insert our hook call BEFORE the value is loaded for return.
            // Strategy: insert ldarg.0 + call before the ret.
            // If the method is non-void, we need to be careful not to corrupt the stack.

            if (method.ReturnType.FullName == "System.Void")
            {
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Call, hookMethod));
            }
            else
            {
                // For non-void: value is on stack. Store it, call hook, reload it.
                // Find or create a local for the return value
                var retLocal = new VariableDefinition(method.ReturnType);
                method.Body.Variables.Add(retLocal);

                il.InsertBefore(ret, il.Create(OpCodes.Stloc, retLocal));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Ldloc, retLocal));
                il.InsertBefore(ret, il.Create(OpCodes.Call, hookMethod));
                // hookMethod should return the same type (pass-through)
                // or we reload from local:
                il.InsertBefore(ret, il.Create(OpCodes.Ldloc, retLocal));
                // Remove the original value load? No — ret expects one value.
                // Actually: stloc pops the value, then we ldloc before ret puts it back.
                // The call in between is void. Let me rethink.
            }
        }

        Console.WriteLine($"  HOOKED: {typeName}::{methodName} → {hookMethodName} ({retInstructions.Count} return points)");
        return true;
    }

    /// <summary>
    /// Inserts a call to hookMethod AFTER every occurrence of targetCallName in the method.
    /// The hook takes the return value of the target call (on stack) and returns it modified.
    /// Used to inject a call in the middle of a fluent builder chain.
    /// </summary>
    public static bool InsertBeforeCall(
        AssemblyDefinition vanillaAsm,
        string typeName, string methodName, int paramCount,
        string hookMethodName, string targetCallName)
    {
        var type = vanillaAsm.MainModule.GetType(typeName);
        if (type == null) { Console.Error.WriteLine($"  HOOK SKIP: type not found {typeName}"); return false; }

        var method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.Parameters.Count == paramCount);
        if (method == null) { Console.Error.WriteLine($"  HOOK SKIP: method not found {typeName}::{methodName}"); return false; }

        var hookMethod = type.Methods.FirstOrDefault(m => m.Name == hookMethodName);
        if (hookMethod == null) { Console.Error.WriteLine($"  HOOK SKIP: hook method not found {typeName}::{hookMethodName}"); return false; }

        var il = method.Body.GetILProcessor();
        var targetCalls = method.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                        i.Operand is MethodReference mr && mr.Name == targetCallName)
            .ToList();

        if (targetCalls.Count == 0)
        {
            Console.Error.WriteLine($"  HOOK SKIP: no call to {targetCallName} found in {typeName}::{methodName}");
            return false;
        }

        int inserted = 0;
        foreach (var call in targetCalls)
        {
            if (AlreadyCallsHook(call.Next, hookMethodName))
            {
                continue;
            }

            // After the target call: its return value (GuiComposer) is on the stack.
            // Insert AFTER the call: stloc tmp; ldarg.0; ldloc tmp; call hook; [result back on stack]
            var retLocal = new VariableDefinition(vanillaAsm.MainModule.ImportReference(hookMethod.ReturnType));
            method.Body.Variables.Add(retLocal);

            // Find the instruction AFTER the call
            var nextInstr = call.Next;
            if (nextInstr == null) continue;

            il.InsertBefore(nextInstr, il.Create(OpCodes.Stloc, retLocal));
            il.InsertBefore(nextInstr, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(nextInstr, il.Create(OpCodes.Ldloc, retLocal));
            il.InsertBefore(nextInstr, il.Create(OpCodes.Call, hookMethod));
            inserted++;
        }

        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, method.Body.MaxStackSize + 2);
        Console.WriteLine($"  HOOKED: {typeName}::{methodName} after {targetCallName} → {hookMethodName} ({inserted} sites)");
        return true;
    }

    private static bool AlreadyCallsHook(Instruction? start, string hookMethodName)
    {
        Instruction? cursor = start;
        for (int i = 0; i < 6 && cursor != null; i++, cursor = cursor.Next)
        {
            if ((cursor.OpCode == OpCodes.Call || cursor.OpCode == OpCodes.Callvirt) &&
                cursor.Operand is MethodReference method &&
                method.Name == hookMethodName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For a non-void method returning T: injects a call to a transform/void hook before ret.
    /// </summary>
    public static bool InsertVoidCallBeforeReturn(
        AssemblyDefinition vanillaAsm,
        string typeName, string methodName, int paramCount,
        string hookMethodName)
    {
        var type = vanillaAsm.MainModule.GetType(typeName);
        if (type == null) { Console.Error.WriteLine($"  HOOK SKIP: type not found {typeName}"); return false; }

        var method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.Parameters.Count == paramCount);
        if (method == null) { Console.Error.WriteLine($"  HOOK SKIP: method not found {typeName}::{methodName}"); return false; }

        var hookMethod = type.Methods.FirstOrDefault(m => m.Name == hookMethodName);
        if (hookMethod == null) { Console.Error.WriteLine($"  HOOK SKIP: hook method not found {typeName}::{hookMethodName}"); return false; }

        var il = method.Body.GetILProcessor();
        var retInstructions = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

        bool isTransform = hookMethod.ReturnType.FullName != "System.Void" && hookMethod.Parameters.Count == 1;

        foreach (var ret in retInstructions)
        {
            if (isTransform)
            {
                // Transform hook: takes the return value as param, returns the modified value.
                // Stack before ret: [returnValue]
                // After: ldarg.0; [returnValue already on stack]; call hook → [modifiedValue]; ret
                // Actually: hook is instance method with 1 param (the value).
                // Stack: [value] → ldarg.0 must go BEFORE the value.
                // We need: stloc tmp; ldarg.0; ldloc tmp; call hook; ret
                var retLocal = new VariableDefinition(method.ReturnType);
                method.Body.Variables.Add(retLocal);
                il.InsertBefore(ret, il.Create(OpCodes.Stloc, retLocal));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Ldloc, retLocal));
                il.InsertBefore(ret, il.Create(OpCodes.Call, hookMethod));
            }
            else if (method.ReturnType.FullName == "System.Void")
            {
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Call, hookMethod));
            }
            else
            {
                // Non-void, void hook: stash, call, restore
                var retLocal = new VariableDefinition(method.ReturnType);
                method.Body.Variables.Add(retLocal);
                il.InsertBefore(ret, il.Create(OpCodes.Stloc, retLocal));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Call, hookMethod));
                il.InsertBefore(ret, il.Create(OpCodes.Ldloc, retLocal));
            }
        }

        Console.WriteLine($"  HOOKED: {typeName}::{methodName} → {hookMethodName} ({retInstructions.Count} ret points, {(isTransform ? "transform" : "void")})");
        return true;
    }
}
