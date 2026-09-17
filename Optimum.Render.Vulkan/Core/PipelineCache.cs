using Optimum.Render.Vulkan.Shaders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Creates graphics pipelines on demand and remembers them.
///
/// Vulkan wants pipeline state baked ahead of time; GL lets it change one call
/// before a draw. Bridging that is the job here: a draw states its fixed state
/// (NativePipelineDescription), and the first draw that needs a given
/// combination compiles a pipeline for it. Because Vulkan 1.3 makes viewport,
/// scissor, cull, front face, depth and stencil dynamic, the combinations that
/// remain are few - roughly a few hundred across the whole game - and after the
/// first minutes of play the cache stops growing.
///
/// A driver-side <see cref="Silk.NET.Vulkan.PipelineCache" /> backs it so that
/// even those first compiles are cheap on a second run.
/// </summary>
internal sealed unsafe class GraphicsPipelineCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
    private readonly Silk.NET.Vulkan.PipelineCache _driverCache;

    /// <summary>
    /// Serialises every host access to <see cref="_driverCache" />: creations against it,
    /// merges into it and reads of its data. Merges require it (the destination is externally
    /// synchronised), and serialising the reads and merges is the workaround for the AMD
    /// reports of parallel creation corrupting cache data (docs/research/vulkan-caching.md §1,
    /// "Design for this renderer" item 4). The background compiles themselves run against a
    /// cache of their own, outside this lock.
    /// </summary>
    private readonly object _driverCacheLock = new();

    private readonly DynamicState[] _dynamicStates;
    private bool _disposed;

    /// <summary>How many pipelines have been compiled, for diagnostics.</summary>
    public int Count => _pipelines.Count;

    /// <summary>How many lookups were served from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>How many lookups had to compile.</summary>
    public long Misses { get; private set; }

    private long _compiledSync;
    private long _compiledAsync;
    private long _prewarmedCount;
    private long _warm;
    private long _prewarmHits;
    private long _drawsSkipped;
    private long _queuedCompiles;

    /// <summary>Pipelines compiled on the calling thread.</summary>
    public long CompiledSync => Interlocked.Read(ref _compiledSync);

    /// <summary>Pipelines a lookup asked for that the background worker compiled.</summary>
    public long CompiledAsync => Interlocked.Read(ref _compiledAsync);

    /// <summary>Pipelines the worker compiled from the key log before any lookup asked.</summary>
    public long Prewarmed => Interlocked.Read(ref _prewarmedCount);

    /// <summary>FAIL_ON_PIPELINE_COMPILE_REQUIRED creations the driver cache satisfied without a compile.</summary>
    public long Warm => Interlocked.Read(ref _warm);

    /// <summary>First lookups of a key served by a prewarmed pipeline.</summary>
    public long PrewarmHits => Interlocked.Read(ref _prewarmHits);

    /// <summary>Lookups that reported "not ready" (the draw was skipped).</summary>
    public long DrawsSkipped => Interlocked.Read(ref _drawsSkipped);

    /// <summary>Jobs handed to the background worker for a lookup (prewarm jobs not included).</summary>
    public long QueuedCompiles => Interlocked.Read(ref _queuedCompiles);

    private bool _asyncCompiles;

    /// <summary>
    /// Whether <see cref="TryGet" /> may hand compiles to the background worker. Only takes
    /// effect on a device with pipelineCreationCacheControl; off (the default) keeps every
    /// lookup blocking, as <see cref="Get" /> always is.
    /// </summary>
    public bool AsyncCompiles
    {
        get => _asyncCompiles;
        set => _asyncCompiles = value && _context.Capabilities.PipelineCreationCacheControl && !_workersStopped;
    }

    /// <summary>Where every pipeline this cache hands out is recorded, for the next launch's prewarm. Null records nothing.</summary>
    public PipelineKeyLog? KeyLog { get; set; }

    /// <summary>The settings hash key-log entries of this cache carry (<see cref="PipelineKeyLog.SettingsHashFor" />).</summary>
    public ulong SettingsHash { get; }

    /// <summary>The colour write tier every pipeline of this cache is built for.</summary>
    public ColorWriteTier ColorWriteTier { get; }

    /// <summary>Everything Vulkan 1.3 core lets us change without a new pipeline.</summary>
    private static readonly DynamicState[] CoreDynamicStates =
    {
        DynamicState.Viewport,
        DynamicState.Scissor,
        DynamicState.LineWidth,
        DynamicState.CullMode,
        DynamicState.FrontFace,
        DynamicState.PrimitiveTopology,
        DynamicState.DepthTestEnable,
        DynamicState.DepthWriteEnable,
        DynamicState.DepthCompareOp,
        DynamicState.StencilTestEnable,
        DynamicState.StencilOp,
        DynamicState.StencilCompareMask,
        DynamicState.StencilWriteMask,
        DynamicState.StencilReference,
    };

    public GraphicsPipelineCache(VulkanContext context, byte[]? initialData = null)
        : this(context, ColorWriteTier.PipelineKey, dynamicBlend: false, initialData)
    {
    }

    /// <summary>
    /// A cache whose pipelines declare the colour write state of <paramref name="tier" />
    /// dynamic (and the blend set, with <paramref name="dynamicBlend" /> on the mask tier).
    /// Draws through it must then emit that state (VulkanDevice.ApplyDynamicState).
    /// </summary>
    public GraphicsPipelineCache(VulkanContext context, ColorWriteTier tier, bool dynamicBlend, byte[]? initialData = null)
    {
        _context = context;
        ColorWriteTier = tier;
        SettingsHash = PipelineKeyLog.SettingsHashFor(tier, dynamicBlend);

        var dynamicStates = new List<DynamicState>(CoreDynamicStates);
        if (tier == ColorWriteTier.DynamicEnable) dynamicStates.Add(DynamicState.ColorWriteEnableExt);
        if (tier == ColorWriteTier.DynamicMask)
        {
            dynamicStates.Add(DynamicState.ColorWriteMaskExt);
            if (dynamicBlend)
            {
                dynamicStates.Add(DynamicState.ColorBlendEnableExt);
                dynamicStates.Add(DynamicState.ColorBlendEquationExt);
            }
        }
        _dynamicStates = dynamicStates.ToArray();

        // A rejected blob is not an error: the driver starts cold instead. Some
        // drivers return an error rather than an empty cache for data they do not
        // accept, so that case retries without it (docs/research/vulkan-caching.md §1).
        if (initialData is { Length: > 0 } && TryCreateDriverCache(context, initialData, out _driverCache))
        {
            SeedAccepted = true;
        }
        else
        {
            TryCreateDriverCache(context, null, out _driverCache);
        }
    }

    /// <summary>The driver's pipeline cache; compute pipelines compile through it too, so one file warms both.</summary>
    public Silk.NET.Vulkan.PipelineCache DriverCache => _driverCache;

    /// <summary>The driver created its cache from the initial data rather than empty.</summary>
    public bool SeedAccepted { get; }

    private static bool TryCreateDriverCache(
        VulkanContext context, byte[]? initialData, out Silk.NET.Vulkan.PipelineCache cache)
    {
        fixed (byte* data = initialData)
        {
            // Never a non-null pointer with a zero size: one driver fails on exactly that.
            var createInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initialData?.Length ?? 0),
                PInitialData = initialData is { Length: > 0 } ? data : null,
            };

            if (context.Api.CreatePipelineCache(context.Device, &createInfo, null, out cache) == Result.Success)
            {
                return true;
            }
            cache = default;
            return false;
        }
    }

    /// <summary>Everything a pipeline needs that is not already in the key.</summary>
    internal sealed class PipelineRequest
    {
        public required ShaderProgramResources Program { get; init; }
        public required VertexLayoutDescription VertexLayout { get; init; }
        public required RenderTargetFormats Targets { get; init; }
        public required AttachmentBlend[] Blend { get; init; }
        public required PolygonMode PolygonMode { get; init; }
        public required PrimitiveTopology Topology { get; init; }
    }

    /// <summary>The pipeline for <paramref name="key" />, compiled on this thread if it has to be.</summary>
    public Pipeline Get(PipelineKey key, PipelineRequest request)
    {
        if (_pipelines.TryGetValue(key, out Pipeline existing))
        {
            Hits++;
            return existing;
        }

        Misses++;
        PipelineKeyLogEntry entry = PipelineKeyLogEntry.From(SettingsHash, request);
        if (!TryAdoptPrewarmed((request.Program.ProgramId, entry.ContentId), out Pipeline pipeline))
        {
            pipeline = CreateBlocking(request);
        }
        Store(key, pipeline, entry);
        return pipeline;
    }

    /// <summary>
    /// The pipeline for <paramref name="key" /> if it can be had without compiling on this
    /// thread; otherwise false, with the compile queued on the background worker, and the
    /// caller skips its draw (Unreal's default for a PSO that is not ready,
    /// docs/research/vulkan-caching.md §2). A key already compiling is not queued again.
    /// Finished compiles become visible at <see cref="PublishCompleted" />.
    ///
    /// With <see cref="AsyncCompiles" /> off this never returns false.
    /// </summary>
    /// <summary>
    /// <see cref="TryGet" /> ahead of any draw: the compile starts (or the driver cache serves it)
    /// when a native system asks for its pipeline, and no draw is counted as skipped for it.
    /// </summary>
    public void Prepare(PipelineKey key, PipelineRequest request)
    {
        _preparing = true;
        try
        {
            TryGet(key, request, out _);
        }
        finally
        {
            _preparing = false;
        }
    }

    private bool _preparing;

    public bool TryGet(PipelineKey key, PipelineRequest request, out Pipeline pipeline)
    {
        if (_pipelines.TryGetValue(key, out pipeline))
        {
            Hits++;
            return true;
        }

        if (_pendingByKey.ContainsKey(key))
        {
            NoteSkipped();
            return false;
        }

        Misses++;
        PipelineKeyLogEntry entry = PipelineKeyLogEntry.From(SettingsHash, request);
        var id = (request.Program.ProgramId, entry.ContentId);

        if (TryAdoptPrewarmed(id, out pipeline))
        {
            Store(key, pipeline, entry);
            return true;
        }

        // The same pipeline under another key, or a prewarm of it, is already on its way.
        if (_pendingJobs.TryGetValue(id, out CompileJob? pending))
        {
            // A prewarm no worker has reached yet never had a warm attempt: on a warm start
            // the driver cache serves it here, and waiting for the queue would skip the draw.
            if (TryTakeWarmFromQueuedPrewarm(pending, request, out pipeline))
            {
                Store(key, pipeline, entry);
                return true;
            }
            pending.DemandKeys.Add(key);
            _pendingByKey[key] = pending;
            Promote(pending);
            NoteSkipped();
            return false;
        }

        if (!AsyncCompiles || _failedJobs.Contains(id))
        {
            pipeline = CreateBlocking(request);
            Store(key, pipeline, entry);
            return true;
        }

        Result result;
        lock (_driverCacheLock)
        {
            result = CreatePipeline(request, _driverCache, PipelineCreateFlags.CreateFailOnPipelineCompileRequiredBit,
                out pipeline);
        }
        if (result == Result.Success)
        {
            Interlocked.Increment(ref _warm);
            VulkanStats.NotePipelineWarm();
            Store(key, pipeline, entry);
            return true;
        }
        if (result != Result.PipelineCompileRequired)
        {
            throw new InvalidOperationException("vkCreateGraphicsPipelines failed: " + result);
        }

        var job = new CompileJob(id, request, entry, prewarm: false);
        if (!TryEnqueue(job))
        {
            // The worker is saturated; a draw is never left waiting on a queue it cannot join.
            pipeline = CreateBlocking(request);
            Store(key, pipeline, entry);
            return true;
        }

        Interlocked.Increment(ref _queuedCompiles);
        job.DemandKeys.Add(key);
        _pendingJobs[id] = job;
        _pendingByKey[key] = job;
        VulkanStats.NotePipelinesPending(_pendingJobs.Count);
        NoteSkipped();
        return false;
    }

    /// <summary>
    /// The FAIL_ON_PIPELINE_COMPILE_REQUIRED attempt for a lookup whose pipeline is only
    /// queued as a prewarm. On success the prewarm is dropped: removed from its queue, or,
    /// when a worker took it meanwhile, cancelled so its result is destroyed at publication.
    /// A prewarm already promoted by an earlier lookup had its attempt then and gets none.
    /// </summary>
    private bool TryTakeWarmFromQueuedPrewarm(CompileJob job, PipelineRequest request, out Pipeline pipeline)
    {
        pipeline = default;
        lock (_queueLock)
        {
            if (!job.Prewarm || !job.Queued) return false;
        }

        Result result;
        lock (_driverCacheLock)
        {
            result = CreatePipeline(request, _driverCache, PipelineCreateFlags.CreateFailOnPipelineCompileRequiredBit,
                out pipeline);
        }
        if (result == Result.PipelineCompileRequired) return false;
        if (result != Result.Success)
        {
            throw new InvalidOperationException("vkCreateGraphicsPipelines failed: " + result);
        }

        Interlocked.Increment(ref _warm);
        VulkanStats.NotePipelineWarm();
        lock (_queueLock)
        {
            if (job.Queued)
            {
                job.Queued = false;
                if (!_prewarmQueue.Remove(job)) _demandQueue.Remove(job);
            }
            else
            {
                job.Cancelled = true;
            }
            Forget(job);
        }
        VulkanStats.NotePipelinesPending(_pendingJobs.Count);
        return true;
    }

    private void NoteSkipped()
    {
        if (_preparing) return;
        Interlocked.Increment(ref _drawsSkipped);
        VulkanStats.NotePipelineDrawSkipped();
    }

    private Pipeline CreateBlocking(PipelineRequest request)
    {
        Result result;
        Pipeline pipeline;
        lock (_driverCacheLock)
        {
            result = CreatePipeline(request, _driverCache, 0, out pipeline);
        }
        if (result != Result.Success)
        {
            throw new InvalidOperationException("vkCreateGraphicsPipelines failed: " + result);
        }
        Interlocked.Increment(ref _compiledSync);
        VulkanStats.NotePipelineCompiledSync();
        return pipeline;
    }

    private void Store(PipelineKey key, Pipeline pipeline, PipelineKeyLogEntry entry)
    {
        _pipelines[key] = pipeline;
        KeyLog?.Record(entry, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private bool TryAdoptPrewarmed((int ProgramId, UInt128 ContentId) id, out Pipeline pipeline)
    {
        if (!_prewarmed.Remove(id, out pipeline)) return false;
        Interlocked.Increment(ref _prewarmHits);
        return true;
    }

    // ------------------------------------------------------------ background compiles

    /// <summary>A compile for the worker. Fields other than the result are touched by the render thread only.</summary>
    private sealed class CompileJob
    {
        public CompileJob((int ProgramId, UInt128 ContentId) id, PipelineRequest request, PipelineKeyLogEntry entry,
            bool prewarm)
        {
            Id = id;
            Request = request;
            Entry = entry;
            Prewarm = prewarm;
        }

        public (int ProgramId, UInt128 ContentId) Id { get; }
        public PipelineRequest Request { get; }
        public PipelineKeyLogEntry Entry { get; }

        /// <summary>Built from the key log; cleared when a lookup promotes it. Under the queue lock.</summary>
        public bool Prewarm;

        /// <summary>Waiting in a queue rather than running or done. Under the queue lock.</summary>
        public bool Queued;

        /// <summary>Its program was deleted; the result is destroyed rather than published.</summary>
        public volatile bool Cancelled;

        /// <summary>The keys whose lookups wait for this pipeline.</summary>
        public readonly List<PipelineKey> DemandKeys = new();

        public Pipeline Pipeline;
        public Result Status;
    }

    /// <summary>Lookups first; each queue bounded so a burst cannot grow memory without limit.</summary>
    internal const int DemandQueueCapacity = 256;

    internal const int PrewarmQueueCapacity = 4096;

    private readonly object _queueLock = new();
    private readonly LinkedList<CompileJob> _demandQueue = new();
    private readonly LinkedList<CompileJob> _prewarmQueue = new();
    private readonly List<CompileJob> _inFlight = new();
    private readonly ConcurrentQueue<CompileJob> _completed = new();
    private Thread[]? _workers;
    private bool _stopping;
    private bool _workersStopped;
    private bool _holdForTests;

    /// <summary>
    /// Tests only: while true the workers take no new job, so a test can observe a lookup
    /// against a job that is still queued. Setting it false wakes them.
    /// </summary>
    internal bool HoldBackgroundCompilesForTests
    {
        set
        {
            lock (_queueLock)
            {
                _holdForTests = value;
                Monitor.PulseAll(_queueLock);
            }
        }
    }

    /// <summary>Render thread: every queued or running job by pipeline identity, and by the keys waiting on it.</summary>
    private readonly Dictionary<(int ProgramId, UInt128 ContentId), CompileJob> _pendingJobs = new();
    private readonly Dictionary<PipelineKey, CompileJob> _pendingByKey = new();

    /// <summary>Render thread: prewarmed pipelines no lookup has claimed yet.</summary>
    private readonly Dictionary<(int ProgramId, UInt128 ContentId), Pipeline> _prewarmed = new();

    /// <summary>Render thread: jobs whose compile failed; their next lookup compiles blocking and reports the error.</summary>
    private readonly HashSet<(int ProgramId, UInt128 ContentId)> _failedJobs = new();

    /// <summary>Compiles queued or running, prewarm included.</summary>
    public int PendingCompiles => _pendingJobs.Count;

    /// <summary>Prewarmed pipelines published and not yet claimed by a lookup.</summary>
    public int PrewarmedWaiting => _prewarmed.Count;

    private bool TryEnqueue(CompileJob job)
    {
        lock (_queueLock)
        {
            if (_stopping) return false;
            LinkedList<CompileJob> queue = job.Prewarm ? _prewarmQueue : _demandQueue;
            int capacity = job.Prewarm ? PrewarmQueueCapacity : DemandQueueCapacity;
            if (queue.Count >= capacity) return false;
            queue.AddLast(job);
            job.Queued = true;
            _workers ??= StartWorkers();
            Monitor.Pulse(_queueLock);
            return true;
        }
    }

    /// <summary>A lookup waits on a prewarm job still in its queue: it moves to the lookup queue.</summary>
    private void Promote(CompileJob job)
    {
        lock (_queueLock)
        {
            if (!job.Prewarm) return;
            job.Prewarm = false;
            if (!job.Queued) return;
            _prewarmQueue.Remove(job);
            _demandQueue.AddLast(job);
        }
    }

    private Thread[] StartWorkers()
    {
        // One or two: compiles hold the main cache's lock only for the short warm attempt
        // and the merge, so a second worker helps a cold start; more would compete with the
        // game's own threads.
        int count = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
        var workers = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "optimum-pipeline-compile-" + i,
            };
            workers[i].Start();
        }
        return workers;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            CompileJob job;
            lock (_queueLock)
            {
                while (!_stopping && (_holdForTests || (_demandQueue.Count == 0 && _prewarmQueue.Count == 0)))
                {
                    Monitor.Wait(_queueLock);
                }
                if (_stopping) return;
                LinkedList<CompileJob> queue = _demandQueue.Count > 0 ? _demandQueue : _prewarmQueue;
                job = queue.First!.Value;
                queue.RemoveFirst();
                job.Queued = false;
                _inFlight.Add(job);
            }

            try
            {
                Compile(job);
            }
            finally
            {
                _completed.Enqueue(job);
                lock (_queueLock)
                {
                    _inFlight.Remove(job);
                    Monitor.PulseAll(_queueLock);
                }
            }
        }
    }

    private void Compile(CompileJob job)
    {
        Vk api = _context.Api;

        // The main cache may already hold it (a warm start): no compile, no merge.
        lock (_driverCacheLock)
        {
            job.Status = CreatePipeline(job.Request, _driverCache, PipelineCreateFlags.CreateFailOnPipelineCompileRequiredBit,
                out job.Pipeline);
        }
        if (job.Status == Result.Success)
        {
            Interlocked.Increment(ref _warm);
            VulkanStats.NotePipelineWarm();
            NoteBuilt(job);
            return;
        }
        if (job.Status != Result.PipelineCompileRequired) return;

        // The compile itself runs against a cache of this job's own, so the render thread's
        // warm attempts never wait on it; the result is merged into the main cache after.
        if (!TryCreateDriverCache(_context, null, out Silk.NET.Vulkan.PipelineCache local))
        {
            job.Status = Result.ErrorInitializationFailed;
            return;
        }
        try
        {
            job.Status = CreatePipeline(job.Request, local, 0, out job.Pipeline);
            if (job.Status != Result.Success) return;

            lock (_driverCacheLock)
            {
                api.MergePipelineCaches(_context.Device, _driverCache, 1, &local);
            }

            bool prewarm;
            lock (_queueLock) prewarm = job.Prewarm;
            if (!prewarm)
            {
                Interlocked.Increment(ref _compiledAsync);
                VulkanStats.NotePipelineCompiledAsync();
            }
            NoteBuilt(job);
        }
        finally
        {
            api.DestroyPipelineCache(_context.Device, local, null);
        }
    }

    /// <summary>Counts a prewarm job's pipeline once it exists, compiled or taken from the driver cache.</summary>
    private void NoteBuilt(CompileJob job)
    {
        bool prewarm;
        lock (_queueLock) prewarm = job.Prewarm;
        if (!prewarm) return;
        Interlocked.Increment(ref _prewarmedCount);
        VulkanStats.NotePipelinePrewarmed();
    }

    /// <summary>
    /// Render thread, at a safe point (frame start): makes the worker's finished pipelines
    /// visible to lookups. Returns how many were published.
    /// </summary>
    public int PublishCompleted()
    {
        int published = 0;
        while (_completed.TryDequeue(out CompileJob? job))
        {
            // A job dropped early (Forget) may have been replaced under its id or keys by a
            // newer one; only this job's own entries go.
            Forget(job);

            if (job.Status != Result.Success || job.Pipeline.Handle == 0)
            {
                // A lookup waiting on it retries blocking and surfaces the error there.
                if (!job.Cancelled) _failedJobs.Add(job.Id);
                continue;
            }

            if (job.Cancelled || _disposed)
            {
                _context.Api.DestroyPipeline(_context.Device, job.Pipeline, null);
                continue;
            }

            bool used = false;
            foreach (PipelineKey key in job.DemandKeys)
            {
                if (_pipelines.ContainsKey(key)) continue;
                Store(key, job.Pipeline, job.Entry);
                used = true;
            }
            if (!used && (job.DemandKeys.Count > 0 || !_prewarmed.TryAdd(job.Id, job.Pipeline)))
            {
                _context.Api.DestroyPipeline(_context.Device, job.Pipeline, null);
                continue;
            }
            published++;
        }
        VulkanStats.NotePipelinesPending(_pendingJobs.Count);
        return published;
    }

    /// <summary>
    /// Render thread, when <paramref name="program" /> links: queues a background build of
    /// every key-log entry recorded for a program with the same SPIR-V under this cache's
    /// settings. Needs <see cref="AsyncCompiles" />. Returns how many were queued.
    /// </summary>
    public int PrewarmFor(ShaderProgramResources program)
    {
        if (KeyLog == null || !AsyncCompiles) return 0;

        int queued = 0;
        foreach (PipelineKeyLogEntry entry in KeyLog.Matching(SettingsHash, program.SourceHash))
        {
            var id = (program.ProgramId, entry.ContentId);
            if (_pendingJobs.ContainsKey(id) || _prewarmed.ContainsKey(id)) continue;

            var job = new CompileJob(id, entry.ToRequest(program), entry, prewarm: true);
            if (!TryEnqueue(job)) break;
            _pendingJobs[id] = job;
            queued++;
        }
        VulkanStats.NotePipelinesPending(_pendingJobs.Count);
        return queued;
    }

    /// <summary>
    /// Render thread, before <paramref name="program" /> is destroyed: drops its queued
    /// compiles and waits for any the worker is running, so no compile ever reads a
    /// destroyed shader module or layout.
    /// </summary>
    public void CancelProgram(ShaderProgramResources program)
    {
        // Every job of the program the render thread still tracks, including one the worker
        // finished that waits in the completed queue for the next frame start: publishing
        // that one would park a pipeline of a deleted program where no lookup can claim it.
        foreach (CompileJob job in _pendingJobs.Values)
        {
            if (ReferenceEquals(job.Request.Program, program)) job.Cancelled = true;
        }

        lock (_queueLock)
        {
            foreach (LinkedList<CompileJob> queue in new[] { _demandQueue, _prewarmQueue })
            {
                for (LinkedListNode<CompileJob>? node = queue.First; node != null;)
                {
                    LinkedListNode<CompileJob>? next = node.Next;
                    if (ReferenceEquals(node.Value.Request.Program, program))
                    {
                        node.Value.Cancelled = true;
                        node.Value.Queued = false;
                        queue.Remove(node);
                        Forget(node.Value);
                    }
                    node = next;
                }
            }

            while (true)
            {
                bool running = false;
                foreach (CompileJob job in _inFlight)
                {
                    if (!ReferenceEquals(job.Request.Program, program)) continue;
                    job.Cancelled = true;
                    running = true;
                }
                if (!running) break;
                Monitor.Wait(_queueLock);
            }
        }

        var stale = new List<(int ProgramId, UInt128 ContentId)>();
        foreach ((int ProgramId, UInt128 ContentId) id in _prewarmed.Keys)
        {
            if (id.ProgramId == program.ProgramId) stale.Add(id);
        }
        foreach ((int ProgramId, UInt128 ContentId) id in stale)
        {
            _prewarmed.Remove(id, out Pipeline pipeline);
            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        }
        VulkanStats.NotePipelinesPending(_pendingJobs.Count);
    }

    private void Forget(CompileJob job)
    {
        if (_pendingJobs.TryGetValue(job.Id, out CompileJob? current) && ReferenceEquals(current, job))
        {
            _pendingJobs.Remove(job.Id);
        }
        foreach (PipelineKey key in job.DemandKeys)
        {
            if (_pendingByKey.TryGetValue(key, out CompileJob? waiting) && ReferenceEquals(waiting, job))
            {
                _pendingByKey.Remove(key);
            }
        }
    }

    /// <summary>Waits until no compile is queued or running. Tests only; false on timeout.</summary>
    internal bool WaitForBackgroundCompiles(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_queueLock)
        {
            while (_demandQueue.Count > 0 || _prewarmQueue.Count > 0 || _inFlight.Count > 0)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_queueLock, (int)Math.Min(remaining, int.MaxValue));
            }
        }
        return true;
    }

    /// <summary>
    /// Stops the workers after the job each is running, dropping queued ones. Lookups
    /// compile blocking from then on. Before any program is destroyed at shutdown.
    /// </summary>
    public void StopBackgroundCompiles()
    {
        Thread[]? workers;
        lock (_queueLock)
        {
            _stopping = true;
            _workersStopped = true;
            _asyncCompiles = false;
            foreach (CompileJob job in _demandQueue) job.Cancelled = true;
            foreach (CompileJob job in _prewarmQueue) job.Cancelled = true;
            _demandQueue.Clear();
            _prewarmQueue.Clear();
            workers = _workers;
            Monitor.PulseAll(_queueLock);
        }
        if (workers != null)
        {
            foreach (Thread worker in workers) worker.Join();
        }
        while (_completed.TryDequeue(out CompileJob? job))
        {
            if (job.Pipeline.Handle != 0) _context.Api.DestroyPipeline(_context.Device, job.Pipeline, null);
        }
        _pendingJobs.Clear();
        _pendingByKey.Clear();
    }

    private Result CreatePipeline(PipelineRequest request, Silk.NET.Vulkan.PipelineCache cache,
        PipelineCreateFlags flags, out Pipeline pipeline)
    {
        Vk api = _context.Api;
        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        // A native program's settings are specialization constants (docs/vulkan-native-shaders.md
        // section 5); every stage gets the same map, and an id a module does not declare is ignored.
        NativeSpecialization? specialization = request.Program.Specialization;
        SpecializationInfo* specializationInfo = null;
        if (specialization != null && specialization.Entries.Length > 0)
        {
            nuint entryBytes = (nuint)(sizeof(SpecializationMapEntry) * specialization.Entries.Length);
            var map = (SpecializationMapEntry*)System.Runtime.InteropServices.NativeMemory.Alloc(entryBytes);
            for (int i = 0; i < specialization.Entries.Length; i++)
            {
                NativeSpecialization.Entry entry = specialization.Entries[i];
                map[i] = new SpecializationMapEntry { ConstantID = entry.Id, Offset = entry.Offset, Size = entry.Size };
            }
            var data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Math.Max(specialization.Data.Length, 1));
            specialization.Data.AsSpan().CopyTo(new Span<byte>(data, specialization.Data.Length));
            specializationInfo = (SpecializationInfo*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)sizeof(SpecializationInfo));
            *specializationInfo = new SpecializationInfo
            {
                MapEntryCount = (uint)specialization.Entries.Length,
                PMapEntries = map,
                DataSize = (nuint)specialization.Data.Length,
                PData = data,
            };
        }

        var stages = new List<PipelineShaderStageCreateInfo>();
        foreach (KeyValuePair<EnumShaderType, ShaderModule> module in request.Program.Modules)
        {
            stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = module.Key switch
                {
                    EnumShaderType.VertexShader => ShaderStageFlags.VertexBit,
                    EnumShaderType.FragmentShader => ShaderStageFlags.FragmentBit,
                    _ => ShaderStageFlags.GeometryBit,
                },
                Module = module.Value,
                PName = entryPoint,
                PSpecializationInfo = specializationInfo,
            });
        }

        var bindings = new VertexInputBindingDescription[request.VertexLayout.Bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            VertexBinding binding = request.VertexLayout.Bindings[i];
            bindings[i] = new VertexInputBindingDescription
            {
                Binding = binding.Binding,
                Stride = binding.Stride,
                InputRate = binding.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex,
            };
        }

        var attributes = new VertexInputAttributeDescription[request.VertexLayout.Attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            VertexAttribute attribute = request.VertexLayout.Attributes[i];
            attributes[i] = new VertexInputAttributeDescription
            {
                Location = attribute.Location,
                Binding = attribute.Binding,
                Format = attribute.Format,
                Offset = attribute.Offset,
            };
        }

        var blendAttachments = new PipelineColorBlendAttachmentState[request.Targets.ColorFormats.Length];
        for (int i = 0; i < blendAttachments.Length; i++)
        {
            AttachmentBlend blend = i < request.Blend.Length ? request.Blend[i] : AttachmentBlend.Default;
            // An enabled attachment the fragment shader never stores to keeps
            // its contents, as it does on GL; Vulkan would write undefined
            // values (validation: "Output variable was never written to").
            ColorComponentFlags writeMask = request.Program.Interface.WrittenFragmentOutputs.Contains(i)
                ? blend.WriteMask
                : 0;
            blendAttachments[i] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blend.Enabled,
                SrcColorBlendFactor = blend.SrcColor,
                DstColorBlendFactor = blend.DstColor,
                ColorBlendOp = blend.ColorOp,
                SrcAlphaBlendFactor = blend.SrcAlpha,
                DstAlphaBlendFactor = blend.DstAlpha,
                AlphaBlendOp = blend.AlphaOp,
                ColorWriteMask = writeMask,
            };
        }

        // Everything Vulkan lets us change without a new pipeline, plus the
        // colour write state of the tier. Keeping this list wide is what keeps
        // the cache small.
        DynamicState[] dynamicStates = _dynamicStates;

        try
        {
            fixed (PipelineShaderStageCreateInfo* stagesPtr = stages.ToArray())
            fixed (VertexInputBindingDescription* bindingsPtr = bindings)
            fixed (VertexInputAttributeDescription* attributesPtr = attributes)
            fixed (PipelineColorBlendAttachmentState* blendPtr = blendAttachments)
            fixed (DynamicState* dynamicPtr = dynamicStates)
            fixed (Format* colorFormatsPtr = request.Targets.ColorFormats)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)bindings.Length,
                    PVertexBindingDescriptions = bindings.Length == 0 ? null : bindingsPtr,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = attributes.Length == 0 ? null : attributesPtr,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = request.Topology,
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = request.PolygonMode,
                    // Cull mode and front face are dynamic; these are placeholders.
                    CullMode = CullModeFlags.None,
                    FrontFace = RenderLimits.FrontFace,
                    LineWidth = 1.0f,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };

                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = false,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                    StencilTestEnable = false,
                };

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = (uint)blendAttachments.Length,
                    PAttachments = blendAttachments.Length == 0 ? null : blendPtr,
                };

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates = dynamicPtr,
                };

                // Dynamic rendering names the attachment formats here, so there
                // is no render pass or framebuffer object anywhere in the design.
                var renderingInfo = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = (uint)request.Targets.ColorFormats.Length,
                    PColorAttachmentFormats = request.Targets.ColorFormats.Length == 0 ? null : colorFormatsPtr,
                    DepthAttachmentFormat = request.Targets.DepthFormat,
                };

                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    // FAIL_ON_PIPELINE_COMPILE_REQUIRED for a warm attempt: the driver
                    // returns VK_PIPELINE_COMPILE_REQUIRED instead of compiling.
                    Flags = flags,
                    PNext = &renderingInfo,
                    StageCount = (uint)stages.Count,
                    PStages = stagesPtr,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = request.Program.PipelineLayout,
                };

                Result result = api.CreateGraphicsPipelines(
                    _context.Device, cache, 1, &createInfo, null, out pipeline);
                if (result != Result.Success) pipeline = default;
                return result;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
            if (specializationInfo != null)
            {
                System.Runtime.InteropServices.NativeMemory.Free(specializationInfo->PMapEntries);
                System.Runtime.InteropServices.NativeMemory.Free(specializationInfo->PData);
                System.Runtime.InteropServices.NativeMemory.Free(specializationInfo);
            }
        }
    }

    /// <summary>
    /// The driver's cache blob, to be written next to the SPIR-V cache so the
    /// next run starts warm (<see cref="PipelineCacheFile" />).
    /// </summary>
    public byte[] SerializeDriverCache()
    {
        if (_driverCache.Handle == 0) return Array.Empty<byte>();
        lock (_driverCacheLock) return SerializeDriverCacheLocked();
    }

    /// <summary>The serialised driver cache size in bytes, without copying it. Safe from any thread.</summary>
    public long DriverCacheSize()
    {
        if (_driverCache.Handle == 0) return 0;
        lock (_driverCacheLock)
        {
            nuint size = 0;
            return _context.Api.GetPipelineCacheData(_context.Device, _driverCache, ref size, null) == Result.Success
                ? (long)size
                : 0;
        }
    }

    private byte[] SerializeDriverCacheLocked()
    {
        // The cache can grow between the size query and the fetch while another
        // thread creates a pipeline; VK_INCOMPLETE then means "ask again".
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nuint size = 0;
            if (_context.Api.GetPipelineCacheData(_context.Device, _driverCache, ref size, null) != Result.Success ||
                size == 0)
            {
                return Array.Empty<byte>();
            }

            var data = new byte[(int)size];
            Result result;
            fixed (byte* dataPtr = data)
            {
                result = _context.Api.GetPipelineCacheData(_context.Device, _driverCache, ref size, dataPtr);
            }
            if (result == Result.Success) return size == (nuint)data.Length ? data : data[..(int)size];
            if (result != Result.Incomplete) break;
        }
        return Array.Empty<byte>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopBackgroundCompiles();

        Vk api = _context.Api;
        // One pipeline can serve several keys (equal content under different interned ids).
        var destroyed = new HashSet<ulong>();
        foreach (Pipeline pipeline in _pipelines.Values)
        {
            if (destroyed.Add(pipeline.Handle)) api.DestroyPipeline(_context.Device, pipeline, null);
        }
        _pipelines.Clear();
        foreach (Pipeline pipeline in _prewarmed.Values)
        {
            if (destroyed.Add(pipeline.Handle)) api.DestroyPipeline(_context.Device, pipeline, null);
        }
        _prewarmed.Clear();

        if (_driverCache.Handle != 0)
        {
            api.DestroyPipelineCache(_context.Device, _driverCache, null);
        }
    }
}
