using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using CliWrap.EventStream;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Acquisition;

public sealed record ToolAcquisitionResult(bool Ok, string? InstalledPath, FailureReason? Reason, string? Message)
{
    public static ToolAcquisitionResult Success(string installedPath) => new(true, installedPath, null, null);

    public static ToolAcquisitionResult Failure(FailureReason reason, string message) =>
        new(false, null, reason, message);
}

public interface IAppimagetoolAcquisition
{
    Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken);
}

/// <summary>
/// Installs the upstream x86-64 appimagetool AppImage into the checkout's
/// private tool directory. The download is staged beside the final file so an
/// interruption never replaces a previously usable tool with a partial file.
/// </summary>
public sealed class AppimagetoolAcquisition : IAppimagetoolAcquisition
{
    public const string DownloadUrl =
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage";

    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly ISystemProbe _probe;
    private readonly string _downloadUrl;

    public AppimagetoolAcquisition(ISystemProbe probe) : this(probe, DownloadUrl) { }

    internal AppimagetoolAcquisition(ISystemProbe probe, string downloadUrl)
    {
        _probe = probe;
        _downloadUrl = downloadUrl;
    }

    public async Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || _probe.Os != OsKind.Linux || _probe.Arch != Architecture.X64)
        {
            return ToolAcquisitionResult.Failure(FailureReason.UnsupportedVersion,
                "the automatic appimagetool install currently supports Linux x86-64 only");
        }

        string target = TargetPath(repoRoot);
        if (_probe.IsExecutable(target))
            return ToolAcquisitionResult.Success(target);

        string? curl = CommandSearch.Which(_probe, "curl");
        if (curl is null)
        {
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                "curl was not found on PATH; install curl and retry");
        }

        string toolDirectory = Path.GetDirectoryName(target)!;
        string staging = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Directory.CreateDirectory(toolDirectory);
            observer.Log(LogLevel.Info, "Downloading appimagetool...");

            int exitCode = await DownloadAsync(curl, staging, observer, cancellationToken);
            if (exitCode != 0)
            {
                TryDelete(staging);
                return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                    $"appimagetool download failed (curl exit {exitCode})");
            }

            File.SetUnixFileMode(staging,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staging, target, overwrite: true);

            if (!_probe.IsExecutable(target))
            {
                return ToolAcquisitionResult.Failure(FailureReason.VerificationFailed,
                    $"the downloaded appimagetool is not executable: {target}");
            }

            observer.Log(LogLevel.Info, $"Installed appimagetool at {target}");
            return ToolAcquisitionResult.Success(target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            return ToolAcquisitionResult.Failure(FailureReason.Cancelled,
                "the appimagetool download was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            TryDelete(staging);
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"could not install appimagetool: {ex.Message}");
        }
    }

    public static string TargetPath(string repoRoot) => Path.Combine(repoRoot, ".tools", "appimagetool");

    internal IReadOnlyList<string> DownloadArguments(string destination) =>
        ["--location", "--fail", "--show-error", "--output", destination, _downloadUrl];

    private async Task<int> DownloadAsync(
        string curl, string destination, IBuildObserver observer, CancellationToken cancellationToken)
    {
        int exitCode = -1;
        Command command = Cli.Wrap(curl)
            .WithArguments(DownloadArguments(destination))
            .WithValidation(CommandResultValidation.None);

        await foreach (CommandEvent commandEvent in
            command.ListenAsync(Utf8, Utf8, cancellationToken, CancellationToken.None))
        {
            switch (commandEvent)
            {
                case StandardOutputCommandEvent stdout:
                    observer.RawOutput(false, stdout.Text);
                    break;
                case StandardErrorCommandEvent stderr:
                    observer.RawOutput(stderr.Text.Contains("error", StringComparison.OrdinalIgnoreCase), stderr.Text);
                    break;
                case ExitedCommandEvent exited:
                    exitCode = exited.ExitCode;
                    break;
            }
        }

        return exitCode;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
