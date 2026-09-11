using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 5: the last graphics-API leaf sites outside the platform
/// call ClientPlatformAbstract virtuals, and the static device seam (IOptimumGraphicsDevice,
/// OptimumRender.Device) is gone. Every leaf virtual carries the GL line the site issued in its
/// ClientPlatformWindows override and the device call in VulkanClientPlatform, is injected by
/// the patcher on both types and is in the runtime self-check.
/// </summary>
public class PlatformSeamDeletionCoverageTests
{
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    private static readonly string[] SeamNames = { "OptimumRender.Device", "IOptimumGraphicsDevice" };

    /// <summary>The trees that ship into the game: the lib donor, the mod forks, the API fork and the contracts.</summary>
    private static readonly string[] ShippedTrees =
    {
        "build/VintagestoryLib",
        "VSEssentials",
        "VSSurvivalMod",
        "VSCreativeMod",
        "VintagestoryApi",
        "optimum-api-contracts",
        "sources",
        "patches",
    };

    [Fact]
    public void NoShippedSourceNamesTheDeletedSeam()
    {
        string root = RepositoryRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "build", "VintagestoryLib")), "build/VintagestoryLib is not materialised");

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string tree in ShippedTrees)
        {
            string directory = Path.Combine(root, tree);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".patch", StringComparison.Ordinal)) continue;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                scanned++;
                string text = File.ReadAllText(file);
                foreach (string name in SeamNames)
                {
                    if (text.Contains(name, StringComparison.Ordinal)) offenders.Add(relative + ": " + name);
                }
            }
        }

        Assert.True(scanned > 1000, "expected to scan the shipped trees, scanned " + scanned + " files");
        Assert.True(offenders.Count == 0, "the deleted device seam is still named:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheContractsKeepOnlyTheBackendDecisionAndTheForkBridge()
    {
        string contracts = Read("VintagestoryApi/Client/optimum-render-device.cs");

        Assert.Contains("public static EnumRenderBackend ActiveBackend = EnumRenderBackend.OpenGL;", contracts);
        Assert.Contains("public static string FallbackReason;", contracts);
        Assert.Contains("public static bool IsVulkan => ActiveBackend == EnumRenderBackend.Vulkan;", contracts);
        Assert.Contains("public static bool NoGraphicsApiWindow;", contracts);
        Assert.Contains("public static void FallBackToOpenGL(string reason)", contracts);
        Assert.Contains("public abstract class OptimumForkGraphics", contracts);
        Assert.DoesNotContain("interface ", contracts);

        // GameWindowNative's pre-window clear keys on the window flag, not on a device.
        string window = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GameWindowNative.cs");
        Assert.Contains("if (!OptimumRender.NoGraphicsApiWindow)", window);

        // The fork bridge is for the forks only: the lib never reaches for it, and the
        // Vulkan platform publishes it with its graphics and withdraws it before teardown.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "build", "VintagestoryLib"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("OptimumForkGraphics", File.ReadAllText(file));
        }
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("OptimumForkGraphics.Active = new VulkanForkGraphics(device);", vulkan);
        string shutdown = Body(vulkan, "public override void ShutdownGraphics()");
        Assert.True(shutdown.IndexOf("OptimumForkGraphics.Active = null;", StringComparison.Ordinal)
            < shutdown.IndexOf("device?.Dispose();", StringComparison.Ordinal));

        foreach (string fork in new[]
        {
            "VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs",
            "VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapTextureArray.cs",
            "VSEssentials/Systems/Weather/Newclouds/CloudRendererVolumetric.cs",
            "VSEssentials/Systems/Weather/Newclouds/CloudRendererMap.cs",
            "VSSurvivalMod/Entity/Behavior/BehaviorHideWaterSurface.cs",
        })
        {
            Assert.Contains("OptimumForkGraphics.Active;", Read(fork));
        }
        Assert.Contains("if (OptimumRender.IsVulkan)", Read("VSEssentials/Systems/WorldMap/ChunkLayer/OptimumBc7Support.cs"));
    }

    /// <summary>Each former seam site calls the platform and no longer issues the GL call itself.</summary>
    [Theory]
    [InlineData("Vintagestory.Client/ScreenManager.cs", "Platform.ClearDefaultDepth(num);|Platform.SetDepthRange(0f, 20000f);", "GL.ClearBuffer|GL.DepthRange")]
    [InlineData("Vintagestory.Client.NoObf/ClientMain.cs", "Platform.SetDepthRange(0f, 20000f);|Platform.SetDepthRange(0f, 1f);", "GL.DepthRange")]
    [InlineData("Vintagestory.Client.NoObf/VAO.cs", "platform.DeleteVertexArrayHandles(this);", "GL.")]
    [InlineData("Vintagestory.Client.NoObf/ChunkRenderer.cs", "game.Platform.SetTextureLodBias(textureIds, bias);|game.Platform.BindSampler(8, 0);", "GL.BindSampler|(TextureParameterName)34049")]
    [InlineData("Vintagestory.Client.NoObf/ShaderRegistry.cs", "platform.SetSamplerLodBias(sampler, bias);", "GL.SamplerParameter")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderFrameBufferDebug.cs", "game.Platform.SetTextureDepthCompare(frameBufferRef.DepthTextureId, 0);|game.Platform.SetTextureDepthCompare(frameBufferRef.DepthTextureId, 34894);", "(TextureParameterName)34892|SetOptimumDepthCompare")]
    [InlineData("Vintagestory.Client.NoObf/SvgLoader.cs", "num = ScreenManager.Platform.LoadTextureFromRgbaPointer(textureWidth, textureHeight, (IntPtr)(nint)ptr);", "GL.")]
    [InlineData("Vintagestory.Client.NoObf/InventoryItemRenderer.cs", "game.Platform.ClearTextureRegion(task.TexPos.atlasTextureId, (int)num, (int)num2, size, size, clearPixels);", "GL.TexSubImage2D")]
    [InlineData("Vintagestory.Client.NoObf/ClientSystemStartup.cs", "if (game.Platform.GraphicsBackendName == \"OpenGL\" && GL.GetString((StringName)7937).Contains(\"Arc(TM)\")", "optimumRendererName")]
    [InlineData("Vintagestory.ClientNative/Screenshot.cs", "Vintagestory.Client.ScreenManager.Platform.ReadDefaultFramebuffer(0, 0, size.Width, size.Height, val.GetPixels());", "GL.ReadPixels")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderSunMoon.cs", "occlQueryId = game.Platform.GenOcclusionQuery();|platform.TryGetOcclusionQueryResult(occlQueryId, out num2)|platform.BeginOcclusionQuery(occlQueryId);|platform.EndOcclusionQuery(occlQueryId);|game.Platform.DeleteOcclusionQuery(occlQueryId);|platform.GlColorMask(false, false, false, false);|platform.GlColorMask(true, true, true, true);", "GL.GenQueries|GL.GetQueryObject|GL.BeginQuery|GL.EndQuery|GL.DeleteQuery|GL.ColorMask")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderOITLayers.cs", "ScreenManager.Platform.SetProgramSamplerUnit(program.ProgramId, \"OITaccumulation\", 7);|ScreenManager.Platform.BeginOitAccumulation(currentTransparentfb);|ScreenManager.Platform.CreateOitTargets(transparentfb, layers, out revealTextureId, out accumTextureId);|ScreenManager.Platform.BindOitTextures(revealTextureId, accumTextureId);|ScreenManager.Platform.GLDeleteTexture(accumTextureId);|platform.ApplyTransparentPassBlendState();", "GL.DrawBuffers|GL.BlendFunc|GL.ClearBuffer|GL.GenTexture|GL.Uniform1|GL.BindTexture|GL.DeleteTexture|SetOptimumOitSampling")]
    public void TheLeafSiteCallsThePlatform(string file, string calls, string forbidden)
    {
        string code = StripComments(Read("build/VintagestoryLib/" + file));
        foreach (string call in calls.Split('|'))
        {
            Assert.True(code.Contains(call, StringComparison.Ordinal), file + " does not call " + call);
        }
        foreach (string token in forbidden.Split('|'))
        {
            Assert.False(code.Contains(token, StringComparison.Ordinal), file + " still contains " + token);
        }
    }

    [Fact]
    public void TheSharedIndexBufferIsDeletedByThePlatform()
    {
        string body = Body(StripComments(Read(AbstractSource)), "public static void DisposeIndexBuffer()");
        Assert.Contains("platform.DeleteMeshHandle(singleIndexBufferId);", body);
        Assert.DoesNotContain("GL.", body);
    }

    /// <summary>
    /// Phase 1 review: VAO.Dispose is the single release point on both backends, as vanilla's
    /// DeleteMesh is only a Dispose. The Vulkan DeleteMesh override must not release the
    /// device mesh itself (a double free of a reusable id), and the Vulkan
    /// DeleteVertexArrayHandles must (MeshRef.Dispose is called directly everywhere).
    /// </summary>
    [Fact]
    public void AMeshIsReleasedOnlyThroughVaoDispose()
    {
        string vulkan = StripComments(VulkanPlatformSource.Read());
        string deleteMesh = Body(vulkan, "public override void DeleteMesh(MeshRef modelref)");
        Assert.Contains("((VAO)modelref).Dispose();", deleteMesh);
        Assert.DoesNotContain("device.DeleteMesh", deleteMesh);
        Assert.Contains("device.DeleteMesh(vao.VaoId);", Body(vulkan, "public override void DeleteVertexArrayHandles(VAO vao)"));

        string swapchain = Read("Optimum.Render.Vulkan/Present/Swapchain.cs");
        Assert.Contains("_retirement.Retire(old, SwapchainPolicy.RetireAfter(old.LastPresentValue));", swapchain);
    }

    [Fact]
    public void TheOitLayersKeepOnlyTheirFailurePathUnitReset()
    {
        string code = StripComments(Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        int glCalls = Regex.Matches(code, @"\bGL\.").Count;
        int unitResets = Regex.Matches(code, Regex.Escape("try { GL.ActiveTexture((TextureUnit)33984); } catch { }")).Count;
        Assert.Equal(unitResets, glCalls);
    }

    /// <summary>(member name, ClientPlatformWindows signature, GL line, VulkanClientPlatform line or null for a documented no-op).</summary>
    public static IEnumerable<object?[]> LeafVirtuals()
    {
        yield return new object?[] { "SetDepthRange", "public override void SetDepthRange(float near, float far)", "GL.DepthRange(near, far);", null };
        yield return new object?[] { "ClearDefaultDepth", "public override void ClearDefaultDepth(float depth)", "GL.ClearBuffer((ClearBuffer)6145, 0, ref depth);", "device.ClearDepth(Math.Clamp(depth, 0f, 1f));" };
        yield return new object?[] { "DeleteMeshHandle", "public override void DeleteMeshHandle(int bufferId)", "GL.DeleteBuffer(bufferId);", "device.DeleteMesh(bufferId);" };
        yield return new object?[] { "DeleteVertexArrayHandles", "public override void DeleteVertexArrayHandles(VAO vao)", "GL.DeleteVertexArray(vao.VaoId);", "device.DeleteMesh(vao.VaoId);" };
        yield return new object?[] { "SetTextureLodBias", "public override void SetTextureLodBias(int[] textureIds, float bias)", "GL.TexParameter((TextureTarget)3553, (TextureParameterName)34049, bias);", "device.SetTextureParameter(textureIds[k], OptimumGlConstants.TextureLodBias, bias);" };
        yield return new object?[] { "SetSamplerLodBias", "public override void SetSamplerLodBias(int samplerId, float bias)", "GL.SamplerParameter(samplerId, (SamplerParameterName)34049, bias);", "device.SetSamplerParameter(samplerId, OptimumGlConstants.TextureLodBias, bias);" };
        yield return new object?[] { "SetTextureDepthCompare", "public override void SetTextureDepthCompare(int textureId, int mode)", "GL.TexParameter((TextureTarget)3553, (TextureParameterName)34892, mode);", "device.SetTextureParameter(textureId, OptimumGlConstants.TextureCompareMode, mode);" };
        yield return new object?[] { "ClearTextureRegion", "public override void ClearTextureRegion(int textureId, int x, int y, int width, int height, int[] pixels)", "GL.TexSubImage2D<int>((TextureTarget)3553, 0, x, y, width, height, (PixelFormat)32993, (PixelType)5121, pixels);", "device.UploadTexture2D(textureId, 0, x, y, width, height, EnumTexturePixelFormat.Rgba, pin.AddrOfPinnedObject());" };
        yield return new object?[] { "LoadTextureFromRgbaPointer", "public override int LoadTextureFromRgbaPointer(int width, int height, IntPtr pixels)", "GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)32856, width, height, 0, (PixelFormat)6408, (PixelType)5121, pixels);", "device.CreateTexture2DRaw(width, height, OptimumGlConstants.Rgba8, pixels, 4);" };
        yield return new object?[] { "SetProgramSamplerUnit", "public override void SetProgramSamplerUnit(int programId, string samplerName, int unit)", "GL.Uniform1(GL.GetUniformLocation(programId, samplerName), unit);", "device.SetSamplerUnit(programId, samplerName, unit);" };
        yield return new object?[] { "CreateOitTargets", "public override void CreateOitTargets(FrameBufferRef transparent, int layers, out int revealTexture, out int accumTexture)", "GL.FramebufferTextureLayer((FramebufferTarget)36160, (FramebufferAttachment)36069, accumTexture, 0, 2);", "device.AttachTexture(transparent.FboId, (EnumFramebufferAttachment)36069, accumTexture, 2);" };
        yield return new object?[] { "BeginOitAccumulation", "public override void BeginOitAccumulation(FrameBufferRef transparent)", "GL.ClearBuffer((ClearBuffer)6144, 5, array3);", "device.SetDrawBuffers(transparent.FboId, 0x3F);" };
        yield return new object?[] { "BindOitTextures", "public override void BindOitTextures(int revealTexture, int accumTexture)", "GL.BindTexture((TextureTarget)35866, accumTexture);", "device.BindTexture(7, accumTexture);" };
        yield return new object?[] { "GenOcclusionQuery", "public override int GenOcclusionQuery()", "GL.GenQueries(1, out queryId);", "return device.CreateOcclusionQuery();" };
        yield return new object?[] { "BeginOcclusionQuery", "public override void BeginOcclusionQuery(int queryId)", "GL.BeginQuery((QueryTarget)35092, queryId);", "device.BeginOcclusionQuery(queryId);" };
        yield return new object?[] { "EndOcclusionQuery", "public override void EndOcclusionQuery(int queryId)", "GL.EndQuery((QueryTarget)35092);", "device.EndOcclusionQuery(queryId);" };
        yield return new object?[] { "TryGetOcclusionQueryResult", "public override bool TryGetOcclusionQueryResult(int queryId, out int samples)", "GL.GetQueryObject(queryId, (GetQueryObjectParam)34918, out samples);", "samples = device.GetQueryResult(queryId);" };
        yield return new object?[] { "DeleteOcclusionQuery", "public override void DeleteOcclusionQuery(int queryId)", "GL.DeleteQuery(queryId);", "device.DeleteQuery(queryId);" };
        yield return new object?[] { "ReadDefaultFramebuffer", "public override void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)", "GL.ReadPixels(x, y, width, height, (PixelFormat)32993, (PixelType)5121, destination);", "device.ReadDefaultFramebuffer(x, y, width, height, destination);" };
        yield return new object?[] { "GraphicsBackendName", "public override string GraphicsBackendName", "return \"OpenGL\";", "public override string GraphicsBackendName => device.BackendName;" };
    }

    [Theory]
    [MemberData(nameof(LeafVirtuals))]
    public void EveryLeafVirtualHasBothBodiesAndIsShippedAndSelfChecked(string name, string signature, string gl, string? vulkanLine)
    {
        string abstractPlatform = StripComments(Read(AbstractSource));
        Assert.Matches(new Regex(@"public\s+virtual\s+[\w<>\[\].]+\s+" + name + @"\b"), abstractPlatform);

        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.Contains(gl, Body(windows, signature));

        string vulkan = VulkanPlatformSource.Read();
        string boundary = char.IsLetterOrDigit(signature[signature.Length - 1]) ? @"\b" : string.Empty;
        Assert.Single(Regex.Matches(vulkan, Regex.Escape(signature) + boundary));
        if (vulkanLine != null)
        {
            Assert.Contains(vulkanLine, vulkan);
        }
        else
        {
            Assert.Equal("{ }", Regex.Replace(Body(vulkan, signature), @"\s+", " ").Trim());
        }

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"" + name + "\",", Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},"));
        Assert.Contains("\"" + name + "\",", Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()", "},"));

        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.True(selfCheck.Contains("new(true, \"" + name + "\"", StringComparison.Ordinal)
            || selfCheck.Contains("new(true, \"get_" + name + "\"", StringComparison.Ordinal), name + " is not in the self-check");
    }

    [Fact]
    public void TheRemovedInjectedHelpersAreNoLongerShipped()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.DoesNotContain("\"SetOptimumDepthCompare\"", patcher);
        Assert.DoesNotContain("\"SetOptimumOitSampling\"", patcher);
    }

    private static string RepositoryRoot() =>
        Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int cursor = start + signature.Length;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
        if (string.CompareOrdinal(source, cursor, "=>", 0, 2) == 0)
        {
            return source.Substring(cursor, source.IndexOf(';', cursor) - cursor + 1);
        }
        int open = source.IndexOf('{', cursor);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
