using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

using LinkedProgram = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The generic native draw (VulkanClientPlatform.NativeStated.cs, Platform/StatedDraw.cs) against a
/// native draw whose pipeline and pass are written out by hand, on one device: the same mesh, the
/// same program. The generic route gets its fixed state only through the platform's own virtuals;
/// the reference states what OpenGL would do with those calls. Identical pixels mean the record
/// states what the client said.
///
/// The program is a gui program that is not the registered ShaderPrograms.Gui, so no dedicated
/// route takes the draw: it is exactly the shape of a mod renderer's draw.
/// </summary>
public class NativeStatedTests(ITestOutputHelper output)
{
    private const int Size = 16;

    private sealed class StatedPlatform : VulkanClientPlatform
    {
        public StatedPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    public enum Mask { All, RedGreen }

    /// <summary>
    /// Blend off, three blend modes, a colour mask and a scissor rectangle: the generic route
    /// draws what the hand-stated reference draws, and records one native draw.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, EnumBlendMode.Standard, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.All, false)]
    [InlineData(true, EnumBlendMode.PremultipliedAlpha, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Brighten, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.RedGreen, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.All, true)]
    public unsafe void TheStatedRouteDrawsWhatTheHandStatedReferenceDraws(bool blend, EnumBlendMode mode, Mask mask, bool scissor)
    {
        using Session session = Open();

        byte[] reference = session.Run(stated: false, blend, mode, mask, scissor);

        long statedBefore = session.Platform.StatedDrawsForTests;
        byte[] native = session.Run(stated: true, blend, mode, mask, scissor);

        Assert.Equal(1, session.Platform.StatedDrawsForTests - statedBefore);
        output.WriteLine("centre reference " + Centre(reference, 0) + " stated " + Centre(native, 0));
        Assert.Equal(reference, native);
        Assert.NotEqual("0,51,102,153", Centre(native, 0));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A target with two colour attachments and only the first selected as a draw buffer: the
    /// second keeps its clear colour - the stated draw buffers are write masks - and the first
    /// matches the reference, which writes the first slot only.
    /// </summary>
    [SkippableFact]
    public unsafe void AnUnselectedDrawBufferKeepsItsContentsOnBothRoutes()
    {
        using Session session = Open();

        (byte[] firstReference, byte[] secondReference) = session.RunTwoTargets(stated: false);
        (byte[] firstStated, byte[] secondStated) = session.RunTwoTargets(stated: true);

        Assert.Equal(firstReference, firstStated);
        Assert.Equal(secondReference, secondStated);
        // The second attachment is still the clear colour (0, 51, 102, 153).
        Assert.Equal("0,51,102,153", Centre(secondStated, 0));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A platform bind is the latest bind: a fork renderer's raw framebuffer bind before it no
    /// longer addresses the generic draws and clears (GL has one binding point).
    /// </summary>
    [Fact]
    public void APlatformBindReplacesAForkBind()
    {
        var platform = new StatedPlatform();
        var target = new FrameBufferRef { FboId = 7, Width = Size, Height = Size, ColorTextureIds = new[] { 1 } };

        platform.NoteForkFramebuffer(12);
        Assert.Equal(12, platform.CurrentTargetId);

        platform.CurrentFrameBuffer = target;
        Assert.Equal(7, platform.CurrentTargetId);

        platform.NoteForkFramebuffer(12);
        platform.BindCurrentFrameBufferKeepViewport(target);
        Assert.Equal(7, platform.CurrentTargetId);
    }

    private static string Centre(byte[] pixels, int offset)
    {
        int i = offset + (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);
        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    private sealed class Session : IDisposable
    {
        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public StatedPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        private FrameBufferRef target = null!;
        private FrameBufferRef twoTargets = null!;
        private MeshRef quad = null!;
        private ShaderProgram gui = null!;
        private int texture;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";

        public static Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-stated-" + Guid.NewGuid().ToString("N"));
            var platform = new StatedPlatform
            {
                DeviceFactory = () =>
                {
                    VulkanDevice created = GpuTest.NewDevice();
                    created.NativeShaderDirectory = manifestDirectory;
                    created.NativeShadersEnabled = true;
                    created.IgnoreModShaderScan = true;
                    return created;
                },
                CrashMarkerDataPath = dataPath,
            };
            if (!platform.InitializeGraphics(IntPtr.Zero, Size, Size, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                return null;
            }
            platform.NativeGuiEnabled = false;

            var session = new Session { Platform = platform, previousPlatform = ScreenManager.Platform, dataPath = dataPath };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.target = CreateTarget(seam, 1);
            session.twoTargets = CreateTarget(seam, 2);
            InstallFrameBuffers(platform, session.target);
            // The draw buffers each target writes, stated the way the platform states its own.
            platform.StateDrawBuffers(session.target.FboId, 1);
            platform.StateDrawBuffers(session.twoTargets.FboId, 1);

            var program = new ShaderProgram { PassName = "gui" };
            Link(seam, program, "gui",
                new[] { "projectionMatrix", "modelViewMatrix", "rgbaIn", "noTexture", "applyColor", "alphaTest" });
            session.gui = program;
            session.texture = Gradient(seam);
            session.quad = platform.UploadMesh(BuildQuad());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            if (quad != null) Platform.DeleteMesh(quad);
            ScreenManager.Platform = previousPlatform!;
            Platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        /// <summary>
        /// One frame: the target bound and cleared, one quad - through the platform with the state
        /// set through its virtuals, or (<paramref name="stated" /> false) the hand-stated reference.
        /// </summary>
        public unsafe byte[] Run(bool stated, bool blend, EnumBlendMode mode, Mask mask, bool scissor)
        {
            Platform.BeginFrame();
            Prepare(target);
            if (stated)
            {
                Platform.GlToggleBlend(blend, mode);
                if (mask == Mask.RedGreen) Platform.GlColorMask(true, true, false, false);
                if (scissor)
                {
                    Platform.GlScissorFlag(true);
                    Platform.GlScissor(4, 4, 8, 8);
                }
                Platform.RenderMesh(quad);
            }
            else
            {
                AttachmentBlend attachment = AttachmentBlend.For(blend, mode);
                if (mask == Mask.RedGreen) attachment.WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit;
                DrawReference(target, attachment,
                    scissor ? new Rect2D(new Offset2D(4, 4), new Extent2D(8, 8)) : null);
            }

            Platform.GlColorMask(true, true, true, true);
            Platform.GlScissorFlag(false);
            byte[] pixels = Read(target.ColorTextureIds[0]);
            Platform.EndFrame();
            return pixels;
        }

        public unsafe (byte[] First, byte[] Second) RunTwoTargets(bool stated)
        {
            Platform.BeginFrame();
            Prepare(twoTargets);
            if (stated)
            {
                Platform.GlToggleBlend(false);
                Platform.RenderMesh(quad);
            }
            else
            {
                DrawReference(twoTargets, AttachmentBlend.For(false, EnumBlendMode.Standard), null);
            }
            byte[] first = Read(twoTargets.ColorTextureIds[0]);
            byte[] second = Read(twoTargets.ColorTextureIds[1]);
            Platform.EndFrame();
            return (first, second);
        }

        private void Prepare(FrameBufferRef frameBuffer)
        {
            VulkanDevice seam = Seam;
            // Clears are not part of the comparison: every attachment starts from the same colour.
            seam.BindFramebuffer(frameBuffer.FboId);
            seam.SetDrawBuffers(frameBuffer.FboId, (1 << frameBuffer.ColorTextureIds.Length) - 1);
            for (int i = 0; i < frameBuffer.ColorTextureIds.Length; i++) seam.ClearColor(i, 0f, 0.2f, 0.4f, 0.6f);
            Platform.StateDrawBuffers(frameBuffer.FboId, 1);

            Platform.CurrentFrameBuffer = frameBuffer;
            Platform.GlDisableDepthTest();
            Platform.GlDepthMask(false);
            Platform.GlDisableCullFace();

            Platform.UseShaderProgram(gui.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = gui;
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["projectionMatrix"], Identity);
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["modelViewMatrix"], Identity);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["rgbaIn"], 1f, 0.75f, 0.5f, 0.8f);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["noTexture"], 0f);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["applyColor"], 1);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["alphaTest"], 0f);
            Platform.BindProgramTexture2D(gui, "tex2d", texture, 0);
            Platform.BindProgramTexture2D(gui, "tex2dOverlay", 0, 1);
        }

        /// <summary>
        /// The quad drawn with everything written out: depth and cull off, slot 0 only, the
        /// full-target viewport, the program's two samplers on the gradient and on nothing.
        /// </summary>
        private void DrawReference(FrameBufferRef frameBuffer, AttachmentBlend attachment, Rect2D? scissor)
        {
            VulkanDevice seam = Seam;
            int meshId = ((VAO)quad).VaoId;
            NativePipeline? pipeline = seam.RequestNativePipeline(new NativePipelineDescription
            {
                ProgramId = gui.ProgramId,
                Blend = new[] { attachment },
                DepthTest = false,
                DepthWrite = false,
                Cull = CullModeFlags.None,
                Topology = seam.NativeMeshTopology(meshId),
                VertexLayoutId = seam.NativeMeshLayoutId(meshId),
                Targets = seam.NativeTargetFormats(frameBuffer.FboId, 1u)!,
            }, out string error);
            Assert.True(pipeline != null, error);
            Assert.True(seam.BeginNativePass(new NativePassDescription
            {
                Name = "Reference",
                FramebufferId = frameBuffer.FboId,
                ColorSlots = 1u,
                Reads = new[] { texture },
                Scissor = scissor,
            }));
            Assert.True(seam.DrawNativeMesh(pipeline!, meshId, new[]
            {
                new NativeTexture(pipeline!.Sampler("tex2d"), texture),
                new NativeTexture(pipeline.Sampler("tex2dOverlay"), 0),
            }));
            seam.EndNativePass();
        }

        private unsafe byte[] Read(int textureId)
        {
            VulkanDevice seam = Seam;
            int reader = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(reader, EnumFramebufferAttachment.ColorAttachment0, textureId, 0);
            seam.SetDrawBuffers(reader, 1);
            seam.BindFramebuffer(reader);
            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            seam.DeleteFramebuffer(reader);
            return pixels;
        }

        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name, string[] uniforms)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                name, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), new ShaderCorpus.ShaderVariant());
            var linked = new LinkedProgram { PassName = name };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new LinkedShader { Type = stage.Stage, Code = stage.Code, PrefixCode = stage.PrefixCode };
                Assert.True(seam.CompileShader(shader));
                if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
            }
            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, seam.GetError() ?? "link failed");
            program.ProgramId = id;
            foreach (string uniform in uniforms)
            {
                int location = seam.GetUniformLocation(id, uniform);
                Assert.True(location != -1, name + " has no location for " + uniform);
                program.uniformLocations[uniform] = location;
            }
        }

        private static FrameBufferRef CreateTarget(VulkanDevice seam, int attachments)
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new int[attachments],
            };
            for (int i = 0; i < attachments; i++)
            {
                target.ColorTextureIds[i] = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                seam.AttachTexture(target.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + i),
                    target.ColorTextureIds[i], 0);
            }
            seam.SetDrawBuffers(target.FboId, (1 << attachments) - 1);
            Assert.True(seam.CheckFramebufferComplete(target.FboId, out string status), status);
            return target;
        }

        private static void InstallFrameBuffers(StatedPlatform platform, FrameBufferRef target)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = target;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        }

        private static unsafe int Gradient(VulkanDevice seam)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                pixels[i] = (byte)(16 + x * 30);
                pixels[i + 1] = (byte)(32 + y * 25);
                pixels[i + 2] = (byte)(((x + y) & 1) * 200 + 20);
                pixels[i + 3] = (byte)(96 + ((x + y) & 3) * 40);
            }
            fixed (byte* first = pixels)
            {
                return seam.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)first, false);
            }
        }

        private static MeshData BuildQuad()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: false);
            float[] positions = { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexWithFlags(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb, 0);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }

    private static readonly Lazy<(string Directory, string Reason)> NativeManifest = new(BuildNativeShaders);

    private static (string, string) BuildNativeShaders()
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return ("", reason);
        using (compiler)
        {
            var builder = new NativeShaderBuilder(compiler!);
            NativeShaderBuildResult result = builder.Build(Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk"), "gui");
            if (!result.Success) return ("", string.Join("\n", result.Errors));
            string root = Path.Combine(Path.GetTempPath(), "optimum-native-stated-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(result, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
