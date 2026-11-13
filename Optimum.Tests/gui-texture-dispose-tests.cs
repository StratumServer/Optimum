using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

// Issue #568: GuiElementItemSlotGridBase cached one GL texture per slot background icon and color and
// never deleted them, and GuiElementCustomDraw never deleted the texture Redraw() generates.
public sealed class GuiTextureDisposeTests
{
    private const string GridPatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/Inventory/GuiElementItemSlotGridBase.cs.patch";
    private const string ConfigListPatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/GuiElementConfigList.cs.patch";
    private const string CustomDrawPatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Static/GuiElementCustomDraw.cs.patch";

    [Fact]
    public void Patches_DeleteTheCachedTexturesOnDispose_AndClearTheCache()
    {
        string grid = PatchReader.ReadPatch(GridPatchPath);
        Assert.Contains("foreach (int texId in slotTextureIdsByBgIconAndColor.Values) api.Render.GLDeleteTexture(texId);", grid);
        Assert.Contains("slotTextureIdsByBgIconAndColor.Clear();", grid);

        string draw = PatchReader.ReadPatch(CustomDrawPatchPath);
        Assert.Contains("public override void Dispose()", draw);
        Assert.Contains("base.Dispose();", draw);
        Assert.Contains("if (texId > 0) api.Render.GLDeleteTexture(texId);", draw);
        Assert.Contains("texId = 0;", draw);

        // Issue #581: the config list texture is deleted with the element and the id is reset, because a
        // disposed composer can be disposed or recomposed again.
        string configList = PatchReader.ReadPatch(ConfigListPatchPath);
        Assert.Contains("if (textureId > 0) api.Render.GLDeleteTexture(textureId);", configList);
        Assert.Contains("textureId = 0;", configList);
        Assert.Contains("hoverTexture.Dispose();", configList);
    }

    [Fact]
    public void Bridge_DeleteGuiTextures_IsRegisteredAndCountedByThePatcher()
    {
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        Assert.Contains("PatchGuiTextureDisposal(", patcher);
        Assert.Contains("if (guiTextureDisposals != 3)", patcher);
    }

    [Fact]
    public void PatchGuiTextureDisposal_AppendsTheDeletionToBothDisposeMethods()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumApiBridge).Assembly.Location);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")!;
        var deleteTextures = bridge.Methods.Single(m => m.Name == "DeleteGuiTextures" && m.Parameters.Count == 2);
        var deleteTexture = bridge.Methods.Single(m => m.Name == "DeleteGuiTexture" && m.Parameters.Count == 2);

        int patched = ApiPatcher.PatchGuiTextureDisposal(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(deleteTextures),
            vanilla.MainModule.ImportReference(deleteTexture));
        Assert.Equal(3, patched);
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        var grid = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementItemSlotGridBase")!;
        var gridDispose = grid.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        var gridInstructions = gridDispose.Body.Instructions;
        int bridgeCall = gridInstructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "DeleteGuiTextures");
        int baseCall = gridInstructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Dispose" &&
            ((MethodReference)i.Operand).DeclaringType.Name == "GuiElement");
        int firstReturn = gridInstructions.ToList().FindIndex(i => i.OpCode == OpCodes.Ret);
        Assert.True(baseCall >= 0 && bridgeCall > baseCall, "the deletion follows base.Dispose()");
        // The crossedOutTexture null check returns early, so no ret may come before the deletion.
        Assert.True(bridgeCall < firstReturn, "a ret before the deletion would skip it");

        var draw = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementCustomDraw")!;
        var drawDispose = draw.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        Assert.True(drawDispose.IsVirtual);
        Assert.False(drawDispose.IsNewSlot);
        Assert.Contains(drawDispose.Body.Instructions, i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Dispose");
        Assert.Contains(drawDispose.Body.Instructions, i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "DeleteGuiTexture");

        var configList = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementConfigList")!;
        var configListDispose = configList.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        var configListInstructions = configListDispose.Body.Instructions.ToList();
        int configBaseCall = configListInstructions.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Dispose"
            && ((MethodReference)i.Operand).DeclaringType.Name == "GuiElement");
        int configDelete = configListInstructions.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "DeleteGuiTexture");
        int configHoverDispose = configListInstructions.FindIndex(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "Dispose"
            && ((MethodReference)i.Operand).DeclaringType.Name == "LoadedTexture");
        Assert.True(configBaseCall >= 0 && configDelete > configBaseCall, "the deletion follows base.Dispose()");
        Assert.True(configHoverDispose > configDelete, "the hover texture is still disposed");
        Assert.Equal("textureId", ((FieldReference)configListInstructions[configDelete - 3].Operand).Name);
        Assert.Equal(OpCodes.Ldflda, configListInstructions[configDelete - 3].OpCode);
    }

    // Walks up from the test output to the repository root; AppContext.BaseDirectory is several levels below it.
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
