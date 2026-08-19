using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Catches the silent failure mode of <see cref="MemberInjector.RetypeFields"/>:
/// retyping a vanilla field's declared type in place (e.g. object -> System.Threading.Lock)
/// makes every untransplanted vanilla reader silently re-type too. `ldfld class Lock
/// ClientMain::dirtyChunksLock` followed by `Monitor::Enter(object, bool&)` is legal IL
/// and runs without error - but Monitor on a Lock instance does not interoperate with
/// Lock.EnterScope(). A transplanted reader (using the new locking primitive) and an
/// untransplanted one (using the old one, on the very same field) become mutually
/// non-exclusive, with no load-time or verification error to signal it.
///
/// Nothing else catches this: SelfConsistencyVerifier only inspects references scoped to
/// an AssemblyNameReference matching the module's own name - i.e. donor-imported
/// references - and vanilla's own untransplanted bodies are module-scoped, so
/// IsSelfScoped returns false for them.
/// </summary>
public static class RetypedFieldReaderVerifier
{
    public static List<string> Verify(
        ModuleDefinition module,
        IReadOnlyList<FieldDefinition> retypedFields,
        HashSet<string> acceptedReaderMethodKeys)
    {
        var errors = new List<string>();
        var retypedByDeclaringType = retypedFields
            .GroupBy(f => f.DeclaringType.FullName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var type in FlattenTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (!retypedByDeclaringType.TryGetValue(type.FullName, out var candidateFields)) continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode != OpCodes.Ldfld && instr.OpCode != OpCodes.Ldflda && instr.OpCode != OpCodes.Stfld)
                        continue;
                    if (instr.Operand is not FieldReference fieldRef) continue;

                    var retypedField = candidateFields.FirstOrDefault(f => f.Name == fieldRef.Name);
                    if (retypedField == null) continue;

                    string methodKey = MethodSignature.GetKey(method);
                    if (acceptedReaderMethodKeys.Contains(methodKey)) continue;

                    errors.Add(
                        $"{type.FullName}::{method.Name} reads retyped field " +
                        $"{fieldRef.DeclaringType.FullName}::{fieldRef.Name} ({instr.OpCode}) " +
                        $"at IL_{instr.Offset:X4} but was not transplanted from the donor");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<TypeDefinition> FlattenTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var n in FlattenNestedTypes(t))
                yield return n;
        }
    }

    private static IEnumerable<TypeDefinition> FlattenNestedTypes(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var x in FlattenNestedTypes(n))
                yield return x;
        }
    }
}
