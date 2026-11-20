using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class GamePatcherTests
{
    private sealed class StubRuntimeValidator(bool ok = true, string? detail = null) : IRuntimeValidator
    {
        public RuntimeValidationResult Validate(string packageDirectory) => new(ok, detail);
    }

    [Fact]
    public async Task PatchRejectsMissingGameDirectory()
    {
        var probe = new FakeSystemProbe();
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest(""));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("--game-dir is required", result.Message);
    }

    [Fact]
    public async Task PatchRejectsRelativeGameDirectory()
    {
        var probe = new FakeSystemProbe();
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("relative/path"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("absolute path", result.Message);
    }

    [Fact]
    public async Task PatchRejectsNonExistentGameDirectory()
    {
        var probe = new FakeSystemProbe();
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/non/existent/game"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("does not exist", result.Message);
    }

    [Fact]
    public async Task PatchRejectsNonVintageStoryDirectory()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/random.txt", "not vintage story");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("does not appear to be a Vintage Story install", result.Message);
    }

    [Fact]
    public async Task PatchRejectsRelativeOverlayDirectory()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game", OverlayDirectory: "rel/overlay"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("--overlay must be an absolute path", result.Message);
    }

    [Fact]
    public async Task PatchRejectsNonExistentOverlayDirectory()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game", OverlayDirectory: "/no/overlay"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("overlay directory does not exist", result.Message);
    }

    [Fact]
    public async Task PatchRejectsWhenPatcherNotFound()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");
        probe.AddDirectory("/overlay");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game", OverlayDirectory: "/overlay"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("Optimum.Patcher executable or assembly not found", result.Message);
    }

    [Fact]
    public async Task PatchRejectsWhenDonorNotFound()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");
        probe.AddDirectory("/overlay");
        probe.AddFile("/overlay/Optimum.Patcher.dll", "patcher");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game", OverlayDirectory: "/overlay"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("Donor.dll not found", result.Message);
    }

    [Fact]
    public async Task RollbackRejectsWhenNoBackupExists()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");
        var patcher = new GamePatcher(probe, new StubRuntimeValidator());

        var result = await patcher.PatchAsync(new PatchRequest("/game", Rollback: true));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("no vanilla backup found", result.Message);
    }

    [Fact]
    public async Task RealFilesystemRoundTripPatchAndRollback()
    {
        string root = Directory.CreateTempSubdirectory("optimum-patcher-test").FullName;
        try
        {
            string gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));
            Directory.CreateDirectory(Path.Combine(gameDir, "Lib"));
            Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "shaders"));
            Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "lang"));

            File.WriteAllText(Path.Combine(gameDir, "VintagestoryLib.dll"), "vanilla-lib");
            File.WriteAllText(Path.Combine(gameDir, "VintagestoryAPI.dll"), "vanilla-api");
            File.WriteAllText(Path.Combine(gameDir, "Lib", "cairo-sharp.dll"), "vanilla-cairo");
            File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "vanilla-essentials");
            File.WriteAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json"), "{\"vanilla-key\": \"Vanilla Value\"}");
            File.WriteAllText(Path.Combine(gameDir, "run.sh"), "#!/bin/sh\n./Optimum\n");

            string overlayDir = Path.Combine(root, "overlay");
            string donorsDir = Path.Combine(overlayDir, ".optimum", "donors");
            Directory.CreateDirectory(donorsDir);
            Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "shaders"));
            Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "lang"));
            string darkVisionPatchDir = Path.Combine(overlayDir, "assets", "darkvision", "config", "shaderpatches");
            Directory.CreateDirectory(darkVisionPatchDir);

            File.WriteAllText(Path.Combine(donorsDir, "VintagestoryLib.Donor.dll"), "donor-lib");
            File.WriteAllText(Path.Combine(donorsDir, "VintagestoryAPI.Contracts.dll"), "donor-api");
            File.WriteAllText(Path.Combine(donorsDir, "cairo-sharp.dll"), "candidate-cairo");
            File.WriteAllText(Path.Combine(donorsDir, "VSEssentials.Donor.dll"), "donor-essentials");
            File.WriteAllText(Path.Combine(overlayDir, "Optimum.Api.Contracts.dll"), "contracts-dll");
            File.WriteAllText(Path.Combine(overlayDir, "Optimum.GameContent.dll"), "game-content-dll");
            File.WriteAllText(Path.Combine(overlayDir, "Optimum"), "#!/bin/sh\necho optimum\n");
            File.WriteAllText(Path.Combine(overlayDir, "assets", "game", "shaders", "optimum.vsh"), "void main() {}");
            File.WriteAllText(Path.Combine(overlayDir, "assets", "game", "lang", "en.json"), "{\"optimum-key\": \"Optimum Value\"}");
            File.WriteAllText(Path.Combine(darkVisionPatchDir, "optimum_vsp_terrain_desaturation.txt"), "darkvision compatibility patch");

            // Create executable stub patcher
            string patcherExe = Path.Combine(overlayDir, OperatingSystem.IsWindows() ? "Optimum.Patcher.bat" : "Optimum.Patcher");
            if (OperatingSystem.IsWindows())
            {
                // Mirror the Unix stub: write "patched" to the LAST argument,
                // whichever position it is (the lib call passes 3 args, the --api
                // call passes 4). A for-loop keeps the final token in %%t.
                File.WriteAllText(patcherExe,
                    "@echo off\r\nset \"target=\"\r\nfor %%t in (%*) do set \"target=%%~t\"\r\necho patched > \"%target%\"\r\nexit /b 0\r\n");
            }
            else
            {
                File.WriteAllText(patcherExe, "#!/usr/bin/env bash\ntarget=\"${@: -1}\"\necho \"patched\" > \"$target\"\nexit 0\n");
                File.SetUnixFileMode(patcherExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var probe = SystemProbe.Default;
            var patcher = new GamePatcher(probe, new StubRuntimeValidator());

            // 1. Initial Patch
            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.True(result.Ok, result.Message);
            Assert.NotNull(result.PatchedTargets);
            Assert.Contains("VintagestoryLib.dll", result.PatchedTargets);
            Assert.Contains("VintagestoryAPI.dll", result.PatchedTargets);
            Assert.Contains("Mods/VSEssentials.dll", result.PatchedTargets);

            // Verify vanilla backups exist
            Assert.True(File.Exists(Path.Combine(gameDir, ".optimum", "vanilla", "VintagestoryLib.vanilla.dll")));
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, ".optimum", "vanilla", "VintagestoryLib.vanilla.dll")).Trim());
            Assert.True(File.Exists(Path.Combine(gameDir, ".optimum", "vanilla", "VintagestoryAPI.vanilla.dll")));
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, ".optimum", "vanilla", "VintagestoryAPI.vanilla.dll")).Trim());
            Assert.Equal("vanilla-cairo", File.ReadAllText(Path.Combine(gameDir, ".optimum", "vanilla", "Lib", "cairo-sharp.dll")).Trim());
            Assert.True(File.Exists(Path.Combine(gameDir, ".optimum", "vanilla", "Mods", "VSEssentials.dll")));

            // Verify patched DLLs were written
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());

            // Verify overlay assets deployed
            Assert.True(File.Exists(Path.Combine(gameDir, "Optimum.Api.Contracts.dll")));
            Assert.Equal("candidate-cairo", File.ReadAllText(Path.Combine(gameDir, "Lib", "cairo-sharp.dll")).Trim());
            Assert.Equal("game-content-dll", File.ReadAllText(Path.Combine(gameDir, "Optimum.GameContent.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "assets", "game", "shaders", "optimum.vsh")));
            Assert.True(File.Exists(Path.Combine(gameDir, "assets", "game", "lang", "en.json")));
            Assert.Contains("Optimum Value", File.ReadAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json")));
            Assert.Contains("Vanilla Value", File.ReadAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json")));
            string deployedDarkVisionPatch = Path.Combine(gameDir, "assets", "darkvision", "config", "shaderpatches", "optimum_vsp_terrain_desaturation.txt");
            Assert.True(File.Exists(deployedDarkVisionPatch));
            Assert.Equal("darkvision compatibility patch", File.ReadAllText(deployedDarkVisionPatch));
            Assert.True(File.Exists(Path.Combine(gameDir, ".optimum", "manifest.json")));

            // 2. Idempotent re-patch (reads from .optimum/vanilla, does not fail)
            var repatchResult = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(repatchResult.Ok, repatchResult.Message);
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, ".optimum", "vanilla", "VintagestoryLib.vanilla.dll")).Trim());

            // 3. Rollback
            var rollbackResult = await patcher.PatchAsync(new PatchRequest(gameDir, Rollback: true));
            Assert.True(rollbackResult.Ok, rollbackResult.Message);
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.Equal("vanilla-cairo", File.ReadAllText(Path.Combine(gameDir, "Lib", "cairo-sharp.dll")).Trim());
            Assert.False(File.Exists(deployedDarkVisionPatch));
            Assert.False(File.Exists(Path.Combine(gameDir, "Optimum.GameContent.dll")));
            string rolledBackLang = File.ReadAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json"));
            Assert.Contains("Vanilla Value", rolledBackLang);
            Assert.DoesNotContain("Optimum Value", rolledBackLang);

            // An existing assembly is restored instead of being removed on rollback.
            File.WriteAllText(Path.Combine(gameDir, "Optimum.GameContent.dll"), "existing-game-content");
            var existingAssetPatch = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(existingAssetPatch.Ok, existingAssetPatch.Message);
            Assert.Equal("game-content-dll", File.ReadAllText(Path.Combine(gameDir, "Optimum.GameContent.dll")));
            var existingAssetRollback = await patcher.PatchAsync(new PatchRequest(gameDir, Rollback: true));
            Assert.True(existingAssetRollback.Ok, existingAssetRollback.Message);
            Assert.Equal("existing-game-content", File.ReadAllText(Path.Combine(gameDir, "Optimum.GameContent.dll")));

            // If the original game has no Cairo wrapper, rollback removes the
            // candidate installed to satisfy the patched API's runtime helper.
            File.Delete(Path.Combine(gameDir, "Lib", "cairo-sharp.dll"));
            var absentCairoPatch = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(absentCairoPatch.Ok, absentCairoPatch.Message);
            Assert.Equal("candidate-cairo", File.ReadAllText(Path.Combine(gameDir, "Lib", "cairo-sharp.dll")).Trim());
            var absentCairoRollback = await patcher.PatchAsync(new PatchRequest(gameDir, Rollback: true));
            Assert.True(absentCairoRollback.Ok, absentCairoRollback.Message);
            Assert.False(File.Exists(Path.Combine(gameDir, "Lib", "cairo-sharp.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PatchRejectsIncompatibleGameVersionDeclaredInRequest()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");

        var patcher = new GamePatcher(probe, new StubRuntimeValidator(), _ => "1.21.0");

        var result = await patcher.PatchAsync(new PatchRequest(
            "/game",
            SupportedGameVersions: ["1.20.4", "1.20.5"]));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.UnsupportedVersion, result.Reason);
        Assert.Contains("incompatible game version", result.Message);
        Assert.Contains("detected Vintage Story 1.21.0", result.Message);
        Assert.Contains("1.20.4, 1.20.5", result.Message);
        Assert.False(((ISystemProbe)probe).DirectoryExists("/game/.optimum/vanilla"));
    }

    [Fact]
    public async Task PatchRejectsIncompatibleGameVersionDeclaredInOverlayManifest()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");

        probe.AddDirectory("/overlay");
        string manifestJson = """
        {
          "optimumVersion": "1.0.0",
          "supportedGameVersions": ["1.20.4"],
          "rid": "linux-x64"
        }
        """;
        probe.AddFile("/overlay/optimum-manifest.json", manifestJson);

        var patcher = new GamePatcher(probe, new StubRuntimeValidator(), _ => "1.22.7");

        var result = await patcher.PatchAsync(new PatchRequest(
            "/game",
            OverlayDirectory: "/overlay"));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.UnsupportedVersion, result.Reason);
        Assert.Contains("incompatible game version", result.Message);
        Assert.Contains("detected Vintage Story 1.22.7", result.Message);
        Assert.Contains("1.20.4", result.Message);
        Assert.False(((ISystemProbe)probe).DirectoryExists("/game/.optimum/vanilla"));
    }

    [Fact]
    public async Task PatchAllowsCompatibleGameVersion()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/game");
        probe.AddFile("/game/VintagestoryLib.dll", "lib");
        probe.AddFile("/game/VintagestoryAPI.dll", "api");

        var patcher = new GamePatcher(probe, new StubRuntimeValidator(), _ => "1.20.4");

        var result = await patcher.PatchAsync(new PatchRequest(
            "/game",
            SupportedGameVersions: ["1.20.4", "1.20.5"]));

        // Should pass version check and fail on missing patcher/donor rather than version mismatch
        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains("Optimum.Patcher", result.Message);
        Assert.False(((ISystemProbe)probe).DirectoryExists("/game/.optimum/vanilla"));
    }

    [Fact]
    public async Task MergeJsonFilePreservesVanillaFileOnCorruptedJson()
    {
        string root = Directory.CreateTempSubdirectory("optimum-json-test").FullName;
        try
        {
            string gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "lang"));
            string vanillaFile = Path.Combine(gameDir, "assets", "game", "lang", "en.json");
            File.WriteAllText(vanillaFile, "{\"vanilla-key\": \"Vanilla Value\"}");

            string overlayDir = Path.Combine(root, "overlay");
            Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "lang"));
            string invalidOverlayFile = Path.Combine(overlayDir, "assets", "game", "lang", "en.json");
            File.WriteAllText(invalidOverlayFile, "{ invalid json content !!!");

            var probe = SystemProbe.Default;
            var patcher = new GamePatcher(probe, new StubRuntimeValidator());

            await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(File.Exists(vanillaFile));
            Assert.Equal("{\"vanilla-key\": \"Vanilla Value\"}", File.ReadAllText(vanillaFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Done.\nPATCHED SHA256: sha256:abcdef0123456789\n", "sha256:abcdef0123456789")]
    [InlineData("output\r\nPATCHED SHA256: sha256:12345\r\nDone", "sha256:12345")]
    [InlineData("no hash here", null)]
    [InlineData(null, null)]
    public void ExtractPatchedHash_ParsesCorrectly(string? output, string? expected)
    {
        string? result = GamePatcher.ExtractPatchedHash(output);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SecondTargetPatchFailureLeavesGameDirectoryUntouchedAndCleansStaging()
    {
        string root = Directory.CreateTempSubdirectory("optimum-staged-fail-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(root, failOnApi: true);
            var patcher = new GamePatcher(SystemProbe.Default, new StubRuntimeValidator());

            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.False(result.Ok);
            Assert.Equal(FailureReason.PatchConflict, result.Reason);
            Assert.Contains("VintagestoryAPI.dll", result.Message);

            // VintagestoryLib.dll must remain untouched vanilla even though its stage succeeded
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.False(File.Exists(Path.Combine(gameDir, "Optimum.Api.Contracts.dll")));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeValidationFailureRollsBackPatchedAssembliesAndOverlayAssets()
    {
        string root = Directory.CreateTempSubdirectory("optimum-validation-rollback-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(root, failOnApi: false);
            var patcher = new GamePatcher(
                SystemProbe.Default,
                new StubRuntimeValidator(ok: false, detail: "missing assembly reference"));

            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.False(result.Ok);
            Assert.Equal(FailureReason.VerificationFailed, result.Reason);
            Assert.Contains("missing assembly reference", result.Message);

            // Live binaries and assets must be restored to vanilla
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.False(File.Exists(Path.Combine(gameDir, "Optimum.Api.Contracts.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir, "Optimum.GameContent.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir, ".optimum", "manifest.json")));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MidCommitSwapFailureRollsBackToVanilla()
    {
        string root = Directory.CreateTempSubdirectory("optimum-midswap-rollback-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(root, failOnApi: false);
            int swapCount = 0;
            var patcher = new GamePatcher(SystemProbe.Default, new StubRuntimeValidator())
            {
                FailAtCommitStep = step =>
                {
                    if (step == "swap" && ++swapCount == 1)
                        throw new IOException("simulated disk failure after first assembly swap");
                },
            };

            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.False(result.Ok);
            Assert.Equal(FailureReason.EngineInternal, result.Reason);
            Assert.Contains("rolled back", result.Message);

            // Even though VintagestoryLib.dll was swapped before the exception, rollback restores vanilla
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string GameDir, string OverlayDir) CreateTestGameAndOverlay(
        string root,
        bool failOnApi,
        bool includeEssentials = true,
        bool includeSurvival = false)
    {
        string gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Lib"));
        Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "lang"));

        File.WriteAllText(Path.Combine(gameDir, "VintagestoryLib.dll"), "vanilla-lib");
        File.WriteAllText(Path.Combine(gameDir, "VintagestoryAPI.dll"), "vanilla-api");
        if (includeEssentials)
            File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "vanilla-essentials");
        if (includeSurvival)
            File.WriteAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll"), "vanilla-survival");
        File.WriteAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json"), "{\"vanilla-key\": \"Vanilla Value\"}");

        string overlayDir = Path.Combine(root, "overlay");
        string donorsDir = Path.Combine(overlayDir, ".optimum", "donors");
        Directory.CreateDirectory(donorsDir);
        Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "lang"));

        File.WriteAllText(Path.Combine(donorsDir, "VintagestoryLib.Donor.dll"), "donor-lib");
        File.WriteAllText(Path.Combine(donorsDir, "VintagestoryAPI.Contracts.dll"), "donor-api");
        if (includeEssentials)
            File.WriteAllText(Path.Combine(donorsDir, "VSEssentials.Donor.dll"), "donor-essentials");
        if (includeSurvival)
            File.WriteAllText(Path.Combine(donorsDir, "VSSurvivalMod.Donor.dll"), "donor-survival");
        File.WriteAllText(Path.Combine(overlayDir, "Optimum.Api.Contracts.dll"), "contracts-dll");
        File.WriteAllText(Path.Combine(overlayDir, "Optimum.GameContent.dll"), "game-content-dll");
        File.WriteAllText(Path.Combine(overlayDir, "assets", "game", "lang", "en.json"), "{\"optimum-key\": \"Optimum Value\"}");

        string patcherExe = Path.Combine(overlayDir, OperatingSystem.IsWindows() ? "Optimum.Patcher.bat" : "Optimum.Patcher");
        if (OperatingSystem.IsWindows())
        {
            string failCheck = failOnApi ? "if \"%~1\"==\"--api\" exit /b 1\r\n" : "";
            File.WriteAllText(patcherExe,
                $"@echo off\r\n{failCheck}set \"target=\"\r\nfor %%t in (%*) do set \"target=%%~t\"\r\necho patched > \"%target%\"\r\nexit /b 0\r\n");
        }
        else
        {
            string failCheck = failOnApi ? "if [ \"$1\" = \"--api\" ]; then echo \"api conflict\" >&2; exit 1; fi\n" : "";
            File.WriteAllText(patcherExe, $"#!/usr/bin/env bash\n{failCheck}target=\"${{@: -1}}\"\necho \"patched\" > \"$target\"\nexit 0\n");
            File.SetUnixFileMode(patcherExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return (gameDir, overlayDir);
    }

    [Fact]
    public async Task InProcessPatcherReusesPatchedSha256WithoutRehashingStagedOutputs()
    {
        string root = Directory.CreateTempSubdirectory("optimum-inproc-sha256-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(root, failOnApi: false);
            File.Delete(Path.Combine(overlayDir, OperatingSystem.IsWindows() ? "Optimum.Patcher.bat" : "Optimum.Patcher"));
            File.Copy(typeof(Vintagestory.Client.ClientProgram).Assembly.Location, Path.Combine(overlayDir, "Optimum.Patcher.dll"), overwrite: true);
            Optimum.Patcher.Program.OmitDigest = false;

            var hashedPaths = new List<string>();
            var patcher = new GamePatcher(SystemProbe.Default, new StubRuntimeValidator())
            {
                OnComputeSha256 = p => hashedPaths.Add(p),
            };

            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(result.Ok, result.Message);

            // Only the 3 vanilla sources are hashed via ComputeSha256; none of the staged output DLLs are re-opened.
            Assert.Equal(3, hashedPaths.Count);
            Assert.DoesNotContain(hashedPaths, p => p.Contains("staging-", StringComparison.OrdinalIgnoreCase));

            string manifestJson = File.ReadAllText(Path.Combine(gameDir, PatchInstallManifest.RelativePath));
            PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(manifestJson);
            Assert.NotNull(manifest);
            Assert.Equal(3, manifest!.Targets.Count);

            foreach (PatchTargetRecord target in manifest.Targets)
            {
                string livePath = Path.Combine(gameDir, target.Assembly.Replace('/', Path.DirectorySeparatorChar));
                string expectedLiveHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(livePath)));
                Assert.Equal(expectedLiveHash, target.PatchedHash);
            }
        }
        finally
        {
            Optimum.Patcher.Program.OmitDigest = false;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InProcessPatcherWithoutDigestFallsBackToHashingStagedOutputs()
    {
        string root = Directory.CreateTempSubdirectory("optimum-legacy-inproc-sha256-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(root, failOnApi: false);
            File.Delete(Path.Combine(overlayDir, OperatingSystem.IsWindows() ? "Optimum.Patcher.bat" : "Optimum.Patcher"));
            File.Copy(typeof(Vintagestory.Client.ClientProgram).Assembly.Location, Path.Combine(overlayDir, "Optimum.Patcher.dll"), overwrite: true);
            Optimum.Patcher.Program.OmitDigest = true;

            var hashedPaths = new List<string>();
            var patcher = new GamePatcher(SystemProbe.Default, new StubRuntimeValidator())
            {
                OnComputeSha256 = p => hashedPaths.Add(p),
            };

            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(result.Ok, result.Message);

            // 3 vanilla inputs + 3 staged fallback hashes = 6 total ComputeSha256 calls.
            Assert.Equal(6, hashedPaths.Count);
            Assert.Equal(3, hashedPaths.Count(p => p.Contains("staging-", StringComparison.OrdinalIgnoreCase)));

            string manifestJson = File.ReadAllText(Path.Combine(gameDir, PatchInstallManifest.RelativePath));
            PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(manifestJson);
            Assert.NotNull(manifest);
            foreach (PatchTargetRecord target in manifest!.Targets)
            {
                string livePath = Path.Combine(gameDir, target.Assembly.Replace('/', Path.DirectorySeparatorChar));
                string expectedLiveHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(livePath)));
                Assert.Equal(expectedLiveHash, target.PatchedHash);
            }
        }
        finally
        {
            Optimum.Patcher.Program.OmitDigest = false;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParallelStagingOverlapsTwoWorkersIsolatesStagingDirsAndPreservesCanonicalOrder()
    {
        string root = Directory.CreateTempSubdirectory("optimum-parallel-order-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(
                root, failOnApi: false, includeEssentials: true, includeSurvival: true);

            using var firstWaveStarted = new CountdownEvent(2);
            using var releaseLib = new ManualResetEventSlim(false);
            int activeWorkers = 0;
            int maxConcurrentWorkers = 0;
            var stagedDirectories = new ConcurrentDictionary<string, string>();
            var completionOrder = new ConcurrentQueue<string>();

            var probe = new InterceptingSystemProbe(SystemProbe.Default, (_, arguments, _) =>
            {
                int current = Interlocked.Increment(ref activeWorkers);
                UpdateMax(ref maxConcurrentWorkers, current);
                try
                {
                    string outputPath = arguments[^1];
                    string fileName = Path.GetFileName(outputPath);
                    stagedDirectories[fileName] = Path.GetDirectoryName(outputPath)!;

                    // Verify both initial workers overlap before either completes, and force
                    // out-of-order completion (API finishes before Lib).
                    if (fileName is "VintagestoryLib.dll" or "VintagestoryAPI.dll")
                    {
                        firstWaveStarted.Signal();
                        Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(10)), "expected 2 staging workers to run concurrently");
                    }

                    if (fileName == "VintagestoryLib.dll")
                    {
                        Assert.True(releaseLib.Wait(TimeSpan.FromSeconds(10)), "expected API to finish before releasing Lib");
                    }

                    string content = $"patched-{fileName}\n";
                    File.WriteAllText(outputPath, content);
                    File.WriteAllText(Path.ChangeExtension(outputPath, ".pdb"), $"pdb-{fileName}\n");
                    string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outputPath)));
                    completionOrder.Enqueue(fileName);

                    if (fileName == "VintagestoryAPI.dll")
                    {
                        releaseLib.Set();
                    }

                    return new ProcessOutcome(true, 0, $"PATCHED SHA256: {digest}\n", string.Empty);
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            });

            var observer = new RecordingObserver();
            var patcher = new GamePatcher(probe, new StubRuntimeValidator());
            var result = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir), observer);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(2, maxConcurrentWorkers);

            // Every target staged into its own isolated subdirectory under staging-*.
            Assert.Equal(4, stagedDirectories.Count);
            Assert.Equal(4, stagedDirectories.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(stagedDirectories.Values, dir => Assert.Contains("staging-", dir, StringComparison.OrdinalIgnoreCase));

            // API completed before Lib even though canonical output keeps Lib first.
            string[] completed = completionOrder.ToArray();
            Assert.True(Array.IndexOf(completed, "VintagestoryAPI.dll") < Array.IndexOf(completed, "VintagestoryLib.dll"));

            string[] expectedOrder =
            [
                "VintagestoryLib.dll",
                "VintagestoryAPI.dll",
                "Mods/VSEssentials.dll",
                "Mods/VSSurvivalMod.dll",
            ];
            Assert.Equal(expectedOrder, result.PatchedTargets);

            string manifestJson = File.ReadAllText(Path.Combine(gameDir, PatchInstallManifest.RelativePath));
            PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(manifestJson);
            Assert.NotNull(manifest);
            Assert.Equal(expectedOrder, manifest!.Targets.Select(t => t.Assembly).ToArray());

            foreach (PatchTargetRecord target in manifest.Targets)
            {
                string livePath = Path.Combine(gameDir, target.Assembly.Replace('/', Path.DirectorySeparatorChar));
                string expectedHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(livePath)));
                Assert.Equal(expectedHash, target.PatchedHash);
                Assert.Equal($"pdb-{Path.GetFileName(livePath)}", File.ReadAllText(Path.ChangeExtension(livePath, ".pdb")).Trim());
            }

            // Progress percentages reported to observer never regress.
            int[] patchPercents = observer.Phases.Where(p => p.Phase == ProgressPhase.Patch).Select(p => p.Percent).ToArray();
            Assert.NotEmpty(patchPercents);
            for (int i = 1; i < patchPercents.Length; i++)
            {
                Assert.True(patchPercents[i] >= patchPercents[i - 1],
                    $"progress regressed from {patchPercents[i - 1]} to {patchPercents[i]}");
            }

            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParallelStagingCancellationStopsQueuedTargetsAndWaitsForInFlightWorkersBeforeCleanup()
    {
        string root = Directory.CreateTempSubdirectory("optimum-parallel-cancel-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(
                root, failOnApi: false, includeEssentials: true, includeSurvival: true);

            using var cts = new CancellationTokenSource();
            using var firstWaveStarted = new CountdownEvent(2);
            using var allowInFlightExit = new ManualResetEventSlim(false);
            var startedTargets = new ConcurrentBag<string>();
            bool stagingExistedAtWorkerExit = false;

            var probe = new InterceptingSystemProbe(SystemProbe.Default, (_, arguments, _) =>
            {
                string outputPath = arguments[^1];
                string fileName = Path.GetFileName(outputPath);
                startedTargets.Add(fileName);

                if (fileName is "VintagestoryLib.dll" or "VintagestoryAPI.dll")
                {
                    firstWaveStarted.Signal();
                    Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(10)));
                }

                if (fileName == "VintagestoryLib.dll")
                {
                    Assert.True(allowInFlightExit.Wait(TimeSpan.FromSeconds(10)));
                    stagingExistedAtWorkerExit = Directory.Exists(Path.GetDirectoryName(outputPath)!);
                }

                File.WriteAllText(outputPath, $"patched-{fileName}\n");
                return new ProcessOutcome(true, 0, string.Empty, string.Empty);
            });

            var patcher = new GamePatcher(probe, new StubRuntimeValidator());
            Task<PatchResult> patchTask = patcher.PatchAsync(
                new PatchRequest(gameDir, OverlayDirectory: overlayDir),
                cancellationToken: cts.Token);

            Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(10)));
            cts.Cancel();
            // Give PatchAsync a brief moment to verify it does not delete stagingDir while Lib is still running.
            await Task.Delay(50);
            Assert.False(patchTask.IsCompleted);
            allowInFlightExit.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patchTask);

            Assert.True(stagingExistedAtWorkerExit, "staging directory was removed while an in-flight worker was still running");
            Assert.DoesNotContain("VSEssentials.dll", startedTargets);
            Assert.DoesNotContain("VSSurvivalMod.dll", startedTargets);

            // Live game assemblies must remain untouched vanilla and staging must be cleaned up.
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.Equal("vanilla-survival", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")).Trim());
            Assert.False(File.Exists(Path.Combine(gameDir, PatchInstallManifest.RelativePath)));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParallelStagingFailureStopsQueuedTargetsLeavesGameUntouchedAndAllowsRetry()
    {
        string root = Directory.CreateTempSubdirectory("optimum-parallel-fail-retry-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(
                root, failOnApi: false, includeEssentials: true, includeSurvival: true);

            using var firstWaveStarted = new CountdownEvent(2);
            using var apiFailed = new ManualResetEventSlim(false);
            var startedTargets = new ConcurrentBag<string>();
            bool shouldFailApi = true;

            var probe = new InterceptingSystemProbe(SystemProbe.Default, (_, arguments, _) =>
            {
                string outputPath = arguments[^1];
                string fileName = Path.GetFileName(outputPath);
                startedTargets.Add(fileName);

                if (shouldFailApi && fileName is "VintagestoryLib.dll" or "VintagestoryAPI.dll")
                {
                    firstWaveStarted.Signal();
                    Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(10)));
                }

                if (shouldFailApi && fileName == "VintagestoryAPI.dll")
                {
                    apiFailed.Set();
                    return new ProcessOutcome(true, 1, string.Empty, "simulated parallel API conflict");
                }

                if (shouldFailApi && fileName == "VintagestoryLib.dll")
                {
                    Assert.True(apiFailed.Wait(TimeSpan.FromSeconds(10)));
                    // Yield briefly so API failure cancels linkedCts before Lib finishes.
                    Thread.Sleep(25);
                }

                File.WriteAllText(outputPath, $"patched-{fileName}\n");
                string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outputPath)));
                return new ProcessOutcome(true, 0, $"PATCHED SHA256: {digest}\n", string.Empty);
            });

            var patcher = new GamePatcher(probe, new StubRuntimeValidator());
            var failedResult = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.False(failedResult.Ok);
            Assert.Equal(FailureReason.PatchConflict, failedResult.Reason);
            Assert.Contains("VintagestoryAPI.dll", failedResult.Message);
            Assert.DoesNotContain("VSEssentials.dll", startedTargets);
            Assert.DoesNotContain("VSSurvivalMod.dll", startedTargets);
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.Equal("vanilla-survival", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")).Trim());
            Assert.False(File.Exists(Path.Combine(gameDir, PatchInstallManifest.RelativePath)));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));

            // Retry after clearing failure must succeed cleanly and commit all 4 assemblies.
            shouldFailApi = false;
            var retryResult = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));

            Assert.True(retryResult.Ok, retryResult.Message);
            Assert.Equal(
                ["VintagestoryLib.dll", "VintagestoryAPI.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll"],
                retryResult.PatchedTargets);
            Assert.Equal("patched-VintagestoryLib.dll", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("patched-VintagestoryAPI.dll", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("patched-VSEssentials.dll", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            Assert.Equal("patched-VSSurvivalMod.dll", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")).Trim());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ParallelStagingHandlesOptionalModCombinationsRollbackAndRetry(
        bool includeEssentials,
        bool includeSurvival)
    {
        string root = Directory.CreateTempSubdirectory("optimum-parallel-optional-test").FullName;
        try
        {
            (string gameDir, string overlayDir) = CreateTestGameAndOverlay(
                root, failOnApi: false, includeEssentials: includeEssentials, includeSurvival: includeSurvival);

            var expectedTargets = new List<string> { "VintagestoryLib.dll", "VintagestoryAPI.dll" };
            if (includeEssentials) expectedTargets.Add("Mods/VSEssentials.dll");
            if (includeSurvival) expectedTargets.Add("Mods/VSSurvivalMod.dll");

            // 1. Validation failure triggers automatic rollback cleanly.
            var failingPatcher = new GamePatcher(
                SystemProbe.Default,
                new StubRuntimeValidator(ok: false, detail: "simulated validation failure"));
            var failedResult = await failingPatcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.False(failedResult.Ok);
            Assert.Equal(FailureReason.VerificationFailed, failedResult.Reason);
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            if (includeEssentials)
                Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            if (includeSurvival)
                Assert.Equal("vanilla-survival", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")).Trim());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(gameDir, ".optimum"), "staging-*"));

            // 2. Retry succeeds with canonical target ordering and matching manifest digests.
            var patcher = new GamePatcher(SystemProbe.Default, new StubRuntimeValidator());
            var retryResult = await patcher.PatchAsync(new PatchRequest(gameDir, OverlayDirectory: overlayDir));
            Assert.True(retryResult.Ok, retryResult.Message);
            Assert.Equal(expectedTargets, retryResult.PatchedTargets);

            PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(gameDir, PatchInstallManifest.RelativePath)));
            Assert.NotNull(manifest);
            Assert.Equal(expectedTargets, manifest!.Targets.Select(t => t.Assembly).ToArray());
            foreach (PatchTargetRecord target in manifest.Targets)
            {
                string livePath = Path.Combine(gameDir, target.Assembly.Replace('/', Path.DirectorySeparatorChar));
                string actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(livePath)));
                Assert.Equal(actualHash, target.PatchedHash);
            }

            // 3. Explicit rollback restores every present vanilla assembly.
            var rollbackResult = await patcher.PatchAsync(new PatchRequest(gameDir, Rollback: true));
            Assert.True(rollbackResult.Ok, rollbackResult.Message);
            Assert.Equal("vanilla-lib", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("vanilla-api", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            if (includeEssentials)
                Assert.Equal("vanilla-essentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());
            if (includeSurvival)
                Assert.Equal("vanilla-survival", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")).Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void UpdateMax(ref int location, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref location)))
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current)
                break;
        }
    }

    private sealed class RecordingObserver : IBuildObserver
    {
        private readonly Lock gate = new();
        private readonly List<(ProgressPhase Phase, int Percent, string Detail)> phases = [];

        public IReadOnlyList<(ProgressPhase Phase, int Percent, string Detail)> Phases
        {
            get
            {
                lock (gate)
                    return [.. phases];
            }
        }

        public void Phase(ProgressPhase phase, int percent, string detail)
        {
            lock (gate)
                phases.Add((phase, percent, detail));
        }

        public void Log(LogLevel level, string message) { }

        public void RawOutput(bool isError, string line) { }
    }

    private sealed class InterceptingSystemProbe(
        ISystemProbe inner,
        Func<string, IReadOnlyList<string>, TimeSpan, ProcessOutcome> onRun) : ISystemProbe
    {
        public OsKind Os => inner.Os;
        public Architecture Arch => inner.Arch;
        public string HomeDirectory => inner.HomeDirectory;
        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);
        public IReadOnlyList<string> PathDirectories => inner.PathDirectories;
        public bool FileExists(string path) => inner.FileExists(path);
        public bool IsExecutable(string path) => inner.IsExecutable(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool PathExists(string path) => inner.PathExists(path);
        public bool IsSymbolicLink(string path) => inner.IsSymbolicLink(path);
        public bool CanWriteToDirectory(string path) => inner.CanWriteToDirectory(path);
        public string? ReadText(string path) => inner.ReadText(path);
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern) => inner.EnumerateFiles(directory, searchPattern);
        public IEnumerable<string> EnumerateDirectories(string directory, string searchPattern) => inner.EnumerateDirectories(directory, searchPattern);
        public ProcessOutcome Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout) => onRun(executable, arguments, timeout);
    }
}
