using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Another mod can replace SystemRenderEntities.OnBeforeRender (for example with a Harmony prefix
/// that skips the original body). Optimum's draw passes read a list that only that body fills, so
/// the passes must rebuild it from the vanilla IsRendered flags when the body did not run.
/// </summary>
public class EntityRenderListSkippedPreRenderTests
{
    private const string PatchPath = "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch";

    [Fact]
    public void Patch_RebuildsRenderedEntitiesFromIsRenderedFlagsWhenPreRenderDidNotRun()
    {
        string patch = PatchReader.ReadPatch(PatchPath);

        Assert.Contains("private bool optimumRenderedEntitiesBuilt;", patch);
        Assert.Contains("private void EnsureOptimumRenderedEntities()", patch);

        int ensure = patch.IndexOf("private void EnsureOptimumRenderedEntities()", StringComparison.Ordinal);
        string body = patch[ensure..];
        int end = body.IndexOf("private void OnBeforeRender(float dt)", StringComparison.Ordinal);
        Assert.True(end > 0);
        body = body[..end];

        Assert.Contains("if (optimumRenderedEntitiesBuilt)", body);
        Assert.Contains("optimumRenderedEntitiesBuilt = false;", body);
        Assert.Contains("OptimumDiagnostics.ResetEntityTesselationBudget();", body);
        Assert.Contains("renderedEntities.Clear();", body);
        Assert.Contains("entity.IsRendered", body);
        Assert.Contains("renderedEntities.Add(", body);
        Assert.Contains("ActivateOptimumLightBatch(0)", body);
    }

    [Fact]
    public void Patch_MarksListBuiltOnlyAtTheEndOfTheOptimumPreRenderBody()
    {
        string patch = PatchReader.ReadPatch(PatchPath);

        int preRender = patch.IndexOf("private void OnBeforeRender(float dt)", StringComparison.Ordinal);
        int opaque = patch.IndexOf("public void OnRenderOpaque3D(float deltaTime)", StringComparison.Ordinal);
        Assert.True(preRender >= 0 && opaque > preRender);

        string preRenderBody = patch[preRender..opaque];
        int setIndex = preRenderBody.IndexOf("optimumRenderedEntitiesBuilt = true;", StringComparison.Ordinal);
        int previousCount = preRenderBody.IndexOf("optimumEntityLightPreviousSampleCount = optimumLightSampleCount;", StringComparison.Ordinal);
        Assert.True(setIndex > previousCount && previousCount > 0, "The built flag must be set after the list is filled.");
    }

    [Fact]
    public void Patch_EveryRenderedEntitiesPassGoesThroughOneFrameFreshnessCheck()
    {
        string patch = PatchReader.ReadPatch(PatchPath);

        int opaque = patch.IndexOf("public void OnRenderOpaque3D(float deltaTime)", StringComparison.Ordinal);
        Assert.True(opaque >= 0);
        string opaqueHead = patch[opaque..Math.Min(patch.Length, opaque + 400)];
        Assert.Contains("EnsureOptimumRenderedEntities();", opaqueHead);

        // The check is a once per frame gate in the first consumer. Later passes reuse the same list.
        Assert.Equal(1, CountOccurrences(patch, "EnsureOptimumRenderedEntities();"));
    }

    [Fact]
    public void Patch_FallbackIsGenericAndDoesNotNameOptiTime()
    {
        string patch = PatchReader.ReadPatch(PatchPath);
        Assert.DoesNotContain("OptiTime", patch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patcher_InjectsTheFreshnessFlagAndHelperIntoSystemRenderEntities()
    {
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        int start = program.IndexOf("[\"Vintagestory.Client.NoObf.SystemRenderEntities\"] = new()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string block = program[start..Math.Min(program.Length, start + 1200)];
        Assert.Contains("\"optimumRenderedEntitiesBuilt\"", block);
        Assert.Contains("\"optimumRenderedEntitiesFallbackLogged\"", block);
        Assert.Contains("\"EnsureOptimumRenderedEntities\"", block);
    }

    [Fact]
    public void VintagestoryLibPatched_OpaquePassCallsFreshnessCheckBeforeDrawingAndPreRenderMarksTheList()
    {
        string patchedDll = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"),
            "..", "..", "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"));
        Assert.True(File.Exists(patchedDll), $"Missing {patchedDll}; run make patch-il first.");

        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(patchedDll);
        TypeDefinition system = asm.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderEntities");
        Assert.NotNull(system);

        MethodDefinition opaque = system.Methods.Single(m => m.Name == "OnRenderOpaque3D");
        int ensureIndex = IndexOfCall(opaque, "EnsureOptimumRenderedEntities");
        int drawIndex = IndexOfCall(opaque, "DoRender3DOpaque");
        Assert.True(ensureIndex >= 0, "OnRenderOpaque3D must call EnsureOptimumRenderedEntities.");
        Assert.True(drawIndex > ensureIndex, "The freshness check must run before the first draw call.");

        MethodDefinition preRender = system.Methods.Single(m => m.Name == "OnBeforeRender");
        Assert.Contains(preRender.Body.Instructions,
            ins => ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference fr && fr.Name == "optimumRenderedEntitiesBuilt");

        MethodDefinition ensure = system.Methods.Single(m => m.Name == "EnsureOptimumRenderedEntities");
        Assert.Contains(ensure.Body.Instructions,
            ins => ins.Operand is FieldReference fr && fr.Name == "IsRendered");
        Assert.Contains(ensure.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "ActivateOptimumLightBatch");
        Assert.Contains(ensure.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "ResetEntityTesselationBudget");
    }

    private static int IndexOfCall(MethodDefinition method, string name)
    {
        for (int i = 0; i < method.Body.Instructions.Count; i++)
        {
            if (method.Body.Instructions[i].Operand is MethodReference mr && mr.Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
