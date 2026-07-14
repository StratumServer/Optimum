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
                                errors.Add($"{type.FullName}::{method.Name} references missing or signature-mismatched method " +
                                           $"{mr.DeclaringType.FullName}::{mr.Name}({mr.Parameters.Count} params) at IL_{instr.Offset:X4}");
                            break;

                        case FieldReference fr when IsSelfScoped(fr.DeclaringType, selfName):
                            var fieldError = FieldResolutionError(allTypes, fr);
                            if (fieldError != null)
                                errors.Add($"{type.FullName}::{method.Name} references {fieldError} at IL_{instr.Offset:X4}");
                            break;
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Catches an injected extern method (e.g. [DllImport]) that round-tripped through
    /// MemberInjector without its PInvokeInfo/ImplMap metadata. A method whose Attributes
    /// claim MethodAttributes.PInvokeImpl but which carries no matching PInvokeInfo is
    /// metadata-inconsistent: it has no IL body (correctly, since PInvokeImpl implies
    /// HasBody == false) and no ImplMap row backing the "this is a native call" flag either.
    /// The CLR loader falls back to treating that combination as an internal call (ECall),
    /// which is reserved for system-trusted assemblies and throws
    /// "ECall methods must be packaged into a system module" the first time the method runs
    /// — a crash-on-launch if it sits on the startup path, as it did for
    /// ClientPlatformWindows.OptimumTimeBeginPeriod in 0.2.8 (see
    /// docs/bugs/precise-frame-pacing-spin-tail-2026-07-14.md).
    /// </summary>
    public static List<string> VerifyPInvokeIntegrity(ModuleDefinition module)
    {
        var errors = new List<string>();

        foreach (var type in FlattenAll(module))
        {
            foreach (var method in type.Methods)
            {
                if (method.IsPInvokeImpl && method.PInvokeInfo == null)
                {
                    errors.Add($"{type.FullName}::{method.Name} is flagged PInvokeImpl but has no " +
                               "PInvokeInfo (missing ImplMap) — will throw \"ECall methods must be " +
                               "packaged into a system module\" at runtime instead of calling the native DLL");
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

        // The CLR resolves a method MemberRef by name plus full signature,
        // return type included, so the check must too.
        return type.Methods.Any(m =>
            m.Name == mr.Name &&
            m.Parameters.Count == mr.Parameters.Count &&
            m.ReturnType.FullName == mr.ReturnType.FullName &&
            m.Parameters.Select(p => p.ParameterType.FullName)
                .SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName)));
    }

    /// <summary>
    /// Returns a description of why the field reference cannot resolve against
    /// the module's own type table, or null when it resolves. The CLR resolves
    /// a field MemberRef by name plus signature (the field's type), so a field
    /// that exists under the right name but with a different type still throws
    /// MissingFieldException at JIT time. That is exactly what shipped in
    /// 0.2.1: the transplanted ChunkCuller.CullInvisibleChunks referenced
    /// ClientWorldMap.chunksLock as System.Threading.Lock while the vanilla
    /// field is object, and every world load killed the chunkculling thread.
    /// </summary>
    private static string? FieldResolutionError(List<TypeDefinition> allTypes, FieldReference fr)
    {
        var type = allTypes.FirstOrDefault(t => t.FullName == fr.DeclaringType.FullName);
        if (type == null)
            return $"field on missing type {fr.DeclaringType.FullName}::{fr.Name}";

        var field = type.Fields.FirstOrDefault(f => f.Name == fr.Name);
        if (field == null)
            return $"missing field {fr.DeclaringType.FullName}::{fr.Name}";

        if (field.FieldType.FullName != fr.FieldType.FullName)
            return $"field {fr.DeclaringType.FullName}::{fr.Name} with mismatched type " +
                   $"(reference says {fr.FieldType.FullName}, module defines {field.FieldType.FullName})";

        return null;
    }
}
