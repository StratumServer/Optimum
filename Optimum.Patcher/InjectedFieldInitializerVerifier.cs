using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Catches the silent failure mode of injecting a field into an existing vanilla
/// type: the C# field initializer (<c>List&lt;T&gt; items = new();</c>) compiles into
/// the donor's constructor, not into the field. <see cref="MemberInjector"/> only
/// copies the field definition, so unless the matching vanilla constructor is a
/// transplant target the injected field keeps its CLR default (null or zero) and
/// the first transplanted reader throws NullReferenceException at runtime. The
/// source build never shows this because it compiles the donor constructor
/// directly.
///
/// For every field added to a pre-existing vanilla type, each donor constructor
/// that stores to that field must have its vanilla counterpart transplanted.
/// Donor-only constructors with no vanilla counterpart are ignored because no
/// vanilla caller can reach them.
/// </summary>
public static class InjectedFieldInitializerVerifier
{
    /// <summary>
    /// Records every field name per type before injection so the verifier can tell
    /// injected fields apart from vanilla ones.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Snapshot(ModuleDefinition module)
    {
        var snapshot = new Dictionary<string, HashSet<string>>();
        foreach (var type in FlattenTypes(module))
        {
            snapshot[type.FullName] = new HashSet<string>(type.Fields.Select(f => f.Name));
        }
        return snapshot;
    }

    public static List<string> Verify(
        ModuleDefinition vanilla,
        ModuleDefinition compiled,
        Dictionary<string, HashSet<string>> preInjectionFields,
        HashSet<string> transplantedMethodKeys)
    {
        var errors = new List<string>();
        foreach (var vanillaType in FlattenTypes(vanilla))
        {
            // Whole injected types are cloned with their constructors intact.
            if (!preInjectionFields.TryGetValue(vanillaType.FullName, out var originalFields)) continue;

            var injectedFields = vanillaType.Fields
                .Where(f => !originalFields.Contains(f.Name) && !f.HasConstant && !f.IsStatic)
                .ToList();
            if (injectedFields.Count == 0) continue;

            var compiledType = compiled.GetType(vanillaType.FullName);
            if (compiledType == null) continue;

            foreach (var field in injectedFields)
            {
                foreach (var donorCtor in compiledType.Methods.Where(m =>
                    m.HasBody && m.IsConstructor && !m.IsStatic))
                {
                    if (!StoresField(donorCtor, field)) continue;

                    var vanillaCtor = vanillaType.Methods.FirstOrDefault(m =>
                        m.IsConstructor && !m.IsStatic && MethodSignature.Matches(m, donorCtor));
                    if (vanillaCtor == null) continue;
                    if (transplantedMethodKeys.Contains(MethodSignature.GetKey(vanillaCtor))) continue;

                    errors.Add(
                        $"{vanillaType.FullName}::{field.Name} is injected and initialized by donor " +
                        $"{donorCtor.FullName}, but that constructor is not a transplant target; " +
                        "the field would stay at its CLR default at runtime.");
                }
            }
        }
        return errors;
    }

    private static bool StoresField(MethodDefinition method, FieldDefinition field)
    {
        OpCode store = field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode != store) continue;
            if (instruction.Operand is not FieldReference target) continue;
            if (target.Name != field.Name) continue;
            if (target.DeclaringType.FullName != field.DeclaringType.FullName) continue;
            return true;
        }
        return false;
    }

    private static IEnumerable<TypeDefinition> FlattenTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
                stack.Push(nested);
        }
    }
}
