using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

public static class IlStackVerifier
{
    public static IReadOnlyList<string> VerifyModule(ModuleDefinition module)
    {
        var errors = new List<string>();
        foreach (var type in FlattenTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                errors.AddRange(VerifyMethod(method));
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> VerifyMethod(MethodDefinition method)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
            return Array.Empty<string>();

        var instructions = method.Body.Instructions;
        var indexes = instructions
            .Select((instruction, index) => (instruction, index))
            .ToDictionary(item => item.instruction, item => item.index);
        var stackAt = Enumerable.Repeat(-1, instructions.Count).ToArray();
        var pending = new Queue<int>();
        var errors = new List<string>();
        int currentIndex = 0;

        SetStack(0, 0);
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.HandlerStart is not null && indexes.TryGetValue(handler.HandlerStart, out int handlerIndex))
            {
                SetStack(handlerIndex, handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0);
            }

            if (handler.FilterStart is not null && indexes.TryGetValue(handler.FilterStart, out int filterIndex))
            {
                SetStack(filterIndex, 1);
            }
        }

        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            currentIndex = index;
            var instruction = instructions[index];
            int stack = stackAt[index];
            int popCount;
            int pushCount;

            if (instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S)
            {
                AddTarget(instruction.Operand, 0);
                continue;
            }

            try
            {
                popCount = GetPopCount(method, instruction);
                pushCount = GetPushCount(instruction);
            }
            catch (Exception ex)
            {
                errors.Add(FormatError(method, instruction, ex.Message));
                continue;
            }

            if (stack < popCount)
            {
                errors.Add(FormatError(
                    method,
                    instruction,
                    $"stack underflow: requires {popCount}, has {stack}"));
                continue;
            }

            int nextStack = stack - popCount + pushCount;
            if (instruction.OpCode == OpCodes.Ret && nextStack != 0)
            {
                errors.Add(FormatError(
                    method,
                    instruction,
                    $"ret leaves {nextStack} value(s) on the evaluation stack"));
                continue;
            }

            if (instruction.OpCode == OpCodes.Endfilter && nextStack != 0)
            {
                errors.Add(FormatError(
                    method,
                    instruction,
                    "endfilter requires an empty evaluation stack after its result"));
                continue;
            }

            if (instruction.OpCode == OpCodes.Ret ||
                instruction.OpCode == OpCodes.Throw ||
                instruction.OpCode == OpCodes.Rethrow ||
                instruction.OpCode == OpCodes.Endfinally ||
                instruction.OpCode == OpCodes.Endfilter ||
                instruction.OpCode == OpCodes.Jmp)
            {
                continue;
            }

            if (instruction.OpCode == OpCodes.Switch)
            {
                if (instruction.Operand is not Instruction[] targets)
                {
                    errors.Add(FormatError(method, instruction, "switch has no target table"));
                    continue;
                }

                foreach (var target in targets)
                    AddTarget(target, nextStack);
                AddFallthrough(index, nextStack);
                continue;
            }

            if (IsUnconditionalBranch(instruction.OpCode))
            {
                AddTarget(instruction.Operand, nextStack);
                continue;
            }

            if (IsConditionalBranch(instruction.OpCode))
            {
                AddTarget(instruction.Operand, nextStack);
                AddFallthrough(index, nextStack);
                continue;
            }

            AddFallthrough(index, nextStack);
        }

        return errors;

        void SetStack(int index, int depth)
        {
            if (stackAt[index] == -1)
            {
                stackAt[index] = depth;
                pending.Enqueue(index);
                return;
            }

            if (stackAt[index] != depth)
            {
                errors.Add(FormatError(
                    method,
                    instructions[index],
                    $"inconsistent stack depth: saw {stackAt[index]} and {depth}"));
            }
        }

        void AddTarget(object? operand, int depth)
        {
            if (operand is not Instruction target || !indexes.TryGetValue(target, out int targetIndex))
            {
                errors.Add(FormatError(method, instructions[currentIndex], "branch target is invalid"));
                return;
            }

            SetStack(targetIndex, depth);
        }

        void AddFallthrough(int instructionIndex, int depth)
        {
            int nextIndex = instructionIndex + 1;
            if (nextIndex < instructions.Count)
                SetStack(nextIndex, depth);
        }
    }

    private static int GetPopCount(MethodDefinition method, Instruction instruction)
    {
        if (instruction.OpCode.StackBehaviourPop != StackBehaviour.Varpop)
            return FixedStackCount(instruction.OpCode.StackBehaviourPop);

        if (instruction.OpCode == OpCodes.Ret)
            return method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;

        if (instruction.OpCode == OpCodes.Jmp)
            return 0;

        return instruction.Operand switch
        {
            MethodReference methodReference => methodReference.Parameters.Count +
                (instruction.OpCode == OpCodes.Newobj || !methodReference.HasThis ? 0 : 1),
            CallSite callSite => callSite.Parameters.Count + (callSite.HasThis ? 1 : 0),
            _ => throw new InvalidOperationException("variable-pop instruction has an unsupported operand")
        };
    }

    private static int GetPushCount(Instruction instruction)
    {
        if (instruction.OpCode.StackBehaviourPush != StackBehaviour.Varpush)
            return FixedStackCount(instruction.OpCode.StackBehaviourPush);

        return instruction.OpCode == OpCodes.Newobj
            ? 1
            : instruction.Operand switch
            {
                MethodReference methodReference => methodReference.ReturnType.MetadataType == MetadataType.Void ? 0 : 1,
                CallSite callSite => callSite.ReturnType.MetadataType == MetadataType.Void ? 0 : 1,
                _ => throw new InvalidOperationException("variable-push instruction has an unsupported operand")
            };
    }

    private static int FixedStackCount(StackBehaviour behaviour)
    {
        return behaviour switch
        {
            StackBehaviour.Pop0 or StackBehaviour.Push0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref or
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushref or
            StackBehaviour.Pushr4 => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
            StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popi_pop1 or
            StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi or
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popref or
            StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or
            StackBehaviour.Popref_popi_popr4 or
            StackBehaviour.Popref_popi_popr8 => 3,
            StackBehaviour.Pushi8 or StackBehaviour.Pushr8 => 1,
            _ => throw new InvalidOperationException($"unsupported stack behaviour: {behaviour}")
        };
    }

    private static bool IsUnconditionalBranch(OpCode opCode)
    {
        return opCode == OpCodes.Br || opCode == OpCodes.Br_S;
    }

    private static bool IsConditionalBranch(OpCode opCode)
    {
        return opCode.FlowControl == FlowControl.Cond_Branch;
    }

    private static string FormatError(MethodDefinition method, Instruction instruction, string message)
    {
        return $"{method.DeclaringType.FullName}::{method.Name} at IL_{instruction.Offset:X4}: {message}";
    }

    private static IEnumerable<TypeDefinition> FlattenTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in FlattenNestedTypes(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> FlattenNestedTypes(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var descendant in FlattenNestedTypes(nested))
                yield return descendant;
        }
    }
}
