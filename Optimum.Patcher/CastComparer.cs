using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Compares castclass/isinst targets per method between the vanilla assembly
/// and the compiled (decompile-recompile) donor, to catch decompiler
/// type-binding artifacts: ilspy can emit an unqualified or wrongly qualified
/// type name that the recompile silently binds to a same-named type from
/// another namespace. Casts are the silent failure mode - a wrong assignment
/// type breaks the build, but a wrong cast target compiles fine and only
/// throws (or filters wrongly, for isinst) at runtime. The known instance:
/// EventHelper's cancellable-handler casts came out as System.Func where the
/// vanilla IL casts to Vintagestory.API.Common.Func, so every handler of
/// BeforeActiveSlotChanged-style events failed with InvalidCastException.
///
/// Only divergences where both sides cast to the SAME short type name from
/// DIFFERENT full names are reported: that is the misbinding signature.
/// Intentional Optimum patches that add or remove casts entirely do not trip
/// it.
/// </summary>
public static class CastComparer
{
    public static List<string> Compare(string vanillaPath, string compiledPath)
    {
        var readerParams = new ReaderParameters { AssemblyResolver = new DefaultAssemblyResolver() };
        using var vanilla = ModuleDefinition.ReadModule(vanillaPath, readerParams);
        using var compiled = ModuleDefinition.ReadModule(compiledPath, readerParams);

        var compiledTypes = AllTypes(compiled).ToDictionary(t => t.FullName, t => t);
        var divergences = new List<string>();

        foreach (var vanillaType in AllTypes(vanilla))
        {
            if (!compiledTypes.TryGetValue(vanillaType.FullName, out var compiledType)) continue;

            foreach (var vanillaMethod in vanillaType.Methods)
            {
                var compiledMethod = compiledType.Methods.FirstOrDefault(m =>
                    m.Name == vanillaMethod.Name &&
                    m.Parameters.Count == vanillaMethod.Parameters.Count &&
                    m.Parameters.Select(p => p.ParameterType.Name)
                        .SequenceEqual(vanillaMethod.Parameters.Select(p => p.ParameterType.Name)));
                if (compiledMethod == null) continue;

                var vanillaCasts = CastTargets(vanillaMethod);
                var compiledCasts = CastTargets(compiledMethod);
                var vanillaOnly = vanillaCasts.Except(compiledCasts).ToList();
                var compiledOnly = compiledCasts.Except(vanillaCasts).ToList();

                foreach (var vanillaTarget in vanillaOnly)
                {
                    string shortName = ShortName(vanillaTarget);
                    var misbound = compiledOnly.FirstOrDefault(c => ShortName(c) == shortName);
                    if (misbound != null)
                    {
                        divergences.Add(
                            $"{vanillaType.FullName}::{vanillaMethod.Name}: vanilla casts to {vanillaTarget} " +
                            $"but the compiled build casts to {misbound}");
                    }
                }
            }
        }

        return divergences;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
            new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));
        return module.Types.SelectMany(Flatten);
    }

    private static List<string> CastTargets(MethodDefinition method)
    {
        if (!method.HasBody) return new List<string>();
        return method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Castclass || i.OpCode == OpCodes.Isinst)
            .Select(i => ((TypeReference)i.Operand).GetElementType().FullName)
            .OrderBy(x => x)
            .ToList();
    }

    private static string ShortName(string fullName)
    {
        return fullName.Substring(fullName.LastIndexOf('.') + 1);
    }
}
