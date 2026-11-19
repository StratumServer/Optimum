using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class SsaoDepthPositionTests
{
    private static readonly string[] ModifiedShaderNames =
    [
        "ssao",
        "chunkopaque",
        "chunktopsoil",
        "standard",
        "entityanimated",
        "instanced",
        "particlescube",
        "celestialobject",
        "helditem",
        "sky",
        "nightsky",
        "transparentcompose",
    ];

    [Fact]
    public void ConfigPersistence_Diagnostics_And_RuntimeFallbackWorkCleanly()
    {
        bool origEnabled = OptimumConfig.SsaoDepthPositionEnabled;
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "optimum-ssao-depth-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            OptimumConfig.ResetShaderCompatibilityForTests();
            OptimumConfig.SetDataPath(tempDir);
            OptimumConfig.SsaoDepthPositionEnabled = false;
            OptimumConfig.Save();

            OptimumConfig.SsaoDepthPositionEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.SsaoDepthPositionEnabled);
            Assert.False(OptimumConfig.EffectiveSsaoDepthPosition);

            OptimumConfig.SsaoDepthPositionEnabled = true;
            Assert.True(OptimumConfig.EffectiveSsaoDepthPosition);

            OptimumConfig.SetSsaoDepthPositionRuntimeCompatible(false);
            Assert.False(OptimumConfig.EffectiveSsaoDepthPosition);

            OptimumConfig.ResetShaderCompatibilityAfterReload();
            Assert.True(OptimumConfig.EffectiveSsaoDepthPosition);

            OptimumDiagnostics.RecordPrimaryGPositionState(true, 1920L * 1080L * 8L);
            Assert.True(OptimumDiagnostics.SsaoDepthPositionDropped);
            Assert.Equal(16_588_800L, OptimumDiagnostics.SsaoGPositionSavedBytes);

            string summary = OptimumDiagnostics.GetSsaoDepthPositionSummary();
            Assert.Contains("enabled=True", summary, StringComparison.Ordinal);
            Assert.Contains("active=True", summary, StringComparison.Ordinal);
            Assert.Contains("gPositionDropped=True", summary, StringComparison.Ordinal);
            Assert.Contains("15.82", summary.Replace(',', '.'), StringComparison.Ordinal);

            OptimumDiagnostics.ResetAllCounters();
            Assert.False(OptimumDiagnostics.SsaoDepthPositionDropped);
            Assert.Equal(0L, OptimumDiagnostics.SsaoGPositionSavedBytes);
        }
        finally
        {
            OptimumConfig.ResetShaderCompatibilityForTests();
            OptimumConfig.SetDataPath(null);
            OptimumConfig.SsaoDepthPositionEnabled = origEnabled;
            OptimumDiagnostics.ResetAllCounters();
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PatchedVintagestoryLib_ShipsAllSsaoDepthPositionMethodsAndCecilRegistry()
    {
        string programCs = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"ClearFrameBuffer\", 1,", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"GlToggleBlend\", 2,", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderFrameBufferDebug\", \"OnRenderFrame2DOverlay\", 1)", programCs);
        Assert.Contains("\"VerifyOptimumSsaoDepthContracts\"", programCs);
        Assert.Contains("\"HasOptimumSsaoDepthContract\"", programCs);
        Assert.Contains("\"DowngradeOptimumSsaoDepthPrefixes\"", programCs);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderFrameBufferDebug.cs.patch", cecilList);

        string? tempPatchedPath = null;
        string patchedLibPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(patchedLibPath);
            var module = asm.MainModule;

            var platform = module.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows")!;
            var setupMethod = platform.Methods.Single(m => m.Name == "SetupDefaultFrameBuffers" && m.Parameters.Count == 0);
            var postMethod = platform.Methods.Single(m => m.Name == "RenderPostprocessingEffects" && m.Parameters.Count == 1);
            var clearMethod = platform.Methods.Single(m => m.Name == "ClearFrameBuffer" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "EnumFrameBuffer");
            var blendMethod = platform.Methods.Single(m => m.Name == "GlToggleBlend" && m.Parameters.Count == 2);

            AssertCallsMember(setupMethod, "get_EffectiveSsaoDepthPosition");
            AssertCallsMember(setupMethod, "RecordPrimaryGPositionState");
            AssertCallsMember(setupMethod, "EnsureOptimumDefaults");
            AssertCallsMember(postMethod, "BindTexture2D");
            AssertContainsIntConstant(clearMethod, 3);
            AssertContainsIntConstant(blendMethod, 3);

            var fbDebug = module.GetType("Vintagestory.Client.NoObf.SystemRenderFrameBufferDebug")!;
            var overlayMethod = fbDebug.Methods.Single(m => m.Name == "OnRenderFrame2DOverlay" && m.Parameters.Count == 1);
            Assert.Contains(overlayMethod.Body.Instructions, i => i.OpCode == OpCodes.Ldlen);

            var registry = module.GetType("Vintagestory.Client.NoObf.ShaderRegistry")!;
            var prefixMethod = registry.Methods.Single(m => m.Name == "registerDefaultShaderCodePrefixes" && m.Parameters.Count == 2);
            var loadMethod = registry.Methods.Single(m => m.Name == "loadRegisteredShaderPrograms" && m.Parameters.Count == 0);
            Assert.Contains(prefixMethod.Body.Instructions, i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("SSAO_DEPTH_RECONSTRUCT", StringComparison.Ordinal));
            AssertCallsMember(loadMethod, "VerifyOptimumSsaoDepthContracts");
            AssertCallsMember(loadMethod, "SetSsaoDepthPositionRuntimeCompatible");
            AssertCallsMember(loadMethod, "DowngradeOptimumSsaoDepthPrefixes");
            AssertCallsMember(loadMethod, "RebuildFrameBuffers");
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
    public void RealGlContext_AllTwelveModifiedShadersCompileAndLinkAcrossDepthModes()
    {
        string vanillaShadersDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/assets/game/shaders/ssao.fsh"))!;
        string vanillaIncludesDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/assets/game/shaderincludes/fogandlight.fsh"))!;
        string optimumShadersDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile("sources/shaders/ssao.fsh"))!;

        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(4, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(64, 64),
        });
        window.MakeCurrent();

        var includes = LoadIncludes(vanillaIncludesDir, vanillaShadersDir);

        foreach (string shaderName in ModifiedShaderNames)
        {
            string vshPath = File.Exists(Path.Combine(optimumShadersDir, shaderName + ".vsh"))
                ? Path.Combine(optimumShadersDir, shaderName + ".vsh")
                : Path.Combine(vanillaShadersDir, shaderName + ".vsh");
            string fshPath = Path.Combine(optimumShadersDir, shaderName + ".fsh");

            string rawVsh = File.ReadAllText(vshPath);
            string rawFsh = File.ReadAllText(fshPath);

            var variants = BuildShaderVariants(shaderName);
            foreach (var (depthMode, useOit, allowDepthOffset) in variants)
            {
                string prefix = BuildPrefix(depthMode, useOit, allowDepthOffset);
                bool ssboChunk = shaderName.StartsWith("chunk", StringComparison.Ordinal);
                string vsSrc = PreprocessShader(rawVsh, prefix, includes, upgradeVersion430: ssboChunk);
                string fsSrc = PreprocessShader(rawFsh, prefix, includes, upgradeVersion430: false);

                int prog = CompileAndLinkProgram(vsSrc, fsSrc, $"{shaderName} (depth={depthMode}, oit={useOit}, depthOff={allowDepthOffset})");
                GL.DeleteProgram(prog);
            }
        }
    }

    [GlContextFact]
    public void RealGlContext_ActualSsaoShaderReconstructsDepthAndMatchesVanillaWithoutGPosition()
    {
        string vanillaIncludesDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/assets/game/shaderincludes/fogandlight.fsh"))!;
        string vanillaShadersDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/assets/game/shaders/ssao.fsh"))!;
        string ssaoFshPath = PatchReader.FindRepositoryFile("sources/shaders/ssao.fsh");
        string rawSsao = File.ReadAllText(ssaoFshPath);
        var includes = LoadIncludes(vanillaIncludesDir, vanillaShadersDir);

        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(4, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(64, 64),
        });
        window.MakeCurrent();
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

        const int width = 1280;
        const int height = 720;
        const int ssaoW = width / 2;
        const int ssaoH = height / 2;

        const string fsVert = @"#version 330 core
layout(location = 0) in vec2 inPos;
out vec2 texcoord;
void main() {
    texcoord = inPos * 0.5 + 0.5;
    gl_Position = vec4(inPos, 0.0, 1.0);
}";
        const string geomVert = @"#version 330 core
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNorm;
layout(location = 2) in float inAtten;
layout(location = 3) in float inLeaves;
uniform mat4 projection;
out vec3 vPos;
out vec3 vNorm;
out float vAtten;
out float vLeaves;
void main() {
    vPos = inPos;
    vNorm = inNorm;
    vAtten = inAtten;
    vLeaves = inLeaves;
    gl_Position = projection * vec4(inPos, 1.0);
}";
        const string geomVanillaFrag = @"#version 330 core
in vec3 vPos;
in vec3 vNorm;
in float vAtten;
in float vLeaves;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
void main() {
    outColor = vec4(0.5, 0.6, 0.7, 1.0);
    outGlow = vec4(0.0, 0.0, 0.0, 1.0);
    outGNormal = vec4(vNorm, vLeaves);
    outGPosition = vec4(vPos, vAtten);
}";
        const string geomDepthFrag = @"#version 330 core
in vec3 vPos;
in vec3 vNorm;
in float vAtten;
in float vLeaves;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outGNormal;
vec2 optimumOctEncodeNormal(vec3 n) {
    float lenSq = dot(n, n);
    if (lenSq <= 1e-10) return vec2(0.0);
    n *= inversesqrt(lenSq);
    vec2 p = n.xy * (1.0 / (abs(n.x) + abs(n.y) + abs(n.z)));
    if (n.z < 0.0) p = (1.0 - abs(p.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}
void main() {
    outColor = vec4(0.5, 0.6, 0.7, 1.0);
    outGlow = vec4(0.0, 0.0, 0.0, 1.0);
    outGNormal = vec4(optimumOctEncodeNormal(vNorm), vAtten, vLeaves);
}";

        string ssaoVanillaSrc = PreprocessShader(rawSsao, BuildPrefix(0, 0, 0), includes, false);
        string ssaoDepthSrc = PreprocessShader(rawSsao, BuildPrefix(1, 0, 0), includes, false);

        int geomVanProg = CompileAndLinkProgram(geomVert, geomVanillaFrag, "geomVanilla");
        int geomOctProg = CompileAndLinkProgram(geomVert, geomDepthFrag, "geomDepthOct");
        int ssaoVanProg = CompileAndLinkProgram(fsVert, ssaoVanillaSrc, "ssaoVanilla");
        int ssaoDepProg = CompileAndLinkProgram(fsVert, ssaoDepthSrc, "ssaoDepth");

        float[] proj = CreatePerspective(MathF.PI / 180f * 70f, (float)width / height, 0.06f, 3000f);
        float[] kernel = BuildSsaoKernel();

        // Fullscreen quad + 3D corner + leaves + angled -z normal + held item (atten=1) + open sky area (depth=1)
        float[] quadVerts = { -1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f };
        int quadVao = GL.GenVertexArray();
        int quadVbo = GL.GenBuffer();
        GL.BindVertexArray(quadVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quadVerts.Length * sizeof(float), quadVerts, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        float[] sceneData = BuildTestSceneVertices();
        int sceneVao = GL.GenVertexArray();
        int sceneVbo = GL.GenBuffer();
        GL.BindVertexArray(sceneVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, sceneVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, sceneData.Length * sizeof(float), sceneData, BufferUsageHint.StaticDraw);
        int stride = 8 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 7 * sizeof(float));
        GL.BindVertexArray(0);

        int noiseTex = CreateSolidTexture(PixelInternalFormat.Rgba16f, 4, 4, new float[4 * 4 * 4]);
        int revealTex = CreateSolidTexture(PixelInternalFormat.Rgba8, 1, 1, new float[] { 1f, 1f, 1f, 1f });

        var (vanFbo, vanColor0, vanColor1, vanNorm, vanPos, vanDepth) = CreatePrimaryFbo(width, height, withGPosition: true, linearGNormal: true);
        var (octFbo, octColor0, octColor1, octNorm, _, octDepth) = CreatePrimaryFbo(width, height, withGPosition: false, linearGNormal: false);
        var (outVanFbo, outVanTex) = CreateSsaoOutFbo(ssaoW, ssaoH);
        var (outOctFbo, outOctTex) = CreateSsaoOutFbo(ssaoW, ssaoH);

        Assert.Equal(FramebufferStatus.FramebufferComplete, GL.CheckNamedFramebufferStatus(octFbo, FramebufferTarget.Framebuffer));

        // Render geometry into both FBOs
        RenderGeometry(vanFbo, width, height, geomVanProg, sceneVao, sceneData.Length / 8, proj);
        RenderGeometry(octFbo, width, height, geomOctProg, sceneVao, sceneData.Length / 8, proj);

        // Render SSAO pass using actual sources/shaders/ssao.fsh
        RenderSsao(outVanFbo, ssaoW, ssaoH, ssaoVanProg, vanPos, vanNorm, noiseTex, revealTex, quadVao, proj, kernel, "gPosition");
        RenderSsao(outOctFbo, ssaoW, ssaoH, ssaoDepProg, octDepth, octNorm, noiseTex, revealTex, quadVao, proj, kernel, "gDepth");

        Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());

        float[] vanPixels = ReadRChannel(outVanFbo, ssaoW, ssaoH);
        float[] octPixels = ReadRChannel(outOctFbo, ssaoW, ssaoH);

        int occludedVan = 0;
        int occludedOct = 0;
        double diffSum = 0;
        var sampleDiffs = new List<string>();
        for (int i = 0; i < vanPixels.Length; i++)
        {
            if (float.IsNaN(vanPixels[i]) || float.IsNaN(octPixels[i]))
            {
                Assert.Fail($"Unexpected NaN at pixel {i}: van={vanPixels[i]}, oct={octPixels[i]}");
            }
            if (vanPixels[i] < 0.99f) occludedVan++;
            if (octPixels[i] < 0.99f) occludedOct++;
            float diff = MathF.Abs(vanPixels[i] - octPixels[i]);
            diffSum += diff;
            if (diff > 0.1f && sampleDiffs.Count < 8 && (i / ssaoW) >= 15)
            {
                sampleDiffs.Add($"({i % ssaoW},{i / ssaoW}):v={vanPixels[i]:F3}/o={octPixels[i]:F3}");
            }
        }

        double meanDiff = diffSum / vanPixels.Length;
        Assert.True(occludedOct > 1000, $"Expected active SSAO occlusion pixels in corner/leaves scene, got occludedOct={occludedOct}, occludedVan={occludedVan}, meanDiff={meanDiff:F5}");
        Assert.True(meanDiff < 0.004, $"Expected close SSAO parity with actual ssao.fsh: occludedVan={occludedVan}, occludedOct={occludedOct}, meanDiff={meanDiff:F5}, samples=[{string.Join(", ", sampleDiffs)}]");

        // Sky region (top-right corner: depth=1.0) and held-item region (atten=1.0) must remain unoccluded (1.0)
        int topRightIdx = (ssaoH - 2) * ssaoW + (ssaoW - 2);
        Assert.InRange(octPixels[topRightIdx], 0.999f, 1.001f);

        int heldItemIdx = (ssaoH / 2) * ssaoW + (int)(ssaoW * 0.75f);
        Assert.InRange(octPixels[heldItemIdx], 0.999f, 1.001f);

        GL.DeleteFramebuffer(vanFbo);
        GL.DeleteFramebuffer(octFbo);
        GL.DeleteFramebuffer(outVanFbo);
        GL.DeleteFramebuffer(outOctFbo);
        GL.DeleteTexture(vanColor0);
        GL.DeleteTexture(vanColor1);
        GL.DeleteTexture(vanNorm);
        GL.DeleteTexture(vanPos);
        GL.DeleteTexture(vanDepth);
        GL.DeleteTexture(octColor0);
        GL.DeleteTexture(octColor1);
        GL.DeleteTexture(octNorm);
        GL.DeleteTexture(octDepth);
        GL.DeleteTexture(outVanTex);
        GL.DeleteTexture(outOctTex);
        GL.DeleteTexture(noiseTex);
        GL.DeleteTexture(revealTex);
        GL.DeleteVertexArray(quadVao);
        GL.DeleteBuffer(quadVbo);
        GL.DeleteVertexArray(sceneVao);
        GL.DeleteBuffer(sceneVbo);
        GL.DeleteProgram(geomVanProg);
        GL.DeleteProgram(geomOctProg);
        GL.DeleteProgram(ssaoVanProg);
        GL.DeleteProgram(ssaoDepProg);
    }

    private static List<(int depthMode, int useOit, int allowDepthOffset)> BuildShaderVariants(string shaderName)
    {
        var list = new List<(int, int, int)> { (1, 0, 0), (0, 0, 0) };
        if (shaderName is "entityanimated")
        {
            list.Add((1, 1, 0));
            list.Add((1, 0, 1));
            list.Add((0, 0, 1));
        }
        else if (shaderName is "standard")
        {
            list.Add((1, 0, 1));
            list.Add((0, 0, 1));
        }
        return list;
    }

    private static string BuildPrefix(int ssaoDepthReconstruct, int useOit, int allowDepthOffset)
    {
        return string.Join("\n", new[]
        {
            "#define FXAA 0",
            "#define SSAOLEVEL 2",
            "#define NORMALVIEW 0",
            "#define BLOOM 1",
            "#define GODRAYS 1",
            "#define FOAMEFFECT 1",
            "#define SHINYEFFECT 1",
            "#define SHADOWQUALITY 1",
            "#define DYNLIGHTS 8",
            $"#define USEOIT {useOit}",
            "#define USESSBO 1",
            "#define WAVINGSTUFF 1",
            "#define MINBRIGHT 0.0",
            "#define MAXANIMATEDELEMENTS 64",
            "#define GREEDYMESH 1",
            "#define GREEDYMESH_GRAD 1",
            $"#define ALLOWDEPTHOFFSET {allowDepthOffset}",
            $"#define SSAO_DEPTH_RECONSTRUCT {ssaoDepthReconstruct}",
        }) + "\n";
    }

    private static Dictionary<string, string> LoadIncludes(string includesDir, string shadersDir)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { includesDir, shadersDir })
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                dict[Path.GetFileName(file).ToLowerInvariant()] = File.ReadAllText(file);
            }
        }
        return dict;
    }

    private static string PreprocessShader(string rawSource, string prefix, Dictionary<string, string> includes, bool upgradeVersion430)
    {
        string normalized = rawSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        string resolved = ResolveIncludes(normalized, includes, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (upgradeVersion430)
        {
            resolved = Regex.Replace(resolved, @"^#version\s+\d+(.*)$", "#version 430$1", RegexOptions.Multiline);
        }
        var match = Regex.Match(resolved, @"^#version\s+[^\n]*\n?", RegexOptions.Multiline);
        if (!match.Success)
        {
            return prefix + resolved;
        }
        int insertAt = match.Index + match.Length;
        return resolved[..insertAt] + prefix + resolved[insertAt..];
    }

    private static string ResolveIncludes(string source, Dictionary<string, string> includes, HashSet<string> seen)
    {
        return Regex.Replace(source, @"^#include\s+(.*)$", match =>
        {
            string name = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (!seen.Add(name) || !includes.TryGetValue(name, out string? inc))
            {
                return "";
            }
            return ResolveIncludes(inc.Replace("\r\n", "\n", StringComparison.Ordinal), includes, seen);
        }, RegexOptions.Multiline);
    }

    private static int CompileAndLinkProgram(string vsSrc, string fsSrc, string label)
    {
        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vsSrc);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out int vsOk);
        string vsLog = GL.GetShaderInfoLog(vs);
        Assert.True(vsOk == 1, $"Vertex shader failed ({label}): {vsLog}");

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fsSrc);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out int fsOk);
        string fsLog = GL.GetShaderInfoLog(fs);
        Assert.True(fsOk == 1, $"Fragment shader failed ({label}): {fsLog}");

        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vs);
        GL.AttachShader(prog, fs);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linkOk);
        string linkLog = GL.GetProgramInfoLog(prog);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        Assert.True(linkOk == 1, $"Program link failed ({label}): {linkLog}");
        return prog;
    }

    private static float[] BuildTestSceneVertices()
    {
        var list = new List<float>();
        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, float atten, float leaves)
        {
            void Push(Vector3 p)
            {
                list.Add(p.X); list.Add(p.Y); list.Add(p.Z);
                list.Add(n.X); list.Add(n.Y); list.Add(n.Z);
                list.Add(atten); list.Add(leaves);
            }
            Push(a); Push(b); Push(c);
            Push(a); Push(c); Push(d);
        }

        // Corner floor + back wall + side wall + angled -z surface + leaves cards + held-item region (atten=1)
        AddQuad(new Vector3(-10, -1.2f, -0.5f), new Vector3(1, -1.2f, -0.5f), new Vector3(1, -1.2f, -15), new Vector3(-10, -1.2f, -15), new Vector3(0, 1, 0), 0f, 0f);
        AddQuad(new Vector3(-10, -1.2f, -4.5f), new Vector3(1, -1.2f, -4.5f), new Vector3(1, 2.2f, -4.5f), new Vector3(-10, 2.2f, -4.5f), new Vector3(0, 0, 1), 0f, 0f);
        AddQuad(new Vector3(-2.0f, -1.2f, -0.5f), new Vector3(-2.0f, -1.2f, -5.0f), new Vector3(-2.0f, 3.0f, -5.0f), new Vector3(-2.0f, 3.0f, -0.5f), new Vector3(1, 0, 0), 0f, 0f);
        AddQuad(new Vector3(-1.2f, -1.2f, -3.4f), new Vector3(0.4f, -1.2f, -4.1f), new Vector3(0.4f, 1.0f, -4.1f), new Vector3(-1.2f, 1.0f, -3.4f), Vector3.Normalize(new Vector3(-0.35f, 0.2f, -0.91f)), 0.05f, 0f);
        AddQuad(new Vector3(-0.4f, -1.2f, -3.5f), new Vector3(0.8f, -1.2f, -3.8f), new Vector3(0.8f, 1.0f, -3.8f), new Vector3(-0.4f, 1.0f, -3.5f), Vector3.Normalize(new Vector3(0.15f, 0.3f, 0.94f)), 0f, 1f);
        // Right-half quad at mid-height with atten=1.0 (mimics held-item/wireframe SSAO disable)
        AddQuad(new Vector3(0.8f, -0.4f, -2.5f), new Vector3(4.0f, -0.4f, -2.5f), new Vector3(4.0f, 0.4f, -2.5f), new Vector3(0.8f, 0.4f, -2.5f), new Vector3(0, 0, 1), 1.0f, 0f);
        return list.ToArray();
    }

    private static void RenderGeometry(int fbo, int width, int height, int prog, int vao, int count, float[] proj)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, width, height);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.ClearDepth(1.0);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.UseProgram(prog);
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "projection"), 1, false, proj);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count);
        GL.BindVertexArray(0);
    }

    private static void RenderSsao(int outFbo, int ssaoW, int ssaoH, int prog, int posOrDepthTex, int normTex, int noiseTex, int revealTex, int quadVao, float[] proj, float[] kernel, string posSamplerName)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, outFbo);
        GL.Viewport(0, 0, ssaoW, ssaoH);
        GL.Disable(EnableCap.DepthTest);
        GL.ClearColor(1f, 1f, 1f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.UseProgram(prog);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, posOrDepthTex);
        GL.Uniform1(GL.GetUniformLocation(prog, posSamplerName), 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, normTex);
        GL.Uniform1(GL.GetUniformLocation(prog, "gNormal"), 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, noiseTex);
        GL.Uniform1(GL.GetUniformLocation(prog, "texNoise"), 2);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, revealTex);
        GL.Uniform1(GL.GetUniformLocation(prog, "revealage"), 3);
        GL.Uniform2(GL.GetUniformLocation(prog, "screenSize"), (float)ssaoW, (float)ssaoH);
        GL.UniformMatrix4(GL.GetUniformLocation(prog, "projection"), 1, false, proj);
        int samplesLoc = GL.GetUniformLocation(prog, "samples");
        if (samplesLoc < 0) samplesLoc = GL.GetUniformLocation(prog, "samples[0]");
        GL.Uniform3(samplesLoc, 64, kernel);
        GL.BindVertexArray(quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
    }

    private static float[] ReadRChannel(int fbo, int w, int h)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        var buf = new float[w * h * 4];
        GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.Float, buf);
        var res = new float[w * h];
        for (int i = 0; i < res.Length; i++) res[i] = buf[i * 4];
        return res;
    }

    private static (int fbo, int c0, int c1, int norm, int pos, int depth) CreatePrimaryFbo(int w, int h, bool withGPosition, bool linearGNormal)
    {
        int fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        int depth = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, depth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, w, h, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);

        int c0 = CreateSolidTexture(PixelInternalFormat.Rgba8, w, h, null);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, c0, 0);
        int c1 = CreateSolidTexture(PixelInternalFormat.Rgba8, w, h, null);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, c1, 0);
        int norm = CreateFloatTexture(w, h, linearGNormal);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, norm, 0);

        int pos = 0;
        if (withGPosition)
        {
            pos = CreateFloatTexture(w, h, true);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment3, TextureTarget.Texture2D, pos, 0);
            GL.DrawBuffers(4, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 });
        }
        else
        {
            GL.DrawBuffers(3, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 });
        }

        return (fbo, c0, c1, norm, pos, depth);
    }

    private static (int fbo, int tex) CreateSsaoOutFbo(int w, int h)
    {
        int fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        int tex = CreateFloatTexture(w, h, true);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        return (fbo, tex);
    }

    private static int CreateSolidTexture(PixelInternalFormat fmt, int w, int h, float[]? data)
    {
        int t = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, t);
        if (data != null)
            GL.TexImage2D(TextureTarget.Texture2D, 0, fmt, w, h, 0, PixelFormat.Rgba, PixelType.Float, data);
        else
            GL.TexImage2D(TextureTarget.Texture2D, 0, fmt, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        return t;
    }

    private static int CreateFloatTexture(int w, int h, bool linear)
    {
        int t = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, t);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, w, h, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        int f = linear ? (int)TextureMinFilter.Linear : (int)TextureMinFilter.Nearest;
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, f);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, f);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, new float[] { 1f, 1f, 1f, 1f });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        return t;
    }

    private static float[] CreatePerspective(float fovy, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovy / 2f);
        float nf = 1f / (near - far);
        return new[]
        {
            f / aspect, 0f, 0f, 0f,
            0f, f, 0f, 0f,
            0f, 0f, (far + near) * nf, -1f,
            0f, 0f, (2f * far * near) * nf, 0f
        };
    }

    private static float[] BuildSsaoKernel()
    {
        var rng = new Random(1234);
        var arr = new float[192];
        for (int i = 0; i < 64; i++)
        {
            var v = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble());
            v.Normalize();
            v *= (float)rng.NextDouble();
            float s = i / 64f;
            v *= 0.1f + 0.9f * (s * s);
            arr[i * 3] = v.X;
            arr[i * 3 + 1] = v.Y;
            arr[i * 3 + 2] = v.Z;
        }
        return arr;
    }

    [Fact]
    public void RebuildAndDisposeFrameBuffers_PreserveNewlyRebuiltActiveTierAndGuardDoubleDelete()
    {
        string platformPatch = PatchReader.ReadPatchedContent(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));
        Assert.Contains("optimumTierCache.DrainAll(optimumDrainScratch);", platformPatch, StringComparison.Ordinal);
        Assert.Contains("if (IsShuttingDown || ReferenceEquals(buffers, frameBuffers))", platformPatch, StringComparison.Ordinal);
        Assert.Contains("if (fb != null && !fb.Disposed)", platformPatch, StringComparison.Ordinal);
        Assert.Contains("targetBuffers[0].ColorTextureIds.Length == expectedPrimaryColors", platformPatch, StringComparison.Ordinal);

        string shaderRegistryPatch = PatchReader.ReadPatchedContent(
            PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch"));
        Assert.Contains("frameBuffers[0].Disposed || frameBuffers[0].ColorTextureIds.Length != expectedColorCount", shaderRegistryPatch, StringComparison.Ordinal);

        string? tempPatchedPath = null;
        string patchedLibPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(patchedLibPath);
            var platform = asm.MainModule.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows")!;
            var rebuildMethod = platform.Methods.Single(m => m.Name == "RebuildFrameBuffers" && m.Parameters.Count == 0);
            var disposeMethod = platform.Methods.Single(m => m.Name == "DisposeFrameBuffers" && m.Parameters.Count == 1);
            var activateTierMethod = platform.Methods.Single(m => m.Name == "OptimumActivateScaleTier" && m.Parameters.Count == 3);

            AssertCallsMember(rebuildMethod, "OptimumDisposeFallbackTiersBeforeRebuild");
            AssertCallsMember(rebuildMethod, "DrainAll");
            AssertCallsMember(rebuildMethod, "SetupDefaultFrameBuffers");
            AssertCallsMember(rebuildMethod, "DisposeFrameBuffers");

            AssertCallsMember(disposeMethod, "get_IsShuttingDown");
            AssertCallsMember(disposeMethod, "OptimumDisposeFallbackTiersBeforeRebuild");
            AssertCallsMember(activateTierMethod, "get_EffectiveSsaoDepthPosition");
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
    public void RealGlContext_RebuildFrameBuffers_AfterSsaoFallbackAndResize_KeepsLiveFbosAliveForExternalPasses()
    {
        bool origSsaoDepth = OptimumConfig.SsaoDepthPositionEnabled;
        bool origDynamicScale = OptimumConfig.DynamicRenderScaleEnabled;
        float origRenderScale = OptimumConfig.RenderScale;
        string? tempPatchedPath = null;
        AssemblyLoadContext? alc = null;

        try
        {
            OptimumConfig.ResetShaderCompatibilityForTests();
            OptimumConfig.SsaoDepthPositionEnabled = true;
            OptimumConfig.DynamicRenderScaleEnabled = false;
            OptimumConfig.RenderScale = 1.0f;

            string patchedLibPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
            string libDir = Path.GetDirectoryName(patchedLibPath)!;
            string repoRoot = Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;
            string vanillaLibDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib");
            string binDir = Path.Combine(repoRoot, "bin", "Release", "net10.0");

            alc = new AssemblyLoadContext($"SsaoFboRebuildTest-{Guid.NewGuid():N}", isCollectible: true);
            alc.Resolving += (ctx, name) =>
            {
                if (string.Equals(name.Name, "VintagestoryAPI", StringComparison.OrdinalIgnoreCase))
                    return typeof(FrameBufferRef).Assembly;
                if (string.Equals(name.Name, "Optimum.Api.Contracts", StringComparison.OrdinalIgnoreCase))
                    return typeof(OptimumConfig).Assembly;
                if (name.Name != null && name.Name.StartsWith("OpenTK", StringComparison.OrdinalIgnoreCase))
                {
                    var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
                        string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                    if (loaded != null) return loaded;
                }
                foreach (string dir in new[] { libDir, binDir, vanillaLibDir, AppContext.BaseDirectory })
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
                }
                return null;
            };

            Assembly libAsm = alc.LoadFromAssemblyPath(patchedLibPath);
            Type platformType = libAsm.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows", throwOnError: true)!;
            Type gameWindowType = libAsm.GetType("Vintagestory.Client.NoObf.GameWindowNative", throwOnError: true)!;
            Type clientSettingsType = libAsm.GetType("Vintagestory.Client.NoObf.ClientSettings", throwOnError: true)!;

            // Configure ClientSettings: SSAO High (2), SSAA 1.0, Shadows 1.
            clientSettingsType.GetProperty("SSAOQuality", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, 2);
            clientSettingsType.GetProperty("SSAA", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, 1.0f);
            clientSettingsType.GetProperty("ShadowMapQuality", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, 1);

            GLFWProvider.CheckForMainThread = false;
            var gws = GameWindowSettings.Default;
            var nws = new NativeWindowSettings
            {
                StartVisible = false,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new Vector2i(320, 180),
            };
            using var window = (NativeWindow)Activator.CreateInstance(gameWindowType, new object[] { gws, nws })!;
            window.MakeCurrent();
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

            object platform = RuntimeHelpers.GetUninitializedObject(platformType);
            platformType.GetField("window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, window);
            object nullLogger = Activator.CreateInstance(libAsm.GetType("Vintagestory.Common.NullLogger", throwOnError: true)!)!;
            platformType.GetField("logger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, nullLogger);
            platformType.GetField("OffscreenBuffer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, true);
            platformType.GetField("SetupSSAO", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, true);
            platformType.GetField("ssaoKernel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, new float[192]);
            platformType.GetField("sampleCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(platform, 1);

            FieldInfo frameBuffersField = platformType.GetField("frameBuffers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            FieldInfo tierCacheField = platformType.GetField("optimumTierCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo screenQuadField = platformType.GetField("screenQuad", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo setupMethod = platformType.GetMethod("SetupDefaultFrameBuffers", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo rebuildMethod = platformType.GetMethod("RebuildFrameBuffers", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo disposeMethod = platformType.GetMethod("DisposeFrameBuffers", BindingFlags.Instance | BindingFlags.Public)!;

            // Step 1: Initial startup with compatible shaders (SSAO depth reconstruction active -> 3 Primary color attachments).
            var initialBuffers = (List<FrameBufferRef>)setupMethod.Invoke(platform, null)!;
            frameBuffersField.SetValue(platform, initialBuffers);
            var initialQuad = (MeshRef)screenQuadField.GetValue(platform)!;
            Assert.NotNull(initialQuad);
            Assert.False(initialQuad.Disposed);
            Assert.Equal(3, initialBuffers[0].ColorTextureIds.Length);
            Assert.False(initialBuffers[0].Disposed);
            Assert.False(initialBuffers[1].Disposed);
            Assert.True(OptimumDiagnostics.SsaoDepthPositionDropped);

            // Step 2: Shader override triggers SSAO depth-position fallback -> RebuildFrameBuffers() at same resolution.
            OptimumConfig.SetSsaoDepthPositionRuntimeCompatible(false);
            rebuildMethod.Invoke(platform, null);

            var fallbackBuffers = (List<FrameBufferRef>)frameBuffersField.GetValue(platform)!;
            Assert.NotSame(initialBuffers, fallbackBuffers);
            Assert.True(initialBuffers[0].Disposed, "Old Primary FBO must be disposed.");
            Assert.True(initialBuffers[1].Disposed, "Old Transparent FBO must be disposed.");
            Assert.Same(initialQuad, screenQuadField.GetValue(platform));
            Assert.False(initialQuad.Disposed, "Fullscreen screenQuad must stay alive across RebuildFrameBuffers for external renderers.");

            // Core #675 regression assertion: new Primary and Transparent FBOs must stay ALIVE with 4 color attachments.
            Assert.False(fallbackBuffers[0].Disposed, "Rebuilt Primary FBO was prematurely disposed.");
            Assert.False(fallbackBuffers[1].Disposed, "Rebuilt Transparent FBO was prematurely disposed.");
            Assert.Equal(4, fallbackBuffers[0].ColorTextureIds.Length);
            Assert.False(OptimumDiagnostics.SsaoDepthPositionDropped);
            Assert.True(GL.IsFramebuffer(fallbackBuffers[0].FboId));
            Assert.True(GL.IsTexture(fallbackBuffers[0].DepthTextureId));
            Assert.True(GL.IsTexture(fallbackBuffers[0].ColorTextureIds[2]));
            Assert.True(GL.IsTexture(fallbackBuffers[0].ColorTextureIds[3]));
            AssertExternalDeferredGBufferComplete(fallbackBuffers[0]);

            // Step 3: Simulate resize + return (e.g. fullscreen toggle) and verify rebuilt FBOs remain alive and cache has 1 entry.
            window.ClientSize = new Vector2i(400, 240);
            rebuildMethod.Invoke(platform, null);
            var resizedBuffers = (List<FrameBufferRef>)frameBuffersField.GetValue(platform)!;
            Assert.True(fallbackBuffers[0].Disposed);
            Assert.False(resizedBuffers[0].Disposed);
            Assert.False(resizedBuffers[1].Disposed);
            Assert.Equal(4, resizedBuffers[0].ColorTextureIds.Length);
            AssertExternalDeferredGBufferComplete(resizedBuffers[0]);

            var cache = (OptimumTierCache<List<FrameBufferRef>>)tierCacheField.GetValue(platform)!;
            Assert.Equal(1, cache.Count);

            // Step 4: Restore compatible shaders (e.g. mod removed / reload) -> depth reconstruction re-engages cleanly.
            OptimumConfig.ResetShaderCompatibilityAfterReload();
            rebuildMethod.Invoke(platform, null);
            var compatBuffers = (List<FrameBufferRef>)frameBuffersField.GetValue(platform)!;
            Assert.True(resizedBuffers[0].Disposed);
            Assert.False(compatBuffers[0].Disposed);
            Assert.False(compatBuffers[1].Disposed);
            Assert.Equal(3, compatBuffers[0].ColorTextureIds.Length);
            Assert.True(OptimumDiagnostics.SsaoDepthPositionDropped);
            Assert.True(GL.IsFramebuffer(compatBuffers[0].FboId));
            Assert.True(GL.IsTexture(compatBuffers[0].ColorTextureIds[2]));

            // Clean teardown.
            platformType.BaseType!.GetProperty("IsShuttingDown", BindingFlags.Instance | BindingFlags.Public)!.SetValue(platform, true);
            disposeMethod.Invoke(platform, new object[] { compatBuffers });
            Assert.True(compatBuffers[0].Disposed);
            Assert.True(initialQuad.Disposed, "Fullscreen screenQuad must be disposed on shutdown.");
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            OptimumConfig.ResetShaderCompatibilityForTests();
            OptimumConfig.SsaoDepthPositionEnabled = origSsaoDepth;
            OptimumConfig.DynamicRenderScaleEnabled = origDynamicScale;
            OptimumConfig.RenderScale = origRenderScale;
            OptimumDiagnostics.ResetAllCounters();
            alc?.Unload();
            if (tempPatchedPath != null && File.Exists(tempPatchedPath))
            {
                try { File.Delete(tempPatchedPath); } catch { }
            }
        }
    }

    private static void AssertExternalDeferredGBufferComplete(FrameBufferRef primary)
    {
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
        int fbo = GL.GenFramebuffer();
        int c0 = GL.GenTexture();
        int c1 = GL.GenTexture();
        int c3 = GL.GenTexture();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            void AttachColor(int tex, FramebufferAttachment att, PixelInternalFormat fmt)
            {
                GL.BindTexture(TextureTarget.Texture2D, tex);
                GL.TexImage2D(TextureTarget.Texture2D, 0, fmt, primary.Width, primary.Height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, att, TextureTarget.Texture2D, tex, 0);
            }

            AttachColor(c0, FramebufferAttachment.ColorAttachment0, PixelInternalFormat.Rgba8);
            AttachColor(c1, FramebufferAttachment.ColorAttachment1, PixelInternalFormat.Rgba16f);
            AttachColor(c3, FramebufferAttachment.ColorAttachment3, PixelInternalFormat.Rgba16f);
            // Alias Primary's live gNormal (ColorTextureIds[2]) and DepthTextureId just like external deferred passes.
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, primary.ColorTextureIds[2], 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, primary.DepthTextureId, 0);

            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            Assert.Equal(FramebufferErrorCode.FramebufferComplete, status);
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(c0);
            GL.DeleteTexture(c1);
            GL.DeleteTexture(c3);
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
            throw new FileNotFoundException("Neither VintagestoryLib-patched.dll nor donor VintagestoryLib.dll was found under Release or Debug.");
        }

        string? vanillaLibPath = new[]
        {
            ".vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryLib.dll",
        }
        .Select(rel =>
        {
            try { return PatchReader.FindRepositoryFile(rel); }
            catch (FileNotFoundException) { return null; }
        })
        .FirstOrDefault(p => p != null && File.Exists(p));

        if (vanillaLibPath == null)
        {
            throw new FileNotFoundException("Vanilla VintagestoryLib.dll not found under .vanilla/win-x64/vintagestory.");
        }

        tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-ssao-test-patched-{Guid.NewGuid():N}.dll");
        int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath, tempPatchedPath });
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(tempPatchedPath), "Optimum.Patcher did not produce the temporary patched VintagestoryLib assembly.");
        return tempPatchedPath;
    }

    private static void AssertCallsMember(MethodDefinition method, string memberName)
    {
        Assert.Contains(method.Body.Instructions, i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == memberName);
    }

    private static void AssertContainsIntConstant(MethodDefinition method, int expected)
    {
        bool found = method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == expected) ||
            (expected == 3 && i.OpCode == OpCodes.Ldc_I4_3));
        Assert.True(found, $"Expected integer constant {expected} in {method.FullName}");
    }
}
