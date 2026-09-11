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
    private UploadManager _uploads = null!;
    private GlStateTracker _state = null!;
    private TextureManager _textures = null!;
    private MeshManager _meshes = null!;
    private RenderTargetManager _targets = null!;
    private GraphicsPipelineCache _pipelines = null!;
    private DescriptorCache _descriptors = null!;

    /// <summary>How many descriptor sets the cache currently holds. For tests.</summary>
    internal int CachedDescriptorSets => _descriptors.Count;
    private FrameRing _frames = null!;
    private ShaderCompiler _shaderCompiler = null!;

    private readonly Dictionary<int, ShaderProgramResources> _programs = new();

    /// <summary>Pass names by program id, so a device-loss report can name the shader.</summary>
    private readonly Dictionary<int, string> _programNames = new();

    /// <summary>Scratch for the SSBO path's pruned custom ints, grown as needed.</summary>
    private int[] _prunedCustomInts = [];

    private uint _frameCounter;
    private uint _uniformExhaustionReportedFrame = uint.MaxValue;
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
    private int _placeholderArrayTexture;
    private int _placeholderCubeTexture;
    private int _placeholderDepthTexture;
    private VulkanBuffer? _placeholderUniforms;

    /// <summary>Texture bound to each unit, and any sampler overriding the texture's own state.</summary>
    private readonly int[] _boundTextures = new int[GlStateTracker.MaxTextureUnits];
    private readonly int[] _unitSamplerOverrides = new int[GlStateTracker.MaxTextureUnits];

    // Atlas composition reads one tile while writing another in the same image.
    // Reuse a snapshot image, but refresh its contents before each such draw.
    private readonly Dictionary<int, int> _feedbackCopies = new();
    private readonly Dictionary<int, int> _sampledTextureOverrides = new();

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
    /// Where a bare OPTIMUM_VULKAN_VALIDATION=1 mirrors the layer's messages.
    /// Before this default the messages only surfaced when the client happened
    /// to poll the error channel, and a whole class of hazards went unlogged.
    /// </summary>
    private static readonly string DefaultValidationLogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-vulkan-validation.log");

    /// <summary>
    /// Where validation messages are mirrored, when the variable names a path
    /// rather than just switching the layers on.
    ///
    /// The client's own error channel only surfaces them when it happens to call
    /// CheckGlError, and a device-lost kills the process before that; a file gets
    /// the message that preceded the loss.
    /// </summary>
    private static readonly string? ValidationLogPath =
        ResolveValidationLogPath(ValidationSetting, DefaultValidationLogPath);

    /// <summary>
    /// A setting that names a path is used as one; anything else (the bare "1")
    /// only switches the layers on and mirrors to <paramref name="fallback" />.
    /// Windows separators count as a path too, so "C:\logs\vulkan.log" is not
    /// silently redirected to the temp file.
    /// </summary>
    internal static string? ResolveValidationLogPath(string? setting, string fallback)
    {
        if (setting == null) return null;
        return setting.Contains('/') || setting.Contains('\\') ? setting : fallback;
    }

    /// <summary>
    /// OPTIMUM_VULKAN_VALIDATION_FEATURES: comma list of "sync" (synchronization
    /// validation), "best" (best practices, vendor checks included) and "gpu"
    /// (GPU-assisted). Requested through VK_EXT_validation_features so it does
    /// not depend on the layer's environment variable names, which changed.
    /// </summary>
    private static readonly string ValidationFeatureSetting =
        Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_VALIDATION_FEATURES") ?? "";

    /// <summary>
    /// The client logs diagnostics through string.Format, and a layer message
    /// that prints a struct ("pImageMemoryBarriers[0]: { ... }") throws a
    /// FormatException there and is lost. Braces become brackets before the
    /// message reaches either channel.
    /// </summary>
    private static string SanitiseForClientLog(string message) =>
        message.Replace('{', '[').Replace('}', ']');

    private static void MirrorValidationMessage(string message)
    {
        if (ValidationLogPath == null) return;
        try
        {
            System.IO.File.AppendAllText(ValidationLogPath, message + "\n");
        }
        catch (Exception error) when (
            error is System.IO.IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            // A diagnostic write must never take the device down: a read-only
            // directory or a malformed path is a lost log line, nothing more.
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
            ValidationFeatures = ValidationFeatureSetting,
            DebugCallback = message =>
            {
                _diagnostics.Add(SanitiseForClientLog(message));
                MirrorValidationMessage(message);
                if (RenderTrace.Enabled)
                    RenderTrace.Write("validation: program=" + (_state?.CurrentProgram ?? 0) +
                        " target=" + (_targets?.Bound?.Id ?? -1) + " " + message);
            },
            // Surface extensions have to be enabled at instance creation, before
            // any surface can exist, so the window system is asked first.
            RequiredInstanceExtensions = headless
                ? Array.Empty<string>()
                : WindowSurface.RequiredInstanceExtensions(),
        };

        ConfigureContextOptions?.Invoke(options);

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
        VulkanResult.DescribeDeviceLoss = DescribeDeviceLoss;
        MirrorValidationMessage("--- device up on " + _context.Capabilities.DeviceName +
            "; validation layers " + (_context.ValidationEnabled ? "ENABLED" : "NOT AVAILABLE") +
            "; GPU checkpoints " + (_context.CheckpointsAvailable ? "ENABLED" : "NOT AVAILABLE") +
            "; device fault reporting " + (_context.DeviceFaultAvailable ? "ENABLED" : "NOT AVAILABLE") +
            "; poison " + (_context.PoisonFreshResources ? "ON" : "off"));
        _state = new GlStateTracker();
        // Uploads never wait: they ride the next frame submission, recorded from
        // any thread into the ring's upload batch (or inline into the frame when
        // it already used the destination; see UploadManager).
        _frames = new FrameRing(_context);
        _uploads = _frames.Uploads;
        _textures = new TextureManager(_context, _uploads);
        _meshes = new MeshManager(_context, _state, _uploads);
        _targets = new RenderTargetManager(_context, _textures, _state);
        // An inline upload records transfer commands into the frame command
        // buffer, which no rendering scope may enclose.
        _uploads.CloseRenderingScope = commandBuffer => _targets.EndRendering(commandBuffer);
        _pipelines = new GraphicsPipelineCache(_context);
        _descriptors = new DescriptorCache(_context);
        _queryRing = new QueryRing(_context, _frames.Timeline, _frames.FramesInFlight);
        _readbacks = new ReadbackManager(_context, _textures, _frames);
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

            // A descriptor's view type has to match the sampler's dimensionality
            // - a 2D view in a sampler2DArray slot is invalid, not merely black -
            // so an arrayed and a cube placeholder stand in for those samplers.
            _placeholderArrayTexture = _textures.Create(1, 1, Format.R8G8B8A8Unorm, layers: 2);
            for (uint layer = 0; layer < 2; layer++)
            {
                _textures.Upload(_placeholderArrayTexture, 0, 0, 0, 1, 1, (IntPtr)pixels, 4, layer);
            }

            _placeholderCubeTexture = _textures.Create(1, 1, Format.R8G8B8A8Unorm, layers: 6, cube: true);
            for (uint face = 0; face < 6; face++)
            {
                _textures.Upload(_placeholderCubeTexture, 0, 0, 0, 1, 1, (IntPtr)pixels, 4, face);
            }
        }

        // A shadow sampler compares against depth, so its placeholder is a depth
        // texel at the far plane: every comparison passes and nothing is shadowed,
        // which is what a missing shadow map looks like on GL. The state enables
        // comparison so the sampler object matches the sampler declaration too.
        float far = 1f;
        _placeholderDepthTexture = _textures.Create(1, 1, Format.D32Sfloat);
        _textures.Upload(_placeholderDepthTexture, 0, 0, 0, 1, 1, (IntPtr)(&far), 4);
        VulkanTexture? depthPlaceholder = _textures.Get(_placeholderDepthTexture);
        if (depthPlaceholder != null)
        {
            depthPlaceholder.State = depthPlaceholder.State with { CompareEnable = true };
        }
    }

    /// <summary>
    /// The placeholder that fits a sampler's declaration: a shadow sampler
    /// compares against depth and needs a depth format, the others need the
    /// matching view type. An arrayed shadow sampler gets the 2D depth
    /// placeholder, which the trace will show should the game ever declare one.
    /// </summary>
    private int PlaceholderFor(string samplerType) =>
        samplerType.Contains("Shadow", StringComparison.Ordinal) ? _placeholderDepthTexture
        : samplerType.Contains("Cube", StringComparison.Ordinal) ? _placeholderCubeTexture
        : samplerType.Contains("Array", StringComparison.Ordinal) ? _placeholderArrayTexture
        : _placeholderTexture;

    /// <summary>
    /// Whether a texture can legally sit behind a sampler of the given type. A
    /// shadow sampler on a colour texture is the case that matters: GL leaves the
    /// comparison undefined, Vulkan rejects the descriptor, and the game reaches
    /// it whenever a shadow map slot exists without a shadow map behind it.
    /// </summary>
    private static bool TextureSuitsSampler(VulkanTexture texture, string samplerType)
    {
        if (samplerType.Contains("Shadow", StringComparison.Ordinal)
            && !TextureManager.IsDepthFormat(texture.Format))
        {
            return false;
        }

        // The view type has to match the sampler's dimensionality, which GL
        // enforces through its texture targets: a 2D texture cannot be bound
        // where a sampler2DArray reads, nor an array where a sampler2D does.
        bool wantsCube = samplerType.Contains("Cube", StringComparison.Ordinal);
        bool wantsArray = samplerType.Contains("Array", StringComparison.Ordinal);
        if (wantsCube) return texture.Cube;
        if (wantsArray) return texture.Layers > 1 && !texture.Cube;
        return texture.Layers == 1 && !texture.Cube;
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
        if (_defaultColor > 0) ReleaseTexture(_defaultColor);
        if (_defaultDepth > 0) ReleaseTexture(_defaultDepth);

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
    /// Test seam: adjusts the context options <see cref="Initialize" /> builds,
    /// just before the context is created (validation features, a message
    /// recorder, poison mode). Null in the client.
    /// </summary>
    internal Action<VulkanContextOptions>? ConfigureContextOptions { get; set; }

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
        // CPU frame interval: start of one frame to the start of the next, so it
        // includes the Frame timeline pacing wait below and everything the client did.
        long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastFrameStart != 0)
        {
            VulkanStats.NoteFrameInterval(
                (frameStart - _lastFrameStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        _lastFrameStart = frameStart;

        FrameSlot slot = _frames.BeginFrame();
        _frameActive = true;
        _frameCounter++;
        _indirectFrameUsage = 0;
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));

        // The slot's previous frame has finished: its query results move to the
        // host buffer before the pools reset, and its readback arena is free again.
        _queryRing.BeginSlot(slot.Index, slot.CommandBuffer);
        _readbacks.BeginSlot(slot.Index);

        // Sets naming resources deleted since last frame leave the cache now and
        // are freed once the Frame timeline has passed every frame that could
        // have bound them.
        IDisposable? freedSets = _descriptors.CollectReleases();
        if (freedSets != null) _frames.DeferDeletion(freedSets);

        VulkanStats.NoteFrame();
        if (StatsLogPath != null &&
            VulkanStats.SampleIfDue(TimeSpan.FromSeconds(1)) is { } sample)
        {
            try
            {
                // One sample is several lines (see VulkanStats); the first keeps the original format.
                System.IO.File.AppendAllText(StatsLogPath, sample + "\n");
            }
            catch (System.IO.IOException)
            {
            }
        }
    }

    /// <summary>Stopwatch timestamp of the last BeginFrame, 0 before the first.</summary>
    private long _lastFrameStart;

    /// <summary>Deferred destructions still waiting on the timelines. Tests only.</summary>
    internal int PendingRetirementsForTests => _frames.PendingDeletionCount;

    /// <summary>The frame ring's timelines. Tests only.</summary>
    internal FrameTimeline TimelineForTests => _frames.Timeline;

    /// <summary>The frame ring's upload manager. Tests only.</summary>
    internal UploadManager UploadsForTests => _uploads;

    /// <summary>Static meshes on device-local memory through staging (Phase 1B step 5's default). Tests only.</summary>
    internal bool DeviceLocalStaticMeshesForTests
    {
        set => _meshes.DeviceLocalStaticBuffers = value;
    }

    /// <summary>Where per-second backend counters go, when asked for.</summary>
    private static readonly string? StatsLogPath = Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_STATS");

    /// <summary>
    /// Leaves a marker the driver reports back if the GPU stops. Free when the
    /// extension is absent; one small command otherwise.
    /// </summary>
    private void Checkpoint(CommandBuffer commandBuffer, nint marker)
    {
        if (_context.CheckpointsAvailable) _context.CmdSetCheckpoint(commandBuffer, marker);
    }

    /// <summary>
    /// What the GPU was doing when it was lost, from the driver's checkpoint and
    /// fault records. VulkanResult.Check calls this on the first loss.
    ///
    /// Reading checkpoints wants the queue synchronised like any other queue
    /// call, but the thread that noticed the loss may already hold the lock, or
    /// another may be inside a submit that is about to fail. A bounded wait keeps
    /// the crash report from deadlocking behind the crash it is describing.
    /// </summary>
    private string? DescribeDeviceLoss()
    {
        if (_context == null) return null;

        var text = new System.Text.StringBuilder();

        if (_context.CheckpointsAvailable)
        {
            bool locked = System.Threading.Monitor.TryEnter(_context.QueueLock, 2000);
            try
            {
                List<(PipelineStageFlags Stage, nint Marker)> checkpoints = _context.ReadQueueCheckpoints();
                if (checkpoints.Count == 0)
                {
                    text.Append("The driver recorded no GPU checkpoints.");
                }
                else
                {
                    text.Append("Last GPU checkpoint per stage -");
                    foreach ((PipelineStageFlags stage, nint marker) in checkpoints)
                    {
                        text.Append(' ').Append(StageName(stage)).Append(": ")
                            .Append(CheckpointMarker.Describe(marker, ProgramNameOf)).Append(';');
                    }
                }
            }
            finally
            {
                if (locked) System.Threading.Monitor.Exit(_context.QueueLock);
            }
        }
        else
        {
            text.Append("GPU checkpoints are not available on this driver.");
        }

        string? fault = _context.DeviceFaultAvailable ? _context.ReadDeviceFault() : null;
        if (fault != null) text.Append(' ').Append(fault).Append('.');

        return text.ToString();
    }

    private string? ProgramNameOf(int programId) =>
        _programNames.TryGetValue(programId, out string? name) ? name : null;

    private static string StageName(PipelineStageFlags stage) => stage switch
    {
        PipelineStageFlags.TopOfPipeBit => "last started",
        PipelineStageFlags.BottomOfPipeBit => "last completed",
        _ => stage.ToString(),
    };

    public void Present()
    {
        if (!_frameActive) return;

        TextureDump.NoteFrame();
        if (TextureDump.Wanted) DumpRequestedTextures();

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

        Checkpoint(commandBuffer, CheckpointMarker.PresentBlit(imageIndex, _frameCounter));
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
            SrcAccessMask = TextureManager.AccessForLayout(from, writer: true),
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = TextureManager.AccessForLayout(to, writer: false),
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
        VulkanStats.NoteImageBarriers(1);
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

        VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);

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

    public void SetBlendEnabled(bool enabled) => _state.SetBlendEnabled(enabled);

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
    /// <summary>
    /// Largest shader source accepted per stage. Vanilla's biggest stage is
    /// well under 100 KiB; the cap keeps a broken or hostile mod shader from
    /// handing the native compiler an unbounded input.
    /// </summary>
    internal const int MaxShaderSourceBytes = 2 * 1024 * 1024;

    public bool CompileShader(IShader shader)
    {
        if (shader?.Code == null) return false;

        string stageName = shader.Type.ToString();
        if (shader.Code.Length + (shader.PrefixCode?.Length ?? 0) > MaxShaderSourceBytes)
        {
            _diagnostics.Add($"{stageName}: shader source exceeds {MaxShaderSourceBytes} bytes and was rejected");
            return false;
        }
        if (shader.Code.IndexOf('\0') >= 0 || (shader.PrefixCode?.IndexOf('\0') ?? -1) >= 0)
        {
            _diagnostics.Add($"{stageName}: shader source contains a NUL byte and was rejected");
            return false;
        }

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
        _programNames[programId] = program.PassName ?? "";
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
        _programNames.Remove(programId);
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

    public void SetUniform(int programId, int location, int x, int y, int z)
    {
        // Scalar block layout stores an ivec3 as three consecutive 32-bit ints.
        int* values = stackalloc int[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(int)));
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

    /// <summary>
    /// One uniform buffer object the client created for a named block.
    ///
    /// The CPU shadow is the source of truth, not the GPU buffer. The client
    /// updates one UBO per block and re-updates it between draws - the entity
    /// renderer uploads the "Animation" block once per entity, immediately before
    /// that entity's draw - but a draw is only recorded here, not executed, so a
    /// buffer written in place would give every entity in the frame the last
    /// entity's transforms. Writes therefore land in ordinary memory and a draw
    /// snapshots them into the frame's uniform ring, exactly as the generated
    /// block does.
    ///
    /// There is deliberately no GPU buffer per block: the snapshot goes in the
    /// ring, and the ring-exhausted path allocates its own transient copy for
    /// that one draw, so a persistent buffer would only ever sit unbound.
    /// </summary>
    private sealed class ClientUniformBuffer
    {
        public ClientUniformBuffer(byte[] shadow, string blockName)
        {
            Shadow = shadow;
            BlockName = blockName;
        }

        public byte[] Shadow { get; }
        public string BlockName { get; }

        /// <summary>Bumped by every write, so an unchanged block reuses its snapshot.</summary>
        public uint Version { get; private set; } = 1;

        /// <summary>Which frame's ring the snapshot below lives in, and what it holds.</summary>
        public uint SnapshotFrame { get; private set; }
        public uint SnapshotVersion { get; private set; }
        public uint SnapshotOffset { get; private set; }

        public void Write(IntPtr data, int offset, int size)
        {
            // A client that re-uploads identical bytes before every draw would
            // otherwise cost a fresh ring slice per draw; comparing is cheaper.
            var incoming = new ReadOnlySpan<byte>((void*)data, size);
            Span<byte> target = Shadow.AsSpan(offset, size);
            if (incoming.SequenceEqual(target)) return;
            incoming.CopyTo(target);
            Version++;
        }

        public void NoteSnapshot(uint frame, uint offset)
        {
            SnapshotFrame = frame;
            SnapshotVersion = Version;
            SnapshotOffset = offset;
        }

        public bool HasSnapshotFor(uint frame) => SnapshotFrame == frame && SnapshotVersion == Version;
    }

    private readonly Dictionary<int, ClientUniformBuffer> _uniformBuffers = new();

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
        int bytes = Math.Max(size, 4);
        int id = _nextUniformBufferId++;
        _uniformBuffers[id] = new ClientUniformBuffer(new byte[bytes], blockName ?? "");

        // GL's glBindBufferBase in the client's constructor takes effect at once,
        // and a buffer is only ever created to be used.
        if (!string.IsNullOrEmpty(blockName)) _boundUniformBuffers[blockName] = id;
        return id;
    }

    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size)
    {
        if (!_uniformBuffers.TryGetValue(handle, out ClientUniformBuffer? ubo)) return;
        if (data == IntPtr.Zero || offset < 0 || size < 0) return;
        if ((long)offset + size > ubo.Shadow.Length) return;

        ubo.Write(data, offset, size);
    }

    public void BindUniformBuffer(int handle)
    {
        if (_uniformBuffers.TryGetValue(handle, out ClientUniformBuffer? ubo) && ubo.BlockName.Length > 0)
        {
            _boundUniformBuffers[ubo.BlockName] = handle;
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
        if (!_uniformBuffers.Remove(handle, out ClientUniformBuffer? ubo)) return;

        if (ubo.BlockName.Length > 0 &&
            _boundUniformBuffers.TryGetValue(ubo.BlockName, out int bound) && bound == handle)
        {
            _boundUniformBuffers.Remove(ubo.BlockName);
        }
    }

    // -------------------------------------------------------------------- textures

    public int CreateTexture2D(
        int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps)
    {
        int id = _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, (int)internalFormat);

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
        RecordGlInternalFormat(id, glInternalFormat);

        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);

            // A chain that was asked for has to be filled here. GL's texture is
            // complete the moment glGenerateMipmap runs, but an image created
            // with levels and never blitted into keeps whatever its memory held,
            // and every sample above level 0 reads that - which looks like other
            // textures bleeding onto a surface as it turns away from the camera.
            if (generateMipmaps) _textures.GenerateMipmaps(id);
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

    public void UploadTexture2DRaw(
        int textureId, int level, int x, int y, int width, int height, IntPtr pixels, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0) return;
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels, bytesPerPixel);
    }

    public void GenerateMipmaps(int textureId) => _textures.GenerateMipmaps(textureId);

    public void DeleteTexture(int textureId) => ReleaseTexture(textureId);

    /// <summary>
    /// Deletes a texture and evicts every descriptor set that names it.
    ///
    /// The eviction is the important half. The texture itself is destroyed once
    /// the Frame timeline passed every frame that could name it, but a cached set would outlive it and, once the driver
    /// reused the view handle for a new texture, be served to draws of that new
    /// texture - which is a GPU read of freed memory. The GUI re-renders its text
    /// into fresh textures constantly, so this was the loading-screen crash.
    /// </summary>
    private void ReleaseTexture(int textureId)
    {
        if (_feedbackCopies.Remove(textureId, out int copy)) ReleaseTexture(copy);
        _sampledTextureOverrides.Remove(textureId);
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _descriptors.Release(texture.Id);
        _textures.Delete(textureId, _frames);
        // Only a delete that found something is a delete. Deleting an id twice
        // (framebuffers share a depth texture) otherwise inflated the counter
        // past the number of textures that ever existed.
        if (texture != null) VulkanStats.NoteTextureDeleted();
    }

    public void SetTextureParameter(int textureId, int parameterName, int value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureParameter(int textureId, int parameterName, float value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureBorderColor(int textureId, float r, float g, float b, float a) =>
        _textures.SetBorderColor(textureId, r, g, b, a);

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
        if (RenderTrace.Enabled) RenderTrace.Write("bind unit=" + unit + " texture=" + textureId);
    }

    public void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y,
        int width, int height, IntPtr pixels) =>
        _textures.Upload(textureId, 0, x, y, (uint)width, (uint)height, pixels, 4, (uint)layer);

    public void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y,
        int width, int height, short[] pixels) =>
        _textures.UploadNormalizedShorts(textureId, level, x, y, width, height, pixels);

    public void BindTextureCube(int unit, int textureId) => BindTexture(unit, textureId);

    private readonly Dictionary<int, SamplerState> _standaloneSamplers = new();
    private int _nextSamplerId = 1;

    public int CreateSampler(bool linear)
    {
        int id = _nextSamplerId++;
        _standaloneSamplers[id] = SamplerState.Default with
        {
            MagFilter = linear ? Filter.Linear : Filter.Nearest,
            // GenSampler uses GL_NEAREST_MIPMAP_LINEAR for both variants;
            // the flag changes magnification only. Terrain relies on this
            // override retaining the atlas mip chain at a distance.
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Linear,
            Mipmapped = true,
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

        _unitSamplerOverrides[unit] = _standaloneSamplers.ContainsKey(samplerId) ? samplerId : 0;
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
        // Sized as GL's UploadMesh sizes them. Every part follows the vertex
        // count except flags, which GL allocates at the array's full length -
        // a mesh that later grows within that capacity updates its flags in
        // place there, and would overflow a vertex-count-sized buffer here.
        int vertices = data.VerticesCount;
        int id = _meshes.CreateEmpty(
            data.xyz != null ? vertices * 3 * sizeof(float) : 0,
            data.Normals != null ? vertices * sizeof(int) : 0,
            data.Uv != null ? vertices * 2 * sizeof(float) : 0,
            data.Rgba != null ? vertices * 4 : 0,
            data.Flags != null ? data.Flags.Length * sizeof(int) : 0,
            data.IndicesCount * sizeof(int),
            data.CustomFloats, data.CustomShorts, data.CustomBytes, data.CustomInts,
            data.mode, staticDraw, ssbo: false, signedCustomShorts: true);

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

    /// <summary>
    /// Writes a mesh's data, honouring the destination offset each part carries.
    ///
    /// Those offsets are the whole point. The game pools chunk meshes: one large
    /// mesh holds many chunks, and each chunk is handed the same mesh with the
    /// byte offset of its own slice in every part. GL's updateVAO takes that
    /// offset as the destination for a glBufferSubData, so writing it at zero
    /// instead stacks every chunk in the world on top of the first one - which
    /// renders as no terrain at all.
    ///
    /// The counts are per part as well, not VerticesCount: a part can be absent
    /// or shorter than the vertex count, and the custom buffers have no fixed
    /// relationship to it.
    /// </summary>
    public void UpdateMesh(int meshId, MeshData data)
    {
        // An SSBO mesh's xyz slot holds packed face records, written through
        // UpdateMeshStorageBuffer; positions never belong there. The game hands
        // the same MeshData to both calls, so without this the positions would
        // land on top of the records - or, depending on order, under them.
        bool ssbo = _meshes.IsSsbo(meshId);

        if (data.xyz != null && data.XyzCount > 0 && !ssbo)
        {
            fixed (float* source = data.xyz)
            {
                _meshes.Write(meshId, MeshManager.BufferXyz, data.XyzOffset,
                    (IntPtr)source, data.XyzCount * sizeof(float));
            }
        }
        // The normals, uv and flags streams have no buffer on an SSBO mesh - the
        // face records carry what the shader needs from them - so GL's SSBO
        // update path never writes them either.
        if (data.Normals != null && data.VerticesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Normals)
            {
                _meshes.Write(meshId, MeshManager.BufferNormals, data.NormalsOffset,
                    (IntPtr)source, data.VerticesCount * sizeof(int));
            }
        }
        if (data.Uv != null && data.UvCount > 0 && !ssbo)
        {
            fixed (float* source = data.Uv)
            {
                _meshes.Write(meshId, MeshManager.BufferUv, data.UvOffset,
                    (IntPtr)source, data.UvCount * sizeof(float));
            }
        }
        if (data.Rgba != null && data.RgbaCount > 0)
        {
            fixed (byte* source = data.Rgba)
            {
                _meshes.Write(meshId, MeshManager.BufferRgba, data.RgbaOffset,
                    (IntPtr)source, data.RgbaCount);
            }
        }
        if (data.Flags != null && data.FlagsCount > 0 && !ssbo)
        {
            fixed (int* source = data.Flags)
            {
                _meshes.Write(meshId, MeshManager.BufferFlags, data.FlagsOffset,
                    (IntPtr)source, data.FlagsCount * sizeof(int));
            }
        }
        if (data.CustomFloats != null && data.CustomFloats.Count > 0)
        {
            fixed (float* source = data.CustomFloats.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomFloat, data.CustomFloats.BaseOffset,
                    (IntPtr)source, data.CustomFloats.Count * sizeof(float));
            }
        }
        if (data.CustomShorts != null && data.CustomShorts.Count > 0)
        {
            fixed (short* source = data.CustomShorts.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomShort, data.CustomShorts.BaseOffset,
                    (IntPtr)source, data.CustomShorts.Count * sizeof(short));
            }
        }
        if (data.CustomInts != null && data.CustomInts.Count > 0)
        {
            if (ssbo)
            {
                WritePrunedCustomInts(meshId, data.CustomInts);
            }
            else
            {
                fixed (int* source = data.CustomInts.Values)
                {
                    _meshes.Write(meshId, MeshManager.BufferCustomInt, data.CustomInts.BaseOffset,
                        (IntPtr)source, data.CustomInts.Count * sizeof(int));
                }
            }
        }
        if (data.CustomBytes != null && data.CustomBytes.Count > 0)
        {
            fixed (byte* source = data.CustomBytes.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomByte, data.CustomBytes.BaseOffset,
                    (IntPtr)source, data.CustomBytes.Count);
            }
        }
        // An SSBO mesh never takes indices from the data: GL draws every such
        // mesh through one shared index buffer holding the fixed quad pattern,
        // filled once at allocation, and its update path leaves indices alone.
        // The mesh here got the same pattern when it was created.
        if (data.Indices != null && data.IndicesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Indices)
            {
                _meshes.Write(meshId, -1, data.IndicesOffset,
                    (IntPtr)source, data.IndicesCount * sizeof(int));
            }
        }
    }

    /// <summary>
    /// Writes the custom ints as the SSBO path stores them: two per vertex go in
    /// and only the second of each pair is kept, the first being the colormap
    /// data that the face record already carries. The destination offset halves
    /// with the stride. A part with a single int per vertex is not bound at all
    /// on this path, so there is nothing to write.
    /// </summary>
    private void WritePrunedCustomInts(int meshId, CustomMeshDataPartInt customInts)
    {
        if (customInts.InterleaveStride <= 4) return;

        int kept = customInts.Count / 2;
        if (kept <= 0) return;

        if (_prunedCustomInts.Length < kept) _prunedCustomInts = new int[kept];

        int[] values = customInts.Values;
        for (int i = 0; i < kept; i++) _prunedCustomInts[i] = values[i * 2 + 1];

        fixed (int* source = _prunedCustomInts)
        {
            _meshes.Write(meshId, MeshManager.BufferCustomInt, customInts.BaseOffset / 2,
                (IntPtr)source, kept * sizeof(int));
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
        if (instanceCount <= 0) return;
        if (!PrepareDraw(_meshes.LayoutIdOf(meshId), meshId, out CommandBuffer commandBuffer)) return;
        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Draw, _state.CurrentProgram, _targets.Bound?.Id ?? 0, meshId));
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
        if (!PrepareDraw(_meshes.LayoutIdOf(meshId), meshId, out CommandBuffer commandBuffer)) return;
        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.DrawMulti, _state.CurrentProgram, _targets.Bound?.Id ?? 0, meshId));

        VulkanBuffer indirect = AllocateIndirect(groupCount, out ulong indirectOffset);

        // The chunk pass is the only storage-buffer multi-draw, and units 0 and
        // 1 are terrainTex and terrainTexLinear, so this is the block atlas.
        if (ssbo && TextureDump.WantsTerrain)
        {
            TextureDump.RequestTerrain(_boundTextures[0], _boundTextures[1]);
        }
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("multidraw mesh=" + meshId + " program=" + _state.CurrentProgram +
                " groups=" + groupCount + " first=" + (groupCount > 0 ? indicesStarts[0] + "/" + indicesSizes[0] : "-") +
                " target=" + (_targets.Bound?.Id ?? -1) + " cull=" + _state.CullEnabled + "/" + _state.CullMode +
                " depthTest=" + _state.DepthTest + " uniforms=" + _lastUniformAllocationOk +
                " indirectOffset=" + indirectOffset);
        }
        _meshes.DrawMulti(commandBuffer, meshId, indicesStarts, indicesSizes, groupCount, indirect, indirectOffset);
    }

    public void DrawFullscreenTriangle()
    {
        if (!PrepareDraw(MeshManager.EmptyLayoutId, 0, out CommandBuffer commandBuffer)) return;
        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Fullscreen, _state.CurrentProgram, _targets.Bound?.Id ?? 0, 0));
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
    private bool PrepareDraw(int vertexLayoutId, int meshId, out CommandBuffer commandBuffer)
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

        // Primitive mode belongs to the mesh, just as it does to GL's VAO.
        // Apply it before both pipeline selection and dynamic state emission.
        // Fullscreen draws have no mesh and must reset a preceding line draw.
        _state.SetTopology(_meshes.Get(meshId)?.DrawMode ?? EnumDrawMode.Triangles);

        // A draw that samples the bound depth attachment with depth writes off is
        // GL's way of reading scene depth mid-pass; the scope holds depth
        // read-only for it, and returns to writable for the next draw that needs
        // to write. Decided before the scope opens, since it decides the layout.
        _targets.SetDepthReadOnly(SamplesBoundDepthWithoutWriting(program));

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

        BindDescriptors(commandBuffer, program, meshId);
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
    /// A sampled colour attachment is snapshotted first: atlas composition reads
    /// an existing tile while drawing into another tile of the same texture.
    /// </summary>
    /// <summary>
    /// Whether any sampler this program reads through is bound to the depth
    /// attachment of the current framebuffer while depth writes are off.
    /// </summary>
    private bool SamplesBoundDepthWithoutWriting(ShaderProgramResources program)
    {
        if (_state.DepthWrite || program.Interface.Samplers.Count == 0) return false;

        foreach (SamplerBinding declared in program.Interface.Samplers)
        {
            int unit = program.SamplerUnits.TryGetValue(declared.Name, out int mapped) ? mapped : declared.Binding;
            if ((uint)unit >= GlStateTracker.MaxTextureUnits) continue;
            if (_targets.IsBoundDepth(_boundTextures[unit])) return true;
        }
        return false;
    }

    private void TransitionSampledTextures(CommandBuffer commandBuffer, ShaderProgramResources program)
    {
        _sampledTextureOverrides.Clear();
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

            // Sampled by this frame command buffer: a later upload to it this
            // frame must go inline, after this draw, as it would on GL.
            _uploads.NoteUse(commandBuffer, texture);

            // The bound depth attachment read with writes off: EnsureRendering
            // puts it in the read-only layout, which serves both uses at once.
            if (_targets.DepthReadOnly && _targets.IsBoundDepth(_boundTextures[unit])) continue;

            if (_targets.IsAttachmentOfBound(_boundTextures[unit]))
            {
                if (texture.Aspect == ImageAspectFlags.ColorBit)
                {
                    SnapshotColorAttachment(commandBuffer, _boundTextures[unit], texture);
                    continue;
                }
                if (RenderTrace.Enabled)
                {
                    RenderTrace.Write("feedback: program " + program.ProgramId + " '" +
                        ProgramNameOf(program.ProgramId) + "' samples texture " + _boundTextures[unit] +
                        " (layout " + texture.Layout + ") which is a written attachment of framebuffer " +
                        (_targets.Bound?.Id ?? -1));
                }
                continue;
            }

            if (texture.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;

            _targets.EndRendering(commandBuffer);
            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.ShaderReadOnlyOptimal);
        }

        if (!placeholderNeeded) return;

        foreach (int id in new[]
                 {
                     _placeholderTexture, _placeholderArrayTexture, _placeholderCubeTexture, _placeholderDepthTexture,
                 })
        {
            VulkanTexture? placeholder = _textures.Get(id);
            if (placeholder == null || placeholder.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;

            _targets.EndRendering(commandBuffer);
            _textures.TransitionTexture(commandBuffer, placeholder, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    private void SnapshotColorAttachment(CommandBuffer commandBuffer, int textureId, VulkanTexture source)
    {
        if (_sampledTextureOverrides.ContainsKey(textureId)) return;

        _targets.EndRendering(commandBuffer);
        if (!_feedbackCopies.TryGetValue(textureId, out int copyId))
        {
            copyId = _textures.Create(source.Width, source.Height, source.Format,
                layers: source.Layers, cube: source.Cube,
                generateMipmaps: source.MipLevels > 1);
            _feedbackCopies.Add(textureId, copyId);
        }
        VulkanTexture copy = _textures.Get(copyId)!;
        copy.State = source.State;

        _textures.TransitionTexture(commandBuffer, source, ImageLayout.TransferSrcOptimal);
        _textures.TransitionTexture(commandBuffer, copy, ImageLayout.TransferDstOptimal);
        for (uint level = 0; level < source.MipLevels; level++)
        {
            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(source.Aspect, level, 0, source.Layers),
                DstSubresource = new ImageSubresourceLayers(copy.Aspect, level, 0, copy.Layers),
                Extent = new Extent3D(Math.Max(1u, source.Width >> (int)level),
                    Math.Max(1u, source.Height >> (int)level), 1),
            };
            _context.Api.CmdCopyImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
                copy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        }
        _textures.TransitionTexture(commandBuffer, copy, ImageLayout.ShaderReadOnlyOptimal);
        _sampledTextureOverrides.Add(textureId, copyId);
        if (RenderTrace.Enabled)
            RenderTrace.Write("snapshot texture=" + textureId + " copy=" + copyId +
                " size=" + source.Width + "x" + source.Height + " mips=" + source.MipLevels);
        // EnsureRendering transitions the source back to its attachment layout.
    }

    /// <summary>
    /// Copies a client UBO's shadow into this frame's uniform ring, so the draw
    /// about to be recorded reads the contents the client uploaded for it rather
    /// than whatever the last upload of the frame left behind.
    ///
    /// One snapshot serves every draw that follows with the block unchanged: the
    /// pairing of frame and version is what makes a thousand chunk draws sharing
    /// one block cost one copy rather than a thousand. A new frame invalidates it
    /// because the ring's cursor is reset. A partial submit does not: the frame
    /// stays in the same slot, the cursor keeps counting, and the snapshot's bytes
    /// are untouched until that slot starts its next frame.
    /// </summary>
    private bool TrySnapshotClientBlock(
        ClientUniformBuffer ubo, ShaderProgramResources program, out uint offset)
    {
        if (ubo.HasSnapshotFor(_frameCounter))
        {
            offset = ubo.SnapshotOffset;
            return true;
        }

        if (!_frames.Current.TryAllocateUniforms(ubo.Shadow.Length, out RingAllocation allocation))
        {
            ReportUniformExhaustion(program, "block '" + ubo.BlockName + "'");
            offset = 0;
            return false;
        }

        fixed (byte* source = ubo.Shadow)
        {
            System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                ubo.Shadow.Length, ubo.Shadow.Length);
        }
        ubo.NoteSnapshot(_frameCounter, allocation.Offset);
        offset = allocation.Offset;
        return true;
    }

    /// <summary>
    /// Reports that the frame's uniform ring ran out. Said once per frame so a
    /// long frame does not flood the log.
    /// </summary>
    private void ReportUniformExhaustion(ShaderProgramResources program, string what)
    {
        if (_uniformExhaustionReportedFrame == _frameCounter) return;

        _uniformExhaustionReportedFrame = _frameCounter;
        string message = VulkanContext.ErrorPrefix + "uniform ring exhausted in frame " + _frameCounter +
            " (" + _frames.Current.UniformBytesUsed + " of " + _frames.Current.UniformCapacity +
            " bytes used) at a draw with program " + program.ProgramId +
            " '" + ProgramNameOf(program.ProgramId) + "' for " + what;
        _diagnostics.Add(SanitiseForClientLog(message));
        MirrorValidationMessage(message);
    }

    private void BindDescriptors(CommandBuffer commandBuffer, ShaderProgramResources program, int meshId)
    {
        Vk api = _context.Api;

        // Set 0: the generated uniform block plus one entry for every block the
        // shader declared for itself. Every one of them is a dynamic descriptor
        // pointing at this frame's uniform ring, so the set itself never changes
        // - the per-draw offset travels alongside it instead.
        bool hasGeneratedBlock = program.Interface.HasUniformBlock;
        int dynamicCount = (hasGeneratedBlock ? 1 : 0) + program.Interface.UniformBlocks.Count;

        if (dynamicCount > 0)
        {
            var buffers = new List<BufferBindingValue>(dynamicCount);

            // Dynamic offsets are consumed in increasing order of binding number,
            // not in the order the bindings were written, so each one is carried
            // with its binding and sorted below.
            uint* offsetBindings = stackalloc uint[dynamicCount];
            uint* offsetValues = stackalloc uint[dynamicCount];
            int offsetCount = 0;
            bool allocationOk = true;

            if (hasGeneratedBlock)
            {
                uint generatedOffset = 0;
                if (_frames.Current.TryAllocateUniforms(
                        program.UniformShadow.Length, out RingAllocation allocation))
                {
                    fixed (byte* source = program.UniformShadow)
                    {
                        System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                            program.UniformShadow.Length, program.UniformShadow.Length);
                    }
                    generatedOffset = allocation.Offset;
                    program.MarkUniformsClean();
                }
                else
                {
                    // The draw goes ahead reading offset zero of the ring, which
                    // is some other draw's block: wrong, and for a shader that
                    // loops on a uniform count, possibly fatal.
                    allocationOk = false;
                    ReportUniformExhaustion(program, "its generated uniform block");
                }

                buffers.Add(new BufferBindingValue(
                    ProgramInterfaceLayout.DefaultBlockBinding,
                    _frames.UniformBuffer, 0, (ulong)program.UniformShadow.Length));
                offsetBindings[offsetCount] = ProgramInterfaceLayout.DefaultBlockBinding;
                offsetValues[offsetCount++] = generatedOffset;
            }

            // A block the shader declares is fed by whichever UBO the client
            // created under that name; one it has not created yet reads zeroes
            // rather than leaving the descriptor undefined.
            foreach (BlockBinding block in program.Interface.UniformBlocks)
            {
                ClientUniformBuffer? ubo = null;
                if (_boundUniformBuffers.TryGetValue(block.BlockName, out int handle))
                {
                    _uniformBuffers.TryGetValue(handle, out ubo);
                }

                if (ubo == null)
                {
                    // Zeroes, at dynamic offset zero. The placeholder has existed
                    // since the device came up; should it somehow not, the ring
                    // stands in, because a set with a hole in it - or a dynamic
                    // offset count that disagrees with the layout - is an invalid
                    // draw rather than merely a wrong colour.
                    buffers.Add(_placeholderUniforms != null
                        ? new BufferBindingValue((uint)block.Binding, _placeholderUniforms.Handle,
                            0, _placeholderUniforms.Size, _placeholderUniforms.Id)
                        : new BufferBindingValue((uint)block.Binding, _frames.UniformBuffer,
                            0, Math.Min(16384UL, _context.Capabilities.MaxUniformBufferRange)));
                    offsetBindings[offsetCount] = (uint)block.Binding;
                    offsetValues[offsetCount++] = 0;
                    continue;
                }

                if (TrySnapshotClientBlock(ubo, program, out uint blockOffset))
                {
                    buffers.Add(new BufferBindingValue((uint)block.Binding,
                        _frames.UniformBuffer, 0, (ulong)ubo.Shadow.Length));
                    offsetBindings[offsetCount] = (uint)block.Binding;
                    offsetValues[offsetCount++] = blockOffset;
                }
                else
                {
                    // No room left in the ring. Rather than aliasing the block's
                    // persistent buffer - which would hand every remaining draw
                    // in the frame the last upload, the exact bug the ring
                    // exists to fix - this draw gets its own transient copy.
                    // Slower, but still correct; the overflow is counted so a
                    // scene that lives in this path shows up in the stats.
                    allocationOk = false;
                    VulkanStats.NoteUniformOverflow();
                    var overflow = new VulkanBuffer(_context, (ulong)ubo.Shadow.Length,
                        BufferUsageFlags.UniformBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                    fixed (byte* shadow = ubo.Shadow)
                    {
                        System.Buffer.MemoryCopy(shadow, (void*)overflow.Mapped,
                            ubo.Shadow.Length, ubo.Shadow.Length);
                    }
                    buffers.Add(new BufferBindingValue((uint)block.Binding,
                        overflow.Handle, 0, overflow.Size, overflow.Id));
                    offsetBindings[offsetCount] = (uint)block.Binding;
                    offsetValues[offsetCount++] = 0;
                    // Released and deferred in that order: the cached set naming
                    // this buffer must not outlive it under a reused handle.
                    // (This is the only VulkanBuffer a client UBO ever owns -
                    // the block itself is host-side shadow plus a ring snapshot.)
                    _descriptors.Release(overflow.Id);
                    _frames.DeferDeletion(overflow);
                }
            }

            _lastUniformAllocationOk = allocationOk;

            // Insertion sort by binding: at most a handful of entries, and the
            // generated block is already the lowest of them.
            for (int i = 1; i < offsetCount; i++)
            {
                uint binding = offsetBindings[i];
                uint value = offsetValues[i];
                int j = i - 1;
                while (j >= 0 && offsetBindings[j] > binding)
                {
                    offsetBindings[j + 1] = offsetBindings[j];
                    offsetValues[j + 1] = offsetValues[j];
                    j--;
                }
                offsetBindings[j + 1] = binding;
                offsetValues[j + 1] = value;
            }

            var uniformContents = new DescriptorSetContents(
                program.ProgramId, ProgramInterfaceLayout.DefaultBlockSet,
                Array.Empty<SamplerBindingValue>(), buffers.ToArray());

            DescriptorSet uniformSet = _descriptors.Get(
                uniformContents, program.SetLayouts[ProgramInterfaceLayout.DefaultBlockSet]);

            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                ProgramInterfaceLayout.DefaultBlockSet, 1, &uniformSet,
                (uint)offsetCount, offsetCount == 0 ? null : offsetValues);
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
                ulong resource = 0;

                if ((uint)unit < GlStateTracker.MaxTextureUnits)
                {
                    int textureId = _sampledTextureOverrides.TryGetValue(_boundTextures[unit], out int copy)
                        ? copy : _boundTextures[unit];
                    VulkanTexture? texture = _textures.Get(textureId);
                    if (texture != null && !TextureSuitsSampler(texture, declared.TypeName))
                    {
                        // Left unbound on purpose, so the placeholder that suits
                        // the sampler takes the slot below.
                        if (RenderTrace.Enabled)
                        {
                            RenderTrace.Write("sampler '" + declared.Name + "' (" + declared.TypeName +
                                ") on program " + program.ProgramId + " has texture " + _boundTextures[unit] +
                                " of format " + texture.Format + " bound, which it cannot sample; using a placeholder");
                        }
                        texture = null;
                    }
                    if (texture != null)
                    {
                        view = texture.View;
                        resource = texture.Id;
                        // A sampler bound to the unit overrides the texture's own
                        // state, which is what glBindSampler means.
                        // MAX_LEVEL belongs to the texture, even when a sampler
                        // overrides its filters. Resolve at draw time so changes
                        // to either object also affect an already-bound unit.
                        SamplerState sampling = _standaloneSamplers.TryGetValue(_unitSamplerOverrides[unit], out SamplerState custom)
                            ? custom with { MaxLevel = texture.State.MaxLevel }
                            : texture.State;
                        sampler = _textures.Samplers.Get(sampling);
                    }
                }

                // The bound depth attachment, sampled with writes off, is read in
                // the layout the scope holds it in rather than shader-read-only.
                ImageLayout layout = view.Handle != 0 && _targets.DepthReadOnly
                                     && _targets.IsBoundDepth(_boundTextures[unit])
                    ? ImageLayout.DepthReadOnlyOptimal
                    : ImageLayout.ShaderReadOnlyOptimal;

                bindings[i] = new SamplerBindingValue((uint)declared.Binding, view, sampler, resource, layout);
            }

            // A sampler the client left unbound gets the placeholder rather than
            // an empty descriptor. Leaving the set unbound is not an option: the
            // shader statically uses set 1, and drawing without it is undefined
            // behaviour that costs the device rather than one texture. GL is
            // permissive here - sampling an unbound texture reads black and the
            // draw proceeds - so the placeholder is also the closer emulation.
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].View.Handle != 0 && bindings[i].Sampler.Handle != 0) continue;

                VulkanTexture? placeholder =
                    _textures.Get(PlaceholderFor(program.Interface.Samplers[i].TypeName));
                if (placeholder == null) continue;

                bindings[i] = new SamplerBindingValue(
                    bindings[i].Binding, placeholder.View, _textures.Samplers.Get(placeholder.State),
                    placeholder.Id);
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

        // Set 2: the storage buffers a shader reads its own vertices from.
        //
        // The SSBO chunk path does not use vertex attributes at all - the chunk
        // shaders declare `readonly buffer faceDataBuf` and index it by
        // gl_VertexID, with the packed face records living in the mesh's xyz
        // slot. Without this set bound the shader reads nothing and the terrain
        // is simply absent, which is exactly how it presented.
        if (program.Interface.StorageBlocks.Count > 0 && meshId > 0)
        {
            var storage = new List<BufferBindingValue>(program.Interface.StorageBlocks.Count);
            foreach (BlockBinding block in program.Interface.StorageBlocks)
            {
                VulkanBuffer? buffer = _meshes.BufferOf(meshId, MeshManager.BufferXyz);
                if (buffer == null) continue;

                storage.Add(new BufferBindingValue(
                    (uint)block.Binding, buffer.Handle, 0, buffer.Size, buffer.Id));
            }

            if (storage.Count == program.Interface.StorageBlocks.Count)
            {
                DescriptorSet storageSet = _descriptors.Get(
                    new DescriptorSetContents(program.ProgramId, ProgramInterfaceLayout.StorageSet,
                        Array.Empty<SamplerBindingValue>(), storage.ToArray()),
                    program.SetLayouts[ProgramInterfaceLayout.StorageSet]);

                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                    ProgramInterfaceLayout.StorageSet, 1, &storageSet, 0, null);
            }
            else if (RenderTrace.Enabled)
            {
                RenderTrace.Write("draw with an incomplete storage set on program " + program.ProgramId +
                    " mesh " + meshId);
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

        VulkanStats.NoteDynamicStateCommands(VulkanStats.DynamicStateCommandsPerDraw);
    }

    private VulkanBuffer? _indirectScratch;
    private ulong _indirectCursor;
    private ulong _indirectFrameUsage;
    private ulong _indirectPeakFrameUsage;

    /// <summary>
    /// Hands out a region of the indirect-command buffer for one multi-draw.
    ///
    /// The commands are written on the CPU when the draw is recorded and read by
    /// the GPU when it executes, which is later - after every other draw of the
    /// frame has been recorded too. So each draw needs its own region: writing
    /// them all at offset zero meant every multi-draw in a frame executed with
    /// the ranges of whichever was recorded last, and the chunk pass is hundreds
    /// of them.
    ///
    /// The buffer is a ring that wraps, sized to hold four times the busiest
    /// frame seen, so a wrap can never reach a region a frame still in flight is
    /// reading. A buffer that has to grow is deferred rather than freed, because
    /// draws already recorded this frame still name it.
    /// </summary>
    private VulkanBuffer AllocateIndirect(int groupCount, out ulong offset)
    {
        ulong needed = (ulong)Math.Max(groupCount, 1) * (ulong)sizeof(DrawIndexedIndirectCommand);

        _indirectFrameUsage += needed;
        if (_indirectFrameUsage > _indirectPeakFrameUsage) _indirectPeakFrameUsage = _indirectFrameUsage;

        ulong required = Math.Max(Math.Max(_indirectPeakFrameUsage * 4, needed), 256UL * 1024);
        if (_indirectScratch == null || _indirectScratch.Size < required)
        {
            if (_indirectScratch != null) _frames.DeferDeletion(_indirectScratch);

            _indirectScratch = new VulkanBuffer(_context, required,
                BufferUsageFlags.IndirectBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            _indirectCursor = 0;
        }

        if (_indirectCursor + needed > _indirectScratch.Size) _indirectCursor = 0;

        offset = _indirectCursor;
        _indirectCursor += needed;
        return _indirectScratch;
    }

    // -------------------------------------------------------------------- queries

    private QueryRing _queryRing = null!;
    private ReadbackManager _readbacks = null!;

    /// <summary>Whether occlusion queries count samples exactly. Tests only.</summary>
    internal bool PreciseOcclusionForTests => _context.Capabilities.OcclusionQueryPrecise;

    /// <summary>Occlusion query pools across every frame slot. Tests only.</summary>
    internal int OcclusionQueryPoolsForTests => _queryRing.PoolCount;

    public int CreateOcclusionQuery() => _queryRing.Create();

    public void BeginOcclusionQuery(int queryId)
    {
        if (!_frameActive || !_queryRing.TryBegin(queryId, out QueryPool pool, out uint index, out bool freshPool)) return;

        // The slot's pools are reset at frame start, before any scope opens, so
        // the query begins inside the scope the covered draw uses. Only a pool
        // created just now needs a reset here, and a reset has to happen outside
        // a scope: one restart per pool ever, never in steady state.
        CommandBuffer commandBuffer = Commands;
        if (freshPool)
        {
            _targets.EndRendering(commandBuffer);
            _context.Api.CmdResetQueryPool(commandBuffer, pool, 0, QueryRing.QueriesPerPool);
        }
        _targets.EnsureRendering(commandBuffer);
        // GL_SAMPLES_PASSED is an exact count (sun glare divides it by 1500).
        _context.Api.CmdBeginQuery(commandBuffer, pool, index,
            _context.Capabilities.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : default(QueryControlFlags));
    }

    public void EndOcclusionQuery(int queryId)
    {
        if (_frameActive &&
            _queryRing.TryEnd(queryId, _frames.Current.FrameValue, out QueryPool pool, out uint index))
        {
            _context.Api.CmdEndQuery(Commands, pool, index);
        }
    }

    /// <summary>
    /// GL_QUERY_RESULT_AVAILABLE without any wait: true once the Frame timeline
    /// passed the command buffer that ended the query, a frame or two later.
    /// </summary>
    public bool IsQueryResultAvailable(int queryId) => _queryRing.IsResultAvailable(queryId);

    /// <summary>
    /// The samples the latest query counted. Never waits and never submits: the
    /// client polls availability first (sun glare does), and a result asked for
    /// early returns the previous query's count, or "all visible" if there was
    /// none - for a query that gates culling or glare, the cheap failure.
    /// </summary>
    public int GetQueryResult(int queryId) => _queryRing.GetResult(queryId);

    /// <summary>
    /// Submits everything the frame has recorded so far and keeps recording it in
    /// the same slot, so a readback queued next sees work the frame already issued.
    /// The open upload batch rides along, first. No wait, no new slot, no frame
    /// counter increment: arena cursors and uniform snapshots carry on.
    /// </summary>
    private ulong SubmitPartial()
    {
        _targets.EndRendering(Commands);
        ulong submitted = _frames.SubmitPartial();
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));
        return submitted;
    }

    public void DeleteQuery(int queryId) => _queryRing.Delete(queryId);

    // ------------------------------------------------------------------- readback

    /// <summary>
    /// Reads back every texture OPTIMUM_DUMP_TEXTURES asked for. Debug only; see
    /// <see cref="TextureDump" /> for why it exists.
    /// </summary>
    private void DumpRequestedTextures()
    {
        foreach (int textureId in TextureDump.Take())
        {
            VulkanTexture? texture = _textures.Get(textureId);
            if (texture == null)
            {
                RenderTrace.Write("texture dump: no texture " + textureId);
                continue;
            }

            int width = (int)texture.Width;
            int height = (int)texture.Height;
            byte[] data = ReadBackLevel0(texture);

            bool bgra = texture.Format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
            bool written = TextureDump.Write(textureId, width, height, bgra, texture.Format, data);
            if (written) TextureDump.Complete(textureId);

            RenderTrace.Write("texture dump: " + textureId + " " + width + "x" + height +
                " " + texture.Format + " mips=" + texture.MipLevels + " -> " + (written ? "ok" : "failed"));
        }
    }

    /// <summary>
    /// Copies level 0 of a texture into host memory, raw texels in the image's own
    /// format, rows in memory order (GL order: the backend never flips Y). Inside
    /// a frame it goes through <see cref="ReadBack" />, so the frame stays open.
    /// Depth images are copied through their depth aspect.
    /// </summary>
    private byte[] ReadBackLevel0(VulkanTexture texture)
    {
        int width = (int)texture.Width;
        int height = (int)texture.Height;
        ulong bytes = (ulong)width * (ulong)height * (ulong)BytesPerPixel(texture.Format);
        ImageAspectFlags aspect = (texture.Aspect & ImageAspectFlags.DepthBit) != 0
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.ColorBit;

        byte[] data = new byte[bytes];
        fixed (byte* destination = data)
        {
            ReadBack(texture, 0, 0, (uint)width, (uint)height, aspect, bytes, (IntPtr)destination);
        }
        return data;
    }

    /// <summary>
    /// The one readback path: screenshots, the texture dump and the parity dump.
    ///
    /// Inside a frame the open scope closes, the copy is recorded into the frame
    /// itself (into the slot's readback arena), the recorded part is submitted
    /// with <see cref="SubmitPartial" /> and the caller waits on that single Frame
    /// timeline value; the frame carries on in the same slot, so every draw after
    /// the read still reaches the screen. Between frames the copy is appended to
    /// the open upload batch (after every upload recorded so far), which is
    /// submitted on its own; the queue runs it after every frame already submitted,
    /// so waiting on its Transfer value is enough. Neither path waits for the
    /// whole device.
    /// </summary>
    private void ReadBack(VulkanTexture texture, int x, int y, uint width, uint height,
        ImageAspectFlags aspect, ulong bytes, IntPtr destination)
    {
        if (_frameActive)
        {
            _targets.EndRendering(Commands);
            ReadbackTicket ticket = _readbacks.CopyToHost(texture, x, y, width, height, aspect, bytes);
            SubmitPartial();
            _readbacks.WaitAndCopy(ticket, destination);
            return;
        }

        using var readback = new VulkanBuffer(_context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        ImageLayout restore = texture.Layout;
        CommandBuffer commandBuffer = _uploads.BeginRecording(inlineInFrame: false);
        try
        {
            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
                ImageOffset = new Offset3D(x, y, 0),
                ImageExtent = new Extent3D(width, height, 1),
            };
            _context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);

            if (restore != ImageLayout.Undefined) _textures.TransitionTexture(commandBuffer, texture, restore);
        }
        finally
        {
            _uploads.EndRecording();
        }
        ulong transferValue = _uploads.SubmitStandalone();
        _frames.Timeline.WaitForTransfer(transferValue, WaitSite.Readback);

        System.Buffer.MemoryCopy((void*)readback.Mapped, (void*)destination, (long)bytes, (long)bytes);
    }

    private void RecordGlInternalFormat(int textureId, int glInternalFormat)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) texture.GlInternalFormat = glInternalFormat;
    }

    /// <summary>
    /// The parity dump's readback (<see cref="OptimumParityDump" />): level 0 in
    /// the representation glGetTexImage produces on the OpenGL path, decoded by
    /// <see cref="TextureDump.ToParityReadback" />. Debug only.
    /// </summary>
    public OptimumTextureReadback? ReadTextureForParity(int textureId)
    {
        if (!_frameActive) return null;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null || texture.Cube || texture.Layers > 1) return null;

        byte[] data = ReadBackLevel0(texture);
        int glInternalFormat = texture.GlInternalFormat != 0
            ? texture.GlInternalFormat
            : TextureDump.GlInternalFormatOf(texture.Format);
        OptimumTextureReadback? readback = TextureDump.ToParityReadback(texture.Format, glInternalFormat,
            (int)texture.Width, (int)texture.Height, data);
        RenderTrace.Write("parity dump: texture " + textureId + " " + texture.Width + "x" + texture.Height +
            " " + texture.Format + " -> " + (readback != null ? "ok" : "undecodable"));
        return readback;
    }

    /// <summary>
    /// Bytes per texel for the formats the dump path is expected to see.
    /// Shared with <see cref="TextureDump.Write" />'s decode switch so the
    /// readback size and the reader always agree on the stride.
    /// </summary>
    private static int BytesPerPixel(Format format) => TextureDump.BytesPerTexel(format);

    /// <summary>
    /// Reads back the bound target's first colour attachment, four bytes per
    /// pixel, rows bottom-up.
    ///
    /// Bottom-up is not an accident: it is what <c>glReadPixels</c> produces, and
    /// the existing screenshot and AVI paths already expect it. Because the
    /// backend never flips Y, the image in memory is laid out exactly as GL laid
    /// it out, so those paths keep working untouched. The game reads pixels
    /// mid-frame and carries on drawing; <see cref="ReadBack" /> keeps the frame open.
    /// </summary>
    public void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)
    {
        if (destination == IntPtr.Zero || width <= 0 || height <= 0) return;

        VulkanFramebuffer? target = _targets.Bound;
        if (target == null) return;

        VulkanTexture? texture = _textures.Get(target.Color[0].TextureId);
        if (texture == null) return;

        ReadBack(texture, x, y, (uint)width, (uint)height, ImageAspectFlags.ColorBit,
            (ulong)width * (ulong)height * 4, destination);
    }

    // ------------------------------------------------------------------- teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_context != null)
        {
            VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);
        }

        foreach (ShaderProgramResources program in _programs.Values) program.Dispose();
        _programs.Clear();

        _uniformBuffers.Clear();

        _queryRing?.Dispose();
        _readbacks?.Dispose();

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
        _context?.Dispose();
    }
}
