using System.Text.Json;

namespace Optimum.Bootstrap.Core.Ndjson;

/// <summary>
/// Emits the engine's NDJSON stream from INSTALLER-PLAN.md section 4: one JSON
/// object per line on stdout, progress that never decreases and never reaches
/// 100, and exactly one terminal <c>result</c> line. The writer enforces those
/// invariants so a caller's parser never has to defend against the engine. When
/// it has to adjust a caller's progress value it also emits a <c>warn</c> log and
/// counts it, so an engine-side miscalculation is visible rather than silent.
/// </summary>
public sealed class NdjsonWriter(TextWriter output)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private int _lastPercent;
    private bool _resultWritten;

    public bool ResultWritten => _resultWritten;

    /// <summary>How many times <see cref="Progress"/> had to rewrite a caller's value.</summary>
    public int AnomalyCount { get; private set; }

    public void Progress(ProgressPhase phase, int percent, string detail)
    {
        GuardOpen();

        int clamped = Math.Clamp(percent, _lastPercent, BootstrapProgress.MaxEnginePercent);
        if (clamped != percent)
        {
            AnomalyCount++;
            WriteLog(NdjsonLevel.Warn,
                $"progress {percent} for phase {WirePhase(phase)} adjusted to {clamped}: it must be monotonic and in 0 to 99");
        }

        _lastPercent = clamped;
        Write(writer =>
        {
            writer.WriteString("type", "progress");
            writer.WriteString("phase", WirePhase(phase));
            writer.WriteNumber("progress", clamped);
            writer.WriteString("detail", detail);
        });
    }

    public void Log(NdjsonLevel level, string message)
    {
        GuardOpen();
        WriteLog(level, message);
    }

    public void Success(string runtimePath)
    {
        GuardOpen();
        _resultWritten = true;
        Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteBoolean("ok", true);
            writer.WriteString("runtimePath", runtimePath);
        });
    }

    public void Failure(FailureReason reason, string message)
    {
        GuardOpen();
        _resultWritten = true;
        Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteBoolean("ok", false);
            writer.WriteString("reason", reason.Wire());
            writer.WriteString("message", message);
        });
    }

    private void WriteLog(NdjsonLevel level, string message) => Write(writer =>
    {
        writer.WriteString("type", "log");
        writer.WriteString("level", level switch
        {
            NdjsonLevel.Info => "info",
            NdjsonLevel.Warn => "warn",
            NdjsonLevel.Error => "error",
            _ => "info",
        });
        writer.WriteString("message", message);
    });

    private void GuardOpen()
    {
        if (_resultWritten)
            throw new InvalidOperationException("The NDJSON stream already carries a terminal result line.");
    }

    private void Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        // NDJSON is newline-delimited with a bare '\n', never the platform newline.
        output.Write(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        output.Write('\n');
        output.Flush();
    }

    internal static string WirePhase(ProgressPhase phase) => phase switch
    {
        ProgressPhase.Decompile => "decompile",
        ProgressPhase.Patch => "patch",
        ProgressPhase.Verify => "verify",
        ProgressPhase.Assemble => "assemble",
        _ => "assemble",
    };
}

public enum NdjsonLevel
{
    Info,
    Warn,
    Error,
}
