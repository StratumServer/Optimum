using System.Text;
using Optimum.Update;
using Xunit;

namespace Optimum.Update.Tests;

public sealed class OverlayManifestTests
{
    private const string Hash0 = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string Hash1 = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static byte[] Good(Func<string, string>? edit = null)
    {
        string json = "{\"manifestVersion\":1,\"optimumVersion\":\"0.3.22\",\"supportedGameVersions\":[\"1.22.7\"],\"rid\":\"win-x64\","
            + "\"archive\":{\"filename\":\"Optimum-v0.3.22-win-x64-overlay.tar.gz\",\"size\":100,\"sha256\":\"" + Hash0 + "\"},"
            + "\"targets\":[],"
            + "\"files\":[{\"path\":\".optimum/version\",\"size\":6,\"sha256\":\"" + Hash1 + "\"}]}";
        return Encoding.UTF8.GetBytes(edit is null ? json : edit(json));
    }

    private static bool Parses(Func<string, string> edit) => OverlayManifest.TryParse(Good(edit), out _, out _);

    [Fact]
    public void ReadsAValidManifest()
    {
        Assert.True(OverlayManifest.TryParse(Good(), out OverlayManifest? manifest, out string error), error);
        Assert.Equal("0.3.22", manifest!.OptimumVersion);
        Assert.Equal("win-x64", manifest.Rid);
        Assert.True(manifest.SupportsGameVersion("1.22.7"));
        Assert.False(manifest.SupportsGameVersion("1.22.8"));
        Assert.Single(manifest.Files);
        Assert.Equal(new string('1', 64), manifest.Files[0].Sha256);
    }

    [Theory]
    [InlineData("\"manifestVersion\":1", "\"manifestVersion\":2")]
    [InlineData("\"optimumVersion\":\"0.3.22\"", "\"optimumVersion\":\"0.3\"")]
    [InlineData("\"optimumVersion\":\"0.3.22\"", "\"optimumVersion\":\"0.3.22-beta\"")]
    [InlineData("\"rid\":\"win-x64\"", "\"rid\":\"osx-arm64\"")]
    [InlineData("\"supportedGameVersions\":[\"1.22.7\"]", "\"supportedGameVersions\":[]")]
    [InlineData("\"supportedGameVersions\":[\"1.22.7\"]", "\"supportedGameVersions\":[7]")]
    [InlineData("Optimum-v0.3.22-win-x64-overlay.tar.gz", "Other.tar.gz")]
    [InlineData("Optimum-v0.3.22-win-x64-overlay.tar.gz", "Optimum-v0.3.21-win-x64-overlay.tar.gz")]
    [InlineData("\"size\":100", "\"size\":0")]
    [InlineData("\"size\":100", "\"size\":999999999999")]
    [InlineData(Hash0, "sha256:ABC")]
    [InlineData(Hash0, "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(Hash1, Hash1 + "G")]
    public void RejectsAFieldThatIsNotExactlyRight(string from, string to)
    {
        Assert.False(Parses(json => json.Replace(from, to)));
    }

    [Fact]
    public void RejectsAManifestWithoutFiles()
    {
        Assert.False(Parses(json => json.Replace("[{\"path\":\".optimum/version\",\"size\":6,\"sha256\":\"" + Hash1 + "\"}]", "[]")));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../x")]
    [InlineData("/abs")]
    [InlineData("a\\\\b")]
    [InlineData("C:/x")]
    [InlineData("a//b")]
    [InlineData("./x")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("a/b:stream")]
    public void RejectsAnUnsafeFilePath(string path)
    {
        Assert.False(Parses(json => json.Replace(".optimum/version", path)));
    }

    [Fact]
    public void RejectsADuplicatePathIgnoringCase()
    {
        Assert.False(Parses(json => json.Replace("\"files\":[", "\"files\":[{\"path\":\".OPTIMUM/version\",\"size\":1,\"sha256\":\"sha256:" + new string('2', 64) + "\"},")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void RejectsDocumentsThatAreNotAManifest(string text)
    {
        Assert.False(OverlayManifest.TryParse(Encoding.UTF8.GetBytes(text), out _, out _));
    }

    [Theory]
    [InlineData("0.3.22", "0.3.21", 1)]
    [InlineData("0.3.21", "0.3.21", 0)]
    [InlineData("0.3.9", "0.3.10", -1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.4.0", "0.3.99", 1)]
    public void ComparesVersionsNumerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(OverlayManifest.CompareVersions(a, b)));
    }
}

public sealed class InstallStateTests
{
    [Fact]
    public void RecognisesAnInstallPatchedInPlace()
    {
        using var dir = new TempDir();
        Assert.False(InstallLayout.IsPatchedInPlace(dir.Path));
        Assert.True(InstallLayout.IsPatchedInPlace(dir.PatchedInstall()));
    }

    [Theory]
    [InlineData("standalone-install")]
    [InlineData("install-manifest.json")]
    public void AnInstallerInstallIsNotPatchedInPlace(string marker)
    {
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        File.WriteAllText(Path.Combine(game, ".optimum", marker), "");
        Assert.False(InstallLayout.IsPatchedInPlace(game));
    }

    [Fact]
    public void AMissingVanillaBackupMeansThereIsNothingToRollBackTo()
    {
        using var dir = new TempDir();
        string game = dir.PatchedInstall();
        File.Delete(Path.Combine(game, ".optimum", "vanilla", "VintagestoryLib.vanilla.dll"));
        Assert.False(InstallLayout.IsPatchedInPlace(game));
    }

    [Fact]
    public void ResultFileRoundTripsAndClears()
    {
        using var dir = new TempDir();
        var written = new UpdateResult("0.3.22", false, "patch conflict", true, DateTimeOffset.UtcNow);
        written.Write(dir.Path);
        UpdateResult? read = UpdateResult.Read(dir.Path);
        Assert.NotNull(read);
        Assert.Equal("0.3.22", read!.Version);
        Assert.False(read.Ok);
        Assert.True(read.RolledBack);
        Assert.Equal("patch conflict", read.Reason);
        UpdateResult.Clear(dir.Path);
        Assert.Null(UpdateResult.Read(dir.Path));
    }

    [Fact]
    public void AnUnreadableResultFileIsTreatedAsNoResult()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".optimum"));
        File.WriteAllText(UpdateResult.PathFor(dir.Path), "{ nope");
        Assert.Null(UpdateResult.Read(dir.Path));
    }

    [Fact]
    public void OnlyOneHolderOfTheLockAtATime()
    {
        using var dir = new TempDir();
        using UpdateLock? first = UpdateLock.TryAcquire(dir.Path);
        Assert.NotNull(first);
        Assert.Null(UpdateLock.TryAcquire(dir.Path));
        first!.Dispose();
        using UpdateLock? second = UpdateLock.TryAcquire(dir.Path);
        Assert.NotNull(second);
    }

    [Fact]
    public void ALockLeftByADeadProcessIsTakenOverWhenOldEnough()
    {
        using var dir = new TempDir();
        using (UpdateLock.TryAcquire(dir.Path)) { }
        string path = Path.Combine(dir.Path, ".optimum", UpdateLock.FileName);
        File.WriteAllText(path, "999999");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - UpdateLock.StaleAfter - TimeSpan.FromMinutes(1));
        using UpdateLock? taken = UpdateLock.TryAcquire(dir.Path);
        Assert.NotNull(taken);
    }

    [Fact]
    public void AFreshLockIsRespected()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, ".optimum", UpdateLock.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "1");
        Assert.Null(UpdateLock.TryAcquire(dir.Path));
    }
}
