using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Update;
using Optimum.Update;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optapply-" + Guid.NewGuid().ToString("N").Substring(0, 10));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string Game => Path.Combine(_root, "game");
    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    private static bool Windows => OperatingSystem.IsWindows();
    private static string Rid => Windows ? "win-x64" : "linux-x64";

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Game);
        Write("VintagestoryLib.dll", "lib-0.3.21");
        Write("VintagestoryAPI.dll", "api-0.3.21");
        Write("Mods/VSEssentials.dll", "ess-0.3.21");
        Write("Optimum.GameContent.dll", "content-0.3.21");
        Write("assets/game/lang/en.json", "{\"v\":\"0.3.21\"}");
        Write(".optimum/version", "0.3.21");
        Write(".optimum/vanilla/VintagestoryLib.vanilla.dll", "vanilla-lib");
        WriteInstalledManifest("0.3.21", ("VintagestoryLib.dll", "lib-0.3.21"));
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar)));
    private bool Exists(string relative) => File.Exists(Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private bool _bareHashes;

    private void WriteInstalledManifest(string version, params (string Assembly, string Content)[] targets)
    {
        var manifest = new PatchInstallManifest
        {
            OptimumVersion = version,
            PatchedAtUtc = DateTimeOffset.UtcNow,
            GameDirectory = Game,
            Targets = targets.Select(t => new PatchTargetRecord { Assembly = t.Assembly, PatchedHash = (_bareHashes ? "" : "sha256:") + Sha(t.Content) }).ToList(),
        };
        Write(".optimum/manifest.json", manifest.Serialize());
    }

    /// <summary>Stages 0.3.22 the way the stager does: files plus the signed manifest kept beside them.</summary>
    private string Stage(string version = "0.3.22", bool sign = true, Func<string, string>? tamper = null)
    {
        string overlay = Path.Combine(Game, ".optimum", "pending", $"{version}-{Rid}");
        var files = new Dictionary<string, string>
        {
            ["optimum.exe"] = "cli-" + version,
            [".optimum/donors/VintagestoryLib.Donor.dll"] = "donor-" + version,
            ["assets/game/lang/en.json"] = "{\"v\":\"" + version + "\"}",
            ["assets/game/shaders/new.fsh"] = "shader-" + version,
        };
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(overlay, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        string archiveName = OverlayManifest.ExpectedArchiveName(version, Rid);
        string json = JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            optimumVersion = version,
            supportedGameVersions = new[] { "1.22.7" },
            rid = Rid,
            archive = new { filename = archiveName, size = 100, sha256 = "sha256:" + new string('0', 64) },
            targets = Array.Empty<object>(),
            files = files.Select(f => new { path = f.Key, size = Encoding.UTF8.GetByteCount(f.Value), sha256 = "sha256:" + Sha(f.Value) }).ToArray(),
        });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Path.Combine(overlay, OverlayStager.StagedManifest), bytes);
        byte[] signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (!sign) signature[5] ^= 1;
        File.WriteAllBytes(Path.Combine(overlay, OverlayStager.StagedManifestSignature), signature);
        if (tamper is not null) File.WriteAllText(Path.Combine(overlay, "optimum.exe"), tamper("cli-" + version));
        return overlay;
    }

    private ApplyRequest Request(string overlay, int? pid = null, TimeSpan? wait = null)
        => new(Game, overlay, pid, wait, PublicKey);

    private UpdateApplier Applier(IGamePatcher patcher, Func<int, bool>? alive = null) => new(patcher, alive, Windows);

    [Fact]
    public async Task AppliesTheStagedUpdateAndRecordsIt()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("0.3.22", outcome.Version);
        Assert.False(outcome.RolledBack);
        Assert.Equal("lib-0.3.22", Read("VintagestoryLib.dll"));
        Assert.Equal("0.3.22", Read(".optimum/version"));
        Assert.False(Directory.Exists(overlay));
        UpdateResult? result = UpdateResult.Read(Game);
        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.False(File.Exists(Path.Combine(Game, ".optimum", UpdateLock.FileName)));
        Assert.Equal(overlay, patcher.OverlayUsed);
    }

    [Fact]
    public async Task AcceptsARecordThatUsesTheBareDigest()
    {
        _bareHashes = true;
        string overlay = Stage();

        ApplyOutcome outcome = await Applier(new FakePatcher(this)).ApplyAsync(Request(overlay), null, default);

        Assert.True(outcome.Ok, outcome.Reason);
    }

    [Fact]
    public async Task KeepsOneSnapshotOfTheVersionItReplaced()
    {
        string overlay = Stage();
        await Applier(new FakePatcher(this)).ApplyAsync(Request(overlay), null, default);

        string[] snapshots = Directory.GetDirectories(Path.Combine(Game, ".optimum", "rollback"));
        Assert.Single(snapshots);
        Assert.Equal("lib-0.3.21", File.ReadAllText(Path.Combine(snapshots[0], "files", "VintagestoryLib.dll")));
    }

    [Fact]
    public async Task APatchThatFailsHalfwayPutsTheOldVersionBack()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this) { FailAfterWriting = true };

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.True(outcome.RolledBack);
        Assert.Contains("conflict", outcome.Reason);
        Assert.Equal("lib-0.3.21", Read("VintagestoryLib.dll"));
        Assert.Equal("api-0.3.21", Read("VintagestoryAPI.dll"));
        Assert.Equal("0.3.21", Read(".optimum/version"));
        Assert.Equal("{\"v\":\"0.3.21\"}", Read("assets/game/lang/en.json"));
        Assert.False(Exists("assets/game/shaders/new.fsh"), "a file the failed patch created must be removed");
        UpdateResult? result = UpdateResult.Read(Game);
        Assert.False(result!.Ok);
        Assert.True(result.RolledBack);
        Assert.False(File.Exists(Path.Combine(Game, ".optimum", UpdateLock.FileName)));
    }

    [Fact]
    public async Task APatchThatThrowsPutsTheOldVersionBack()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this) { Throw = true };

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.True(outcome.RolledBack);
        Assert.Equal("lib-0.3.21", Read("VintagestoryLib.dll"));
    }

    [Fact]
    public async Task APatchThatReportsSuccessWithTheWrongHashIsRolledBack()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this) { RecordWrongHash = true };

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.True(outcome.RolledBack);
        Assert.Contains("does not match", outcome.Reason);
        Assert.Equal("lib-0.3.21", Read("VintagestoryLib.dll"));
    }

    [Fact]
    public async Task APatchThatRecordsAnotherVersionIsRolledBack()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this) { RecordVersion = "0.3.20" };

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.True(outcome.RolledBack);
        Assert.Equal("lib-0.3.21", Read("VintagestoryLib.dll"));
    }

    [Fact]
    public async Task AFileChangedAfterStagingStopsTheUpdateBeforeAnythingIsTouched()
    {
        string overlay = Stage(tamper: _ => "evil");
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.False(outcome.RolledBack);
        Assert.Contains("optimum.exe", outcome.Reason);
        Assert.False(patcher.Called);
        Assert.Equal("lib-0.3.21", Read("VintagestoryLib.dll"));
        Assert.False(Directory.Exists(Path.Combine(Game, ".optimum", "rollback")));
    }

    [Fact]
    public async Task AManifestThatIsNotSignedByTheTrustedKeyStopsTheUpdate()
    {
        string overlay = Stage(sign: false);
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("signature", outcome.Reason);
        Assert.False(patcher.Called);
    }

    [Fact]
    public async Task AnOverlayOutsideThePendingFolderIsRefused()
    {
        Stage();
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(elsewhere), null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("pending", outcome.Reason);
        Assert.False(patcher.Called);
    }

    [Fact]
    public async Task AnUpdateThatIsNotNewerIsRefused()
    {
        WriteInstalledManifest("0.3.22", ("VintagestoryLib.dll", "lib-0.3.21"));
        string overlay = Stage();
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("not newer", outcome.Reason);
        Assert.False(patcher.Called);
    }

    [Fact]
    public async Task AnInstallThatIsNotPatchedInPlaceIsRefused()
    {
        string overlay = Stage();
        File.WriteAllText(Path.Combine(Game, ".optimum", "standalone-install"), "");
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.False(patcher.Called);
    }

    [Fact]
    public async Task WaitsForTheGameToExitBeforeTouchingFiles()
    {
        string overlay = Stage();
        int checks = 0;
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher, _ => ++checks < 4).ApplyAsync(Request(overlay, pid: 4242, wait: TimeSpan.FromSeconds(30)), null, default);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.True(checks >= 4);
    }

    [Fact]
    public async Task DoesNothingWhenTheGameNeverExits()
    {
        string overlay = Stage();
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher, _ => true).ApplyAsync(Request(overlay, pid: 4242, wait: TimeSpan.FromMilliseconds(400)), null, default);

        Assert.False(outcome.Ok);
        Assert.False(outcome.RolledBack);
        Assert.Contains("did not close", outcome.Reason);
        Assert.False(patcher.Called);
        Assert.True(Directory.Exists(overlay), "the staged update must stay for the next try");
    }

    [Fact]
    public async Task RefusesWhenAnotherUpdateHoldsTheLock()
    {
        string overlay = Stage();
        using UpdateLock? held = UpdateLock.TryAcquire(Game);
        var patcher = new FakePatcher(this);

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("another update", outcome.Reason);
        Assert.False(patcher.Called);
    }

    [Fact]
    public async Task ReportsWhenTheOldVersionCannotBeRestoredEither()
    {
        if (!OperatingSystem.IsWindows()) return; // a locked file is the only portable way to make a restore fail, and only Windows enforces it
        string overlay = Stage();
        var patcher = new FakePatcher(this) { FailAfterWriting = true, LockLibAfterWriting = true };

        ApplyOutcome outcome = await Applier(patcher).ApplyAsync(Request(overlay), null, default);
        patcher.ReleaseLock();

        Assert.False(outcome.Ok);
        Assert.False(outcome.RolledBack);
        Assert.Contains("restoring the previous version also failed", outcome.Reason);
        Assert.Contains("VintagestoryLib.dll", outcome.Reason);
    }

    /// <summary>Stands in for the real patcher: writes what a patch writes, fails the ways a patch can fail.</summary>
    private sealed class FakePatcher : IGamePatcher
    {
        private readonly UpdateApplierTests _test;
        private FileStream? _lock;
        public FakePatcher(UpdateApplierTests test) => _test = test;

        public bool Called { get; private set; }
        public string? OverlayUsed { get; private set; }
        public bool FailAfterWriting { get; init; }
        public bool Throw { get; init; }
        public bool RecordWrongHash { get; init; }
        public string? RecordVersion { get; init; }
        public bool LockLibAfterWriting { get; init; }

        public Task<PatchResult> PatchAsync(PatchRequest request, IBuildObserver? observer = null, CancellationToken cancellationToken = default)
        {
            Called = true;
            OverlayUsed = request.OverlayDirectory;
            string version = Path.GetFileName(request.OverlayDirectory!).Split('-')[0];

            _test.Write("VintagestoryLib.dll", "lib-" + version);
            _test.Write("VintagestoryAPI.dll", "api-" + version);
            _test.Write(".optimum/version", version);
            _test.Write("assets/game/lang/en.json", "{\"v\":\"" + version + "\"}");
            _test.Write("assets/game/shaders/new.fsh", "shader");

            if (LockLibAfterWriting)
                _lock = new FileStream(Path.Combine(_test.Game, "VintagestoryLib.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
            if (Throw) throw new InvalidOperationException("boom");
            if (FailAfterWriting) return Task.FromResult(PatchResult.Failure(FailureReason.PatchConflict, "patch conflict in VSEssentials"));

            _test.WriteInstalledManifest(RecordVersion ?? version, ("VintagestoryLib.dll", RecordWrongHash ? "something else" : "lib-" + version));
            return Task.FromResult(PatchResult.Success(request.GameDirectory, new[] { "VintagestoryLib.dll" }));
        }

        public void ReleaseLock() => _lock?.Dispose();
    }
}
