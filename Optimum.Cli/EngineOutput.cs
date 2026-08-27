using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Ndjson;

namespace Optimum.Cli;

/// <summary>
/// Bridges the engine to the two output modes. Under <c>--json</c> every
/// structured event goes through <see cref="NdjsonWriter"/> on stdout and raw
/// subprocess output goes to stderr. Without it, everything is plain text.
/// </summary>
public sealed class EngineOutput(TextWriter stdout, TextWriter stderr, bool json) : IBuildObserver
{
    private readonly NdjsonWriter? _ndjson = json ? new NdjsonWriter(stdout) : null;

    public int ProgressAnomalies => _ndjson?.AnomalyCount ?? 0;

    public void Phase(ProgressPhase phase, int percent, string detail)
    {
        if (_ndjson is not null)
            _ndjson.Progress(phase, percent, detail);
        else
            stderr.WriteLine($"[{phase.ToString().ToLowerInvariant()} {percent}%] {detail}");
    }

    public void Log(LogLevel level, string message)
    {
        if (_ndjson is not null)
            _ndjson.Log(level, message);
        else
            stderr.WriteLine($"[{level.ToString().ToLowerInvariant()}] {message}");
    }

    public void RawOutput(bool isError, string line) => stderr.WriteLine(line);

    public int Success(string runtimePath)
    {
        if (_ndjson is not null)
            _ndjson.Success(runtimePath);
        else
            stdout.WriteLine(runtimePath);
        return CliRunner.ExitOk;
    }

    public int Failure(FailureReason reason, string message)
    {
        if (_ndjson is not null)
            _ndjson.Failure(reason, message);
        else
            stderr.WriteLine($"error ({reason.Wire()}): {message}");
        return reason == FailureReason.BadInput ? CliRunner.ExitUsage : CliRunner.ExitError;
    }

    /// <summary>Emit a query answer (preflight, capabilities): a single JSON object or plain text.</summary>
    public void Answer(string jsonLine, string humanText)
    {
        stdout.WriteLine(json ? jsonLine : humanText);
    }
}
