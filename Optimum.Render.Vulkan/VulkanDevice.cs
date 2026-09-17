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
/// The Vulkan renderer behind <see cref="Platform.VulkanClientPlatform" />, which owns it
/// and calls it from every graphics override (Vulkan-native plan, Phase 1A).
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
public sealed unsafe partial class VulkanDevice : IDisposable
{
    private VulkanContext _context = null!;
    private UploadManager _uploads = null!;
    private TextureManager _textures = null!;
    private MeshManager _meshes = null!;
    private RenderTargetManager _targets = null!;

    /// <summary>
    /// The streaming frame graph (Phase 2 step 2). On unless OPTIMUM_VULKAN_FRAMEGRAPH=0;
    /// off, declarations only bind and every scope comes from inference as before.
    /// </summary>
    private readonly Graph.FrameGraph _graph = new();
    private GraphicsPipelineCache _pipelines = null!;

    /// <summary>Compute programs and their pipelines (the frame graph's compute pass kind).</summary>
    private ComputePipelineCache _compute = null!;

    /// <summary>Per-slot descriptor sets of compute passes, reset when the slot begins a frame.</summary>
    private ComputeDescriptorArena[] _computeArenas = Array.Empty<ComputeDescriptorArena>();
    private DescriptorCache _descriptors = null!;

    /// <summary>How many descriptor sets the cache currently holds. For tests.</summary>
    internal int CachedDescriptorSets => _descriptors.Count;
    private FrameRing _frames = null!;
    private ShaderCompiler _shaderCompiler = null!;

    /// <summary>The native shaders loaded at device start; null when they are off (docs/vulkan-native-shaders.md section 8).</summary>
    private NativeShaderLibrary? _nativeShaders;
    private int _nativeLinks, _rewrittenLinks, _failedNativeLinks;
    private int _nativeLinksReported, _rewrittenLinksReported, _failedNativeLinksReported;

    /// <summary>
    /// Where compiled SPIR-V and the driver's pipeline cache are kept between launches,
    /// set before <see cref="Initialize" />. Null keeps nothing, which is what tests get;
    /// the platform points it at the game's per-user cache folder. OPTIMUM_VULKAN_SHADER_CACHE
    /// overrides it: a path to use instead, or 0 to keep nothing.
    /// </summary>
    public string? ShaderCacheDirectory { get; set; }

    /// <summary>
    /// False forces the rewriter for every program, as <c>OPTIMUM_VK_NATIVE_SHADERS=0</c> does; null follows the
    /// environment. Read at <see cref="Initialize" />.
    /// </summary>
    public bool? NativeShadersEnabled { get; set; }

    /// <summary>
    /// The directory holding <c>shaders.manifest.json</c>, in place of <c>shaders-vk</c> beside the renderer
    /// assembly (and of <c>OPTIMUM_VK_SHADER_SOURCE</c>). Read at <see cref="Initialize" />. For tests.
    /// </summary>
    internal string? NativeShaderDirectory { get; set; }

    /// <summary>
    /// The launcher's mod shader scan (<c>OptimumConfig.IsShaderProgramOverriddenByMods</c>): true for a pass name
    /// whose GLSL a mod replaced, and for <c>"all"</c> when every program is rewriter-only. Such a program links
    /// through the rewriter from the mod's source instead of the native SPIR-V. Null consults no scan (tests, a
    /// device outside the client); <c>OPTIMUM_VK_NATIVE_SHADERS=force</c> ignores it. Read at <see cref="Initialize" />.
    /// </summary>
    public Func<string, bool>? ShaderProgramOverriddenByMods { get; set; }

    /// <summary>True ignores <see cref="ShaderProgramOverriddenByMods" /> as <c>OPTIMUM_VK_NATIVE_SHADERS=force</c> does; null follows the environment. For tests.</summary>
    internal bool? IgnoreModShaderScan { get; set; }

    /// <summary>The scan <see cref="LinkProgram" /> consults: null when there is none or it is ignored.</summary>
    private Func<string, bool>? _modShaderScan;
    private readonly HashSet<string> _modOverrideLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="Initialize" /> made of the native shaders: the origin and program count, or why they are off.</summary>
    internal string NativeShaderStatus { get; private set; } = "not loaded";

    /// <summary>Programs linked from the manifest, through the rewriter, and native programs that fell back, since the device came up.</summary>
    internal (int Native, int Rewritten, int Failed) ShaderLinkCounts => (_nativeLinks, _rewrittenLinks, _failedNativeLinks);

    /// <summary>Whether a linked program came from the native manifest.</summary>
    internal bool IsNativeProgram(int programId) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) && program.IsNative;

    /// <summary>The pipeline cache and key-log files and their saves; null when there is no cache root.</summary>
    private PipelineCachePersistence? _pipelinePersistence;

    /// <summary>
    /// Forces blocking pipeline creation (every draw lands in the frame that issues it) when
    /// true, allows background compiles with skipped draws when false; null reads
    /// OPTIMUM_VULKAN_SYNC_PIPELINES. Set before <see cref="Initialize" />. GPU tests that read
    /// pixels back after one frame get true from GpuTest.
    /// </summary>
    public bool? SynchronousPipelines { get; set; }

    internal static bool ResolveSynchronousPipelines(bool? configured, string? environment) =>
        configured ?? environment?.Trim() is "1" or "on" or "true";

    /// <summary>
    /// The same, where a frame capture also forces blocking creation: OPTIMUM_PARITY_DUMP and
    /// OPTIMUM_HEADLESS_FRAMES write exact frames to disk, and a background compile would leave
    /// draws out of them. Each counts when it names an absolute directory, which is when the
    /// capture code acts on it. An explicit <paramref name="configured" /> still wins.
    /// </summary>
    internal static bool ResolveSynchronousPipelines(bool? configured, string? environment, string? parityDump,
        string? headlessFrames) =>
        configured ?? (ResolveSynchronousPipelines(null, environment) || NamesCaptureDirectory(parityDump) ||
                       NamesCaptureDirectory(headlessFrames));

    private static bool NamesCaptureDirectory(string? value) =>
        !string.IsNullOrWhiteSpace(value) && System.IO.Path.IsPathRooted(value);

    /// <summary>The pipeline cache. Tests only.</summary>
    internal GraphicsPipelineCache PipelinesForTests => _pipelines;

    internal static string? ResolveShaderCacheRoot(string? configured, string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment)) return string.IsNullOrWhiteSpace(configured) ? null : configured;
        string value = environment.Trim();
        return value is "0" or "off" or "false" ? null : value;
    }

    private readonly Dictionary<int, ShaderProgramResources> _programs = new();

    /// <summary>Pass names by program id, so a device-loss report can name the shader.</summary>
    private readonly Dictionary<int, string> _programNames = new();

    /// <summary>Scratch for the SSBO path's pruned custom ints, grown as needed.</summary>
    private int[] _prunedCustomInts = [];

    private uint _frameCounter;
    private uint _uniformExhaustionReportedFrame = uint.MaxValue;
    private readonly Dictionary<IShader, StagedStage> _stagedStages = new();

    /// <summary>
    /// Error-severity diagnostics since the last GetError, under their own lock:
    /// the layers call back from whichever thread made the Vulkan call.
    /// </summary>
    private readonly List<string> _errors = new();

    /// <summary>
    /// How many entries <see cref="_errors" /> holds. GetError runs after every
    /// render stage, and in steady state this read is all it costs.
    /// </summary>
    private volatile int _errorCount;

    /// <summary>A client that never drains the queue does not grow it without bound.</summary>
    private const int MaxQueuedErrors = 1024;

    /// <summary>What the frame command buffer already holds, so a draw emits only changed dynamic state.</summary>
    private readonly DynamicStateCache _dynamicState = new();
    private long _dynamicStateCommands;

    /// <summary>Which resources are young enough that their descriptor sets belong in the per-slot arena.</summary>
    private readonly ResourceAge _resourceAge = new();
    private DescriptorArena[] _descriptorArenas = Array.Empty<DescriptorArena>();

    /// <summary>Per-slot indirect-command buffers; see <see cref="IndirectRing" />.</summary>
    private IndirectRing _indirectRing = null!;
    private VulkanBuffer?[] _indirectBuffers = Array.Empty<VulkanBuffer?>();

    /// <summary>Buffers taken this frame by multi-draws that did not fit their slot's buffer.</summary>
    private readonly List<VulkanBuffer> _indirectOverflow = new();
    private ulong _indirectOverflowCursor;
    private long _indirectOverflows;
    private long _indirectGrowths;

    /// <summary>
    /// Decision 9's set 1 and the one shared pipeline layout every program's pipelines
    /// are built against. Created at bring-up and kept current (slots retire with their
    /// textures, writes flush before every submission).
    /// </summary>
    private BindlessTextureTable? _bindless;
    private SharedPipelineLayout? _sharedLayout;

    /// <summary>
    /// The shared frame block (set 0, <see cref="FrameGlobals" />): the CPU shadow every
    /// frame-global write lands in, and the ring snapshot draws bind until a write
    /// changes something. Replaces up to 56 per-program copies of the same values.
    /// </summary>
    private readonly byte[] _frameGlobals = FrameGlobals.CreateShadow();
    private uint _frameGlobalsVersion = 1;
    private uint _frameGlobalsSnapshotFrame;
    private uint _frameGlobalsSnapshotVersion;
    private uint _frameGlobalsSnapshotOffset;

    /// <summary>Sixteen bytes of float defaults followed by sixteen of int.</summary>
    private const ulong DefaultAttributeBufferSize = 32;

    private VulkanBuffer? _defaultAttributes;

    /// <summary>
    /// The zero-filled buffer at every set 2 binding a draw has nothing for. An unbound
    /// sampler reads the bindless table's placeholder of its kind instead.
    /// </summary>
    private VulkanBuffer? _placeholderUniforms;

    // Atlas composition reads one tile while writing another in the same image.
    // Each such draw takes a pooled ReadSelf copy, refreshed before the draw and
    // released when the next draw's samplers are resolved (Phase 2 step 4).
    private Graph.FeedbackCopyPool _readSelfCopies = null!;
    private readonly Dictionary<int, int> _sampledTextureOverrides = new();

    // Physical backing for frame-graph transients (Transient pool class).
    private Graph.TransientAllocator _transients = null!;

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
    /// validation), "best" (best practices with the NVIDIA and AMD sets), "mobile"
    /// (the Arm and IMG sets), "gpu" (GPU-assisted) and "gpu-only" (GPU-assisted,
    /// core off). Requested through VK_EXT_layer_settings (the deprecated
    /// VK_EXT_validation_features on older layers) so it does not depend on the
    /// layer's environment variable names, which changed.
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
                AddDiagnostic(SanitiseForClientLog(message));
                MirrorValidationMessage(message);
                if (RenderTrace.Enabled)
                    RenderTrace.Write("validation: target=" + (_targets?.Bound?.Id ?? -1) + " " + message);
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
            AddDiagnostic(VulkanContext.ErrorPrefix + message);
            MirrorValidationMessage(message);
        };
        VulkanResult.DescribeDeviceLoss = DescribeDeviceLoss;
        MirrorValidationMessage("--- device up on " + _context.Capabilities.DeviceName +
            "; validation layers " + (_context.ValidationEnabled ? "ENABLED " + _context.ValidationLayerVersion : "NOT AVAILABLE") +
            (_context.ValidationSettingsApplied.Length == 0 ? "" : "; " + _context.ValidationSettingsApplied) +
            "; GPU checkpoints " + (_context.CheckpointsAvailable ? "ENABLED" : "NOT AVAILABLE") +
            "; device fault reporting " + (_context.DeviceFaultAvailable ? "ENABLED" : "NOT AVAILABLE") +
            "; poison " + (_context.PoisonFreshResources ? "ON" : "off") +
            "; color write tier " + DeviceCaps.Token(_context.Capabilities.ColorWriteTier) +
            (_context.Capabilities.DynamicColorBlend ? " (dynamic blend)" : "") +
            "; bindless sampled images per stage " +
            _context.Capabilities.DescriptorIndexing.MaxPerStageDescriptorUpdateAfterBindSampledImages +
            " (needs " + DescriptorIndexingFloor.RequiredSampledImages + ")" +
            "; push constants " + _context.Capabilities.DescriptorIndexing.MaxPushConstantsSize + " B" +
            "; " + _context.Capabilities.LatencySummary);
        // Seams S1-S5: the backend is installed before the frame ring and the first
        // swapchain exist, so nothing in the frame ever sees a different instance.
        InstallSelectedLatencyBackend();
        // A ReBAR miss is logged, not an error: the validation mirror and the
        // trace, never GetError. The stats sample reads this allocator's heaps.
        _context.Allocator.Log = MirrorValidationMessage;
        VulkanStats.MemorySource = _context.Allocator;
        // Uploads never wait: they ride the next frame submission, recorded from
        // any thread into the ring's upload batch (or inline into the frame when
        // it already used the destination; see UploadManager).
        _frames = new FrameRing(_context);
        // Seam S4: the installed backend tags this ring's submits.
        _frames.Latency.Backend = Latency;
        _uploads = _frames.Uploads;
        _textures = new TextureManager(_context, _uploads);
        _meshes = new MeshManager(_context, _uploads);
        _targets = new RenderTargetManager(_context, _textures, _graph);
        // An inline upload records transfer commands into the frame command
        // buffer, which no rendering scope may enclose.
        _uploads.CloseRenderingScope = commandBuffer => _targets.EndRendering(commandBuffer);
        // A barrier flushed into the frame command buffer while a scope is open
        // is a transition inside the scope; debug builds reject it.
        _textures.ScopeOpen = commandBuffer =>
            _frameActive && _targets.RenderingActive && commandBuffer.Handle == Commands.Handle;
        _barriers = _textures.CreateBatcher();
        // Transients and ReadSelf copies live in the Transient pool class; both
        // release through ReleaseTexture, which retires on the timeline.
        _transients = new Graph.TransientAllocator(new Graph.TextureTransientBacking(_textures, ReleaseTexture),
            TransientAliasingOverride ?? Graph.TransientAllocator.AliasingFromEnvironment());
        _readSelfCopies = new Graph.FeedbackCopyPool(_frames.Timeline, CreateReadSelfCopy, ReleaseTexture);
        string? cacheRoot = ResolveShaderCacheRoot(ShaderCacheDirectory,
            Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_SHADER_CACHE"));
        byte[]? pipelineSeed = null;
        if (cacheRoot != null)
        {
            _pipelinePersistence = PipelineCachePersistence.Open(cacheRoot,
                PipelineCacheIdentity.Of(_context.Capabilities), out pipelineSeed);
            _pipelinePersistence.Log = MirrorValidationMessage;
        }
        _pipelines = new GraphicsPipelineCache(_context, _context.Capabilities.ColorWriteTier,
            _context.Capabilities.DynamicColorBlend, pipelineSeed);
        // Background compiles (docs/research/vulkan-caching.md, design item 4): a draw whose
        // pipeline is not in the driver cache is skipped while a worker compiles it.
        bool synchronousPipelines = ResolveSynchronousPipelines(SynchronousPipelines,
            Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_SYNC_PIPELINES"),
            Environment.GetEnvironmentVariable("OPTIMUM_PARITY_DUMP"),
            Environment.GetEnvironmentVariable("OPTIMUM_HEADLESS_FRAMES"));
        _pipelines.AsyncCompiles = !synchronousPipelines;
        _pipelines.KeyLog = _pipelinePersistence?.KeyLog;
        _descriptors = new DescriptorCache(_context);
        _compute = new ComputePipelineCache(_context, () => _pipelines.DriverCache);
        // Decision 9: the bindless table retires a texture's slots on the timeline
        // values of its deletion, and the shared layout names the table's set layout.
        _bindless = new BindlessTextureTable(_context, _textures, _frames.Timeline);
        _textures.Deleted = texture =>
        {
            _bindless.Release(texture.Id);
            ForgetFrameTexture(texture.Id);
        };
        _sharedLayout = new SharedPipelineLayout(_context, _bindless.Layout);
        _descriptorArenas = new DescriptorArena[_frames.FramesInFlight];
        for (int i = 0; i < _descriptorArenas.Length; i++) _descriptorArenas[i] = new DescriptorArena(_context);
        _computeArenas = new ComputeDescriptorArena[_frames.FramesInFlight];
        for (int i = 0; i < _computeArenas.Length; i++) _computeArenas[i] = new ComputeDescriptorArena(_context);
        _indirectRing = new IndirectRing(_frames.FramesInFlight);
        _indirectBuffers = new VulkanBuffer?[_frames.FramesInFlight];
        _queryRing = new QueryRing(_context, _frames.Timeline, _frames.FramesInFlight);
        _readbacks = new ReadbackManager(_context, _textures, _frames);
        // A GL query counts across scope ends; a Vulkan one must not be active
        // across vkCmdEndRendering, so the ring suspends and resumes it.
        _targets.ScopeClosing = _queryRing.OnScopeClosing;
        _targets.ScopeClosed = _queryRing.OnScopeClosed;
        _targets.ScopeOpened = _queryRing.OnScopeOpened;
        _shaderCompiler = new ShaderCompiler
        {
            BinaryCache = cacheRoot == null ? null : new ShaderBinaryCache(System.IO.Path.Combine(cacheRoot, "spirv")),
        };
        LoadNativeShaders();
        MirrorValidationMessage(cacheRoot == null
            ? "--- shader cache off"
            : "--- shader cache " + cacheRoot + "; pipeline cache " +
              (pipelineSeed == null ? "cold" : _pipelines.SeedAccepted ? "warm (" + pipelineSeed.Length + " bytes)" : "rejected by the driver") +
              "; pipeline key log " + (_pipelinePersistence?.KeyLog.Count ?? 0) + " entries");
        MirrorValidationMessage("--- pipelines " + (_pipelines.AsyncCompiles
            ? "compile in the background"
            : synchronousPipelines ? "compile blocking (OPTIMUM_VULKAN_SYNC_PIPELINES, a frame capture or the device setting)" : "compile blocking (no pipelineCreationCacheControl)"));
        CreateDefaultAttributeBuffer();
        CreatePlaceholderUniformBuffer();

        // The default target is an ordinary offscreen one, so it exists headless too: the
        // frame's last passes (the blit) write into it, and a headless run - the capture
        // harness, a test driving the post chain - reads it back. Only presenting it needs
        // a surface.
        CreateDefaultFramebuffer((uint)width, (uint)height);

        if (!headless)
        {
            if (!WindowSurface.TryCreate(_context, windowHandle, out SurfaceKHR surface, out string? surfaceError))
            {
                failureReason = surfaceError ?? "could not create a presentation surface";
                return false;
            }

            if (!Swapchain.TryCreate(_context, surface, (uint)width, (uint)height, _vsync, _frames.Timeline,
                    out Swapchain? swapchain, out string? swapchainError, Latency))
            {
                failureReason = swapchainError ?? "could not create a swapchain";
                return false;
            }

            _swapchain = swapchain;
            // Seam S2: VkPresentIdKHR may only be chained when VK_KHR_present_id and its
            // feature were actually enabled; chaining it otherwise is a validation error.
            _swapchain!.PresentIdEnabled = _context.Capabilities.PresentIdEnabled;
            _presentPath = new BlitPresentPath(_context, _textures, DefaultColorTexture);
        }

        failureReason = null!;
        return true;
    }

    private Swapchain? _swapchain;
    private IPresentPath? _presentPath;
    private readonly MissedVsyncDetector _missedVsyncs = new();
    private long _lastPresentReturn;
    private string? _reportedRebuildFailure;
    private bool _vsync = true;

    private VulkanTexture? DefaultColorTexture() => _textures.Get(_defaultColor);
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
    /// Builds the zero-filled buffer that fills any shader-declared uniform block
    /// the client has not supplied a buffer for yet, and every other set 2 binding
    /// a draw does not read (the shared layout's set is written whole).
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
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit,
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
        // Phase 1B step 6: a volatile read; the message is built only when there is one.
        if (_errorCount == 0) return null!;

        lock (_errors)
        {
            if (_errors.Count == 0) return null!;
            string joined = string.Join("\n", _errors);
            _errors.Clear();
            _errorCount = 0;
            return joined;
        }
    }

    /// <summary>
    /// Queues a diagnostic for GetError. Only error-severity messages (the
    /// <see cref="VulkanContext.ErrorPrefix" /> ones) are kept: GetError never
    /// reported anything else, and warnings already reach the trace and the
    /// validation log where they are raised. Safe from any thread.
    /// </summary>
    private void AddDiagnostic(string message)
    {
        if (!message.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal))
        {
            // A native route's refusal is not raised anywhere else: the trace carries it.
            if (RenderTrace.Enabled && !message.StartsWith("[", StringComparison.Ordinal)) RenderTrace.Write("diagnostic: " + message);
            return;
        }

        lock (_errors)
        {
            if (_errors.Count >= MaxQueuedErrors) return;
            _errors.Add(message);
            _errorCount = _errors.Count;
        }
    }

    /// <summary>Queues a diagnostic as the device's own sources do. Tests only.</summary>
    internal void AddDiagnosticForTests(string message) => AddDiagnostic(message);

    // ---------------------------------------------------------------------- frame

    public void BeginFrame()
    {
        // A native pass never spans a frame boundary.
        _nativePass = null;
        _nativeTarget = null;

        // CPU frame interval: start of one frame to the start of the next, so it
        // includes the Frame timeline pacing wait below and everything the client did.
        long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastFrameStart != 0)
        {
            VulkanStats.NoteFrameInterval(
                (frameStart - _lastFrameStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        _lastFrameStart = frameStart;
        ReportShaderLoad();

        // The safe point for pipelines the background worker finished, and for the
        // opportunistic pipeline cache save (a timestamp check; the save runs on a worker).
        _pipelines.PublishCompleted();
        _pipelinePersistence?.Tick(_pipelines, frameStart);

        // The frame that ended: its ReadSelf copies wait on the Frame value it
        // recorded (taken before the ring reserves the next), and its transient
        // leases and bindings end.
        ReleaseReadSelfCopies();
        _readSelfCopies.EndFrame();
        VulkanStats.NoteTransientFrame(_transients.PhysicalBytes + _transients.OptedInBytes,
            _transients.AliasedBytes, _transients.Leases.Count, _transients.AliasedLeaseCount, _readSelfCopies.Live);
        _transients.BeginFrame();

        // Seam S2: the frame's latency identity, before the ring hands out the slot.
        BeginLatencyFrameIdentity();

        FrameSlot slot = _frames.BeginFrame();
        // After the ring's wait and collection, before anything is recorded: freed
        // bindless slots get their placeholder back and queued writes land.
        _bindless?.BeginFrame();
        _readSelfCopies.Collect();
        _frameActive = true;
        _frameCounter++;
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));

        // The slot's previous frame has completed (FrameRing waited for it), so
        // its indirect cursor and descriptor arena reset wholesale.
        BeginIndirectFrame(slot.Index);
        _descriptorArenas[slot.Index].Reset();
        _computeArenas[slot.Index].Reset();
        _resourceAge.NoteFrame(ResourceIds.Highest);
        _dynamicState.Invalidate();

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

    /// <summary>Decision 9's set 1. Tests only.</summary>
    internal BindlessTextureTable BindlessForTests => _bindless!;

    /// <summary>Decision 9's shared pipeline layout. Tests only.</summary>
    internal SharedPipelineLayout SharedLayoutForTests => _sharedLayout!;

    /// <summary>
    /// Records one fullscreen triangle into the bound target with a pipeline built on
    /// the shared layout: the bindless set bound at set 1, <paramref name="pushConstants" />
    /// pushed for vertex and fragment. Every placeholder and every texture in
    /// <paramref name="sampledTextureIds" /> is made shader-readable first, as a draw
    /// does for what it samples. Tests only, until shaders target the shared layout.
    /// </summary>
    internal void DrawBindlessForTests(Pipeline pipeline, int width, int height, byte[] pushConstants,
        params int[] sampledTextureIds)
    {
        if (!_frameActive || _targets.Bound == null || _bindless == null || _sharedLayout == null) return;
        CommandBuffer commandBuffer = Commands;
        Vk api = _context.Api;

        var sampled = new List<int>(sampledTextureIds);
        for (int kind = 0; kind < BindlessKinds.Count; kind++) sampled.Add(_bindless.PlaceholderTextureId((TextureKind)kind));
        foreach (int id in sampled)
        {
            VulkanTexture? texture = _textures.Get(id);
            if (texture == null || texture.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;
            _targets.EndRendering(commandBuffer);
            _textures.Require(_barriers, commandBuffer, texture, Graph.ResourceUsage.SampleFragment);
        }
        _barriers.Flush(commandBuffer);
        _targets.EnsureRendering(commandBuffer);

        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        var viewport = new Viewport(0, 0, width, height, 0, 1);
        api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
        api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        DescriptorSet set = _bindless.Set;
        api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _sharedLayout.Layout,
            (uint)Shaders.SetConvention.TextureSet, 1, &set, 0, null);
        fixed (byte* push = pushConstants)
        {
            api.CmdPushConstants(commandBuffer, _sharedLayout.Layout, SharedPipelineLayout.Stages, 0,
                (uint)pushConstants.Length, push);
        }
        api.CmdDraw(commandBuffer, 3, 1, 0, 0);
        // The raw bind and states above are not what the cache believes the buffer holds.
        _dynamicState.Invalidate();
        ForgetBoundDescriptors();
    }

    // ------------------------------------------------------------------ compute

    /// <summary>The compute programs. Tests only.</summary>
    internal ComputePipelineCache ComputeForTests => _compute;

    /// <summary>A compute program from compiled SPIR-V; see <see cref="ComputeProgramDescription" />.</summary>
    internal int CreateComputeProgram(ComputeProgramDescription description) => _compute.Create(description);

    /// <summary>
    /// Compiles a native compute shader and creates its program; 0, with the compiler's
    /// message in <see cref="GetError" />, when it does not compile.
    /// </summary>
    internal int CreateComputeProgram(string code, string name, ComputeSlot[] slots, uint pushConstantBytes = 0,
        uint localSizeX = 8, uint localSizeY = 8)
    {
        ShaderCompileResult compiled = _shaderCompiler.CompileCompute(code, name);
        if (!compiled.Success)
        {
            AddDiagnostic(VulkanContext.ErrorPrefix + "compute shader '" + name + "' failed to compile: " + compiled.Error);
            return 0;
        }
        return _compute.Create(new ComputeProgramDescription
        {
            Name = name,
            Spirv = compiled.Spirv,
            Slots = slots,
            PushConstantBytes = pushConstantBytes,
            LocalSizeX = localSizeX,
            LocalSizeY = localSizeY,
        });
    }

    /// <summary>Deletes a compute program once no submitted frame can still bind it.</summary>
    internal void DeleteComputeProgram(int programId)
    {
        ComputeProgram? program = _compute.Remove(programId);
        if (program != null) _frames.DeferDeletion(program);
    }

    /// <summary>
    /// A texture compute passes store to: <paramref name="format" /> where the device can
    /// store to and sample it, else a wider format of the same kind (RGBA8 last);
    /// <paramref name="mipLevels" /> levels. The chosen format is the texture's
    /// <see cref="VulkanTexture.Format" />.
    /// </summary>
    internal int CreateStorageTexture(int width, int height, Format format, int mipLevels = 1) =>
        _textures.CreateStorage((uint)Math.Max(1, width), (uint)Math.Max(1, height), format, (uint)Math.Max(1, mipLevels));

    /// <summary>A live texture's description (size, levels, chosen format, usage) for compute pass owners; null when it does not exist.</summary>
    internal VulkanTexture? TextureOf(int textureId) => _textures.Get(textureId);

    private Graph.ComputeImageInfo? ComputeImageInfoOf(int textureId) =>
        _textures.Get(textureId) is { } texture
            ? new Graph.ComputeImageInfo(texture.Width, texture.Height, texture.MipLevels, texture.Cube ? 6u : texture.Layers)
            : null;

    private static readonly SamplerState ComputeNearest = new(Filter.Nearest, Filter.Nearest, SamplerMipmapMode.Nearest,
        SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, 0f, false, 1f, BorderColor.FloatOpaqueBlack,
        Mipmapped: true);

    private static readonly SamplerState ComputeLinear = ComputeNearest with
    {
        MagFilter = Filter.Linear,
        MinFilter = Filter.Linear,
    };

    /// <summary>
    /// Records a compute pass into the frame (<see cref="Graph.ComputePassDeclaration" />):
    /// closes any open rendering scope, lands clears pending on its images, queues one
    /// barrier per binding level range from its access and flushes them as one command,
    /// binds the pipeline for the pass's specialization values and one descriptor set,
    /// and records every dispatch. False, with the reason in <see cref="GetError" />,
    /// when no frame is open or the declaration does not fit its program.
    /// </summary>
    internal bool RecordComputePass(Graph.ComputePassDeclaration pass)
    {
        if (!_frameActive) return false;

        ComputeProgram? program = _compute.Get(pass.ProgramId);
        string? error = program == null
            ? "no compute program " + pass.ProgramId
            : Graph.ComputePassPlanner.Validate(pass, ComputeImageInfoOf);
        if (error == null && program != null)
        {
            foreach (Graph.ComputeBinding binding in pass.Bindings)
            {
                if (!program.TryGetSlot(binding.Binding, out ComputeSlot slot))
                {
                    error = "binding " + binding.Binding + " is not in program '" + program.Name + "'";
                    break;
                }
                bool storage = Graph.ComputePassPlanner.IsStorage(binding.Access);
                if (storage != (slot.Kind == ComputeSlotKind.Storage))
                {
                    error = "binding " + binding.Binding + " is declared " + slot.Kind + " by program '" + program.Name +
                            "' but bound " + binding.Access;
                    break;
                }
                if (storage && (_textures.Get(binding.TextureId)!.Usage & ImageUsageFlags.StorageBit) == 0)
                {
                    error = "binding " + binding.Binding + " stores to texture " + binding.TextureId +
                            ", which was not created as a storage texture";
                    break;
                }
            }
            foreach (ComputeSlot slot in program.Slots)
            {
                if (error != null) break;
                if (Array.FindIndex(pass.Bindings, b => b.Binding == slot.Binding) < 0)
                    error = "program '" + program.Name + "' binding " + slot.Binding + " is not bound";
            }
            foreach (Graph.ComputeDispatch dispatch in pass.Dispatches)
            {
                if (error != null) break;
                if (dispatch.PushConstants is { Length: > 0 } push && push.Length > program.PushConstantBytes)
                    error = "a dispatch pushes " + push.Length + " bytes; program '" + program.Name + "' declares " +
                            program.PushConstantBytes;
            }
        }
        if (error != null)
        {
            AddDiagnostic(VulkanContext.ErrorPrefix + "compute pass '" + pass.Name + "': " + error);
            return false;
        }

        CommandBuffer commandBuffer = Commands;
        Vk api = _context.Api;

        // No rendering scope encloses a dispatch, and a clear promoted into one of the
        // pass's images lands before the pass reads or writes it.
        _targets.EndRendering(commandBuffer);
        foreach (Graph.ComputeBinding binding in pass.Bindings)
        {
            _targets.FlushPendingClears(commandBuffer, _textures.Get(binding.TextureId)!);
        }

        _graph.OpenComputePass(Graph.ComputePassPlanner.Signature(_graph.NameId("compute:" + pass.Name), pass,
            ComputeImageInfoOf));

        foreach (Graph.ComputeBinding binding in pass.Bindings)
        {
            _textures.Require(_barriers, commandBuffer, _textures.Get(binding.TextureId)!, binding.BaseMip,
                binding.MipCount, Graph.ComputePassPlanner.UsageOf(binding.Access));
        }
        _barriers.Flush(commandBuffer);

        Span<ComputeImageWrite> writes = pass.Bindings.Length <= 16
            ? stackalloc ComputeImageWrite[pass.Bindings.Length]
            : new ComputeImageWrite[pass.Bindings.Length];
        for (int i = 0; i < pass.Bindings.Length; i++)
        {
            Graph.ComputeBinding binding = pass.Bindings[i];
            VulkanTexture texture = _textures.Get(binding.TextureId)!;
            bool storage = Graph.ComputePassPlanner.IsStorage(binding.Access);
            writes[i] = new ComputeImageWrite(binding.Binding,
                storage ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler,
                texture.ViewOfMips(binding.BaseMip, binding.MipCount),
                storage ? default : _textures.Samplers.Get(binding.Linear ? ComputeLinear : ComputeNearest),
                storage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal);
        }
        DescriptorSet set = _computeArenas[_frames.Current.Index].Get(program!.SetLayout, writes);
        Pipeline pipeline = _compute.PipelineFor(program, pass.Specialization);

        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, program.Layout, ComputeProgram.PassSet, 1,
            &set, 0, null);

        foreach (Graph.ComputeDispatch dispatch in pass.Dispatches)
        {
            if (dispatch.PushConstants is { Length: > 0 } push)
            {
                fixed (byte* data = push)
                {
                    api.CmdPushConstants(commandBuffer, program.Layout, ShaderStageFlags.ComputeBit, 0, (uint)push.Length,
                        data);
                }
            }
            (uint x, uint y, uint z) = Graph.ComputePassPlanner.Groups(dispatch, pass, ComputeImageInfoOf,
                program.LocalSizeX, program.LocalSizeY);
            if (x == 0 || y == 0 || z == 0) continue;
            api.CmdDispatch(commandBuffer, x, y, z);
            _graph.NoteDispatch();
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("dispatch '" + pass.Name + "' program " + program.Id + " '" + program.Name + "' groups=" +
                    x + "x" + y + "x" + z);
            }
        }
        return true;
    }

    /// <summary>Static meshes on device-local memory through staging (Phase 1B step 5's default). Tests only.</summary>
    internal bool DeviceLocalStaticMeshesForTests
    {
        set => _meshes.DeviceLocalStaticBuffers = value;
    }

    /// <summary>The mesh store. Tests only.</summary>
    internal MeshManager MeshesForTests => _meshes;

    /// <summary>The context (and its allocator). Tests only.</summary>
    internal VulkanContext ContextForTests => _context;

    /// <summary>The texture manager. Tests only.</summary>
    internal TextureManager TexturesForTests => _textures;

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

    /// <summary>
    /// Ends the frame in two submissions. Submit A carries the upload batch and
    /// the frame and signals the Frame timeline; only then does the CPU block on
    /// vkAcquireNextImageKHR, with the whole frame already in flight. Submit B
    /// (the present path: the flipped blit) waits on the frame at
    /// COLOR_ATTACHMENT_OUTPUT and on the acquire semaphore at the image's first
    /// use, and signals the image's present semaphore; then the image is presented.
    /// </summary>
    public void Present()
    {
        if (!_frameActive) return;

        TextureDump.NoteFrame();
        if (TextureDump.Wanted) DumpRequestedTextures();

        // Clears no pass consumed land now: the image keeps them into the next frame.
        _targets.FlushAllPendingClears(_frames.Current.CommandBuffer);
        _targets.EndPass(_frames.Current.CommandBuffer);
        if (_graph.Enabled) _graph.EndFrame();

        // Any open rendering scope has to close before the command buffer ends.
        _targets.EndRendering(_frames.Current.CommandBuffer);

        long presentEntry = System.Diagnostics.Stopwatch.GetTimestamp();
        // A slot first resolved while recording is written before its draws are submitted.
        _bindless?.Flush();
        ulong renderValue = _frames.EndFrame();
        _frameActive = false;
        // Seam S4: the frame's work is queued (Submit A). Stamped before the acquire,
        // which is where the CPU may block, so the render-submit interval is recording
        // time and nothing else.
        Latency.Marker(_latencyFrameId, LatencyMarker.RenderSubmitEnd);
        long frameSubmitted = System.Diagnostics.Stopwatch.GetTimestamp();

        // Headless: nothing to present; the frame is submitted all the same.
        if (_swapchain == null || _presentPath == null) return;

        bool acquired = _swapchain.TryAcquire(out PresentTarget target);
        long acquireReturned = System.Diagnostics.Stopwatch.GetTimestamp();
        bool renderCompletedAtAcquire = _frames.Timeline.FrameCompleted >= renderValue;
        ReportRebuildFailure();
        if (!acquired)
        {
            LastPresentTimingsForTests = new PresentTimings(presentEntry, frameSubmitted, acquireReturned, 0,
                renderValue, 0, renderCompletedAtAcquire, false);
            return;
        }

        CommandBuffer presentCommands = _frames.BeginPresentCommands();
        Checkpoint(presentCommands, CheckpointMarker.PresentBlit(target.ImageIndex, _frameCounter));
        _presentPath.Record(presentCommands, target);
        ulong presentValue = _frames.SubmitPresent(
            target.AcquireSemaphore, _presentPath.AcquireWaitStage, renderValue, target.PresentSemaphore);
        _swapchain.NotePresentSubmitted(target, presentValue);
        long presentSubmitted = System.Diagnostics.Stopwatch.GetTimestamp();

        // Seam S4: PresentStart and PresentEnd bracket vkQueuePresentKHR itself, and the
        // present id the call was given closes the frame's report.
        Latency.Marker(_latencyFrameId, LatencyMarker.PresentStart);
        ulong presentId = _swapchain.Present(target, _latencyFrameId);
        Latency.Marker(_latencyFrameId, LatencyMarker.PresentEnd);
        Latency.OnPresent(_latencyFrameId, presentId);
        LastPresentIdForTests = presentId;
        LastPresentTimingsForTests = new PresentTimings(presentEntry, frameSubmitted, acquireReturned, presentSubmitted,
            renderValue, presentValue, renderCompletedAtAcquire, true);

        long presentReturn = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPresentReturn != 0 && _vsync &&
            _missedVsyncs.NoteInterval((presentReturn - _lastPresentReturn) * 1000.0 / System.Diagnostics.Stopwatch.Frequency) &&
            _swapchain.PromoteToRelaxedFifo())
        {
            MirrorValidationMessage("--- sustained missed vsyncs: swapchain promoted to FIFO_RELAXED");
        }
        _lastPresentReturn = presentReturn;
    }

    /// <summary>Stopwatch timestamps of one Present, for PresentDecouplingTests.</summary>
    internal readonly record struct PresentTimings(
        long PresentEntry, long FrameSubmitted, long AcquireReturned, long PresentSubmitted,
        ulong RenderValue, ulong PresentValue, bool RenderCompletedAtAcquire, bool Presented);

    /// <summary>The last Present's timings. Tests only.</summary>
    internal PresentTimings LastPresentTimingsForTests { get; private set; }

    /// <summary>The swapchain, null when headless. Tests only.</summary>
    internal Swapchain? SwapchainForTests => _swapchain;

    /// <summary>The present path's acquire wait stage. Tests only.</summary>
    internal PipelineStageFlags PresentAcquireWaitStageForTests =>
        _presentPath?.AcquireWaitStage ?? PresentWaitStages.BlitAcquireWait;

    private void ReportRebuildFailure()
    {
        string? failure = _swapchain?.RebuildFailure;
        if (failure != null && failure != _reportedRebuildFailure)
        {
            AddDiagnostic("swapchain recreation failed: " + failure);
        }
        _reportedRebuildFailure = failure;
    }

    /// <summary>
    /// A new window size: the default framebuffer is rebuilt now (its old images
    /// retire on the timelines), the swapchain at the next acquire. Nothing waits.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (_swapchain == null || width <= 0 || height <= 0) return;
        if ((uint)width == _windowWidth && (uint)height == _windowHeight) return;

        DestroyDefaultFramebuffer();
        CreateDefaultFramebuffer((uint)width, (uint)height);
        _swapchain.RequestRebuild(_windowWidth, _windowHeight, _vsync);
    }

    public void SetVSync(bool enabled)
    {
        if (_vsync == enabled) return;
        _vsync = enabled;
        _missedVsyncs.Reset();
        _swapchain?.RequestRebuild(_windowWidth, _windowHeight, _vsync);
    }

    private CommandBuffer Commands => _frames.Current.CommandBuffer;

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
            AddDiagnostic($"{stageName}: shader source exceeds {MaxShaderSourceBytes} bytes and was rejected");
            return false;
        }
        if (shader.Code.IndexOf('\0') >= 0 || (shader.PrefixCode?.IndexOf('\0') ?? -1) >= 0)
        {
            AddDiagnostic($"{stageName}: shader source contains a NUL byte and was rejected");
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
            AddDiagnostic($"shader program '{program.PassName}' has no stages");
            return 0;
        }

        // The seam (docs/vulkan-native-shaders.md section 8): a program the manifest has links from its
        // SPIR-V; one it has not links through the rewriter; one it has but cannot serve (a bad hash, an
        // unreadable define, a module the driver refuses) links through the rewriter and counts as failed.
        string passName = program.PassName ?? "";
        ShaderProgramResources? resources = null;
        TranslatedProgram? native = null;
        bool nativeFailed = false;
        string nativeDetail = "";
        if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(passName))
        {
            // A mod replaced this program's GLSL (or the scan cannot rule it out): the native SPIR-V would draw
            // vanilla over the mod, so the mod's source goes through the rewriter. Rewritten, not failed.
            ReportModOverride(passName);
        }
        else if (_nativeShaders != null)
        {
            NativeShaderLibrary.Outcome outcome = _nativeShaders.TryLink(passName, stages, out native, out nativeDetail);
            nativeFailed = outcome == NativeShaderLibrary.Outcome.Failed;
            if (outcome != NativeShaderLibrary.Outcome.Native) native = null;
        }

        int programId = 0;
        if (native != null)
        {
            programId = _nextProgramId++;
            try
            {
                resources = new ShaderProgramResources(_context, programId, native, _sharedLayout!.Layout);
            }
            catch (InvalidOperationException error)
            {
                nativeFailed = true;
                nativeDetail += ": " + error.Message;
            }
        }
        if (nativeFailed) ReportNativeFailure(passName, nativeDetail);

        TranslatedProgram translated;
        if (resources != null)
        {
            translated = native!;
            _nativeLinks++;
        }
        else
        {
            if (nativeFailed) _failedNativeLinks++;
            else _rewrittenLinks++;

            // The include files the registry assembled the program from decide which
            // of its uniforms read the shared frame block. A program built any other
            // way - a mod's, a test's - keeps every uniform to itself.
            translated = ShaderTranslator.Translate(stages, _shaderCompiler, null,
                (program as Vintagestory.Client.NoObf.ShaderProgramBase)?.includes);
            if (!translated.Success)
            {
                foreach (string error in translated.Errors)
                {
                    AddDiagnostic($"{program.PassName}: {error}");
                }
                return 0;
            }

            if (programId == 0) programId = _nextProgramId++;
        }
        if (RenderTrace.Enabled)
        {
            RenderTrace.DumpProgramSources(program.PassName, translated);
            RenderTrace.Write("program " + programId + " '" + program.PassName + "' uniformBlockBytes=" +
                translated.Layout.BlockSize + " pushBytes=" + translated.Layout.PushConstantSize +
                (translated.IsNative ? " native [" + nativeDetail + "]" : ""));
            foreach (UniformMember member in translated.Layout.Members)
            {
                RenderTrace.Write("  uniform " + member.Name + " offset=" + member.Offset +
                    " type=" + member.Type + " count=" + member.ArrayLength);
            }
        }
        resources ??= new ShaderProgramResources(_context, programId, translated, _sharedLayout!.Layout);
        _programs[programId] = resources;
        // The variant a native program was linked for (TryLink reports the key as its detail),
        // so a native pipeline request can state the variant it expects.
        if (resources.IsNative) _programVariants[programId] = nativeDetail;
        _programNames[programId] = program.PassName ?? "";
        // Pipelines an earlier launch used with this exact program start compiling now.
        int prewarming = _pipelines.PrewarmFor(resources);
        if (prewarming > 0 && RenderTrace.Enabled)
        {
            RenderTrace.Write("program " + programId + " prewarming " + prewarming + " pipelines");
        }
        return programId;
    }

    /// <summary>
    /// Loads the native shaders once, at device start: the manifest beside the renderer assembly, the directory a
    /// test named, or the source tree <c>OPTIMUM_VK_SHADER_SOURCE</c> names. One log line says what came of it.
    /// </summary>
    private void LoadNativeShaders()
    {
        string? assemblyDirectory = null;
        try
        {
            assemblyDirectory = System.IO.Path.GetDirectoryName(typeof(VulkanDevice).Assembly.Location);
        }
        catch (Exception error) when (error is ArgumentException or System.IO.PathTooLongException)
        {
            // An assembly loaded from bytes has no location; the resolution below reports it.
        }

        (NativeShaderLibrary.Mode mode, string? path, string reason) = NativeShaderLibrary.Resolve(
            NativeShadersEnabled, NativeShaderDirectory,
            Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable),
            Environment.GetEnvironmentVariable(NativeShaderLibrary.SourceVariable),
            assemblyDirectory);

        _nativeShaders = mode switch
        {
            NativeShaderLibrary.Mode.Directory => NativeShaderLibrary.Load(path!, _shaderCompiler.Identity, out reason),
            NativeShaderLibrary.Mode.Source => NativeShaderLibrary.BuildFromSource(path!, _shaderCompiler, out reason),
            _ => null,
        };

        string enabledVariable = Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable) ?? "";
        bool ignoreScan = IgnoreModShaderScan ?? NativeShaderLibrary.IgnoresModScan(enabledVariable);
        _modShaderScan = ignoreScan ? null : ShaderProgramOverriddenByMods;

        NativeShaderStatus = _nativeShaders == null
            ? "off: " + reason
            : _nativeShaders.Manifest.Programs.Count + " programs from " + _nativeShaders.Origin +
              (reason.Length > 0 ? "; " + reason : "");
        if (_nativeShaders != null && ignoreScan && ShaderProgramOverriddenByMods != null)
        {
            NativeShaderStatus += "; mod shader scan ignored (" + NativeShaderLibrary.EnabledVariable + "=" + NativeShaderLibrary.ForceValue + ")";
        }
        else if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(AllShaderProgramsEntry))
        {
            NativeShaderStatus += "; the mod shader scan makes every program rewriter-only (no report, a failed scan, or a shaderincludes override)";
        }
        LogShaderLine("[Optimum] shaders: native " + NativeShaderStatus);
    }

    /// <summary>The scan's entry for every program (<c>OptimumConfig.AllShaderPrograms</c>, the scanner's <c>AllPrograms</c>).</summary>
    internal const string AllShaderProgramsEntry = "all";

    /// <summary>Logs, once per program the manifest has, that the mod shader scan sent it to the rewriter.</summary>
    private void ReportModOverride(string passName)
    {
        if (_nativeShaders?.Manifest.FindProgram(passName) == null) return;
        bool all = _modShaderScan!(AllShaderProgramsEntry);
        lock (_modOverrideLogged)
        {
            if (!_modOverrideLogged.Add(passName)) return;
        }
        string line = "[Optimum] shaders: native '" + passName + "' linked through the rewriter: " +
            (all ? "the mod shader scan makes every program rewriter-only" : "a mod replaces its GLSL (launcher shader scan)");
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    private void ReportNativeFailure(string passName, string detail)
    {
        string line = "[Optimum] shaders: native '" + passName + "' failed, linked through the rewriter: " + detail;
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    /// <summary>
    /// The line after a shader load: the programs linked since the last report. ShaderRegistry links every program
    /// of a load or reload in one synchronous call, so the first frame after links is the end of that load.
    /// </summary>
    private void ReportShaderLoad()
    {
        int native = _nativeLinks - _nativeLinksReported;
        int rewritten = _rewrittenLinks - _rewrittenLinksReported;
        int failed = _failedNativeLinks - _failedNativeLinksReported;
        if (native + rewritten + failed == 0) return;

        _nativeLinksReported = _nativeLinks;
        _rewrittenLinksReported = _rewrittenLinks;
        _failedNativeLinksReported = _failedNativeLinks;
        LogShaderLine("[Optimum] shaders: " + native + " native, " + rewritten + " rewritten, " + failed + " failed");
        VulkanStats.NoteShaderLoad(native, rewritten, failed);
    }

    private static void LogShaderLine(string line)
    {
        Console.WriteLine(line);
        MirrorValidationMessage("--- " + line);
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
        // No background compile may still be reading its modules or layout.
        _pipelines.CancelProgram(program);
        ForgetNativePipelines(programId);
        _frames.DeferDeletion(program);
    }

    public int GetUniformLocation(int programId, string name) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? program.LocationOf(name) : -1;

    // ------------------------------------------------------------------- uniforms

    private void Write(int programId, int location, ReadOnlySpan<byte> data)
    {
        // A member of the shared frame block: one shadow for every program.
        if (ShaderProgramResources.IsFrameLocation(location))
        {
            WriteFrameGlobal(location - ShaderProgramResources.FrameLocationBase, data);
            return;
        }

        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            // A native program's push member (a DRAW uniform): kept per program, pushed per draw.
            if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, data);
            else program.SetUniform(location, data);
        }
    }

    /// <summary>
    /// Writes into the shared frame block. Use() rewrites the same values on every
    /// program switch, so the common case is bytes that already match: a comparison
    /// and nothing else. A real change bumps the version and the next draw takes a
    /// new snapshot.
    /// </summary>
    private void WriteFrameGlobal(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > _frameGlobals.Length) return;

        Span<byte> destination = _frameGlobals.AsSpan(offset, data.Length);
        if (data.SequenceEqual(destination)) return;

        data.CopyTo(destination);
        _frameGlobalsVersion++;
    }

    /// <summary>A copy of a linked program's record shadow, initializers included; null for an unknown program. For tests.</summary>
    internal byte[]? ProgramRecordForTests(int programId) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? (byte[])program.UniformShadow.Clone() : null;

    /// <summary>A copy of the shared frame block's current bytes. For tests.</summary>
    internal byte[] FrameGlobalsForTests => (byte[])_frameGlobals.Clone();

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

    /// <summary>
    /// A post-chain colour texture (framebuffer slots in
    /// <see cref="Graph.TransientAllocator.PostChainSlots" />): created in the Transient
    /// memory pool class and registered with the transient allocator. Until the frame
    /// graph binds it (<see cref="BindTransientForFrame" />) it behaves like any texture.
    /// </summary>
    public int CreateTransientTexture2D(int width, int height, EnumTextureInternalFormat internalFormat,
        int framebufferSlot)
    {
        int id = _textures.Create((uint)width, (uint)height, GlEnums.TextureFormatFrom(internalFormat),
            poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, (int)internalFormat);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>Registers (or re-tags) a texture as the transient of client framebuffer slot <paramref name="framebufferSlot" />.</summary>
    public void OptInTransient(int textureId, int framebufferSlot) => _transients.OptIn(textureId, framebufferSlot);

    /// <summary><see cref="CreateTransientTexture2D" /> with a raw GL internal format token, no pixels.</summary>
    public int CreateTransientTexture2DRaw(int width, int height, int glInternalFormat, int framebufferSlot)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, glInternalFormat);
        RenderTrace.TextureCreated(id, width, height, format, IntPtr.Zero, 0);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>
    /// Serves a texture for passes [<paramref name="firstPass" />, <paramref name="lastPass" />]
    /// of the current frame through the transient allocator and returns the texture id
    /// that backs it (itself unless aliasing is on). Call after BeginFrame, in pass order.
    /// </summary>
    public int BindTransientForFrame(int textureId, int firstPass, int lastPass) =>
        _transients.Bind(textureId, firstPass, lastPass);

    /// <summary>The transient allocator the frame graph acquires physical images from.</summary>
    internal Graph.TransientAllocator Transients => _transients;

    /// <summary>The ReadSelf copy pool. Tests only.</summary>
    internal Graph.FeedbackCopyPool ReadSelfCopiesForTests => _readSelfCopies;

    /// <summary>Forces transient aliasing on or off before Initialize (default: <c>OPTIMUM_VULKAN_ALIAS</c>).</summary>
    internal bool? TransientAliasingOverride { get; set; }

    private int CreateReadSelfCopy(Graph.FeedbackCopyDesc desc) =>
        _textures.Create(desc.Width, desc.Height, desc.Format, layers: desc.Layers, cube: desc.Cube,
            generateMipmaps: desc.MipLevels > 1, poolClass: MemoryPoolClass.Transient);

    /// <summary>Gives the previous draw's ReadSelf copies back to the pool.</summary>
    private void ReleaseReadSelfCopies()
    {
        if (_sampledTextureOverrides.Count == 0) return;
        foreach (int copy in _sampledTextureOverrides.Values) _readSelfCopies.Release(copy);
        _sampledTextureOverrides.Clear();
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
        EnumTexturePixelFormat pixelFormat, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels,
            pixelFormat == EnumTexturePixelFormat.Red ? 1 : 4);
    }

    public void UploadTexture2DRaw(
        int textureId, int level, int x, int y, int width, int height, IntPtr pixels, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0) return;
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels, bytesPerPixel);
    }

    public void GenerateMipmaps(int textureId)
    {
        FlushPendingClears(textureId);
        _textures.GenerateMipmaps(textureId);
    }

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
        if (_sampledTextureOverrides.Remove(textureId, out int copy)) _readSelfCopies?.Release(copy);
        _transients?.Forget(textureId);
        _textures.RestoreBinding(textureId);
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null)
        {
            _descriptors.Release(texture.Id);
            _targets.DropPendingClears(texture);
        }
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

    public void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y,
        int width, int height, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, 0, x, y, (uint)width, (uint)height, pixels, 4, (uint)layer);
    }

    public void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y,
        int width, int height, short[] pixels)
    {
        FlushPendingClears(textureId);
        _textures.UploadNormalizedShorts(textureId, level, x, y, width, height, pixels);
    }

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

    public void DeleteFramebuffer(int framebufferId)
    {
        _targets.Delete(framebufferId);
        FramebufferDeleted?.Invoke(framebufferId);
    }

    /// <summary>The platform whose graphics this device is; null for a bare device (the GPU tests).</summary>
    internal Platform.VulkanClientPlatform? OwnerPlatform { get; set; }

    /// <summary>
    /// Raised after a framebuffer is deleted. Its id is reused by the next one created, so a
    /// record keyed on it (the stated draw buffers) has to forget it here.
    /// </summary>
    internal Action<int>? FramebufferDeleted;

    /// <summary>Whether the frame graph records this device's frames. Change only between frames.</summary>
    internal bool FrameGraphEnabled
    {
        get => _graph.Enabled;
        set => _graph.Enabled = value;
    }

    /// <summary>The frame graph's totals. Tests only.</summary>
    internal Graph.FrameGraph FrameGraphForTests => _graph;

    /// <summary>The bound render target's id (0 before any bind).</summary>
    internal int BoundFramebufferId => _targets.Bound?.Id ?? 0;

    /// <summary>The render target standing for the default framebuffer (0 when headless).</summary>
    internal int DefaultFramebufferId => _defaultFramebuffer;

    /// <summary>
    /// Ends the pass a render stage left open (closes its scope): a native draw recorded with
    /// keepScope stays in the stage's declaration until here. No-op with the frame graph off.
    /// </summary>
    internal void EndStagePass()
    {
        if (_frameActive) _targets.EndPass(Commands);
    }

    /// <summary>Lands the clears promoted into a texture before it is written some other way.</summary>
    private void FlushPendingClears(int textureId)
    {
        if (!_frameActive || !_graph.HasPendingClears) return;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _targets.FlushPendingClears(Commands, texture);
    }

    /// <summary>
    /// A colour clear of one attachment of an explicit target, outside every native pass: the
    /// promoted LOAD_OP_CLEAR of the next pass on it, or an attachment clear inside an open scope.
    /// The caller has applied the draw buffers and colour mask it stated (VulkanClientPlatform).
    /// </summary>
    internal void ClearNativeColor(int framebufferId, int attachment, float r, float g, float b, float a)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearColor attachment=" + attachment + " target=" + _targets.Bound!.Id +
                " rgba=" + r + "," + g + "," + b + "," + a);
        }
        _targets.ClearColor(Commands, attachment, r, g, b, a);
    }

    /// <summary>The depth clear of an explicit target; the caller has applied the stated depth mask.</summary>
    internal void ClearNativeDepth(int framebufferId, float depth)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled) RenderTrace.Write("clearDepth target=" + _targets.Bound!.Id + " depth=" + depth);
        _targets.ClearDepth(Commands, depth);
    }

    private bool BindForNativeClear(int framebufferId)
    {
        EndNativePass();
        if (!_frameActive) return false;
        int id = ResolveNativeFramebuffer(framebufferId);
        VulkanFramebuffer? target = _targets.Get(id);
        if (target == null) return false;
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(Commands, id);
        return true;
    }

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

    // ------------------------------------------------------------------- barriers

    /// <summary>
    /// The frame thread's barriers for sampled textures and feedback snapshots:
    /// every texture a draw samples moves in one barrier command.
    /// </summary>
    private Graph.BarrierBatcher _barriers = null!;

    private void SnapshotColorAttachment(CommandBuffer commandBuffer, int textureId, VulkanTexture source)
    {
        if (_sampledTextureOverrides.ContainsKey(textureId)) return;

        _targets.EndRendering(commandBuffer);
        // A pooled ReadSelf copy for this pass (FeedbackCopyPool).
        int copyId = _readSelfCopies.Acquire(new Graph.FeedbackCopyDesc(source.Width, source.Height, source.Format,
            source.MipLevels, source.Layers, source.Cube));
        VulkanStats.NoteReadSelfCopy();
        VulkanTexture copy = _textures.Get(copyId)!;
        copy.State = source.State;

        // Source, copy, and any texture this draw already queued: one command.
        _textures.Require(_barriers, commandBuffer, source, Graph.ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commandBuffer, copy, Graph.ResourceUsage.TransferDst);
        _barriers.Flush(commandBuffer);
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
        _textures.Require(_barriers, commandBuffer, copy, Graph.ResourceUsage.SampleFragment);
        _barriers.Flush(commandBuffer);
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
        AddDiagnostic(SanitiseForClientLog(message));
        MirrorValidationMessage(message);
    }

    // ----------------------------------------------- shared layout: what is bound

    /// <summary>
    /// What the current recording holds at each set of the shared pipeline layout (plan
    /// decision 9), and the push bytes it last received. Every program's pipelines are
    /// built against the one layout, so binding another program's pipeline disturbs none
    /// of it: set 1 is bound once per recording, set 0 and set 2 only when their set or
    /// dynamic offset changes, and push constants only when the bytes do. A new recording
    /// (another serial) or a raw bind outside this path (<see cref="ForgetBoundDescriptors" />)
    /// starts over.
    /// </summary>
    private ulong _boundSerial;
    private DescriptorSet _boundFrameSet;
    private uint _boundFrameOffset;
    private bool _boundTextureSet;
    private DescriptorSet _boundStorageSet;
    private uint _boundRecordOffset;
    private int _pushedLength;
    private readonly byte[] _pushShadow = new byte[SetConvention.PushConstantBytes];
    private readonly byte[] _pushedBytes = new byte[SetConvention.PushConstantBytes];

    /// <summary>
    /// Set 0's frame textures as the last draw that samples each resolved them, in
    /// <see cref="SetConvention.FrameTextures" /> order; an empty value is that binding's
    /// placeholder. Only a program that samples a frame texture reads the binding, and it
    /// resolves it again first, so a value left from another program is never read.
    /// A texture's deletion clears its values (any thread, hence the lock).
    /// </summary>
    private readonly SamplerBindingValue[] _frameTextureValues = new SamplerBindingValue[SetConvention.FrameTextures.Length];

    /// <summary>The client texture id each <see cref="_frameTextureValues" /> entry was resolved from.</summary>
    private readonly int[] _frameTextureIds = new int[SetConvention.FrameTextures.Length];
    private readonly object _frameTextureLock = new();

    /// <summary>Whether set 1's placeholders have been put in the layout their descriptors name.</summary>
    private bool _bindlessPlaceholdersReadable;

    /// <summary>Binds of set 0 and set 1 this device recorded. Tests only.</summary>
    internal long FrameSetBindsForTests { get; private set; }
    internal long TextureSetBindsForTests { get; private set; }

    /// <summary>Forgets what the recording holds bound, after a bind this path did not make.</summary>
    private void ForgetBoundDescriptors() => _boundSerial = 0;

    private void SyncBoundDescriptors(CommandBuffer commandBuffer)
    {
        FrameSlot slot = _frames.Current;
        ulong serial = slot.CommandBuffer.Handle == commandBuffer.Handle ? slot.RecordingSerial : 0;
        if (serial != 0 && serial == _boundSerial) return;

        _boundSerial = serial;
        _boundFrameSet = default;
        _boundFrameOffset = 0;
        _boundTextureSet = false;
        _boundStorageSet = default;
        _boundRecordOffset = 0;
        _pushedLength = 0;
    }

    private void ForgetFrameTexture(ulong textureId)
    {
        lock (_frameTextureLock)
        {
            for (int i = 0; i < _frameTextureValues.Length; i++)
            {
                if (_frameTextureValues[i].Resource != textureId) continue;
                _frameTextureValues[i] = default;
                _frameTextureIds[i] = 0;
            }
        }
    }

    private static int FrameTextureIndex(int binding)
    {
        for (int i = 0; i < SetConvention.FrameTextures.Length; i++)
        {
            if (SetConvention.FrameTextures[i].Value == binding) return i;
        }
        throw new ArgumentOutOfRangeException(nameof(binding), binding, "not a frame texture binding");
    }

    private static TextureKind KindOf(SamplerBinding sampler)
    {
        if (!sampler.IsFrameTexture) return sampler.Kind;
        BindlessKinds.TryFromGlslType(sampler.TypeName, out TextureKind kind);
        return kind;
    }

    /// <summary>Set 0's placeholder for a frame texture: the bindless table's placeholder of the declared kind.</summary>
    private SamplerBindingValue FrameTexturePlaceholder(int index)
    {
        SetConvention.Binding frame = SetConvention.FrameTextures[index];
        BindlessKinds.TryFromGlslType(frame.GlslType, out TextureKind kind);
        VulkanTexture placeholder = _textures.Get(_bindless!.PlaceholderTextureId(kind))!;
        return new SamplerBindingValue((uint)frame.Value, placeholder.View,
            _textures.Samplers.Get(BindlessKinds.EffectiveState(SamplerState.Default, kind)), placeholder.Id);
    }

    /// <summary>
    /// Takes a snapshot of the shared frame block when it changed and returns its ring
    /// offset. Every draw that follows in the frame reads the same snapshot and only the
    /// dynamic offset moves when a value does.
    /// </summary>
    private uint SnapshotFrameGlobals(ShaderProgramResources program)
    {
        if (_frameGlobalsSnapshotFrame == _frameCounter && _frameGlobalsSnapshotVersion == _frameGlobalsVersion)
        {
            return _frameGlobalsSnapshotOffset;
        }
        if (!_frames.Current.TryAllocateUniforms(_frameGlobals.Length, out RingAllocation allocation))
        {
            ReportUniformExhaustion(program, "the shared frame block");
            return 0;
        }
        fixed (byte* source = _frameGlobals)
        {
            System.Buffer.MemoryCopy(source, (void*)allocation.Pointer, _frameGlobals.Length, _frameGlobals.Length);
        }
        _frameGlobalsSnapshotFrame = _frameCounter;
        _frameGlobalsSnapshotVersion = _frameGlobalsVersion;
        _frameGlobalsSnapshotOffset = allocation.Offset;
        return allocation.Offset;
    }

    /// <summary>
    /// Binds the three sets of the shared layout for a draw whose push shadow already holds
    /// its sampler slots: the frame set, the texture set with the push block, and the storage
    /// set. Every native draw binds through here.
    /// </summary>
    private void BindProgramSets(CommandBuffer commandBuffer, ShaderProgramResources program, int meshId)
    {
        Vk api = _context.Api;
        SharedPipelineLayout shared = _sharedLayout!;
        SyncBoundDescriptors(commandBuffer);

        // Set 0: the frame block and the fixed frame textures.
        if (program.Interface.UsesFrameBlock || program.Interface.UsesFrameTextures)
        {
            uint offset = SnapshotFrameGlobals(program);
            var samplers = new SamplerBindingValue[SetConvention.FrameTextures.Length];
            lock (_frameTextureLock)
            {
                for (int i = 0; i < samplers.Length; i++)
                {
                    samplers[i] = _frameTextureValues[i].View.Handle != 0 ? _frameTextureValues[i] : FrameTexturePlaceholder(i);
                }
            }
            var contents = new DescriptorSetContents(0, SetConvention.FrameSet, samplers,
                new[]
                {
                    new BufferBindingValue((uint)SetConvention.FrameGlobalsBinding, _frames.UniformBuffer, 0,
                        (ulong)_frameGlobals.Length),
                });
            DescriptorSet frameSet = GetDescriptorSet(contents, shared.FrameSetLayout);
            if (frameSet.Handle != _boundFrameSet.Handle || offset != _boundFrameOffset)
            {
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
                    (uint)SetConvention.FrameSet, 1, &frameSet, 1, &offset);
                _boundFrameSet = frameSet;
                _boundFrameOffset = offset;
                FrameSetBindsForTests++;
            }
        }

        // Set 1 once per recording, and the slot indices when they changed.
        int pushSize = program.Interface.PushConstantSize;
        if (pushSize > 0)
        {
            if (!_boundTextureSet)
            {
                DescriptorSet textureSet = _bindless!.Set;
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
                    (uint)SetConvention.TextureSet, 1, &textureSet, 0, null);
                _boundTextureSet = true;
                TextureSetBindsForTests++;
            }
            if (pushSize > _pushedLength ||
                !_pushShadow.AsSpan(0, pushSize).SequenceEqual(_pushedBytes.AsSpan(0, pushSize)))
            {
                fixed (byte* push = _pushShadow)
                {
                    api.CmdPushConstants(commandBuffer, shared.Layout, SharedPipelineLayout.Stages, 0, (uint)pushSize, push);
                }
                _pushShadow.AsSpan(0, pushSize).CopyTo(_pushedBytes);
                _pushedLength = Math.Max(_pushedLength, pushSize);
                VulkanStats.NotePushConstantWrite();
            }
        }

        if (program.Interface.UsesStorageSet)
        {
            BindStorageSet(commandBuffer, program, meshId);
        }
    }

    /// <summary>
    /// Set 2, built per draw against the shared layout: the program record at its
    /// dynamic binding (a ring snapshot when the shadow changed), each named block from
    /// the client's UBO snapshot as a std140 storage buffer at the snapshot's offset,
    /// each storage block from the mesh's vertex buffer, and the zero-filled placeholder
    /// buffer at every binding the program does not read. A set naming a ring offset is
    /// new every frame, so it comes from the slot's arena.
    /// </summary>
    private void BindStorageSet(CommandBuffer commandBuffer, ShaderProgramResources program, int meshId)
    {
        SharedPipelineLayout shared = _sharedLayout!;
        VulkanBuffer placeholder = _placeholderUniforms!;
        var buffers = new BufferBindingValue[SetConvention.StorageSetBindingCount];
        for (int binding = 0; binding < buffers.Length; binding++)
        {
            buffers[binding] = new BufferBindingValue((uint)binding, placeholder.Handle, 0, placeholder.Size, placeholder.Id);
        }

        bool namesRingOffset = false;
        uint recordOffset = 0;
        const int record = SetConvention.ProgramRecordBinding;

        if (program.Interface.HasUniformBlock)
        {
            if (program.HasSnapshotFor(_frameCounter))
            {
                // Nothing written since this program's last draw this frame took its snapshot.
                recordOffset = program.SnapshotOffset;
            }
            else if (_frames.Current.TryAllocateUniforms(program.UniformShadow.Length, out RingAllocation allocation))
            {
                fixed (byte* source = program.UniformShadow)
                {
                    System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                        program.UniformShadow.Length, program.UniformShadow.Length);
                }
                recordOffset = allocation.Offset;
                program.NoteSnapshot(_frameCounter, allocation.Offset);
            }
            else
            {
                // The draw reads offset zero of the ring, which is some other draw's record:
                // wrong, and for a shader that loops on a uniform count, possibly fatal.
                ReportUniformExhaustion(program, "its program record");
            }
            buffers[record] = new BufferBindingValue(record, _frames.UniformBuffer, 0, (ulong)program.UniformShadow.Length);
        }

        // A block the shader declares is fed by whichever UBO the client created under
        // that name; one it has not created yet reads the placeholder's zeroes.
        foreach (BlockBinding block in program.Interface.UniformBlocks)
        {
            ClientUniformBuffer? ubo = null;
            if (_boundUniformBuffers.TryGetValue(block.BlockName, out int handle)) _uniformBuffers.TryGetValue(handle, out ubo);
            if (ubo == null) continue;

            if (TrySnapshotClientBlock(ubo, program, out uint blockOffset))
            {
                buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, _frames.UniformBuffer,
                    blockOffset, (ulong)ubo.Shadow.Length);
                namesRingOffset = true;
                continue;
            }

            // No room left in the ring. Rather than aliasing a buffer every remaining draw
            // would share - the exact bug the ring exists to fix - this draw gets its own
            // transient copy. Counted, so a scene that lives in this path shows in the stats.
            VulkanStats.NoteUniformOverflow();
            var overflow = new VulkanBuffer(_context, (ulong)ubo.Shadow.Length,
                BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            fixed (byte* shadow = ubo.Shadow)
            {
                System.Buffer.MemoryCopy(shadow, (void*)overflow.Mapped, ubo.Shadow.Length, ubo.Shadow.Length);
            }
            buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, overflow.Handle, 0, overflow.Size, overflow.Id);
            namesRingOffset = true;
            // Released and deferred in that order: no cached set naming it may outlive it.
            _descriptors.Release(overflow.Id);
            _frames.DeferDeletion(overflow);
        }

        // The SSBO chunk path reads its vertices from the mesh's xyz buffer by gl_VertexIndex.
        foreach (BlockBinding block in program.Interface.StorageBlocks)
        {
            VulkanBuffer? buffer = meshId > 0 ? _meshes.BufferOf(meshId, MeshManager.BufferXyz) : null;
            if (buffer != null)
            {
                buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, buffer.Handle, 0, buffer.Size, buffer.Id);
            }
            else if (RenderTrace.Enabled)
            {
                RenderTrace.Write("storage block '" + block.BlockName + "' on program " + program.ProgramId +
                    " has no mesh buffer (mesh " + meshId + "); it reads the placeholder");
            }
        }

        if (RenderTrace.Enabled && meshId > 0)
        {
            // Diagnostic: what this draw binds, so the emulated and the native route can be diffed per draw.
            var trace = new System.Text.StringBuilder("  sets program=").Append(program.ProgramId).Append(" mesh=").Append(meshId)
                .Append(" record=").Append(recordOffset);
            static ulong Fnv(ReadOnlySpan<byte> bytes)
            {
                ulong h = 14695981039346656037UL;
                foreach (byte b in bytes) h = (h ^ b) * 1099511628211UL;
                return h;
            }
            foreach (BlockBinding block in program.Interface.UniformBlocks)
            {
                trace.Append(' ').Append(block.BlockName).Append('@').Append(block.Binding).Append('=')
                    .Append(buffers[block.Binding].Offset).Append('/').Append(buffers[block.Binding].Resource);
                if (_boundUniformBuffers.TryGetValue(block.BlockName, out int traceHandle) &&
                    _uniformBuffers.TryGetValue(traceHandle, out ClientUniformBuffer? traceUbo))
                {
                    trace.Append(" h").Append(traceHandle).Append(":#").Append(Fnv(traceUbo.Shadow).ToString("x16"));
                }
                else
                {
                    trace.Append(" (unbound)");
                }
            }
            trace.Append(" rec#").Append(Fnv(program.UniformShadow).ToString("x16"))
                .Append(" push#").Append(Fnv(_pushShadow).ToString("x16"));
            trace.Append(" pushBytes=").Append(program.PushShadow?.Length ?? 0)
                .Append(" frameBlock=").Append(program.Interface.UsesFrameBlock)
                .Append(" frame@").Append(_frameGlobalsSnapshotOffset).Append(" v").Append(_frameGlobalsVersion)
                .Append('#').Append(Fnv(_frameGlobals).ToString("x16"));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            trace.Append(" recordBytes=").Append(program.UniformShadow.Length);
            foreach (UniformMember member in program.Interface.Members)
            {
                if (member.Name is not ("projectionMatrix" or "viewMatrix" or "modelMatrix")) continue;
                if (member.Offset < 0 || member.Offset + 64 > program.UniformShadow.Length) continue;
                var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(program.UniformShadow.AsSpan(member.Offset, 64));
                trace.Append(' ').Append(member.Name).Append('@').Append(member.Offset).Append("=[")
                    .Append(f[0].ToString("G4", inv)).Append(',').Append(f[5].ToString("G4", inv)).Append(";t=")
                    .Append(f[12].ToString("G4", inv)).Append(',').Append(f[13].ToString("G4", inv)).Append(',').Append(f[14].ToString("G4", inv)).Append(']');
            }
            RenderTrace.Write(trace.ToString());
        }

        var contents = new DescriptorSetContents(0, SetConvention.StorageSet, Array.Empty<SamplerBindingValue>(), buffers);
        DescriptorSet storageSet = namesRingOffset
            ? _descriptorArenas[_frames.Current.Index].Get(contents, shared.StorageSetLayout)
            : GetDescriptorSet(contents, shared.StorageSetLayout);
        if (storageSet.Handle == _boundStorageSet.Handle && recordOffset == _boundRecordOffset) return;

        _context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
            (uint)SetConvention.StorageSet, 1, &storageSet, 1, &recordOffset);
        _boundStorageSet = storageSet;
        _boundRecordOffset = recordOffset;
        VulkanStats.NoteStorageSetBind();
    }

    /// <summary>
    /// Records the dynamic state a draw needs and the recording does not already hold. The
    /// values come from the native pipeline's fixed state and the pass's viewport and scissor.
    /// </summary>
    private void EmitDynamicState(CommandBuffer commandBuffer, DynamicStateValues values, ulong serial,
        ColorWriteTier tier, bool dynamicBlend, int colorStates, ReadOnlySpan<AttachmentBlend> blendStates)
    {
        Vk api = _context.Api;
        uint colorWrite = values.ColorWrite;
        DynamicStateDirty dirty = _dynamicState.Update(serial, values);
        if (tier == ColorWriteTier.PipelineKey) dirty &= ~DynamicStateDirty.ColorWrite;
        if (!dynamicBlend) dirty &= ~DynamicStateDirty.ColorBlend;
        if (dirty == DynamicStateDirty.None) return;
        int extraCommands = 0;

        if ((dirty & DynamicStateDirty.ColorWrite) != 0)
        {
            if (tier == ColorWriteTier.DynamicEnable)
            {
                // All of maxColorAttachments, so the count covers every pipeline's attachments.
                Silk.NET.Core.Bool32* enables = stackalloc Silk.NET.Core.Bool32[colorStates];
                for (int i = 0; i < colorStates; i++) enables[i] = ((colorWrite >> i) & 1) != 0;
                _context.ColorWriteEnableApi!.CmdSetColorWriteEnable(commandBuffer, (uint)colorStates, enables);
            }
            else
            {
                ColorComponentFlags* masks = stackalloc ColorComponentFlags[colorStates];
                for (int i = 0; i < colorStates; i++) masks[i] = (ColorComponentFlags)((colorWrite >> (i * 4)) & 0xF);
                _context.DynamicState3Api!.CmdSetColorWriteMask(commandBuffer, 0, (uint)colorStates, masks);
            }
            extraCommands++;
        }

        if ((dirty & DynamicStateDirty.ColorBlend) != 0)
        {
            Silk.NET.Core.Bool32* blendEnables = stackalloc Silk.NET.Core.Bool32[colorStates];
            ColorBlendEquationEXT* equations = stackalloc ColorBlendEquationEXT[colorStates];
            for (int i = 0; i < colorStates; i++)
            {
                AttachmentBlend blend = i < blendStates.Length ? blendStates[i] : AttachmentBlend.Default;
                blendEnables[i] = blend.Enabled;
                equations[i] = new ColorBlendEquationEXT
                {
                    SrcColorBlendFactor = blend.SrcColor,
                    DstColorBlendFactor = blend.DstColor,
                    ColorBlendOp = blend.ColorOp,
                    SrcAlphaBlendFactor = blend.SrcAlpha,
                    DstAlphaBlendFactor = blend.DstAlpha,
                    AlphaBlendOp = blend.AlphaOp,
                };
            }
            _context.DynamicState3Api!.CmdSetColorBlendEnable(commandBuffer, 0, (uint)colorStates, blendEnables);
            _context.DynamicState3Api!.CmdSetColorBlendEquation(commandBuffer, 0, (uint)colorStates, equations);
            extraCommands += 2;
        }

        if ((dirty & DynamicStateDirty.Viewport) != 0) api.CmdSetViewport(commandBuffer, 0, 1, &values.Viewport);
        if ((dirty & DynamicStateDirty.Scissor) != 0) api.CmdSetScissor(commandBuffer, 0, 1, &values.Scissor);
        if ((dirty & DynamicStateDirty.CullMode) != 0) api.CmdSetCullMode(commandBuffer, values.CullMode);
        if ((dirty & DynamicStateDirty.FrontFace) != 0) api.CmdSetFrontFace(commandBuffer, values.FrontFace);
        if ((dirty & DynamicStateDirty.Topology) != 0) api.CmdSetPrimitiveTopology(commandBuffer, values.Topology);

        if ((dirty & DynamicStateDirty.DepthTestEnable) != 0) api.CmdSetDepthTestEnable(commandBuffer, values.DepthTest);
        if ((dirty & DynamicStateDirty.DepthWriteEnable) != 0) api.CmdSetDepthWriteEnable(commandBuffer, values.DepthWrite);
        if ((dirty & DynamicStateDirty.DepthCompareOp) != 0) api.CmdSetDepthCompareOp(commandBuffer, values.DepthCompare);

        if ((dirty & DynamicStateDirty.StencilTestEnable) != 0) api.CmdSetStencilTestEnable(commandBuffer, values.StencilTest);
        if ((dirty & DynamicStateDirty.StencilOp) != 0)
            api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
                values.StencilFail, values.StencilPass, values.StencilDepthFail, values.StencilCompare);
        if ((dirty & DynamicStateDirty.StencilCompareMask) != 0)
            api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilCompareMask);
        if ((dirty & DynamicStateDirty.StencilWriteMask) != 0)
            api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilWriteMask);
        if ((dirty & DynamicStateDirty.StencilReference) != 0)
            api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilReference);

        if ((dirty & DynamicStateDirty.LineWidth) != 0) api.CmdSetLineWidth(commandBuffer, values.LineWidth);

        int emitted = DynamicStateCache.CommandCount(dirty & DynamicStateDirty.All) + extraCommands;
        _dynamicStateCommands += emitted;
        VulkanStats.NoteDynamicStateCommands(emitted);
    }

    /// <summary>
    /// Commands the first draw of a recording emits: the core set plus the colour
    /// write state of the tier (one command; two more with dynamic blend). Tests only.
    /// </summary>
    internal int DynamicStateCommandsPerDrawForTests =>
        VulkanStats.DynamicStateCommandsPerDraw +
        (_context.Capabilities.ColorWriteTier == ColorWriteTier.PipelineKey ? 0 : 1) +
        (_context.Capabilities.DynamicColorBlend ? 2 : 0);

    /// <summary>The colour write tier this device's draws use. Tests only.</summary>
    internal ColorWriteTier ColorWriteTierForTests => _context.Capabilities.ColorWriteTier;

    /// <summary>vkCmdBeginRendering calls of this device. Tests only.</summary>
    internal long ScopesOpenedForTests => _targets.ScopesOpened;

    /// <summary>A texture's current layout. Tests only.</summary>
    internal ImageLayout TextureLayoutForTests(int textureId) =>
        _textures.Get(textureId)?.Layout ?? ImageLayout.Undefined;

    /// <summary>Restarts that reopened an identical attachment set; must stay 0. Tests only.</summary>
    internal long MaskRestartsForTests => _targets.MaskRestarts;

    /// <summary>Restarts for a sampled, draw-buffer-excluded slot. Tests only.</summary>
    internal long FeedbackSplitsForTests => _targets.FeedbackSplits;

    /// <summary>Dynamic-state commands this device recorded. Tests only.</summary>
    internal long DynamicStateCommandsForTests => _dynamicStateCommands;

    /// <summary>False emits every dynamic-state command on every draw, as before masking. Tests only.</summary>
    internal bool DynamicStateMaskingForTests
    {
        get => _dynamicState.Enabled;
        set => _dynamicState.Enabled = value;
    }

    /// <summary>
    /// Routes a set to the current slot's arena when it names a resource created
    /// in the last <see cref="ResourceAge.ShortLivedFrames" /> frames (GUI text,
    /// atlas tasks, fresh meshes, overflow uniform copies), otherwise to the
    /// long-lived cache.
    /// </summary>
    private DescriptorSet GetDescriptorSet(DescriptorSetContents contents, DescriptorSetLayout layout) =>
        _resourceAge.NamesShortLived(contents)
            ? _descriptorArenas[_frames.Current.Index].Get(contents, layout)
            : _descriptors.Get(contents, layout);

    /// <summary>The frames a resource's sets stay in the arena; 0 sends every set to the cache. Tests only.</summary>
    internal int ShortLivedFramesForTests
    {
        get => _resourceAge.ShortLivedFrames;
        set => _resourceAge.ShortLivedFrames = value;
    }

    /// <summary>A slot's descriptor arena. Tests only.</summary>
    internal DescriptorArena DescriptorArenaForTests(int slot) => _descriptorArenas[slot];

    /// <summary>The slot the current (or last) frame records into. Tests only.</summary>
    internal int CurrentSlotForTests => _frames.Current.Index;

    /// <summary>The indirect ring's bookkeeping. Tests only.</summary>
    internal IndirectRing IndirectRingForTests => _indirectRing;

    /// <summary>Multi-draws that took an overflow buffer, and slot buffers grown at a frame boundary. Tests only.</summary>
    internal long IndirectOverflowsForTests => _indirectOverflows;
    internal long IndirectGrowthsForTests => _indirectGrowths;

    /// <summary>Replaces the ring with one whose slot buffers start at <paramref name="value" /> bytes. Before the first multi-draw only. Tests only.</summary>
    internal ulong IndirectMinimumCapacityForTests
    {
        set => _indirectRing = new IndirectRing(_frames.FramesInFlight, value);
    }

    /// <summary>
    /// The frame boundary of the indirect ring: this slot's cursor returns to 0,
    /// and its buffer grows here, and only here, when the busiest frame so far did
    /// not fit. Overflow buffers of the frames before retire on the timelines.
    /// </summary>
    private void BeginIndirectFrame(int slot)
    {
        foreach (VulkanBuffer overflow in _indirectOverflow) _frames.DeferDeletion(overflow);
        _indirectOverflow.Clear();
        _indirectOverflowCursor = 0;

        if (_indirectRing.BeginFrame(slot, out ulong capacity))
        {
            // Draws of the slot's previous frame named the old buffer; it retires
            // on the timelines like any other resource.
            _frames.DeferDeletion(_indirectBuffers[slot]!);
            _indirectBuffers[slot] = CreateIndirectBuffer(capacity);
            _indirectRing.Attach(capacity);
            _indirectGrowths++;
        }
    }

    /// <summary>Per-frame dynamic data, so the ReBAR class (a miss falls through, counted).</summary>
    private VulkanBuffer CreateIndirectBuffer(ulong size) =>
        new(_context, size,
            BufferUsageFlags.IndirectBufferBit,
            MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPoolClass.ReBar);

    /// <summary>
    /// Hands out a region of the current slot's indirect-command buffer for one
    /// multi-draw.
    ///
    /// The commands are written on the CPU when the draw is recorded and read by
    /// the GPU when it executes, which is later - after every other draw of the
    /// frame has been recorded too. So each draw needs its own region: writing
    /// them all at offset zero meant every multi-draw in a frame executed with
    /// the ranges of whichever was recorded last, and the chunk pass is hundreds
    /// of them.
    ///
    /// Regions are bump-allocated per slot and never wrap (Phase 1B step 6): the
    /// cursor resets only at the slot's next frame start. A frame that outgrows
    /// its slot's buffer continues in an overflow buffer, counted, and the slot
    /// grows at its next frame boundary.
    /// </summary>
    private VulkanBuffer AllocateIndirect(int groupCount, out ulong offset)
    {
        ulong needed = (ulong)Math.Max(groupCount, 1) * (ulong)sizeof(DrawIndexedIndirectCommand);
        int slot = _indirectRing.Current;

        if (_indirectRing.NeedsBuffer(needed, out ulong capacity))
        {
            // Nothing recorded names a buffer the slot never had, so creating one is safe mid-frame.
            _indirectBuffers[slot] = CreateIndirectBuffer(capacity);
            _indirectRing.Attach(capacity);
        }

        if (_indirectRing.TryAllocate(needed, out offset)) return _indirectBuffers[slot]!;

        _indirectOverflows++;
        VulkanStats.NoteIndirectOverflow();
        VulkanBuffer? current = _indirectOverflow.Count == 0 ? null : _indirectOverflow[^1];
        if (current == null || _indirectOverflowCursor + needed > current.Size)
        {
            current = CreateIndirectBuffer(_indirectRing.CapacityFor(Math.Max(needed, _indirectRing.CapacityOf(slot))));
            _indirectOverflow.Add(current);
            _indirectOverflowCursor = 0;
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("indirect overflow: slot " + slot + " capacity " + _indirectRing.CapacityOf(slot) +
                    " frame usage " + _indirectRing.FrameUsageOf(slot) + "; overflow buffer " + current.Size);
            }
        }

        offset = _indirectOverflowCursor;
        _indirectOverflowCursor += needed;
        return current;
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
        if (!_frameActive || !_queryRing.CanBegin(queryId)) return;

        // The slot's pools are reset at frame start, before any scope opens, so
        // the query begins inside the scope the covered draw uses. Only a pool
        // created just now needs a reset here, and a reset has to happen outside
        // a scope: one restart per pool ever, never in steady state. If the
        // scope later closes before the query ends, the ring's scope hooks
        // suspend it and resume it in the next scope.
        CommandBuffer commandBuffer = Commands;
        if (_queryRing.NextNeedsPool)
        {
            _targets.EndRendering(commandBuffer);
            _queryRing.AddPool(commandBuffer);
        }
        // No scope is opened for it: a query begun outside one is suspended and starts in the next
        // scope that opens - the native pass of the draw it covers (QueryRing.OnScopeOpened).
        _queryRing.Begin(queryId, commandBuffer, _targets.RenderingActive);
    }

    public void EndOcclusionQuery(int queryId)
    {
        if (_frameActive) _queryRing.End(queryId, _frames.Current.FrameValue, Commands);
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
        _bindless?.Flush();
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
    /// <summary>Level 0 of a texture through the dump path's readback. Tests only.</summary>
    internal byte[] ReadBackLevel0ForTests(int textureId) =>
        ReadBackLevel0(_textures.Get(textureId) ?? throw new ArgumentException("no texture " + textureId));

    /// <summary>One mip level of a texture through the in-frame readback. Tests only; a frame must be open.</summary>
    internal byte[] ReadBackLevelForTests(int textureId, uint mipLevel)
    {
        VulkanTexture texture = _textures.Get(textureId) ?? throw new ArgumentException("no texture " + textureId);
        if (!_frameActive) throw new InvalidOperationException("a mip readback needs an open frame");
        uint width = Math.Max(1, texture.Width >> (int)mipLevel);
        uint height = Math.Max(1, texture.Height >> (int)mipLevel);
        ulong bytes = (ulong)width * height * (ulong)BytesPerPixel(texture.Format);
        var data = new byte[bytes];
        _targets.FlushPendingClears(Commands, texture);
        _targets.EndRendering(Commands);
        ReadbackTicket ticket = _readbacks.CopyToHost(texture, 0, 0, width, height, texture.Aspect, bytes, mipLevel);
        SubmitPartial();
        fixed (byte* destination = data) _readbacks.WaitAndCopy(ticket, (IntPtr)destination);
        return data;
    }

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
            _targets.FlushPendingClears(Commands, texture);
            _targets.EndRendering(Commands);
            ReadbackTicket ticket = _readbacks.CopyToHost(texture, x, y, width, height, aspect, bytes);
            SubmitPartial();
            _readbacks.WaitAndCopy(ticket, destination);
            return;
        }

        // The copy writes whole texels of the image's format whatever the caller
        // sized its destination for; the buffer holds them all, the caller gets its bytes.
        ulong copied = (ulong)width * height * (ulong)BytesPerPixel(texture.Format);
        ulong handed = Math.Min(bytes, copied);
        using var readback = new VulkanBuffer(_context, Math.Max(bytes, copied),
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);

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

        System.Buffer.MemoryCopy((void*)readback.Mapped, (void*)destination, (long)bytes, (long)handed);
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

    /// <summary>Colour attachment 0 of an explicit target (the default one for <see cref="PassDeclaration.DefaultFramebuffer" />).</summary>
    internal void ReadFramebufferColor(int framebufferId, int x, int y, int width, int height, IntPtr destination)
    {
        EndNativePass();
        ReadFramebufferColor(_targets.Get(ResolveNativeFramebuffer(framebufferId)), x, y, width, height, destination);
    }

    private void ReadFramebufferColor(VulkanFramebuffer? target, int x, int y, int width, int height, IntPtr destination)
    {
        if (destination == IntPtr.Zero || width <= 0 || height <= 0) return;
        if (target == null) return;

        VulkanTexture? texture = _textures.Get(target.Color[0].TextureId);
        if (texture == null) return;

        ReadBack(texture, x, y, (uint)width, (uint)height, ImageAspectFlags.ColorBit,
            (ulong)width * (ulong)height * 4, destination);
    }

    /// <summary>
    /// The format of the default colour target, so the platform above knows the
    /// channel order the readback hands back rather than assuming one.
    /// </summary>
    internal Format DefaultColorFormat => DefaultColorTexture()?.Format ?? Format.R8G8B8A8Unorm;

    // ------------------------------------------------------------------- teardown

    /// <summary>
    /// Writes the driver's pipeline cache and the pipeline-key log for the next launch, after
    /// the device is idle; opportunistic saves during the session come from
    /// <see cref="PipelineCachePersistence.Tick" />. A failed write costs the next launch its
    /// warm start, nothing more.
    /// </summary>
    private void SavePipelineCache()
    {
        if (_pipelinePersistence == null || _pipelines == null) return;
        _pipelinePersistence.SaveAtShutdown(_pipelines);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_context != null)
        {
            VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);
        }

        // Background compiles read program modules and layouts, and a background save
        // reads the driver cache: both end before anything they use is destroyed.
        _pipelines?.StopBackgroundCompiles();
        _pipelinePersistence?.WaitForPendingSave();

        foreach (ShaderProgramResources program in _programs.Values) program.Dispose();
        _programs.Clear();
        _compute?.Dispose();
        // The shared pipeline layout before the table's set layout it names; the
        // table's placeholders are textures and go with the texture manager.
        _sharedLayout?.Dispose();
        _bindless?.Dispose();

        _uniformBuffers.Clear();

        _queryRing?.Dispose();
        _readbacks?.Dispose();

        foreach (VulkanBuffer? indirect in _indirectBuffers) indirect?.Dispose();
        foreach (VulkanBuffer overflow in _indirectOverflow) overflow.Dispose();
        _indirectOverflow.Clear();
        foreach (DescriptorArena arena in _descriptorArenas) arena.Dispose();
        foreach (ComputeDescriptorArena arena in _computeArenas) arena.Dispose();
        _defaultAttributes?.Dispose();
        _placeholderUniforms?.Dispose();
        _swapchain?.Dispose();
        _shaderCompiler?.Dispose();
        _frames?.Dispose();
        _descriptors?.Dispose();
        SavePipelineCache();
        _pipelines?.Dispose();
        _targets?.Dispose();
        _meshes?.Dispose();
        _textures?.Dispose();
        if (_context != null && ReferenceEquals(VulkanStats.MemorySource, _context.Allocator))
        {
            VulkanStats.MemorySource = null;
        }
        DisposeLatency();
        _context?.Dispose();
    }
}
