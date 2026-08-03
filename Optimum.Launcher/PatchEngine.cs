using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Launcher;

/// <summary>
/// Progress report for a single patch step.
/// </summary>
public record PatchProgress(int Current, int Total, string Description);

/// <summary>
/// Wraps the Cecil patching logic. In the MVP, this delegates to
/// Optimum.Patcher as a subprocess (since ILPatcher references types
/// we can't load without the donor DLL). Future versions will embed
/// the patcher logic directly.
/// </summary>
public static class PatchEngine
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Patches a vanilla assembly using the Optimum.Patcher executable.
    /// Returns the patched bytes (DLL + PDB) or throws on failure.
    ///
    /// In Phase 1 (MVP), this invokes Optimum.Patcher as a subprocess:
    ///   Optimum.Patcher {vanillaPath} {compiledPath} {outputPath}
    ///
    /// The compiled (donor) DLL must be available alongside Optimum.exe.
    /// </summary>
    public static PatchResult Patch(
        string vanillaDllPath,
        string donorDllPath,
        string outputDllPath,
        PatchMode mode,
        string? modName,
        IProgress<PatchProgress>? progress = null)
    {
        var patcherExe = FindPatcherExecutable();
        if (patcherExe is null)
            throw new FileNotFoundException("Optimum.Patcher executable not found alongside Optimum.exe");

        var outputDirectory = Path.GetDirectoryName(outputDllPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        // Do not let a previous partial run satisfy the output check after a
        // new patcher process exits without writing a fresh assembly.
        if (File.Exists(outputDllPath))
            File.Delete(outputDllPath);
        var outputPdbPath = Path.ChangeExtension(outputDllPath, ".pdb");
        if (File.Exists(outputPdbPath))
            File.Delete(outputPdbPath);

        progress?.Report(new PatchProgress(0, 1, $"Patching {Path.GetFileName(vanillaDllPath)}..."));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(patcherExe);
        switch (mode)
        {
            case PatchMode.Transplant:
                break;
            case PatchMode.Api:
                psi.ArgumentList.Add("--api");
                break;
            case PatchMode.Mod when !string.IsNullOrWhiteSpace(modName):
                psi.ArgumentList.Add("--mod");
                psi.ArgumentList.Add(modName);
                break;
            case PatchMode.Mod:
                throw new ArgumentException("A mod patch target requires a module name.", nameof(modName));
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
        psi.ArgumentList.Add(vanillaDllPath);
        psi.ArgumentList.Add(donorDllPath);
        psi.ArgumentList.Add(outputDllPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Optimum.Patcher process");

        // Start both readers before waiting. Reading one redirected stream to
        // completion before the other can deadlock when the second pipe fills.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and Kill call.
            }
            catch (Exception ex)
            {
                throw new PatchFailedException(
                    $"Optimum.Patcher timed out after {ProcessTimeout} and could not stop: {ex.Message}");
            }
            bool stopped = proc.WaitForExit(10_000);
            bool outputDrained = Task.WaitAll([stdoutTask, stderrTask], 10_000);
            string timedOutStdout = outputDrained ? stdoutTask.GetAwaiter().GetResult() : "(unavailable)";
            string timedOutStderr = outputDrained ? stderrTask.GetAwaiter().GetResult() : "(unavailable)";
            if (!stopped)
            {
                throw new PatchFailedException(
                    $"Optimum.Patcher timed out after {ProcessTimeout} and did not stop.\n" +
                    $"stdout: {timedOutStdout}\nstderr: {timedOutStderr}");
            }
            throw new PatchFailedException(
                $"Optimum.Patcher timed out after {ProcessTimeout}.\n" +
                $"stdout: {timedOutStdout}\nstderr: {timedOutStderr}");
        }

        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            throw new PatchFailedException(
                $"Optimum.Patcher exited with code {proc.ExitCode}.\n" +
                $"stdout: {stdout}\nstderr: {stderr}");
        }

        // Read the output DLL + PDB
        if (!File.Exists(outputDllPath))
            throw new PatchFailedException($"Patcher did not produce output: {outputDllPath}");

        var dllBytes = File.ReadAllBytes(outputDllPath);
        var pdbPath = outputPdbPath;
        var pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

        // Parse stdout for patch count
        int patchCount = ParsePatchCount(stdout);

        progress?.Report(new PatchProgress(1, 1, "Done."));

        return new PatchResult(dllBytes, pdbBytes, patchCount);
    }

    /// <summary>
    /// Finds the Optimum.Patcher DLL next to the running executable.
    /// </summary>
    private static string? FindPatcherExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var patcherDll = Path.Combine(baseDir, "Optimum.Patcher.dll");
        if (File.Exists(patcherDll))
            return patcherDll;

        // Also check for the exe variant
        var patcherExe = Path.Combine(baseDir, "Optimum.Patcher.exe");
        if (File.Exists(patcherExe))
            return patcherExe;

        return null;
    }

    /// <summary>
    /// Extracts the patch count from Patcher stdout.
    /// The patcher prints lines like "Done." and returns 0 on success.
    /// The total count is derived from the patcher's target list.
    /// </summary>
    private static int ParsePatchCount(string stdout)
    {
        // The patcher prints the count implicitly via its return value.
        // We count "Transplanting" or similar lines if present.
        int count = 0;
        foreach (var line in stdout.AsSpan().EnumerateLines())
        {
            if (line.Contains("→", StringComparison.Ordinal) ||
                line.Contains("Transplant", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Inject", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }
        return count > 0 ? count : 1; // At least 1 if patcher succeeded
    }
}

/// <summary>Result of a successful patch operation.</summary>
public record PatchResult(byte[] DllBytes, byte[]? PdbBytes, int PatchCount);

/// <summary>Thrown when the Cecil patcher fails.</summary>
public sealed class PatchFailedException : Exception
{
    public PatchFailedException(string message) : base(message) { }
}
