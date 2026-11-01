using Optimum.Update;
using Xunit;

namespace Optimum.Update.Tests;

public sealed class UpdaterTests
{
    private static OptimumUpdater Updater(ReleaseFixture release, string gameDir, string installed = "0.3.21", Func<string, string?>? env = null, string? key = null, HttpClient? http = null)
        => new(gameDir, installed, "1.22.7", release.Rid, () => http ?? release.Client(), key ?? release.PublicKey, ReleaseFixture.Base, env ?? (_ => null));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "timed out waiting for the updater");
    }

    [Fact]
    public void OffersNothingWithoutAKey()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall(), key: "");
        Assert.False(updater.IsSupported(out string reason));
        Assert.Contains("signing key", reason);
        updater.OfferRelease("0.3.22");
        Assert.Equal(UpdatePhase.Idle, updater.Phase);
    }

    [Fact]
    public void OffersNothingWhenAnotherLauncherManagesTheInstall()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall(), env: name => name == OptimumUpdater.ManagedByEnvironmentVariable ? "RiftLauncher" : null);
        Assert.False(updater.IsSupported(out string reason));
        Assert.Contains("another launcher", reason);
        updater.OfferRelease("0.3.22");
        Assert.Equal(UpdatePhase.Idle, updater.Phase);
    }

    [Fact]
    public void OffersNothingForAnInstallerInstall()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        File.WriteAllText(Path.Combine(game, ".optimum", "standalone-install"), "");
        OptimumUpdater updater = Updater(release, game);
        updater.OfferRelease("0.3.22");
        Assert.Equal(UpdatePhase.Idle, updater.Phase);
    }

    [Theory]
    [InlineData("0.3.21")]
    [InlineData("0.3.20")]
    [InlineData("junk")]
    public void IgnoresAReleaseThatIsNotNewer(string offered)
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall());
        updater.OfferRelease(offered);
        Assert.Equal(UpdatePhase.Idle, updater.Phase);
    }

    [Fact]
    public async Task StagesInTheBackgroundAndEndsStaged()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall());
        var changes = 0;
        updater.StateChanged += () => Interlocked.Increment(ref changes);

        updater.OfferRelease("0.3.22");
        Assert.Equal(UpdatePhase.Available, updater.Phase);
        Assert.Equal("0.3.22", updater.TargetVersion);

        updater.BeginStaging();
        await WaitFor(() => updater.Phase is UpdatePhase.Staged or UpdatePhase.Failed);

        Assert.Equal(UpdatePhase.Staged, updater.Phase);
        Assert.NotNull(updater.StagedDirectory);
        Assert.True(Directory.Exists(updater.StagedDirectory));
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task AFailedStageShowsWhyAndCanBeRetried()
    {
        using var release = new ReleaseFixture { SignManifest = false }.Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall());
        updater.OfferRelease("0.3.22");
        updater.BeginStaging();
        await WaitFor(() => updater.Phase is UpdatePhase.Staged or UpdatePhase.Failed);
        Assert.Equal(UpdatePhase.Failed, updater.Phase);
        Assert.NotEmpty(updater.Detail);

        // Publish the signature and try again.
        release.Served["v0.3.22/optimum-manifest-win-x64.json.sig"] = release.Key.SignData(release.ManifestBytes, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        updater.BeginStaging();
        await WaitFor(() => updater.Phase is UpdatePhase.Staged);
        Assert.Equal(UpdatePhase.Staged, updater.Phase);
    }

    [Fact]
    public async Task StartingTwiceDownloadsOnce()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        int archiveRequests = 0;
        OptimumUpdater updater = Updater(release, dir.PatchedInstall(), http: release.Client(path => { if (path.EndsWith(".tar.gz")) Interlocked.Increment(ref archiveRequests); return null; }));
        updater.OfferRelease("0.3.22");
        updater.BeginStaging();
        updater.BeginStaging();
        await WaitFor(() => updater.Phase is UpdatePhase.Staged or UpdatePhase.Failed);
        Assert.Equal(1, archiveRequests);
    }

    [Fact]
    public async Task AFreshStartOffersTheRestartWhenACopyWasStagedBefore()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        OptimumUpdater first = Updater(release, game);
        first.OfferRelease("0.3.22");
        first.BeginStaging();
        await WaitFor(() => first.Phase == UpdatePhase.Staged);

        int requests = 0;
        OptimumUpdater second = Updater(release, game, http: release.Client(_ => { requests++; return null; }));
        second.OfferRelease("0.3.22");

        Assert.Equal(UpdatePhase.Staged, second.Phase);
        Assert.Equal(first.StagedDirectory, second.StagedDirectory);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task AStagedCopyThatWasChangedIsNotOffered()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        OptimumUpdater first = Updater(release, game);
        first.OfferRelease("0.3.22");
        first.BeginStaging();
        await WaitFor(() => first.Phase == UpdatePhase.Staged);
        File.WriteAllText(Path.Combine(first.StagedDirectory!, "optimum.exe"), "tampered");

        OptimumUpdater second = Updater(release, game);
        second.OfferRelease("0.3.22");

        Assert.Equal(UpdatePhase.Available, second.Phase);
        Assert.Null(second.StagedDirectory);
    }

    [Fact]
    public void AStagedCopySignedByAnotherKeyIsNotFound()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        Assert.True(new OverlayStager(release.Client(), ReleaseFixture.Base, release.PublicKey).Stage(game, "0.3.21", "1.22.7", release.Rid, "0.3.22", null, default).Ok);
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        Assert.NotNull(OverlayStager.FindStaged(game, "0.3.22", release.Rid, release.PublicKey));
        Assert.Null(OverlayStager.FindStaged(game, "0.3.22", release.Rid, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.Null(OverlayStager.FindStaged(game, "0.3.22", release.Rid, ""));
        Assert.Null(OverlayStager.FindStaged(game, "0.3.23", release.Rid, release.PublicKey));
    }

    [Fact]
    public async Task TheHelperCommandRunsTheStagedCliWithTheTrustedKeyAndRestart()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        OptimumUpdater updater = Updater(release, game);
        updater.OfferRelease("0.3.22");
        updater.BeginStaging();
        await WaitFor(() => updater.Phase == UpdatePhase.Staged);

        System.Diagnostics.ProcessStartInfo? command = updater.BuildHelperCommand(4321, Path.Combine(game, "Vintagestory.exe"), "--dataPath X", windows: true);

        Assert.NotNull(command);
        Assert.Equal(Path.Combine(updater.StagedDirectory!, "optimum.exe"), command!.FileName);
        string[] args = command.ArgumentList.ToArray();
        Assert.Equal("apply-update", args[0]);
        Assert.Equal(game, args[Array.IndexOf(args, "--game-dir") + 1]);
        Assert.Equal(updater.StagedDirectory, args[Array.IndexOf(args, "--overlay") + 1]);
        Assert.Equal("4321", args[Array.IndexOf(args, "--wait-pid") + 1]);
        Assert.Equal(release.PublicKey, args[Array.IndexOf(args, "--trusted-key") + 1]);
        Assert.Equal(Path.Combine(game, "Vintagestory.exe"), args[Array.IndexOf(args, "--restart") + 1]);
        Assert.Equal("--dataPath X", args[Array.IndexOf(args, "--restart-args") + 1]);
        Assert.False(command.UseShellExecute);
    }

    [Fact]
    public void ThereIsNoHelperCommandBeforeAnythingIsStaged()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        OptimumUpdater updater = Updater(release, dir.PatchedInstall());
        Assert.Null(updater.BuildHelperCommand(1, "x", "", windows: true));
        Assert.False(updater.StartHelper(1, "x", ""));
        Assert.Equal(UpdatePhase.Failed, updater.Phase);
    }

    [Fact]
    public async Task HelperCommandUsesTheStagedCliAndWaitsForTheGameToExit()
    {
        using var release = new ReleaseFixture().Build();
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        OptimumUpdater updater = Updater(release, game);
        updater.OfferRelease("0.3.22");
        updater.BeginStaging();
        await WaitFor(() => updater.Phase == UpdatePhase.Staged);

        var command = updater.BuildHelperCommand(1234, "", "", windows: true);
        Assert.NotNull(command);
        Assert.Equal(Path.Combine(updater.StagedDirectory!, "optimum.exe"), command!.FileName);
        Assert.Equal(new[] { "apply-update", "--game-dir", game, "--overlay", updater.StagedDirectory,
            "--wait-pid", "1234", "--trusted-key", release.PublicKey }, command.ArgumentList);
    }
}
