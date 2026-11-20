using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

public sealed record PatchRequest(
    string GameDirectory,
    string? OverlayDirectory = null,
    bool Backup = true,
    bool Rollback = false,
    IReadOnlyList<string>? SupportedGameVersions = null);

public sealed record PatchResult(
    bool Ok,
    FailureReason? Reason,
    string? Message,
    string? GameDirectory,
    IReadOnlyList<string>? PatchedTargets)
{
    public static PatchResult Success(string gameDir, IReadOnlyList<string> targets) =>
        new(true, null, null, gameDir, targets);

    public static PatchResult Failure(FailureReason reason, string message) =>
        new(false, reason, message, null, null);
}

public interface IGamePatcher
{
    Task<PatchResult> PatchAsync(
        PatchRequest request,
        IBuildObserver? observer = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies Optimum Cecil patches, contracts, shaders, and language strings to an
/// existing Vintage Story game directory using an overlay or donor layout.
/// Preserves pristine vanilla assemblies into <c>.optimum/vanilla/</c> for idempotency and rollback.
/// </summary>
public sealed class GamePatcher(
    ISystemProbe probe,
    IRuntimeValidator? validator = null,
    Func<string, string?>? versionDetector = null) : IGamePatcher
{
    internal const int DefaultMaxStagingConcurrency = 2;

    private readonly IRuntimeValidator _validator = validator ?? new RuntimeValidator(probe);
    private readonly Func<string, string?> _versionDetector = versionDetector ?? DetectGameVersion;
    private readonly Lock hashCallbackGate = new();

    /// <summary>
    /// Test seam invoked during the commit phase with the step name
    /// (<c>"swap"</c>, <c>"overlay"</c>, <c>"manifest"</c>).
    /// </summary>
    internal Action<string>? FailAtCommitStep { get; init; }

    /// <summary>
    /// Test seam invoked whenever <see cref="ComputeSha256"/> hashes a file from disk.
    /// </summary>
    internal Action<string>? OnComputeSha256 { get; init; }

    /// <summary>
    /// Maximum number of game assembly targets staged concurrently before commit.
    /// Bounded to two workers by default to overlap Cecil work without spiking memory.
    /// </summary>
    internal int MaxStagingConcurrency { get; init; } = DefaultMaxStagingConcurrency;

    private sealed record PatchTargetSpec(
        int CanonicalIndex,
        int SchedulePriority,
        string AssemblyName,
        string SourcePath,
        string LivePath,
        string StagedPath,
        string[] PatcherArgs);

    private sealed record StagedTargetResult(
        PatchTargetSpec Spec,
        string VanillaHash,
        string PatchedHash);

    private sealed record InProcessPatcherInvoker(
        MethodInfo Method,
        bool HasHashOutParam);

    public async Task<PatchResult> PatchAsync(
        PatchRequest request,
        IBuildObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.GameDirectory))
            return PatchResult.Failure(FailureReason.BadInput, "--game-dir is required");

        if (!Path.IsPathRooted(request.GameDirectory))
            return PatchResult.Failure(FailureReason.BadInput, $"--game-dir must be an absolute path: {request.GameDirectory}");

        string gameDir = NormalizeAbsolute(request.GameDirectory);
        if (!probe.DirectoryExists(gameDir))
            return PatchResult.Failure(FailureReason.BadInput, $"the game directory does not exist: {gameDir}");

        // Rollback flow
        if (request.Rollback)
        {
            return Rollback(gameDir, observer);
        }

        // Verify that gameDir is a Vintage Story installation
        string vanillaBackupDir = Path.Combine(gameDir, ".optimum", "vanilla");
        string gameLib = Path.Combine(gameDir, "VintagestoryLib.dll");
        string backupLib = Path.Combine(vanillaBackupDir, "VintagestoryLib.vanilla.dll");
        bool hasLib = probe.FileExists(gameLib) || probe.FileExists(backupLib);

        string gameApi = Path.Combine(gameDir, "VintagestoryAPI.dll");
        string backupApi = Path.Combine(vanillaBackupDir, "VintagestoryAPI.vanilla.dll");
        bool hasApi = probe.FileExists(gameApi) || probe.FileExists(backupApi);

        if (!hasLib || !hasApi)
        {
            return PatchResult.Failure(FailureReason.BadInput,
                $"the game directory does not appear to be a Vintage Story install (missing VintagestoryLib.dll and VintagestoryAPI.dll): {gameDir}");
        }

        // Resolve overlay directory
        string? overlayDir = null;
        if (request.OverlayDirectory is not null)
        {
            if (!Path.IsPathRooted(request.OverlayDirectory))
                return PatchResult.Failure(FailureReason.BadInput, $"--overlay must be an absolute path: {request.OverlayDirectory}");

            overlayDir = NormalizeAbsolute(request.OverlayDirectory);
            if (!probe.DirectoryExists(overlayDir))
                return PatchResult.Failure(FailureReason.BadInput, $"the overlay directory does not exist: {overlayDir}");
        }

        // Verify game version compatibility before backup or modification
        IReadOnlyList<string>? supportedVersions = request.SupportedGameVersions;
        if (supportedVersions is null || supportedVersions.Count == 0)
        {
            if (overlayDir is not null)
            {
                string manifestPath = Path.Combine(overlayDir, PatchOverlayManifest.FileName);
                if (probe.FileExists(manifestPath))
                {
                    string? json = probe.ReadText(manifestPath);
                    if (json is not null)
                    {
                        var overlayManifest = PatchOverlayManifest.Deserialize(json);
                        if (overlayManifest?.SupportedGameVersions is { Count: > 0 } sv)
                        {
                            supportedVersions = sv;
                        }
                    }
                }
            }
        }

        if (supportedVersions is { Count: > 0 })
        {
            string candidateLib = probe.FileExists(backupLib) ? backupLib : gameLib;
            string? detectedVersion = _versionDetector(candidateLib);
            if (!string.IsNullOrEmpty(detectedVersion))
            {
                bool isSupported = supportedVersions.Any(sv =>
                    string.Equals(sv, detectedVersion, StringComparison.OrdinalIgnoreCase));
                if (!isSupported)
                {
                    return PatchResult.Failure(FailureReason.UnsupportedVersion,
                        $"incompatible game version: detected Vintage Story {detectedVersion}, but this release of Optimum only supports: {string.Join(", ", supportedVersions)}");
                }
            }
        }

        // Resolve patcher tool
        (string? patcherPath, bool isDll) = ResolvePatcher(overlayDir);
        if (patcherPath is null)
        {
            return PatchResult.Failure(FailureReason.BadInput,
                "Optimum.Patcher executable or assembly not found in overlay or application directory.");
        }

        // Resolve donor assemblies
        string? donorLib = ResolveDonor(overlayDir, gameDir, "VintagestoryLib.Donor.dll", "VintagestoryLib.dll");
        if (donorLib is null)
        {
            return PatchResult.Failure(FailureReason.BadInput,
                "VintagestoryLib.Donor.dll not found in overlay or donors directory.");
        }

        string? donorApi = ResolveDonor(overlayDir, gameDir, "VintagestoryAPI.Contracts.dll", "Optimum.Api.Contracts.dll");
        if (donorApi is null)
        {
            return PatchResult.Failure(FailureReason.BadInput,
                "VintagestoryAPI.Contracts.dll not found in overlay or donors directory.");
        }

        string? donorEssentials = ResolveDonor(overlayDir, gameDir, "VSEssentials.Donor.dll", "VSEssentials.dll");
        string? donorSurvival = ResolveDonor(overlayDir, gameDir, "VSSurvivalMod.Donor.dll", "VSSurvivalMod.dll");

        observer?.Log(LogLevel.Info, $"patching Vintage Story at {gameDir}");
        observer?.Phase(ProgressPhase.Patch, 10, "preparing patch targets and vanilla backups");

        // Idempotent backup: preserve pristine files into .optimum/vanilla/ before any modification
        string vanillaModsDir = Path.Combine(vanillaBackupDir, "Mods");
        string gameEssentials = Path.Combine(gameDir, "Mods", "VSEssentials.dll");
        string backupEssentials = Path.Combine(vanillaModsDir, "VSEssentials.dll");
        string gameSurvival = Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll");
        string backupSurvival = Path.Combine(vanillaModsDir, "VSSurvivalMod.dll");

        if (request.Backup)
        {
            EnsureDirectory(vanillaBackupDir);
            EnsureDirectory(vanillaModsDir);

            if (!probe.FileExists(backupLib) && probe.FileExists(gameLib))
                CopyFile(gameLib, backupLib);

            if (!probe.FileExists(backupApi) && probe.FileExists(gameApi))
                CopyFile(gameApi, backupApi);

            if (!probe.FileExists(backupEssentials) && probe.FileExists(gameEssentials))
                CopyFile(gameEssentials, backupEssentials);

            if (!probe.FileExists(backupSurvival) && probe.FileExists(gameSurvival))
                CopyFile(gameSurvival, backupSurvival);
        }

        // Determine patch sources: if vanilla backup exists, use it as source to guarantee idempotency
        string sourceLib = probe.FileExists(backupLib) ? backupLib : gameLib;
        string sourceApi = probe.FileExists(backupApi) ? backupApi : gameApi;
        string? sourceEssentials = probe.FileExists(backupEssentials) ? backupEssentials
            : probe.FileExists(gameEssentials) ? gameEssentials : null;
        string? sourceSurvival = probe.FileExists(backupSurvival) ? backupSurvival
            : probe.FileExists(gameSurvival) ? gameSurvival : null;

        string stagingDir = Path.Combine(gameDir, ".optimum", $"staging-{Guid.NewGuid():N}");
        EnsureDirectory(stagingDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build targets in canonical manifest order. Each target stages into its own
            // isolated subdirectory so Cecil's output-directory resolver search never observes
            // a sibling worker's in-progress DLL or PDB.
            var targets = new List<PatchTargetSpec>(4);
            string stagedLib = Path.Combine(stagingDir, "lib", "VintagestoryLib.dll");
            targets.Add(new PatchTargetSpec(
                CanonicalIndex: targets.Count,
                SchedulePriority: 0,
                AssemblyName: "VintagestoryLib.dll",
                SourcePath: sourceLib,
                LivePath: gameLib,
                StagedPath: stagedLib,
                PatcherArgs: [sourceLib, donorLib, stagedLib]));

            string stagedApi = Path.Combine(stagingDir, "api", "VintagestoryAPI.dll");
            targets.Add(new PatchTargetSpec(
                CanonicalIndex: targets.Count,
                SchedulePriority: 1,
                AssemblyName: "VintagestoryAPI.dll",
                SourcePath: sourceApi,
                LivePath: gameApi,
                StagedPath: stagedApi,
                PatcherArgs: ["--api", sourceApi, donorApi, stagedApi]));

            if (sourceEssentials is not null && donorEssentials is not null)
            {
                string stagedEssentials = Path.Combine(stagingDir, "vsessentials", "VSEssentials.dll");
                targets.Add(new PatchTargetSpec(
                    CanonicalIndex: targets.Count,
                    SchedulePriority: 3,
                    AssemblyName: "Mods/VSEssentials.dll",
                    SourcePath: sourceEssentials,
                    LivePath: gameEssentials,
                    StagedPath: stagedEssentials,
                    PatcherArgs: ["--mod", "vsessentials", sourceEssentials, donorEssentials, stagedEssentials]));
            }

            if (sourceSurvival is not null && donorSurvival is not null)
            {
                string stagedSurvival = Path.Combine(stagingDir, "vssurvivalmod", "VSSurvivalMod.dll");
                targets.Add(new PatchTargetSpec(
                    CanonicalIndex: targets.Count,
                    SchedulePriority: 2,
                    AssemblyName: "Mods/VSSurvivalMod.dll",
                    SourcePath: sourceSurvival,
                    LivePath: gameSurvival,
                    StagedPath: stagedSurvival,
                    PatcherArgs: ["--mod", "vssurvivalmod", sourceSurvival, donorSurvival, stagedSurvival]));
            }

            foreach (PatchTargetSpec target in targets)
            {
                EnsureDirectory(Path.GetDirectoryName(target.StagedPath)!);
            }

            InProcessPatcherInvoker? inProcessInvoker = TryResolveInProcessPatcher(
                patcherPath, isDll, out string? inProcessLoadError);
            if (inProcessLoadError is not null)
            {
                return PatchResult.Failure(FailureReason.PatchConflict,
                    $"failed to patch {targets[0].AssemblyName} (exit 1): {inProcessLoadError}");
            }

            (StagedTargetResult?[] stagedResults, PatchResult? stagingFailure) = await StageTargetsAsync(
                targets,
                patcherPath,
                isDll,
                inProcessInvoker,
                observer,
                cancellationToken).ConfigureAwait(false);

            if (stagingFailure is not null)
            {
                return stagingFailure;
            }

            var patchedTargets = new List<string>(targets.Count);
            var targetRecords = new List<PatchTargetRecord>(targets.Count);
            var stagedSwaps = new List<(string StagedPath, string LivePath, string StagedPdbPath, string LivePdbPath)>(targets.Count);

            for (int i = 0; i < targets.Count; i++)
            {
                StagedTargetResult? staged = stagedResults[i];
                if (staged is null)
                {
                    return PatchResult.Failure(FailureReason.VerificationFailed,
                        $"staged assembly missing before commit: {Path.GetFileName(targets[i].StagedPath)}");
                }

                stagedSwaps.Add((
                    staged.Spec.StagedPath,
                    staged.Spec.LivePath,
                    Path.ChangeExtension(staged.Spec.StagedPath, ".pdb"),
                    Path.ChangeExtension(staged.Spec.LivePath, ".pdb")));
                patchedTargets.Add(staged.Spec.AssemblyName);
                targetRecords.Add(new PatchTargetRecord
                {
                    Assembly = staged.Spec.AssemblyName,
                    VanillaHash = staged.VanillaHash,
                    PatchedHash = staged.PatchedHash,
                });
            }

            // Validate staged outputs before touching live game files
            foreach (var swap in stagedSwaps)
            {
                if (!probe.FileExists(swap.StagedPath))
                {
                    return PatchResult.Failure(FailureReason.VerificationFailed,
                        $"staged assembly missing before commit: {Path.GetFileName(swap.StagedPath)}");
                }
            }

            // Commit and validate transactionally: any failure restores vanilla backups
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var swap in stagedSwaps)
                {
                    MoveFile(swap.StagedPath, swap.LivePath);
                    if (probe.FileExists(swap.StagedPdbPath))
                        MoveFile(swap.StagedPdbPath, swap.LivePdbPath);
                    FailAtCommitStep?.Invoke("swap");
                }

                // Deploy contracts, shaders, lang strings, launcher
                observer?.Phase(ProgressPhase.Patch, 90, "deploying contracts and overlay assets");
                DeployOverlayAssets(overlayDir, gameDir, observer);
                FailAtCommitStep?.Invoke("overlay");

                // Write marker and manifest
                string optimumDir = Path.Combine(gameDir, ".optimum");
                EnsureDirectory(optimumDir);
                WriteText(Path.Combine(optimumDir, "version"), CoreInfo.Version);

                var manifest = new PatchInstallManifest
                {
                    OptimumVersion = CoreInfo.Version,
                    PatchedAtUtc = DateTimeOffset.UtcNow,
                    GameDirectory = gameDir,
                    Targets = targetRecords,
                };
                WriteText(Path.Combine(gameDir, PatchInstallManifest.RelativePath), manifest.Serialize());
                FailAtCommitStep?.Invoke("manifest");

                // Validate patched runtime
                observer?.Phase(ProgressPhase.Verify, 95, "validating patched runtime");
                RuntimeValidationResult validation = _validator.Validate(gameDir);
                if (!validation.Ok)
                {
                    Rollback(gameDir, observer);
                    return PatchResult.Failure(FailureReason.VerificationFailed,
                        validation.Detail ?? "runtime validation failed");
                }
            }
            catch (Exception ex)
            {
                Rollback(gameDir, observer);
                return PatchResult.Failure(FailureReason.EngineInternal,
                    $"patch commit failed and was rolled back: {ex.Message}");
            }

            observer?.Phase(ProgressPhase.Verify, 99, "patch complete");
            observer?.Log(LogLevel.Info, $"successfully patched {patchedTargets.Count} assemblies at {gameDir}");
            return PatchResult.Success(gameDir, patchedTargets);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private async Task<(StagedTargetResult?[] Results, PatchResult? Failure)> StageTargetsAsync(
        IReadOnlyList<PatchTargetSpec> targets,
        string patcherPath,
        bool isDll,
        InProcessPatcherInvoker? inProcessInvoker,
        IBuildObserver? observer,
        CancellationToken cancellationToken)
    {
        var results = new StagedTargetResult?[targets.Count];
        var failures = new PatchResult?[targets.Count];
        int workerCount = Math.Clamp(MaxStagingConcurrency, 1, targets.Count);

        // Schedule VintagestoryLib and VintagestoryAPI first (separate patcher pipelines
        // avoid cold-JIT contention), then VSSurvivalMod before VSEssentials so the
        // two workers finish near-simultaneously while outputs stay in canonical order.
        var workQueue = new ConcurrentQueue<PatchTargetSpec>(
            workerCount == 1
                ? targets
                : targets.OrderBy(t => t.SchedulePriority));

        var observerGate = new Lock();
        int completedCount = 0;
        int currentProgress = 20;

        void ReportProgress(string message, bool incrementCompleted)
        {
            if (observer is null)
                return;

            lock (observerGate)
            {
                if (incrementCompleted)
                {
                    completedCount++;
                    currentProgress = 20 + (68 * completedCount / targets.Count);
                }
                observer.Phase(ProgressPhase.Patch, currentProgress, message);
            }
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? unexpectedException = null;

        void RunWorker()
        {
            while (!linkedCts.IsCancellationRequested && workQueue.TryDequeue(out PatchTargetSpec? target))
            {
                if (linkedCts.IsCancellationRequested)
                    break;

                try
                {
                    ReportProgress($"patching {target.AssemblyName}", incrementCompleted: false);
                    ProcessOutcome outcome = RunPatcherCore(patcherPath, isDll, target.PatcherArgs, inProcessInvoker);
                    if (!outcome.Started || outcome.ExitCode != 0 || !probe.FileExists(target.StagedPath))
                    {
                        failures[target.CanonicalIndex] = PatchResult.Failure(
                            FailureReason.PatchConflict,
                            $"failed to patch {target.AssemblyName} (exit {outcome.ExitCode}): {TrimOutput(outcome)}");
                        linkedCts.Cancel();
                        return;
                    }

                    string vanillaHash = ComputeSha256(target.SourcePath);
                    string patchedHash = ExtractPatchedHash(outcome.StandardOutput) ?? ComputeSha256(target.StagedPath);
                    results[target.CanonicalIndex] = new StagedTargetResult(target, vanillaHash, patchedHash);
                    ReportProgress($"patched {target.AssemblyName}", incrementCompleted: true);
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref unexpectedException, ExceptionDispatchInfo.Capture(ex), null);
                    linkedCts.Cancel();
                    return;
                }
            }
        }

        if (workerCount == 1)
        {
            RunWorker();
        }
        else
        {
            var workerTasks = new Task[workerCount];
            for (int w = 0; w < workerCount; w++)
            {
                // Run on the thread pool and always await every started worker before returning,
                // so the caller's finally block never deletes stagingDir while a worker writes to it.
                workerTasks[w] = Task.Run(RunWorker, CancellationToken.None);
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }

        // Preserve external cancellation semantics only after every started worker has stopped.
        cancellationToken.ThrowIfCancellationRequested();

        if (unexpectedException is not null)
        {
            unexpectedException.Throw();
        }

        for (int i = 0; i < failures.Length; i++)
        {
            if (failures[i] is not null)
            {
                return (results, failures[i]);
            }
        }

        return (results, null);
    }

    /// <summary>
    /// Normalises an already-absolute path without rewriting it against the host
    /// drive. <see cref="Path.GetFullPath(string)"/> would turn a POSIX path like
    /// <c>/game</c> into <c>C:\game</c> on Windows, which is wrong whenever the
    /// probe models another platform (and in cross-platform tests). Relative
    /// paths are rejected by the caller before this runs, so the input is always
    /// rooted; collapse only redundant separators.
    /// </summary>
    private static string NormalizeAbsolute(string path) =>
        path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    private PatchResult Rollback(string gameDir, IBuildObserver? observer)
    {
        string vanillaBackupDir = Path.Combine(gameDir, ".optimum", "vanilla");
        string backupLib = Path.Combine(vanillaBackupDir, "VintagestoryLib.vanilla.dll");
        string backupApi = Path.Combine(vanillaBackupDir, "VintagestoryAPI.vanilla.dll");
        string backupEssentials = Path.Combine(vanillaBackupDir, "Mods", "VSEssentials.dll");
        string backupSurvival = Path.Combine(vanillaBackupDir, "Mods", "VSSurvivalMod.dll");

        if (!probe.FileExists(backupLib) && !probe.FileExists(backupApi))
        {
            return PatchResult.Failure(FailureReason.BadInput,
                "no vanilla backup found to rollback: .optimum/vanilla is missing or incomplete");
        }

        var restored = new List<string>();

        if (probe.FileExists(backupLib))
        {
            CopyFile(backupLib, Path.Combine(gameDir, "VintagestoryLib.dll"));
            restored.Add("VintagestoryLib.dll");
        }

        if (probe.FileExists(backupApi))
        {
            CopyFile(backupApi, Path.Combine(gameDir, "VintagestoryAPI.dll"));
            restored.Add("VintagestoryAPI.dll");
        }

        if (probe.FileExists(backupEssentials))
        {
            CopyFile(backupEssentials, Path.Combine(gameDir, "Mods", "VSEssentials.dll"));
            restored.Add("Mods/VSEssentials.dll");
        }

        if (probe.FileExists(backupSurvival))
        {
            CopyFile(backupSurvival, Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll"));
            restored.Add("Mods/VSSurvivalMod.dll");
        }

        RestoreGameContentAsset(gameDir);
        RestoreCairoWrapperAsset(gameDir);
        RestoreFileAsset(gameDir, "Optimum.Api.Contracts.dll");
        string launcherName = probe.Os == OsKind.Windows ? "Optimum.exe" : "Optimum";
        RestoreFileAsset(gameDir, launcherName);
        RestoreDirectoryAssets(gameDir, Path.Combine("assets", "game", "lang"), restored);
        RestoreDirectoryAssets(gameDir, Path.Combine("assets", "darkvision", "config", "shaderpatches"));
        TryDeleteFile(Path.Combine(gameDir, ".optimum", "version"));
        TryDeleteFile(Path.Combine(gameDir, PatchInstallManifest.RelativePath));

        observer?.Log(LogLevel.Info, $"rollback complete: restored {restored.Count} vanilla assemblies");
        return PatchResult.Success(gameDir, restored);
    }

    private bool CopySafeFile(string source, string destination, string gameDir, IBuildObserver? observer = null)
    {
        if (!SymlinkComponentCheck.IsContainedWithinRoot(probe, destination, gameDir, out string? escaping))
        {
            observer?.Log(LogLevel.Warn,
                $"Refusing to write to {destination}: destination path escapes game root {gameDir} via symbolic link at {escaping}");
            return false;
        }

        CopyFile(source, destination);
        return true;
    }

    private void DeployOverlayAssets(string? overlayDir, string gameDir, IBuildObserver? observer)
    {
        // 1. Copy Optimum.Api.Contracts.dll to gameDir
        string? contractsSrc = ResolveDonor(overlayDir, gameDir, "Optimum.Api.Contracts.dll", "VintagestoryAPI.Contracts.dll");
        if (contractsSrc is not null && probe.FileExists(contractsSrc))
        {
            BackupFileAsset(gameDir, "Optimum.Api.Contracts.dll");
            CopySafeFile(contractsSrc, Path.Combine(gameDir, "Optimum.Api.Contracts.dll"), gameDir, observer);
        }

        // 1b. The patched API calls Skia helpers in the candidate Cairo wrapper.
        // The overlay carries the same wrapper beside its API donor for Cecil
        // patching and in game/Lib for runtime assembly resolution.
        string? cairoSrc = FindFile(overlayDir, ".optimum/donors/cairo-sharp.dll", "cairo-sharp.dll");
        if (cairoSrc is not null && probe.FileExists(cairoSrc))
        {
            string cairoDst = Path.Combine(gameDir, "Lib", "cairo-sharp.dll");
            BackupCairoWrapperAsset(gameDir, cairoDst);
            CopySafeFile(cairoSrc, cairoDst, gameDir, observer);
        }

        // 2. Copy the assembly referenced by the patched VSEssentials.dll.
        string? gameContentSrc = FindFile(overlayDir, "Optimum.GameContent.dll");
        if (gameContentSrc is not null && probe.FileExists(gameContentSrc))
        {
            string gameContentDst = Path.Combine(gameDir, "Optimum.GameContent.dll");
            BackupGameContentAsset(gameDir, gameContentDst);
            CopySafeFile(gameContentSrc, gameContentDst, gameDir, observer);
        }

        // 3. Overlay shaders
        string? shadersSrc = FindDirectory(overlayDir, "assets/game/shaders", "shaders");
        if (shadersSrc is not null && probe.DirectoryExists(shadersSrc))
        {
            string shadersDst = Path.Combine(gameDir, "assets", "game", "shaders");
            if (!SymlinkComponentCheck.IsContainedWithinRoot(probe, shadersDst, gameDir, out string? escaping))
            {
                observer?.Log(LogLevel.Warn,
                    $"Refusing to deploy shaders to {shadersDst}: destination path escapes game root {gameDir} via symbolic link at {escaping}");
            }
            else
            {
                EnsureDirectory(shadersDst);
                foreach (string file in probe.EnumerateFiles(shadersSrc, "*"))
                {
                    CopySafeFile(file, Path.Combine(shadersDst, Path.GetFileName(file)), gameDir, observer);
                }
            }
        }

        // 4. Merge language files
        string? langSrc = FindDirectory(overlayDir, "assets/game/lang", "lang");
        if (langSrc is not null && probe.DirectoryExists(langSrc))
        {
            string langDst = Path.Combine(gameDir, "assets", "game", "lang");
            if (!SymlinkComponentCheck.IsContainedWithinRoot(probe, langDst, gameDir, out string? escaping))
            {
                observer?.Log(LogLevel.Warn,
                    $"Refusing to deploy language files to {langDst}: destination path escapes game root {gameDir} via symbolic link at {escaping}");
            }
            else
            {
                EnsureDirectory(langDst);
                foreach (string file in probe.EnumerateFiles(langSrc, "*.json"))
                {
                    string dstFile = Path.Combine(langDst, Path.GetFileName(file));
                    if (SymlinkComponentCheck.IsContainedWithinRoot(probe, dstFile, gameDir, out _))
                    {
                        BackupFileAsset(gameDir, Path.Combine("assets", "game", "lang", Path.GetFileName(file)));
                        MergeJsonFile(file, dstFile);
                    }
                }
            }
        }

        // 5. DarkVision compatibility shader patches
        string? darkVisionPatchesSrc = FindDirectory(overlayDir, "assets/darkvision/config/shaderpatches");
        if (darkVisionPatchesSrc is not null && probe.DirectoryExists(darkVisionPatchesSrc))
        {
            string darkVisionPatchesDst = Path.Combine(gameDir, "assets", "darkvision", "config", "shaderpatches");
            if (!SymlinkComponentCheck.IsContainedWithinRoot(probe, darkVisionPatchesDst, gameDir, out string? escaping))
            {
                observer?.Log(LogLevel.Warn,
                    $"Refusing to deploy darkvision patches to {darkVisionPatchesDst}: destination path escapes game root {gameDir} via symbolic link at {escaping}");
            }
            else
            {
                EnsureDirectory(darkVisionPatchesDst);
                foreach (string file in probe.EnumerateFiles(darkVisionPatchesSrc, "*.txt"))
                {
                    string fileName = Path.GetFileName(file);
                    BackupFileAsset(gameDir, Path.Combine("assets", "darkvision", "config", "shaderpatches", fileName));
                    CopySafeFile(file, Path.Combine(darkVisionPatchesDst, fileName), gameDir, observer);
                }
            }
        }

        // 6. Launcher wrapper
        string launcherName = probe.Os == OsKind.Windows ? "Optimum.exe" : "Optimum";
        string? launcherSrc = FindFile(overlayDir, launcherName);
        if (launcherSrc is not null && probe.FileExists(launcherSrc))
        {
            string launcherDst = Path.Combine(gameDir, launcherName);
            BackupFileAsset(gameDir, launcherName);
            if (CopySafeFile(launcherSrc, launcherDst, gameDir, observer))
            {
                MakeExecutable(launcherDst);
            }
        }

        // If run.sh exists, ensure executable
        string runSh = Path.Combine(gameDir, "run.sh");
        if (probe.FileExists(runSh))
        {
            MakeExecutable(runSh);
        }
    }

    private void BackupFileAsset(string gameDir, string relativePath)
    {
        string vanillaDir = Path.Combine(gameDir, ".optimum", "vanilla");
        string targetPath = Path.Combine(gameDir, relativePath);
        string backupPath = Path.Combine(vanillaDir, relativePath);
        string absentMarker = Path.Combine(vanillaDir, relativePath + ".absent");

        if (probe.FileExists(backupPath) || probe.FileExists(absentMarker))
            return;

        EnsureDirectory(Path.GetDirectoryName(backupPath)!);
        if (probe.FileExists(targetPath))
            CopyFile(targetPath, backupPath);
        else
            WriteText(absentMarker, string.Empty);
    }

    private void RestoreFileAsset(string gameDir, string relativePath)
    {
        string vanillaDir = Path.Combine(gameDir, ".optimum", "vanilla");
        string targetPath = Path.Combine(gameDir, relativePath);
        string backupPath = Path.Combine(vanillaDir, relativePath);
        string absentMarker = Path.Combine(vanillaDir, relativePath + ".absent");

        if (probe.FileExists(backupPath))
        {
            CopyFile(backupPath, targetPath);
            TryDeleteFile(backupPath);
        }
        else if (probe.FileExists(absentMarker))
        {
            TryDeleteFile(targetPath);
        }

        TryDeleteFile(absentMarker);
    }

    private void RestoreDirectoryAssets(string gameDir, string relativeDir, List<string>? restored = null)
    {
        string backupDir = Path.Combine(gameDir, ".optimum", "vanilla", relativeDir);
        if (!probe.DirectoryExists(backupDir)) return;

        string targetDir = Path.Combine(gameDir, relativeDir);
        foreach (string file in probe.EnumerateFiles(backupDir, "*"))
        {
            if (file.EndsWith(".absent", StringComparison.OrdinalIgnoreCase))
            {
                string targetFileName = Path.GetFileName(file[..^7]);
                string targetPath = Path.Combine(targetDir, targetFileName);
                TryDeleteFile(targetPath);
                TryDeleteFile(file);
            }
            else
            {
                string targetFileName = Path.GetFileName(file);
                string targetPath = Path.Combine(targetDir, targetFileName);
                CopyFile(file, targetPath);
                TryDeleteFile(file);
                restored?.Add(Path.Combine(relativeDir, targetFileName).Replace('\\', '/'));
            }
        }
    }

    private void BackupGameContentAsset(string gameDir, string gameContentPath)
    {
        BackupFileAsset(gameDir, "Optimum.GameContent.dll");
    }

    private void RestoreGameContentAsset(string gameDir)
    {
        RestoreFileAsset(gameDir, "Optimum.GameContent.dll");
    }

    private void BackupCairoWrapperAsset(string gameDir, string cairoPath)
    {
        BackupFileAsset(gameDir, Path.Combine("Lib", "cairo-sharp.dll"));
    }

    private void RestoreCairoWrapperAsset(string gameDir)
    {
        RestoreFileAsset(gameDir, Path.Combine("Lib", "cairo-sharp.dll"));
    }

    private void MergeJsonFile(string srcFile, string dstFile)
    {
        try
        {
            string? srcText = probe.ReadText(srcFile);
            if (string.IsNullOrWhiteSpace(srcText)) return;

            if (!probe.FileExists(dstFile))
            {
                CopyFile(srcFile, dstFile);
                return;
            }

            string? dstText = probe.ReadText(dstFile);
            if (string.IsNullOrWhiteSpace(dstText))
            {
                CopyFile(srcFile, dstFile);
                return;
            }

            var srcNode = JsonNode.Parse(srcText) as JsonObject;
            var dstNode = JsonNode.Parse(dstText) as JsonObject;
            if (srcNode is null || dstNode is null)
            {
                return;
            }

            foreach (var kvp in srcNode)
            {
                dstNode[kvp.Key] = kvp.Value?.DeepClone();
            }

            string tempFile = $"{dstFile}.tmp-{Guid.NewGuid():N}";
            try
            {
                WriteText(tempFile, dstNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                MoveFile(tempFile, dstFile);
            }
            finally
            {
                TryDeleteFile(tempFile);
            }
        }
        catch
        {
            // On JSON syntax failure or write error, preserve pristine original file untouched.
        }
    }

    private (string? Path, bool IsDll) ResolvePatcher(string? overlayDir)
    {
        var dirs = new List<string>();
        if (overlayDir is not null)
        {
            dirs.Add(overlayDir);
            dirs.Add(Path.Combine(overlayDir, "patcher"));
        }
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "patcher"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "Optimum.Patcher", "bin", "Release", "net10.0"));

        foreach (string dir in dirs)
        {
            // On Windows the patcher may ship as a native .exe or, in dev and test
            // setups, a .bat/.cmd wrapper; accept either. On Unix it is the
            // extensionless executable.
            string[] exeNames = probe.Os == OsKind.Windows
                ? ["Optimum.Patcher.exe", "Optimum.Patcher.bat", "Optimum.Patcher.cmd"]
                : ["Optimum.Patcher"];
            foreach (string exeName in exeNames)
            {
                string exePath = Path.Combine(dir, exeName);
                if (probe.FileExists(exePath) && (probe.Os == OsKind.Windows || probe.IsExecutable(exePath)))
                    return (exePath, false);
            }

            string dllPath = Path.Combine(dir, "Optimum.Patcher.dll");
            if (probe.FileExists(dllPath))
                return (dllPath, true);
        }

        return (null, false);
    }

    private string? ResolveDonor(string? overlayDir, string gameDir, string primaryName, params string[] fallbackNames)
    {
        var names = new List<string> { primaryName };
        names.AddRange(fallbackNames);

        var dirs = new List<string>();
        if (overlayDir is not null)
        {
            dirs.Add(Path.Combine(overlayDir, ".optimum", "donors"));
            dirs.Add(Path.Combine(overlayDir, "donors"));
            dirs.Add(overlayDir);
        }
        dirs.Add(Path.Combine(gameDir, ".optimum", "donors"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, ".optimum", "donors"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "donors"));
        dirs.Add(AppContext.BaseDirectory);
        // Dev fallback locations
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "bin", "Release", "net10.0"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "build", "VintagestoryLib", "bin", "Release", "net10.0"));

        foreach (string dir in dirs)
        {
            foreach (string name in names)
            {
                string p = Path.Combine(dir, name);
                if (probe.FileExists(p))
                    return p;
            }
        }
        return null;
    }

    private string? FindDirectory(string? overlayDir, params string[] relativeCandidates)
    {
        var baseDirs = new List<string>();
        if (overlayDir is not null)
            baseDirs.Add(overlayDir);
        baseDirs.Add(AppContext.BaseDirectory);

        foreach (string baseDir in baseDirs)
        {
            foreach (string rel in relativeCandidates)
            {
                string p = Path.Combine(baseDir, rel);
                if (probe.DirectoryExists(p))
                    return p;
            }
        }
        return null;
    }

    private string? FindFile(string? overlayDir, params string[] relativeCandidates)
    {
        var baseDirs = new List<string>();
        if (overlayDir is not null)
            baseDirs.Add(overlayDir);
        baseDirs.Add(AppContext.BaseDirectory);

        foreach (string baseDir in baseDirs)
        {
            foreach (string rel in relativeCandidates)
            {
                string p = Path.Combine(baseDir, rel);
                if (probe.FileExists(p))
                    return p;
            }
        }
        return null;
    }

    internal ProcessOutcome RunPatcher(string patcherPath, bool isDll, IReadOnlyList<string> args)
    {
        InProcessPatcherInvoker? invoker = TryResolveInProcessPatcher(patcherPath, isDll, out string? loadError);
        if (loadError is not null)
        {
            return new ProcessOutcome(Started: true, ExitCode: 1, StandardOutput: string.Empty, StandardError: loadError);
        }

        return RunPatcherCore(patcherPath, isDll, args, invoker);
    }

    private static InProcessPatcherInvoker? TryResolveInProcessPatcher(
        string patcherPath,
        bool isDll,
        out string? loadError)
    {
        loadError = null;
        if (!isDll || !Path.GetFileName(patcherPath).Equals("Optimum.Patcher.dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(patcherPath))
        {
            return null;
        }

        try
        {
            Assembly asm = Assembly.LoadFrom(patcherPath);
            Type? entryType = asm.GetType("Optimum.Patcher.Program") ?? asm.GetType("Program");
            const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            MethodInfo? runWithHashMethod = entryType?.GetMethod(
                "Run",
                StaticFlags,
                binder: null,
                types: [typeof(string[]), typeof(string).MakeByRefType()],
                modifiers: null);
            if (runWithHashMethod is not null)
            {
                return new InProcessPatcherInvoker(runWithHashMethod, HasHashOutParam: true);
            }

            MethodInfo? runMethod = entryType?.GetMethod("Run", StaticFlags, binder: null, types: [typeof(string[])], modifiers: null)
                ?? entryType?.GetMethod("Main", StaticFlags, binder: null, types: [typeof(string[])], modifiers: null);
            if (runMethod is not null)
            {
                return new InProcessPatcherInvoker(runMethod, HasHashOutParam: false);
            }

            return null;
        }
        catch (Exception ex)
        {
            loadError = (ex.InnerException ?? ex).Message;
            return null;
        }
    }

    private ProcessOutcome RunPatcherCore(
        string patcherPath,
        bool isDll,
        IReadOnlyList<string> args,
        InProcessPatcherInvoker? inProcessInvoker)
    {
        if (inProcessInvoker is not null)
        {
            try
            {
                string[] runnerArgs = [.. args];
                if (inProcessInvoker.HasHashOutParam)
                {
                    object?[] invokeArgs = [runnerArgs, null];
                    object? result = inProcessInvoker.Method.Invoke(null, invokeArgs);
                    int exitCode = result is int code ? code : 0;
                    string? patchedHash = exitCode == 0 ? invokeArgs[1] as string : null;
                    string standardOutput = !string.IsNullOrEmpty(patchedHash)
                        ? $"PATCHED SHA256: {patchedHash}"
                        : string.Empty;
                    return new ProcessOutcome(Started: true, ExitCode: exitCode, StandardOutput: standardOutput, StandardError: string.Empty);
                }

                object? legacyResult = inProcessInvoker.Method.Invoke(null, [runnerArgs]);
                int legacyExitCode = legacyResult is int legacyCode ? legacyCode : 0;
                return new ProcessOutcome(Started: true, ExitCode: legacyExitCode, StandardOutput: string.Empty, StandardError: string.Empty);
            }
            catch (Exception ex)
            {
                Exception root = ex.InnerException ?? ex;
                return new ProcessOutcome(Started: true, ExitCode: 1, StandardOutput: string.Empty, StandardError: root.Message);
            }
        }

        if (isDll)
        {
            var commandArgs = new List<string> { patcherPath };
            commandArgs.AddRange(args);
            return probe.Run("dotnet", commandArgs, TimeSpan.FromMinutes(5));
        }

        // A .bat/.cmd cannot be launched directly when UseShellExecute is false;
        // route it through the command interpreter. Native executables run as-is.
        if (probe.Os == OsKind.Windows
            && (patcherPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                || patcherPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            var commandArgs = new List<string> { "/c", patcherPath };
            commandArgs.AddRange(args);
            return probe.Run("cmd.exe", commandArgs, TimeSpan.FromMinutes(5));
        }

        return probe.Run(patcherPath, args, TimeSpan.FromMinutes(5));
    }

    private void CopyFile(string source, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private void MoveFile(string source, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination, overwrite: true);
    }

    private void WriteText(string path, string content)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void EnsureDirectory(string directory)
    {
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    private void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* best effort */ }
    }

    internal static string? ExtractPatchedHash(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;

        const string prefix = "PATCHED SHA256: ";
        int index = output.LastIndexOf(prefix, StringComparison.Ordinal);
        if (index < 0) return null;

        int start = index + prefix.Length;
        int end = output.IndexOfAny(['\r', '\n'], start);
        string candidate = (end < 0 ? output[start..] : output[start..end]).Trim();
        return candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private string ComputeSha256(string path)
    {
        Action<string>? onComputeSha256 = OnComputeSha256;
        if (onComputeSha256 is not null)
        {
            lock (hashCallbackGate)
                onComputeSha256(path);
        }
        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return "sha256:" + Convert.ToHexStringLower(hash);
        }
        catch
        {
            return "";
        }
    }

    internal static string? DetectGameVersion(string libPath)
    {
        try
        {
            if (!File.Exists(libPath)) return null;
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(libPath);
            string? version = info.FileVersion?.Trim();
            if (string.IsNullOrEmpty(version))
                version = info.ProductVersion?.Trim();
            if (string.IsNullOrEmpty(version))
                return null;

            return NormalizeGameVersion(version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reduces a file version to the form release manifests use. A three-part
    /// version such as the "1.22.7" that Vintage Story ships in
    /// VintagestoryLib.dll keeps its patch number, and a fourth part of zero is dropped.
    /// </summary>
    internal static string NormalizeGameVersion(string version)
    {
        version = version.Trim();
        int plusIndex = version.IndexOf('+');
        if (plusIndex >= 0) version = version[..plusIndex];

        if (!Version.TryParse(version, out var parsed))
            return version;

        if (parsed.Build >= 0 && parsed.Revision > 0)
            return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}.{parsed.Revision}";
        if (parsed.Build >= 0)
            return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        return $"{parsed.Major}.{parsed.Minor}";
    }

    private static string TrimOutput(ProcessOutcome outcome)
    {
        string err = outcome.StandardError.Trim();
        if (!string.IsNullOrEmpty(err))
            return err.Length <= 300 ? err : err[..300];
        string outText = outcome.StandardOutput.Trim();
        return outText.Length <= 300 ? outText : outText[..300];
    }
}
