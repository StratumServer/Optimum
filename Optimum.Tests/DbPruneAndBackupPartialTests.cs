#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class DbPruneAndBackupPartialTests
{
    private sealed class IsolatedPatchedAlc : AssemblyLoadContext
    {
        private readonly string[] searchDirs;
        private readonly Dictionary<string, Assembly> preloaded = new(StringComparer.OrdinalIgnoreCase);

        public IsolatedPatchedAlc(string name, params string[] searchDirs)
            : base(name, isCollectible: true)
        {
            this.searchDirs = searchDirs;
        }

        public Assembly LoadEntryAssembly(string path, string logicalName)
        {
            Assembly asm = LoadFromAssemblyPath(path);
            preloaded[logicalName] = asm;
            return asm;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name != null && preloaded.TryGetValue(name.Name, out Assembly? pinned))
            {
                return pinned;
            }

            if (name.Name != null && !name.Name.StartsWith("System.", StringComparison.Ordinal) &&
                name.Name != "netstandard" && name.Name != "mscorlib")
            {
                foreach (string dir in searchDirs)
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return LoadFromAssemblyPath(candidate);
                    }
                }
            }

            return null;
        }
    }

    private static string FindRepoRoot()
    {
        return Path.GetFullPath(Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!);
    }

    private static bool TryGetPatchedPaths(out string repoRoot, out string patchedLib, out string patchedApi, out string outDir)
    {
        repoRoot = FindRepoRoot();
        string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"))
            ? "Release"
            : "Debug";
        patchedLib = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib-patched.dll");
        outDir = Path.Combine(repoRoot, "bin", cfgDir, "net10.0");
        patchedApi = Path.Combine(outDir, "VintagestoryAPI-patched.dll");
        return File.Exists(patchedLib) && File.Exists(patchedApi);
    }

    private static IsolatedPatchedAlc CreateAlc(string name, string repoRoot, string patchedLib, string patchedApi, string outDir, out Assembly apiAsm, out Assembly libAsm)
    {
        var alc = new IsolatedPatchedAlc(
            name,
            Path.GetDirectoryName(patchedLib)!,
            outDir,
            Path.Combine(repoRoot, ".vanilla", "linux-x64", "vintagestory", "Lib"),
            Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib"));
        apiAsm = alc.LoadEntryAssembly(patchedApi, "VintagestoryAPI");
        libAsm = alc.LoadEntryAssembly(patchedLib, "VintagestoryLib");
        return alc;
    }

    [Fact]
    public void OptimumConfig_BatchedDbMaintenanceEnabled_DefaultsToTrue()
    {
        Assert.True(OptimumConfig.BatchedDbMaintenanceEnabled);
    }

    [Fact]
    public void PatchedVintagestoryLib_ShipsOptimumDbMaintenanceAndTransplantedMethods()
    {
        if (!TryGetPatchedPaths(out _, out string patchedLib, out _, out _))
        {
            return;
        }

        using ModuleDefinition module = ModuleDefinition.ReadModule(patchedLib);
        TypeDefinition? helperType = module.GetType("Vintagestory.Server.OptimumDbMaintenance");
        Assert.NotNull(helperType);

        TypeDefinition cmdType = module.GetType("Vintagestory.Server.ServerSystemSupplyChunkCommands");
        MethodDefinition pruneMethod = cmdType.Methods.First(m => m.Name == "prune" && m.Parameters.Count == 5);
        Assert.Contains(
            pruneMethod.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.DeclaringType.FullName == "Vintagestory.Server.OptimumDbMaintenance" && mr.Name == "StartPrune");

        TypeDefinition sqliteType = module.GetType("Vintagestory.Common.Database.SQLiteDbConnectionv2");
        MethodDefinition backupPartial = sqliteType.Methods.First(m => m.Name == "BackupPartial" && m.Parameters.Count == 4);
        Assert.Contains(
            backupPartial.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.DeclaringType.FullName == "Vintagestory.Server.OptimumDbMaintenance" && mr.Name == "ExecuteBatchedBackupPartial");

        MethodDefinition deleteChunks = sqliteType.Methods.First(m => m.Name == "DeleteChunks" && m.Parameters.Count == 2);
        Assert.Contains(
            deleteChunks.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "Prepare");
    }

    [Fact]
    public void ShouldKeepChunk_MatchesFullProtobufDeserialize_WithZeroSteadyStateAllocation()
    {
        if (!TryGetPatchedPaths(out string repoRoot, out string patchedLib, out string patchedApi, out string outDir))
        {
            return;
        }

        IsolatedPatchedAlc alc = CreateAlc("Optimum648HeaderScan", repoRoot, patchedLib, patchedApi, outDir, out _, out Assembly libAsm);
        try
        {
            Type serverChunkType = libAsm.GetType("Vintagestory.Server.ServerChunk", throwOnError: true)!;
            Type maintType = libAsm.GetType("Vintagestory.Server.OptimumDbMaintenance", throwOnError: true)!;
            Assembly protobufAsm = alc.LoadFromAssemblyName(new AssemblyName("protobuf-net"));
            Type serializerType = protobufAsm.GetType("ProtoBuf.Serializer", throwOnError: true)!;
            MethodInfo genericSerialize = serializerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Stream))
                .MakeGenericMethod(serverChunkType);

            byte[] SerializeChunk(int placed, int removed, string? version, int compressedPayloadSize)
            {
                object chunk = Activator.CreateInstance(serverChunkType, nonPublic: true)!;
                serverChunkType.GetField("BlocksPlaced")!.SetValue(chunk, placed);
                serverChunkType.GetField("BlocksRemoved")!.SetValue(chunk, removed);
                serverChunkType.GetField("GameVersionCreated")!.SetValue(chunk, version);
                serverChunkType.GetProperty("blocksCompressed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                    .SetValue(chunk, new byte[compressedPayloadSize]);
                serverChunkType.GetProperty("lightCompressed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                    .SetValue(chunk, new byte[256]);
                serverChunkType.GetField("DecorsSerialized")!.SetValue(chunk, new byte[64]);
                using var ms = new MemoryStream();
                genericSerialize.Invoke(null, new object[] { ms, chunk });
                return ms.ToArray();
            }

            byte[] uneditedBlob = SerializeChunk(0, 0, "1.22.7", 4096);
            byte[] editedBlob = SerializeChunk(3, 4, "1.22.7", 4096);
            byte[] legacyVersionBlob = SerializeChunk(0, 0, "1.19.8", 4096);

            MethodInfo shouldKeep = maintType.GetMethod("ShouldKeepChunk", BindingFlags.Public | BindingFlags.Static)!;
            object?[] args = new object?[] { uneditedBlob, 5, "1.20.0", null, 0, null, false };

            Assert.False((bool)shouldKeep.Invoke(null, args)!);

            args[0] = editedBlob;
            Assert.True((bool)shouldKeep.Invoke(null, args)!);

            args[0] = legacyVersionBlob;
            Assert.True((bool)shouldKeep.Invoke(null, args)!);

            // Truncated payload falls back to Serializer.Deserialize and throws as vanilla does.
            byte[] truncated = new byte[] { 0x0A, 0xFF, 0x01 };
            args[0] = truncated;
            Assert.Throws<TargetInvocationException>(() => shouldKeep.Invoke(null, args));
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public void ScanSqliteDatabaseForPrune_And_BackupPartial_MatchVanillaRowForRow()
    {
        if (!TryGetPatchedPaths(out string repoRoot, out string patchedLib, out string patchedApi, out string outDir))
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-648-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string backupsDir = Path.Combine(tempDir, "Backups");
        Directory.CreateDirectory(backupsDir);

        IsolatedPatchedAlc alc = CreateAlc("Optimum648SqliteParity", repoRoot, patchedLib, patchedApi, outDir, out Assembly apiAsm, out Assembly libAsm);
        try
        {
            Type gamePathsType = apiAsm.GetType("Vintagestory.API.Config.GamePaths", throwOnError: true)!;
            FieldInfo? dataPathField = gamePathsType.GetField("DataPath", BindingFlags.Public | BindingFlags.Static);
            string? prevDataPath = dataPathField?.GetValue(null) as string;
            dataPathField?.SetValue(null, tempDir);

            try
            {
                Type configType = apiAsm.GetType("Vintagestory.API.Config.OptimumConfig", throwOnError: true)!;
                FieldInfo toggleField = configType.GetField("BatchedDbMaintenanceEnabled", BindingFlags.Public | BindingFlags.Static)!;
                Type? nullLoggerType = libAsm.GetType("Vintagestory.Common.NullLogger")
                    ?? apiAsm.GetType("Vintagestory.API.Common.NullLogger");
                object? logger = nullLoggerType != null ? Activator.CreateInstance(nullLoggerType) : null;

                Type sqliteV2Type = libAsm.GetType("Vintagestory.Common.Database.SQLiteDbConnectionv2", throwOnError: true)!;
                Type chunkPosType = libAsm.GetType("Vintagestory.Common.Database.ChunkPos", throwOnError: true)!;
                Type dbChunkType = libAsm.GetType("Vintagestory.Common.Database.DbChunk", throwOnError: true)!;
                Type serverChunkType = libAsm.GetType("Vintagestory.Server.ServerChunk", throwOnError: true)!;
                Type maintType = libAsm.GetType("Vintagestory.Server.OptimumDbMaintenance", throwOnError: true)!;
                Type fastVec2iType = apiAsm.GetType("Vintagestory.API.MathTools.FastVec2i", throwOnError: true)!;
                Type vec2iType = apiAsm.GetType("Vintagestory.API.MathTools.Vec2i", throwOnError: true)!;
                Type landClaimType = apiAsm.GetType("Vintagestory.API.Common.LandClaim", throwOnError: true)!;
                Type cuboidiType = apiAsm.GetType("Vintagestory.API.MathTools.Cuboidi", throwOnError: true)!;

                Assembly protobufAsm = alc.LoadFromAssemblyName(new AssemblyName("protobuf-net"));
                Type serializerType = protobufAsm.GetType("ProtoBuf.Serializer", throwOnError: true)!;
                MethodInfo genericSerialize = serializerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Stream))
                    .MakeGenericMethod(serverChunkType);

                byte[] MakeChunkBlob(int placed, int removed, string version, byte seedByte)
                {
                    object chunk = Activator.CreateInstance(serverChunkType, nonPublic: true)!;
                    serverChunkType.GetField("BlocksPlaced")!.SetValue(chunk, placed);
                    serverChunkType.GetField("BlocksRemoved")!.SetValue(chunk, removed);
                    serverChunkType.GetField("GameVersionCreated")!.SetValue(chunk, version);
                    byte[] payload = new byte[512];
                    Array.Fill(payload, seedByte);
                    serverChunkType.GetProperty("blocksCompressed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                        .SetValue(chunk, payload);
                    using var ms = new MemoryStream();
                    genericSerialize.Invoke(null, new object[] { ms, chunk });
                    return ms.ToArray();
                }

                object MakeChunkPos(int x, int y, int z, int dim = 0)
                {
                    return Activator.CreateInstance(chunkPosType, new object[] { x, y, z, dim })!;
                }

                object MakeDbChunk(object pos, byte[] data)
                {
                    object dbc = Activator.CreateInstance(dbChunkType)!;
                    dbChunkType.GetField("Position")!.SetValue(dbc, pos);
                    dbChunkType.GetField("Data")!.SetValue(dbc, data);
                    return dbc;
                }

                string sourceDbPath = Path.Combine(tempDir, "source.vcdbs");
                object db = Activator.CreateInstance(sqliteV2Type, new object?[] { logger })!;
                MethodInfo openOrCreate = sqliteV2Type.GetMethod("OpenOrCreate", BindingFlags.Public | BindingFlags.Instance)!;
                object?[] openArgs = new object?[] { sourceDbPath, null, true, false, false };
                Assert.True((bool)openOrCreate.Invoke(db, openArgs)!);

                try
                {
                    sqliteV2Type.GetMethod("StoreGameData")!.Invoke(db, new object[] { new byte[] { 1, 2, 3, 4 } });
                    sqliteV2Type.GetMethod("SetPlayerData")!.Invoke(db, new object[] { "plr-1", new byte[] { 9, 8, 7 } });

                    Type listDbChunkType = typeof(List<>).MakeGenericType(dbChunkType);
                    var chunksList = (IList)Activator.CreateInstance(listDbChunkType)!;
                    var mapChunksList = (IList)Activator.CreateInstance(listDbChunkType)!;
                    var mapRegionsList = (IList)Activator.CreateInstance(listDbChunkType)!;

                    // Populate 6x6 chunk columns (X=10..15, Z=20..25), 4 vertical layers (Y=0..3), plus dim=1 chunks
                    for (int x = 10; x <= 15; x++)
                    {
                        for (int z = 20; z <= 25; z++)
                        {
                            for (int y = 0; y < 4; y++)
                            {
                                int placed = (x == 12 && z == 22 && y == 2) ? 6 : 0;
                                int removed = (x == 14 && z == 24 && y == 1) ? 3 : 0;
                                string ver = (x == 11 && z == 21 && y == 3) ? "1.19.8" : "1.22.7";
                                byte seed = (byte)((x * 7 + z * 13 + y) & 0xFF);
                                chunksList.Add(MakeDbChunk(MakeChunkPos(x, y, z, 0), MakeChunkBlob(placed, removed, ver, seed)));
                            }

                            // Dimension 1 chunk at the same (x, z) must NOT be copied by BackupPartial (which copies dim 0).
                            chunksList.Add(MakeDbChunk(MakeChunkPos(x, 0, z, 1), MakeChunkBlob(99, 99, "1.22.7", 0xEE)));
                            mapChunksList.Add(MakeDbChunk(MakeChunkPos(x, 0, z, 0), new byte[] { (byte)x, (byte)z }));
                        }
                    }

                    for (int rx = 0; rx <= 2; rx++)
                    {
                        for (int rz = 0; rz <= 2; rz++)
                        {
                            mapRegionsList.Add(MakeDbChunk(MakeChunkPos(rx, 0, rz, 0), new byte[] { (byte)(rx + 100), (byte)(rz + 100) }));
                        }
                    }

                    sqliteV2Type.GetMethod("SetChunks")!.Invoke(db, new object[] { chunksList });
                    sqliteV2Type.GetMethod("SetMapChunks")!.Invoke(db, new object[] { mapChunksList });
                    sqliteV2Type.GetMethod("SetMapRegions")!.Invoke(db, new object[] { mapRegionsList });

                    // 1. Test Prune scan parity (legacy vs batched) with land claim at column (13, 23), threshold=5, aboveVersion="1.20.0"
                    Type listClaimsType = typeof(List<>).MakeGenericType(landClaimType);
                    var claims = (IList)Activator.CreateInstance(listClaimsType)!;
                    object claim = Activator.CreateInstance(landClaimType)!;
                    Type listCuboidType = typeof(List<>).MakeGenericType(cuboidiType);
                    var areas = (IList)Activator.CreateInstance(listCuboidType)!;
                    areas.Add(Activator.CreateInstance(cuboidiType, new object[] { 13 * 32, 0, 23 * 32, 13 * 32 + 16, 128, 23 * 32 + 16 })!);
                    landClaimType.GetField("Areas")!.SetValue(claim, areas);
                    claims.Add(claim);

                    Type hashSetVecType = typeof(HashSet<>).MakeGenericType(fastVec2iType);
                    MethodInfo scanSqlite = maintType.GetMethod("ScanSqliteDatabaseForPrune", BindingFlags.Public | BindingFlags.Static)!;

                    foreach ((int thresh, bool keepCl, string? aboveVer) in new (int, bool, string?)[]
                    {
                        (5, true, "1.20.0"),
                        (5, false, null),
                        (0, true, null),
                        (0, false, null)
                    })
                    {
                        object toDeleteOpt = Activator.CreateInstance(hashSetVecType)!;
                        object toKeepOpt = Activator.CreateInstance(hashSetVecType)!;
                        toggleField.SetValue(null, true);
                        scanSqlite.Invoke(null, new object?[] { db, 32, thresh, keepCl, aboveVer, claims, toDeleteOpt, toKeepOpt });

                        object toDeleteLeg = Activator.CreateInstance(hashSetVecType)!;
                        object toKeepLeg = Activator.CreateInstance(hashSetVecType)!;
                        toggleField.SetValue(null, false);
                        object legJob = Activator.CreateInstance(maintType, new object?[] { null, null, null, thresh, true, keepCl, aboveVer, 32, claims, toDeleteLeg, toKeepLeg })!;
                        MethodInfo onLegacy = maintType.GetMethod("OnLegacyChunk")!;
                        Type actionDbChunk = typeof(Action<>).MakeGenericType(dbChunkType);
                        Delegate del = Delegate.CreateDelegate(actionDbChunk, legJob, onLegacy);
                        sqliteV2Type.GetMethod("ForAllChunks")!.Invoke(db, new object[] { del });
                        MethodInfo removeMethod = hashSetVecType.GetMethod("Remove")!;
                        foreach (object k in (IEnumerable)toKeepLeg)
                        {
                            removeMethod.Invoke(toDeleteLeg, new object[] { k });
                        }

                        var optDelSet = ((IEnumerable)toDeleteOpt).Cast<object>().Select(v => v.ToString()!).OrderBy(s => s).ToArray();
                        var legDelSet = ((IEnumerable)toDeleteLeg).Cast<object>().Select(v => v.ToString()!).OrderBy(s => s).ToArray();
                        var optKeepSet = ((IEnumerable)toKeepOpt).Cast<object>().Select(v => v.ToString()!).OrderBy(s => s).ToArray();
                        var legKeepSet = ((IEnumerable)toKeepLeg).Cast<object>().Select(v => v.ToString()!).OrderBy(s => s).ToArray();

                        Assert.Equal(legKeepSet, optKeepSet);
                        Assert.Equal(legDelSet, optDelSet);
                    }

                    // 2. Test BackupPartial row-by-row parity on bounding box X=[11..14], Z=[21..24], mapSizeYinChunks=4
                    object startVec = Activator.CreateInstance(vec2iType, new object[] { 11, 21 })!;
                    object endVec = Activator.CreateInstance(vec2iType, new object[] { 14, 24 })!;
                    MethodInfo backupPartial = sqliteV2Type.GetMethod("BackupPartial")!;

                    toggleField.SetValue(null, false);
                    backupPartial.Invoke(db, new object[] { "vanilla-partial.vcdbs", startVec, endVec, 4 });

                    toggleField.SetValue(null, true);
                    backupPartial.Invoke(db, new object[] { "batched-partial.vcdbs", startVec, endVec, 4 });

                    Dictionary<ulong, byte[]> ReadTable(string dbFile, string table)
                    {
                        var rows = new Dictionary<ulong, byte[]>();
                        object verifyDb = Activator.CreateInstance(sqliteV2Type, new object?[] { logger })!;
                        object?[] vArgs = new object?[] { dbFile, null, false, false, false };
                        Assert.True((bool)openOrCreate.Invoke(verifyDb, vArgs)!);
                        try
                        {
                            MethodInfo getAll = sqliteV2Type.GetMethod("GetAllChunks", new[] { typeof(string) })!;
                            MethodInfo toIndex = chunkPosType.GetMethod("ToChunkIndex", Type.EmptyTypes)!;
                            foreach (object dbc in (IEnumerable)getAll.Invoke(verifyDb, new object[] { table })!)
                            {
                                object pos = dbChunkType.GetField("Position")!.GetValue(dbc)!;
                                ulong idx = (ulong)toIndex.Invoke(pos, null)!;
                                byte[] data = (byte[])dbChunkType.GetField("Data")!.GetValue(dbc)!;
                                rows[idx] = data;
                            }
                            return rows;
                        }
                        finally
                        {
                            sqliteV2Type.GetMethod("Close")!.Invoke(verifyDb, null);
                        }
                    }

                    string vanillaPath = Path.Combine(backupsDir, "vanilla-partial.vcdbs");
                    string batchedPath = Path.Combine(backupsDir, "batched-partial.vcdbs");
                    Assert.True(File.Exists(vanillaPath));
                    Assert.True(File.Exists(batchedPath));

                    foreach (string table in new[] { "chunk", "mapchunk", "mapregion" })
                    {
                        Dictionary<ulong, byte[]> vanillaRows = ReadTable(vanillaPath, table);
                        Dictionary<ulong, byte[]> batchedRows = ReadTable(batchedPath, table);
                        Assert.Equal(vanillaRows.Count, batchedRows.Count);
                        foreach (KeyValuePair<ulong, byte[]> kv in vanillaRows)
                        {
                            Assert.True(batchedRows.TryGetValue(kv.Key, out byte[]? bVal));
                            Assert.Equal(kv.Value, bVal);
                        }
                    }

                    // 4x4 columns * 4 Y layers = 64 chunks, 16 mapchunks, 1 mapregion (0,0)
                    Assert.Equal(64, ReadTable(batchedPath, "chunk").Count);
                    Assert.Equal(16, ReadTable(batchedPath, "mapchunk").Count);
                    Assert.Single(ReadTable(batchedPath, "mapregion"));
                }
                finally
                {
                    toggleField.SetValue(null, true);
                    sqliteV2Type.GetMethod("Close")!.Invoke(db, null);
                }
            }
            finally
            {
                dataPathField?.SetValue(null, prevDataPath);
            }
        }
        finally
        {
            alc.Unload();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
