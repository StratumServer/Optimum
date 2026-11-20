using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>A fact that is skipped, and reported as skipped, when this account cannot create symbolic links.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-symlink-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "This account cannot create symbolic links.";
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class VanillaArchivePinsTests
{
    private const string Version = "1.22.7";
    private static readonly byte[] OfficialApi = [1, 2, 3];
    private const string ArchiveSha256 = "ab12";

    [Fact]
    public void EveryClientArchiveOfTheBuiltAgainstVersionIsPinned()
    {
        string repoRoot = RepoRoot();
        string version = ForksVersion(repoRoot);
        VanillaArchivePins pins = VanillaArchivePins.ForRepo(repoRoot);
        string[] archives =
        [
            $"vs_install_win-x64_{version}.exe",
            $"vs_client_linux-x64_{version}.tar.gz",
            $"vs_client_osx-x64_{version}.tar.gz",
            $"vs_client_osx-arm64_{version}.tar.gz",
        ];

        foreach (string archive in archives)
        {
            VanillaArchivePin? pin = pins.FindArchive(archive);

            Assert.True(pin is not null, $"{archive} is not pinned; add its size and SHA-256 to VanillaArchivePins.");
            Assert.Equal(64, pin!.Sha256.Length);
            Assert.True(pin.Bytes > 100_000_000);
        }

        IReadOnlyDictionary<string, string> files = pins.FindExtractedFiles(version);

        Assert.Equal(["VintagestoryAPI.dll", "VintagestoryLib.vanilla.dll"], files.Keys.Order().ToArray());
        Assert.All(files.Values, sha => Assert.Equal(64, sha.Length));
    }

    [Fact]
    public void ASourceTreeWithoutPinsOrWithoutForksJsonHasNoPins()
    {
        string repo = NewRepo();
        try
        {
            Assert.Null(VanillaArchivePins.ForRepo(repo).FindArchive("vs_install_win-x64_1.22.7.exe"));

            File.WriteAllText(Path.Combine(repo, "forks.json"), "{\"vintageStoryVersion\":\"1.22.7\"}");
            Assert.Null(VanillaArchivePins.ForRepo(repo).FindArchive("vs_install_win-x64_1.22.7.exe"));

            File.WriteAllText(Path.Combine(repo, "forks.json"), "not json");
            Assert.Null(VanillaArchivePins.ForRepo(repo).FindArchive("vs_install_win-x64_1.22.7.exe"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"archives\":{\"a.exe\":{\"bytes\":5}}}}}")] // no sha256
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"archives\":{\"a.exe\":{\"sha256\":\"ab\"}}}}}")] // no bytes
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"archives\":{\"a.exe\":{\"bytes\":5,\"sha256\":\"too short\"}}}}}")]
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"archives\":{\"a.exe\":{\"bytes\":99999999999999999999,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}}}}}")]
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"extractedFiles\":{\"VintagestoryAPI.dll\":null}}}}")]
    [InlineData("{\"vintageStoryPins\":{\"1.0.0\":{\"archives\":[]}}}")]
    public void AMalformedPinBlockIsAnErrorAndNeverSilentlyNoPins(string forksJson)
    {
        Assert.Throws<InvalidDataException>(() => VanillaArchivePins.FromJson(forksJson));

        string repo = NewRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "forks.json"), forksJson);
            Assert.Throws<InvalidDataException>(() => VanillaArchivePins.ForRepo(repo));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void AnUnpinnedVersionHasNoPinsSoDevelopmentBuildsKeepWorking()
    {
        VanillaArchivePins pins = VanillaArchivePins.ForRepo(RepoRoot());

        Assert.Null(pins.FindArchive("vs_install_win-x64_1.99.0.exe"));
        Assert.Empty(pins.FindExtractedFiles("1.99.0"));
    }

    [Fact]
    public void ACacheWhoseKeyFilesAreNotTheOfficialBuildIsRemovedWithItsStamp()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, [9, 9, 9], stamp: true);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
            Assert.False(File.Exists(StampPath(repo)));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ACacheExtractedFromTheVerifiedArchiveWithOfficialKeyFilesIsKept()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, OfficialApi, stamp: true);

            Discard(repo, OsKind.Windows);

            Assert.True(File.Exists(Path.Combine(vanilla, "VintagestoryAPI.dll")));
            Assert.True(File.Exists(StampPath(repo)));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ACacheWithoutTheVerificationStampIsRemovedEvenWhenItsKeyFilesMatch()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, OfficialApi, stamp: false);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"archive\":\"vs_install_win-x64_1.22.7.exe\",\"sha256\":\"ff00\"}")]
    [InlineData("{\"archive\":\"vs_client_linux-x64_1.22.7.tar.gz\",\"sha256\":\"ab12\"}")]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void ACacheWhoseStampNamesAnotherArchiveHasAnotherShaOrIsUnreadableIsRemoved(string stamp)
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, OfficialApi, stamp: true);
            File.WriteAllText(StampPath(repo), stamp);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ACacheMissingAKeyFileIsRemoved()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, OfficialApi, stamp: true);
            File.Delete(Path.Combine(vanilla, "VintagestoryAPI.dll"));

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void AnEmptyOrAbsentCacheIsLeftAlone()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = Path.Combine(repo, ".vanilla", "win-x64", "vintagestory");
            Directory.CreateDirectory(vanilla);

            Discard(repo, OsKind.Windows);

            Assert.True(Directory.Exists(vanilla));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ACacheIsLeftAloneWhenTheVersionIsNotPinned()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, [9, 9, 9], stamp: false);

            Discard(repo, OsKind.Windows, VanillaArchivePins.None);

            Assert.True(Directory.Exists(vanilla));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void AMacOsCacheIsRemovedAsWholeTreesSoTheScriptsCanExtractAgain()
    {
        // bootstrap.sh renames the archive's single root folder to "vintagestory" only when that folder is
        // the only one in .vanilla/win-x64, so a leftover runtime-donors folder must go with the cache.
        string repo = NewRepo();
        try
        {
            WriteVanillaCache(repo, [9, 9, 9], stamp: true, os: OsKind.MacOs);
            string donors = Path.Combine(repo, ".vanilla", "win-x64", "runtime-donors");
            Directory.CreateDirectory(donors);
            File.WriteAllText(Path.Combine(donors, "VSEssentials.dll"), "x");
            string app = Path.Combine(repo, ".vanilla", "osx-x64", "Vintage Story.app");
            Directory.CreateDirectory(app);
            File.WriteAllBytes(Path.Combine(app, "VintagestoryAPI.dll"), OfficialApi);
            Directory.CreateDirectory(Path.Combine(repo, "build", "snapshot"));
            File.WriteAllText(Path.Combine(repo, ".bootstrap-complete"), "x");

            Discard(repo, OsKind.MacOs);

            Assert.False(Directory.Exists(Path.Combine(repo, ".vanilla", "win-x64")));
            Assert.False(Directory.Exists(Path.Combine(repo, ".vanilla", "osx-x64")));
            Assert.False(Directory.Exists(Path.Combine(repo, "build", "snapshot")));
            Assert.False(File.Exists(Path.Combine(repo, ".bootstrap-complete")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void TheLinuxPackageCacheIsCheckedAndRemovedWithTheBootstrapCache()
    {
        string repo = NewRepo();
        try
        {
            string bootstrap = WriteVanillaCache(repo, OfficialApi, stamp: true, os: OsKind.Linux);
            string package = Path.Combine(repo, ".vanilla", "linux-x64", "vintagestory");
            Directory.CreateDirectory(package);
            File.WriteAllBytes(Path.Combine(package, "VintagestoryAPI.dll"), [9, 9, 9]);

            Discard(repo, OsKind.Linux);

            Assert.False(Directory.Exists(bootstrap));
            Assert.False(Directory.Exists(Path.Combine(repo, ".vanilla", "linux-x64")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ALeftoverLegacyCacheIsRemoved()
    {
        string repo = NewRepo();
        try
        {
            WriteVanillaCache(repo, OfficialApi, stamp: true);
            string legacy = Path.Combine(repo, ".vanilla", "vintagestory");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "Vintagestory.exe"), "x");

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(legacy));
            Assert.False(Directory.Exists(Path.Combine(repo, ".vanilla", "win-x64")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ACacheWithReadOnlyFilesIsStillRemoved()
    {
        string repo = NewRepo();
        try
        {
            string vanilla = WriteVanillaCache(repo, [9, 9, 9], stamp: true);
            string readOnly = Path.Combine(vanilla, "assets", "locked.json");
            Directory.CreateDirectory(Path.GetDirectoryName(readOnly)!);
            File.WriteAllText(readOnly, "{}");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
        }
        finally
        {
            ClearReadOnlyUnder(repo);
            Directory.Delete(repo, recursive: true);
        }
    }

    [SymlinkFact]
    public void ACacheThatIsALinkIsUnlinkedWithoutTouchingWhatItPointsAt()
    {
        string repo = NewRepo();
        string outside = NewRepo();
        try
        {
            // The target holds the official bytes, so only the link check can remove this cache.
            File.WriteAllBytes(Path.Combine(outside, "VintagestoryAPI.dll"), OfficialApi);
            string vanilla = Path.Combine(repo, ".vanilla", "win-x64", "vintagestory");
            Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
            Directory.CreateSymbolicLink(vanilla, outside);
            WriteStamp(repo, OsKind.Windows);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(vanilla));
            Assert.True(File.Exists(Path.Combine(outside, "VintagestoryAPI.dll")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [SymlinkFact]
    public void ALinkAboveTheCacheIsUnlinkedAndNeverFollowed()
    {
        string repo = NewRepo();
        string outside = NewRepo();
        try
        {
            string cache = Path.Combine(outside, "win-x64", "vintagestory");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "VintagestoryAPI.dll"), OfficialApi);
            Directory.CreateSymbolicLink(Path.Combine(repo, ".vanilla"), outside);

            Discard(repo, OsKind.Windows);

            Assert.False(Directory.Exists(Path.Combine(repo, ".vanilla")));
            Assert.True(File.Exists(Path.Combine(cache, "VintagestoryAPI.dll")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [SymlinkFact]
    public void ALinkedCacheIsRemovedEvenForAVersionWithoutPins()
    {
        string repo = NewRepo();
        string outside = NewRepo();
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "VintagestoryAPI.dll"), OfficialApi);
            string vanilla = Path.Combine(repo, ".vanilla", "win-x64", "vintagestory");
            Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
            Directory.CreateSymbolicLink(vanilla, outside);

            Discard(repo, OsKind.Windows, VanillaArchivePins.None);

            Assert.False(Directory.Exists(vanilla));
            Assert.True(File.Exists(Path.Combine(outside, "VintagestoryAPI.dll")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void Discard(string repo, OsKind os, IVanillaArchivePins? pins = null)
    {
        var driver = new ScriptBuildDriver(new FakeSystemProbe { Os = os }, pins: pins ?? new FixedPins());
        driver.DiscardUnverifiedExtractionCaches(Request(repo), new NullBuildObserver());
    }

    private static BuildRequest Request(string repo) => new(repo, Path.Combine(repo, "out"), Version: Version);

    private static string WriteVanillaCache(string repo, byte[] apiContent, bool stamp, OsKind os = OsKind.Windows)
    {
        string vanilla = Path.Combine(repo, ".vanilla", "win-x64", "vintagestory");
        Directory.CreateDirectory(vanilla);
        File.WriteAllBytes(Path.Combine(vanilla, "VintagestoryAPI.dll"), apiContent);
        File.WriteAllText(Path.Combine(vanilla, "Vintagestory.exe"), "x");
        if (stamp)
            WriteStamp(repo, os);
        return vanilla;
    }

    private static void WriteStamp(string repo, OsKind os)
    {
        string archive = ClientArchiveAcquisition.ArchiveFileName(Version, os, Architecture.X64);
        Directory.CreateDirectory(Path.GetDirectoryName(StampPath(repo))!);
        File.WriteAllText(StampPath(repo), JsonSerializer.Serialize(new { archive, sha256 = ArchiveSha256 }));
    }

    private static string StampPath(string repo) => Path.Combine(repo, ".vanilla", "client-archive-verified.json");

    private static void ClearReadOnlyUnder(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    private static string NewRepo()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-pins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ForksVersion(string repoRoot)
    {
        using JsonDocument forks = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "forks.json")));
        return forks.RootElement.GetProperty("vintageStoryVersion").GetString()!;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "forks.json")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("forks.json was not found above the test assembly.");
    }

    /// <summary>Every archive is pinned to <see cref="ArchiveSha256"/> and the extracted API dll to <see cref="OfficialApi"/>.</summary>
    private sealed class FixedPins : IVanillaArchivePins
    {
        public VanillaArchivePin? FindArchive(string archiveFileName) => new(100, ArchiveSha256);

        public IReadOnlyDictionary<string, string> FindExtractedFiles(string version) =>
            new Dictionary<string, string> { ["VintagestoryAPI.dll"] = Convert.ToHexString(SHA256.HashData(OfficialApi)) };
    }
}
