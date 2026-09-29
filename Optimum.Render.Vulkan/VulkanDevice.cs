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
/// It accepts the game's stateful rendering contract - state, named uniforms,
/// texture bindings and mesh draws - and records native Vulkan passes. Mods using
/// the client API can take this route; direct OpenGL calls need separate support.
///
/// Managers own their resources and expose integer ids for the client API. The
/// partial files group program, resource, mesh, binding and readback operations;
/// this facade coordinates frame submission and teardown.
/// </summary>
public sealed unsafe partial class VulkanDevice : IDisposable, Platform.ILatencyStageListener
{
    void Platform.ILatencyStageListener.OnFrameRenderStart() => NoteRenderStageStarted();

    internal FrameTimingRecorder Latency { get; private set; } = new();
    internal ulong LatencyFrameId => _latencyFrameId;
    private ulong _latencyFrameId;
    private bool _latencyFrameIdPending;
    private ulong _latencyRenderStartFrame;

    private void InitializeFrameTiming()
    {
        Latency = new FrameTimingRecorder(MirrorValidationMessage);
        VulkanStats.LatencySource = Latency;
    }

    /// <summary>Called before input; BeginFrame supplies an identity for headless callers.</summary>
    public ulong BeginLatencyFrame()
    {
        _latencyFrameIdPending = true;
        return ++_latencyFrameId;
    }

    private void BeginLatencyFrameIdentity()
    {
        if (!_latencyFrameIdPending) BeginLatencyFrame();
        _latencyFrameIdPending = false;
        _frames.Latency.FrameId = _latencyFrameId;
    }

    internal void NoteRenderStageStarted()
    {
        if (_latencyRenderStartFrame == _latencyFrameId) return;
        _latencyRenderStartFrame = _latencyFrameId;
        Latency.Marker(_latencyFrameId, LatencyMarker.SimulationEnd);
        Latency.Marker(_latencyFrameId, LatencyMarker.RenderSubmitStart);
    }

    private void DisposeLatency()
    {
        if (ReferenceEquals(VulkanStats.LatencySource, Latency)) VulkanStats.LatencySource = null;
    }

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

    /// <summary>The native shaders loaded at device start; null when they are off (docs/vulkan.md).</summary>
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
    /// A parity capture forces blocking creation so its exact frame includes every draw.
    /// An explicit <paramref name="configured" /> still wins.
    /// </summary>
    internal static bool ResolveSynchronousPipelines(bool? configured, string? environment, string? parityDump) =>
        configured ?? (ResolveSynchronousPipelines(null, environment) || NamesCaptureDirectory(parityDump));

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
        InitializeFrameTiming();
        // A ReBAR miss is logged, not an error: the validation mirror and the
        // trace, never GetError. The stats sample reads this allocator's heaps.
        _context.Allocator.Log = MirrorValidationMessage;
        VulkanStats.MemorySource = _context.Allocator;
        // Uploads never wait: they ride the next frame submission, recorded from
        // any thread into the ring's upload batch (or inline into the frame when
        // it already used the destination; see UploadManager).
        _frames = new FrameRing(_context);
        // Seam S4: the installed backend tags this ring's submits.
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
        // Background compiles (docs/vulkan.md#caches, design item 4): a draw whose
        // pipeline is not in the driver cache is skipped while a worker compiles it.
        bool synchronousPipelines = ResolveSynchronousPipelines(SynchronousPipelines,
            Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_SYNC_PIPELINES"),
            Environment.GetEnvironmentVariable("OPTIMUM_PARITY_DUMP"));
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
                    out Swapchain? swapchain, out string? swapchainError))
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
    private BlitPresentPath? _presentPath;
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
    /// <summary>The frame ring's timelines. Tests only.</summary>
    internal FrameTimeline TimelineForTests => _frames.Timeline;

    /// <summary>The frame ring's upload manager. Tests only.</summary>
    /// <summary>Decision 9's set 1. Tests only.</summary>
    internal BindlessTextureTable BindlessForTests => _bindless!;

    /// <summary>Decision 9's shared pipeline layout. Tests only.</summary>
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
