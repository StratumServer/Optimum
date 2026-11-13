using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

// Issue #570: GuiElementHoverText.SetNewText recomposed unconditionally, which re-parses the text, rebuilds a
// blurred background and uploads a texture. The hotbar gear tooltip calls it every frame with the same text.
public sealed class GuiHoverTextSameTextTests
{
    private const string PatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/Text/GuiElementHoverText.cs.patch";

    [Fact]
    public void Patch_SkipsUnchangedText_OnlyWithATextureComposedAtTheCurrentScale()
    {
        string patched = PatchReader.ReadPatch(PatchPath);
        Assert.Contains("float composedGuiScale;", patched);
        Assert.Contains("composedGuiScale = RuntimeEnv.GUIScale;", patched);
        Assert.Contains("if (text == this.text && hoverTexture.TextureId != 0 && composedGuiScale == RuntimeEnv.GUIScale) return;", patched);

        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        Assert.Contains("PatchGuiHoverTextSameText(", patcher);
        Assert.Contains("if (guiHoverTextPatches != 2)", patcher);
    }

    [Fact]
    public void PatchGuiHoverTextSameText_GuardsSetNewText_AndStampsTheScaleInRecompose()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        Assert.Equal(2, ApiPatcher.PatchGuiHoverTextSameText(vanilla.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        var hover = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementHoverText")!;
        Assert.Contains(hover.Fields, f => f.Name == "composedGuiScale" && f.FieldType.MetadataType == MetadataType.Single && f.IsPrivate);

        // Recompose starts by recording the scale it composes at.
        var recompose = hover.Methods.Single(m => m.Name == "Recompose" && m.Parameters.Count == 0);
        var recomposeBody = recompose.Body.Instructions.ToList();
        Assert.Equal(OpCodes.Ldarg_0, recomposeBody[0].OpCode);
        Assert.Equal("GUIScale", ((FieldReference)recomposeBody[1].Operand).Name);
        Assert.Equal(OpCodes.Stfld, recomposeBody[2].OpCode);
        Assert.Equal("composedGuiScale", ((FieldReference)recomposeBody[2].Operand).Name);

        // SetNewText returns early only when text, texture id and scale all match; every other path runs the original body.
        var setNewText = hover.Methods.Single(m => m.Name == "SetNewText" && m.Parameters.Count == 1);
        var body = setNewText.Body.Instructions.ToList();
        int equality = body.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "op_Equality");
        int earlyReturn = body.FindIndex(i => i.OpCode == OpCodes.Ret);
        int originalStore = body.FindIndex(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "text");
        Assert.True(equality >= 0 && earlyReturn > equality, "the guard compares the text before returning");
        Assert.True(originalStore > earlyReturn, "the original assignment still follows the guard");
        Instruction originalFirst = body[earlyReturn + 1];
        Assert.Equal(OpCodes.Ldarg_0, originalFirst.OpCode);

        var branches = body.Take(earlyReturn).Where(i => i.Operand is Instruction).ToList();
        Assert.Equal(3, branches.Count);
        Assert.All(branches, i => Assert.Same(originalFirst, i.Operand));
        Assert.Contains(body.Take(earlyReturn), i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "TextureId");
        Assert.Contains(body.Take(earlyReturn), i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "composedGuiScale");
        Assert.Contains(body.Take(earlyReturn), i => i.OpCode == OpCodes.Ldsfld && ((FieldReference)i.Operand).Name == "GUIScale");
        Assert.Equal(OpCodes.Bne_Un, branches.Last().OpCode);
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
