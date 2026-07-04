using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ParticleDistanceGateCoverageTests
{
    [Fact]
    public void SpawnParticlesGatesOnDistanceBeforeReviveLoop()
    {
        // The working tree source, not the patch diff: a unified diff's
        // limited context window doesn't reach ReviveOne(), which sits well
        // past the gate's own hunk and was never touched, so it never
        // appears in the .patch file at all. Ordering can only be checked
        // against the full method body.
        string source = File.ReadAllText(FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs"));

        int gateIndex = source.IndexOf("ParticleDistanceGateEnabled");
        int reviveIndex = source.IndexOf("ParticlesPool.ReviveOne()");
        Assert.True(gateIndex >= 0 && reviveIndex >= 0 && gateIndex < reviveIndex,
            "the distance gate must run before the revive loop, not after");
    }

    [Theory]
    [InlineData("build/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs.patch")]
    public void SpawnParticlesGateReferencesTheRightConfigAndDiagnostics(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumConfig.ParticleDistanceGateEnabled", source);
        Assert.Contains("ClientSettings.ViewDistance", source);
        Assert.Contains("OptimumDiagnostics.ParticleDistanceGate.Skip()", source);
        Assert.Contains("OptimumDiagnostics.ParticleDistanceGate.Hit()", source);
    }

    [Fact]
    public void SpawnParticlesIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ParticlePoolQuads\", \"SpawnParticles\"", programSource);
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
