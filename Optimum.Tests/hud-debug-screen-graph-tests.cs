using System;
using System.IO;
using System.Linq;
using Xunit;
#if !NO_DONOR
using Mono.Cecil;
using Mono.Cecil.Cil;
#endif

namespace Optimum.Tests;

// Issue #572: HudDebugScreen rebuilt and re-uploaded its 300-quad graph mesh every 2 s, even
// while the graph was hidden. The mesh only depends on the frame size, so it is now rebuilt
// when that size changes, at the draw entry.
public sealed class HudDebugScreenGraphTests
{
    private const string PatchPath = "patches/VintagestoryLib/Vintagestory.Client.NoObf/HudDebugScreen.cs.patch";

    [Fact]
    public void Patch_RemovesTheTwoSecondTimer_AndRebuildsOnFrameSizeChange()
    {
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(PatchPath));
        string patched = PatchReader.ReadPatch(PatchPath);

        Assert.Contains("-\t\tcapi.Event.RegisterGameTickListener(EveryOtherSecond, 2000);", patch);
        Assert.DoesNotContain("RegisterGameTickListener", patched);
        Assert.Contains("if (frameSlicesWidth != capi.Render.FrameWidth || frameSlicesHeight != capi.Render.FrameHeight)", patched);
        Assert.Contains("frameSlicesWidth = capi.Render.FrameWidth;", patched);
        Assert.Contains("frameSlicesHeight = capi.Render.FrameHeight;", patched);
    }

    [Fact]
    public void Patch_ChecksTheFrameSizeBeforeUpdatingTheMesh()
    {
        string patched = PatchReader.ReadPatch(PatchPath);
        int method = patched.IndexOf("private void updateFrameSlicesMesh()", StringComparison.Ordinal);
        int check = patched.IndexOf("GenFrameSlicesMesh();", method, StringComparison.Ordinal);
        int slices = patched.IndexOf("int num = capi.Render.FrameHeight - historyheight - 40;", method, StringComparison.Ordinal);

        Assert.True(method >= 0 && check > method && slices > check,
            "the frame size check must run before the per-frame mesh update");
    }

    [Fact]
    public void Patcher_InjectsTheCachedFrameSize_AndTransplantsTheThreeMethods()
    {
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("[\"Vintagestory.Client.NoObf.HudDebugScreen\"] = new()", program);
        Assert.Contains("\"frameSlicesWidth\",", program);
        Assert.Contains("\"frameSlicesHeight\",", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.HudDebugScreen\", \".ctor\", 1),", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.HudDebugScreen\", \"updateFrameSlicesMesh\", 0),", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.HudDebugScreen\", \"GenFrameSlicesMesh\", 0),", program);
        Assert.Contains(PatchPath, File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list")));
    }

#if !NO_DONOR
    [Fact]
    public void Donor_ConstructorNoLongerReferencesTheTimerHandler()
    {
        using AssemblyDefinition assembly = ReadDonor();
        TypeDefinition type = assembly.MainModule.GetType("Vintagestory.Client.NoObf.HudDebugScreen");
        Assert.NotNull(type);

        MethodDefinition ctor = type.Methods.Single(m => m.IsConstructor && !m.IsStatic);
        Assert.DoesNotContain(ctor.Body.Instructions, inst =>
            inst.Operand is MethodReference target && target.Name == "RegisterGameTickListener");
        Assert.DoesNotContain(type.Methods, m => m.Name == "EveryOtherSecond");
    }

    [Fact]
    public void Donor_UpdateFrameSlicesMesh_RebuildsOnlyThroughTheSizeCheck()
    {
        using AssemblyDefinition assembly = ReadDonor();
        TypeDefinition type = assembly.MainModule.GetType("Vintagestory.Client.NoObf.HudDebugScreen");
        Assert.NotNull(type);

        MethodDefinition update = type.Methods.Single(m => m.Name == "updateFrameSlicesMesh");
        Assert.Contains(update.Body.Instructions, inst =>
            inst.OpCode == OpCodes.Call && inst.Operand is MethodReference target && target.Name == "GenFrameSlicesMesh");

        int callers = type.Methods.Count(m => m.HasBody && m.Body.Instructions.Any(inst =>
            inst.Operand is MethodReference target && target.Name == "GenFrameSlicesMesh"));
        Assert.Equal(2, callers); // the constructor, for the first frame, and the size check
    }

    private static AssemblyDefinition ReadDonor()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "VintagestoryLib.dll");
        if (!File.Exists(path))
        {
            path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        }

        Assert.True(File.Exists(path), "the compiled donor VintagestoryLib.dll is needed; run make build");
        return AssemblyDefinition.ReadAssembly(path);
    }
#endif
}
