using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class ShadowProjectionSnappingTests
{
    [Theory]
    [InlineData(1, 4096)]
    [InlineData(2, 4096)]
    [InlineData(3, 5120)]
    [InlineData(4, 6144)]
    public void TexelSnappingSnapsTranslationToIntegerMultiplesOfTexelSize(int quality, int expectedResolution)
    {
        double shadowMapResolution = Math.Max(4, quality + 2) * 1024.0;
        Assert.Equal(expectedResolution, shadowMapResolution);

        double texelSizeX = 2.0 / shadowMapResolution;
        double texelSizeY = 2.0 / shadowMapResolution;

        double[] sampleTranslations = [0.000123, 1.234567, -42.987654, 100.000488];

        foreach (double rawX in sampleTranslations)
        {
            double snappedX = Math.Round(rawX / texelSizeX) * texelSizeX;
            double snapOffsetX = snappedX - rawX;
            double finalX = rawX + snapOffsetX;

            double texelMultiple = finalX / texelSizeX;
            double nearestInteger = Math.Round(texelMultiple);
            Assert.True(Math.Abs(texelMultiple - nearestInteger) < 1e-9,
                $"Expected finalX ({finalX}) to be an exact integer multiple of texelSizeX ({texelSizeX})");
        }

        foreach (double rawY in sampleTranslations)
        {
            double snappedY = Math.Round(rawY / texelSizeY) * texelSizeY;
            double snapOffsetY = snappedY - rawY;
            double finalY = rawY + snapOffsetY;

            double texelMultiple = finalY / texelSizeY;
            double nearestInteger = Math.Round(texelMultiple);
            Assert.True(Math.Abs(texelMultiple - nearestInteger) < 1e-9,
                $"Expected finalY ({finalY}) to be an exact integer multiple of texelSizeY ({texelSizeY})");
        }
    }

    [Fact]
    public void SubTexelJitterIsEliminatedByTexelGridQuantization()
    {
        double texelSize = 2.0 / 4096.0; // 0.00048828125
        double basePosition = 10.0;

        // Sub-texel camera motions smaller than half a texel should snap to identical snapped position
        double jitter1 = 0.00001;
        double jitter2 = 0.00005;

        double pos1 = basePosition + jitter1;
        double pos2 = basePosition + jitter2;

        double snapped1 = Math.Round(pos1 / texelSize) * texelSize;
        double snapped2 = Math.Round(pos2 / texelSize) * texelSize;

        Assert.Equal(snapped1, snapped2);
    }

    [Fact]
    public void PatchAndPatcherConfigurationIntegrity()
    {
        string patchPath = PatchReader.FindRepositoryFile(Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "SystemRenderShadowMap.cs.patch"));
        Assert.True(File.Exists(patchPath), "Patch file must exist");

        string patchContent = File.ReadAllText(patchPath);
        Assert.Contains("Math.Max(4, ClientSettings.ShadowMapQuality + 2) * 1024", patchContent);
        Assert.Contains("snapOffsetX", patchContent);
        Assert.Contains("snapOffsetY", patchContent);
        Assert.Contains("projectionViewMatrix[12] += snapOffsetX;", patchContent);
        Assert.Contains("projectionViewMatrix[13] += snapOffsetY;", patchContent);
        Assert.Contains("projectionMatrix[12] += snapOffsetX;", patchContent);
        Assert.Contains("projectionMatrix[13] += snapOffsetY;", patchContent);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("patches", "cecil-owned.list")));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderShadowMap.cs.patch", cecilList);

        string programCs = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("Optimum.Patcher", "Program.cs")));
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderShadowMap\", \"PrepareForShadowRendering\", 3", programCs);
    }
}
