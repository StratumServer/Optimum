using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

// Issue #569: GuiElementStatbar.recomposeOverlays ran on every value change, several times per second while diving. It
// rasterized the hover text on the main thread, although the text is only visible while the mouse is over the bar, and
// queued the flash overlay again, although that overlay only depends on the bounds, the GUI scale and the color.
public sealed class GuiStatbarHoverTextTests
{
    private const string PatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/GuiElementStatbar.cs.patch";

    [Fact]
    public void Patch_KeepsTheString_AndRasterizesItOnTheHoveredFrame()
    {
        string patched = PatchReader.ReadPatch(PatchPath);
        Assert.Contains("pendingValueText = onGetStatbarValue();", patched);
        Assert.Contains("string pendingValueText;", patched);
        Assert.Contains("ComposeFlashOverlay();", patched);
        Assert.Contains("void buildPendingValueText()", patched);
        Assert.Contains("if (pendingValueText == null) return;", patched);
        Assert.Contains("pendingValueText = null;", patched);

        int hover = patched.IndexOf("if (ShowValueOnHover && Bounds.PointInside(", StringComparison.Ordinal);
        int build = patched.IndexOf("buildPendingValueText();", hover, StringComparison.Ordinal);
        int render = patched.IndexOf("api.Render.RenderTexture(valueTexture.TextureId", hover, StringComparison.Ordinal);
        Assert.True(hover >= 0 && build > hover && render > build, "the text must be built before the hover texture is drawn");

        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        Assert.Contains("PatchGuiStatbarOnDemand(", patcher);
        Assert.Contains("if (guiStatbarPatches != 5)", patcher);
    }

    [Fact]
    public void PatchGuiStatbarOnDemand_MovesTheRasterizationToTheHoveredFrame()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        Assert.Equal(5, ApiPatcher.PatchGuiStatbarOnDemand(vanilla.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        var statbar = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementStatbar")!;
        Assert.Contains(statbar.Fields, f => f.Name == "pendingValueText" && f.IsPrivate && f.FieldType.MetadataType == MetadataType.String);

        // recomposeOverlays still calls the delegate once, stores the string, and no longer rasterizes.
        var recompose = statbar.Methods.Single(m => m.Name == "recomposeOverlays" && m.Parameters.Count == 0);
        var recomposeBody = recompose.Body.Instructions.ToList();
        Assert.DoesNotContain(recomposeBody, i => i.Operand is MethodReference r && r.Name == "GenOrUpdateTextTexture");
        Assert.Equal(1, recomposeBody.Count(i => i.Operand is MethodReference r && r.Name == "Invoke" && r.DeclaringType.Name == "StatbarValueDelegate"));
        Assert.Contains(recomposeBody, i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "pendingValueText");
        Assert.Equal(OpCodes.Ret, recomposeBody[^1].OpCode);
        Assert.All(recomposeBody.Where(i => i.Operand is Instruction), i => Assert.Same(recomposeBody[^1], i.Operand));

        // The new method does the original rasterization and clears the pending string.
        var build = statbar.Methods.Single(m => m.Name == "buildPendingValueText" && m.Parameters.Count == 0);
        var buildBody = build.Body.Instructions.ToList();
        int generate = buildBody.FindIndex(i => i.Operand is MethodReference r && r.Name == "GenOrUpdateTextTexture");
        int clear = buildBody.FindLastIndex(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "pendingValueText");
        Assert.True(generate > 0 && clear > generate, "the string is cleared after it is rasterized");
        Assert.Equal(OpCodes.Brfalse, buildBody[2].OpCode);
        Assert.Same(buildBody[^1], buildBody[2].Operand);
        Assert.Contains(buildBody, i => i.Operand is FieldReference f && f.Name == "valueFont");
        Assert.Contains(buildBody, i => i.OpCode == OpCodes.Ldflda && ((FieldReference)i.Operand).Name == "valueTexture");

        // RenderInteractiveElements builds the text right after the hover test and before it reads the texture size.
        var render = statbar.Methods.Single(m => m.Name == "RenderInteractiveElements" && m.Parameters.Count == 1);
        var renderBody = render.Body.Instructions.ToList();
        int pointInside = renderBody.FindIndex(i => i.Operand is MethodReference r && r.Name == "PointInside");
        int call = renderBody.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "buildPendingValueText");
        int height = renderBody.FindIndex(pointInside, i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "Height");
        Assert.True(pointInside >= 0 && call == pointInside + 3 && height > call, "the hover branch must build the text before reading the texture height");
        Assert.DoesNotContain(renderBody, i => i.OpCode.Code is Code.Br_S or Code.Brfalse_S or Code.Brtrue_S or Code.Blt_S or Code.Bge_S);

        // The flash overlay is composed once per ComposeElements, before the value overlay, and no longer by the recompose task.
        var composeElements = statbar.Methods.Single(m => m.Name == "ComposeElements" && m.Parameters.Count == 2);
        var composeBody = composeElements.Body.Instructions.ToList();
        int flash = composeBody.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "ComposeFlashOverlay");
        int valuesSet = composeBody.FindIndex(i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "valuesSet");
        Assert.True(flash >= 0 && valuesSet > flash, "the flash overlay must be composed unconditionally, before the valuesSet check");
        Assert.Equal(1, composeBody.Count(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "ComposeFlashOverlay"));
        var task = statbar.Methods.Single(m => m.Name.StartsWith("<recomposeOverlays>", StringComparison.Ordinal));
        var taskBody = task.Body.Instructions.ToList();
        Assert.Contains(taskBody, i => i.Operand is MethodReference r && r.Name == "ComposeValueOverlay");
        Assert.DoesNotContain(taskBody, i => i.Operand is MethodReference r && r.Name == "ComposeFlashOverlay");
    }

    [Fact]
    public void PatchGuiStatbarOnDemand_CannotBeAppliedTwice()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        ApiPatcher.PatchGuiStatbarOnDemand(vanilla.MainModule);
        Assert.Throws<InvalidOperationException>(() => ApiPatcher.PatchGuiStatbarOnDemand(vanilla.MainModule));
    }

    private static string? FindVanillaApi()
    {
        foreach (string relative in new[]
        {
            ".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll",
        })
        {
            try
            {
                return PatchReader.FindRepositoryFile(relative);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return null;
    }
}
