using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class AllocationLookupBatchCoverageTests
{
    // A unified diff's hunk legitimately contains the removed old line
    // alongside the added new one, so "the old pattern is gone" can only
    // be asserted against the working-tree source, never the .patch file.
    // "The new pattern is present" is safe to check against both.

    [Fact]
    public void UpdateFreeMouseNoLongerUsesLinqInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        Assert.DoesNotContain("OpenedGuis.Where(", source);
        Assert.DoesNotContain("OpenedGuis.Any(", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch")]
    public void UpdateFreeMouseUsesASinglePassOverOpenedGuis(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));
        Assert.Contains("for (int i = 0; i < openedGuis.Count; i++)", source);
    }

    [Fact]
    public void UpdateFreeMouseIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"UpdateFreeMouse\", 0", programSource);
    }

    [Fact]
    public void OnBeforeRenderNoLongerAllocatesAVec3dForThePlayerPositionInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch");
        Assert.DoesNotContain("game.EntityPlayer.Pos.XYZ", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch")]
    public void OnBeforeRenderReadsPlayerXZDirectly(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("double plrX = game.EntityPlayer.Pos.X;", source);
        Assert.Contains("double plrZ = game.EntityPlayer.Pos.Z;", source);
    }

    [Fact]
    public void AmbientManagerScratchFieldsAreDeclaredBareNotEagerlyInitialized()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch");

        string[] fields =
        {
            "_updateAmbientFogColorScratch",
            "_updateAmbientAmbientColorScratch",
            "_waterColorHsvScratch",
            "_waterColorRgbScratch",
            "_daylightBlockPosScratch",
            "_colorGradingBlockPosScratch",
        };

        foreach (string field in fields)
        {
            Assert.DoesNotContain($"{field} = new", source);
        }
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    public void AmbientManagerScratchFieldsAreLazilyConstructed(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        string[] fields =
        {
            "_updateAmbientFogColorScratch",
            "_updateAmbientAmbientColorScratch",
            "_waterColorHsvScratch",
            "_waterColorRgbScratch",
            "_daylightBlockPosScratch",
            "_colorGradingBlockPosScratch",
        };

        foreach (string field in fields)
        {
            Assert.Contains($"{field} ??=", source);
        }
    }

    [Fact]
    public void SetWaterColorsNoLongerAllocatesInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch");
        Assert.DoesNotContain("WaterMurkColor = new Vec4f(", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    public void SetWaterColorsUsesNonAllocatingColorUtilOverloadsAndMutatesWaterMurkColorInPlace(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("ColorUtil.RgbToHsvInts(num2 & 0xFF, (num2 >> 8) & 0xFF, (num2 >> 16) & 0xFF, array);", source);
        Assert.Contains("ColorUtil.Hsv2RgbInts(array[0], array[1], array[2], array2);", source);
        Assert.Contains("WaterMurkColor.Set(", source);
    }

    [Fact]
    public void UpdateDaylightNoLongerUsesAsBlockPosInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch");
        Assert.DoesNotContain("game.player.Entity.Pos.AsBlockPos", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    public void UpdateDaylightReusesScratchBlockPosWithDimensionAwareSet(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));
        Assert.Contains("_daylightBlockPosScratch ??= new BlockPos()).Set(game.player.Entity.Pos)", source);
    }

    [Fact]
    public void UpdateColorGradingValuesNoLongerUsesAsBlockPosInSource()
    {
        // .XYZ bakes dimension into Y; the dimension-naive Set(Vec3d) would
        // silently corrupt dimension for anyone not in dimension 0, which is
        // why this needs SetAndCorrectDimension instead (asserted below).
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch");
        Assert.DoesNotContain("game.player.Entity.Pos.XYZ.AsBlockPos", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/AmbientManager.cs.patch")]
    public void UpdateColorGradingValuesUsesSetAndCorrectDimension(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));
        Assert.Contains("_colorGradingBlockPosScratch ??= new BlockPos()).SetAndCorrectDimension(game.player.Entity.Pos.XYZ)", source);
    }

    [Fact]
    public void AllFourEditedAmbientManagerMethodsAreRegisteredAsCecilTransplantTargets()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("\"Vintagestory.Client.NoObf.AmbientManager\", \"UpdateAmbient\", 1", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.AmbientManager\", \"setWaterColors\", 0", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.AmbientManager\", \"UpdateDaylight\", 1", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.AmbientManager\", \"updateColorGradingValues\", 1", programSource);
    }

    [Fact]
    public void OnRenderFrame3DNoLongerAllocatesInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs.patch");

        Assert.DoesNotContain("EntityPos.GetViewVector(game.mouseYaw, game.mousePitch);", source);
        Assert.DoesNotContain("game.EntityPlayer.Pos.XYZ.ToVec3f()", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs.patch")]
    public void OnRenderFrame3DReusesScratchVectorsInsteadOfAllocatingEveryFrame(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("_scratchViewVector ??= new Vec3f()", source);
        Assert.Contains("_scratchPlayerPos ??= new Vec3f()).Set(", source);
    }

    [Fact]
    public void OnRenderFrame3DIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderSkyColor\", \"OnRenderFrame3D\", 1", programSource);
    }

    [Fact]
    public void AnimatorBaseAlreadyUsesOrdinalIgnoreCaseComparer()
    {
        // Batch 6.1's AnimatorBase item was already shipped before this
        // task; assert it stays that way rather than silently regressing.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Common/Model/Animation/AnimatorBase.cs"));
        Assert.Contains("StringComparer.OrdinalIgnoreCase", source);
    }

    [Fact]
    public void AnimationManagerHasNoRemainingAnyLinqCalls()
    {
        // Shipped in Task 5.5; assert it stays fixed.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Common/Model/Animation/AnimationManager.cs"));
        Assert.DoesNotContain(".Any(", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
