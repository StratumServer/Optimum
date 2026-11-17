using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class ChunkFaceBucketCullTests
{
    private sealed class BucketTestMeshRef : MeshRef
    {
        public override bool Initialized => !Disposed;
    }

    private sealed class BucketRenderHarness
    {
        internal readonly ICoreClientAPI Api;
        internal readonly List<(int[] Starts, int[] Sizes)> Draws = new();

        public BucketRenderHarness()
        {
            var shader = CreateProxy<IShaderProgram>((_, _) => null);
            var logger = CreateProxy<ILogger>((_, _) => null);
            var world = CreateProxy<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_Logger" => logger,
                "TryGetMiniDimension" => false,
                _ => null,
            });
            var render = CreateProxy<IRenderAPI>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_UseSSBOs":
                        return false;
                    case "get_CurrentActiveShader":
                        return shader;
                    case "AllocateEmptyMesh":
                        return new BucketTestMeshRef();
                    case "RenderMesh":
                        if (args != null && args.Length >= 4 && args[1] is int[] starts && args[2] is int[] sizes)
                        {
                            int count = (int)args[3]!;
                            var s = new int[count];
                            var sz = new int[count];
                            for (int i = 0; i < count; i++)
                            {
                                s[i] = starts[i * 2];
                                sz[i] = sizes[i];
                            }
                            Draws.Add((s, sz));
                        }
                        return null;
                    case "DeleteMesh":
                        if (args![0] is MeshRef mr) mr.Dispose();
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
            var proxy = DispatchProxy.Create<T, MeshPoolSortTests.InterceptorProxy>();
            ((MeshPoolSortTests.InterceptorProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private static FrustumCulling CreateWideFrustum(double viewDistance = 2000d)
    {
        var fc = new FrustumCulling();
        fc.UpdateViewDistance((int)viewDistance);
        fc.lod0BiasSq = 0f;
        fc.lod2BiasSq = double.MaxValue;
        fc.shadowRangeX = viewDistance;
        fc.shadowRangeZ = viewDistance;
        double[] proj = Mat4d.Create();
        Mat4d.Ortho(proj, -200000d, 200000d, -200000d, 200000d, -200000d, 200000d);
        double[] cam = Mat4d.Create();
        fc.CalcFrustumEquations(new BlockPos(0, 0, 0), proj, cam);
        return fc;
    }

    private static void AddCardinalQuad(
        MeshData mesh,
        int faceIndex,
        float x,
        float y,
        float z,
        int customTag,
        int extraFlags = 0,
        bool invertWinding = false,
        bool skewZ = false)
    {
        int flags = BlockFacing.ALLFACES[faceIndex].NormalPackedFlags | extraFlags;
        (float x, float y, float z)[] corners = faceIndex switch
        {
            BlockFacing.indexNORTH => new[] { (x, y, z), (x, y + 1, z), (x + 1, y + 1, z), (x + 1, y, z) },
            BlockFacing.indexEAST => new[] { (x + 1, y, z), (x + 1, y + 1, z), (x + 1, y + 1, z + 1), (x + 1, y, z + 1) },
            BlockFacing.indexSOUTH => new[] { (x + 1, y, z + 1), (x + 1, y + 1, z + 1), (x, y + 1, z + 1), (x, y, z + 1) },
            BlockFacing.indexWEST => new[] { (x, y, z + 1), (x, y + 1, z + 1), (x, y + 1, z), (x, y, z) },
            BlockFacing.indexUP => new[] { (x, y + 1, z + 1), (x + 1, y + 1, z + 1), (x + 1, y + 1, z), (x, y + 1, z) },
            _ => new[] { (x, y, z), (x + 1, y, z), (x + 1, y, z + 1), (x, y, z + 1) },
        };

        if (invertWinding)
        {
            (corners[1], corners[3]) = (corners[3], corners[1]);
        }
        if (skewZ)
        {
            corners[2] = (corners[2].x, corners[2].y, corners[2].z + 0.25f);
        }

        int baseVert = mesh.VerticesCount;
        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(corners[i].x, corners[i].y, corners[i].z, i * 0.1f, i * 0.2f, unchecked((int)0xFFFFFFFF), flags);
            mesh.CustomInts?.Add(customTag * 10 + i);
            if (mesh.CustomShorts != null)
            {
                mesh.CustomShorts.Add((short)(customTag * 2 + 1));
                mesh.CustomShorts.Add((short)(customTag * 3 + 2));
            }
        }
        mesh.AddQuadIndices(baseVert);
    }

    private static MeshData CreateMultiFaceMesh(bool includeTopSoilShorts = false)
    {
        var mesh = new MeshData(64, 96, withNormals: false, withUv: true, withRgba: true, withFlags: true);
        mesh.CustomInts = new CustomMeshDataPartInt(64) { InterleaveStride = 4 };
        if (includeTopSoilShorts)
        {
            mesh.CustomShorts = new CustomMeshDataPartShort(128) { InterleaveStride = 4 };
        }

        // Scrambled order mixing cardinals plus 3 unaligned cases (wind, inverted winding, non-planar).
        AddCardinalQuad(mesh, BlockFacing.indexUP, 2, 10, 4, 1);
        AddCardinalQuad(mesh, BlockFacing.indexUP, 3, 10, 4, 2, extraFlags: 3 << VertexFlags.WindModeBitsPos);
        AddCardinalQuad(mesh, BlockFacing.indexDOWN, 2, 2, 4, 3);
        AddCardinalQuad(mesh, BlockFacing.indexNORTH, 4, 5, 1, 4, skewZ: true);
        AddCardinalQuad(mesh, BlockFacing.indexNORTH, 2, 5, 1, 5);
        AddCardinalQuad(mesh, BlockFacing.indexEAST, 9, 5, 4, 6);
        AddCardinalQuad(mesh, BlockFacing.indexSOUTH, 4, 5, 9, 7, invertWinding: true);
        AddCardinalQuad(mesh, BlockFacing.indexSOUTH, 3, 5, 9, 8);
        AddCardinalQuad(mesh, BlockFacing.indexWEST, 1, 5, 4, 9);
        return mesh;
    }

    [Fact]
    public void BucketAndAttach_ReordersCardinalsPreservingCustomAttributesAndRoutingWindAndObliqueToUnaligned()
    {
        bool orig = OptimumConfig.ChunkFaceBucketCullEnabled;
        try
        {
            OptimumConfig.ChunkFaceBucketCullEnabled = true;
            var mesh = CreateMultiFaceMesh(includeTopSoilShorts: true);
            int origVerts = mesh.VerticesCount;
            int origIdx = mesh.IndicesCount;

            OptimumChunkFaceBuckets.BucketAndAttach(mesh, EnumChunkRenderPass.TopSoil);

            Assert.Equal(origVerts, mesh.VerticesCount);
            Assert.Equal(origIdx, mesh.IndicesCount);
            Assert.Equal(origVerts, mesh.CustomInts.Count);
            Assert.Equal(origVerts * 2, mesh.CustomShorts.Count);

            // Bind and verify exact bucket distribution:
            // Slots [Down(1), North(1), East(1), South(1), West(1), Up(1), Unaligned(3)] = 9 quads * 6 = 54 indices.
            var harness = new BucketRenderHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = CreateWideFrustum();
            var pool = MeshDataPool.AllocateNewPool(harness.Api, 1024, 1536, 16, null,
                new CustomMeshDataPartShort { InterleaveOffsets = new[] { 0 }, InterleaveSizes = new[] { 2 }, InterleaveStride = 4 },
                null,
                new CustomMeshDataPartInt { InterleaveOffsets = new[] { 0 }, InterleaveSizes = new[] { 1 }, InterleaveStride = 4 });
            var loc = pool.TryAdd(harness.Api, mesh, new Vec3i(32, 0, 64), 0, new Sphere(40, 6, 72, 10, 10, 10));
            Assert.NotNull(loc);

            OptimumChunkFaceBuckets.BindLocation(loc, mesh, new Vec3i(32, 0, 64), 0);
            Assert.True(OptimumChunkFaceBuckets.TryGetLocationBuckets(loc!, out var data));
            Assert.NotNull(data);
            Assert.Equal(6, data!.IndexCounts[OptimumChunkFaceBuckets.SlotDown]);
            Assert.Equal(6, data.IndexCounts[OptimumChunkFaceBuckets.SlotNorth]);
            Assert.Equal(6, data.IndexCounts[OptimumChunkFaceBuckets.SlotEast]);
            Assert.Equal(6, data.IndexCounts[OptimumChunkFaceBuckets.SlotSouth]);
            Assert.Equal(6, data.IndexCounts[OptimumChunkFaceBuckets.SlotWest]);
            Assert.Equal(6, data.IndexCounts[OptimumChunkFaceBuckets.SlotUp]);
            Assert.Equal(18, data.IndexCounts[OptimumChunkFaceBuckets.SlotUnaligned]);

            // Every quad in Down..Unaligned must keep its custom attributes synchronized with its vertices.
            for (int q = 0; q < 9; q++)
            {
                int v0 = q * 4;
                int tag = mesh.CustomInts.Values[v0] / 10;
                Assert.Equal(tag * 10 + 3, mesh.CustomInts.Values[v0 + 3]);
                Assert.Equal((short)(tag * 2 + 1), mesh.CustomShorts.Values[v0 * 2]);
                Assert.Equal((short)(tag * 3 + 2), mesh.CustomShorts.Values[v0 * 2 + 1]);
            }

            // Unsupported passes (e.g. BlendNoCull leaves) must never be bucketed.
            var leaveMesh = CreateMultiFaceMesh(false);
            OptimumChunkFaceBuckets.BucketAndAttach(leaveMesh, EnumChunkRenderPass.BlendNoCull);
            var locLeave = pool.TryAdd(harness.Api, leaveMesh, new Vec3i(32, 0, 64), 0, new Sphere(40, 6, 72, 10, 10, 10));
            OptimumChunkFaceBuckets.BindLocation(locLeave, leaveMesh, new Vec3i(32, 0, 64), 0);
            Assert.False(OptimumChunkFaceBuckets.TryGetLocationBuckets(locLeave!, out _));
        }
        finally
        {
            OptimumChunkFaceBuckets.EndOpaquePass();
            OptimumConfig.ChunkFaceBucketCullEnabled = orig;
        }
    }

    [Fact]
    public void FrustumCull_OmitsBackfacingCardinalsCoalescesSpansAndRespectsWarpAndDimensions()
    {
        bool origCull = OptimumConfig.ChunkFaceBucketCullEnabled;
        bool origSort = OptimumConfig.FrontToBackChunkSortEnabled;
        OptimumDiagnostics.ResetAllCounters();
        try
        {
            OptimumConfig.ChunkFaceBucketCullEnabled = true;
            OptimumConfig.FrontToBackChunkSortEnabled = true;

            var harness = new BucketRenderHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = CreateWideFrustum();
            var manager = new MeshDataPoolManager(master, frustum, harness.Api, 1024, 1536, 16,
                null, null, null, new CustomMeshDataPartInt { InterleaveOffsets = new[] { 0 }, InterleaveSizes = new[] { 1 }, InterleaveStride = 4 });

            var mesh = CreateMultiFaceMesh();
            OptimumChunkFaceBuckets.BucketAndAttach(mesh, EnumChunkRenderPass.Opaque);

            // Dimension 2 offset shifts world Y by 2 * 32768 = 65536.
            var origin = new Vec3i(32, 0, 64);
            var loc = manager.AddModel(mesh, origin, 2, new Sphere(37, 65542, 70, 10, 10, 10));
            Assert.NotNull(loc);
            loc!.CullVisible = new Bools(true, true);
            loc.LodLevel = 1;
            OptimumChunkFaceBuckets.BindLocation(loc, mesh, origin, 2);

            var poolsField = typeof(MeshDataPoolManager).GetField("pools", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pool = ((List<MeshDataPool>)poolsField.GetValue(manager)!)[0];

            // Camera strictly South, East, Above chunk in dim 2: keeps {East, South, Up, Unaligned} = 36 indices (12 tris),
            // coalescing {East,South} and {Up,Unaligned} into just 2 spans!
            var camAboveSouthEast = new Vec3d(100, 65600, 150);
            OptimumMeshPoolSort.SetOpaqueEarlyZPass(true);
            OptimumChunkFaceBuckets.BeginOpaquePass(camAboveSouthEast, warpActive: false);
            OptimumMeshPoolSort.SetRenderCameraPosition(camAboveSouthEast);
            try
            {
                bool handled = OptimumMeshPoolSort.TryFrustumCullSortedOpaque(pool, frustum, EnumFrustumCullMode.CullNormal);
                Assert.True(handled);
                Assert.Equal(2, pool.indicesGroupsCount);
                Assert.Equal(12, pool.RenderedTriangles);
                Assert.Equal(12, pool.indicesSizes[0]); // East + South
                Assert.Equal(24, pool.indicesSizes[1]); // Up + Unaligned
                Assert.True(OptimumDiagnostics.ChunkFaceBucketTrisCulled > 0);
                Assert.Contains("keptTris=12, culledTris=6", OptimumDiagnostics.GetChunkFaceBucketCullSummary());
            }
            finally
            {
                OptimumChunkFaceBuckets.EndOpaquePass();
                OptimumMeshPoolSort.ClearRenderCameraPosition();
                OptimumMeshPoolSort.SetOpaqueEarlyZPass(false);
            }

            // Camera inside chunk bounds keeps ALL 9 quads (54 indices = 18 tris) coalesced into 1 contiguous span.
            var camInside = new Vec3d(37, 65541, 69);
            OptimumChunkFaceBuckets.BeginOpaquePass(camInside, warpActive: false);
            try
            {
                OptimumMeshPoolSort.TryFrustumCullSortedOpaque(pool, frustum, EnumFrustumCullMode.CullNormal);
                Assert.Equal(1, pool.indicesGroupsCount);
                Assert.Equal(18, pool.RenderedTriangles);
            }
            finally
            {
                OptimumChunkFaceBuckets.EndOpaquePass();
            }

            // Active temporal storm / shader warp bypasses direction culling cleanly.
            OptimumChunkFaceBuckets.BeginOpaquePass(camAboveSouthEast, warpActive: true);
            Assert.False(OptimumChunkFaceBuckets.IsOpaqueFaceCullActive);
            OptimumChunkFaceBuckets.EndOpaquePass();

            // FrontToBackChunkSort disabled still executes ChunkFaceBucketCull!
            OptimumConfig.FrontToBackChunkSortEnabled = false;
            OptimumChunkFaceBuckets.BeginOpaquePass(camAboveSouthEast, warpActive: false);
            try
            {
                bool handled = OptimumMeshPoolSort.TryFrustumCullSortedOpaque(pool, frustum, EnumFrustumCullMode.CullNormal);
                Assert.True(handled);
                Assert.Equal(2, pool.indicesGroupsCount);
                Assert.Equal(12, pool.RenderedTriangles);
            }
            finally
            {
                OptimumChunkFaceBuckets.EndOpaquePass();
            }
        }
        finally
        {
            OptimumChunkFaceBuckets.EndOpaquePass();
            OptimumConfig.FrontToBackChunkSortEnabled = origSort;
            OptimumConfig.ChunkFaceBucketCullEnabled = origCull;
            OptimumConfig.SetDataPath(null!);
        }
    }

    [Fact]
    public void EmitLocationSpans_GrowsPoolSpanArraysSafelyWhenSplitExceedsInitialMaxPartsPerPool()
    {
        bool origCull = OptimumConfig.ChunkFaceBucketCullEnabled;
        bool origSort = OptimumConfig.FrontToBackChunkSortEnabled;
        try
        {
            OptimumConfig.ChunkFaceBucketCullEnabled = true;
            OptimumConfig.FrontToBackChunkSortEnabled = true;

            var harness = new BucketRenderHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = CreateWideFrustum();
            // Start with tiny maxPartsPerPool = 4 -> indicesSizes.Length == 4.
            var manager = new MeshDataPoolManager(master, frustum, harness.Api, 2048, 3072, 4);

            // Add non-empty culled separators (North at z=2, West at x=2) between Down(0), East(2), and Up(5)
            // so camera at (200, 5, 50) keeps {Down(0), East(2), Up(5)} separated by culled {North(1), West(4)} = 3 spans/chunk.
            for (int i = 0; i < 4; i++)
            {
                var mesh = new MeshData(32, 48, false, true, true, true);
                AddCardinalQuad(mesh, BlockFacing.indexDOWN, 2, 1, 2, 1);
                AddCardinalQuad(mesh, BlockFacing.indexNORTH, 2, 5, 2, 2); // Culled when camZ > maxZ
                AddCardinalQuad(mesh, BlockFacing.indexEAST, 9, 5, 4, 3);
                AddCardinalQuad(mesh, BlockFacing.indexWEST, 2, 5, 4, 4);  // Culled when camX > maxX
                AddCardinalQuad(mesh, BlockFacing.indexUP, 2, 9, 4, 5);
                OptimumChunkFaceBuckets.BucketAndAttach(mesh, EnumChunkRenderPass.Opaque);
                var origin = new Vec3i(i * 32, 0, 0);
                var loc = manager.AddModel(mesh, origin, 0, new Sphere(i * 32 + 5, 5, 5, 8, 8, 8));
                Assert.NotNull(loc);
                loc!.CullVisible = new Bools(true, true);
                loc.LodLevel = 1;
                OptimumChunkFaceBuckets.BindLocation(loc, mesh, origin, 0);
            }

            var poolsField = typeof(MeshDataPoolManager).GetField("pools", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pool = ((List<MeshDataPool>)poolsField.GetValue(manager)!)[0];
            Assert.Equal(4, pool.indicesSizes.Length);

            var camInsideY = new Vec3d(200, 5, 50);
            OptimumChunkFaceBuckets.BeginOpaquePass(camInsideY, warpActive: false);
            OptimumMeshPoolSort.SetOpaqueEarlyZPass(true);
            OptimumMeshPoolSort.SetRenderCameraPosition(camInsideY);
            try
            {
                bool handled = OptimumMeshPoolSort.TryFrustumCullSortedOpaque(pool, frustum, EnumFrustumCullMode.CullNormal);
                Assert.True(handled);
                Assert.Equal(12, pool.indicesGroupsCount);
                Assert.True(pool.indicesSizes.Length >= 12);
                Assert.True(pool.indicesStartsByte.Length >= 24);
                Assert.Equal(24, pool.RenderedTriangles);
            }
            finally
            {
                OptimumChunkFaceBuckets.EndOpaquePass();
                OptimumMeshPoolSort.ClearRenderCameraPosition();
                OptimumMeshPoolSort.SetOpaqueEarlyZPass(false);
            }
        }
        finally
        {
            OptimumChunkFaceBuckets.EndOpaquePass();
            OptimumConfig.FrontToBackChunkSortEnabled = origSort;
            OptimumConfig.ChunkFaceBucketCullEnabled = origCull;
        }
    }

    [Fact]
    public void ChunkFaceBucketCullConfig_PersistsEnabledFlagRoundTrip()
    {
        bool orig = OptimumConfig.ChunkFaceBucketCullEnabled;
        string tempDir = Path.Combine(Path.GetTempPath(), $"optimum-bucket-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "ModConfig"));
        try
        {
            OptimumConfig.SetDataPath(tempDir);
            OptimumConfig.ChunkFaceBucketCullEnabled = false;
            OptimumConfig.Save();

            OptimumConfig.ChunkFaceBucketCullEnabled = true;
            OptimumConfig.Load();

            Assert.False(OptimumConfig.ChunkFaceBucketCullEnabled);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.ChunkFaceBucketCullEnabled = orig;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

#if !NO_DONOR
    [Fact]
    public void PatchedClient_WiresChunkFaceBucketsAcrossTesselationUploadAndRenderOpaque()
    {
        string? tempPatchedPath = null;
        try
        {
            string libPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
            using var module = ModuleDefinition.ReadModule(libPath);

            var tct = module.Types.First(t => t.FullName == "Vintagestory.Client.NoObf.ChunkTesselator");
            var populate = tct.Methods.First(m => m.Name == "populateTesselatedChunkPart" && m.HasBody);
            AssertCallsMember(populate, nameof(OptimumChunkFaceBuckets.BucketAndAttach));

            var tcp = module.Types.First(t => t.FullName == "Vintagestory.Client.NoObf.TesselatedChunkPart");
            var addModel = tcp.Methods.First(m => m.Name == "AddModelAndStoreLocation" && m.HasBody);
            AssertCallsMember(addModel, nameof(OptimumChunkFaceBuckets.BindLocation));

            var cr = module.Types.First(t => t.FullName == "Vintagestory.Client.NoObf.ChunkRenderer");
            var renderOpaque = cr.Methods.First(m => m.Name == "RenderOpaque" && m.HasBody);
            AssertCallsMember(renderOpaque, nameof(OptimumChunkFaceBuckets.BeginOpaquePass));
            AssertCallsMember(renderOpaque, nameof(OptimumChunkFaceBuckets.EndOpaquePass));

            // Also verify shipped VintagestoryAPI-patched.dll carries forwarders and PatchMeshDataPoolSort hooks.
            string patchedApiPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(libPath)!, "..", "..", "..", "..", "bin", "Release", "net10.0", "VintagestoryAPI-patched.dll"));
            if (!File.Exists(patchedApiPath))
            {
                patchedApiPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"))!, "..", "bin", "Release", "net10.0", "VintagestoryAPI-patched.dll"));
            }
            if (File.Exists(patchedApiPath))
            {
                using var apiMod = ModuleDefinition.ReadModule(patchedApiPath);
                Assert.Contains(apiMod.ExportedTypes, et => et.FullName == typeof(OptimumChunkFaceBuckets).FullName);
                var poolType = apiMod.Types.First(t => t.FullName == "Vintagestory.API.Client.MeshDataPool");
                var frustumCull = poolType.Methods.First(m => m.Name == "FrustumCull" && m.Parameters.Count == 2);
                AssertCallsMember(frustumCull, nameof(OptimumMeshPoolSort.TryFrustumCullSortedOpaque));
            }
        }
        finally
        {
            if (tempPatchedPath != null && File.Exists(tempPatchedPath))
            {
                File.Delete(tempPatchedPath);
            }
        }
    }

    private static string ResolveOrBuildPatchedVintagestoryLibPath(out string? tempPatchedPath)
    {
        tempPatchedPath = null;
        string buildLibDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile("build/VintagestoryLib/VintagestoryLib.csproj"))!;
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.GetFullPath(Path.Combine(buildLibDir, "bin", config, "net10.0", "VintagestoryLib-patched.dll"));
            if (File.Exists(candidate)) return candidate;
        }
        string? compiledLibPath = new[] { "Release", "Debug" }
            .Select(config => Path.GetFullPath(Path.Combine(buildLibDir, "bin", config, "net10.0", "VintagestoryLib.dll")))
            .FirstOrDefault(File.Exists);
        Assert.NotNull(compiledLibPath);
        string vanillaLibPath = PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll");
        tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-bucket-patched-{Guid.NewGuid():N}.dll");
        int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath!, tempPatchedPath });
        Assert.Equal(0, exitCode);
        return tempPatchedPath;
    }

    private static void AssertCallsMember(MethodDefinition method, string memberName)
    {
        Assert.Contains(method.Body.Instructions, i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == memberName);
    }
#endif
}
