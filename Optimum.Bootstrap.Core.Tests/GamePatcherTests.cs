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
            Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "shaders"));
            Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "lang"));

            File.WriteAllText(Path.Combine(gameDir, "VintagestoryLib.dll"), "vanilla-lib");
            File.WriteAllText(Path.Combine(gameDir, "VintagestoryAPI.dll"), "vanilla-api");
            File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "vanilla-essentials");
            File.WriteAllText(Path.Combine(gameDir, "run.sh"), "#!/bin/sh\n./Optimum\n");

            string overlayDir = Path.Combine(root, "overlay");
            string donorsDir = Path.Combine(overlayDir, ".optimum", "donors");
            Directory.CreateDirectory(donorsDir);
            Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "shaders"));
            Directory.CreateDirectory(Path.Combine(overlayDir, "assets", "game", "lang"));

            File.WriteAllText(Path.Combine(donorsDir, "VintagestoryLib.Donor.dll"), "donor-lib");
            File.WriteAllText(Path.Combine(donorsDir, "VintagestoryAPI.Contracts.dll"), "donor-api");
            File.WriteAllText(Path.Combine(donorsDir, "VSEssentials.Donor.dll"), "donor-essentials");
            File.WriteAllText(Path.Combine(overlayDir, "Optimum.Api.Contracts.dll"), "contracts-dll");
            File.WriteAllText(Path.Combine(overlayDir, "Optimum"), "#!/bin/sh\necho optimum\n");
            File.WriteAllText(Path.Combine(overlayDir, "assets", "game", "shaders", "optimum.vsh"), "void main() {}");
            File.WriteAllText(Path.Combine(overlayDir, "assets", "game", "lang", "en.json"), "{\"optimum-key\": \"Optimum Value\"}");

            // Create executable stub patcher
            string patcherExe = Path.Combine(overlayDir, OperatingSystem.IsWindows() ? "Optimum.Patcher.bat" : "Optimum.Patcher");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(patcherExe, "@echo off\r\necho patched > %~dpnx3\r\nexit /b 0\r\n");
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
            Assert.True(File.Exists(Path.Combine(gameDir, ".optimum", "vanilla", "Mods", "VSEssentials.dll")));

            // Verify patched DLLs were written
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "VintagestoryLib.dll")).Trim());
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "VintagestoryAPI.dll")).Trim());
            Assert.Equal("patched", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")).Trim());

            // Verify overlay assets deployed
            Assert.True(File.Exists(Path.Combine(gameDir, "Optimum.Api.Contracts.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "assets", "game", "shaders", "optimum.vsh")));
            Assert.True(File.Exists(Path.Combine(gameDir, "assets", "game", "lang", "en.json")));
            Assert.Contains("Optimum Value", File.ReadAllText(Path.Combine(gameDir, "assets", "game", "lang", "en.json")));
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
