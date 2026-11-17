using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class MeshPoolReclaimTests
{
    public class InterceptorProxy : DispatchProxy
    {
        internal System.Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod == null ? null : Handler(targetMethod, args);
        }
    }

    private sealed class FakeMeshRef : MeshRef
    {
        public int VaoId;
        public int VboId;
        public int EboId;

        public override bool Initialized => !Disposed;
    }

    private sealed class PoolTestHarness
    {
        internal int AllocatedCount;
        internal int DisposedCount;
        internal int UpdatedCount;
        internal int ErrorLogCount;
        internal readonly List<FakeMeshRef> LiveMeshes = new();
        internal bool UseSSBOs;
        internal readonly ICoreClientAPI Api;
        internal System.Action<FakeMeshRef>? OnDeleteMesh;
        internal System.Func<int, int, FakeMeshRef>? OnAllocateMesh;

        public PoolTestHarness()
        {
            var logger = CreateProxy<ILogger>((method, _) =>
            {
                if (method.Name == "Error")
                {
                    ErrorLogCount++;
                }
                return null;
            });

            var world = CreateProxy<IClientWorldAccessor>((method, _) =>
            {
                if (method.Name == "get_Logger")
                {
                    return logger;
                }
                return null;
            });

            var render = CreateProxy<IRenderAPI>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_UseSSBOs":
                        return UseSSBOs;
                    case "AllocateEmptyMesh":
                    {
                        AllocatedCount++;
                        int xyzBytes = (int)args![0]!;
                        int idxBytes = (int)args[5]!;
                        var mesh = OnAllocateMesh != null ? OnAllocateMesh(xyzBytes, idxBytes) : new FakeMeshRef();
                        LiveMeshes.Add(mesh);
                        return mesh;
                    }
                    case "UpdateChunkMesh":
                        UpdatedCount++;
                        return null;
                    case "DeleteMesh":
                        if (args![0] is FakeMeshRef meshRef && !meshRef.Disposed)
                        {
                            meshRef.Dispose();
                            DisposedCount++;
                            LiveMeshes.Remove(meshRef);
                            OnDeleteMesh?.Invoke(meshRef);
                        }
                        return null;
                    default:
                        return null;
                }
            });

            Api = CreateProxy<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_Render" => render,
                "get_World" => world,
                "get_Logger" => logger,
                _ => null,
            });
        }

        private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, InterceptorProxy>();
            ((InterceptorProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private static MeshData CreateTestMesh(int vertices, int indices)
    {
        var mesh = new MeshData(vertices, indices, withNormals: false, withUv: true, withRgba: true, withFlags: true);
        mesh.VerticesCount = vertices;
        mesh.IndicesCount = indices;
        return mesh;
    }

    private static Sphere MakeSphere(float x, float y, float z, float radius) =>
        new Sphere(x, y, z, radius, radius, radius);

    [Fact]
    public void ReclaimEmptyPools_KeepsOneWarmDefaultPool_DisposesSurplusAndOversized_AndRecyclesIds()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        OptimumDiagnostics.ResetAllCounters();
        try
        {
            OptimumConfig.MeshPoolReclaimEnabled = true;
            var harness = new PoolTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = new FrustumCulling();
            var manager = new MeshDataPoolManager(master, frustum, harness.Api, defaultVertexPoolSize: 256, defaultIndexPoolSize: 384, maxPartsPerPool: 64);

            // Distance > 5000 blocks between origins forces 5 separate default-sized pools.
            var locations = new List<ModelDataPoolLocation>();
            for (int i = 0; i < 5; i++)
            {
                var origin = new Vec3i(i * 6000, 0, 0);
                var loc = manager.AddModel(CreateTestMesh(32, 48), origin, 0, MakeSphere(origin.X, 0, 0, 16));
                Assert.NotNull(loc);
                locations.Add(loc);
            }

            Assert.Equal(5, master.QuantityModelDataPools());
            Assert.Equal(5, harness.AllocatedCount);
            Assert.Equal(0, harness.DisposedCount);

            // Unload chunks 0..3 -> 4 pools become empty; keep 1 warm default pool, dispose 3 surplus.
            for (int i = 0; i < 4; i++)
            {
                master.RemoveDataPoolLocations(new[] { locations[i] });
            }

            Assert.Equal(2, master.QuantityModelDataPools()); // 1 occupied + 1 warm default empty
            Assert.Equal(3, harness.DisposedCount);
            Assert.Equal(2, harness.LiveMeshes.Count);

            // Unload final default chunk -> both remaining pools empty -> keep 1 warm default pool, dispose 1.
            master.RemoveDataPoolLocations(new[] { locations[4] });
            Assert.Equal(1, master.QuantityModelDataPools());
            Assert.Equal(4, harness.DisposedCount);

            // Adding an oversized chunk allocates an oversized pool; unloading it MUST dispose it immediately.
            var giantOrigin = new Vec3i(100_000, 0, 0);
            var giantLoc = manager.AddModel(CreateTestMesh(512, 768), giantOrigin, 0, MakeSphere(giantOrigin.X, 0, 0, 16));
            Assert.Equal(2, master.QuantityModelDataPools());
            Assert.Equal(6, harness.AllocatedCount);

            master.RemoveDataPoolLocations(new[] { giantLoc });
            Assert.Equal(1, master.QuantityModelDataPools()); // Only the 1 warm default pool stays alive
            Assert.Equal(5, harness.DisposedCount);

            // Adding a normal chunk at a distant new origin reuses the retained warm default pool (0 new allocations!).
            var distantOrigin = new Vec3i(250_000, 0, 0);
            var reusedLoc = manager.AddModel(CreateTestMesh(32, 48), distantOrigin, 0, MakeSphere(distantOrigin.X, 0, 0, 16));
            Assert.NotNull(reusedLoc);
            Assert.Equal(6, harness.AllocatedCount);
            Assert.Equal(1, master.QuantityModelDataPools());

            // Adding 2 more pools should recycle freed IDs without expanding modelPools unboundedly.
            var extraOrigin1 = new Vec3i(260_000, 0, 0);
            var extraOrigin2 = new Vec3i(270_000, 0, 0);
            var extra1 = manager.AddModel(CreateTestMesh(32, 48), extraOrigin1, 0, MakeSphere(extraOrigin1.X, 0, 0, 16));
            var extra2 = manager.AddModel(CreateTestMesh(32, 48), extraOrigin2, 0, MakeSphere(extraOrigin2.X, 0, 0, 16));
            Assert.Equal(3, master.QuantityModelDataPools());
            Assert.InRange(reusedLoc.PoolId, 0, 4);
            Assert.InRange(extra1.PoolId, 0, 4);
            Assert.InRange(extra2.PoolId, 0, 4);
            Assert.NotEqual(reusedLoc.PoolId, extra1.PoolId);
            Assert.NotEqual(extra1.PoolId, extra2.PoolId);

            master.DisposeAllPools(harness.Api);
            Assert.Equal(0, master.QuantityModelDataPools());
            Assert.Empty(harness.LiveMeshes);
            Assert.Equal(0, harness.ErrorLogCount);
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
        }
    }

    [Fact]
    public void DelayedRemoval_ReferenceCountsPendingBatches_AndNeverDisposesEarly()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        try
        {
            OptimumConfig.MeshPoolReclaimEnabled = true;
            var harness = new PoolTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api) { DelayedPoolLocationRemoval = true };
            var manager = new MeshDataPoolManager(master, new FrustumCulling(), harness.Api, 256, 384, 64);

            // Pool A holds 2 staggered chunks (same origin), Pool B & Pool C hold 1 chunk each.
            var originA = new Vec3i(0, 0, 0);
            var locA1 = manager.AddModel(CreateTestMesh(24, 36), originA, 0, MakeSphere(0, 0, 0, 16));
            var locA2 = manager.AddModel(CreateTestMesh(24, 36), originA, 0, MakeSphere(0, 0, 0, 16));
            var locB = manager.AddModel(CreateTestMesh(24, 36), new Vec3i(6000, 0, 0), 0, MakeSphere(6000, 0, 0, 16));
            var locC = manager.AddModel(CreateTestMesh(24, 36), new Vec3i(12000, 0, 0), 0, MakeSphere(12000, 0, 0, 16));

            Assert.Equal(3, master.QuantityModelDataPools());

            // Frame 0: queue removal of locA1, locB, locC.
            master.RemoveDataPoolLocations(new[] { locA1, locB, locC });
            Assert.True(locA1.Hide);
            Assert.True(locB.Hide);
            Assert.True(locC.Hide);
            Assert.Equal(3, master.QuantityModelDataPools());
            Assert.Equal(0, harness.DisposedCount);

            // Advance frames 1, 2, 3: removalQueue still holds pending locations -> zero disposals!
            for (int f = 0; f < 3; f++)
            {
                master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
                Assert.Equal(3, master.QuantityModelDataPools());
                Assert.Equal(0, harness.DisposedCount);
            }

            // Enqueue locA2 before Frame 4 drains Frame 0's batch.
            master.RemoveDataPoolLocations(new[] { locA2 });
            Assert.True(locA2.Hide);

            // Frame 4: drains [locA1, locB, locC] -> B and C become empty (1 warm kept, 1 disposed); A still holds locA2.
            master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
            Assert.Equal(2, master.QuantityModelDataPools()); // Pool A + 1 warm empty pool
            Assert.Equal(1, harness.DisposedCount);

            // Advance 2 more frames (Frame 4 already moved locA2 queue[0]->[1]); settles on next OnFrame.
            for (int f = 0; f < 2; f++)
            {
                master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
                Assert.Equal(2, master.QuantityModelDataPools());
                Assert.Equal(1, harness.DisposedCount);
            }

            // Final drain: Pool A becomes empty too -> surplus disposed, leaving 1 warm default pool.
            master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
            Assert.Equal(1, master.QuantityModelDataPools());
            Assert.Equal(2, harness.DisposedCount);
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
        }
    }

    [Fact]
    public void TryAdd_ResetsDimensionWhenEmpty_AndRejectsMismatchedDimensionWhenOccupied()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        try
        {
            OptimumConfig.MeshPoolReclaimEnabled = true;
            var harness = new PoolTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var manager = new MeshDataPoolManager(master, new FrustumCulling(), harness.Api, 256, 384, 64);

            var locDim0 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(0, 0, 0), dimension: 0, MakeSphere(0, 0, 0, 16));
            Assert.Equal(1, harness.AllocatedCount);
            master.RemoveDataPoolLocations(new[] { locDim0 });
            Assert.Equal(1, master.QuantityModelDataPools());

            // Warm empty pool previously held dimension 0; adding a dimension 2 mesh MUST reuse it without allocating!
            var locDim2 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(10, 0, 0), dimension: 2, MakeSphere(10, 0, 0, 16));
            Assert.Equal(1, harness.AllocatedCount);
            Assert.Equal(locDim0.PoolId, locDim2.PoolId);

            // While pool holds an occupied dim-2 mesh, a dim-0 mesh must reject it and allocate a new pool.
            var locDim0Second = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(20, 0, 0), dimension: 0, MakeSphere(20, 0, 0, 16));
            Assert.Equal(2, harness.AllocatedCount);
            Assert.NotEqual(locDim2.PoolId, locDim0Second.PoolId);

            // Empty both pools -> 1 warm pool retained; subsequent dim-0 mesh reuses it with 0 new allocations.
            master.RemoveDataPoolLocations(new[] { locDim2, locDim0Second });
            Assert.Equal(1, master.QuantityModelDataPools());
            var reusedAfterSwap = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(500, 0, 0), dimension: 0, MakeSphere(500, 0, 0, 16));
            Assert.Equal(2, harness.AllocatedCount);
            Assert.NotNull(reusedAfterSwap);
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
        }
    }

    [Fact]
    public void DisposeAllPools_CalcFragmentation_And_NullLocations_AreSafeAndIdempotent()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        try
        {
            OptimumConfig.MeshPoolReclaimEnabled = true;
            var harness = new PoolTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var manager = new MeshDataPoolManager(master, new FrustumCulling(), harness.Api, 256, 384, 64);

            Assert.Equal(0f, master.CalcFragmentation());
            Assert.False(float.IsNaN(master.CalcFragmentation()));

            // Null batches / null element must not crash
            master.RemoveDataPoolLocations(null!);
            master.RemoveDataPoolLocations(Array.Empty<ModelDataPoolLocation>());
            master.DelayedPoolLocationRemoval = true;
            master.RemoveDataPoolLocations(new ModelDataPoolLocation[] { null! });
            for (int f = 0; f < 4; f++)
            {
                master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
            }
            Assert.Equal(1, harness.ErrorLogCount);

            master.DelayedPoolLocationRemoval = false;
            var loc1 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(0, 0, 0), 0, MakeSphere(0, 0, 0, 16));
            var loc2 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(6000, 0, 0), 0, MakeSphere(6000, 0, 0, 16));
            var loc3 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(12000, 0, 0), 0, MakeSphere(12000, 0, 0, 16));
            master.RemoveDataPoolLocations(new[] { loc1, loc2 }); // creates reclaimed null slots alongside live loc3

            float frag = master.CalcFragmentation();
            Assert.False(float.IsNaN(frag));
            Assert.InRange(frag, 0f, 1f);

            // Standalone MeshDataPool.Dispose must be idempotent even when called repeatedly or with null capi
            var standalone = MeshDataPool.AllocateNewPool(harness.Api, 64, 96, 8);
            int beforeDispose = harness.DisposedCount;
            standalone.Dispose(null);
            standalone.Dispose(harness.Api);
            standalone.Dispose(harness.Api);
            Assert.Equal(beforeDispose + 1, harness.DisposedCount);

            // DisposeAllPools with reclaimed null slots must succeed without NRE and reset counts to 0
            master.DisposeAllPools(harness.Api);
            Assert.Equal(0, master.QuantityModelDataPools());
            Assert.Equal(0f, master.CalcFragmentation());
            Assert.Empty(harness.LiveMeshes);
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
        }
    }

    [Fact]
    public void ToggleDisabled_ConfigRoundTrip_And_DiagnosticsContractsWork()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        bool origStreamEnabled = OptimumChunkStreamStats.Enabled;
        OptimumDiagnostics.ResetAllCounters();
        OptimumChunkStreamStats.Reset();
        try
        {
            OptimumChunkStreamStats.Enabled = true;

            // Disabled toggle preserves empty pools (matches vanilla retention behavior).
            OptimumConfig.MeshPoolReclaimEnabled = false;
            var harness = new PoolTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var manager = new MeshDataPoolManager(master, new FrustumCulling(), harness.Api, 256, 384, 64);

            var loc1 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(0, 0, 0), 0, MakeSphere(0, 0, 0, 16));
            var loc2 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(6000, 0, 0), 0, MakeSphere(6000, 0, 0, 16));
            var loc3 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(12000, 0, 0), 0, MakeSphere(12000, 0, 0, 16));
            master.RemoveDataPoolLocations(new[] { loc1, loc2, loc3 });

            Assert.Equal(3, master.QuantityModelDataPools());
            Assert.Equal(0, harness.DisposedCount);
            Assert.Equal(3L, OptimumDiagnostics.MeshPoolsAllocated);
            Assert.Equal(0L, OptimumDiagnostics.MeshPoolsReclaimed);
            Assert.Equal(0L, OptimumChunkStreamStats.PoolReclamations);

            // Enabling and emptying a batch reclaims empty pools and records both diagnostics + stream stats.
            OptimumConfig.MeshPoolReclaimEnabled = true;
            var loc4 = manager.AddModel(CreateTestMesh(16, 24), new Vec3i(18000, 0, 0), 0, MakeSphere(18000, 0, 0, 16));
            master.RemoveDataPoolLocations(new[] { loc4 });

            Assert.Equal(1, master.QuantityModelDataPools());
            Assert.Equal(2, harness.DisposedCount);
            Assert.Equal(2L, OptimumDiagnostics.MeshPoolsReclaimed);
            Assert.True(OptimumDiagnostics.MeshPoolReclaimedBytes > 0L);
            Assert.Equal(2L, OptimumChunkStreamStats.PoolReclamations);

            string summary = OptimumDiagnostics.GetMeshPoolReclaimSummary();
            Assert.Contains("enabled=True", summary);
            Assert.Contains("reclaimed=2", summary);

            string streamSummary = OptimumChunkStreamStats.GetSummary();
            Assert.Contains("poolReclaims=2", streamSummary);

            var metrics = new Dictionary<string, double>();
            OptimumChunkStreamStats.AddMetrics(metrics);
            Assert.Equal(2d, metrics["stream.poolReclamations.count"]);

            var defaultConfigData = new OptimumConfigData();
            Assert.True(defaultConfigData.MeshPoolReclaim);
            Assert.Contains(OptimumConfig.DescribeToggles(), entry => entry.Name == nameof(OptimumConfigData.MeshPoolReclaim));

            string statusCs = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
            Assert.Contains("GetMeshPoolReclaimSummary()", statusCs);
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
            OptimumChunkStreamStats.Enabled = origStreamEnabled;
            OptimumChunkStreamStats.Reset();
            OptimumDiagnostics.ResetAllCounters();
        }
    }

    [Fact]
    public void ApiPatcher_ShipsValidIlAndExecutesLifecycleOnPatchedVanillaApi()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string vanillaApiPath = PatchReader.FindRepositoryFile(".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll");
        string contractsPath = PatchReader.FindRepositoryFile($"bin/{configuration}/net10.0/Optimum.Api.Contracts.dll");
        if (!File.Exists(vanillaApiPath) || !File.Exists(contractsPath))
        {
            return;
        }

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath);

        int applied = ApiPatcher.PatchMeshDataPoolLifecycle(vanilla.MainModule, contracts.MainModule);
        Assert.Equal(10, applied);
        // Re-applying stays idempotent
        Assert.Equal(10, ApiPatcher.PatchMeshDataPoolLifecycle(vanilla.MainModule, contracts.MainModule));

        // Verify InjectTypeForwards + IlStackVerifier + SelfConsistencyVerifier pass cleanly
        string tempDir = Path.Combine(AppContext.BaseDirectory, "meshpool-patch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string patchedApiPath = Path.Combine(tempDir, "VintagestoryAPI-patched.dll");
        try
        {
            Assert.True(ApiPatcher.Patch(vanillaApiPath, contractsPath, patchedApiPath));
            using var patchedDef = AssemblyDefinition.ReadAssembly(patchedApiPath);
            Assert.Empty(IlStackVerifier.VerifyModule(patchedDef.MainModule));
            Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(patchedDef.MainModule));

            // Execute the patched vanilla assembly directly inside an isolated ALC (loaded from memory so Windows CI never locks files).
            var alc = new PatchedApiLoadContext(patchedApiPath, contractsPath);
            Assembly patchedApi = alc.LoadFromStream(new MemoryStream(File.ReadAllBytes(patchedApiPath)));
            Assembly patchedContracts = alc.LoadFromStream(new MemoryStream(File.ReadAllBytes(contractsPath)));
            VerifyPatchedVanillaExecution(patchedApi, patchedContracts);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [GlContextFact]
    public void RealGlContext_ReclaimsRealVaoAndVboOnChunkUnload_WithNoGlErrors()
    {
        bool origReclaim = OptimumConfig.MeshPoolReclaimEnabled;
        try
        {
            OptimumConfig.MeshPoolReclaimEnabled = true;

            GLFWProvider.CheckForMainThread = false;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new OpenTK.Mathematics.Vector2i(32, 32),
            });
            window.MakeCurrent();
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

            var generatedVaos = new List<int>();
            var generatedVbos = new List<int>();
            var harness = new PoolTestHarness
            {
                OnAllocateMesh = (xyzBytes, idxBytes) =>
                {
                    int vao = GL.GenVertexArray();
                    int vbo = GL.GenBuffer();
                    int ebo = GL.GenBuffer();
                    GL.BindVertexArray(vao);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                    GL.BufferData(BufferTarget.ArrayBuffer, xyzBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, idxBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    GL.BindVertexArray(0);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                    generatedVaos.Add(vao);
                    generatedVbos.Add(vbo);
                    generatedVbos.Add(ebo);
                    return new FakeMeshRef { VaoId = vao, VboId = vbo, EboId = ebo };
                },
                OnDeleteMesh = meshRef =>
                {
                    if (meshRef.VaoId != 0)
                    {
                        GL.DeleteVertexArray(meshRef.VaoId);
                        meshRef.VaoId = 0;
                    }
                    if (meshRef.VboId != 0)
                    {
                        GL.DeleteBuffer(meshRef.VboId);
                        meshRef.VboId = 0;
                    }
                    if (meshRef.EboId != 0)
                    {
                        GL.DeleteBuffer(meshRef.EboId);
                        meshRef.EboId = 0;
                    }
                },
            };

            var master = new MeshDataPoolMasterManager(harness.Api) { DelayedPoolLocationRemoval = true };
            var manager = new MeshDataPoolManager(master, new FrustumCulling(), harness.Api, 256, 384, 64);

            var locations = new List<ModelDataPoolLocation>();
            for (int i = 0; i < 4; i++)
            {
                var origin = new Vec3i(i * 6000, 0, 0);
                locations.Add(manager.AddModel(CreateTestMesh(32, 48), origin, 0, MakeSphere(origin.X, 0, 0, 16)));
            }
            var oversizedOrigin = new Vec3i(50_000, 0, 0);
            var oversizedLoc = manager.AddModel(CreateTestMesh(512, 768), oversizedOrigin, 0, MakeSphere(oversizedOrigin.X, 0, 0, 16));

            Assert.Equal(5, generatedVaos.Count);
            Assert.All(generatedVaos, vao => Assert.True(GL.IsVertexArray(vao)));
            Assert.All(generatedVbos, vbo => Assert.True(GL.IsBuffer(vbo)));

            // Unload all 5 pools under 3-frame delayed removal -> 4 GL pools reclaimed, 1 warm default pool stays live.
            master.RemoveDataPoolLocations(locations.Append(oversizedLoc).ToArray());
            for (int f = 0; f < 4; f++)
            {
                master.OnFrame(0.016f, Array.Empty<float>(), Array.Empty<float>());
            }

            Assert.Equal(1, master.QuantityModelDataPools());
            Assert.Single(harness.LiveMeshes);
            int liveVaos = generatedVaos.Count(GL.IsVertexArray);
            int liveVbos = generatedVbos.Count(GL.IsBuffer);
            Assert.Equal(1, liveVaos);
            Assert.Equal(2, liveVbos);
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());

            master.DisposeAllPools(harness.Api);
            Assert.Equal(0, generatedVaos.Count(GL.IsVertexArray));
            Assert.Equal(0, generatedVbos.Count(GL.IsBuffer));
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            OptimumConfig.MeshPoolReclaimEnabled = origReclaim;
        }
    }

    private sealed class PatchedApiLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, Assembly> cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string patchedApiPath;
        private readonly string contractsPath;
        private readonly string depsDir;

        public PatchedApiLoadContext(string patchedApiPath, string contractsPath)
            : base("PatchedMeshPoolTestAlc", isCollectible: false)
        {
            this.patchedApiPath = patchedApiPath;
            this.contractsPath = contractsPath;
            this.depsDir = Path.GetDirectoryName(contractsPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? simpleName = assemblyName.Name;
            if (string.IsNullOrEmpty(simpleName))
            {
                return null;
            }
            if (cache.TryGetValue(simpleName, out var cached))
            {
                return cached;
            }

            if (string.Equals(simpleName, "VintagestoryAPI", StringComparison.OrdinalIgnoreCase))
            {
                var loaded = LoadFromStream(new MemoryStream(File.ReadAllBytes(patchedApiPath)));
                cache[simpleName] = loaded;
                return loaded;
            }
            if (string.Equals(simpleName, "Optimum.Api.Contracts", StringComparison.OrdinalIgnoreCase))
            {
                var loaded = LoadFromStream(new MemoryStream(File.ReadAllBytes(contractsPath)));
                cache[simpleName] = loaded;
                return loaded;
            }
            string candidate = Path.Combine(depsDir, simpleName + ".dll");
            if (File.Exists(candidate))
            {
                var loaded = LoadFromStream(new MemoryStream(File.ReadAllBytes(candidate)));
                cache[simpleName] = loaded;
                return loaded;
            }
            return null;
        }
    }

    private static void VerifyPatchedVanillaExecution(Assembly patchedApi, Assembly patchedContracts)
    {
        Type configType = patchedContracts.GetType("Vintagestory.API.Config.OptimumConfig", throwOnError: true)!;
        FieldInfo enabledField = configType.GetField("MeshPoolReclaimEnabled", BindingFlags.Public | BindingFlags.Static)!;
        enabledField.SetValue(null, true);

        Type capiType = patchedApi.GetType("Vintagestory.API.Client.ICoreClientAPI", throwOnError: true)!;
        Type renderType = patchedApi.GetType("Vintagestory.API.Client.IRenderAPI", throwOnError: true)!;
        Type worldType = patchedApi.GetType("Vintagestory.API.Client.IClientWorldAccessor", throwOnError: true)!;
        Type loggerType = patchedApi.GetType("Vintagestory.API.Common.ILogger", throwOnError: true)!;
        Type meshRefBaseType = patchedApi.GetType("Vintagestory.API.Client.MeshRef", throwOnError: true)!;
        Type masterType = patchedApi.GetType("Vintagestory.API.Client.MeshDataPoolMasterManager", throwOnError: true)!;
        Type managerType = patchedApi.GetType("Vintagestory.API.Client.MeshDataPoolManager", throwOnError: true)!;
        Type meshDataType = patchedApi.GetType("Vintagestory.API.Client.MeshData", throwOnError: true)!;
        Type frustumType = patchedApi.GetType("Vintagestory.API.Client.FrustumCulling", throwOnError: true)!;
        Type vec3iType = patchedApi.GetType("Vintagestory.API.MathTools.Vec3i", throwOnError: true)!;
        Type sphereType = patchedApi.GetType("Vintagestory.API.MathTools.Sphere", throwOnError: true)!;
        Type locType = patchedApi.GetType("Vintagestory.API.Client.ModelDataPoolLocation", throwOnError: true)!;

        // Dynamically emit concrete MeshRef and interface implementations directly against patchedApi
        // (avoids DispatchProxy cross-ALC proxy caching collisions).
        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PatchedMeshRefDyn"), AssemblyBuilderAccess.Run);
        var modBuilder = asmBuilder.DefineDynamicModule("PatchedMeshRefMod");
        var typeBuilder = modBuilder.DefineType("TestPatchedMeshRef", System.Reflection.TypeAttributes.Public, meshRefBaseType);
        var getInit = typeBuilder.DefineMethod(
            "get_Initialized",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.SpecialName | System.Reflection.MethodAttributes.HideBySig,
            typeof(bool),
            Type.EmptyTypes);
        var il = getInit.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        typeBuilder.DefineMethodOverride(getInit, meshRefBaseType.GetProperty("Initialized")!.GetMethod!);
        Type concreteMeshRefType = typeBuilder.CreateType()!;

        int allocatedMeshes = 0;
        int disposedMeshes = 0;
        var disposedRefs = new HashSet<object>();

        System.Func<object> allocCallback = () =>
        {
            allocatedMeshes++;
            return Activator.CreateInstance(concreteMeshRefType)!;
        };
        System.Action<object?> deleteCallback = meshObj =>
        {
            if (meshObj != null && disposedRefs.Add(meshObj))
            {
                disposedMeshes++;
            }
        };

        object logger = EmitInterfaceStub(modBuilder, "StubLogger", loggerType);
        object world = EmitInterfaceStub(modBuilder, "StubWorld", worldType, new Dictionary<string, object> { ["get_Logger"] = logger });
        object render = EmitRenderStub(modBuilder, renderType, allocCallback, deleteCallback);
        object capi = EmitInterfaceStub(modBuilder, "StubCapi", capiType, new Dictionary<string, object>
        {
            ["get_Render"] = render,
            ["get_World"] = world,
            ["get_Logger"] = logger,
        });

        object MakeMeshData(int v, int idx)
        {
            object md = Activator.CreateInstance(meshDataType, v, idx, false, true, true, true)!;
            meshDataType.GetField("VerticesCount")!.SetValue(md, v);
            meshDataType.GetField("IndicesCount")!.SetValue(md, idx);
            return md;
        }

        object master = Activator.CreateInstance(masterType, capi)!;
        object frustum = Activator.CreateInstance(frustumType)!;
        object manager = Activator.CreateInstance(managerType, master, frustum, capi, 256, 384, 64, null, null, null, null)!;

        MethodInfo addModel = managerType.GetMethod("AddModel")!;
        MethodInfo removeData = masterType.GetMethod("RemoveDataPoolLocations")!;
        MethodInfo qtyMethod = masterType.GetMethod("QuantityModelDataPools")!;
        MethodInfo fragMethod = masterType.GetMethod("CalcFragmentation")!;
        MethodInfo disposeAll = masterType.GetMethod("DisposeAllPools")!;

        object Add(int x, int v, int idx, int dim)
        {
            object origin = Activator.CreateInstance(vec3iType, x, 0, 0)!;
            object sphere = Activator.CreateInstance(sphereType, (float)x, 0f, 0f, 16f, 16f, 16f)!;
            return addModel.Invoke(manager, new[] { MakeMeshData(v, idx), origin, dim, sphere })!;
        }

        object loc0 = Add(0, 16, 24, 0);
        object loc1 = Add(6000, 16, 24, 0);
        object loc2 = Add(12000, 16, 24, 0);
        Assert.Equal(3, (int)qtyMethod.Invoke(master, null)!);
        Assert.Equal(3, allocatedMeshes);

        Array arr = Array.CreateInstance(locType, 3);
        arr.SetValue(loc0, 0);
        arr.SetValue(loc1, 1);
        arr.SetValue(loc2, 2);
        removeData.Invoke(master, new object[] { arr });

        // 1 warm pool kept, 2 surplus disposed!
        Assert.Equal(1, (int)qtyMethod.Invoke(master, null)!);
        Assert.Equal(2, disposedMeshes);

        // Reuse the warm empty pool cleanly in dimension 2 (0 new allocations!)
        object locDim2 = Add(15000, 16, 24, 2);
        Assert.NotNull(locDim2);
        Assert.Equal(3, allocatedMeshes);
        Assert.Equal(1, (int)qtyMethod.Invoke(master, null)!);

        Array arr2 = Array.CreateInstance(locType, 1);
        arr2.SetValue(locDim2, 0);
        removeData.Invoke(master, new object[] { arr2 });

        disposeAll.Invoke(master, new[] { capi });
        Assert.Equal(0, (int)qtyMethod.Invoke(master, null)!);
        Assert.Equal(0f, (float)fragMethod.Invoke(master, null)!);
        Assert.Equal(3, disposedMeshes);
    }

    private static IEnumerable<Type> CollectAllInterfaces(Type iface)
    {
        var seen = new HashSet<Type> { iface };
        var queue = new Queue<Type>();
        queue.Enqueue(iface);
        while (queue.Count > 0)
        {
            foreach (var parent in queue.Dequeue().GetInterfaces())
            {
                if (seen.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }
        return seen;
    }

    private static object EmitInterfaceStub(
        ModuleBuilder modBuilder,
        string name,
        Type iface,
        Dictionary<string, object>? getterOverrides = null)
    {
        var tb = modBuilder.DefineType(name, System.Reflection.TypeAttributes.Public, typeof(object), new[] { iface });
        var fields = new Dictionary<string, FieldBuilder>();
        if (getterOverrides != null)
        {
            foreach (var kv in getterOverrides)
            {
                fields[kv.Key] = tb.DefineField("f_" + kv.Key, typeof(object), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
            }
        }

        foreach (var itf in CollectAllInterfaces(iface))
        {
            foreach (var m in itf.GetMethods())
            {
                EmitInterfaceMethodStub(tb, m, ilg =>
                {
                    if (fields.TryGetValue(m.Name, out var field))
                    {
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Castclass, m.ReturnType);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    return false;
                });
            }
        }

        Type built = tb.CreateType()!;
        if (getterOverrides != null)
        {
            foreach (var kv in getterOverrides)
            {
                built.GetField("f_" + kv.Key)!.SetValue(null, kv.Value);
            }
        }
        return Activator.CreateInstance(built)!;
    }

    private static object EmitRenderStub(
        ModuleBuilder modBuilder,
        Type renderType,
        System.Func<object> onAllocate,
        System.Action<object?> onDelete)
    {
        var tb = modBuilder.DefineType("StubRender", System.Reflection.TypeAttributes.Public, typeof(object), new[] { renderType });
        var allocField = tb.DefineField("f_alloc", typeof(System.Func<object>), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var deleteField = tb.DefineField("f_delete", typeof(System.Action<object?>), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);

        foreach (var itf in CollectAllInterfaces(renderType))
        {
            foreach (var m in itf.GetMethods())
            {
                EmitInterfaceMethodStub(tb, m, ilg =>
                {
                    if (m.Name == "AllocateEmptyMesh")
                    {
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ldsfld, allocField);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Func<object>).GetMethod("Invoke")!);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Castclass, m.ReturnType);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    if (m.Name == "DeleteMesh")
                    {
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ldsfld, deleteField);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Action<object?>).GetMethod("Invoke")!);
                        ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    return false;
                });
            }
        }

        Type built = tb.CreateType()!;
        built.GetField("f_alloc")!.SetValue(null, onAllocate);
        built.GetField("f_delete")!.SetValue(null, onDelete);
        return Activator.CreateInstance(built)!;
    }

    private static void EmitInterfaceMethodStub(
        TypeBuilder tb,
        MethodInfo m,
        System.Func<ILGenerator, bool> tryEmitCustom)
    {
        var paramTypes = m.GetParameters().Select(p => p.ParameterType).ToArray();
        var mb = tb.DefineMethod(
            m.Name,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.NewSlot | System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.Final,
            m.ReturnType,
            paramTypes);
        if (m.IsGenericMethodDefinition)
        {
            var genArgs = m.GetGenericArguments();
            mb.DefineGenericParameters(genArgs.Select(g => g.Name).ToArray());
        }
        var ilg = mb.GetILGenerator();
        if (!tryEmitCustom(ilg))
        {
            if (m.ReturnType == typeof(void))
            {
                ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
            }
            else if (!m.ReturnType.IsValueType)
            {
                ilg.Emit(System.Reflection.Emit.OpCodes.Ldnull);
                ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
            }
            else
            {
                var local = ilg.DeclareLocal(m.ReturnType);
                ilg.Emit(System.Reflection.Emit.OpCodes.Ldloca_S, local);
                ilg.Emit(System.Reflection.Emit.OpCodes.Initobj, m.ReturnType);
                ilg.Emit(System.Reflection.Emit.OpCodes.Ldloc, local);
                ilg.Emit(System.Reflection.Emit.OpCodes.Ret);
            }
        }
        tb.DefineMethodOverride(mb, m);
    }
}
