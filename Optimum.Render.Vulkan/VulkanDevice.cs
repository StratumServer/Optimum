using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

/// <summary>
/// The Vulkan implementation of Optimum's graphics backend seam.
///
/// It presents the OpenGL protocol the game and its mods were written against -
/// set state, set named uniforms on the active program, bind textures to units,
/// draw a mesh - and resolves that into Vulkan at the moment of a draw. Nothing
/// here asks the client to change how it renders, which is the whole point: the
/// alternative would break every render system and every graphics mod.
///
/// Ownership is flat and explicit. Each manager owns one kind of object and hands
/// out integer ids, because the game's public API exposes raw GL names as fields
/// that mods read and pass back.
/// </summary>
public sealed unsafe class VulkanDevice : IOptimumGraphicsDevice
{
    private VulkanContext _context = null!;
    private VulkanCommands _setupCommands = null!;
    private GlStateTracker _state = null!;
    private TextureManager _textures = null!;
    private MeshManager _meshes = null!;
    private RenderTargetManager _targets = null!;
    private GraphicsPipelineCache _pipelines = null!;
    private DescriptorCache _descriptors = null!;
    private FrameRing _frames = null!;
    private ShaderCompiler _shaderCompiler = null!;

    private readonly Dictionary<int, ShaderProgramResources> _programs = new();
    private readonly Dictionary<IShader, StagedStage> _stagedStages = new();
    private readonly List<string> _diagnostics = new();

    /// <summary>
    /// Whether the last draw got its own slice of the frame's uniform ring. A
    /// failure means the draw fell back to whatever is at offset 0, which is a
    /// silently wrong frame rather than a crash - worth being able to see.
    /// </summary>
    private bool _lastUniformAllocationOk = true;

    /// <summary>Sixteen bytes of float defaults followed by sixteen of int.</summary>
    private const ulong DefaultAttributeBufferSize = 32;

    private VulkanBuffer? _defaultAttributes;

    /// <summary>
    /// A one-texel image that stands in for any sampler the client has not bound.
    /// See the placeholder note in BindDescriptors.
    /// </summary>
    private int _placeholderTexture;
    private VulkanBuffer? _placeholderUniforms;

    /// <summary>Texture bound to each unit, and any sampler overriding the texture's own state.</summary>
    private readonly int[] _boundTextures = new int[GlStateTracker.MaxTextureUnits];
    private readonly Sampler[] _unitSamplerOverrides = new Sampler[GlStateTracker.MaxTextureUnits];

    private int _nextProgramId = 1;
    private bool _frameActive;
    private bool _disposed;

    /// <summary>A stage that has been preprocessed but not yet linked.</summary>
    private sealed class StagedStage
    {
        public EnumShaderType Stage;
        public string Code = "";
        public string PrefixCode = "";
        public string Filename = "shader";
    }

    // ------------------------------------------------------------------ lifecycle

    public string BackendName => "Vulkan";

    /// <summary>
    /// Whether this machine can run the backend, decided without a window.
    ///
    /// The check has to happen before the window is created, because a window
    /// opened with no graphics API cannot be handed back to OpenGL without being
    /// destroyed and reopened. Creating an instance and a device is the only
    /// honest way to know - driver support for the required 1.3 features is not
    /// something that can be inferred from a vendor string.
    /// </summary>
    /// <summary>
    /// Whether OPTIMUM_VULKAN_VALIDATION asks for the validation layers.
    ///
    /// Read once: the answer cannot change within a process, because the layers
    /// are baked into the instance.
    /// </summary>
    private static readonly string? ValidationSetting =
        Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_VALIDATION");

    private static readonly bool ValidationRequestedByEnvironment =
        !string.IsNullOrEmpty(ValidationSetting);

    /// <summary>
    /// Where validation messages are mirrored, when the variable names a path
    /// rather than just switching the layers on.
    ///
    /// The client's own error channel only surfaces them when it happens to call
    /// CheckGlError, and a device-lost kills the process before that; a file gets
    /// the message that preceded the loss.
    /// </summary>
    private static readonly string? ValidationLogPath =
        ValidationSetting != null && ValidationSetting.Contains('/') ? ValidationSetting : null;

    private static void MirrorValidationMessage(string message)
    {
        if (ValidationLogPath == null) return;
        try
        {
            System.IO.File.AppendAllText(ValidationLogPath, message + "\n");
        }
        catch (System.IO.IOException)
        {
        }
    }

    public static bool IsSupported(out string failureReason)
    {
        string driver;
        return IsSupported(false, out failureReason, out driver);
    }

    /// <summary>
    /// Probes for a usable device, and on the "auto" setting also decides whether
    /// this driver is one the backend is trusted on.
    ///
    /// An explicit "vulkan" means the user asked for it and gets it wherever it
    /// runs at all. "auto" is the setting a player never chose, so it takes the
    /// backend only on driver families it has actually been exercised against;
    /// everything else stays on OpenGL, which is the path that certainly works.
    /// The list is deliberately about the driver rather than the GPU model:
    /// behaviour that breaks a backend lives in the driver.
    /// </summary>
    public static bool IsSupported(bool automatic, out string failureReason, out string driverName)
    {
        driverName = "unknown";

        var options = new VulkanContextOptions { Headless = true };
        if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason))
        {
            failureReason = reason ?? "no usable Vulkan device";
            return false;
        }

        try
        {
            driverName = context!.Capabilities.DriverName ?? "unknown";
            if (automatic && !IsAllowedForAutomaticSelection(driverName))
            {
                failureReason = "the automatic setting does not select Vulkan on this driver ("
                    + driverName + "); set Renderer to \"vulkan\" to use it anyway";
                return false;
            }
        }
        finally
        {
            context!.Dispose();
        }

        failureReason = null!;
        return true;
    }

    /// <summary>
    /// The driver families the backend is regularly run against. Matching is on a
    /// substring of the reported driver name, because vendors version the rest of
    /// the string freely.
    /// </summary>
    private static readonly string[] AutomaticSelectionAllowList =
    {
        // Exercised continuously during development, both test suite and client.
        "NVIDIA",
        // Mesa's Intel driver, which is also what the Arc target uses on Linux.
        "Intel open-source Mesa driver",
        "Mesa",
        // Windows Intel driver, the Claw's own.
        "Intel Corporation",
        // Mesa's AMD driver.
        "radv",
        "AMD proprietary driver",
    };

    internal static bool IsAllowedForAutomaticSelection(string driverName)
    {
        foreach (string allowed in AutomaticSelectionAllowList)
        {
            if (driverName.Contains(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool Initialize(IntPtr windowHandle, int width, int height, out string failureReason)
    {
        bool headless = windowHandle == IntPtr.Zero;

        var options = new VulkanContextOptions
        {
            // A window handle of zero means no presentation surface, which is how
            // capability probes and tests bring the device up.
            Headless = headless,
            // Validation layers are chosen when the instance is created, so the
            // client's GlDebugMode setting is too late to turn them on - it is
            // applied to DebugMode only after the device exists. OPTIMUM_VULKAN_VALIDATION
            // is the way to get them for a real client session, which is the
            // only place the world-loading paths actually run.
            EnableValidation = DebugMode || ValidationRequestedByEnvironment,
            DebugCallback = message =>
            {
                _diagnostics.Add(message);
                MirrorValidationMessage(message);
            },
            // Surface extensions have to be enabled at instance creation, before
            // any surface can exist, so the window system is asked first.
            RequiredInstanceExtensions = headless
                ? Array.Empty<string>()
                : WindowSurface.RequiredInstanceExtensions(),
        };

        if (!VulkanContext.TryCreate(options, out VulkanContext? context, out failureReason))
        {
            return false;
        }

        _context = context!;
        // Any hard Vulkan failure now reaches the client's error channel and the
        // validation log instead of turning into a silent stall.
        VulkanResult.OnFailure = message =>
        {
            // A failed Vulkan call is an error by definition, so it carries the
            // same prefix the layers' error-severity messages do and reaches the
            // client through GetError.
            _diagnostics.Add(VulkanContext.ErrorPrefix + message);
            MirrorValidationMessage(message);
        };
        MirrorValidationMessage("--- device up on " + _context.Capabilities.DeviceName +
            "; validation layers " + (_context.ValidationEnabled ? "ENABLED" : "NOT AVAILABLE"));
        _setupCommands = new VulkanCommands(_context);
        _state = new GlStateTracker();
        _textures = new TextureManager(_context, _setupCommands);
        _meshes = new MeshManager(_context, _state);
        _targets = new RenderTargetManager(_context, _textures, _state);
        _pipelines = new GraphicsPipelineCache(_context);
        _descriptors = new DescriptorCache(_context);
        _frames = new FrameRing(_context);
        _shaderCompiler = new ShaderCompiler();
        CreateDefaultAttributeBuffer();
        CreatePlaceholderTexture();
        CreatePlaceholderUniformBuffer();

        if (!headless)
        {
            if (!WindowSurface.TryCreate(_context, windowHandle, out SurfaceKHR surface, out string? surfaceError))
            {
                failureReason = surfaceError ?? "could not create a presentation surface";
                return false;
            }

            if (!Swapchain.TryCreate(_context, surface, (uint)width, (uint)height, _vsync,
                    out Swapchain? swapchain, out string? swapchainError))
            {
                failureReason = swapchainError ?? "could not create a swapchain";
                return false;
            }

            _swapchain = swapchain;
            CreateDefaultFramebuffer((uint)width, (uint)height);
        }

        failureReason = null!;
        return true;
    }

    private Swapchain? _swapchain;
    private bool _vsync = true;
    private int _defaultFramebuffer;
    private int _defaultColor;
    private int _defaultDepth;
    private uint _windowWidth;
    private uint _windowHeight;

    /// <summary>
    /// The target the client renders into when it asks for the default
    /// framebuffer.
    ///
    /// It is an ordinary offscreen target rather than the swapchain image,
    /// because the game reads it back for screenshots and because presenting is
    /// where the one flip happens. Rendering straight into a swapchain image
    /// would put that flip in the middle of the pipeline.
    /// </summary>
    private void CreateDefaultFramebuffer(uint width, uint height)
    {
        _windowWidth = Math.Max(width, 1);
        _windowHeight = Math.Max(height, 1);

        _defaultColor = _textures.Create(_windowWidth, _windowHeight, Format.R8G8B8A8Unorm);
        _defaultDepth = _textures.Create(_windowWidth, _windowHeight, Format.D32Sfloat);

        _defaultFramebuffer = _targets.Create(_windowWidth, _windowHeight);
        _targets.Attach(_defaultFramebuffer, 0, _defaultColor);
        _targets.Attach(_defaultFramebuffer, -1, _defaultDepth);
        _targets.SetDrawBuffers(_defaultFramebuffer, 0b1);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("default framebuffer id=" + _defaultFramebuffer +
                " " + _windowWidth + "x" + _windowHeight +
                " swapchain=" + (_swapchain == null
                    ? "none"
                    : _swapchain.Extent.Width + "x" + _swapchain.Extent.Height));
        }
    }

    /// <summary>
    /// Builds the buffer that stands in for GL's constant generic vertex
    /// attribute, holding (0, 0, 0, 1) as floats and again as integers.
    ///
    /// GL guarantees that value for any attribute the draw does not supply, and
    /// shaders here rely on it - a vertex flags word of zero means no glow and no
    /// z-offset, a damage effect of zero means no discard. Vulkan has no such
    /// default, so the value has to come from somewhere real: this buffer, bound
    /// at a reserved binding with stride zero so every vertex reads it.
    /// </summary>
    private void CreateDefaultAttributeBuffer()
    {
        _defaultAttributes = new VulkanBuffer(_context, DefaultAttributeBufferSize,
            BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var floats = new float[] { 0f, 0f, 0f, 1f };
        var integers = new int[] { 0, 0, 0, 1 };

        fixed (float* source = floats)
        {
            System.Buffer.MemoryCopy(source, (void*)_defaultAttributes.Mapped, 16, 16);
        }
        fixed (int* source = integers)
        {
            System.Buffer.MemoryCopy(source, (void*)(_defaultAttributes.Mapped + 16), 16, 16);
        }
    }

    /// <summary>
    /// Builds the one-texel image that fills any sampler binding the client left
    /// empty. Opaque black, which is what GL reads from an unbound texture.
    /// </summary>
    private void CreatePlaceholderTexture()
    {
        var texel = new byte[] { 0, 0, 0, 255 };
        fixed (byte* pixels = texel)
        {
            _placeholderTexture = _textures.Create(1, 1, Format.R8G8B8A8Unorm);
            _textures.Upload(_placeholderTexture, 0, 0, 0, 1, 1, (IntPtr)pixels, 4);
        }
    }

    /// <summary>
    /// Builds the zero-filled buffer that fills any shader-declared uniform block
    /// the client has not supplied a buffer for yet.
    ///
    /// Same reasoning as the placeholder texture: leaving the binding undefined
    /// makes every draw with that program invalid, so a program whose UBO has not
    /// been created yet would take the whole frame down rather than read zeroes.
    /// GL reads zeroes from an unbacked block, so this is also the closer match.
    /// </summary>
    private void CreatePlaceholderUniformBuffer()
    {
        // Large enough for the blocks the game declares - the animation transform
        // block is the biggest at a few tens of kilobytes - and clamped to what
        // the device will actually let a descriptor address.
        ulong size = Math.Min(65536UL, Math.Max(16384UL, _context!.Capabilities.MaxUniformBufferRange));

        _placeholderUniforms = new VulkanBuffer(_context, size,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        if (_placeholderUniforms.Mapped != IntPtr.Zero)
        {
            new Span<byte>((void*)_placeholderUniforms.Mapped, (int)size).Clear();
        }
    }

    private void DestroyDefaultFramebuffer()
    {
        if (_defaultFramebuffer > 0) _targets.Delete(_defaultFramebuffer);
        if (_defaultColor > 0) _textures.Delete(_defaultColor, _frames);
        if (_defaultDepth > 0) _textures.Delete(_defaultDepth, _frames);

        _defaultFramebuffer = 0;
        _defaultColor = 0;
        _defaultDepth = 0;
    }

    public string RendererString => _context?.Capabilities.DeviceName ?? "Vulkan";
    public string VendorString => _context?.Capabilities.DriverName ?? "unknown";
    public string VersionString => _context == null
        ? "unknown"
        : VulkanContext.VersionString(_context.Capabilities.ApiVersion);

    /// <summary>
    /// Reported as a GLSL version because the client parses it to decide whether
    /// a shader's <c>#version</c> is supported. The backend accepts everything the
    /// translator accepts, which is well above what any shader in the game asks
    /// for.
    /// </summary>
    public string ShaderVersionString => "4.50";

    public int MaxTextureSize => (int)(_context?.Capabilities.MaxImageDimension2D ?? 0);
    public bool SupportsThickLines => _context?.Capabilities.WideLines ?? false;
    public bool SupportsSSBOs => true;

    public bool DebugMode { get; set; }

    /// <summary>
    /// Drains queued diagnostics, reporting only what the layers called an
    /// error.
    ///
    /// The client turns a non-null result into a thrown exception via
    /// CheckGlError, so this has to mean "something is actually wrong" - the
    /// GL call it stands in for, glGetError, never reported advice. Warnings are
    /// still dropped into the trace for anyone reading it.
    /// </summary>
    public string GetError()
    {
        if (_diagnostics.Count == 0) return null!;

        var errors = new List<string>();
        foreach (string diagnostic in _diagnostics)
        {
            if (diagnostic.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal))
            {
                errors.Add(diagnostic);
            }
        }
        _diagnostics.Clear();

        return errors.Count == 0 ? null! : string.Join("\n", errors);
    }

    // ---------------------------------------------------------------------- frame

    public void BeginFrame()
    {
        _frames.BeginFrame();
        _frameActive = true;
    }

    public void Present()
    {
        if (!_frameActive) return;

        CommandBuffer commandBuffer = _frames.Current.CommandBuffer;

        // Any open rendering scope has to close before the command buffer ends.
        _targets.EndRendering(commandBuffer);

        if (_swapchain == null)
        {
            // Headless: nothing to present, but the frame still has to be
            // submitted or the slot's fence would never signal.
            _frames.EndFrame();
            _frameActive = false;
            return;
        }

        if (!_swapchain.TryAcquire(out uint imageIndex,
                out Semaphore imageAvailable, out Semaphore renderFinished))
        {
            _frames.EndFrame();
            _frameActive = false;
            RecreateSwapchain();
            return;
        }

        BlitToSwapchain(commandBuffer, imageIndex);

        _frames.EndFrame(imageAvailable, renderFinished);
        _frameActive = false;

        _swapchain.Present(imageIndex, renderFinished);
        if (_swapchain.NeedsRecreation) RecreateSwapchain();
    }

    /// <summary>
    /// Copies the rendered frame into the acquired swapchain image, flipped.
    ///
    /// This inverted blit is the entire Y-flip story for the backend. Everything
    /// upstream stays in OpenGL's orientation, which is what keeps intermediate
    /// targets and screenshots byte-identical to the GL path; the display wants
    /// row 0 at the top, so the source rows are read bottom-to-top exactly once,
    /// here.
    /// </summary>
    private void BlitToSwapchain(CommandBuffer commandBuffer, uint imageIndex)
    {
        VulkanTexture? source = _textures.Get(_defaultColor);
        if (source == null || _swapchain == null) return;

        Vk api = _context.Api;
        Image destination = _swapchain.ImageAt(imageIndex);

        _textures.TransitionTexture(commandBuffer, source, ImageLayout.TransferSrcOptimal);
        TransitionSwapchainImage(commandBuffer, destination,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        // Source Y runs backwards: this is the flip.
        blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
        blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
        blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.DstOffsets.Element1 = new Offset3D((int)_swapchain.Extent.Width, (int)_swapchain.Extent.Height, 1);

        api.CmdBlitImage(commandBuffer,
            source.Image, ImageLayout.TransferSrcOptimal,
            destination, ImageLayout.TransferDstOptimal,
            1, &blit, Filter.Linear);

        TransitionSwapchainImage(commandBuffer, destination,
            ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr);
    }

    private void TransitionSwapchainImage(
        CommandBuffer commandBuffer, Image image, ImageLayout from, ImageLayout to)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = from,
            NewLayout = to,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecreateSwapchain()
    {
        if (_swapchain == null || _windowWidth == 0 || _windowHeight == 0) return;

        if (!_swapchain.Recreate(_windowWidth, _windowHeight, _vsync, out string? failureReason))
        {
            _diagnostics.Add("swapchain recreation failed: " + failureReason);
        }
    }

    public void Resize(int width, int height)
    {
        if (_swapchain == null || width <= 0 || height <= 0) return;
        if ((uint)width == _windowWidth && (uint)height == _windowHeight) return;

        _context.Api.DeviceWaitIdle(_context.Device);

        DestroyDefaultFramebuffer();
        CreateDefaultFramebuffer((uint)width, (uint)height);
        RecreateSwapchain();
    }

    public void SetVSync(bool enabled)
    {
        if (_vsync == enabled) return;
        _vsync = enabled;
        RecreateSwapchain();
    }

    private CommandBuffer Commands => _frames.Current.CommandBuffer;

    // ------------------------------------------------------------------ raw state

    public void SetViewport(int x, int y, int width, int height) => _state.SetViewport(x, y, width, height);
    public void SetScissor(int x, int y, int width, int height) => _state.SetScissor(x, y, width, height);
    public void SetScissorEnabled(bool enabled) => _state.SetScissorEnabled(enabled);
    public bool ScissorEnabled => _state.ScissorEnabled;

    public void SetDepthTest(bool enabled) => _state.SetDepthTest(enabled);
    public void SetDepthMask(bool enabled) => _state.SetDepthWrite(enabled);
    public void SetDepthFunc(int func) => _state.SetDepthFunc(func);

    public void SetCullFace(bool enabled) => _state.SetCullEnabled(enabled);
    public void SetCullFaceMode(bool back) => _state.SetCullBack(back);

    public void SetBlend(bool enabled, EnumBlendMode mode) => _state.SetBlend(enabled, mode);

    public void SetBlendFuncSeparate(int attachment, int srcColor, int dstColor, int srcAlpha, int dstAlpha) =>
        _state.SetAttachmentBlendFunc(attachment, srcColor, dstColor, srcAlpha, dstAlpha);

    public void SetBlendEquation(int attachment, int mode) =>
        _state.SetAttachmentBlendEquation(attachment, mode);

    public void SetColorMask(bool r, bool g, bool b, bool a) => _state.SetColorMask(r, g, b, a);

    public void SetStencilTest(bool enabled) => _state.SetStencilTest(enabled);
    public void SetStencilMask(int mask) => _state.SetStencilMask(mask);
    public void SetStencilFunc(int func, int refValue, int mask) => _state.SetStencilFunc(func, refValue, mask);
    public void SetStencilOp(int sfail, int dpfail, int dppass) => _state.SetStencilOp(sfail, dpfail, dppass);

    public void SetWireframe(bool enabled) => _state.SetWireframe(enabled);
    public void SetLineWidth(float width) => _state.SetLineWidth(width);

    // -------------------------------------------------------------------- shaders

    /// <summary>
    /// Stages a shader. No SPIR-V is produced here because GL resolves uniforms
    /// and varyings by name across the whole program, so nothing about a stage is
    /// final until its siblings are known.
    /// </summary>
    public bool CompileShader(IShader shader)
    {
        if (shader?.Code == null) return false;

        _stagedStages[shader] = new StagedStage
        {
            Stage = shader.Type,
            Code = shader.Code,
            PrefixCode = shader.PrefixCode ?? "",
            Filename = shader.Type.ToString(),
        };
        return true;
    }

    public int LinkProgram(IShaderProgram program)
    {
        var stages = new List<ShaderStageSource>();
        AddStage(stages, program.VertexShader, EnumShaderType.VertexShader, program.PassName);
        AddStage(stages, program.FragmentShader, EnumShaderType.FragmentShader, program.PassName);
        AddStage(stages, program.GeometryShader, EnumShaderType.GeometryShader, program.PassName);

        if (stages.Count == 0)
        {
            _diagnostics.Add($"shader program '{program.PassName}' has no stages");
            return 0;
        }

        TranslatedProgram translated = ShaderTranslator.Translate(stages, _shaderCompiler);
        if (!translated.Success)
        {
            foreach (string error in translated.Errors)
            {
                _diagnostics.Add($"{program.PassName}: {error}");
            }
            return 0;
        }

        int programId = _nextProgramId++;
        if (RenderTrace.Enabled)
        {
            RenderTrace.DumpProgramSources(program.PassName, translated);
            RenderTrace.Write("program " + programId + " '" + program.PassName + "' uniformBlockBytes=" +
                translated.Layout.BlockSize);
            foreach (UniformMember member in translated.Layout.Members)
            {
                RenderTrace.Write("  uniform " + member.Name + " offset=" + member.Offset +
                    " type=" + member.Type + " count=" + member.ArrayLength);
            }
        }
        _programs[programId] = new ShaderProgramResources(_context, programId, translated);
        return programId;
    }

    private void AddStage(List<ShaderStageSource> stages, IShader? shader, EnumShaderType stage, string passName)
    {
        if (shader == null || !_stagedStages.TryGetValue(shader, out StagedStage? staged)) return;

        stages.Add(new ShaderStageSource
        {
            Stage = stage,
            Code = staged.Code,
            PrefixCode = staged.PrefixCode,
            Filename = passName + StageExtension(stage),
        });
    }

    private static string StageExtension(EnumShaderType stage) => stage switch
    {
        EnumShaderType.VertexShader => ".vsh",
        EnumShaderType.FragmentShader => ".fsh",
        _ => ".gsh",
    };

    public void DeleteProgram(int programId)
    {
        if (!_programs.Remove(programId, out ShaderProgramResources? program)) return;
        _frames.DeferDeletion(program);
    }

    public void UseProgram(int programId) => _state.SetProgram(programId);

    public int GetUniformLocation(int programId, string name) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? program.LocationOf(name) : -1;

    // ------------------------------------------------------------------- uniforms

    private void Write(int programId, int location, ReadOnlySpan<byte> data)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            program.SetUniform(location, data);
        }
    }

    public void SetUniform(int programId, int location, float value) =>
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(float)));

    public void SetUniform(int programId, int location, int value)
    {
        // Assigning a sampler its texture unit is an int write to its uniform
        // location in GL. Here the sampler is a descriptor binding, so the same
        // call has to reach the unit table instead of the uniform block.
        if (ShaderProgramResources.IsSamplerLocation(location))
        {
            if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
            {
                program.SetSamplerUnitByLocation(location, value);
            }
            return;
        }
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(int)));
    }

    public void SetUniform(int programId, int location, float x, float y)
    {
        float* values = stackalloc float[2] { x, y };
        Write(programId, location, new ReadOnlySpan<byte>(values, 2 * sizeof(float)));
    }

    public void SetUniform(int programId, int location, float x, float y, float z)
    {
        float* values = stackalloc float[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(float)));
    }

    public void SetUniform(int programId, int location, float x, float y, float z, float w)
    {
        float* values = stackalloc float[4] { x, y, z, w };
        Write(programId, location, new ReadOnlySpan<byte>(values, 4 * sizeof(float)));
    }

    // The array setters are a straight memcpy because the generated block uses
    // scalar layout, where a float[] packs exactly as the shader expects. Under
    // std140 each of these would need re-striding on the way in.
    private void WriteArray(int programId, int location, int count, float[] values, int componentsPerElement)
    {
        int floats = Math.Min(values.Length, count * componentsPerElement);
        if (floats <= 0) return;

        fixed (float* source = values)
        {
            Write(programId, location, new ReadOnlySpan<byte>(source, floats * sizeof(float)));
        }
    }

    public void SetUniformArray1(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 1);

    public void SetUniformArray2(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 2);

    public void SetUniformArray3(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 3);

    public void SetUniformArray4(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 4);

    public void SetUniformMatrix(int programId, int location, float[] matrix) =>
        WriteArray(programId, location, 1, matrix, 16);

    public void SetUniformMatrices(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 16);

    public void SetUniformMatrices4x3(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 12);

    /// <summary>
    /// The samplers a linked program declares, in declaration order.
    ///
    /// Not part of the seam - the client never needs it, because it binds the
    /// samplers it knows by name. It exists so a test can bind every sampler a
    /// real program declares without hardcoding the list, since a draw whose
    /// descriptor set is incomplete is skipped rather than drawn.
    /// </summary>
    internal List<string> SamplerNamesOf(int programId)
    {
        var names = new List<string>();
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            foreach (SamplerBinding sampler in program.Interface.Samplers) names.Add(sampler.Name);
        }
        return names;
    }

    public void SetSamplerUnit(int programId, string samplerName, int unit)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            program.SamplerUnits[samplerName] = unit;
        }
    }

    // ------------------------------------------------------------ uniform buffers

    private readonly Dictionary<int, VulkanBuffer> _uniformBuffers = new();

    /// <summary>Block name each uniform buffer was created for.</summary>
    private readonly Dictionary<int, string> _uniformBufferBlocks = new();

    /// <summary>
    /// The buffer currently supplying each named block.
    ///
    /// The client's UBO binds with glBindBufferBase to binding point 0 and names
    /// the block when it creates the buffer, so the block name is what actually
    /// identifies which declaration a buffer feeds. Vulkan has no such global
    /// binding point, so the association is kept here and resolved per draw
    /// against the program's own declared blocks.
    /// </summary>
    private readonly Dictionary<string, int> _boundUniformBuffers = new(StringComparer.Ordinal);

    private int _nextUniformBufferId = 1;

    public int CreateUniformBuffer(int programId, int bindingPoint, string blockName, int size)
    {
        var buffer = new VulkanBuffer(_context, (ulong)Math.Max(size, 4),
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        int id = _nextUniformBufferId++;
        _uniformBuffers[id] = buffer;
        _uniformBufferBlocks[id] = blockName ?? "";

        // GL's glBindBufferBase in the client's constructor takes effect at once,
        // and a buffer is only ever created to be used.
        if (!string.IsNullOrEmpty(blockName)) _boundUniformBuffers[blockName] = id;
        return id;
    }

    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size)
    {
        if (!_uniformBuffers.TryGetValue(handle, out VulkanBuffer? buffer)) return;
        if (buffer.Mapped == IntPtr.Zero || data == IntPtr.Zero) return;
        if ((ulong)(offset + size) > buffer.Size) return;

        System.Buffer.MemoryCopy((void*)data, (void*)(buffer.Mapped + offset), size, size);
    }

    public void BindUniformBuffer(int handle)
    {
        if (_uniformBufferBlocks.TryGetValue(handle, out string? blockName) && blockName.Length > 0)
        {
            _boundUniformBuffers[blockName] = handle;
        }
    }

    /// <summary>
    /// Deliberately does not break the block association.
    ///
    /// The client's Unbind is glBindBuffer(UNIFORM_BUFFER, 0), which clears the
    /// generic target and leaves the glBindBufferBase index binding standing -
    /// and the index binding is what feeds the shader. Dropping the association
    /// here would unbind the block the client still expects to be supplied.
    /// </summary>
    public void UnbindUniformBuffer(int handle) { }

    public void DeleteUniformBuffer(int handle)
    {
        if (_uniformBufferBlocks.Remove(handle, out string? blockName) &&
            _boundUniformBuffers.TryGetValue(blockName, out int bound) && bound == handle)
        {
            _boundUniformBuffers.Remove(blockName);
        }

        if (_uniformBuffers.Remove(handle, out VulkanBuffer? buffer))
        {
            _frames.DeferDeletion(buffer);
        }
    }

    // -------------------------------------------------------------------- textures

    public int CreateTexture2D(
        int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps)
    {
        int id = _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), generateMipmaps: generateMipmaps);

        if (pixels != IntPtr.Zero)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, BytesPerPixel(internalFormat));
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        return id;
    }

    public int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel,
        bool generateMipmaps = false)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, generateMipmaps: generateMipmaps);

        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);
        }
        RenderTrace.TextureCreated(id, width, height, format, pixels, bytesPerPixel);
        return id;
    }

    public int CreateTextureCubeRaw(int size, int glInternalFormat, IntPtr[] facePixels, int bytesPerPixel)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], bytesPerPixel, face);
        }
        return id;
    }

    public int CreateTextureCube(
        int size, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr[] facePixels)
    {
        Format format = GlEnums.TextureFormatFrom(internalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], BytesPerPixel(internalFormat), face);
        }
        return id;
    }

    public int CreateTexture2DArray(
        int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat) =>
        _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), layers: (uint)layers);

    public void UploadTexture2D(
        int textureId, int level, int x, int y, int width, int height,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels) =>
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels,
            pixelFormat == EnumTexturePixelFormat.Red ? 1 : 4);

    public void GenerateMipmaps(int textureId) => _textures.GenerateMipmaps(textureId);

    public void DeleteTexture(int textureId) => _textures.Delete(textureId, _frames);

    public void SetTextureParameter(int textureId, int parameterName, int value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureParameter(int textureId, int parameterName, float value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public int GetTextureParameter(int textureId, int parameterName)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null) return 0;

        return parameterName == GlEnums.TextureCompareMode
            ? texture.State.CompareEnable ? GlEnums.TextureCompareRefToTexture : GlEnums.TextureCompareModeNone
            : 0;
    }

    public void BindTexture(int unit, int textureId)
    {
        if ((uint)unit >= GlStateTracker.MaxTextureUnits) return;
        _boundTextures[unit] = textureId;
    }

    public void BindTextureCube(int unit, int textureId) => BindTexture(unit, textureId);

    private readonly Dictionary<int, SamplerState> _standaloneSamplers = new();
    private int _nextSamplerId = 1;

    public int CreateSampler(bool linear)
    {
        int id = _nextSamplerId++;
        _standaloneSamplers[id] = SamplerState.Default with
        {
            MagFilter = linear ? Filter.Linear : Filter.Nearest,
            MinFilter = linear ? Filter.Linear : Filter.Nearest,
            MipmapMode = linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
        };
        return id;
    }

    public void SetSamplerParameter(int samplerId, int parameterName, float value)
    {
        if (!_standaloneSamplers.TryGetValue(samplerId, out SamplerState state)) return;

        _standaloneSamplers[samplerId] = parameterName == GlEnums.TextureLodBias
            ? state with { LodBias = value }
            : state;
    }

    public void BindSampler(int unit, int samplerId)
    {
        if ((uint)unit >= GlStateTracker.MaxTextureUnits) return;

        _unitSamplerOverrides[unit] = samplerId > 0 && _standaloneSamplers.TryGetValue(samplerId, out SamplerState state)
            ? _textures.Samplers.Get(state)
            : default;
    }

    public void DeleteSampler(int samplerId) => _standaloneSamplers.Remove(samplerId);

    private static int BytesPerPixel(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => 4,
        EnumTextureInternalFormat.Rgba16f => 8,
        EnumTextureInternalFormat.R16f => 2,
        EnumTextureInternalFormat.DepthComponent32 => 4,
        _ => 4,
    };

    // ---------------------------------------------------------------- framebuffers

    public int CreateFramebuffer(int width, int height) => _targets.Create((uint)width, (uint)height);

    public void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer)
    {
        int index = attachment == EnumFramebufferAttachment.DepthAttachment
            ? -1
            : (int)attachment - (int)EnumFramebufferAttachment.ColorAttachment0;

        _targets.Attach(framebufferId, index, textureId, (uint)layer);
    }

    public void SetDrawBuffers(int framebufferId, int attachmentMask) =>
        _targets.SetDrawBuffers(framebufferId, (uint)attachmentMask);

    public bool CheckFramebufferComplete(int framebufferId, out string status)
    {
        // Dynamic rendering has no framebuffer object to validate, so
        // completeness reduces to having a target with attachments.
        VulkanFramebuffer? framebuffer = _targets.Get(framebufferId);
        if (framebuffer == null)
        {
            status = "no such framebuffer";
            return false;
        }

        status = "complete";
        return true;
    }

    public void BindFramebuffer(int framebufferId)
    {
        if (_frameActive) _targets.Bind(Commands, framebufferId);
    }

    public void BindDefaultFramebuffer()
    {
        if (_frameActive) _targets.Bind(Commands, _defaultFramebuffer);
    }

    public void DeleteFramebuffer(int framebufferId) => _targets.Delete(framebufferId);

    public void ClearColor(int attachment, float r, float g, float b, float a)
    {
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearColor attachment=" + attachment + " target=" +
                (_targets.Bound?.Id ?? -1) + " rgba=" + r + "," + g + "," + b + "," + a);
        }
        if (_frameActive) _targets.ClearColor(Commands, attachment, r, g, b, a);
    }

    public void ClearDepth(float depth)
    {
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearDepth target=" + (_targets.Bound?.Id ?? -1) + " depth=" + depth);
        }
        if (_frameActive) _targets.ClearDepth(Commands, depth);
    }

    public void ClearStencil() { }

    // --------------------------------------------------------------------- meshes

    public int CreateMesh(MeshData data, bool staticDraw)
    {
        int vertices = data.VerticesCount;
        int id = _meshes.CreateEmpty(
            data.xyz != null ? vertices * 3 * sizeof(float) : 0,
            data.Normals != null ? vertices * sizeof(int) : 0,
            data.Uv != null ? vertices * 2 * sizeof(float) : 0,
            data.Rgba != null ? vertices * 4 : 0,
            data.Flags != null ? vertices * sizeof(int) : 0,
            data.IndicesCount * sizeof(int),
            data.CustomFloats, data.CustomShorts, data.CustomBytes, data.CustomInts,
            data.mode, staticDraw, ssbo: false);

        UpdateMesh(id, data);
        return id;
    }

    public int CreateEmptyMesh(
        int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts,
        CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo) =>
        _meshes.CreateEmpty(xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts, drawMode, staticDraw, ssbo);

    public void UpdateMesh(int meshId, MeshData data)
    {
        int vertices = data.VerticesCount;

        if (data.xyz != null)
        {
            fixed (float* source = data.xyz)
            {
                _meshes.Write(meshId, MeshManager.BufferXyz, 0, (IntPtr)source, vertices * 3 * sizeof(float));
            }
        }
        if (data.Uv != null)
        {
            fixed (float* source = data.Uv)
            {
                _meshes.Write(meshId, MeshManager.BufferUv, 0, (IntPtr)source, vertices * 2 * sizeof(float));
            }
        }
        if (data.Rgba != null)
        {
            fixed (byte* source = data.Rgba)
            {
                _meshes.Write(meshId, MeshManager.BufferRgba, 0, (IntPtr)source, vertices * 4);
            }
        }
        if (data.Flags != null)
        {
            fixed (int* source = data.Flags)
            {
                _meshes.Write(meshId, MeshManager.BufferFlags, 0, (IntPtr)source, vertices * sizeof(int));
            }
        }
        if (data.Normals != null)
        {
            fixed (int* source = data.Normals)
            {
                _meshes.Write(meshId, MeshManager.BufferNormals, 0, (IntPtr)source, vertices * sizeof(int));
            }
        }
        if (data.Indices != null)
        {
            fixed (int* source = data.Indices)
            {
                _meshes.Write(meshId, -1, 0, (IntPtr)source, data.IndicesCount * sizeof(int));
            }
        }
    }

    /// <summary>
    /// The SSBO chunk path packs four vertices into one face record and stores
    /// them in the xyz slot, which CreateEmptyMesh gave StorageBufferBit usage
    /// and no vertex-attribute binding when ssbo was set. Writing it is a plain
    /// buffer write; the shader reads it through gl_VertexIndex.
    /// </summary>
    public void UpdateMeshStorageBuffer(int meshId, IntPtr data, int byteOffset, int byteSize) =>
        _meshes.Write(meshId, MeshManager.BufferXyz, byteOffset, data, byteSize);

    public IntPtr GetMappedPointer(int meshId, EnumMeshBufferPart part) => part switch
    {
        EnumMeshBufferPart.Xyz => _meshes.MappedPointer(meshId, MeshManager.BufferXyz),
        EnumMeshBufferPart.Normals => _meshes.MappedPointer(meshId, MeshManager.BufferNormals),
        EnumMeshBufferPart.Uv => _meshes.MappedPointer(meshId, MeshManager.BufferUv),
        EnumMeshBufferPart.Rgba => _meshes.MappedPointer(meshId, MeshManager.BufferRgba),
        EnumMeshBufferPart.Flags => _meshes.MappedPointer(meshId, MeshManager.BufferFlags),
        EnumMeshBufferPart.CustomFloats => _meshes.MappedPointer(meshId, MeshManager.BufferCustomFloat),
        EnumMeshBufferPart.CustomShorts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomShort),
        EnumMeshBufferPart.CustomInts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomInt),
        EnumMeshBufferPart.CustomBytes => _meshes.MappedPointer(meshId, MeshManager.BufferCustomByte),
        _ => _meshes.MappedPointer(meshId, -1),
    };

    public void DeleteMesh(int meshId) => _meshes.Delete(meshId, _frames);

    // ---------------------------------------------------------------------- draws

    public void DrawMesh(int meshId) => DrawMeshInstanced(meshId, 1);

    public void DrawMeshInstanced(int meshId, int instanceCount)
    {
        if (!PrepareDraw(_meshes.LayoutIdOf(meshId), out CommandBuffer commandBuffer)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("draw mesh=" + meshId + " program=" + _state.CurrentProgram +
                " indices=" + (_meshes.Get(meshId)?.IndexCount ?? -1) +
                " tex0=" + _boundTextures[0] +
                " target=" + (_targets.Bound?.Id ?? -1) +
                " depthTest=" + _state.DepthTest + " depthWrite=" + _state.DepthWrite +
                " depthFunc=" + _state.DepthCompare + " blend=" + _state.BlendFor(0).Enabled +
                " cull=" + _state.CullEnabled + "/" + _state.CullMode +
                " scissor=" + _state.ScissorEnabled +
                " viewport=" + _state.Viewport.Offset.X + "," + _state.Viewport.Offset.Y + " " +
                    _state.Viewport.Extent.Width + "x" + _state.Viewport.Extent.Height +
                " blendSrc=" + _state.BlendFor(0).SrcColor + " blendDst=" + _state.BlendFor(0).DstColor +
                " uniforms=" + _lastUniformAllocationOk);
        }
        _meshes.Draw(commandBuffer, meshId, instanceCount);
    }

    public void DrawMeshMulti(int meshId, int[] indicesStarts, int[] indicesSizes, int groupCount, bool ssbo)
    {
        if (!PrepareDraw(_meshes.LayoutIdOf(meshId), out CommandBuffer commandBuffer)) return;

        VulkanBuffer indirect = EnsureIndirectScratch(groupCount);
        _meshes.DrawMulti(commandBuffer, meshId, indicesStarts, indicesSizes, groupCount, indirect);
    }

    public void DrawFullscreenTriangle()
    {
        if (!PrepareDraw(MeshManager.EmptyLayoutId, out CommandBuffer commandBuffer)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("fullscreen program=" + _state.CurrentProgram +
                " tex0=" + _boundTextures[0] + " target=" + (_targets.Bound?.Id ?? -1));
        }
        _context.Api.CmdDraw(commandBuffer, 3, 1, 0, 0);
    }

    /// <summary>
    /// Resolves everything a draw needs: the rendering scope, the pipeline for
    /// the current state, the descriptor sets for the bound textures, the uniform
    /// upload, and the dynamic state. This is where the recorded GL state finally
    /// becomes Vulkan commands.
    /// </summary>
    private bool PrepareDraw(int vertexLayoutId, out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!_frameActive)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("draw skipped: no active frame");
            return false;
        }

        VulkanFramebuffer? target = _targets.Bound;
        if (target == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("draw skipped: no bound render target");
            return false;
        }

        if (!_programs.TryGetValue(_state.CurrentProgram, out ShaderProgramResources? program))
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("draw skipped: program " + _state.CurrentProgram + " not resident");
            }
            return false;
        }

        commandBuffer = Commands;

        // Before the scope opens, not after: a layout transition is illegal
        // inside one, so anything this draw samples has to be put right first.
        TransitionSampledTextures(commandBuffer, program);
        _targets.EnsureRendering(commandBuffer);

        int formatsId = _targets.FormatsIdOf(target);
        RenderTargetFormats formats = _state.TargetFormats(formatsId);
        int attachmentCount = _targets.EnabledAttachmentCount(target);

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = _state.BlendFor(i);

        // A mesh that no longer exists reports -1; falling back to the reserved
        // empty layout keeps the key valid rather than indexing past the interner.
        int layoutId = vertexLayoutId >= 0 ? vertexLayoutId : MeshManager.EmptyLayoutId;

        // GL supplies a constant for any attribute the mesh does not carry; this
        // is where that promise is kept. The pipeline key already names both the
        // program and the mesh layout, so the merged result is stable per entry.
        VertexLayoutDescription meshLayout = _meshes.LayoutOf(layoutId);
        VertexLayoutDescription vertexLayout = meshLayout.WithDefaultsFor(program.Interface.VertexInputs);
        if (RenderTrace.Enabled && !ReferenceEquals(meshLayout, vertexLayout))
        {
            RenderTrace.Write("  defaults added: mesh had " + meshLayout.Attributes.Length +
                " attributes, program declares " + program.Interface.VertexInputs.Count +
                ", merged " + vertexLayout.Attributes.Length);
        }

        Pipeline pipeline = _pipelines.Get(
            _state.BuildKey(layoutId, formatsId, attachmentCount),
            new GraphicsPipelineCache.PipelineRequest
            {
                Program = program,
                VertexLayout = vertexLayout,
                Targets = formats,
                Blend = blend,
                PolygonMode = _state.PolygonMode,
                Topology = _state.Topology,
            });

        Vk api = _context.Api;
        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

        // The mesh binds its own buffers from zero; the defaults sit above them
        // and are bound whenever the pipeline actually declares that binding.
        if (vertexLayout.Bindings.Length > 0 &&
            vertexLayout.Bindings[^1].Binding == VertexLayoutDescription.DefaultAttributeBinding &&
            _defaultAttributes != null)
        {
            Buffer defaults = _defaultAttributes.Handle;
            ulong offset = 0;
            api.CmdBindVertexBuffers(commandBuffer,
                VertexLayoutDescription.DefaultAttributeBinding, 1, &defaults, &offset);
        }

        BindDescriptors(commandBuffer, program);
        ApplyDynamicState(commandBuffer, target);
        return true;
    }

    /// <summary>
    /// Puts every texture this draw samples into the layout a shader read needs.
    ///
    /// GL has no notion of image layout: a texture uploaded a moment ago, or one
    /// an earlier pass rendered into, can be sampled straight away. Vulkan wants
    /// it in SHADER_READ_ONLY_OPTIMAL at the point the descriptor is accessed and
    /// rejects the draw otherwise, and a transition cannot be recorded inside a
    /// rendering scope - so a texture found in the wrong layout closes the scope,
    /// transitions, and the scope reopens around the draw.
    ///
    /// An attachment of the framebuffer being drawn into is skipped: it has to
    /// keep its attachment layout, EnsureRendering already transitions the ones
    /// left out of the draw, and sampling what you are writing is a feedback loop
    /// GL does not allow either.
    /// </summary>
    private void TransitionSampledTextures(CommandBuffer commandBuffer, ShaderProgramResources program)
    {
        if (program.Interface.Samplers.Count == 0) return;

        bool placeholderNeeded = false;

        for (int i = 0; i < program.Interface.Samplers.Count; i++)
        {
            SamplerBinding declared = program.Interface.Samplers[i];
            int unit = program.SamplerUnits.TryGetValue(declared.Name, out int mapped)
                ? mapped
                : declared.Binding;

            VulkanTexture? texture = (uint)unit < GlStateTracker.MaxTextureUnits
                ? _textures.Get(_boundTextures[unit])
                : null;

            if (texture == null)
            {
                // BindDescriptors will reach for the placeholder here, so that is
                // what this draw actually samples.
                placeholderNeeded = true;
                continue;
            }

            if (texture.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;
            if (_targets.IsAttachmentOfBound(_boundTextures[unit])) continue;

            _targets.EndRendering(commandBuffer);
            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.ShaderReadOnlyOptimal);
        }

        if (!placeholderNeeded) return;

        VulkanTexture? placeholder = _textures.Get(_placeholderTexture);
        if (placeholder == null || placeholder.Layout == ImageLayout.ShaderReadOnlyOptimal) return;

        _targets.EndRendering(commandBuffer);
        _textures.TransitionTexture(commandBuffer, placeholder, ImageLayout.ShaderReadOnlyOptimal);
    }

    private void BindDescriptors(CommandBuffer commandBuffer, ShaderProgramResources program)
    {
        Vk api = _context.Api;

        // Set 0: the generated uniform block, uploaded into this frame's ring and
        // reached through a dynamic offset so the set itself never changes, plus
        // one entry for every block the shader declared for itself.
        uint dynamicOffset = 0;
        bool hasGeneratedBlock = program.Interface.HasUniformBlock;

        if (hasGeneratedBlock || program.Interface.UniformBlocks.Count > 0)
        {
            var buffers = new List<BufferBindingValue>(1 + program.Interface.UniformBlocks.Count);

            if (hasGeneratedBlock)
            {
                _lastUniformAllocationOk =
                    _frames.Current.TryAllocateUniforms(program.UniformShadow.Length, out RingAllocation allocation);
                if (_lastUniformAllocationOk)
                {
                    fixed (byte* source = program.UniformShadow)
                    {
                        System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                            program.UniformShadow.Length, program.UniformShadow.Length);
                    }
                    dynamicOffset = allocation.Offset;
                    program.MarkUniformsClean();
                }

                buffers.Add(new BufferBindingValue(
                    ProgramInterfaceLayout.DefaultBlockBinding,
                    _frames.UniformBuffer, 0, (ulong)program.UniformShadow.Length));
            }

            // A block the shader declares is fed by whichever UBO the client
            // created under that name; one it has not created yet reads zeroes
            // rather than leaving the descriptor undefined.
            foreach (BlockBinding block in program.Interface.UniformBlocks)
            {
                VulkanBuffer? blockBuffer = null;
                if (_boundUniformBuffers.TryGetValue(block.BlockName, out int handle))
                {
                    _uniformBuffers.TryGetValue(handle, out blockBuffer);
                }
                blockBuffer ??= _placeholderUniforms;
                if (blockBuffer == null) continue;

                buffers.Add(new BufferBindingValue(
                    (uint)block.Binding, blockBuffer.Handle, 0, blockBuffer.Size));
            }

            var uniformContents = new DescriptorSetContents(
                program.ProgramId, ProgramInterfaceLayout.DefaultBlockSet,
                Array.Empty<SamplerBindingValue>(), buffers.ToArray());

            DescriptorSet uniformSet = _descriptors.Get(
                uniformContents, program.SetLayouts[ProgramInterfaceLayout.DefaultBlockSet]);

            uint offset = dynamicOffset;
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                ProgramInterfaceLayout.DefaultBlockSet, 1, &uniformSet,
                hasGeneratedBlock ? 1u : 0u, hasGeneratedBlock ? &offset : null);
        }

        // Set 1: one combined image sampler per declared sampler, resolved through
        // the unit each sampler uniform points at.
        if (program.Interface.Samplers.Count > 0)
        {
            var bindings = new SamplerBindingValue[program.Interface.Samplers.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                SamplerBinding declared = program.Interface.Samplers[i];
                int unit = program.SamplerUnits.TryGetValue(declared.Name, out int mapped)
                    ? mapped
                    : declared.Binding;

                ImageView view = default;
                Sampler sampler = default;

                if ((uint)unit < GlStateTracker.MaxTextureUnits)
                {
                    VulkanTexture? texture = _textures.Get(_boundTextures[unit]);
                    if (texture != null)
                    {
                        view = texture.View;
                        // A sampler bound to the unit overrides the texture's own
                        // state, which is what glBindSampler means.
                        sampler = _unitSamplerOverrides[unit].Handle != 0
                            ? _unitSamplerOverrides[unit]
                            : _textures.Samplers.Get(texture.State);
                    }
                }

                bindings[i] = new SamplerBindingValue((uint)declared.Binding, view, sampler);
            }

            // A sampler the client left unbound gets the placeholder rather than
            // an empty descriptor. Leaving the set unbound is not an option: the
            // shader statically uses set 1, and drawing without it is undefined
            // behaviour that costs the device rather than one texture. GL is
            // permissive here - sampling an unbound texture reads black and the
            // draw proceeds - so the placeholder is also the closer emulation.
            VulkanTexture? placeholder = _textures.Get(_placeholderTexture);
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].View.Handle != 0 && bindings[i].Sampler.Handle != 0) continue;
                if (placeholder == null) break;

                bindings[i] = new SamplerBindingValue(
                    bindings[i].Binding, placeholder.View, _textures.Samplers.Get(placeholder.State));
            }

            bool complete = true;
            foreach (SamplerBindingValue binding in bindings)
            {
                if (binding.View.Handle == 0 || binding.Sampler.Handle == 0) { complete = false; break; }
            }

            if (complete)
            {
                DescriptorSet samplerSet = _descriptors.Get(
                    new DescriptorSetContents(program.ProgramId, ProgramInterfaceLayout.SamplerSet,
                        bindings, Array.Empty<BufferBindingValue>()),
                    program.SetLayouts[ProgramInterfaceLayout.SamplerSet]);

                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                    ProgramInterfaceLayout.SamplerSet, 1, &samplerSet, 0, null);
            }
            else if (RenderTrace.Enabled)
            {
                RenderTrace.Write("draw with an incomplete sampler set on program " + program.ProgramId);
            }
        }
    }

    private void ApplyDynamicState(CommandBuffer commandBuffer, VulkanFramebuffer target)
    {
        Vk api = _context.Api;

        Rect2D viewport = _state.Viewport;
        var vulkanViewport = new Viewport(
            viewport.Offset.X, viewport.Offset.Y,
            viewport.Extent.Width, viewport.Extent.Height, 0f, 1f);
        api.CmdSetViewport(commandBuffer, 0, 1, &vulkanViewport);

        // GL leaves the whole target writable when the scissor test is off;
        // Vulkan always has a scissor, so "off" becomes the full target.
        Rect2D scissor = _state.ScissorEnabled
            ? _state.Scissor
            : new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height));
        api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        api.CmdSetCullMode(commandBuffer, _state.CullEnabled ? _state.CullMode : CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, GlStateTracker.FrontFace);
        api.CmdSetPrimitiveTopology(commandBuffer, _state.Topology);

        api.CmdSetDepthTestEnable(commandBuffer, _state.DepthTest);
        api.CmdSetDepthWriteEnable(commandBuffer, _state.DepthWrite);
        api.CmdSetDepthCompareOp(commandBuffer, _state.DepthCompare);

        api.CmdSetStencilTestEnable(commandBuffer, _state.StencilTest);
        api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
            _state.StencilFail, _state.StencilPass, _state.StencilDepthFail, _state.StencilCompare);
        api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, _state.StencilCompareMask);
        api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, _state.StencilWriteMask);
        api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, _state.StencilReference);

        api.CmdSetLineWidth(commandBuffer, _context.Capabilities.WideLines ? _state.LineWidth : 1.0f);
    }

    private VulkanBuffer? _indirectScratch;

    private VulkanBuffer EnsureIndirectScratch(int groupCount)
    {
        ulong needed = (ulong)Math.Max(groupCount, 1) * (ulong)sizeof(DrawIndexedIndirectCommand);
        if (_indirectScratch != null && _indirectScratch.Size >= needed) return _indirectScratch;

        if (_indirectScratch != null) _frames.DeferDeletion(_indirectScratch);

        _indirectScratch = new VulkanBuffer(_context, Math.Max(needed * 2, 4096),
            BufferUsageFlags.IndirectBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        return _indirectScratch;
    }

    // -------------------------------------------------------------------- queries

    private readonly Dictionary<int, QueryPool> _queries = new();
    private int _nextQueryId = 1;

    public int CreateOcclusionQuery()
    {
        var createInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Occlusion,
            QueryCount = 1,
        };
        _context.Api.CreateQueryPool(_context.Device, &createInfo, null, out QueryPool pool);

        int id = _nextQueryId++;
        _queries[id] = pool;
        return id;
    }

    public void BeginOcclusionQuery(int queryId)
    {
        if (!_frameActive || !_queries.TryGetValue(queryId, out QueryPool pool)) return;

        _context.Api.CmdResetQueryPool(Commands, pool, 0, 1);
        _context.Api.CmdBeginQuery(Commands, pool, 0, 0);
    }

    public void EndOcclusionQuery(int queryId)
    {
        if (_frameActive && _queries.TryGetValue(queryId, out QueryPool pool))
        {
            _context.Api.CmdEndQuery(Commands, pool, 0);
        }
    }

    public bool IsQueryResultAvailable(int queryId)
    {
        if (!_queries.TryGetValue(queryId, out QueryPool pool)) return false;

        ulong result = 0;
        Result status = _context.Api.GetQueryPoolResults(
            _context.Device, pool, 0, 1, sizeof(ulong), &result, sizeof(ulong), QueryResultFlags.Result64Bit);
        return status == Result.Success;
    }

    public int GetQueryResult(int queryId)
    {
        if (!_queries.TryGetValue(queryId, out QueryPool pool)) return 0;

        ulong result = 0;
        _context.Api.GetQueryPoolResults(
            _context.Device, pool, 0, 1, sizeof(ulong), &result, sizeof(ulong),
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        return (int)Math.Min(result, int.MaxValue);
    }

    public void DeleteQuery(int queryId)
    {
        if (_queries.Remove(queryId, out QueryPool pool))
        {
            _context.Api.DestroyQueryPool(_context.Device, pool, null);
        }
    }

    // ------------------------------------------------------------------- readback

    /// <summary>
    /// Reads back the bound target's first colour attachment as BGRA8, rows
    /// bottom-up.
    ///
    /// Bottom-up is not an accident: it is what <c>glReadPixels</c> produces, and
    /// the existing screenshot and AVI paths already expect it. Because the
    /// backend never flips Y, the image in memory is laid out exactly as GL laid
    /// it out, so those paths keep working untouched.
    /// </summary>
    public void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)
    {
        if (destination == IntPtr.Zero || width <= 0 || height <= 0) return;

        VulkanFramebuffer? target = _targets.Bound;
        if (target == null) return;

        VulkanTexture? texture = _textures.Get(target.Color[0].TextureId);
        if (texture == null) return;

        // Readback has to see finished work, so any recording frame is closed
        // out first rather than racing it.
        if (_frameActive) Present();
        _context.Api.DeviceWaitIdle(_context.Device);

        ulong bytes = (ulong)width * (ulong)height * 4;
        using var readback = new VulkanBuffer(_context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        ImageLayout restore = texture.Layout;
        _setupCommands.SubmitAndWait(commandBuffer =>
        {
            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(x, y, 0),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            _context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        System.Buffer.MemoryCopy((void*)readback.Mapped, (void*)destination, (long)bytes, (long)bytes);

        if (restore != ImageLayout.Undefined)
        {
            _setupCommands.SubmitAndWait(commandBuffer =>
                _textures.TransitionTexture(commandBuffer, texture, restore));
        }
    }

    // ------------------------------------------------------------------- teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_context != null)
        {
            _context.Api.DeviceWaitIdle(_context.Device);
        }

        foreach (ShaderProgramResources program in _programs.Values) program.Dispose();
        _programs.Clear();

        foreach (VulkanBuffer buffer in _uniformBuffers.Values) buffer.Dispose();
        _uniformBuffers.Clear();

        foreach (QueryPool pool in _queries.Values)
        {
            _context?.Api.DestroyQueryPool(_context.Device, pool, null);
        }
        _queries.Clear();

        _indirectScratch?.Dispose();
        _defaultAttributes?.Dispose();
        _placeholderUniforms?.Dispose();
        _swapchain?.Dispose();
        _shaderCompiler?.Dispose();
        _frames?.Dispose();
        _descriptors?.Dispose();
        _pipelines?.Dispose();
        _targets?.Dispose();
        _meshes?.Dispose();
        _textures?.Dispose();
        _setupCommands?.Dispose();
        _context?.Dispose();
    }
}
