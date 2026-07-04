using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace Optimum.Patcher;

/// <summary>
/// Catches a transplant or injection that references a member missing from the
/// output module, before the module gets written to disk.
///
/// Mono.Cecil's ImportReference has no way to know that a reference copied from
/// the compiled donor assembly (also named VintagestoryLib, since the donor
/// project matches the vanilla assembly name on purpose) is truly self-referential
/// once merged into the vanilla module being patched. It produces a reference
/// scoped to an external assembly named "VintagestoryLib" rather than the module
/// itself, so tools that resolve through the normal external-assembly path (an
/// on-disk file search, like ilverify's -r flag or a fresh AssemblyDefinition.Resolve)
/// either fail outright or, worse, silently resolve against whatever same-named
/// VintagestoryLib.dll happens to sit in the search path, which is never the file
/// actually being verified. This check resolves those specific self-scoped
/// references directly against the in-memory output module's own type table,
/// the same thing the CLR does at load time for a single-assembly self-reference.
///
/// Ordinary vanilla method bodies never trigger this: Cecil scopes their
/// same-module references to the ModuleDefinition itself, not an
/// AssemblyNameReference, since those instructions were read directly from
/// vanillaPath and never round-tripped through ImportReference from a
/// different AssemblyDefinition object.
/// </summary>
public static class SelfConsistencyVerifier
{
    public static List<string> VerifySelfReferences(ModuleDefinition module)
    {
        var errors = new List<string>();
        var selfName = module.Assembly.Name.Name;
        var allTypes = FlattenAll(module).ToList();

        foreach (var type in allTypes)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                foreach (var instr in method.Body.Instructions)
                {
                    switch (instr.Operand)
                    {
                        case MethodReference mr when IsSelfScoped(mr.DeclaringType, selfName):
                            if (!ResolvesInModule(allTypes, mr))
                                errors.Add($"{type.FullName}::{method.Name} references missing method " +
                                           $"{mr.DeclaringType.FullName}::{mr.Name}({mr.Parameters.Count} params) at IL_{instr.Offset:X4}");
                            break;

                        case FieldReference fr when IsSelfScoped(fr.DeclaringType, selfName):
                            if (!ResolvesInModule(allTypes, fr))
                                errors.Add($"{type.FullName}::{method.Name} references missing field " +
                                           $"{fr.DeclaringType.FullName}::{fr.Name} at IL_{instr.Offset:X4}");
                            break;
                    }
                }
            }
        }

        return errors;
    }

    private static IEnumerable<TypeDefinition> FlattenAll(ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var n in FlattenNested(t))
                yield return n;
        }
    }

    private static IEnumerable<TypeDefinition> FlattenNested(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var x in FlattenNested(n))
                yield return x;
        }
    }

    private static bool IsSelfScoped(TypeReference typeRef, string selfName)
    {
        var t = typeRef;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Scope is AssemblyNameReference anr && anr.Name == selfName;
    }

    private static bool ResolvesInModule(List<TypeDefinition> allTypes, MethodReference mr)
    {
        var type = allTypes.FirstOrDefault(t => t.FullName == mr.DeclaringType.FullName);
        if (type == null) return false;

        return type.Methods.Any(m =>
            m.Name == mr.Name &&
            m.Parameters.Count == mr.Parameters.Count &&
            m.Parameters.Select(p => p.ParameterType.FullName)
                .SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName)));
    }

    private static bool ResolvesInModule(List<TypeDefinition> allTypes, FieldReference fr)
    {
        var type = allTypes.FirstOrDefault(t => t.FullName == fr.DeclaringType.FullName);
        if (type == null) return false;

        return type.Fields.Any(f => f.Name == fr.Name);
    }
}
