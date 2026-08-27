using System.Text.Json;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Ndjson;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class NdjsonWriterTests
{
    private static JsonElement[] Parse(string stream) =>
        stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    [Fact]
    public void ProgressIsMonotonicAndCappedAt99()
    {
        var sw = new StringWriter();
        var writer = new NdjsonWriter(sw);

        writer.Progress(ProgressPhase.Decompile, 10, "a");
        writer.Progress(ProgressPhase.Decompile, 5, "b");   // non-increasing, held at 10
        writer.Progress(ProgressPhase.Assemble, 250, "c");  // over the ceiling, held at 99
        writer.Success("/out/Optimum-v0.3.14-linux-x64");

        int[] progress = Parse(sw.ToString())
            .Where(l => l.GetProperty("type").GetString() == "progress")
            .Select(l => l.GetProperty("progress").GetInt32())
            .ToArray();

        Assert.Equal([10, 10, 99], progress);
        Assert.Equal(2, writer.AnomalyCount);
    }

    [Fact]
    public void AClampEmitsAWarnSoTheAnomalyIsNotSilent()
    {
        var sw = new StringWriter();
        var writer = new NdjsonWriter(sw);

        writer.Progress(ProgressPhase.Patch, 60, "a");
        writer.Progress(ProgressPhase.Patch, 40, "b");   // regression

        JsonElement warn = Parse(sw.ToString())
            .First(l => l.GetProperty("type").GetString() == "log");
        Assert.Equal("warn", warn.GetProperty("level").GetString());
        Assert.Contains("40", warn.GetProperty("message").GetString());
    }

    [Fact]
    public void TheTerminalResultIsTheLastLineAndCarriesTheKebabReason()
    {
        var sw = new StringWriter();
        var writer = new NdjsonWriter(sw);

        writer.Log(LogLevel.Warn, "innoextract not present");
        writer.Failure(FailureReason.PatchConflict, "patches/vsapi/0007 did not apply");

        JsonElement[] lines = Parse(sw.ToString());
        JsonElement result = lines[^1];
        Assert.Equal("result", result.GetProperty("type").GetString());
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("patch-conflict", result.GetProperty("reason").GetString());
        Assert.True(writer.ResultWritten);
    }

    [Fact]
    public void WritingAfterTheResultThrows()
    {
        var writer = new NdjsonWriter(new StringWriter());
        writer.Success("/out");
        Assert.Throws<InvalidOperationException>(() => writer.Log(LogLevel.Info, "too late"));
        Assert.Throws<InvalidOperationException>(() => writer.Progress(ProgressPhase.Verify, 50, "too late"));
    }

    [Fact]
    public void LinesAreDelimitedWithABareNewline()
    {
        var sw = new StringWriter();
        var writer = new NdjsonWriter(sw);
        writer.Progress(ProgressPhase.Patch, 1, "x");
        writer.Success("/out");
        Assert.DoesNotContain('\r', sw.ToString());
    }
}
