using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class ShadowFarReuseTests
{
    [Fact]
    public void ConfigPersistence_And_DiagnosticsSummaryWorkCleanly()
    {
        bool origEnabled = OptimumConfig.ShadowFarReuseEnabled;
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "optimum-shadowfar-reuse-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            OptimumConfig.SetDataPath(tempDir);
            OptimumConfig.ShadowFarReuseEnabled = false;
            OptimumConfig.Save();

            OptimumConfig.ShadowFarReuseEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.ShadowFarReuseEnabled);

            OptimumConfig.ShadowFarReuseEnabled = true;
            OptimumDiagnostics.ResetAllCounters();
            OptimumDiagnostics.ShadowFarReuse.Hit();
            OptimumDiagnostics.ShadowFarReuse.Hit();
            OptimumDiagnostics.ShadowFarReuse.Skip();

            string summary = OptimumDiagnostics.GetShadowFarReuseSummary();
            Assert.Contains("enabled=True", summary, StringComparison.Ordinal);
            Assert.Contains("reused=2", summary, StringComparison.Ordinal);
            Assert.Contains("rendered=1", summary, StringComparison.Ordinal);

            OptimumDiagnostics.ResetAllCounters();
            summary = OptimumDiagnostics.GetShadowFarReuseSummary();
            Assert.Contains("reused=0", summary, StringComparison.Ordinal);
            Assert.Contains("rendered=0", summary, StringComparison.Ordinal);
        }
        finally
        {
            OptimumConfig.SetDataPath(null);
            OptimumConfig.ShadowFarReuseEnabled = origEnabled;
            OptimumDiagnostics.ResetAllCounters();
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void ReprojectToShadowMapSpaceMatrix_PreservesWorldSpaceProjectionUnderCameraTranslation()
    {
        double[] baseProj = new double[16];
        baseProj[0] = 2.0 / 286.0;
        baseProj[5] = 2.0 / 160.0;
        baseProj[10] = -2.0 / 380.0;
        baseProj[15] = 1.0;

        double[] lightView = LookAtD(new[] { 120.0, 90.0, 80.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 });
        double[] basePv = MulD(baseProj, lightView);
        double[] offset = OptimumShadowFarReuse.CreateOffsetMatrix();

        float[] baseToShadow = new float[16];
        double[] scratchPv = new double[16];
        double[] scratchTo = new double[16];
        OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(basePv, offset, 0.0, 0.0, 0.0, scratchPv, scratchTo, baseToShadow);

        // World point P_world, cached camera C_c, moved camera C_t = C_c + delta
        double[] worldPoint = { 85.25, 74.5, -42.75 };
        double[] camCached = { 10.0, 64.0, 12.0 };
        double[] delta = { 3.7, -1.8, 4.9 };
        double[] camCurrent = { camCached[0] + delta[0], camCached[1] + delta[1], camCached[2] + delta[2] };

        float[] refInput =
        {
            (float)(worldPoint[0] - camCached[0]),
            (float)(worldPoint[1] - camCached[1]),
            (float)(worldPoint[2] - camCached[2]),
            1.0f
        };
        float[] refProjected = TransformPoint(baseToShadow, refInput);

        float[] reprojectedMatrix = new float[16];
        OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(basePv, offset, delta[0], delta[1], delta[2], scratchPv, scratchTo, reprojectedMatrix);

        float[] curInput =
        {
            (float)(worldPoint[0] - camCurrent[0]),
            (float)(worldPoint[1] - camCurrent[1]),
            (float)(worldPoint[2] - camCurrent[2]),
            1.0f
        };
        float[] reprojectedCoords = TransformPoint(reprojectedMatrix, curInput);

        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(Math.Abs(refProjected[i] - reprojectedCoords[i]), 0.0f, 1e-5f);
        }
    }

    [Fact]
    public void InvalidationRules_CoverQuality1GuardMarginLightRotationTerrainAndEntities()
    {
        bool origEnabled = OptimumConfig.ShadowFarReuseEnabled;
        try
        {
            OptimumConfig.ShadowFarReuseEnabled = true;
            var cache = new OptimumShadowFarReuse();
            double[] basePv = new double[16];
            basePv[0] = 0.01; basePv[5] = 0.01; basePv[10] = -0.01; basePv[15] = 1.0;

            cache.BeginSnapshotRecording();
            cache.RecordEntity(101L, 40.0, 65.0, -30.0);
            cache.RecordEntity(202L, -55.0, 70.0, 20.0);
            cache.CommitFreshRender(
                basePv,
                camX: 0.0, camY: 64.0, camZ: 0.0,
                dimension: 0,
                shadowMapQuality: 2,
                viewportWidth: 1920, viewportHeight: 1080,
                fboId: 11, depthTexId: 22, fboWidth: 4096, fboHeight: 4096,
                fieldOfView: 70, zNear: 0.1,
                shadowRangeFar: 270f, shadowDistance: 135.0,
                boxWidth: 286.0, boxHeight: 168.0,
                lightPosX: 100.0, lightPosY: 120.0, lightPosZ: 50.0,
                usingMoon: false, shadowBoxZExtend: 120.0,
                vegSkip: true, chiselLod: true, chiselDist: 64, entityCull: true, entityCullDist: 80,
                terrainGen: 7);

            bool Reuse(
                double camX = 0.0, double camY = 64.0, double camZ = 0.0,
                int dimension = 0, int quality = 2,
                int vw = 1920, int vh = 1080,
                int fboId = 11, int texId = 22, int fw = 4096, int fh = 4096,
                int fov = 70, double zNear = 0.1,
                double lx = 100.0, double ly = 120.0, double lz = 50.0,
                bool moon = false, double zExt = 120.0,
                bool vegSkip = true, int terrainGen = 7, int miniDimCount = 0)
            {
                return cache.CanReuseMetadata(
                    camX, camY, camZ, dimension, quality, vw, vh,
                    fboId, texId, fw, fh, fov, zNear,
                    lx, ly, lz, moon, zExt,
                    vegSkip, true, 64, true, 80,
                    terrainGen, miniDimCount);
            }

            // Inside 8-block L2 margin (|4,4,4| = 6.93 <= 8) -> valid
            Assert.True(Reuse(camX: 4.0, camY: 68.0, camZ: -4.0));
            // Outside 8-block L2 margin (|6,0,6| = 8.49 > 8) -> invalid
            Assert.False(Reuse(camX: 6.0, camZ: 6.0));
            // Quality == 1 -> never reuse
            Assert.False(Reuse(quality: 1));
            // Dimension / viewport / FBO / FOV / light-source / vegetation toggle / terrain edit / mini-dimension
            Assert.False(Reuse(dimension: 2));
            Assert.False(Reuse(vw: 1280, vh: 720));
            Assert.False(Reuse(fboId: 12));
            Assert.False(Reuse(texId: 23));
            Assert.False(Reuse(fw: 5120, fh: 5120));
            Assert.False(Reuse(fov: 90));
            Assert.False(Reuse(moon: true));
            Assert.False(Reuse(zExt: 121.0));
            Assert.False(Reuse(vegSkip: false));
            Assert.False(Reuse(terrainGen: 8));
            Assert.False(Reuse(miniDimCount: 1));

            // Tiny light change within 1 texel stays valid; significant light rotation invalidates
            Assert.True(Reuse(lx: 100.01, ly: 120.0, lz: 50.0));
            Assert.False(Reuse(lx: 105.0, ly: 118.0, lz: 48.0));

            // Config disabled invalidates reuse
            OptimumConfig.ShadowFarReuseEnabled = false;
            Assert.False(Reuse());
            OptimumConfig.ShadowFarReuseEnabled = true;

            // Entities: exact match or < 0.25 block move -> valid
            cache.BeginEntityCheck();
            Assert.True(cache.CheckEntity(101L, 40.1, 65.0, -30.1));
            Assert.True(cache.CheckEntity(202L, -55.0, 70.0, 20.0));
            Assert.True(cache.FinishEntityCheck());

            // Entity disappears -> count mismatch fails
            cache.BeginEntityCheck();
            Assert.True(cache.CheckEntity(101L, 40.0, 65.0, -30.0));
            Assert.False(cache.FinishEntityCheck());

            // Unknown new entity enters -> fails
            cache.BeginEntityCheck();
            Assert.False(cache.CheckEntity(999L, 25.0, 65.0, 25.0));

            // Entity moves > 0.25 blocks -> fails
            cache.BeginEntityCheck();
            Assert.False(cache.CheckEntity(101L, 40.4, 65.0, -30.0));

            // Chunk range filtering
            Assert.True(OptimumShadowFarReuse.IsChunkWithinShadowFarRange(0, 0, 0, 128, 0, 0, 270f, 1));
            Assert.False(OptimumShadowFarReuse.IsChunkWithinShadowFarRange(0, 0, 0, 450, 0, 0, 270f, 1));
            Assert.False(OptimumShadowFarReuse.IsChunkWithinShadowFarRange(0, 0, 0, 32, 0, 2, 270f, 1));
            Assert.True(OptimumShadowFarReuse.IsChunkWithinShadowFarRange(0, 0, 0, 999, 0, 1, 270f, 1));
        }
        finally
        {
            OptimumConfig.ShadowFarReuseEnabled = origEnabled;
        }
    }

    [Fact]
    public void PatchedVintagestoryLib_ShipsShadowFarReuseMethodsAndCecilRegistry()
    {
        string patchShadow = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "SystemRenderShadowMap.cs.patch")));
        string patchClient = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "ClientMain.cs.patch")));
        string patchChunk = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "ChunkRenderer.cs.patch")));

        Assert.Contains("TryReuseOptimumShadowFar", patchShadow);
        Assert.Contains("OptimumCommitShadowFarSnapshot", patchShadow);
        Assert.Contains("optimumShadowMapSystem", patchClient);
        Assert.Contains("optimumTerrainShadowGen", patchChunk);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("patches", "cecil-owned.list")));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderShadowMap.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch", cecilList);

        string? tempPatchedPath = null;
        try
        {
            string patchedPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
            using var asm = AssemblyDefinition.ReadAssembly(patchedPath);

            var shadowType = asm.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderShadowMap")!;
            Assert.NotNull(shadowType.Fields.SingleOrDefault(f => f.Name == "optimumShadowFarReuse"));
            var tryReuse = shadowType.Methods.Single(m => m.Name == "TryReuseOptimumShadowFar");
            AssertCallsMember(tryReuse, "CanReuseMetadata");
            AssertCallsMember(tryReuse, "Reproject");
            var onRenderFar = shadowType.Methods.Single(m => m.Name == "OnRenderShadowFar");
            AssertCallsMember(onRenderFar, "OptimumCommitShadowFarSnapshot");

            var clientType = asm.MainModule.GetType("Vintagestory.Client.NoObf.ClientMain")!;
            Assert.NotNull(clientType.Fields.SingleOrDefault(f => f.Name == "optimumShadowMapSystem"));
            var mainLoop = clientType.Methods.Single(m => m.Name == "MainRenderLoop");
            AssertCallsMember(mainLoop, "TryReuseOptimumShadowFar");
            AssertCallsMember(mainLoop, "InvalidateOptimumShadowFar");

            var chunkType = asm.MainModule.GetType("Vintagestory.Client.NoObf.ChunkRenderer")!;
            Assert.NotNull(chunkType.Fields.SingleOrDefault(f => f.Name == "optimumTerrainShadowGen"));
            var swapBuffers = chunkType.Methods.Single(m => m.Name == "SwapVisibleBuffers");
            AssertCallsMember(swapBuffers, "Increment");
            var removeLocs = chunkType.Methods.Single(m => m.Name == "RemoveDataPoolLocations");
            AssertCallsMember(removeLocs, "IsChunkWithinShadowFarRange");
        }
        finally
        {
            if (tempPatchedPath != null && File.Exists(tempPatchedPath))
            {
                try { File.Delete(tempPatchedPath); } catch { }
            }
        }
    }

    [GlContextFact]
    public void RealGlContext_ShadowFarShaderReprojectionMatchesFreshPass()
    {
        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(64, 64),
        });
        window.MakeCurrent();
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

        const int shadowRes = 4096;
        const int winW = 256, winH = 144;
        int shadowProg = CreateProgram(ShadowVertShader, ShadowFragShader);
        int mainProg = CreateProgram(LitVertShader, LitFragShader);
        BuildBoxScene(out int vao, out int vbo, out int vertCount);
        CreateDepthFbo(shadowRes, out int refFbo, out int refTex);
        CreateDepthFbo(shadowRes, out int cachedFbo, out int cachedTex);
        CreateColorDepthFbo(winW, winH, out int outFboA, out int outTexA, out int outDepthA);
        CreateColorDepthFbo(winW, winH, out int outFboB, out int outTexB, out int outDepthB);

        try
        {
            double[] offset = OptimumShadowFarReuse.CreateOffsetMatrix();
            double[] scratchPv = new double[16];
            double[] scratchTo = new double[16];
            float[] toShadowRef = new float[16];
            float[] toShadowCached = new float[16];
            byte[] pxA = new byte[winW * winH * 4];
            byte[] pxB = new byte[winW * winH * 4];

            // Verify zero-pad baseline matches bit-for-bit at delta = (0,0,0)
            double[] exactPv = BuildShadowPvMatrix(new[] { 120.0, 90.0, 80.0 }, farDist: 135.0, padWidth: 0.0, shadowRes);
            RenderDepthFbo(shadowProg, vao, vertCount, refFbo, shadowRes, new Vector3d(0, 0, 0), exactPv);
            RenderDepthFbo(shadowProg, vao, vertCount, cachedFbo, shadowRes, new Vector3d(0, 0, 0), exactPv);
            OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(exactPv, offset, 0, 0, 0, scratchPv, scratchTo, toShadowRef);
            OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(exactPv, offset, 0, 0, 0, scratchPv, scratchTo, toShadowCached);
            RenderLitFbo(mainProg, vao, vertCount, refTex, shadowRes, outFboA, winW, winH, new Vector3d(0, 0, 0), toShadowRef);
            RenderLitFbo(mainProg, vao, vertCount, cachedTex, shadowRes, outFboB, winW, winH, new Vector3d(0, 0, 0), toShadowCached);
            GL.Finish();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, outFboA);
            GL.ReadPixels(0, 0, winW, winH, PixelFormat.Rgba, PixelType.UnsignedByte, pxA);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, outFboB);
            GL.ReadPixels(0, 0, winW, winH, PixelFormat.Rgba, PixelType.UnsignedByte, pxB);
            Assert.Equal(pxA, pxB);

            // Verify padded Quality-2 cache (4096x4096, GuardMargin=8 -> +16 pad) matches fresh render across camera deltas
            double[] cachedPv = BuildShadowPvMatrix(new[] { 120.0, 90.0, 80.0 }, farDist: 135.0, padWidth: 16.0, shadowRes);
            RenderDepthFbo(shadowProg, vao, vertCount, cachedFbo, shadowRes, new Vector3d(0, 0, 0), cachedPv);

            foreach (var delta in new[] { new Vector3d(0, 0, 0), new Vector3d(3.4, -1.2, 4.1), new Vector3d(-5.1, 1.5, 3.8) })
            {
                double[] refPv = BuildShadowPvMatrix(new[] { 120.0, 90.0, 80.0 }, farDist: 135.0, padWidth: 0.0, shadowRes);
                RenderDepthFbo(shadowProg, vao, vertCount, refFbo, shadowRes, delta, refPv);
                OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(refPv, offset, 0, 0, 0, scratchPv, scratchTo, toShadowRef);
                RenderLitFbo(mainProg, vao, vertCount, refTex, shadowRes, outFboA, winW, winH, delta, toShadowRef);

                OptimumShadowFarReuse.ReprojectToShadowMapSpaceMatrix(cachedPv, offset, delta.X, delta.Y, delta.Z, scratchPv, scratchTo, toShadowCached);
                RenderLitFbo(mainProg, vao, vertCount, cachedTex, shadowRes, outFboB, winW, winH, delta, toShadowCached);

                GL.Finish();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, outFboA);
                GL.ReadPixels(0, 0, winW, winH, PixelFormat.Rgba, PixelType.UnsignedByte, pxA);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, outFboB);
                GL.ReadPixels(0, 0, winW, winH, PixelFormat.Rgba, PixelType.UnsignedByte, pxB);
                Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());

                double sumAbs = 0.0;
                int gt15 = 0;
                int total = winW * winH;
                for (int i = 0; i < pxA.Length; i += 4)
                {
                    int diff = Math.Abs(pxA[i] - pxB[i]);
                    sumAbs += diff;
                    if (diff > 15) gt15++;
                }
                double meanAbs = sumAbs / total;
                double pctGt15 = 100.0 * gt15 / total;
                Assert.True(meanAbs < 2.5, $"Expected meanAbs < 2.5/255 for delta={delta}, got {meanAbs:F3}");
                Assert.True(pctGt15 < 3.0, $"Expected >15/255 pixel mismatch < 3%, got {pctGt15:F2}%");
            }
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteTexture(refTex); GL.DeleteFramebuffer(refFbo);
            GL.DeleteTexture(cachedTex); GL.DeleteFramebuffer(cachedFbo);
            GL.DeleteTexture(outTexA); GL.DeleteRenderbuffer(outDepthA); GL.DeleteFramebuffer(outFboA);
            GL.DeleteTexture(outTexB); GL.DeleteRenderbuffer(outDepthB); GL.DeleteFramebuffer(outFboB);
            GL.DeleteBuffer(vbo); GL.DeleteVertexArray(vao);
            GL.DeleteProgram(shadowProg); GL.DeleteProgram(mainProg);
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
        if (compiledLibPath == null)
        {
            throw new FileNotFoundException("Neither VintagestoryLib-patched.dll nor donor VintagestoryLib.dll was found.");
        }
        string? vanillaLibPath = new[] { ".vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll", ".vanilla/win-x64/vintagestory/VintagestoryLib.dll" }
            .Select(rel => { try { return PatchReader.FindRepositoryFile(rel); } catch (FileNotFoundException) { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));
        if (vanillaLibPath == null)
        {
            throw new FileNotFoundException("Vanilla VintagestoryLib.dll not found under .vanilla/win-x64/vintagestory.");
        }
        tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-shadowfar-patched-{Guid.NewGuid():N}.dll");
        int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath, tempPatchedPath });
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

    private static float[] TransformPoint(float[] m, float[] v)
    {
        float[] r = new float[4];
        for (int row = 0; row < 4; row++)
        {
            r[row] = m[row] * v[0] + m[4 + row] * v[1] + m[8 + row] * v[2] + m[12 + row] * v[3];
        }
        return r;
    }

    private static double[] BuildShadowPvMatrix(double[] lightPos, double farDist, double padWidth, int shadowRes)
    {
        double w = farDist * 2.0 + padWidth;
        double h = w / (16.0 / 9.0);
        double l = farDist * 2.0 + 120.0 + padWidth;
        double[] proj = new double[16];
        proj[0] = 2.0 / w; proj[5] = 2.0 / h; proj[10] = -2.0 / l; proj[15] = 1.0;
        double[] view = LookAtD(lightPos, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 });
        double[] pv = MulD(proj, view);
        double tex = 2.0 / shadowRes;
        pv[12] += Math.Round(pv[12] / tex) * tex - pv[12];
        pv[13] += Math.Round(pv[13] / tex) * tex - pv[13];
        return pv;
    }

    private static double[] LookAtD(double[] eye, double[] center, double[] up)
    {
        double fx = center[0] - eye[0], fy = center[1] - eye[1], fz = center[2] - eye[2];
        double rlf = 1.0 / Math.Sqrt(fx * fx + fy * fy + fz * fz);
        fx *= rlf; fy *= rlf; fz *= rlf;
        double sx = fy * up[2] - fz * up[1], sy = fz * up[0] - fx * up[2], sz = fx * up[1] - fy * up[0];
        double rls = 1.0 / Math.Sqrt(sx * sx + sy * sy + sz * sz);
        sx *= rls; sy *= rls; sz *= rls;
        double ux = sy * fz - sz * fy, uy = sz * fx - sx * fz, uz = sx * fy - sy * fx;
        var m = new double[16];
        m[0] = sx; m[1] = ux; m[2] = -fx;
        m[4] = sy; m[5] = uy; m[6] = -fy;
        m[8] = sz; m[9] = uz; m[10] = -fz;
        m[12] = -(sx * eye[0] + sy * eye[1] + sz * eye[2]);
        m[13] = -(ux * eye[0] + uy * eye[1] + uz * eye[2]);
        m[14] = fx * eye[0] + fy * eye[1] + fz * eye[2];
        m[15] = 1.0;
        return m;
    }

    private static double[] MulD(double[] a, double[] b)
    {
        var r = new double[16];
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                r[col * 4 + row] = a[row] * b[col * 4] + a[4 + row] * b[col * 4 + 1] + a[8 + row] * b[col * 4 + 2] + a[12 + row] * b[col * 4 + 3];
            }
        }
        return r;
    }

    private static void RenderDepthFbo(int prog, int vao, int vertCount, int fbo, int res, Vector3d camPos, double[] pv)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, res, res);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.CullFace);
        GL.UseProgram(prog);
        float[] mvp = new float[16];
        for (int i = 0; i < 16; i++) mvp[i] = (float)pv[i];
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "mvpMatrix"), 1, false, mvp);
        GL.Uniform3(GL.GetUniformLocation(prog, "origin"), (float)-camPos.X, (float)-camPos.Y, (float)-camPos.Z);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertCount);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void RenderLitFbo(int prog, int vao, int vertCount, int shadowTex, int shadowRes, int fbo, int w, int h, Vector3d camPos, float[] toShadow)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, w, h);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.ClearColor(0.2f, 0.3f, 0.4f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.UseProgram(prog);
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70f), (float)w / h, 0.1f, 300f);
        var view = Matrix4.LookAt(new Vector3(0f, 22f, 0f), new Vector3(0f, 4f, -75f), Vector3.UnitY);
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "projectionMatrix"), false, ref proj);
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "modelViewMatrix"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "toShadowMapSpaceMatrixFar"), 1, false, toShadow);
        GL.Uniform1(GL.GetUniformLocation(prog, "shadowRangeFar"), 270f);
        GL.Uniform1(GL.GetUniformLocation(prog, "shadowMapWidthInv"), 1f / shadowRes);
        GL.Uniform1(GL.GetUniformLocation(prog, "shadowMapHeightInv"), 1f / shadowRes);
        GL.Uniform3(GL.GetUniformLocation(prog, "origin"), (float)-camPos.X, (float)-camPos.Y, (float)-camPos.Z);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, shadowTex);
        GL.Uniform1(GL.GetUniformLocation(prog, "shadowMapFar"), 0);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertCount);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void BuildBoxScene(out int vao, out int vbo, out int vertCount)
    {
        var v = new List<float>();
        void Tri(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
        {
            v.Add(ax); v.Add(ay); v.Add(az);
            v.Add(bx); v.Add(by); v.Add(bz);
            v.Add(cx); v.Add(cy); v.Add(cz);
        }
        // Ground plane spanning far cascade range
        Tri(-140, 0, -140, 140, 0, -140, 140, 0, 140);
        Tri(-140, 0, -140, 140, 0, 140, -140, 0, 140);
        // Far tower (~70 blocks from origin) casting shadow in ShadowFar cascade
        float x0 = -8, x1 = 8, y0 = 0, y1 = 24, z0 = -82, z1 = -66;
        Tri(x0, y1, z0, x1, y1, z0, x1, y1, z1); Tri(x0, y1, z0, x1, y1, z1, x0, y1, z1);
        Tri(x0, y0, z0, x1, y0, z0, x1, y1, z0); Tri(x0, y0, z0, x1, y1, z0, x0, y1, z0);
        Tri(x0, y0, z1, x1, y1, z1, x1, y0, z1); Tri(x0, y0, z1, x0, y1, z1, x1, y1, z1);
        Tri(x0, y0, z0, x0, y1, z1, x0, y0, z1); Tri(x0, y0, z0, x0, y1, z0, x0, y1, z1);
        Tri(x1, y0, z0, x1, y0, z1, x1, y1, z1); Tri(x1, y0, z0, x1, y1, z1, x1, y1, z0);
        vertCount = v.Count / 3;
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, v.Count * sizeof(float), v.ToArray(), BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    private static void CreateDepthFbo(int res, out int fbo, out int tex)
    {
        fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, res, res, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, new[] { 1f, 1f, 1f, 1f });
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void CreateColorDepthFbo(int w, int h, out int fbo, out int colorTex, out int depthRb)
    {
        fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        colorTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, colorTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        depthRb = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthRb);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static int CreateProgram(string vsh, string fsh)
    {
        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vsh);
        GL.CompileShader(vs);
        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fsh);
        GL.CompileShader(fs);
        int p = GL.CreateProgram();
        GL.AttachShader(p, vs);
        GL.AttachShader(p, fs);
        GL.LinkProgram(p);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return p;
    }

    private const string ShadowVertShader = @"#version 330 core
layout(location = 0) in vec3 xyz;
uniform vec3 origin;
uniform mat4 mvpMatrix;
void main() {
    gl_Position = mvpMatrix * vec4(xyz + origin, 1.0);
}";

    private const string ShadowFragShader = @"#version 330 core
void main() {}";

    private const string LitVertShader = @"#version 330 core
layout(location = 0) in vec3 xyz;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform mat4 toShadowMapSpaceMatrixFar;
uniform float shadowRangeFar;
out vec4 shadowCoordsFar;
void main() {
    vec4 worldPos = vec4(xyz + origin, 1.0);
    gl_Position = projectionMatrix * modelViewMatrix * worldPos;
    shadowCoordsFar = toShadowMapSpaceMatrixFar * worldPos;
    float len = length(worldPos.xyz);
    float d = clamp(
        max(max(0.0, 0.03 - shadowCoordsFar.x) * 10.0, max(0.0, shadowCoordsFar.x - 0.97) * 10.0) +
        max(max(0.0, 0.03 - shadowCoordsFar.y) * 10.0, max(0.0, shadowCoordsFar.y - 0.97) * 10.0) +
        max(0.0, shadowCoordsFar.z - 0.98) * 10.0 +
        max(0.0, len / shadowRangeFar - 0.15), 0.0, 1.0);
    d = d * 2.0 - 0.5;
    shadowCoordsFar.w = max(0.0, clamp(1.0 - d, 0.0, 1.0));
    if (shadowCoordsFar.z >= 0.999) shadowCoordsFar.w = 0.0;
}";

    private const string LitFragShader = @"#version 330 core
in vec4 shadowCoordsFar;
uniform sampler2DShadow shadowMapFar;
uniform float shadowMapWidthInv;
uniform float shadowMapHeightInv;
out vec4 outColor;
void main() {
    float totalFar = 9.0;
    if (shadowCoordsFar.w > 0.0) {
        for (int x = -1; x <= 1; x++) {
            for (int y = -1; y <= 1; y++) {
                totalFar -= texture(shadowMapFar, vec3(shadowCoordsFar.xy + vec2(x * shadowMapWidthInv, y * shadowMapHeightInv), shadowCoordsFar.z - 0.0009));
            }
        }
    }
    float b = 1.0 - (totalFar / 9.0) * shadowCoordsFar.w * 0.5;
    outColor = vec4(b, b, b, 1.0);
}";
}
