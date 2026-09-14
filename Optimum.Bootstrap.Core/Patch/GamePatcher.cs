using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

public sealed record PatchRequest(
    string GameDirectory,
    string? OverlayDirectory = null,
    bool Backup = true,
    bool Rollback = false);

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
public sealed class GamePatcher(ISystemProbe probe, IRuntimeValidator? validator = null) : IGamePatcher
{
    private readonly IRuntimeValidator _validator = validator ?? new RuntimeValidator(probe);

    public Task<PatchResult> PatchAsync(
        PatchRequest request,
        IBuildObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.GameDirectory))
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput, "--game-dir is required"));

        if (!Path.IsPathRooted(request.GameDirectory))
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput, $"--game-dir must be an absolute path: {request.GameDirectory}"));

        string gameDir = Path.GetFullPath(request.GameDirectory);
        if (!probe.DirectoryExists(gameDir))
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput, $"the game directory does not exist: {gameDir}"));

        // Rollback flow
        if (request.Rollback)
        {
            return Task.FromResult(Rollback(gameDir, observer));
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
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput,
                $"the game directory does not appear to be a Vintage Story install (missing VintagestoryLib.dll and VintagestoryAPI.dll): {gameDir}"));
        }

        // Resolve overlay directory
        string? overlayDir = null;
        if (request.OverlayDirectory is not null)
        {
            if (!Path.IsPathRooted(request.OverlayDirectory))
                return Task.FromResult(PatchResult.Failure(FailureReason.BadInput, $"--overlay must be an absolute path: {request.OverlayDirectory}"));

            overlayDir = Path.GetFullPath(request.OverlayDirectory);
            if (!probe.DirectoryExists(overlayDir))
                return Task.FromResult(PatchResult.Failure(FailureReason.BadInput, $"the overlay directory does not exist: {overlayDir}"));
        }

        // Resolve patcher tool
        (string? patcherPath, bool isDll) = ResolvePatcher(overlayDir);
        if (patcherPath is null)
        {
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput,
                "Optimum.Patcher executable or assembly not found in overlay or application directory."));
        }

        // Resolve donor assemblies
        string? donorLib = ResolveDonor(overlayDir, gameDir, "VintagestoryLib.Donor.dll", "VintagestoryLib.dll");
        if (donorLib is null)
        {
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput,
                "VintagestoryLib.Donor.dll not found in overlay or donors directory."));
        }

        string? donorApi = ResolveDonor(overlayDir, gameDir, "VintagestoryAPI.Contracts.dll", "Optimum.Api.Contracts.dll");
        if (donorApi is null)
        {
            return Task.FromResult(PatchResult.Failure(FailureReason.BadInput,
                "VintagestoryAPI.Contracts.dll not found in overlay or donors directory."));
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

        var patchedTargets = new List<string>();
        var targetRecords = new List<PatchTargetRecord>();

        // Patch Target 1: VintagestoryLib.dll (Transplant)
        cancellationToken.ThrowIfCancellationRequested();
        observer?.Phase(ProgressPhase.Patch, 20, "patching VintagestoryLib.dll");
        string tempLib = Path.Combine(gameDir, $"VintagestoryLib.dll.tmp-{Guid.NewGuid():N}");
        try
        {
            ProcessOutcome outcome = RunPatcher(patcherPath, isDll, [sourceLib, donorLib, tempLib]);
            if (!outcome.Started || outcome.ExitCode != 0 || !probe.FileExists(tempLib))
            {
                return Task.FromResult(PatchResult.Failure(FailureReason.PatchConflict,
                    $"failed to patch VintagestoryLib.dll (exit {outcome.ExitCode}): {TrimOutput(outcome)}"));
            }

            string vanillaLibHash = ComputeSha256(sourceLib);
            string patchedLibHash = ComputeSha256(tempLib);
            MoveFile(tempLib, gameLib);
            string tempLibPdb = Path.ChangeExtension(tempLib, ".pdb");
            if (probe.FileExists(tempLibPdb))
                MoveFile(tempLibPdb, Path.ChangeExtension(gameLib, ".pdb"));

            patchedTargets.Add("VintagestoryLib.dll");
            targetRecords.Add(new PatchTargetRecord
            {
                Assembly = "VintagestoryLib.dll",
                VanillaHash = vanillaLibHash,
                PatchedHash = patchedLibHash,
            });
            observer?.Phase(ProgressPhase.Patch, 40, "patched VintagestoryLib.dll");
        }
        finally
        {
            TryDeleteFile(tempLib);
        }

        // Patch Target 2: VintagestoryAPI.dll (Api)
        cancellationToken.ThrowIfCancellationRequested();
        observer?.Phase(ProgressPhase.Patch, 45, "patching VintagestoryAPI.dll");
        string tempApi = Path.Combine(gameDir, $"VintagestoryAPI.dll.tmp-{Guid.NewGuid():N}");
        try
        {
            ProcessOutcome outcome = RunPatcher(patcherPath, isDll, ["--api", sourceApi, donorApi, tempApi]);
            if (!outcome.Started || outcome.ExitCode != 0 || !probe.FileExists(tempApi))
            {
                return Task.FromResult(PatchResult.Failure(FailureReason.PatchConflict,
                    $"failed to patch VintagestoryAPI.dll (exit {outcome.ExitCode}): {TrimOutput(outcome)}"));
            }

            string vanillaApiHash = ComputeSha256(sourceApi);
            string patchedApiHash = ComputeSha256(tempApi);
            MoveFile(tempApi, gameApi);
            string tempApiPdb = Path.ChangeExtension(tempApi, ".pdb");
            if (probe.FileExists(tempApiPdb))
                MoveFile(tempApiPdb, Path.ChangeExtension(gameApi, ".pdb"));

            patchedTargets.Add("VintagestoryAPI.dll");
            targetRecords.Add(new PatchTargetRecord
            {
                Assembly = "VintagestoryAPI.dll",
                VanillaHash = vanillaApiHash,
                PatchedHash = patchedApiHash,
            });
            observer?.Phase(ProgressPhase.Patch, 65, "patched VintagestoryAPI.dll");
        }
        finally
        {
            TryDeleteFile(tempApi);
        }

        // Patch Target 3: Mods/VSEssentials.dll (Mod)
        if (sourceEssentials is not null && donorEssentials is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observer?.Phase(ProgressPhase.Patch, 70, "patching Mods/VSEssentials.dll");
            string modsDir = Path.Combine(gameDir, "Mods");
            EnsureDirectory(modsDir);
            string tempEssentials = Path.Combine(modsDir, $"VSEssentials.dll.tmp-{Guid.NewGuid():N}");
            try
            {
                ProcessOutcome outcome = RunPatcher(patcherPath, isDll,
                    ["--mod", "vsessentials", sourceEssentials, donorEssentials, tempEssentials]);
                if (outcome.Started && outcome.ExitCode == 0 && probe.FileExists(tempEssentials))
                {
                    string vanillaHash = ComputeSha256(sourceEssentials);
                    string patchedHash = ComputeSha256(tempEssentials);
                    MoveFile(tempEssentials, gameEssentials);
                    string tempPdb = Path.ChangeExtension(tempEssentials, ".pdb");
                    if (probe.FileExists(tempPdb))
                        MoveFile(tempPdb, Path.ChangeExtension(gameEssentials, ".pdb"));

                    patchedTargets.Add("Mods/VSEssentials.dll");
                    targetRecords.Add(new PatchTargetRecord
                    {
                        Assembly = "Mods/VSEssentials.dll",
                        VanillaHash = vanillaHash,
                        PatchedHash = patchedHash,
                    });
                    observer?.Phase(ProgressPhase.Patch, 80, "patched Mods/VSEssentials.dll");
                }
                else
                {
                    observer?.Log(LogLevel.Warn, $"skipping Mods/VSEssentials.dll patch: {TrimOutput(outcome)}");
                }
            }
            finally
            {
                TryDeleteFile(tempEssentials);
            }
        }

        // Patch Target 4: Mods/VSSurvivalMod.dll (Mod)
        if (sourceSurvival is not null && donorSurvival is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observer?.Phase(ProgressPhase.Patch, 82, "patching Mods/VSSurvivalMod.dll");
            string modsDir = Path.Combine(gameDir, "Mods");
            EnsureDirectory(modsDir);
            string tempSurvival = Path.Combine(modsDir, $"VSSurvivalMod.dll.tmp-{Guid.NewGuid():N}");
            try
            {
                ProcessOutcome outcome = RunPatcher(patcherPath, isDll,
                    ["--mod", "vssurvivalmod", sourceSurvival, donorSurvival, tempSurvival]);
                if (outcome.Started && outcome.ExitCode == 0 && probe.FileExists(tempSurvival))
                {
                    string vanillaHash = ComputeSha256(sourceSurvival);
                    string patchedHash = ComputeSha256(tempSurvival);
                    MoveFile(tempSurvival, gameSurvival);
                    string tempPdb = Path.ChangeExtension(tempSurvival, ".pdb");
                    if (probe.FileExists(tempPdb))
                        MoveFile(tempPdb, Path.ChangeExtension(gameSurvival, ".pdb"));

                    patchedTargets.Add("Mods/VSSurvivalMod.dll");
                    targetRecords.Add(new PatchTargetRecord
                    {
                        Assembly = "Mods/VSSurvivalMod.dll",
                        VanillaHash = vanillaHash,
                        PatchedHash = patchedHash,
                    });
                    observer?.Phase(ProgressPhase.Patch, 88, "patched Mods/VSSurvivalMod.dll");
                }
                else
                {
                    observer?.Log(LogLevel.Warn, $"skipping Mods/VSSurvivalMod.dll patch: {TrimOutput(outcome)}");
                }
            }
            finally
            {
                TryDeleteFile(tempSurvival);
            }
        }

        // Deploy contracts, shaders, lang strings, launcher
        cancellationToken.ThrowIfCancellationRequested();
        observer?.Phase(ProgressPhase.Patch, 90, "deploying contracts and overlay assets");
        DeployOverlayAssets(overlayDir, gameDir, observer);

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

        // Validate patched runtime
        observer?.Phase(ProgressPhase.Verify, 95, "validating patched runtime");
        RuntimeValidationResult validation = _validator.Validate(gameDir);
        if (!validation.Ok)
        {
            return Task.FromResult(PatchResult.Failure(FailureReason.VerificationFailed,
                validation.Detail ?? "runtime validation failed"));
        }

        observer?.Phase(ProgressPhase.Verify, 99, "patch complete");
        observer?.Log(LogLevel.Info, $"successfully patched {patchedTargets.Count} assemblies at {gameDir}");
        return Task.FromResult(PatchResult.Success(gameDir, patchedTargets));
    }

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

        observer?.Log(LogLevel.Info, $"rollback complete: restored {restored.Count} vanilla assemblies");
        return PatchResult.Success(gameDir, restored);
    }

    private void DeployOverlayAssets(string? overlayDir, string gameDir, IBuildObserver? observer)
    {
        // 1. Copy Optimum.Api.Contracts.dll to gameDir
        string? contractsSrc = ResolveDonor(overlayDir, gameDir, "Optimum.Api.Contracts.dll", "VintagestoryAPI.Contracts.dll");
        if (contractsSrc is not null && probe.FileExists(contractsSrc))
        {
            CopyFile(contractsSrc, Path.Combine(gameDir, "Optimum.Api.Contracts.dll"));
        }

        // 2. Overlay shaders
        string? shadersSrc = FindDirectory(overlayDir, "assets/game/shaders", "shaders");
        if (shadersSrc is not null && probe.DirectoryExists(shadersSrc))
        {
            string shadersDst = Path.Combine(gameDir, "assets", "game", "shaders");
            EnsureDirectory(shadersDst);
            foreach (string file in probe.EnumerateFiles(shadersSrc, "*"))
            {
                CopyFile(file, Path.Combine(shadersDst, Path.GetFileName(file)));
            }
        }

        // 3. Merge language files
        string? langSrc = FindDirectory(overlayDir, "assets/game/lang", "lang");
        if (langSrc is not null && probe.DirectoryExists(langSrc))
        {
            string langDst = Path.Combine(gameDir, "assets", "game", "lang");
            EnsureDirectory(langDst);
            foreach (string file in probe.EnumerateFiles(langSrc, "*.json"))
            {
                string dstFile = Path.Combine(langDst, Path.GetFileName(file));
                MergeJsonFile(file, dstFile);
            }
        }

        // 4. Launcher wrapper
        string launcherName = probe.Os == OsKind.Windows ? "Optimum.exe" : "Optimum";
        string? launcherSrc = FindFile(overlayDir, launcherName);
        if (launcherSrc is not null && probe.FileExists(launcherSrc))
        {
            string launcherDst = Path.Combine(gameDir, launcherName);
            CopyFile(launcherSrc, launcherDst);
            MakeExecutable(launcherDst);
        }

        // If run.sh exists, ensure executable
        string runSh = Path.Combine(gameDir, "run.sh");
        if (probe.FileExists(runSh))
        {
            MakeExecutable(runSh);
        }
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
                CopyFile(srcFile, dstFile);
                return;
            }

            foreach (var kvp in srcNode)
            {
                dstNode[kvp.Key] = kvp.Value?.DeepClone();
            }

            WriteText(dstFile, dstNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            CopyFile(srcFile, dstFile);
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
            string exeName = probe.Os == OsKind.Windows ? "Optimum.Patcher.exe" : "Optimum.Patcher";
            string exePath = Path.Combine(dir, exeName);
            if (probe.FileExists(exePath) && (probe.Os == OsKind.Windows || probe.IsExecutable(exePath)))
                return (exePath, false);

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

    private ProcessOutcome RunPatcher(string patcherPath, bool isDll, IReadOnlyList<string> args)
    {
        if (isDll)
        {
            var commandArgs = new List<string> { patcherPath };
            commandArgs.AddRange(args);
            return probe.Run("dotnet", commandArgs, TimeSpan.FromMinutes(5));
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

    private static string ComputeSha256(string path)
    {
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

    private static string TrimOutput(ProcessOutcome outcome)
    {
        string err = outcome.StandardError.Trim();
        if (!string.IsNullOrEmpty(err))
            return err.Length <= 300 ? err : err[..300];
        string outText = outcome.StandardOutput.Trim();
        return outText.Length <= 300 ? outText : outText[..300];
    }
}
