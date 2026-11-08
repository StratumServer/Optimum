using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumOptiTimeGuardCoverageTests
{
    [Fact]
    public void GuardMatchesDistributedOptiTimeFileForms()
    {
        string sourcePath = FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumOptiTimeGuard.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("Contains(\"optitime\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".zip\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".dll\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".cs\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains(".disabled-by-optimum", source);
    }

    [Fact]
    public void OptiTimeGuard_SourceEnforcesCrashRollbackAndLeaseManifest()
    {
        string sourcePath = FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumOptiTimeGuard.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("OptiTimeLeaseManifest", source);
        Assert.Contains("WriteLeaseManifest", source);
        Assert.Contains("RestoreFromLease", source);
        Assert.Contains("optitime-lease.json", source);
        Assert.Contains("File.Move(entry.DisabledPath, entry.OriginalPath)", source);
    }

#if !NO_DONOR
    [Fact]
    public void OptiTimeGuard_RecoversOrphanedMarkersAndLeasesAcrossCrashes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-optitime-test-" + Guid.NewGuid().ToString("N"));
        string modsDir = Path.Combine(tempDir, "Mods");
        string leaseFile = Path.Combine(tempDir, ".optimum", "optitime-lease.json");
        Directory.CreateDirectory(modsDir);

        try
        {
            OptimumOptiTimeGuard.SetTestOverrides(modsDir, leaseFile);

            // 1. Create a dummy OptiTime zip
            string modZip = Path.Combine(modsDir, "OptiTime-v1.0.0.zip");
            File.WriteAllText(modZip, "dummy zip content");

            // 2. Disable OptiTime
            OptimumOptiTimeGuard.DisableOptiTimeIfPresent(null);

            string disabledZip = modZip + ".disabled-by-optimum";
            Assert.False(File.Exists(modZip));
            Assert.True(File.Exists(disabledZip));
            Assert.True(File.Exists(leaseFile));

            // 3. Simulate process crash by not calling OnProcessExit and starting fresh session
            // Next startup calls RestoreAll on entry in DisableOptiTimeIfPresent
            OptimumOptiTimeGuard.RestoreAll(null);

            Assert.True(File.Exists(modZip));
            Assert.False(File.Exists(disabledZip));
            Assert.False(File.Exists(leaseFile));

            // 4. Test recovery of orphaned marker when lease file is absent (e.g. hard disk crash)
            File.Move(modZip, disabledZip);
            Assert.False(File.Exists(leaseFile));

            OptimumOptiTimeGuard.RestoreAll(null);
            Assert.True(File.Exists(modZip));
            Assert.False(File.Exists(disabledZip));
        }
        finally
        {
            OptimumOptiTimeGuard.SetTestOverrides(null, null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
#endif

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
