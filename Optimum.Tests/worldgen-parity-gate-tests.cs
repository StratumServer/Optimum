using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

// The worldgen parity gate compares patched-vs-vanilla chunk divergence with a vanilla-vs-vanilla reference
// from a single pair of runs. When that pair came out low the limit dropped with it and a normal patched run
// failed twice in 91 CI runs (mapchunk: reference 8, patched 18, limit 17; reference 4, patched 17, limit 10).
// The limit now never assumes less noise than a per-table floor taken from those runs, in serial mode only.
public sealed class WorldgenParityGateTests
{
    private static string? RunGate(string mtMode, string table, int reference)
    {
        string script = File.ReadAllText(PatchReader.FindRepositoryFile("scripts/worldgen-parity-harness.sh"));
        string functions = Extract(script, "gate_limit") + "\n" + Extract(script, "noise_floor");
        string bash = $"MT_MODE={mtMode}\n{functions}\ngate_limit {reference} \"$(noise_floor {table})\"";

        string? bashPath = FindBash();
        if (bashPath == null) return null;

        var info = new ProcessStartInfo(bashPath, "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(info)!;
        process.StandardInput.Write(bash);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private static string Extract(string script, string function)
    {
        Match match = Regex.Match(script, @"^" + function + @"\(\) \{.*?^\}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, function + " is missing from worldgen-parity-harness.sh");
        return match.Value;
    }

    private static string? FindBash()
    {
        foreach (string candidate in new[] { "/bin/bash", "/usr/bin/bash" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Theory]
    [InlineData("mapchunk", 4, 17)]    // observed in CI on a client GUI change: limit used to be 10
    [InlineData("mapchunk", 8, 18)]    // observed in CI on an upload handoff change: limit used to be 17
    [InlineData("mapchunk", 14, 25)]   // highest patched mapchunk count seen in 91 runs
    [InlineData("mapchunk", 2, 29)]    // a low reference must not make a count within 3.5 sd fail
    [InlineData("chunk", 86, 128)]     // lowest reference and highest patched chunk count seen in 91 runs
    public void SerialMode_PassesTheObservedNormalRuns(string table, int reference, int patched)
    {
        string? limit = RunGate("0", table, reference);
        if (limit == null) return;
        Assert.True(patched <= int.Parse(limit), $"{table}: patched {patched} must not exceed limit {limit} for reference {reference}");
    }

    [Theory]
    [InlineData("mapchunk", 13, 60)]
    [InlineData("mapchunk", 13, 40)]
    [InlineData("chunk", 107, 250)]    // vanilla's own multithreaded worldgen differs from serial by about 249 chunks
    public void SerialMode_StillFailsLargeDivergence(string table, int reference, int patched)
    {
        string? limit = RunGate("0", table, reference);
        if (limit == null) return;
        Assert.True(patched > int.Parse(limit), $"{table}: patched {patched} must exceed limit {limit}");
    }

    [Theory]
    [InlineData("chunk", 90, 119)]               // 90 + max(5, 3*sqrt(90)) = 118.46, rounded up
    [InlineData("mapchunk", 4, 10)]             // the floor does not apply to the multithreaded reference
    public void MultithreadedMode_KeepsTheReferenceBasedLimit(string table, int reference, int expectedLimit)
    {
        string? limit = RunGate("1", table, reference);
        if (limit == null) return;
        Assert.Equal(expectedLimit.ToString(), limit);
    }

    [Fact]
    public void SerialMode_FloorOnlyRaisesTheLimit()
    {
        foreach (string table in new[] { "chunk", "mapchunk" })
        {
            for (int reference = 0; reference <= 300; reference += 7)
            {
                string? serial = RunGate("0", table, reference);
                string? multithreaded = RunGate("1", table, reference);
                if (serial == null || multithreaded == null) return;
                Assert.True(int.Parse(serial) >= int.Parse(multithreaded), $"{table} ref {reference}: floor must not lower the limit");
            }
        }
    }
}
