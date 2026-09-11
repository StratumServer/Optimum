using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Attribute surgery for platform substitution: a renderer assembly subclasses a vanilla
/// type (ClientPlatformWindows) and overrides members that vanilla declares sealed or
/// non-virtual. <see cref="UnsealTypes"/> clears TypeAttributes.Sealed,
/// <see cref="VirtualizeMethods"/> turns a non-virtual method into a new virtual slot, and
/// <see cref="VerifyVirtualDispatch"/> proves no method body still reaches a virtualized
/// method with a non-virtual <c>call</c> (or <c>ldftn</c>): such a caller would silently
/// bypass the override, which is the one failure mode the CLR never reports.
/// </summary>
public static class PlatformSubstitution
{
    private const MethodAttributes VirtualFlags =
        MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

    public static int UnsealTypes(ModuleDefinition module, IReadOnlyList<string> typeNames)
    {
        int unsealed = 0;
        foreach (string typeName in typeNames)
        {
            TypeDefinition type = module.GetType(typeName)
                ?? throw new InvalidOperationException($"Type to unseal not found: {typeName}");
            if (type.IsInterface || type.IsValueType)
                throw new InvalidOperationException($"Type to unseal is not a class: {typeName}");
            // A static class is abstract|sealed; unsealing it would not make it subclassable.
            if (type.IsAbstract && type.IsSealed)
                throw new InvalidOperationException($"Type to unseal is a static class: {typeName}");
            type.Attributes &= ~TypeAttributes.Sealed;
            unsealed++;
        }
        return unsealed;
    }

    public static List<MethodDefinition> VirtualizeMethods(ModuleDefinition module, IReadOnlyList<MethodTarget> targets)
    {
        var virtualized = new List<MethodDefinition>();
        foreach (MethodTarget target in targets)
        {
            MethodDefinition method = FindTarget(module, target);
            string? visibility = OverridableVisibilityError(method);
            if (visibility != null)
                throw new InvalidOperationException(
                    $"Method to virtualize is {visibility} and cannot be overridden from another assembly: {target}");
            if (method.IsStatic || method.IsConstructor)
                throw new InvalidOperationException($"Method to virtualize is static or a constructor: {target}");
            if (method.IsVirtual && (!method.IsNewSlot || method.IsFinal))
                throw new InvalidOperationException(
                    $"Method to virtualize already overrides a base slot or is final: {target}");

            method.Attributes |= VirtualFlags;
            virtualized.Add(method);
        }
        return virtualized;
    }

    /// <summary>
    /// Returns one error per violation: a type that still carries Sealed, a method without
    /// Virtual, and every <c>call</c>/<c>ldftn</c> whose operand resolves to a virtualized
    /// method. A <c>call</c> from a subclass of the declaring type (a <c>base.X()</c> call)
    /// is legitimate and accepted.
    /// </summary>
    public static List<string> VerifyVirtualDispatch(
        ModuleDefinition module,
        IReadOnlyList<string> unsealedTypes,
        IReadOnlyList<MethodTarget> virtualizedMethods,
        out int virtualCallSites)
    {
        var errors = new List<string>();
        virtualCallSites = 0;

        foreach (string typeName in unsealedTypes)
        {
            TypeDefinition? type = module.GetType(typeName);
            if (type == null)
                errors.Add($"unsealed type {typeName} is missing from the output module");
            else if (type.IsSealed)
                errors.Add($"unsealed type {typeName} still carries TypeAttributes.Sealed");
        }

        var definitions = new List<MethodDefinition>();
        foreach (MethodTarget target in virtualizedMethods)
        {
            MethodDefinition method;
            try
            {
                method = FindTarget(module, target);
            }
            catch (InvalidOperationException error)
            {
                errors.Add(error.Message);
                continue;
            }
            if (!method.IsVirtual)
                errors.Add($"virtualized method {method.FullName} is not virtual");
            definitions.Add(method);
        }
        if (definitions.Count == 0)
            return errors;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodDefinition method in definitions)
            names.Add(method.Name);

        var typesByName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var allTypes = new List<TypeDefinition>();
        foreach (TypeDefinition type in module.Types)
            Collect(type, allTypes, typesByName);

        foreach (TypeDefinition type in allTypes)
        {
            foreach (MethodDefinition caller in type.Methods)
            {
                if (!caller.HasBody) continue;
                foreach (Instruction instruction in caller.Body.Instructions)
                {
                    OpCode opCode = instruction.OpCode;
                    if (opCode.Code != Code.Call && opCode.Code != Code.Callvirt &&
                        opCode.Code != Code.Ldftn && opCode.Code != Code.Ldvirtftn)
                        continue;
                    if (instruction.Operand is not MethodReference reference || !names.Contains(reference.Name))
                        continue;

                    MethodDefinition? resolved = Match(definitions, reference);
                    if (resolved == null) continue;

                    if (opCode.Code == Code.Callvirt || opCode.Code == Code.Ldvirtftn)
                    {
                        virtualCallSites++;
                        continue;
                    }
                    if (opCode.Code == Code.Call && IsStrictSubclass(type, resolved.DeclaringType, typesByName))
                        continue;

                    errors.Add(
                        $"{caller.FullName} reaches virtualized {resolved.FullName} with {opCode.Name} " +
                        $"at IL_{instruction.Offset:X4}; an override would be bypassed");
                }
            }
        }

        return errors;
    }

    private static MethodDefinition FindTarget(ModuleDefinition module, MethodTarget target)
    {
        TypeDefinition type = module.GetType(target.TypeFullName)
            ?? throw new InvalidOperationException($"Type of method to virtualize not found: {target}");
        MethodDefinition? found = null;
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name != target.MethodName || method.Parameters.Count != target.ParamCount || !target.Matches(method))
                continue;
            if (found != null)
                throw new InvalidOperationException($"Ambiguous method to virtualize: {target}");
            found = method;
        }
        return found ?? throw new InvalidOperationException($"Method to virtualize not found: {target}");
    }

    private static string? OverridableVisibilityError(MethodDefinition method)
    {
        switch (method.Attributes & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public:
            case MethodAttributes.Family:
            case MethodAttributes.FamORAssem:
                return null;
            case MethodAttributes.Private:
                return "private";
            case MethodAttributes.Assembly:
                return "internal";
            case MethodAttributes.FamANDAssem:
                return "private protected";
            default:
                return "compiler-controlled";
        }
    }

    private static MethodDefinition? Match(List<MethodDefinition> definitions, MethodReference reference)
    {
        foreach (MethodDefinition definition in definitions)
        {
            if (MethodSignature.Matches(definition, reference))
                return definition;
        }
        return null;
    }

    private static bool IsStrictSubclass(
        TypeDefinition type, TypeDefinition baseType, Dictionary<string, TypeDefinition> typesByName)
    {
        TypeReference? current = type.BaseType;
        int guard = 0;
        while (current != null && guard++ < 64)
        {
            string name = current is GenericInstanceType generic ? generic.ElementType.FullName : current.FullName;
            if (name == baseType.FullName)
                return true;
            if (!typesByName.TryGetValue(name, out TypeDefinition? next))
                return false;
            current = next.BaseType;
        }
        return false;
    }

    private static void Collect(TypeDefinition type, List<TypeDefinition> all, Dictionary<string, TypeDefinition> byName)
    {
        all.Add(type);
        byName[type.FullName] = type;
        foreach (TypeDefinition nested in type.NestedTypes)
            Collect(nested, all, byName);
    }
}
