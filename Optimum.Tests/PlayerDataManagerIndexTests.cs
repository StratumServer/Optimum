#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Optimum.Tests;

public class PlayerDataManagerIndexTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class PatchedLibHarness : IDisposable
    {
        private readonly AssemblyLoadContext? alc;
        private readonly object? originalLogger;
        private readonly FieldInfo? loggerField;
        private readonly string? tempDataDir;
        private readonly MethodInfo? setDataPathMethod;
        private readonly string? previousDataPath;

        public Assembly LibAsm { get; }
        public Assembly ApiAsm { get; }
        public Type PlayerDataManagerType { get; }
        public Type PlayerEntryType { get; }

        public PatchedLibHarness(string tempDir)
        {
            string repoRoot = RepoRoot();
            string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"))
                ? "Release"
                : "Debug";
            string patchedPath = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib-patched.dll");
            if (!File.Exists(patchedPath))
            {
                LibAsm = null!;
                ApiAsm = null!;
                PlayerDataManagerType = null!;
                PlayerEntryType = null!;
                return;
            }

            tempDataDir = tempDir;
            Directory.CreateDirectory(Path.Combine(tempDir, "Logs"));
            Directory.CreateDirectory(Path.Combine(tempDir, "Playerdata"));

            alc = new AssemblyLoadContext("PlayerDataManagerIndexTest", isCollectible: true);
            string libDir = Path.GetDirectoryName(patchedPath)!;
            string outDir = Path.Combine(repoRoot, "bin", cfgDir, "net10.0");
            string vanillaLibDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib");
            alc.Resolving += (ctx, name) =>
            {
                foreach (string dir in new[] { libDir, outDir, vanillaLibDir, AppContext.BaseDirectory })
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return ctx.LoadFromAssemblyPath(candidate);
                    }
                }
                return null;
            };

            LibAsm = alc.LoadFromAssemblyPath(patchedPath);
            ApiAsm = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == "VintagestoryAPI")
                ?? alc.LoadFromAssemblyName(new AssemblyName("VintagestoryAPI"));
            PlayerDataManagerType = LibAsm.GetType("Vintagestory.Server.PlayerDataManager", throwOnError: true)!;
            PlayerEntryType = LibAsm.GetType("Vintagestory.Server.PlayerEntry", throwOnError: true)!;

            Type gamePathsType = ApiAsm.GetType("Vintagestory.API.Config.GamePaths", throwOnError: true)!;
            FieldInfo? dataPathField = gamePathsType.GetField("DataPath", BindingFlags.Public | BindingFlags.Static);
            previousDataPath = dataPathField?.GetValue(null) as string;
            setDataPathMethod = gamePathsType.GetMethod("SetDataPath", BindingFlags.Public | BindingFlags.Static);
            if (setDataPathMethod != null)
            {
                setDataPathMethod.Invoke(null, [tempDir]);
            }
            else if (dataPathField != null)
            {
                dataPathField.SetValue(null, tempDir);
            }

            Type serverMainType = LibAsm.GetType("Vintagestory.Server.ServerMain", throwOnError: true)!;
            loggerField = serverMainType.GetField("Logger", BindingFlags.Public | BindingFlags.Static)!;
            originalLogger = loggerField.GetValue(null);
            if (originalLogger == null)
            {
                Type serverArgsType = LibAsm.GetType("Vintagestory.Server.ServerProgramArgs", throwOnError: true)!;
                Type serverLoggerType = LibAsm.GetType("Vintagestory.Server.ServerLogger", throwOnError: true)!;
                object args = Activator.CreateInstance(serverArgsType)!;
                object logger = Activator.CreateInstance(serverLoggerType, [args])!;
                loggerField.SetValue(null, logger);
            }
        }

        public bool IsAvailable => LibAsm != null;

        public object CreateManager()
        {
            object pdm = RuntimeHelpers.GetUninitializedObject(PlayerDataManagerType);
            Type listType = typeof(List<>).MakeGenericType(PlayerEntryType);
            PlayerDataManagerType.GetField("BannedPlayers")!.SetValue(pdm, Activator.CreateInstance(listType));
            PlayerDataManagerType.GetField("WhitelistedPlayers")!.SetValue(pdm, Activator.CreateInstance(listType));
            PlayerDataManagerType.GetField("WorldDataByUID")!.SetValue(pdm,
                Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(
                    typeof(string),
                    LibAsm.GetType("Vintagestory.Server.ServerWorldPlayerData", throwOnError: true)!)));
            return pdm;
        }

        public object CreateEntry(string? name, string? uid, DateTime? until = null, string reason = "", string by = "Admin")
        {
            object entry = Activator.CreateInstance(PlayerEntryType)!;
            PlayerEntryType.GetField("PlayerName")!.SetValue(entry, name);
            PlayerEntryType.GetField("PlayerUID")!.SetValue(entry, uid);
            PlayerEntryType.GetField("UntilDate")!.SetValue(entry, until);
            PlayerEntryType.GetField("Reason")!.SetValue(entry, reason);
            PlayerEntryType.GetField("IssuedByPlayerName")!.SetValue(entry, by);
            return entry;
        }

        public object? GetPlayerBan(object pdm, string? name, string? uid)
            => PlayerDataManagerType.GetMethod("GetPlayerBan")!.Invoke(pdm, [name, uid]);

        public object? GetPlayerWhitelist(object pdm, string? name, string? uid)
            => PlayerDataManagerType.GetMethod("GetPlayerWhitelist")!.Invoke(pdm, [name, uid]);

        public void BanPlayer(object pdm, string? name, string? uid, string by = "Admin", string reason = "test", DateTime? until = null)
            => PlayerDataManagerType.GetMethod("BanPlayer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(pdm, [name, uid, by, reason, until]);

        public bool UnbanPlayer(object pdm, string? name, string? uid, string by = "Admin")
            => (bool)PlayerDataManagerType.GetMethod("UnbanPlayer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(pdm, [name, uid, by])!;

        public void WhitelistPlayer(object pdm, string? name, string? uid, string by = "Admin", string reason = "test", DateTime? until = null)
            => PlayerDataManagerType.GetMethod("WhitelistPlayer")!
                .Invoke(pdm, [name, uid, by, reason, until]);

        public bool UnWhitelistPlayer(object pdm, string? name, string? uid)
            => (bool)PlayerDataManagerType.GetMethod("UnWhitelistPlayer")!
                .Invoke(pdm, [name, uid])!;

        public object? InvokeGetPlayerEntry(object pdm, object list, string? uid, string? name)
            => PlayerDataManagerType.GetMethod("GetPlayerEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pdm, [list, uid, name]);

        public void Load(object pdm)
            => PlayerDataManagerType.GetMethod("Load")!.Invoke(pdm, null);

        public IList BannedList(object pdm)
            => (IList)PlayerDataManagerType.GetField("BannedPlayers")!.GetValue(pdm)!;

        public IList WhitelistedList(object pdm)
            => (IList)PlayerDataManagerType.GetField("WhitelistedPlayers")!.GetValue(pdm)!;

        public bool IsBannedDirty(object pdm)
            => (bool)PlayerDataManagerType.GetField("bannedListDirty")!.GetValue(pdm)!;

        public bool IsWhitelistDirty(object pdm)
            => (bool)PlayerDataManagerType.GetField("whiteListDirty")!.GetValue(pdm)!;

        public void Dispose()
        {
            if (loggerField != null)
            {
                try { loggerField.SetValue(null, originalLogger); } catch { }
            }
            if (setDataPathMethod != null && !string.IsNullOrEmpty(previousDataPath))
            {
                try { setDataPathMethod.Invoke(null, [previousDataPath]); } catch { }
            }
            alc?.Unload();
            if (tempDataDir != null)
            {
                try { Directory.Delete(tempDataDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PlayerDataManagerIndex_PatchesAndCecilRegistrationsExist()
    {
        string patch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/PlayerDataManager.cs.patch");
        string indexSource = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VintagestoryLib/Vintagestory.Server/OptimumPlayerEntryIndex.cs"));
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("OptimumPlayerEntryIndex", patch);
        Assert.Contains("optimumBanIndex", patch);
        Assert.Contains("optimumWhitelistIndex", patch);
        Assert.Contains("GetModerationEntry", patch);
        Assert.Contains("internal sealed class OptimumPlayerEntryIndex", indexSource);
        Assert.Contains("StringComparer.OrdinalIgnoreCase", indexSource);

        Assert.Contains("\"Vintagestory.Server.OptimumPlayerEntryIndex\"", programSource);
        Assert.Contains("[\"Vintagestory.Server.PlayerDataManager\"] = new()", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"BanPlayer\", 5)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"UnbanPlayer\", 3)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"UnWhitelistPlayer\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"WhitelistPlayer\", 5)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"GetPlayerBan\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"GetPlayerWhitelist\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.PlayerDataManager\", \"GetPlayerEntry\", 3)", programSource);
    }

    [Fact]
    public void IndexedLookups_MatchUidCaseInsensitiveNameAndNullUidBackfill()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-pdm-idx-" + Guid.NewGuid().ToString("N"));
        using var harness = new PatchedLibHarness(tempDir);
        if (!harness.IsAvailable)
        {
            return;
        }

        object pdm = harness.CreateManager();

        harness.BanPlayer(pdm, "Alice", "uid-alice", "Op", "grief", DateTime.UtcNow.AddDays(1));
        harness.BanPlayer(pdm, "LegacyNoUid", null, "Op", "legacy");

        // Exact UID hit even if player renamed themselves
        object? uidHit = harness.GetPlayerBan(pdm, "AliceRenamed", "uid-alice");
        Assert.NotNull(uidHit);

        // Name-only lookup (playeruid == null) matches UID-populated entries case-insensitively
        object? nameOnlyHit = harness.GetPlayerBan(pdm, "aLiCe", null);
        Assert.Same(uidHit, nameOnlyHit);

        // First lookup with UID against null-UID legacy entry backfills UID and updates index
        object? legacyHit = harness.GetPlayerBan(pdm, "legacynouid", "uid-legacy-1");
        Assert.NotNull(legacyHit);
        Assert.Equal("uid-legacy-1", harness.PlayerEntryType.GetField("PlayerUID")!.GetValue(legacyHit));
        Assert.True(harness.IsBannedDirty(pdm));

        // Subsequent lookup by backfilled UID succeeds even with new player name
        Assert.Same(legacyHit, harness.GetPlayerBan(pdm, "CompletelyDifferentName", "uid-legacy-1"));

        // Different UID must NOT match once the entry's null UID was promoted
        Assert.Null(harness.GetPlayerBan(pdm, "LegacyNoUid", "uid-attacker"));

        // Null name & null UID returns null unless a null-name entry exists (no ArgumentNullException)
        Assert.Null(harness.GetPlayerBan(pdm, null, null));

        // Whitelist updates in place and unwhitelists cleanly
        harness.WhitelistPlayer(pdm, "Builder", "uid-builder", reason: "initial");
        harness.WhitelistPlayer(pdm, "Builder", "uid-builder", reason: "updated");
        Assert.Single(harness.WhitelistedList(pdm));
        Assert.True(harness.UnWhitelistPlayer(pdm, "BUILDER", null));
        Assert.Null(harness.GetPlayerWhitelist(pdm, "Builder", "uid-builder"));
        Assert.False(harness.UnWhitelistPlayer(pdm, "Builder", "uid-builder"));

        // Unban removes from both list and index
        Assert.True(harness.UnbanPlayer(pdm, "Alice", "uid-alice"));
        Assert.Null(harness.GetPlayerBan(pdm, "Alice", "uid-alice"));
        Assert.Null(harness.GetPlayerBan(pdm, "Alice", null));
    }

    [Fact]
    public void IndexedLookups_PreserveFirstHitOrderOnDuplicatesAndSyncExternalListMutations()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-pdm-dup-" + Guid.NewGuid().ToString("N"));
        using var harness = new PatchedLibHarness(tempDir);
        if (!harness.IsAvailable)
        {
            return;
        }

        object pdm = harness.CreateManager();
        IList banned = harness.BannedList(pdm);

        object firstNullUid = harness.CreateEntry("Shared", null, reason: "first-null");
        object secondSameUid = harness.CreateEntry("Other", "uid-shared", reason: "second-uid");
        object thirdSameUid = harness.CreateEntry("Third", "uid-shared", reason: "third-uid");
        banned.Add(firstNullUid);
        banned.Add(secondSameUid);
        banned.Add(thirdSameUid);

        // Vanilla foreach stops at index 0 (null UID matching "shared") before index 1 ("uid-shared")
        object? hit = harness.InvokeGetPlayerEntry(pdm, banned, "uid-shared", "shared");
        Assert.Same(firstNullUid, hit);

        // Removing duplicate entry uncovers the next matching UID entry cleanly
        Assert.True(harness.UnbanPlayer(pdm, "Other", "uid-shared"));
        Assert.Same(thirdSameUid, harness.GetPlayerBan(pdm, "Third", "uid-shared"));

        // External list replacement & mutation auto-syncs cleanly
        Type listType = typeof(List<>).MakeGenericType(harness.PlayerEntryType);
        IList replaced = (IList)Activator.CreateInstance(listType)!;
        object fresh = harness.CreateEntry("Fresh", "uid-fresh");
        replaced.Add(fresh);
        harness.PlayerDataManagerType.GetField("BannedPlayers")!.SetValue(pdm, replaced);
        Assert.Same(fresh, harness.GetPlayerBan(pdm, "Fresh", "uid-fresh"));
        Assert.Null(harness.GetPlayerBan(pdm, "Third", "uid-shared"));

        // Direct in-place index overwrite (same List reference and same Count) is detected via _version
        object swapped = harness.CreateEntry("Swapped", "uid-swapped");
        replaced[0] = swapped;
        Assert.Null(harness.GetPlayerBan(pdm, "Fresh", "uid-fresh"));
        Assert.Same(swapped, harness.GetPlayerBan(pdm, "Swapped", "uid-swapped"));

        // Same-count RemoveAt + Add direct mutation is also detected cleanly
        object replacedViaOps = harness.CreateEntry("RemovedAdded", "uid-removed-added");
        replaced.RemoveAt(0);
        replaced.Add(replacedViaOps);
        Assert.Null(harness.GetPlayerBan(pdm, "Swapped", "uid-swapped"));
        Assert.Same(replacedViaOps, harness.GetPlayerBan(pdm, "RemovedAdded", "uid-removed-added"));

        // Runtime Ban/Unban and Whitelist/UnWhitelist cycles keep index accurate across repeated toggles
        harness.BanPlayer(pdm, "CycleUser", "uid-cycle", reason: "first-ban");
        Assert.NotNull(harness.GetPlayerBan(pdm, "CycleUser", "uid-cycle"));
        Assert.True(harness.UnbanPlayer(pdm, "CycleUser", "uid-cycle"));
        Assert.Null(harness.GetPlayerBan(pdm, "CycleUser", "uid-cycle"));
        harness.BanPlayer(pdm, "CycleUser", "uid-cycle", reason: "second-ban");
        Assert.NotNull(harness.GetPlayerBan(pdm, "CycleUser", "uid-cycle"));

        harness.WhitelistPlayer(pdm, "CycleWl", "uid-cycle-wl", reason: "allow-1");
        Assert.NotNull(harness.GetPlayerWhitelist(pdm, "CycleWl", "uid-cycle-wl"));
        Assert.True(harness.UnWhitelistPlayer(pdm, "CycleWl", "uid-cycle-wl"));
        Assert.Null(harness.GetPlayerWhitelist(pdm, "CycleWl", "uid-cycle-wl"));
        harness.WhitelistPlayer(pdm, "CycleWl", "uid-cycle-wl", reason: "allow-2");
        Assert.NotNull(harness.GetPlayerWhitelist(pdm, "CycleWl", "uid-cycle-wl"));

        // Arbitrary unattached List<PlayerEntry> still works through GetPlayerEntry fallback
        IList customList = (IList)Activator.CreateInstance(listType)!;
        object customEntry = harness.CreateEntry("Custom", "uid-custom");
        customList.Add(customEntry);
        Assert.Same(customEntry, harness.InvokeGetPlayerEntry(pdm, customList, "uid-custom", "custom"));
    }

    [Fact]
    public void Load_FiltersExpiredBansAndPopulatesIndexesFromDisk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-pdm-load-" + Guid.NewGuid().ToString("N"));
        using var harness = new PatchedLibHarness(tempDir);
        if (!harness.IsAvailable)
        {
            return;
        }

        string playerDataDir = Path.Combine(tempDir, "Playerdata");
        DateTime future = DateTime.Now.AddDays(5);
        DateTime past = DateTime.Now.AddDays(-5);

        File.WriteAllText(Path.Combine(playerDataDir, "playersbanned.json"), $$"""
            [
              { "PlayerName": "Expired", "PlayerUID": "uid-expired", "UntilDate": "{{past:O}}", "Reason": "old", "IssuedByPlayerName": "Admin" },
              { "PlayerName": "Active", "PlayerUID": "uid-active", "UntilDate": "{{future:O}}", "Reason": "active", "IssuedByPlayerName": "Admin" }
            ]
            """);
        File.WriteAllText(Path.Combine(playerDataDir, "playerswhitelisted.json"), """
            [
              { "PlayerName": "Vip", "PlayerUID": "uid-vip", "Reason": "member", "IssuedByPlayerName": "Admin" }
            ]
            """);

        object pdm = harness.CreateManager();
        harness.Load(pdm);

        Assert.Single(harness.BannedList(pdm));
        Assert.True(harness.IsBannedDirty(pdm));
        Assert.Null(harness.GetPlayerBan(pdm, "Expired", "uid-expired"));
        Assert.NotNull(harness.GetPlayerBan(pdm, "Active", "uid-active"));
        Assert.NotNull(harness.GetPlayerWhitelist(pdm, "vip", null));
    }

    [Fact]
    public void IndexedLookups_HandleTenThousandEntriesAtConstantTimeSpeed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-pdm-bench-" + Guid.NewGuid().ToString("N"));
        using var harness = new PatchedLibHarness(tempDir);
        if (!harness.IsAvailable)
        {
            return;
        }

        const int entryCount = 10_000;
        const int lookupCount = 10_000;
        object pdm = harness.CreateManager();
        IList banned = harness.BannedList(pdm);
        IList whitelisted = harness.WhitelistedList(pdm);

        for (int i = 0; i < entryCount; i++)
        {
            banned.Add(harness.CreateEntry($"Banned{i}", $"uid-ban-{i:D5}"));
            whitelisted.Add(harness.CreateEntry($"White{i}", $"uid-wl-{i:D5}"));
        }

        MethodInfo getBan = harness.PlayerDataManagerType.GetMethod("GetPlayerBan")!;
        MethodInfo getWhitelist = harness.PlayerDataManagerType.GetMethod("GetPlayerWhitelist")!;

        Func<string?, string?, object?> banLookup = CompileLookupDelegate(pdm, getBan);
        Func<string?, string?, object?> whitelistLookup = CompileLookupDelegate(pdm, getWhitelist);

        // Warm index before timing steady-state join lookups
        Assert.NotNull(banLookup("Banned0", "uid-ban-00000"));
        Assert.NotNull(whitelistLookup("White0", "uid-wl-00000"));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < lookupCount; i++)
        {
            Assert.NotNull(banLookup($"Banned{i}", $"uid-ban-{i:D5}"));
            Assert.Null(banLookup($"NotBanned{i}", $"uid-missing-{i:D5}"));
            Assert.NotNull(whitelistLookup($"White{i}", $"uid-wl-{i:D5}"));
            Assert.Null(whitelistLookup($"NotWhite{i}", $"uid-missing-wl-{i:D5}"));
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"40,000 indexed lookups across 10,000 entries took {sw.ElapsedMilliseconds} ms");
    }

    private static Func<string?, string?, object?> CompileLookupDelegate(object target, MethodInfo method)
    {
        var nameParam = System.Linq.Expressions.Expression.Parameter(typeof(string), "playername");
        var uidParam = System.Linq.Expressions.Expression.Parameter(typeof(string), "playeruid");
        var call = System.Linq.Expressions.Expression.Call(
            System.Linq.Expressions.Expression.Constant(target),
            method,
            nameParam,
            uidParam);
        var boxed = System.Linq.Expressions.Expression.Convert(call, typeof(object));
        return System.Linq.Expressions.Expression.Lambda<Func<string?, string?, object?>>(boxed, nameParam, uidParam).Compile();
    }
}
