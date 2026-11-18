using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class MultiWorkerConfigAndBoundaryTests
{
    [Fact]
    public void MultiWorkerConfig_DefaultsAndEffectiveScalingWork()
    {
        bool origMulti = OptimumConfig.MultiWorkerTesselationEnabled;
        int origWorkers = OptimumConfig.TesselationWorkers;
        int origCap = OptimumConfig.TesselationWorkerCap;
        bool origPartPool = OptimumConfig.MeshPartPoolEnabled;
        try
        {
            OptimumConfig.MultiWorkerTesselationEnabled = true;
            OptimumConfig.TesselationWorkers = 0;
            OptimumConfig.TesselationWorkerCap = int.MaxValue;
            OptimumConfig.MeshPartPoolEnabled = true;

            Assert.True(OptimumConfig.MeshPartPoolActive);
            Assert.Equal(1, OptimumConfig.GetEffectiveTesselationWorkers(1));
            Assert.Equal(1, OptimumConfig.GetEffectiveTesselationWorkers(2));
            Assert.Equal(2, OptimumConfig.GetEffectiveTesselationWorkers(4));
            Assert.Equal(3, OptimumConfig.GetEffectiveTesselationWorkers(6));
            Assert.Equal(4, OptimumConfig.GetEffectiveTesselationWorkers(8));
            Assert.Equal(4, OptimumConfig.GetEffectiveTesselationWorkers(16));
            Assert.Equal(4, OptimumConfig.GetEffectiveTesselationWorkers(32));

            OptimumConfig.TesselationWorkers = 6;
            Assert.Equal(6, OptimumConfig.GetEffectiveTesselationWorkers(16));

            OptimumConfig.TesselationWorkers = 16;
            Assert.Equal(8, OptimumConfig.GetEffectiveTesselationWorkers(32));

            OptimumConfig.TesselationWorkerCap = 2;
            Assert.Equal(2, OptimumConfig.GetEffectiveTesselationWorkers(16));

            OptimumConfig.MultiWorkerTesselationEnabled = false;
            Assert.Equal(1, OptimumConfig.GetEffectiveTesselationWorkers(16));
        }
        finally
        {
            OptimumConfig.MultiWorkerTesselationEnabled = origMulti;
            OptimumConfig.TesselationWorkers = origWorkers;
            OptimumConfig.TesselationWorkerCap = origCap;
            OptimumConfig.MeshPartPoolEnabled = origPartPool;
        }
    }

    private sealed class ForeignStubBlock { }

    private sealed class ForeignTexSource : ITexPositionSource
    {
        public Size2i AtlasSize => new Size2i(16, 16);
        public TextureAtlasPosition this[string textureCode] => null!;
    }

    [Fact]
    public void ModBoundary_AuditsCoreAssembliesAndSynchronizesForeignTypes()
    {
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(Block)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(MeshData)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(OptimumWorkerMeshShadowCache)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(EntityBehaviorRepulseAgents)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(OptimumCrateTexSource)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(OptimumLanternTexSource)));
        Assert.True(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(OptimumCropPropInvTexSource)));

#pragma warning disable CS9216
        Assert.True(ReferenceEquals(OptimumTesselationModBoundary.TryGetModTypeLock(null), null));
        Assert.True(ReferenceEquals(OptimumTesselationModBoundary.TryGetModTypeLock(typeof(Block)), null));
        Assert.True(ReferenceEquals(OptimumTesselationModBoundary.TryGetModTypeLock(typeof(EntityBehaviorRepulseAgents)), null));
        Assert.True(ReferenceEquals(OptimumTesselationModBoundary.TryGetForeignTexSourceLock(null), null));

        Assert.False(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(ForeignStubBlock)));
        Assert.False(OptimumTesselationModBoundary.IsAuditedSafeType(typeof(ForeignTexSource)));

        Lock? lockA1 = OptimumTesselationModBoundary.TryGetModTypeLock(typeof(ForeignStubBlock));
        Lock? lockA2 = OptimumTesselationModBoundary.TryGetModTypeLock(typeof(ForeignStubBlock));
        Lock? lockB = OptimumTesselationModBoundary.TryGetModTypeLock(typeof(MultiWorkerConfigAndBoundaryTests));
        Lock? texLock = OptimumTesselationModBoundary.TryGetForeignTexSourceLock(new ForeignTexSource());

        Assert.False(ReferenceEquals(lockA1, null));
        Assert.True(ReferenceEquals(lockA1, lockA2));
        Assert.False(ReferenceEquals(lockB, null));
        Assert.False(ReferenceEquals(lockA1, lockB));
        Assert.False(ReferenceEquals(texLock, null));
#pragma warning restore CS9216
    }
}

public sealed class MultiWorkerConcurrencyStressTests
{
    public class ProxyDispatcher : DispatchProxy
    {
        internal System.Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod == null ? null : Handler(targetMethod, args);
        }
    }

    private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatcher>();
        ((ProxyDispatcher)(object)proxy).Handler = handler;
        return proxy;
    }

    [Fact]
    public void WorkerMeshShadowCache_IsolatesFlagsAcrossConcurrentWorkersWithoutCloningGeometry()
    {
        var source = new MeshData(false)
        {
            VerticesPerFace = 4,
            IndicesPerFace = 6,
            VerticesMax = 8,
            IndicesMax = 12,
            xyz = new float[24],
            Uv = new float[16],
            Rgba = new byte[32],
            Flags = new int[] { 0x101, 0x202, 0x303, 0x404, 0, 0, 0, 0 },
            Indices = new int[] { 0, 1, 2, 0, 2, 3 },
        };
        source.SetVerticesCount(4);
        source.SetIndicesCount(6);

        const int threadCount = 8;
        const int iterations = 200;
        using var startGate = new Barrier(threadCount);
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int workerIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    var cache = new OptimumWorkerMeshShadowCache();
                    int bit = 1 << (workerIndex + 16);
                    startGate.SignalAndWait();
                    for (int iter = 0; iter < iterations; iter++)
                    {
                        MeshData shadow = cache.PrepareShadow(source);
                        Assert.Same(source.xyz, shadow.xyz);
                        Assert.Same(source.Uv, shadow.Uv);
                        Assert.Same(source.Indices, shadow.Indices);
                        Assert.NotSame(source.Flags, shadow.Flags);
                        Assert.Equal(0x101, shadow.Flags![0]);

                        shadow.Flags[0] ^= bit;
                        shadow.Flags[3] = workerIndex;
                        shadow.SetVerticesCount(2);
                        Assert.Equal(0x101 ^ bit, shadow.Flags[0]);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })
            {
                IsBackground = true
            };
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.Empty(errors);
        Assert.Equal(0x101, source.Flags[0]);
        Assert.Equal(0x404, source.Flags[3]);
        Assert.Equal(4, source.VerticesCount);
    }

    [Fact]
    public void MeshDataRecycler_PerWorkerBucketsAndCrossThreadRecycleStayIsolatedAndSafe()
    {
        long elapsedMs = 1000L;
        var world = CreateProxy<IClientWorldAccessor>((method, _) =>
            method.Name == "get_ElapsedMilliseconds" ? elapsedMs : null);
        var recycler = new MeshDataRecycler(world);

        const int workerCount = 4;
        const int meshesPerWorker = 40;
        var returnedQueue = new ConcurrentQueue<MeshData>();
        using var barrier = new Barrier(workerCount);
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[workerCount];

        for (int w = 0; w < workerCount; w++)
        {
            threads[w] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    var localRented = new List<MeshData>(meshesPerWorker);
                    for (int i = 0; i < meshesPerWorker; i++)
                    {
                        MeshData m = recycler.GetOrCreateMesh(4096);
                        Assert.True(m.Recyclable);
                        localRented.Add(m);
                    }
                    // Half recycled by self, half handed off to main consumer thread for cross-thread Recycle.
                    for (int i = 0; i < meshesPerWorker; i++)
                    {
                        if ((i & 1) == 0)
                        {
                            recycler.Recycle(localRented[i]);
                        }
                        else
                        {
                            returnedQueue.Enqueue(localRented[i]);
                        }
                    }
                    recycler.DoRecycling();
                    MeshData reacquired = recycler.GetOrCreateMesh(4096);
                    Assert.NotNull(reacquired);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })
            {
                IsBackground = true
            };
            threads[w].Start();
        }

        foreach (var thread in threads) thread.Join();
        while (returnedQueue.TryDequeue(out MeshData? item))
        {
            recycler.Recycle(item);
        }
        recycler.DoRecycling();
        recycler.Dispose();

        // Post-dispose rent still returns valid unpooled MeshData without throwing.
        MeshData postDispose = recycler.GetOrCreateMesh(4096);
        Assert.NotNull(postDispose);
        Assert.Empty(errors);
    }

    [Fact]
    public void MeshDataRecyclerAndBridge_LargeEntrySizesAboveFloatPrecisionCliff_DoNotHangTryAdd()
    {
        long elapsedMs = 1000L;
        var world = CreateProxy<IClientWorldAccessor>((method, _) =>
            method.Name == "get_ElapsedMilliseconds" ? elapsedMs : null);
        var recycler = new MeshDataRecycler(world);
        var bridgeRecycler = new MeshDataRecycler(world);

        // VerticesMax = 8192 -> entrySize = 2048, where ULP((float)2048) = 0.000244f > 0.0001f.
        // Storing multiple identical capacities >= 2048 previously spun infinitely in TryAdd.
        int[] largeCapacities = [8192, 16384, 32768];
        foreach (int capacity in largeCapacities)
        {
            for (int i = 0; i < 18; i++)
            {
                recycler.Recycle(new MeshData(capacity) { Recyclable = true });
                OptimumMeshRecyclerBridge.Recycle(bridgeRecycler, new MeshData(capacity) { Recyclable = true });
            }
        }

        recycler.DoRecycling();
        OptimumMeshRecyclerBridge.DoRecycling(bridgeRecycler);

        foreach (int capacity in largeCapacities)
        {
            MeshData reused = recycler.GetOrCreateMesh(capacity);
            Assert.NotNull(reused);
            Assert.True(reused.VerticesMax >= capacity);
            reused.DisposeBasicData();

            MeshData bridgeReused = OptimumMeshRecyclerBridge.GetOrCreateMesh(bridgeRecycler, capacity);
            Assert.NotNull(bridgeReused);
            Assert.True(bridgeReused.VerticesMax >= capacity);
            bridgeReused.DisposeBasicData();
        }

        recycler.Dispose();
        OptimumMeshRecyclerBridge.Dispose(bridgeRecycler);
    }

    [Fact]
    public void ObjectCacheUtilAndBridge_HandleConcurrentReadsWritesDeletesAndReentrantFactories()
    {
        var backing = new Dictionary<string, object>();
        var api = CreateProxy<ICoreAPI>((method, _) =>
            method.Name == "get_ObjectCache" ? backing : null);

        // Non-deadlocking nested factory invocation (factory acquires another key inside GetOrCreate).
        string nestedResult = OptimumObjectCacheBridge.GetOrCreate(api, "outer", () =>
        {
            string inner = OptimumObjectCacheBridge.GetOrCreate(api, "inner", () => "inner-val");
            return "outer:" + inner;
        });
        Assert.Equal("outer:inner-val", nestedResult);
        Assert.Equal("inner-val", OptimumObjectCacheBridge.TryGet<string>(api, "inner"));

        const int threadCount = 8;
        const int keySpace = 24;
        using var startGate = new Barrier(threadCount);
        var errors = new ConcurrentQueue<Exception>();
        var sharedDicts = new Dictionary<string, MeshData>[threadCount];
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int idx = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startGate.SignalAndWait();
                    sharedDicts[idx] = OptimumCacheHelper.GetOrCreateDefault<Dictionary<string, MeshData>>(api, "shared-cache");

                    for (int i = 0; i < 300; i++)
                    {
                        string key = "k-" + (i % keySpace);
                        var dict = ObjectCacheUtil.GetOrCreate(api, key, static () => new Dictionary<string, string>());
                        Assert.NotNull(dict);
                        _ = ObjectCacheUtil.TryGet<Dictionary<string, string>>(api, key);
                        if ((i & 7) == 0)
                        {
                            ObjectCacheUtil.Delete(api, key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })
            {
                IsBackground = true
            };
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.Empty(errors);
        for (int t = 1; t < threadCount; t++)
        {
            Assert.Same(sharedDicts[0], sharedDicts[t]);
        }
    }

    [Fact]
    public void OptimumMeshPartPool_IsolatesWorkerSlotsAndRoutesCrossThreadReclaimsBackToOrigin()
    {
        CustomMeshDataPartInt source = new(4096);
        for (int i = 0; i < 4096; i++) source.Add(i);
        source.InterleaveStride = 4;
        source.InterleaveSizes = new[] { 1 };
        source.InterleaveOffsets = new[] { 0 };

        CustomMeshDataPartInt? rentedOnWorkerA = null;
        int[]? rentedBufferA = null;
        CustomMeshDataPartInt? secondOnWorkerA = null;
        int[]? rentedBufferB = null;
        Exception? workerError = null;

        using var afterRent = new ManualResetEventSlim(false);
        using var afterReclaim = new ManualResetEventSlim(false);

        var workerA = new Thread(() =>
        {
            try
            {
                rentedOnWorkerA = OptimumMeshPartPool.TestClonePartInt(source);
                rentedBufferA = rentedOnWorkerA.Values;
                afterRent.Set();
                afterReclaim.Wait();
                OptimumMeshPartPool.TestDrain();
                secondOnWorkerA = OptimumMeshPartPool.TestClonePartInt(source);
            }
            catch (Exception ex)
            {
                workerError = ex;
            }
        })
        {
            IsBackground = true
        };
        workerA.Start();

        Assert.True(afterRent.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(rentedOnWorkerA);

        // Reclaim happens on a different thread (simulating render-thread upload reclaim).
        OptimumMeshPartPool.TestReclaimInt(rentedOnWorkerA);

        // A second worker thread allocating concurrently must NOT steal Worker A's reclaimed buffer.
        var workerB = new Thread(() =>
        {
            var partB = OptimumMeshPartPool.TestClonePartInt(source);
            rentedBufferB = partB.Values;
        })
        {
            IsBackground = true
        };
        workerB.Start();
        workerB.Join();

        afterReclaim.Set();
        workerA.Join();

        Assert.Null(workerError);
        Assert.NotNull(secondOnWorkerA);
        Assert.Same(rentedBufferA, secondOnWorkerA!.Values);
        Assert.NotSame(rentedBufferA, rentedBufferB);
    }

    private sealed class MapTexSource : ITexPositionSource
    {
        private readonly Dictionary<string, TextureAtlasPosition> _map;
        public Size2i AtlasSize { get; } = new Size2i(64, 64);

        public MapTexSource(Dictionary<string, TextureAtlasPosition> map) => _map = map;

        public TextureAtlasPosition this[string textureCode] =>
            _map.TryGetValue(textureCode, out var pos) ? pos : null!;
    }

    [Fact]
    public void PerCallCrateAndLanternTexSources_IsolateVariantLookupsAcrossInstances()
    {
        var oakPos = new TextureAtlasPosition { atlasTextureId = 1 };
        var birchPos = new TextureAtlasPosition { atlasTextureId = 2 };
        var labelPos = new TextureAtlasPosition { atlasTextureId = 3 };
        var copperPos = new TextureAtlasPosition { atlasTextureId = 4 };
        var silverPos = new TextureAtlasPosition { atlasTextureId = 5 };
        var quartzPos = new TextureAtlasPosition { atlasTextureId = 6 };
        var unknownPos = new TextureAtlasPosition { atlasTextureId = 99 };

        var atlas = CreateProxy<IBlockTextureAtlasAPI>((m, _) =>
            m.Name == "get_UnknownTexturePosition" ? unknownPos : null);
        var capi = CreateProxy<ICoreClientAPI>((m, _) =>
            m.Name == "get_BlockTextureAtlas" ? atlas : null);

        var crateInner = new MapTexSource(new Dictionary<string, TextureAtlasPosition>
        {
            ["oak-sides"] = oakPos,
            ["birch-sides"] = birchPos,
        });
        var crateOak = new OptimumCrateTexSource(capi, crateInner, "oak", useLabelPos: false, labelPos: null);
        var crateBirch = new OptimumCrateTexSource(capi, crateInner, "birch", useLabelPos: false, labelPos: null);
        var crateLabel = new OptimumCrateTexSource(capi, crateInner, "birch", useLabelPos: true, labelPos: labelPos);

        Assert.Same(oakPos, crateOak["sides"]);
        Assert.Same(birchPos, crateBirch["sides"]);
        Assert.Same(unknownPos, crateOak["missing"]);
        Assert.Same(labelPos, crateLabel["sides"]);

        var lanternInner = new MapTexSource(new Dictionary<string, TextureAtlasPosition>
        {
            ["copper"] = copperPos,
            ["silver"] = silverPos,
            ["deco-copper"] = copperPos,
            ["deco-silver"] = silverPos,
        });
        var glassInner = new MapTexSource(new Dictionary<string, TextureAtlasPosition> { ["material"] = quartzPos });
        var lanternA = new OptimumLanternTexSource(new Size2i(64, 64), lanternInner, glassInner, "copper", "plain");
        var lanternB = new OptimumLanternTexSource(new Size2i(64, 64), lanternInner, glassInner, "copper", "silver");

        Assert.Same(copperPos, lanternA["lining"]);
        Assert.Same(silverPos, lanternB["lining"]);
        Assert.Same(quartzPos, lanternA["glass"]);
    }
}

public sealed class MultiWorkerPatchAndIlWiringTests
{
    [Fact]
    public void MultiWorkerSourcesAndPatches_AreRegisteredAndWiredEndToEnd()
    {
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string apiPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        string modPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));

        // VintagestoryAPI Cecil forwarders
        Assert.Contains("PatchMeshDataRecycler", apiPatcherSource);
        Assert.Contains("PatchObjectCacheUtil", apiPatcherSource);

        // VintagestoryLib Cecil ownership & wiring
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/BlockChunkDataLayer.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientChunk.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientWorldMap.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemStartup.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/TextureAtlasManager.cs.patch", cecilList);

        Assert.Contains("optimumThreadBlocksByPaletteIndex", programSource);
        Assert.Contains("OptimumTryBeginWorkerTesselation", programSource);
        Assert.Contains("OptimumFinishWorkerTesselation", programSource);
        Assert.Contains("Vintagestory.Client.NoObf.OptimumTessWorkerSystem", programSource);
        Assert.DoesNotContain("_003C_003Ey__InlineArray4",
            PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch"));

        // VSSurvivalMod runtime donor targets
        Assert.Contains("Vintagestory.GameContent.BlockSmoothTextureTransition", modPatcherSource);
        Assert.Contains("Vintagestory.GameContent.BEBehaviorDisplay", modPatcherSource);
        Assert.Contains("Vintagestory.GameContent.BlockEntityDisplay", modPatcherSource);
        Assert.Contains("Vintagestory.GameContent.BlockEntityGroundStorage", modPatcherSource);
        Assert.Contains("Vintagestory.GameContent.MealMeshCache", modPatcherSource);
        Assert.Contains("finishGroundStorageMeshUpload", modPatcherSource);
        Assert.Contains("optimumWindModeByFlagCount", modPatcherSource);
        Assert.Contains("GetWindModeByFlagCount", modPatcherSource);
        Assert.Contains("Volatile.Read(ref Elements) != null",
            PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/UnloadableShape.cs.patch"));
        Assert.Contains("BlockEntityGroundStorage.cs", File.ReadAllText(PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityGroundStorage.cs.patch")));
        Assert.Contains("MealMeshCache.cs", File.ReadAllText(PatchReader.FindRepositoryFile("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/MealMeshCache.cs.patch")));
    }

    [Fact]
    public void OptimumCacheHelper_GetOrAdd_RunsFactoryOutsideLockWithoutDeadlock()
    {
        var cache = new Dictionary<string, object>();
        const int keyCount = 16;
        using var start = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var observedByThread = new object[8][];
        var threads = new Thread[8];

        for (int t = 0; t < threads.Length; t++)
        {
            int workerIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    observedByThread[workerIndex] = new object[keyCount];
                    start.Wait();
                    for (int i = 0; i < keyCount; i++)
                    {
                        int k = (i + workerIndex) % keyCount;
                        observedByThread[workerIndex][k] = OptimumCacheHelper.GetOrAdd(
                            cache,
                            "outer-" + k,
                            (cache, k),
                            static state =>
                            {
                                // Nested lookup must succeed without deadlocking cache lock.
                                object nested = OptimumCacheHelper.GetOrAdd(
                                    state.cache,
                                    "inner-" + state.k,
                                    state.k,
                                    static innerKey => "val-" + innerKey);
                                return new object[] { nested };
                            });
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });
            threads[t].Start();
        }

        start.Set();
        foreach (Thread thread in threads) thread.Join();

        Assert.Empty(errors);
        for (int k = 0; k < keyCount; k++)
        {
            object canonical = observedByThread[0][k];
            Assert.NotNull(canonical);
            for (int t = 1; t < threads.Length; t++)
            {
                Assert.Same(canonical, observedByThread[t][k]);
            }
        }
    }

    [Fact]
    public void OptimumVariantOverrideTexSource_OverridesOnlyTargetCode()
    {
        TextureAtlasPosition defaultPos = new() { x1 = 0.1f, y1 = 0.2f };
        TextureAtlasPosition wallPos = new() { x1 = 0.5f, y1 = 0.6f };
        var inner = new StubTexPositionSource(new()
        {
            ["wall"] = defaultPos,
            ["top"] = defaultPos,
        });

        var texSource = new OptimumVariantOverrideTexSource(inner, "wall", wallPos);
        Assert.Same(wallPos, texSource["wall"]);
        Assert.Same(defaultPos, texSource["top"]);
        Assert.Same(inner.AtlasSize, texSource.AtlasSize);
    }

    private sealed class StubTexPositionSource : ITexPositionSource
    {
        private readonly Dictionary<string, TextureAtlasPosition> _positions;

        public StubTexPositionSource(Dictionary<string, TextureAtlasPosition> positions)
        {
            _positions = positions;
        }

        public Size2i AtlasSize { get; } = new(512, 512);

        public TextureAtlasPosition? this[string textureCode] =>
            _positions.TryGetValue(textureCode, out var pos) ? pos : null;
    }

    [Fact]
    public void PatchedVintagestoryLibAndApiAssemblies_CarryRequiredThreadStaticAndBridgeIlWhenPresent()
    {
        string repoRoot = Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;
        string patchedLib = Path.Combine(repoRoot, "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll");
        if (!File.Exists(patchedLib))
        {
            // Gate 1 runs dotnet build without make patch-il; Gates 2-4 validate the patched DLLs directly.
            return;
        }

        using var libAsm = AssemblyDefinition.ReadAssembly(patchedLib);
        TypeDefinition? dataLayer = libAsm.MainModule.GetType("Vintagestory.Common.BlockChunkDataLayer");
        Assert.NotNull(dataLayer);

        FieldDefinition? tlsPaletteField = dataLayer!.Fields.FirstOrDefault(f => f.Name == "optimumThreadBlocksByPaletteIndex");
        Assert.NotNull(tlsPaletteField);
        Assert.Contains(tlsPaletteField!.CustomAttributes, a => a.AttributeType.FullName == "System.ThreadStaticAttribute");

        foreach (string methodName in new[] { "getBlockOne", "getBlockTwo", "getBlockThree", "getBlockFour", "getBlockFive", "getBlockGeneralCase" })
        {
            MethodDefinition? method = dataLayer.Methods.FirstOrDefault(m => m.Name == methodName);
            Assert.NotNull(method);
            Assert.Contains(method!.Body.Instructions, i => i.Operand is FieldReference fr && fr.Name == "optimumThreadBlocksByPaletteIndex");
            Assert.DoesNotContain(method.Body.Instructions, i => i.Operand is FieldReference fr && fr.Name == "blocksByPaletteIndex");
        }

        TypeDefinition? crossTess = libAsm.MainModule.GetType("Vintagestory.Client.NoObf.CrossTesselator");
        Assert.NotNull(crossTess);
        foreach (string fieldName in new[] { "optimumStartRot", "optimumEndRot" })
        {
            FieldDefinition? f = crossTess!.Fields.FirstOrDefault(x => x.Name == fieldName);
            Assert.NotNull(f);
            Assert.Contains(f!.CustomAttributes, a => a.AttributeType.FullName == "System.ThreadStaticAttribute");
        }

        TypeDefinition? clientChunk = libAsm.MainModule.GetType("Vintagestory.Client.NoObf.ClientChunk");
        Assert.NotNull(clientChunk);
        Assert.NotNull(clientChunk!.Methods.FirstOrDefault(m => m.Name == "OptimumRecordDirty"));
        Assert.NotNull(clientChunk.Methods.FirstOrDefault(m => m.Name == "OptimumTryBeginWorkerTesselation"));
        Assert.NotNull(clientChunk.Methods.FirstOrDefault(m => m.Name == "OptimumFinishWorkerTesselation"));

        TypeDefinition? tessChunk = libAsm.MainModule.GetType("Vintagestory.Client.NoObf.TesselatedChunk");
        Assert.NotNull(tessChunk);
        Assert.NotNull(tessChunk!.Fields.FirstOrDefault(f => f.Name == "fullSeq"));
        Assert.NotNull(tessChunk.Fields.FirstOrDefault(f => f.Name == "edgeSeq"));
    }
}
