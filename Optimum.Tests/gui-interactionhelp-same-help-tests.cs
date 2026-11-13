using System;
using System.IO;
using System.Linq;
using Xunit;
#if !NO_DONOR
using Mono.Cecil;
using Mono.Cecil.Cil;
#endif

namespace Optimum.Tests;

// Issue #571: the interaction help is re-evaluated every ~21 ticks while an entity is under the crosshair, and each
// evaluation rasterized every line and uploaded a texture even though nothing changed. DrawWorldInteractionUtil now
// composes again only when what drawHelp reads differs from what is shown.
public sealed class GuiInteractionHelpSameHelpTests
{
    private const string PatchPath = "patches/VintagestoryLib/Vintagestory.Client.NoObf/DrawWorldInteractionUtil.cs.patch";

    [Fact]
    public void Patch_ComparesWhatDrawHelpReads_BeforeTouchingTheComposer()
    {
        string patched = PatchReader.ReadPatch(PatchPath);
        int compare = patched.IndexOf("sameAsShown())", StringComparison.Ordinal);
        int clear = patched.IndexOf("composer.Clear(elementBounds);", StringComparison.Ordinal);
        Assert.True(compare >= 0 && clear > compare, "the comparison must run before the composer is cleared");

        Assert.Contains("parts.Add(Lang.CurrentLocale);", patched);
        Assert.Contains("BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale)", patched);
        Assert.Contains("describeHotKey(\"primarymouse\");", patched);
        Assert.Contains("describeHotKey(\"secondarymouse\");", patched);
        Assert.Contains("capi.Input.GetHotKeyByCode(code)?.CurrentMapping", patched);
        Assert.Contains("composer != null && composer.Composed && Composers[composerKeyCode] == composer", patched);
        Assert.Contains("shownParts.Clear();", patched);
    }

    [Fact]
    public void Patcher_InjectsTheStateAndTransplantsTheThreeMethods()
    {
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("[\"Vintagestory.Client.NoObf.DrawWorldInteractionUtil\"] = new()", program);
        foreach (string member in new[] { "lines", "lineStacks", "parts", "shownParts", "values", "shownValues", "describeLines", "describeHotKey", "sameAsShown" })
        {
            Assert.Contains($"\"{member}\",", program);
        }

        Assert.Contains("new(\"Vintagestory.Client.NoObf.DrawWorldInteractionUtil\", \".ctor\", 3),", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.DrawWorldInteractionUtil\", \"ComposeBlockWorldInteractionHelp\", 1),", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.DrawWorldInteractionUtil\", \"Dispose\", 0),", program);
        Assert.Contains(PatchPath, File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list")));
    }

#if !NO_DONOR
    [Fact]
    public void Donor_ComposesOnlyAfterTheComparison_AndKeepsBothLambdas()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "VintagestoryLib.dll");
        if (!File.Exists(path))
        {
            path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        }

        Assert.True(File.Exists(path), "the compiled donor VintagestoryLib.dll is needed; run make build");
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path);
        TypeDefinition type = assembly.MainModule.GetType("Vintagestory.Client.NoObf.DrawWorldInteractionUtil");
        Assert.NotNull(type);

        MethodDefinition compose = type.Methods.Single(m => m.Name == "ComposeBlockWorldInteractionHelp");
        var body = compose.Body.Instructions.ToList();
        int compare = body.FindIndex(i => i.Operand is MethodReference r && r.Name == "sameAsShown");
        int clear = body.FindIndex(i => i.Operand is MethodReference r && r.Name == "Clear" && r.DeclaringType.Name == "GuiComposer");
        int create = body.FindIndex(i => i.Operand is MethodReference r && r.Name == "CreateCompo");
        int composeCall = body.FindIndex(i => i.Operand is MethodReference r && r.Name == "Compose" && r.DeclaringType.Name == "GuiComposer");
        Assert.True(compare >= 0, "the comparison is missing");
        Assert.True(create > compare && clear > compare && composeCall > compare, "the composer must only be touched after the comparison");

        // The vanilla lambda bodies are kept: the custom render for the item icon and the static draw that calls drawHelp.
        var closures = type.NestedTypes.SelectMany(t => t.Methods).ToList();
        Assert.Contains(closures, m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "drawHelp"));
        Assert.Contains(closures, m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "RenderItemstackToGui"));
    }
#endif
}
