using System.Text.Json;
using Xunit;

namespace Optimum.Cli.Tests;

/// <summary>
/// Consumes an NDJSON stream the way RiftLauncher's <c>runTrackedWorker</c> does
/// and asserts the contract in INSTALLER-PLAN.md section 4. This is the reusable
/// conformance check the CI step also runs.
/// </summary>
public sealed class NdjsonStream
{
    private static readonly HashSet<string> KnownTypes = ["progress", "log", "result"];
    private static readonly HashSet<string> KnownPhases = ["decompile", "patch", "verify", "assemble"];
    private static readonly HashSet<string> KnownReasons =
    [
        "bad-input", "unsupported-version", "patch-conflict", "decompile-failed",
        "assemble-failed", "verification-failed", "output-exists", "cancelled", "engine-internal",
    ];

    public required IReadOnlyList<JsonElement> Lines { get; init; }

    public JsonElement Terminal => Lines[^1];

    public static NdjsonStream Parse(string stdout)
    {
        var lines = new List<JsonElement>();
        foreach (string raw in stdout.Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            using var doc = JsonDocument.Parse(raw);
            lines.Add(doc.RootElement.Clone());
        }

        Assert.NotEmpty(lines);
        return new NdjsonStream { Lines = lines };
    }

    public void AssertContract()
    {
        int lastProgress = 0;
        int resultCount = 0;

        for (int i = 0; i < Lines.Count; i++)
        {
            JsonElement line = Lines[i];
            Assert.Equal(JsonValueKind.Object, line.ValueKind);
            string type = line.GetProperty("type").GetString()!;
            Assert.True(KnownTypes.Contains(type), $"unknown line type: {type}");

            switch (type)
            {
                case "progress":
                    Assert.True(KnownPhases.Contains(line.GetProperty("phase").GetString()!));
                    int progress = line.GetProperty("progress").GetInt32();
                    Assert.InRange(progress, lastProgress, 99);
                    lastProgress = progress;
                    break;

                case "log":
                    Assert.Contains(line.GetProperty("level").GetString(), new[] { "info", "warn", "error" });
                    break;

                case "result":
                    resultCount++;
                    Assert.Equal(Lines.Count - 1, i);
                    if (!line.GetProperty("ok").GetBoolean())
                    {
                        Assert.True(KnownReasons.Contains(line.GetProperty("reason").GetString()!));
                        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("message").GetString()));
                    }
                    else
                    {
                        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("runtimePath").GetString()));
                    }
                    break;
            }
        }

        Assert.Equal(1, resultCount);
    }
}
