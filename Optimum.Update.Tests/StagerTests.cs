using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using Optimum.Update;
using Xunit;

namespace Optimum.Update.Tests;

public sealed class StagerTests
{
    private static Task<StageResult> Stage(ReleaseFixture release, string gameDir, HttpClient? client = null, string installed = "0.3.21", string game = "1.22.7",
        string target = "0.3.22", string? key = null, CancellationToken token = default, Action<StageProgress>? progress = null)
        => Task.FromResult(new OverlayStager(client ?? release.Client(), ReleaseFixture.Base, key ?? release.PublicKey)
            .Stage(gameDir, installed, game, release.Rid, target, progress, token));

    [Fact]
    public async Task StagesAVerifiedOverlayInOneFolder()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();

        StageResult result = await Stage(release, game);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(Path.Combine(game, ".optimum", "pending", "0.3.22-win-x64"), result.StagedDirectory);
        foreach ((string path, byte[] data) in release.Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(result.StagedDirectory!, path.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(result.StagedDirectory!, "staged.json")));
        Assert.Equal(new[] { "0.3.22-win-x64" }, Directory.GetDirectories(Path.Combine(game, ".optimum", "pending")).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task ReportsProgressForTheDownload()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        var phases = new List<string>();

        StageResult result = await Stage(release, dir.PatchedInstall(), progress: p => phases.Add(p.Phase));

        Assert.True(result.Ok, result.Message);
        Assert.Contains("manifest", phases);
        Assert.Contains("download", phases);
        Assert.Contains("verify", phases);
    }

    [Fact]
    public async Task StagingTwiceReusesTheVerifiedFolderWithoutDownloadingAgain()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        Assert.True((await Stage(release, game)).Ok);

        int archiveRequests = 0;
        HttpClient counting = release.Client(path => { if (path.EndsWith(".tar.gz")) archiveRequests++; return null; });
        StageResult again = await Stage(release, game, counting);

        Assert.True(again.Ok, again.Message);
        Assert.Equal("already staged", again.Message);
        Assert.Equal(0, archiveRequests);
    }

    [Fact]
    public async Task ARepairedStagedFolderIsReplacedWhenAFileWasChanged()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        StageResult first = await Stage(release, game);
        File.WriteAllText(Path.Combine(first.StagedDirectory!, "optimum.exe"), "tampered");

        StageResult again = await Stage(release, game);

        Assert.True(again.Ok, again.Message);
        Assert.NotEqual("already staged", again.Message);
        Assert.Equal("cli", File.ReadAllText(Path.Combine(again.StagedDirectory!, "optimum.exe")));
    }

    [Fact]
    public async Task RefusesAManifestWithoutASignature()
    {
        using var release = new ReleaseFixture { SignManifest = false }.Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task RefusesAManifestSignedWithAnotherKey()
    {
        using var release = new ReleaseFixture().Build();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall(), key: Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()));
        Assert.Equal(UpdateFailure.BadSignature, result.Failure);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".optimum", "pending", "0.3.22-win-x64")));
    }

    [Fact]
    public async Task RefusesAManifestChangedAfterItWasSigned()
    {
        using var release = new ReleaseFixture().Build();
        byte[] tampered = (byte[])release.ManifestBytes.Clone();
        tampered[^3] ^= 1;
        release.Served["v0.3.22/optimum-manifest-win-x64.json"] = tampered;
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadSignature, result.Failure);
    }

    [Fact]
    public async Task DoesNothingWithoutAConfiguredKey()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        int requests = 0;
        StageResult result = await Stage(release, dir.PatchedInstall(), release.Client(_ => { requests++; return null; }), key: "");
        Assert.Equal(UpdateFailure.NotConfigured, result.Failure);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task RefusesAnInstallThatIsNotPatchedInPlace()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        File.WriteAllText(Path.Combine(game, ".optimum", "standalone-install"), "");
        int requests = 0;
        StageResult result = await Stage(release, game, release.Client(_ => { requests++; return null; }));
        Assert.Equal(UpdateFailure.UnsupportedInstall, result.Failure);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("0.3.22", "0.3.22")]
    [InlineData("0.3.22", "0.4.0")]
    public async Task RefusesAReleaseThatIsNotNewer(string target, string installed)
    {
        using var release = new ReleaseFixture { Version = target }.Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall(), installed: installed, target: target);
        Assert.Equal(UpdateFailure.NotNewer, result.Failure);
    }

    [Fact]
    public async Task RefusesAGameVersionTheReleaseDoesNotSupport()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall(), game: "1.23.0");
        Assert.Equal(UpdateFailure.UnsupportedGameVersion, result.Failure);
    }

    [Fact]
    public async Task RefusesAManifestForAnotherVersion()
    {
        using var release = new ReleaseFixture { Version = "0.3.23" }.Build();
        release.Served["v0.3.22/optimum-manifest-win-x64.json"] = release.ManifestBytes;
        release.Served["v0.3.22/optimum-manifest-win-x64.json.sig"] = release.Served["v0.3.23/optimum-manifest-win-x64.json.sig"];
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadManifest, result.Failure);
    }

    [Fact]
    public async Task RefusesAnArchiveWhoseBytesDiffer()
    {
        using var release = new ReleaseFixture().Build();
        byte[] bad = (byte[])release.ArchiveBytes.Clone();
        bad[bad.Length / 2] ^= 0xFF;
        release.Served["v0.3.22/" + release.ArchiveName] = bad;
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task RefusesATruncatedDownload()
    {
        using var release = new ReleaseFixture().Build();
        release.Served["v0.3.22/" + release.ArchiveName] = release.ArchiveBytes.Take(release.ArchiveBytes.Length / 2).ToArray();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task RefusesAnArchiveLongerThanTheManifestSays()
    {
        using var release = new ReleaseFixture().Build();
        release.Served["v0.3.22/" + release.ArchiveName] = release.ArchiveBytes.Concat(new byte[64]).ToArray();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
    }

    [Fact]
    public async Task RefusesAFileThatDiffersFromItsManifestHash()
    {
        using var release = new ReleaseFixture();
        release.ArchiveFiles = new Dictionary<string, byte[]>(release.Files) { ["optimum.exe"] = System.Text.Encoding.ASCII.GetBytes("evil") };
        release.Build();
        using var dir = new TempDir();

        StageResult result = await Stage(release, dir.PatchedInstall());

        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        Assert.Contains("optimum.exe", result.Message);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task RefusesAnArchiveWithAFileTheManifestDoesNotList()
    {
        using var release = new ReleaseFixture();
        release.ExtraEntries.Add(($"{release.TopFolder}/Surprise.dll", TarEntryType.RegularFile, new byte[] { 1 }, null));
        release.Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task RefusesAnArchiveMissingAListedFile()
    {
        using var release = new ReleaseFixture();
        release.ArchiveFiles = release.Files.Where(f => f.Key != "optimum.exe").ToDictionary(f => f.Key, f => f.Value);
        release.Build();
        using var dir = new TempDir();

        StageResult result = await Stage(release, dir.PatchedInstall());

        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        Assert.Contains("missing", result.Message);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("Optimum-v0.3.22-win-x64-overlay/../../escape.txt")]
    [InlineData("/etc/passwd")]
    public async Task RefusesAnEntryThatEscapesTheOverlayFolder(string name)
    {
        using var release = new ReleaseFixture();
        release.ExtraEntries.Add((name, TarEntryType.RegularFile, new byte[] { 1 }, null));
        release.Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
        Assert.False(File.Exists(Path.Combine(dir.Path, "escape.txt")));
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task RefusesALinkInsideTheArchive()
    {
        using var release = new ReleaseFixture();
        release.ExtraEntries.Add(($"{release.TopFolder}/link", TarEntryType.SymbolicLink, Array.Empty<byte>(), "/etc"));
        release.Build();
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.Equal(UpdateFailure.BadArchive, result.Failure);
    }

    [Fact]
    public async Task ASuccessfulStageLeavesNoTemporaryFolder()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        Assert.True((await Stage(release, game)).Ok);
        Assert.Empty(Directory.GetDirectories(Path.Combine(game, ".optimum", "pending"), ".tmp-*"));
    }

    [Fact]
    public async Task ANetworkErrorIsReportedAndLeavesNothing()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        HttpClient failing = release.Client(path => path.EndsWith(".tar.gz") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);
        StageResult result = await Stage(release, dir.PatchedInstall(), failing);
        Assert.Equal(UpdateFailure.Network, result.Failure);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task ACancelledStageLeavesNothing()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        using var source = new CancellationTokenSource();
        source.Cancel();
        StageResult result = await Stage(release, dir.PatchedInstall(), token: source.Token);
        Assert.Equal(UpdateFailure.Cancelled, result.Failure);
        AssertNothingLeftBehind(dir.Path);
    }

    [Fact]
    public async Task AManifestLargerThanTheLimitIsRefused()
    {
        using var release = new ReleaseFixture().Build();
        release.Served["v0.3.22/optimum-manifest-win-x64.json"] = new byte[OverlayManifest.MaxManifestBytes + 1];
        using var dir = new TempDir();
        StageResult result = await Stage(release, dir.PatchedInstall());
        Assert.False(result.Ok);
    }

    [Fact]
    public void SweepRemovesStagedVersionsThatAreNoLongerNewer()
    {
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        string pending = Path.Combine(game, ".optimum", "pending");
        foreach (string name in new[] { "0.3.21-win-x64", "0.3.22-win-x64", "0.3.23-win-x64", "not-a-version" })
        {
            Directory.CreateDirectory(Path.Combine(pending, name));
            File.WriteAllText(Path.Combine(pending, name, "f.txt"), "x");
        }

        OverlayStager.SweepStale(game, "0.3.22");

        Assert.False(Directory.Exists(Path.Combine(pending, "0.3.21-win-x64")));
        Assert.False(Directory.Exists(Path.Combine(pending, "0.3.22-win-x64")));
        Assert.True(Directory.Exists(Path.Combine(pending, "0.3.23-win-x64")));
        Assert.True(Directory.Exists(Path.Combine(pending, "not-a-version")));
    }

    [Fact]
    public void RemoveStagedDeletesOnlyThatVersion()
    {
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        string pending = Path.Combine(game, ".optimum", "pending");
        foreach (string name in new[] { "0.3.22-win-x64", "0.3.22-linux-x64", "0.3.23-win-x64" })
            Directory.CreateDirectory(Path.Combine(pending, name));

        OverlayStager.RemoveStaged(game, "0.3.22");
        OverlayStager.RemoveStaged(game, "../0.3.23");

        Assert.False(Directory.Exists(Path.Combine(pending, "0.3.22-win-x64")));
        Assert.False(Directory.Exists(Path.Combine(pending, "0.3.22-linux-x64")));
        Assert.True(Directory.Exists(Path.Combine(pending, "0.3.23-win-x64")));
    }

    private static void AssertNothingLeftBehind(string gameDir)
    {
        string pending = Path.Combine(gameDir, ".optimum", "pending");
        if (!Directory.Exists(pending)) return;
        Assert.Empty(Directory.GetFileSystemEntries(pending));
    }
}
