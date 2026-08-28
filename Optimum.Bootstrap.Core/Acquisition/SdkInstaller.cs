using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>
/// Acquires a .NET SDK the build can use, without touching the user's PATH.
/// Downloads the official <c>dotnet-install</c> script and runs it with
/// <c>--install-dir ~/.dotnet</c> and <c>--no-path</c>, honouring the
/// <c>global.json</c> pin. The GUI Prerequisites screen and
/// <c>Optimum.Cli preflight --install</c> both drive one.
/// </summary>
public interface ISdkAcquisition
{
    Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken);
}

public sealed class SdkInstaller : ISdkAcquisition
{
    private readonly ISystemProbe _probe;
    private readonly Func<string, CancellationToken, Task<string?>> _fetchScript;

    public SdkInstaller(ISystemProbe probe) : this(probe, DownloadScriptAsync) { }

    /// <summary>Test seam: <paramref name="fetchScript"/> writes the script to a
    /// temp file and returns its path, or null on failure.</summary>
    internal SdkInstaller(ISystemProbe probe, Func<string, CancellationToken, Task<string?>> fetchScript)
    {
        _probe = probe;
        _fetchScript = fetchScript;
    }

    public async Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken)
    {
        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(_probe, repoRoot);
        if (!decision.CanRunScript || decision.Plan is null)
        {
            return ToolAcquisitionResult.Failure(FailureReason.UnsupportedVersion,
                decision.RefusalReason ?? "a .NET SDK cannot be installed automatically on this system");
        }

        SdkAcquisition.Plan plan = decision.Plan;
        observer.Phase(ProgressPhase.Decompile, 1, "downloading the .NET SDK installer");

        string? scriptPath;
        try
        {
            scriptPath = await _fetchScript(plan.ScriptUrl, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolAcquisitionResult.Failure(FailureReason.Cancelled, "the SDK download was cancelled");
        }
        catch (Exception ex)
        {
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"could not download the .NET SDK installer: {ex.Message}");
        }

        if (scriptPath is null)
        {
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"could not download the .NET SDK installer from {plan.ScriptUrl}");
        }

        try
        {
            observer.Phase(ProgressPhase.Decompile, 2, "installing the .NET SDK");
            var arguments = BuildArguments(plan, scriptPath);
            AcquisitionProcess.Outcome outcome = await AcquisitionProcess.RunAsync(
                plan.ScriptExecutable, arguments, repoRoot,
                environment: null, observer, cancellationToken);
            if (!outcome.Ok)
            {
                return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                    outcome.Message ?? "the .NET SDK installer failed");
            }

            string? found = DotnetSdkProbe.Find(_probe);
            if (found is null)
            {
                return ToolAcquisitionResult.Failure(FailureReason.VerificationFailed,
                    $"the .NET SDK installer reported success but no SDK 10 was found under {plan.InstallDirectory}");
            }

            observer.Log(LogLevel.Info, $"Installed the .NET SDK at {found}");
            return ToolAcquisitionResult.Success(found);
        }
        catch (OperationCanceledException)
        {
            return ToolAcquisitionResult.Failure(FailureReason.Cancelled, "the SDK install was cancelled");
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    /// <summary>
    /// <c>bash dotnet-install.sh &lt;args&gt;</c> on Unix;
    /// <c>powershell -NoProfile -ExecutionPolicy Bypass -File dotnet-install.ps1 &lt;args&gt;</c>
    /// on Windows, where the script's own <c>-InstallDir</c>-style flags are
    /// already in <see cref="SdkAcquisition.Plan.Arguments"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(SdkAcquisition.Plan plan, string scriptPath)
    {
        bool powershell = plan.ScriptUrl.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        List<string> args = powershell
            ? ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]
            : [scriptPath];
        args.AddRange(plan.Arguments);
        return args;
    }

    private static async Task<string?> DownloadScriptAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using HttpResponseMessage response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        string extension = url.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? ".ps1" : ".sh";
        string path = Path.Combine(Path.GetTempPath(), "dotnet-install-" + Guid.NewGuid().ToString("N")[..8] + extension);
        await using (FileStream file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, cancellationToken);
        }

        if (extension == ".sh" && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
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
