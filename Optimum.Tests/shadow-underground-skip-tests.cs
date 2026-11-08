using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class ShadowUndergroundSkipTests
{
    [Fact]
    public void UndergroundHysteresisLogic_SuppressesBelowTwoAndResumesAboveTwo()
    {
        bool suppressed = false;

        void Update(int sunLight)
        {
            if (suppressed)
            {
                if (sunLight >= 3)
                {
                    suppressed = false;
                }
            }
            else if (sunLight <= 1)
            {
                suppressed = true;
            }
        }

        // Surface daylight: unsuppressed
        Update(32);
        Assert.False(suppressed);

        Update(15);
        Assert.False(suppressed);

        // Near cave threshold but not underground yet: stays unsuppressed
        Update(2);
        Assert.False(suppressed);

        // Enters cave: drops to 1, suppressed activates!
        Update(1);
        Assert.True(suppressed);

        // Deep cave: drops to 0, remains suppressed
        Update(0);
        Assert.True(suppressed);

        // Approaches entrance: rises to 2, stays suppressed (hysteresis prevents flicker)
        Update(2);
        Assert.True(suppressed);

        // Steps into daylight entrance: rises to 3, resumes rendering!
        Update(3);
        Assert.False(suppressed);

        // Steps back to 2: stays unsuppressed (hysteresis prevents flicker)
        Update(2);
        Assert.False(suppressed);
    }

    [Fact]
    public void PatchAndPatcherConfigurationIntegrity()
    {
        string patchPath = PatchReader.FindRepositoryFile(Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "SystemRenderShadowMap.cs.patch"));
        Assert.True(File.Exists(patchPath), "Patch file must exist");

        string patchContent = File.ReadAllText(patchPath);
        Assert.Contains("optimumUndergroundShadowSuppressed", patchContent);
        Assert.Contains("optimumShadowBlockPosScratch", patchContent);
        Assert.Contains("EnumLightLevelType.OnlySunLight", patchContent);
        Assert.Contains("sunLight >= 3", patchContent);
        Assert.Contains("sunLight <= 1", patchContent);
        Assert.Contains("game.shUniforms.DropShadowIntensity = (game.AmbientManager.DropShadowIntensity = 0f);", patchContent);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("patches", "cecil-owned.list")));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderShadowMap.cs.patch", cecilList);

        string programCs = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("Optimum.Patcher", "Program.cs")));
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderShadowMap\", \"OnRenderBefore\", 1", programCs);
    }
}
