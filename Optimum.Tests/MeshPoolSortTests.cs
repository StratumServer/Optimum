using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Mono.Cecil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class MeshPoolSortTests
{
    public class InterceptorProxy : DispatchProxy
    {
        internal System.Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod == null ? null : Handler(targetMethod, args);
        }
    }

    private sealed class SortMeshRef : MeshRef
    {
        public int PoolTag;
        public override bool Initialized => !Disposed;
    }

    private sealed class RecordedDrawCall
    {
        public int PoolTag;
        public int[] Starts = Array.Empty<int>();
        public int[] Sizes = Array.Empty<int>();
    }

    private sealed class SortTestHarness
    {
        private int nextTag;
        internal readonly List<RecordedDrawCall> DrawCalls = new();
        internal readonly List<SortMeshRef> CreatedMeshRefs = new();
        internal readonly ICoreClientAPI Api;

        public SortTestHarness()
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
                    {
                        var mr = new SortMeshRef { PoolTag = nextTag++ };
                        CreatedMeshRefs.Add(mr);
                        return mr;
                    }
                    case "RenderMesh":
                    {
                        if (args != null && args.Length >= 4 && args[0] is SortMeshRef mr && args[1] is int[] starts && args[2] is int[] sizes)
                        {
                            int groupCount = (int)args[3]!;
                            var startCopy = new int[groupCount];
                            var sizeCopy = new int[groupCount];
                            for (int i = 0; i < groupCount; i++)
                            {
                                startCopy[i] = starts[i * 2];
                                sizeCopy[i] = sizes[i];
                            }
                            DrawCalls.Add(new RecordedDrawCall
                            {
                                PoolTag = mr.PoolTag,
                                Starts = startCopy,
                                Sizes = sizeCopy,
                            });
                        }
                        return null;
                    }
                    case "DeleteMesh":
                        if (args![0] is MeshRef mrDel) mrDel.Dispose();
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

    private static FrustumCulling CreateOpenFrustum(double viewDistance = 2000d)
    {
        var fc = new FrustumCulling();
        fc.UpdateViewDistance((int)viewDistance);
        fc.lod0BiasSq = 0;
        fc.lod2BiasSq = double.MaxValue;
        fc.shadowRangeX = viewDistance;
        fc.shadowRangeZ = viewDistance;

        // Wide orthographic frustum enclosing [-5000..5000]^3 around origin
        double[] proj = Mat4d.Create();
        Mat4d.Ortho(proj, -5000, 5000, -5000, 5000, -5000, 5000);
        double[] cam = Mat4d.Create();
        fc.CalcFrustumEquations(new BlockPos(0, 0, 0), proj, cam);
        return fc;
    }

    private static ModelDataPoolLocation AddChunk(
        MeshDataPoolManager manager,
        int originX,
        int originY,
        int originZ,
        float sphereX,
        float sphereY,
        float sphereZ,
        int verts = 16,
        int indices = 24,
        int dim = 0)
    {
        var origin = new Vec3i(originX, originY, originZ);
        var sphere = new Sphere(sphereX, sphereY, sphereZ, 16f, 16f, 16f);
        var loc = manager.AddModel(CreateTestMesh(verts, indices), origin, dim, sphere);
        Assert.NotNull(loc);
        loc.CullVisible = new Bools(true, true);
        loc.LodLevel = 1;
        return loc;
    }

    [Fact]
    public void Render_SortsPoolsAndSpansFrontToBack_WithoutMutatingPoolsList_OrAllocating()
    {
        bool origSort = OptimumConfig.FrontToBackChunkSortEnabled;
        OptimumDiagnostics.ResetAllCounters();
        try
        {
            OptimumConfig.FrontToBackChunkSortEnabled = true;
            var harness = new SortTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = CreateOpenFrustum();
            var manager = new MeshDataPoolManager(master, frustum, harness.Api, 1024, 1536, 64);

            // 4 separate pools (>5000 blocks apart on different Z bands) added OUT OF distance order:
            // Pool 0 @ 500m, Pool 1 @ 200m, Pool 2 @ 40m, Pool 3 @ 120m.
            // Within Pool 2 (40m pool), add chunks far-to-near (80m, 40m, 60m) to test span sorting too.
            var p0a = AddChunk(manager, 0, 0, 0, 500, 0, 0);
            var p0b = AddChunk(manager, 0, 0, 0, 460, 0, 0);
            var p1a = AddChunk(manager, 6000, 0, 0, 200, 0, 0);
            var p1b = AddChunk(manager, 6000, 0, 0, 240, 0, 0);
            var p2a = AddChunk(manager, 12000, 0, 0, 80, 0, 0);
            var p2b = AddChunk(manager, 12000, 0, 0, 40, 0, 0);
            var p2c = AddChunk(manager, 12000, 0, 0, 60, 0, 0);
            var p3a = AddChunk(manager, 18000, 0, 0, 120, 0, 0);
            var p3b = AddChunk(manager, 18000, 0, 0, 150, 0, 0);

            var camera = new Vec3d(0, 0, 0);
            var poolsField = typeof(MeshDataPoolManager).GetField("pools", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var poolsList = (List<MeshDataPool>)poolsField.GetValue(manager)!;
            var originalPoolRefs = poolsList.ToArray();

            // 1. Inside opaque pass: expects Pool order [2 (40m), 3 (120m), 1 (200m), 0 (460m)]
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(camera, "origin", EnumFrustumCullMode.CullNormal);
            }

            Assert.Equal(4, harness.DrawCalls.Count);
            Assert.Equal(new[] { 2, 3, 1, 0 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());

            // Within Pool 2, spans were added at [80m (start=0), 40m (start=96), 60m (start=192)].
            // Front-to-back sort MUST emit [40m (96), 60m (192), 80m (0)].
            Assert.Equal(new[] { p2b.IndicesStart * 4, p2c.IndicesStart * 4, p2a.IndicesStart * 4 }, harness.DrawCalls[0].Starts);
            // Within Pool 3: [120m, 150m]
            Assert.Equal(new[] { p3a.IndicesStart * 4, p3b.IndicesStart * 4 }, harness.DrawCalls[1].Starts);
            // Within Pool 1: [200m, 240m]
            Assert.Equal(new[] { p1a.IndicesStart * 4, p1b.IndicesStart * 4 }, harness.DrawCalls[2].Starts);
            // Within Pool 0: [460m, 500m]
            Assert.Equal(new[] { p0b.IndicesStart * 4, p0a.IndicesStart * 4 }, harness.DrawCalls[3].Starts);

            // Underlying manager.pools MUST keep its exact original order (warm pool invariant!).
            Assert.Equal(originalPoolRefs, poolsList.ToArray());
            Assert.True(OptimumDiagnostics.ChunkSortPoolsOrdered >= 4);
            Assert.True(OptimumDiagnostics.ChunkSortSpansSorted >= 9);

            // 2. Outside opaque pass (simulating BlendNoCull / Decor / RenderOIT) OR shadow mode -> original order
            harness.DrawCalls.Clear();
            manager.Render(camera, "origin", EnumFrustumCullMode.CullNormal);
            Assert.Equal(new[] { 0, 1, 2, 3 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());
            Assert.Equal(new[] { p2a.IndicesStart * 4, p2b.IndicesStart * 4, p2c.IndicesStart * 4 }, harness.DrawCalls[2].Starts);

            harness.DrawCalls.Clear();
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(camera, "origin", EnumFrustumCullMode.CullInstantShadowPassNear);
            }
            Assert.Equal(new[] { 0, 1, 2, 3 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());

            // 3. Toggle disabled -> original order
            OptimumConfig.FrontToBackChunkSortEnabled = false;
            harness.DrawCalls.Clear();
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(camera, "origin", EnumFrustumCullMode.CullNormal);
            }
            Assert.Equal(new[] { 0, 1, 2, 3 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());

            // 4. Zero heap allocation check across 100 steady pool sort + FrustumCull passes (avoids mock DispatchProxy boxing noise)
            OptimumConfig.FrontToBackChunkSortEnabled = true;
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                _ = OptimumMeshPoolSort.GetSortedPoolIndices(poolsList, poolsList.Count, camera);
                poolsList[2].FrustumCull(frustum, EnumFrustumCullMode.CullNormal);
                OptimumMeshPoolSort.ClearRenderCameraPosition();

                long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                {
                    int[]? sorted = OptimumMeshPoolSort.GetSortedPoolIndices(poolsList, poolsList.Count, camera);
                    for (int k = 0; k < poolsList.Count; k++)
                    {
                        poolsList[sorted![k]].FrustumCull(frustum, EnumFrustumCullMode.CullNormal);
                    }
                    OptimumMeshPoolSort.ClearRenderCameraPosition();
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
                Assert.Equal(0L, allocated);
            }

            master.DisposeAllPools(harness.Api);
        }
        finally
        {
            OptimumConfig.FrontToBackChunkSortEnabled = origSort;
        }
    }

    [Fact]
    public void SinglePoolOverlappingAabb_And_MiniDimensionFallbackWorkCleanly()
    {
        bool origSort = OptimumConfig.FrontToBackChunkSortEnabled;
        try
        {
            OptimumConfig.FrontToBackChunkSortEnabled = true;
            var harness = new SortTestHarness();
            var master = new MeshDataPoolMasterManager(harness.Api);
            var frustum = CreateOpenFrustum();
            var manager = new MeshDataPoolManager(master, frustum, harness.Api, 1024, 1536, 64);

            // Single pool holding 6 staggered chunks added in arbitrary order -> spans MUST sort front-to-back even when pools.Count == 1
            var c1 = AddChunk(manager, 0, 0, 0, 300, 0, 0);
            var c2 = AddChunk(manager, 0, 0, 0, 50, 0, 0);
            var c3 = AddChunk(manager, 0, 0, 0, 180, 0, 0);
            var c4 = AddChunk(manager, 0, 0, 0, 10, 0, 0);

            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(new Vec3d(0, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }

            Assert.Single(harness.DrawCalls);
            Assert.Equal(
                new[] { c4.IndicesStart * 4, c2.IndicesStart * 4, c3.IndicesStart * 4, c1.IndicesStart * 4 },
                harness.DrawCalls[0].Starts);

            // Now add a second pool far away whose poolOrigin is far from chunk spheres, then test tight AABB cache refresh
            var farChunk = AddChunk(manager, 6000, 0, 0, 1000, 0, 0);
            // Add a very close chunk inside Pool 1 while camera is at (950, 0, 0) -> Pool 1 AABB encloses camera (distSq == 0)
            var closeInPool1 = AddChunk(manager, 6000, 0, 0, 955, 0, 0);

            harness.DrawCalls.Clear();
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(new Vec3d(950, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }

            // Pool 1 encloses (950, 0, 0) so Pool 1 MUST render before Pool 0!
            Assert.Equal(new[] { 1, 0 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());

            // Remove close chunk from Pool 1 and add an even farther chunk -> cached tight AABB MUST invalidate!
            master.RemoveDataPoolLocations(new[] { closeInPool1 });
            var fartherInPool1 = AddChunk(manager, 6000, 0, 0, 1100, 0, 0);
            harness.DrawCalls.Clear();
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                // Move camera near Pool 0's 300m chunk -> Pool 0 now wins cleanly over Pool 1!
                manager.Render(new Vec3d(295, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }
            Assert.Equal(new[] { 0, 1 }, harness.DrawCalls.Select(d => d.PoolTag).ToArray());
            _ = farChunk;
            _ = fartherInPool1;

            // MiniDimension pool (dimension 1) with null TryGetMiniDimension must not crash
            AddChunk(manager, 20, 0, 20, 20, 0, 20, dim: Dimensions.MiniDimensions);
            harness.DrawCalls.Clear();
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(new Vec3d(0, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }
            Assert.Equal(2, harness.DrawCalls.Count);

            master.DisposeAllPools(harness.Api);
        }
        finally
        {
            OptimumConfig.FrontToBackChunkSortEnabled = origSort;
        }
    }

    [Fact]
    public void ConfigDiagnosticsAndChunkRendererOpaqueBoundariesMatchDesign()
    {
        OptimumDiagnostics.ResetAllCounters();
        Assert.True(new OptimumConfigData().FrontToBackChunkSort);
        Assert.Contains(OptimumConfig.DescribeToggles(), entry => entry.Name == nameof(OptimumConfigData.FrontToBackChunkSort));

        OptimumDiagnostics.RecordChunkDrawSort(3, 42);
        string summary = OptimumDiagnostics.GetFrontToBackSortSummary();
        Assert.Contains("calls=1", summary);
        Assert.Contains("poolsOrdered=3", summary);
        Assert.Contains("spansSorted=42", summary);

        string statusCs = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
        Assert.Contains("GetFrontToBackSortSummary()", statusCs);

        string chunkRendererCs = File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs"));
        Assert.Contains("OptimumMeshPoolSort.SetOpaqueEarlyZPass(true);", chunkRendererCs);
        Assert.Contains("OptimumMeshPoolSort.SetOpaqueEarlyZPass(false);", chunkRendererCs);

        int idxEnable0 = chunkRendererCs.IndexOf("OptimumMeshPoolSort.SetOpaqueEarlyZPass(true);", StringComparison.Ordinal);
        int idxPass0 = chunkRendererCs.IndexOf("poolsByRenderPass[0][i].Render", idxEnable0, StringComparison.Ordinal);
        int idxPass5 = chunkRendererCs.IndexOf("poolsByRenderPass[5][j].Render", idxPass0, StringComparison.Ordinal);
        int idxDisableForBlend = chunkRendererCs.IndexOf("OptimumMeshPoolSort.SetOpaqueEarlyZPass(false);", idxPass5, StringComparison.Ordinal);
        int idxPass2 = chunkRendererCs.IndexOf("poolsByRenderPass[2][k].Render", idxDisableForBlend, StringComparison.Ordinal);
        int idxEnable1 = chunkRendererCs.IndexOf("OptimumMeshPoolSort.SetOpaqueEarlyZPass(true);", idxPass2, StringComparison.Ordinal);
        int idxPass1 = chunkRendererCs.IndexOf("poolsByRenderPass[1][l].Render", idxEnable1, StringComparison.Ordinal);
        int idxFinallyDisable = chunkRendererCs.IndexOf("OptimumMeshPoolSort.SetOpaqueEarlyZPass(false);", idxPass1, StringComparison.Ordinal);
        int idxPass8 = chunkRendererCs.IndexOf("poolsByRenderPass[8][m].Render", idxFinallyDisable, StringComparison.Ordinal);

        Assert.True(idxEnable0 > 0 && idxEnable0 < idxPass0 && idxPass0 < idxPass5 && idxPass5 < idxDisableForBlend);
        Assert.True(idxDisableForBlend < idxPass2 && idxPass2 < idxEnable1 && idxEnable1 < idxPass1 && idxPass1 < idxFinallyDisable && idxFinallyDisable < idxPass8);
    }

    [Fact]
    public void ApiPatcher_ShipsValidIlAndSortsPatchedVanillaApi()
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

        int applied = ApiPatcher.PatchMeshDataPoolSort(vanilla.MainModule, contracts.MainModule);
        Assert.Equal(2, applied);
        Assert.Equal(2, ApiPatcher.PatchMeshDataPoolSort(vanilla.MainModule, contracts.MainModule));

        string tempDir = Path.Combine(AppContext.BaseDirectory, "meshpool-sort-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string patchedApiPath = Path.Combine(tempDir, "VintagestoryAPI-patched.dll");
        try
        {
            Assert.True(ApiPatcher.Patch(vanillaApiPath, contractsPath, patchedApiPath));
            using var patchedDef = AssemblyDefinition.ReadAssembly(patchedApiPath);
            Assert.Empty(IlStackVerifier.VerifyModule(patchedDef.MainModule));
            Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(patchedDef.MainModule));

            var alc = new PatchedSortLoadContext(patchedApiPath, contractsPath);
            Assembly patchedApi = alc.LoadFromStream(new MemoryStream(File.ReadAllBytes(patchedApiPath)));
            Assembly patchedContracts = alc.LoadFromStream(new MemoryStream(File.ReadAllBytes(contractsPath)));
            VerifyPatchedVanillaSort(patchedApi, patchedContracts);
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
    public void RealGlContext_FrontToBackSortReducesOverdrawOnOverlappingOpaqueLayers()
    {
        bool origSort = OptimumConfig.FrontToBackChunkSortEnabled;
        try
        {
            OptimumConfig.FrontToBackChunkSortEnabled = true;

            GLFWProvider.CheckForMainThread = false;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new OpenTK.Mathematics.Vector2i(128, 128),
            });
            window.MakeCurrent();
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

            const string vsrc = @"#version 330 core
layout(location = 0) in vec3 inPos;
void main() { gl_Position = vec4(inPos, 1.0); }";
            const string fsrc = @"#version 330 core
uniform float alphaTest;
out vec4 outColor;
void main() {
    float alpha = 1.0;
    if (alpha < alphaTest) discard;
    outColor = vec4(0.2, 0.7, 0.3, 1.0);
}";
            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vsrc);
            GL.CompileShader(vs);
            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fsrc);
            GL.CompileShader(fs);
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);

            // 8 overlapping screen-filling layers submitted back-to-front in pool list (near layer added LAST across distant Z pools)
            const int Layers = 8;
            var glHarness = new GlEarlyZHarness();
            var master = new MeshDataPoolMasterManager(glHarness.Api);
            var frustum = CreateOpenFrustum(5000);
            var manager = new MeshDataPoolManager(master, frustum, glHarness.Api, 64, 96, 32);

            // Add 8 pools at world z = 800..100 (near pool added LAST!), mapping each pool's NDC depth cleanly to its distance!
            for (int layer = 0; layer < Layers; layer++)
            {
                float worldDist = (Layers - layer) * 100f; // 800m, 700m, ... 100m
                float ndcZ = 0.8f - layer * 0.2f; // 0.8 (far) down to -0.6 (near)
                var mesh = CreateTestMesh(4, 6);
                mesh.xyz = new[]
                {
                    -1f, -1f, ndcZ,
                     1f, -1f, ndcZ,
                     1f,  1f, ndcZ,
                    -1f,  1f, ndcZ,
                };
                mesh.Indices = new[] { 0, 1, 2, 0, 2, 3 };
                var origin = new Vec3i(layer * 6000, 0, 0);
                var loc = manager.AddModel(mesh, origin, 0, MakeSphere(0, 0, worldDist, 16f));
                loc.CullVisible = new Bools(true, true);
                loc.LodLevel = 1;
            }

            GL.Viewport(0, 0, 128, 128);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.UseProgram(prog);
            GL.Uniform1(GL.GetUniformLocation(prog, "alphaTest"), 0.001f);

            int query = GL.GenQuery();

            // Unsorted (back-to-front worst case): every later layer passes depth test -> 8x 128*128 samples passed
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.BeginQuery(QueryTarget.SamplesPassed, query);
            OptimumConfig.FrontToBackChunkSortEnabled = false;
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(new Vec3d(0, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }
            GL.Finish();
            GL.EndQuery(QueryTarget.SamplesPassed);
            GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long unsortedSamples);

            // Sorted (front-to-back): near layer renders first -> all 7 rear layers fail Early-Z -> exactly 128*128 samples passed!
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.BeginQuery(QueryTarget.SamplesPassed, query);
            OptimumConfig.FrontToBackChunkSortEnabled = true;
            using (OptimumMeshPoolSort.EnterOpaquePassScope())
            {
                manager.Render(new Vec3d(0, 0, 0), "origin", EnumFrustumCullMode.CullNormal);
            }
            GL.Finish();
            GL.EndQuery(QueryTarget.SamplesPassed);
            GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long sortedSamples);

            Assert.Equal(8L * 128 * 128, unsortedSamples);
            Assert.Equal(128L * 128, sortedSamples);
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());

            master.DisposeAllPools(glHarness.Api);
            GL.DeleteQuery(query);
            GL.DeleteProgram(prog);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }
        finally
        {
            OptimumConfig.FrontToBackChunkSortEnabled = origSort;
        }
    }

    private static Sphere MakeSphere(float x, float y, float z, float r) => new Sphere(x, y, z, r, r, r);

    private static T CreateStub<T>(params (string Method, object? Value)[] overrides) where T : class
    {
        var map = overrides.ToDictionary(x => x.Method, x => x.Value);
        var proxy = DispatchProxy.Create<T, InterceptorProxy>();
        ((InterceptorProxy)(object)proxy).Handler = (m, _) => map.TryGetValue(m.Name, out var v) ? v : null;
        return proxy;
    }

    private sealed class GlEarlyZHarness
    {
        private sealed class GlMeshRef : MeshRef
        {
            public int Vao;
            public int Vbo;
            public int Ebo;
            public override bool Initialized => !Disposed;
        }

        internal readonly ICoreClientAPI Api;

        public GlEarlyZHarness()
        {
            var shader = CreateStub<IShaderProgram>();
            var logger = CreateStub<ILogger>();
            var world = CreateStub<IClientWorldAccessor>(("get_Logger", logger));

            var renderProxy = DispatchProxy.Create<IRenderAPI, InterceptorProxy>();
            ((InterceptorProxy)(object)renderProxy).Handler = (method, args) =>
            {
                switch (method.Name)
                {
                    case "get_UseSSBOs":
                        return false;
                    case "get_CurrentActiveShader":
                        return shader;
                    case "AllocateEmptyMesh":
                    {
                        int vao = GL.GenVertexArray();
                        int vbo = GL.GenBuffer();
                        int ebo = GL.GenBuffer();
                        int xyzBytes = (int)args![0]!;
                        int idxBytes = (int)args[5]!;
                        GL.BindVertexArray(vao);
                        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                        GL.BufferData(BufferTarget.ArrayBuffer, xyzBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
                        GL.EnableVertexAttribArray(0);
                        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
                        GL.BufferData(BufferTarget.ElementArrayBuffer, idxBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                        GL.BindVertexArray(0);
                        return new GlMeshRef { Vao = vao, Vbo = vbo, Ebo = ebo };
                    }
                    case "UpdateChunkMesh":
                    {
                        var mr = (GlMeshRef)args![0]!;
                        var md = (MeshData)args[1]!;
                        GL.BindBuffer(BufferTarget.ArrayBuffer, mr.Vbo);
                        GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)md.XyzOffset, md.VerticesCount * 12, md.xyz);
                        GL.BindBuffer(BufferTarget.ElementArrayBuffer, mr.Ebo);
                        GL.BufferSubData(BufferTarget.ElementArrayBuffer, (IntPtr)md.IndicesOffset, md.IndicesCount * 4, md.Indices);
                        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                        return null;
                    }
                    case "RenderMesh":
                    {
                        var mr = (GlMeshRef)args![0]!;
                        int[] starts = (int[])args[1]!;
                        int[] sizes = (int[])args[2]!;
                        int groupCount = (int)args[3]!;
                        var offsets = new IntPtr[groupCount];
                        for (int i = 0; i < groupCount; i++) offsets[i] = (IntPtr)starts[i * 2];
                        GL.BindVertexArray(mr.Vao);
                        GL.MultiDrawElements(PrimitiveType.Triangles, sizes, DrawElementsType.UnsignedInt, offsets, groupCount);
                        GL.BindVertexArray(0);
                        return null;
                    }
                    case "DeleteMesh":
                    {
                        if (args![0] is GlMeshRef mr && !mr.Disposed)
                        {
                            GL.DeleteVertexArray(mr.Vao);
                            GL.DeleteBuffer(mr.Vbo);
                            GL.DeleteBuffer(mr.Ebo);
                            mr.Dispose();
                        }
                        return null;
                    }
                    default:
                        return null;
                }
            };

            var capiProxy = DispatchProxy.Create<ICoreClientAPI, InterceptorProxy>();
            ((InterceptorProxy)(object)capiProxy).Handler = (m, _) => m.Name switch
            {
                "get_Render" => renderProxy,
                "get_World" => world,
                "get_Logger" => logger,
                _ => null,
            };
            Api = capiProxy;
        }
    }

    private sealed class PatchedSortLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, Assembly> cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string patchedApiPath;
        private readonly string contractsPath;
        private readonly string depsDir;

        public PatchedSortLoadContext(string patchedApiPath, string contractsPath)
            : base("PatchedMeshPoolSortTestAlc", isCollectible: false)
        {
            this.patchedApiPath = patchedApiPath;
            this.contractsPath = contractsPath;
            this.depsDir = Path.GetDirectoryName(contractsPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? simpleName = assemblyName.Name;
            if (string.IsNullOrEmpty(simpleName)) return null;
            if (cache.TryGetValue(simpleName, out var cached)) return cached;

            string? path = simpleName.ToLowerInvariant() switch
            {
                "vintagestoryapi" => patchedApiPath,
                "optimum.api.contracts" => contractsPath,
                _ => File.Exists(Path.Combine(depsDir, simpleName + ".dll")) ? Path.Combine(depsDir, simpleName + ".dll") : null,
            };

            if (path == null) return null;
            var loaded = LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            cache[simpleName] = loaded;
            return loaded;
        }
    }

    private static void VerifyPatchedVanillaSort(Assembly patchedApi, Assembly patchedContracts)
    {
        Type configType = patchedContracts.GetType("Vintagestory.API.Config.OptimumConfig", true)!;
        Type sortType = patchedContracts.GetType("Vintagestory.API.Config.OptimumMeshPoolSort", true)!;
        configType.GetField("FrontToBackChunkSortEnabled", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, true);

        Type capiType = patchedApi.GetType("Vintagestory.API.Client.ICoreClientAPI", true)!;
        Type renderType = patchedApi.GetType("Vintagestory.API.Client.IRenderAPI", true)!;
        Type worldType = patchedApi.GetType("Vintagestory.API.Client.IClientWorldAccessor", true)!;
        Type loggerType = patchedApi.GetType("Vintagestory.API.Common.ILogger", true)!;
        Type shaderType = patchedApi.GetType("Vintagestory.API.Client.IShaderProgram", true)!;
        Type meshRefBaseType = patchedApi.GetType("Vintagestory.API.Client.MeshRef", true)!;
        Type masterType = patchedApi.GetType("Vintagestory.API.Client.MeshDataPoolMasterManager", true)!;
        Type managerType = patchedApi.GetType("Vintagestory.API.Client.MeshDataPoolManager", true)!;
        Type meshDataType = patchedApi.GetType("Vintagestory.API.Client.MeshData", true)!;
        Type frustumType = patchedApi.GetType("Vintagestory.API.Client.FrustumCulling", true)!;
        Type vec3iType = patchedApi.GetType("Vintagestory.API.MathTools.Vec3i", true)!;
        Type vec3dType = patchedApi.GetType("Vintagestory.API.MathTools.Vec3d", true)!;
        Type blockPosType = patchedApi.GetType("Vintagestory.API.MathTools.BlockPos", true)!;
        Type sphereType = patchedApi.GetType("Vintagestory.API.MathTools.Sphere", true)!;
        Type boolsType = patchedApi.GetType("Vintagestory.API.Datastructures.Bools", true)!;
        Type cullModeEnum = patchedApi.GetType("Vintagestory.API.Client.EnumFrustumCullMode", true)!;
        Type mat4dType = patchedApi.GetType("Vintagestory.API.MathTools.Mat4d", true)!;

        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PatchedSortDyn"), AssemblyBuilderAccess.Run);
        var modBuilder = asmBuilder.DefineDynamicModule("PatchedSortMod");

        var mrTb = modBuilder.DefineType("TestSortMeshRef", System.Reflection.TypeAttributes.Public, meshRefBaseType);
        var tagField = mrTb.DefineField("PoolTag", typeof(int), System.Reflection.FieldAttributes.Public);
        var getInit = mrTb.DefineMethod(
            "get_Initialized",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.SpecialName | System.Reflection.MethodAttributes.HideBySig,
            typeof(bool),
            Type.EmptyTypes);
        var initIl = getInit.GetILGenerator();
        initIl.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        initIl.Emit(System.Reflection.Emit.OpCodes.Ret);
        mrTb.DefineMethodOverride(getInit, meshRefBaseType.GetProperty("Initialized")!.GetMethod!);
        Type concreteMeshRef = mrTb.CreateType()!;
        FieldInfo concreteTagField = concreteMeshRef.GetField("PoolTag")!;

        int nextPoolTag = 0;
        var drawLog = new List<(int tag, int[] starts)>();

        System.Func<object> allocFn = () =>
        {
            object mr = Activator.CreateInstance(concreteMeshRef)!;
            concreteTagField.SetValue(mr, nextPoolTag++);
            return mr;
        };
        System.Action<object, int[], int[], int> renderFn = (mr, starts, _, gc) =>
        {
            int tag = (int)concreteTagField.GetValue(mr)!;
            var copied = new int[gc];
            for (int i = 0; i < gc; i++) copied[i] = starts[i * 2];
            drawLog.Add((tag, copied));
        };

        object logger = EmitStub(modBuilder, "SortLogger", loggerType);
        object shader = EmitStub(modBuilder, "SortShader", shaderType);
        object world = EmitStub(modBuilder, "SortWorld", worldType, ("get_Logger", logger));
        object render = EmitRenderStub(modBuilder, renderType, shader, allocFn, renderFn);
        object capi = EmitStub(modBuilder, "SortCapi", capiType, ("get_Render", render), ("get_World", world), ("get_Logger", logger));

        object frustum = Activator.CreateInstance(frustumType)!;
        frustumType.GetMethod("UpdateViewDistance")!.Invoke(frustum, new object[] { 2000 });
        frustumType.GetField("lod2BiasSq")!.SetValue(frustum, double.MaxValue);
        double[] proj = (double[])mat4dType.GetMethod("Create", Type.EmptyTypes)!.Invoke(null, null)!;
        mat4dType.GetMethod("Ortho")!.Invoke(null, new object[] { proj, -5000d, 5000d, -5000d, 5000d, -5000d, 5000d });
        double[] cam = (double[])mat4dType.GetMethod("Create", Type.EmptyTypes)!.Invoke(null, null)!;
        object zeroBlockPos = Activator.CreateInstance(blockPosType, 0, 0, 0)!;
        frustumType.GetMethod("CalcFrustumEquations")!.Invoke(frustum, new[] { zeroBlockPos, proj, cam });

        object master = Activator.CreateInstance(masterType, capi)!;
        object manager = Activator.CreateInstance(managerType, master, frustum, capi, 1024, 1536, 64, null, null, null, null)!;

        MethodInfo addModel = managerType.GetMethod("AddModel")!;
        MethodInfo renderMethod = managerType.GetMethod("Render")!;
        FieldInfo cullVisibleField = patchedApi.GetType("Vintagestory.API.Client.ModelDataPoolLocation", true)!.GetField("CullVisible")!;
        FieldInfo lodField = patchedApi.GetType("Vintagestory.API.Client.ModelDataPoolLocation", true)!.GetField("LodLevel")!;
        FieldInfo startsField = patchedApi.GetType("Vintagestory.API.Client.ModelDataPoolLocation", true)!.GetField("IndicesStart")!;

        int AddLoc(int ox, float sx)
        {
            object md = Activator.CreateInstance(meshDataType, 16, 24, false, true, true, true)!;
            meshDataType.GetField("VerticesCount")!.SetValue(md, 16);
            meshDataType.GetField("IndicesCount")!.SetValue(md, 24);
            object loc = addModel.Invoke(manager, new[]
            {
                md,
                Activator.CreateInstance(vec3iType, ox, 0, 0)!,
                0,
                Activator.CreateInstance(sphereType, sx, 0f, 0f, 16f, 16f, 16f)!,
            })!;
            cullVisibleField.SetValue(loc, Activator.CreateInstance(boolsType, true, true));
            lodField.SetValue(loc, 1);
            return (int)startsField.GetValue(loc)! * 4;
        }

        // Pool 0 @ 400m, Pool 1 @ 50m (spans @ 90m, 30m), Pool 2 @ 200m
        AddLoc(0, 400f);
        int p1Far = AddLoc(6000, 90f);
        int p1Near = AddLoc(6000, 30f);
        AddLoc(12000, 200f);

        object camPos = Activator.CreateInstance(vec3dType, 0d, 0d, 0d)!;
        object cullNormal = Enum.ToObject(cullModeEnum, 1);
        MethodInfo setPass = sortType.GetMethod("SetOpaqueEarlyZPass")!;

        // 1. Inside opaque pass -> Expect Pool 1 (spans [p1Near, p1Far]), Pool 2, Pool 0
        setPass.Invoke(null, new object[] { true });
        try
        {
            renderMethod.Invoke(manager, new[] { camPos, "origin", cullNormal });
        }
        finally
        {
            setPass.Invoke(null, new object[] { false });
        }

        Assert.Equal(new[] { 1, 2, 0 }, drawLog.Select(d => d.tag).ToArray());
        Assert.Equal(new[] { p1Near, p1Far }, drawLog[0].starts);

        // 2. Outside opaque pass -> Expect original order [0, 1, 2] and spans [p1Far, p1Near]
        drawLog.Clear();
        renderMethod.Invoke(manager, new[] { camPos, "origin", cullNormal });
        Assert.Equal(new[] { 0, 1, 2 }, drawLog.Select(d => d.tag).ToArray());
        Assert.Equal(new[] { p1Far, p1Near }, drawLog[1].starts);
    }

    private static IEnumerable<Type> CollectAllInterfaces(Type iface)
    {
        var seen = new HashSet<Type> { iface };
        var q = new Queue<Type>();
        q.Enqueue(iface);
        while (q.Count > 0)
        {
            foreach (var p in q.Dequeue().GetInterfaces())
            {
                if (seen.Add(p)) q.Enqueue(p);
            }
        }
        return seen;
    }

    private static object EmitStub(ModuleBuilder modBuilder, string name, Type iface, params (string Method, object Value)[] getters)
    {
        var tb = modBuilder.DefineType(name, System.Reflection.TypeAttributes.Public, typeof(object), new[] { iface });
        var fields = new Dictionary<string, FieldBuilder>();
        foreach (var (mName, _) in getters)
        {
            fields[mName] = tb.DefineField("f_" + mName, typeof(object), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        }

        foreach (var itf in CollectAllInterfaces(iface))
        {
            foreach (var m in itf.GetMethods())
            {
                EmitMethod(tb, m, il =>
                {
                    if (fields.TryGetValue(m.Name, out var field))
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field);
                        il.Emit(System.Reflection.Emit.OpCodes.Castclass, m.ReturnType);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    return false;
                });
            }
        }

        Type built = tb.CreateType()!;
        foreach (var (mName, val) in getters)
        {
            built.GetField("f_" + mName)!.SetValue(null, val);
        }
        return Activator.CreateInstance(built)!;
    }

    private static object EmitRenderStub(
        ModuleBuilder modBuilder,
        Type renderType,
        object shaderObj,
        System.Func<object> allocFn,
        System.Action<object, int[], int[], int> renderFn)
    {
        var tb = modBuilder.DefineType("SortStubRender", System.Reflection.TypeAttributes.Public, typeof(object), new[] { renderType });
        var shaderF = tb.DefineField("f_shader", typeof(object), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var allocF = tb.DefineField("f_alloc", typeof(System.Func<object>), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var renderF = tb.DefineField("f_render", typeof(System.Action<object, int[], int[], int>), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);

        foreach (var itf in CollectAllInterfaces(renderType))
        {
            foreach (var m in itf.GetMethods())
            {
                EmitMethod(tb, m, il =>
                {
                    if (m.Name == "get_CurrentActiveShader")
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, shaderF);
                        il.Emit(System.Reflection.Emit.OpCodes.Castclass, m.ReturnType);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    if (m.Name == "AllocateEmptyMesh")
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, allocF);
                        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Func<object>).GetMethod("Invoke")!);
                        il.Emit(System.Reflection.Emit.OpCodes.Castclass, m.ReturnType);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    if (m.Name == "RenderMesh" && m.GetParameters().Length >= 4)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, renderF);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_S, (byte)4);
                        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Action<object, int[], int[], int>).GetMethod("Invoke")!);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        return true;
                    }
                    return false;
                });
            }
        }

        Type built = tb.CreateType()!;
        built.GetField("f_shader")!.SetValue(null, shaderObj);
        built.GetField("f_alloc")!.SetValue(null, allocFn);
        built.GetField("f_render")!.SetValue(null, renderFn);
        return Activator.CreateInstance(built)!;
    }

    private static void EmitMethod(TypeBuilder tb, MethodInfo m, System.Func<ILGenerator, bool> custom)
    {
        var mb = tb.DefineMethod(
            m.Name,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.NewSlot | System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.Final,
            m.ReturnType,
            m.GetParameters().Select(p => p.ParameterType).ToArray());
        if (m.IsGenericMethodDefinition)
        {
            mb.DefineGenericParameters(m.GetGenericArguments().Select(g => g.Name).ToArray());
        }
        var il = mb.GetILGenerator();
        if (!custom(il))
        {
            if (m.ReturnType == typeof(void)) il.Emit(System.Reflection.Emit.OpCodes.Ret);
            else if (!m.ReturnType.IsValueType) { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); il.Emit(System.Reflection.Emit.OpCodes.Ret); }
            else
            {
                var local = il.DeclareLocal(m.ReturnType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldloca_S, local);
                il.Emit(System.Reflection.Emit.OpCodes.Initobj, m.ReturnType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldloc, local);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
            }
        }
        tb.DefineMethodOverride(mb, m);
    }
}
