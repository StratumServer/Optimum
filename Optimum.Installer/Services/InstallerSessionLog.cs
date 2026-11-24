using System.Text;

namespace Optimum.Installer.Services;

/// <summary>
/// Persists a single installer session's events and subprocess output to disk
/// from startup through completion or early exit.
/// </summary>
public interface IInstallerLog : IDisposable
{
    string FilePath { get; }
    bool IsAvailable { get; }
    void WriteEvent(string category, string message);
    void WriteRaw(string line);
}

public sealed class InstallerSessionLog : IInstallerLog
{
    public const string UnavailablePath = "(log not written)";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock gate = new();
    private StreamWriter? writer;
    private bool disposed;
    private bool writeFailed;

    private InstallerSessionLog(string filePath, StreamWriter? writer)
    {
        FilePath = filePath;
        this.writer = writer;
    }

    public string FilePath { get; }

    public bool IsAvailable => writer is not null && !writeFailed;

    public static InstallerSessionLog Create(string? directory = null, DateTimeOffset? startedAt = null)
    {
        string targetDirectory = string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory;
        DateTimeOffset timestamp = startedAt ?? DateTimeOffset.Now;
        string stem = $"optimum-install-{timestamp:yyyy-MM-ddTHHmmss}";

        try
        {
            Directory.CreateDirectory(targetDirectory);
            string candidate = Path.Combine(targetDirectory, $"{stem}.log");
            FileStream stream;
            try
            {
                stream = OpenExclusiveNew(candidate);
            }
            catch (IOException)
            {
                string suffix = Guid.NewGuid().ToString("N")[..8];
                candidate = Path.Combine(targetDirectory, $"{stem}-{suffix}.log");
                stream = OpenExclusiveNew(candidate);
            }

            var streamWriter = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
            return new InstallerSessionLog(candidate, streamWriter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstallerSessionLog(UnavailablePath, null);
        }
    }

    private static FileStream OpenExclusiveNew(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public void WriteEvent(string category, string message)
    {
        if (writer is null)
            return;

        string formatted = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
        WriteLineCore(formatted);
    }

    public void WriteRaw(string line)
    {
        if (writer is null)
            return;

        WriteLineCore(line);
    }

    private void WriteLineCore(string text)
    {
        lock (gate)
        {
            if (disposed || writeFailed || writer is null)
                return;

            try
            {
                writer.WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                writeFailed = true;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;

            StreamWriter? current = writer;
            writer = null;
            if (current is not null)
            {
                try
                {
                    current.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    writeFailed = true;
                }
            }
        }
    }
}
