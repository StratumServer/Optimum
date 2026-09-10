using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Pins the shape of the Vulkan backend's integration with the client.
///
/// The backend itself is covered by Optimum.Render.Vulkan.Tests, which needs a
/// GPU. What is checked here is the wiring that reaches into vanilla code, where
/// a mistake is silent rather than loud: a transplant that stops compiling, a
/// duplicate type that only surfaces on a full build, or a backend that stops
/// being reachable because the launcher no longer ships its assembly.
/// </summary>
public class VulkanBackendIntegrationTests
{
    private const string ClientProgramPatch =
        "patches/VintagestoryLib/Vintagestory.Client/ClientProgram.cs.patch";

    /// <summary>
    /// The renderer has to be chosen before the window opens. A window created
    /// with no graphics API cannot be handed back to OpenGL without being
    /// destroyed, so a decision made after <c>AttemptToOpenWindow</c> would be
    /// too late to act on cheaply.
    /// </summary>
    [Fact]
    public void TheRendererIsChosenBeforeTheWindowIsCreated()
    {
        string patch = Read(ClientProgramPatch);
        string added = AddedLines(patch);

        Assert.Contains("OptimumRenderBootstrap.ShouldTryVulkan", added);
        Assert.Contains("ContextAPI.NoAPI", added);

        int decision = added.IndexOf("ShouldTryVulkan", StringComparison.Ordinal);
        int noApi = added.IndexOf("ContextAPI.NoAPI", StringComparison.Ordinal);
        int install = added.IndexOf("OptimumRenderBootstrap.Install", StringComparison.Ordinal);

        Assert.True(decision < noApi, "the backend decision must precede the API choice");
        Assert.True(noApi < install, "the window must be configured before the device is installed");
    }

    /// <summary>
    /// If the device cannot be created after the probe passed, the window has no
    /// graphics API and is unusable for OpenGL. It has to be replaced, or the
    /// client renders GL calls into nothing.
    /// </summary>
    [Fact]
    public void AFailedInstallReopensTheWindowForOpenGl()
    {
        string added = AddedLines(Read(ClientProgramPatch));

        Assert.Contains("OptimumRenderBootstrap.Install", added);
        Assert.Contains("OptimumRender.FallBackToOpenGL", added);
        Assert.Contains("ContextAPI.OpenGL", added);
        Assert.Contains("AttemptToOpenWindow", added);
    }

    /// <summary>
    /// A transplanted method must not contain a lambda the compiler caches in a
    /// generated closure class: injection clones only the named method, and the
    /// verifier then rejects the unresolvable self-reference. ClientProgram::Start
    /// is a transplant target, so everything added to it is written lambda-free.
    /// </summary>
    [Fact]
    public void TheTransplantedStartBodyStaysLambdaFree()
    {
        string added = AddedLines(Read(ClientProgramPatch));

        Assert.DoesNotContain("=>", added);
        Assert.DoesNotContain("delegate", added);
    }

    /// <summary>
    /// The client reaches the renderer only through the seam. A direct reference
    /// would put an assembly dependency on a renderer implementation into a
    /// vanilla assembly, which is exactly what the reflective load avoids.
    /// </summary>
    [Fact]
    public void TheClientNeverNamesTheRendererAssemblyDirectly()
    {
        string added = AddedLines(Read(ClientProgramPatch));

        Assert.DoesNotContain("VulkanDevice", added);
        Assert.DoesNotContain("Optimum.Render.Vulkan", added);
    }

    /// <summary>
    /// The seam and its bootstrap live in the contracts assembly, which the API
    /// patcher merges by type-forwarding rather than duplicating.
    /// </summary>
    [Fact]
    public void TheSeamShipsInTheContractsAssembly()
    {
        string contracts = Read("optimum-api-contracts/optimum-api-contracts.csproj");

        Assert.Contains("optimum-render-device.cs", contracts);
        Assert.Contains("optimum-render-bootstrap.cs", contracts);
    }

    /// <summary>
    /// A type compiled into both the fork and contracts is CS0433 on a full
    /// build, and only on a full build - which is why it is asserted here rather
    /// than left to be discovered.
    /// </summary>
    [Fact]
    public void TheForkExcludesTheTypesThatLiveInContracts()
    {
        string fork = Read("sources/VintagestoryApi/VintagestoryAPI.csproj");

        Assert.Contains("<Compile Remove=\"Client\\optimum-render-device.cs\"", fork);
        Assert.Contains("<Compile Remove=\"Client\\optimum-render-bootstrap.cs\"", fork);
    }

    /// <summary>
    /// OpenGL stays the default until the backend reaches parity, and an
    /// unrecognised value degrades to it rather than failing to parse.
    /// </summary>
    [Fact]
    public void OpenGlRemainsTheDefaultRenderer()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static string Renderer = \"opengl\";", config);
        Assert.Contains("public string Renderer { get; set; } = \"opengl\";", config);
        // Anything the normaliser does not recognise falls back to opengl.
        Assert.Contains("StringComparison.OrdinalIgnoreCase) ? \"auto\" :", config);
        Assert.Contains("\"opengl\";", config);
    }

    /// <summary>
    /// ClientProgram is Cecil-owned, so its patch ships through a method
    /// transplant rather than a recompiled assembly. Dropping it from the list
    /// would make the patch look applied while shipping nothing.
    /// </summary>
    [Fact]
    public void ClientProgramRemainsCecilOwned()
    {
        string owned = Read("patches/cecil-owned.list");
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/ClientProgram.cs.patch", owned);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("new(\"Vintagestory.Client.ClientProgram\", \"Start\", 2)", patcher);
    }

    private const string PlatformPatch =
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch";

    /// <summary>
    /// The guarantee the whole design rests on: with no device installed, the
    /// client runs the vanilla GL body. Each branch is added <em>in front of</em>
    /// the original code rather than replacing it, so an OpenGL session costs one
    /// null check and behaves exactly as it always did.
    ///
    /// Checked by confirming the vanilla GL call is still present alongside the
    /// device call for a representative spread of the routed methods.
    /// </summary>
    [Theory]
    [InlineData("SetViewport", "GL.Viewport(x, y, width, height);")]
    [InlineData("SetScissor", "GL.Scissor(x, y, width, height);")]
    [InlineData("SetDepthMask", "GL.DepthMask(flag);")]
    [InlineData("SetStencilMask", "GL.StencilMask(mask);")]
    [InlineData("SetColorMask", "GL.ColorMask(r, g, b, a);")]
    [InlineData("SetCullFaceMode", "GL.CullFace((TriangleFace)1029);")]
    [InlineData("DeleteTexture", "GL.DeleteTexture(id);")]
    public void RoutedMethodsKeepTheirVanillaOpenGlBody(string deviceCall, string vanillaCall)
    {
        string patch = Read(PlatformPatch);

        Assert.Contains("optimumDevice." + deviceCall, patch);
        // The vanilla line survives, either as untouched context or as an added
        // line where the branch was inserted above it.
        Assert.Contains(vanillaCall, patch);
    }

    /// <summary>
    /// A branch that is not registered as a transplant target compiles into the
    /// donor and then ships nothing, because Optimum patches the vanilla
    /// assembly rather than replacing it. That failure is silent.
    /// </summary>
    [Fact]
    public void EveryRoutedPlatformMethodIsRegisteredAsATransplantTarget()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string[] routed =
        {
            "GlViewport", "GlScissor", "GlScissorFlag",
            "GlEnableDepthTest", "GlDisableDepthTest", "GlDepthMask", "GlDepthFunc",
            "GlEnableCullFace", "GlDisableCullFace", "GlCullFaceBack", "GlCullFaceFront",
            "GlToggleBlend", "GlColorMask", "GLWireframes", "GLLineWidth",
            "GlEnableStencilTest", "GlDisableStencilTest", "GlStencilMask",
            "GlStencilFunc", "GlStencilOp", "GlClearStencil",
            "GetGLShaderVersionString", "GenSampler", "BindTexture2d", "BindTextureCubeMap",
            "GLDeleteTexture", "GlGetMaxTextureSize", "GetGraphicsCardRenderer",
        };

        foreach (string method in routed)
        {
            Assert.True(
                patcher.Contains($"\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"{method}\"",
                    StringComparison.Ordinal),
                $"{method} is routed to the device but is not a Cecil transplant target");
        }
    }

    /// <summary>
    /// The frame is bracketed by the device, and the OpenGL path still reaches
    /// SwapBuffers. Losing either end would either never present or present
    /// twice.
    /// </summary>
    [Fact]
    public void TheDeviceBracketsTheFrameAndOpenGlStillSwaps()
    {
        string added = AddedLines(Read(PlatformPatch));

        Assert.Contains("optimumDevice.BeginFrame();", added);
        Assert.Contains("optimumDevice.Present();", added);

        int begin = added.IndexOf("optimumDevice.BeginFrame();", StringComparison.Ordinal);
        int present = added.IndexOf("optimumDevice.Present();", StringComparison.Ordinal);
        Assert.True(begin < present, "the frame must be opened before it is presented");

        // The vanilla swap survives for the OpenGL path.
        Assert.Contains("SwapBuffers();", Read(PlatformPatch));
    }

    /// <summary>
    /// ClientPlatformWindows bodies are transplant targets too, so the branches
    /// added to them must stay lambda-free for the same reason ClientProgram's do.
    /// </summary>
    [Fact]
    public void ThePlatformBranchesStayLambdaFree()
    {
        string added = AddedLines(Read(PlatformPatch));

        foreach (string line in added.Split('\n'))
        {
            if (!line.Contains("optimumDevice", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("=>", line);
        }
    }

    private const string ShaderProgramBasePatch =
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs.patch";

    /// <summary>
    /// A uniform location on the device path is a byte offset into the generated
    /// block, not a GL location. That works only because the setters read the
    /// same <c>uniformLocations</c> dictionary the routed GetUniformLocation
    /// filled, so the two must stay in agreement.
    /// </summary>
    [Fact]
    public void UniformSettersUseTheLocationTheDeviceHandedOut()
    {
        string added = AddedLines(Read(ShaderProgramBasePatch));

        Assert.Contains("optimumDevice.SetUniform(ProgramId, uniformLocations[uniformName]", added);
        Assert.Contains("optimumDevice.SetUniformArray1(ProgramId, uniformLocations[uniformName]", added);
        Assert.Contains("optimumDevice.SetUniformMatrix(ProgramId, uniformLocations[uniformName]", added);

        string platform = AddedLines(Read(PlatformPatch));
        Assert.Contains("optimumDevice.GetUniformLocation(program.ProgramId, name)", platform);
    }

    /// <summary>
    /// The Vec2i overload casts to float in the GL body, so the shader sees a
    /// vec2; the Vec3i overload does not, so it sees an ivec3. Scalar layout
    /// stores that as three consecutive ints, which is why the components are
    /// written at separate offsets rather than through the float path.
    /// </summary>
    [Fact]
    public void IntegerVectorUniformsKeepTheirIntegerRepresentation()
    {
        string added = AddedLines(Read(ShaderProgramBasePatch));

        // The device lays the three components out itself; the location is opaque here.
        Assert.Contains("value.X, value.Y, value.Z)", added);
        // The Vec2i overload keeps the cast the GL body performs.
        Assert.Contains("(float)value.X, (float)value.Y", added);
    }

    /// <summary>
    /// Binding a texture is three separate operations in GL - aim the sampler at
    /// a unit, activate it, bind the texture - and the device keeps that split.
    /// A unit with a stale sampler override would silently ignore the texture's
    /// own filtering, so the override is cleared when there is no custom sampler.
    /// </summary>
    [Fact]
    public void TextureBindingAimsTheSamplerAndClearsAnyStaleOverride()
    {
        string added = AddedLines(Read(ShaderProgramBasePatch));

        Assert.Contains("optimumDevice.SetSamplerUnit(ProgramId, samplerName, textureNumber)", added);
        Assert.Contains("optimumDevice.BindTexture(textureNumber, textureId)", added);
        Assert.Contains("optimumDevice.BindSampler(textureNumber, 0)", added);
    }

    /// <summary>
    /// Compiling a stage cannot produce SPIR-V on its own, because GL matches
    /// uniforms and varyings by name across the whole program. The device stages
    /// the source and does the real work at link time, where it also assigns the
    /// program id the caller stores.
    /// </summary>
    [Fact]
    public void ShaderStagesAreStagedAtCompileAndTranslatedAtLink()
    {
        string added = AddedLines(Read(PlatformPatch));

        Assert.Contains("optimumDevice.CompileShader(shader)", added);
        Assert.Contains("optimumDevice.LinkProgram(program)", added);
        Assert.Contains("program.ProgramId = optimumProgramId;", added);
        // A link failure is reported the same way the GL path reports one.
        Assert.Contains("Link error in shader program for pass", added);
    }

    /// <summary>
    /// ShaderProgramBase is patched via Cecil transplant like the rest, so it has
    /// to be declared owned or the patch reports as applied while shipping
    /// nothing.
    /// </summary>
    [Fact]
    public void ShaderProgramBaseIsDeclaredCecilOwned()
    {
        string owned = Read("patches/cecil-owned.list");
        Assert.Contains(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs.patch", owned);
    }

    /// <summary>
    /// Render systems index <c>FrameBuffers</c> by <c>EnumFrameBuffer</c>, so the
    /// device path has to populate the same slots the GL path does. A missing or
    /// misplaced one is a null reference or a wrong target deep in a pass, not a
    /// startup failure.
    /// </summary>
    [Theory]
    [InlineData("list[0]", "Primary")]
    [InlineData("list[1]", "Transparent")]
    [InlineData("list[2]", "BlurHorizontalMedRes")]
    [InlineData("list[3]", "BlurVerticalMedRes")]
    [InlineData("list[4]", "FindBright")]
    [InlineData("list[5]", "LiquidDepth")]
    [InlineData("list[7]", "GodRays")]
    [InlineData("list[8]", "BlurVerticalLowRes")]
    [InlineData("list[9]", "BlurHorizontalLowRes")]
    [InlineData("list[10]", "Luma")]
    [InlineData("list[11]", "ShadowmapFar")]
    [InlineData("list[12]", "ShadowmapNear")]
    [InlineData("list[13]", "SSAO")]
    public void TheDevicePathPopulatesEveryFramebufferSlot(string slot, string name)
    {
        string added = AddedLines(Read(PlatformPatch));
        Assert.True(added.Contains(slot + " =", StringComparison.Ordinal),
            $"the device framebuffer setup never assigns {slot} ({name})");
    }

    /// <summary>
    /// The Transparent target shares Primary's depth texture in the GL path.
    /// Giving it its own would let transparent geometry depth-test against an
    /// empty buffer and draw through the world.
    /// </summary>
    [Fact]
    public void TheTransparentTargetSharesPrimaryDepth()
    {
        string added = AddedLines(Read(PlatformPatch));

        Assert.Contains("transparent.DepthTextureId = primary.DepthTextureId;", added);
        Assert.Contains(
            "device.AttachTexture(transparent.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0)",
            added);
    }

    /// <summary>
    /// SSAO turns the Primary target into a four-attachment G-buffer. Getting the
    /// count wrong changes the draw-buffer mask and silently drops the position
    /// or normal write.
    /// </summary>
    [Fact]
    public void SsaoWidensPrimaryToFourAttachments()
    {
        string added = AddedLines(Read(PlatformPatch));

        Assert.Contains("int primaryAttachments = (SetupSSAO ? 4 : 2);", added);
        Assert.Contains("device.SetDrawBuffers(primary.FboId, (1 << primaryAttachments) - 1);", added);
    }

    /// <summary>
    /// The SSAO noise pattern and sample kernel come from a fixed seed, and the
    /// two draw from the same generator in a fixed order. Reordering them changes
    /// the occlusion pattern even though nothing fails.
    /// </summary>
    [Fact]
    public void SsaoNoiseAndKernelKeepTheirSeedAndOrder()
    {
        string added = AddedLines(Read(PlatformPatch));

        Assert.Contains("new Random(5)", added);

        int noise = added.IndexOf("noise[texel * 4]", StringComparison.Ordinal);
        int kernel = added.IndexOf("ssaoKernel[sample * 3]", StringComparison.Ordinal);
        Assert.True(noise >= 0 && kernel >= 0);
        Assert.True(noise < kernel, "the noise texels must be drawn before the sample kernel");
    }

    /// <summary>
    /// The device path's helpers are injected members, not just donor code. An
    /// unregistered one compiles and then is missing at runtime.
    /// </summary>
    [Fact]
    public void TheFramebufferHelpersAreInjectedMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"SetupOptimumFrameBuffers\"", patcher);
        Assert.Contains("\"CreateOptimumColorTarget\"", patcher);
        Assert.Contains("\"CreateOptimumDepthTarget\"", patcher);
    }

    private static string AddedLines(string patch) =>
        string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
