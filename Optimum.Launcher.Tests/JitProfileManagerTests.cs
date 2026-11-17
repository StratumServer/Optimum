using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class JitProfileManagerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "optimum-jit-profile-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsEnabledHonorsEnvironmentAndCliOverrides()
    {
        Assert.True(JitProfileManager.IsEnabled([], _ => null));
        Assert.False(JitProfileManager.IsEnabled([], _ => "0"));
        Assert.False(JitProfileManager.IsEnabled([], _ => "false"));
        Assert.False(JitProfileManager.IsEnabled([], _ => "off"));
        Assert.False(JitProfileManager.IsEnabled(["--optimum-no-jit-profile"], _ => null));
        Assert.False(JitProfileManager.IsEnabled(["--optimum-jit-profile=disabled"], _ => "1"));
        Assert.True(JitProfileManager.IsEnabled(["--optimum-jit-profile=on"], _ => "0"));
        Assert.False(JitProfileManager.IsEnabled(["--version"], _ => null));
        Assert.False(JitProfileManager.IsEnabled(["-v"], _ => null));
        Assert.False(JitProfileManager.IsEnabled(["--help"], _ => null));
        Assert.False(JitProfileManager.IsEnabled(["-h"], _ => null));
    }

    [Fact]
    public void ComputeProfileNameInvalidatesOnVersionCacheOrModChangesAndIgnoresUnrelatedArgs()
    {
        string gameDir = CreateDir("game");
        string dataDir = CreateDir("data");
        string gameModsDir = CreateDir("game", "Mods");
        string dataModsDir = CreateDir("data", "Mods");
        string extraModsDir = CreateDir("extra-mods");

        File.WriteAllText(Path.Combine(gameModsDir, "VSEssentials.dll"), "essentials-v1");
        File.WriteAllText(Path.Combine(dataModsDir, "somemod.zip"), "mod-v1");

        CacheManifest manifest = CreateManifest("0.3.23", "1.22.7", "sha256:lib-a", "sha256:donor-a", "sha256:van-a");

        string baseProfile = JitProfileManager.ComputeProfileName(
            gameDir,
            dataDir,
            "0.3.23",
            manifest,
            ["--openWorld", "testworld", "--addModPath", extraModsDir],
            () => "linux-x64",
            () => Architecture.X64,
            () => new Version(10, 0, 1));

        string unrelatedArgProfile = JitProfileManager.ComputeProfileName(
            gameDir,
            dataDir,
            "0.3.23",
            manifest,
            ["--openWorld", "differentworld", "--addModPath=" + extraModsDir],
            () => "linux-x64",
            () => Architecture.X64,
            () => new Version(10, 0, 1));

        Assert.Equal(baseProfile, unrelatedArgProfile);
        Assert.StartsWith(JitProfileManager.ProfileFilePrefix, baseProfile, StringComparison.Ordinal);
        Assert.EndsWith(JitProfileManager.ProfileFileExtension, baseProfile, StringComparison.Ordinal);

        Assert.NotEqual(
            baseProfile,
            JitProfileManager.ComputeProfileName(
                gameDir,
                dataDir,
                "0.3.24",
                CreateManifest("0.3.24", "1.22.7", "sha256:lib-a", "sha256:donor-a", "sha256:van-a"),
                ["--addModPath", extraModsDir],
                () => "linux-x64",
                () => Architecture.X64,
                () => new Version(10, 0, 1)));

        Assert.NotEqual(
            baseProfile,
            JitProfileManager.ComputeProfileName(
                gameDir,
                dataDir,
                "0.3.23",
                CreateManifest("0.3.23", "1.22.8", "sha256:lib-a", "sha256:donor-a", "sha256:van-a"),
                ["--addModPath", extraModsDir],
                () => "linux-x64",
                () => Architecture.X64,
                () => new Version(10, 0, 1)));

        Assert.NotEqual(
            baseProfile,
            JitProfileManager.ComputeProfileName(
                gameDir,
                dataDir,
                "0.3.23",
                CreateManifest("0.3.23", "1.22.7", "sha256:lib-b", "sha256:donor-a", "sha256:van-a"),
                ["--addModPath", extraModsDir],
                () => "linux-x64",
                () => Architecture.X64,
                () => new Version(10, 0, 1)));

        Assert.NotEqual(
            baseProfile,
            JitProfileManager.ComputeProfileName(
                gameDir,
                dataDir,
                "0.3.23",
                manifest,
                ["--addModPath", extraModsDir],
                () => "linux-arm64",
                () => Architecture.Arm64,
                () => new Version(10, 0, 1)));

        File.WriteAllText(Path.Combine(extraModsDir, "extramod.dll"), "added");
        string withExtraMod = JitProfileManager.ComputeProfileName(
            gameDir,
            dataDir,
            "0.3.23",
            manifest,
            ["--addModPath", extraModsDir],
            () => "linux-x64",
            () => Architecture.X64,
            () => new Version(10, 0, 1));
        Assert.NotEqual(baseProfile, withExtraMod);
    }

    [Fact]
    public void TryStartConfiguresRootStartsStopsOnDisposeAndDetectsWarmProfile()
    {
        string gameDir = CreateDir("game");
        string dataDir = CreateDir("data");
        CacheManifest manifest = CreateManifest("0.3.23", "1.22.7", "sha256:lib", "sha256:donor", "sha256:van");

        var events = new List<string>();
        var logs = new List<string>();

        using (JitProfileHandle? first = JitProfileManager.TryStart(
            gameDir,
            dataDir,
            "0.3.23",
            manifest,
            [],
            dir => events.Add("root:" + dir),
            name => events.Add("start:" + (name ?? "<null>")),
            processorCountOverride: 4,
            getEnvironmentVariable: _ => null,
            log: logs.Add))
        {
            Assert.NotNull(first);
            Assert.False(first!.IsWarmProfile);
            Assert.Equal(2, events.Count);
            Assert.Equal("root:" + JitProfileManager.ResolveProfileRoot(dataDir), events[0]);
            Assert.Equal("start:" + first.ProfileName, events[1]);
            File.WriteAllBytes(Path.Combine(first.ProfileRoot, first.ProfileName), [1, 2, 3, 4]);
        }

        Assert.Equal("start:<null>", events[2]);
        Assert.Contains(logs, line => line.Contains("recording (cold)", StringComparison.Ordinal));

        logs.Clear();
        using (JitProfileHandle? second = JitProfileManager.TryStart(
            gameDir,
            dataDir,
            "0.3.23",
            manifest,
            [],
            dir => events.Add("root:" + dir),
            name => events.Add("start:" + (name ?? "<null>")),
            processorCountOverride: 4,
            getEnvironmentVariable: _ => null,
            log: logs.Add))
        {
            Assert.NotNull(second);
            Assert.True(second!.IsWarmProfile);
        }

        Assert.Contains(logs, line => line.Contains("active (warm)", StringComparison.Ordinal));
    }

    [Fact]
    public void TryStartSkipsGracefullyWhenProfileLockedSingleCoreDisabledOrBlocked()
    {
        string gameDir = CreateDir("game");
        string dataDir = CreateDir("data");
        CacheManifest manifest = CreateManifest("0.3.23", "1.22.7", "sha256:lib", "sha256:donor", "sha256:van");
        var logs = new List<string>();
        int callbackCount = 0;

        using (JitProfileHandle? disabled = JitProfileManager.TryStart(
            gameDir, dataDir, "0.3.23", manifest, ["--optimum-no-jit-profile"],
            _ => callbackCount++, _ => callbackCount++, processorCountOverride: 4, log: logs.Add))
        {
            Assert.Null(disabled);
        }

        using (JitProfileHandle? singleCore = JitProfileManager.TryStart(
            gameDir, dataDir, "0.3.23", manifest, [],
            _ => callbackCount++, _ => callbackCount++, processorCountOverride: 1,
            getEnvironmentVariable: _ => null, log: logs.Add))
        {
            Assert.Null(singleCore);
        }

        using (JitProfileHandle? primary = JitProfileManager.TryStart(
            gameDir, dataDir, "0.3.23", manifest, [],
            _ => { }, _ => { }, processorCountOverride: 4, getEnvironmentVariable: _ => null))
        {
            Assert.NotNull(primary);
            using JitProfileHandle? contended = JitProfileManager.TryStart(
                gameDir, dataDir, "0.3.23", manifest, [],
                _ => callbackCount++, _ => callbackCount++, processorCountOverride: 4,
                getEnvironmentVariable: _ => null, log: logs.Add);
            Assert.Null(contended);
        }

        string blockedDataDir = CreateDir("blocked-data");
        File.WriteAllText(Path.Combine(blockedDataDir, ".optimum"), "not-a-directory");
        using (JitProfileHandle? blocked = JitProfileManager.TryStart(
            gameDir, blockedDataDir, "0.3.23", manifest, [],
            _ => callbackCount++, _ => callbackCount++, processorCountOverride: 4,
            getEnvironmentVariable: _ => null, log: logs.Add))
        {
            Assert.Null(blocked);
        }

        Assert.Equal(0, callbackCount);
        Assert.Contains(logs, line => line.Contains("profile in use", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("multicore JIT profiling skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void PruneStaleProfilesKeepsNewestAndLockedProfilesWithinCap()
    {
        string profileDir = CreateDir("prune");
        DateTime now = DateTime.UtcNow;

        string active = "jit-active.profile";
        string recent1 = "jit-recent1.profile";
        string recent2 = "jit-recent2.profile";
        string recent3 = "jit-recent3.profile";
        string staleUnlocked = "jit-stale-old.profile";
        string staleLocked = "jit-stale-locked.profile";

        string[] all = [active, recent1, recent2, recent3, staleUnlocked, staleLocked];
        for (int index = 0; index < all.Length; index++)
        {
            string path = Path.Combine(profileDir, all[index]);
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-index * 10));
        }

        File.WriteAllBytes(Path.Combine(profileDir, staleUnlocked + JitProfileManager.LockFileExtension), []);
        using var heldLock = new FileStream(
            Path.Combine(profileDir, staleLocked + JitProfileManager.LockFileExtension),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        JitProfileManager.PruneStaleProfiles(profileDir, active, maxRetained: 4);

        Assert.True(File.Exists(Path.Combine(profileDir, active)));
        Assert.True(File.Exists(Path.Combine(profileDir, recent1)));
        Assert.True(File.Exists(Path.Combine(profileDir, recent2)));
        Assert.True(File.Exists(Path.Combine(profileDir, recent3)));
        Assert.False(File.Exists(Path.Combine(profileDir, staleUnlocked)));
        Assert.False(File.Exists(Path.Combine(profileDir, staleUnlocked + JitProfileManager.LockFileExtension)));
        Assert.True(File.Exists(Path.Combine(profileDir, staleLocked)));
    }

    [Fact]
    public void TryStartCreatesAndReusesRealCoreClrProfileInChildProcess()
    {
        if (Environment.ProcessorCount < 2)
            return;

        string gameDir = CreateDir("real-game");
        string dataDir = CreateDir("real-data");
        File.WriteAllText(Path.Combine(gameDir, "version.txt"), "1.22.7");
        File.WriteAllBytes(Path.Combine(gameDir, "VintagestoryLib.dll"), [1, 2, 3, 4]);

        string harnessDll = BuildChildProbeAssembly();

        (int firstExit, string firstStdout, string firstStderr) = RunChildProbe(harnessDll, gameDir, dataDir);
        Assert.True(firstExit == 0, $"First child failed ({firstExit}): {firstStderr}\n{firstStdout}");
        Assert.Contains("recording (cold)", firstStdout, StringComparison.Ordinal);
        Assert.Contains("CHILD_STATE=cold", firstStdout, StringComparison.Ordinal);

        string profileDir = JitProfileManager.ResolveProfileRoot(dataDir);
        string[] profiles = Directory.GetFiles(profileDir, "jit-*.profile");
        Assert.Single(profiles);
        long firstSize = new FileInfo(profiles[0]).Length;
        Assert.True(firstSize > 0, $"Expected CoreCLR ProfileOptimization to write non-empty profile, got {firstSize} bytes.");

        (int secondExit, string secondStdout, string secondStderr) = RunChildProbe(harnessDll, gameDir, dataDir);
        Assert.True(secondExit == 0, $"Second child failed ({secondExit}): {secondStderr}\n{secondStdout}");
        Assert.Contains("active (warm)", secondStdout, StringComparison.Ordinal);
        Assert.Contains("CHILD_STATE=warm", secondStdout, StringComparison.Ordinal);

        string cleanDisabledData = CreateDir("real-data-disabled");
        (int disabledExit, string disabledStdout, string _) = RunChildProbe(
            harnessDll,
            gameDir,
            cleanDisabledData,
            "--optimum-no-jit-profile");
        Assert.Equal(0, disabledExit);
        Assert.Contains("disabled by configuration", disabledStdout, StringComparison.Ordinal);
        Assert.Contains("CHILD_STATE=none", disabledStdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(JitProfileManager.ResolveProfileRoot(cleanDisabledData)));
    }

    public static int ExecuteChildProcessProbe(string[] args)
    {
        string gameDir = args[0];
        string dataDir = args[1];
        string[] launcherArgs = args.Skip(2).ToArray();

        using JitProfileHandle? handle = JitProfileManager.TryStart(
            gameDir,
            dataDir,
            "0.3.23",
            manifest: null,
            launcherArgs,
            log: Console.WriteLine);

        int checksum = ExerciseJitMethods();
        if (handle is null)
        {
            Console.WriteLine($"CHILD_STATE=none CHECKSUM={checksum}");
            return 0;
        }

        string state = handle.IsWarmProfile ? "warm" : "cold";
        Console.WriteLine($"CHILD_STATE={state} PROFILE={handle.ProfileName} CHECKSUM={checksum}");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExerciseJitMethods()
    {
        var values = Enumerable.Range(1, 256).Select(i => (i * 31) ^ 0x5A).ToArray();
        return values.Where(v => (v & 1) == 0).Sum();
    }

    private string BuildChildProbeAssembly()
    {
        string testBaseDir = Path.GetDirectoryName(typeof(JitProfileManagerTests).Assembly.Location)!;
        string harnessDir = CreateDir("child-harness");
        string harnessDll = Path.Combine(harnessDir, "JitProfileChildProbe.dll");

        using (var module = ModuleDefinition.CreateModule(
            "JitProfileChildProbe",
            new ModuleParameters
            {
                Kind = ModuleKind.Console,
                Runtime = TargetRuntime.Net_4_0,
            }))
        {
            var programType = new TypeDefinition(
                "Optimum.Launcher.Tests.ChildProbe",
                "Program",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(programType);

            var mainMethod = new MethodDefinition(
                "Main",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Int32);
            mainMethod.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None,
                new ArrayType(module.TypeSystem.String)));
            programType.Methods.Add(mainMethod);

            var targetMethod = typeof(JitProfileManagerTests).GetMethod(
                nameof(ExecuteChildProcessProbe),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
            var targetRef = module.ImportReference(targetMethod);

            ILProcessor il = mainMethod.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, targetRef);
            il.Emit(OpCodes.Ret);

            module.EntryPoint = mainMethod;
            module.Write(harnessDll);
        }

        File.Copy(
            Path.Combine(testBaseDir, "Optimum.Launcher.Tests.runtimeconfig.json"),
            Path.Combine(harnessDir, "JitProfileChildProbe.runtimeconfig.json"),
            overwrite: true);

        foreach (string requiredAssembly in new[] { "Optimum.Launcher.Tests.dll", "Optimum.dll", "Optimum.Api.Contracts.dll" })
            File.Copy(Path.Combine(testBaseDir, requiredAssembly), Path.Combine(harnessDir, requiredAssembly), overwrite: true);

        return harnessDll;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunChildProbe(
        string harnessDll,
        string gameDir,
        string dataDir,
        params string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveDotnetExecutable(),
            WorkingDirectory = Path.GetDirectoryName(harnessDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Remove(JitProfileManager.DisableEnvVar);
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add(gameDir);
        psi.ArgumentList.Add(dataDir);
        foreach (string arg in extraArgs)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet child process.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Timed out waiting for JitProfileChildProbe subprocess.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string ResolveDotnetExecutable()
    {
        string? envHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(envHost) && File.Exists(envHost))
            return envHost;

        string? currentProcess = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcess))
        {
            string fileName = Path.GetFileNameWithoutExtension(currentProcess);
            if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
                return currentProcess;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
    }

    private string CreateDir(params string[] parts)
    {
        string path = root;
        foreach (string part in parts)
            path = Path.Combine(path, part);
        Directory.CreateDirectory(path);
        return path;
    }

    private static CacheManifest CreateManifest(
        string optimumVersion,
        string gameVersion,
        string cachedHash,
        string donorHash,
        string vanillaHash)
    {
        return new CacheManifest
        {
            OptimumVersion = optimumVersion,
            GameVersion = gameVersion,
            CreatedAt = DateTime.UtcNow,
            Targets =
            [
                new CacheTarget
                {
                    Assembly = "VintagestoryLib.dll",
                    CachedHash = cachedHash,
                    Donor = "VintagestoryLib.Donor.dll",
                    DonorHash = donorHash,
                    VanillaHash = vanillaHash
                }
            ]
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
