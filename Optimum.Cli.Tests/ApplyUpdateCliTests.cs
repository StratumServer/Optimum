using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Update;
using Xunit;

namespace Optimum.Cli.Tests;

public class ApplyUpdateCliTests
{
    private static async Task<(int Code, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliRunner.RunAsync(args, stdout, stderr, new FakeSystemProbe(), new FakeBuildDriver(), default, null, new NeverPatcher());
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Abs(string name) => Path.Combine(Path.GetTempPath(), "optcli-" + name);

    [Fact]
    public async Task RequiresTheGameFolderAndTheOverlay()
    {
        (int code, _, string stderr) = await Run("apply-update", "--overlay", Abs("o"));
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("--game-dir", stderr);

        (code, _, stderr) = await Run("apply-update", "--game-dir", Abs("g"));
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("--overlay", stderr);
    }

    [Theory]
    [InlineData("--game-dir", "relative/game")]
    [InlineData("--overlay", "relative/overlay")]
    [InlineData("--restart", "relative/Optimum.exe")]
    [InlineData("--wait-pid", "abc")]
    [InlineData("--wait-pid", "0")]
    [InlineData("--wait-seconds", "0")]
    [InlineData("--wait-seconds", "99999")]
    public async Task RejectsAValueThatIsNotUsable(string flag, string value)
    {
        var args = new List<string> { "apply-update", "--game-dir", Abs("g"), "--overlay", Abs("o") };
        int index = args.IndexOf(flag);
        if (index >= 0) args[index + 1] = value; else { args.Add(flag); args.Add(value); }

        (int code, _, string stderr) = await Run(args.ToArray());

        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains(flag, stderr);
    }

    [Fact]
    public async Task ReportsAnInstallItCannotUpdateAndLeavesAResultForTheNextStart()
    {
        string game = Path.Combine(Path.GetTempPath(), "optcli-game-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(game);
        try
        {
            (int code, _, string stderr) = await Run("apply-update", "--game-dir", game, "--overlay", Path.Combine(game, ".optimum", "pending", "0.3.22-win-x64"));

            Assert.Equal(CliRunner.ExitError, code);
            Assert.Contains("not patched in place", stderr);
            UpdateResult? result = UpdateResult.Read(game);
            Assert.NotNull(result);
            Assert.False(result!.Ok);
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAnswersWithoutNeedingAnyOtherFlag()
    {
        (int code, string stdout, _) = await Run("apply-update", "--probe");
        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Contains("apply-update", stdout);
    }

    [Fact]
    public async Task AppearsInTheUsageText()
    {
        (_, _, string stderr) = await Run();
        Assert.Contains("apply-update", stderr);
    }

    private sealed class NeverPatcher : IGamePatcher
    {
        public Task<PatchResult> PatchAsync(PatchRequest request, IBuildObserver? observer = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the patcher must not run in these tests");
    }
}
