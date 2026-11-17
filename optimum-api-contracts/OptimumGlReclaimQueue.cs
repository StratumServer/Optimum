using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Lock-free finalizer reclamation queues for undisposed OpenGL VAO, buffer,
/// and texture names. Finalizers enqueue handles tagged with the active GL
/// context generation and never call OpenGL off the render thread.
/// </summary>
public static class OptimumGlReclaimQueue
{
    public const int MinBatchPerFrame = 64;
    public const int MaxBatchPerFrame = 1024;
    internal const long LeakLogIntervalMs = 30_000L;
    internal const long TraceDebugLogBurstLimit = 64L;

    private static readonly ConcurrentQueue<long> vaoQueue = new();
    private static readonly ConcurrentQueue<long> bufferQueue = new();
    private static readonly ConcurrentQueue<long> textureQueue = new();

    private static int contextGeneration = 1;
    private static volatile bool glShuttingDown;
    private static volatile bool loadedTextureFinalizerPatchedByMod;
    private static volatile bool vaoFinalizerPatchedByMod;

    private static long pendingVaos;
    private static long pendingBuffers;
    private static long pendingTextures;
    private static long reclaimedVaos;
    private static long reclaimedBuffers;
    private static long reclaimedTextures;
    private static long drainRuns;

    private static long lastVaoLeakLogMs = -LeakLogIntervalMs;
    private static long lastTextureLeakLogMs = -LeakLogIntervalMs;
    private static long vaoTraceLogCount;
    private static long textureTraceLogCount;

    public static bool IsGlShuttingDown => glShuttingDown;
    public static int ContextGeneration => Volatile.Read(ref contextGeneration);
    public static bool LoadedTextureFinalizerPatchedByMod => loadedTextureFinalizerPatchedByMod;
    public static bool VaoFinalizerPatchedByMod => vaoFinalizerPatchedByMod;

    public static long PendingVaoCount => Math.Max(0L, Volatile.Read(ref pendingVaos));
    public static long PendingBufferCount => Math.Max(0L, Volatile.Read(ref pendingBuffers));
    public static long PendingTextureCount => Math.Max(0L, Volatile.Read(ref pendingTextures));
    public static long PendingTotalCount => PendingVaoCount + PendingBufferCount + PendingTextureCount;

    public static long ReclaimedVaoCount => Interlocked.Read(ref reclaimedVaos);
    public static long ReclaimedBufferCount => Interlocked.Read(ref reclaimedBuffers);
    public static long ReclaimedTextureCount => Interlocked.Read(ref reclaimedTextures);
    public static long TotalReclaimedCount => ReclaimedVaoCount + ReclaimedBufferCount + ReclaimedTextureCount;
    public static long DrainCount => Interlocked.Read(ref drainRuns);

    public static bool HasPending =>
        (Volatile.Read(ref pendingVaos) | Volatile.Read(ref pendingBuffers) | Volatile.Read(ref pendingTextures)) != 0L;

    public static void SetGlShuttingDown(bool shuttingDown)
    {
        glShuttingDown = shuttingDown;
    }

    public static void BeginNewContext()
    {
        Interlocked.Increment(ref contextGeneration);
        ClearPendingQueues();
        glShuttingDown = false;
    }

    public static void ClearPendingQueues()
    {
        while (vaoQueue.TryDequeue(out _)) { }
        while (bufferQueue.TryDequeue(out _)) { }
        while (textureQueue.TryDequeue(out _)) { }
        Volatile.Write(ref pendingVaos, 0L);
        Volatile.Write(ref pendingBuffers, 0L);
        Volatile.Write(ref pendingTextures, 0L);
    }

    public static void EndContextAndAdvanceGeneration()
    {
        glShuttingDown = true;
        Interlocked.Increment(ref contextGeneration);
        ClearPendingQueues();
    }

    public static void EnqueueVao(int vaoId)
    {
        if (vaoId <= 0 || glShuttingDown)
        {
            return;
        }

        int generation = Volatile.Read(ref contextGeneration);
        vaoQueue.Enqueue(((long)generation << 32) | (uint)vaoId);
        Interlocked.Increment(ref pendingVaos);
    }

    public static void EnqueueBuffer(int bufferId)
    {
        if (bufferId <= 0 || glShuttingDown)
        {
            return;
        }

        int generation = Volatile.Read(ref contextGeneration);
        bufferQueue.Enqueue(((long)generation << 32) | (uint)bufferId);
        Interlocked.Increment(ref pendingBuffers);
    }

    public static void EnqueueTexture(int textureId)
    {
        if (textureId <= 0 || glShuttingDown)
        {
            return;
        }

        int generation = Volatile.Read(ref contextGeneration);
        textureQueue.Enqueue(((long)generation << 32) | (uint)textureId);
        Interlocked.Increment(ref pendingTextures);
    }

    public static void EnqueueVaoWithBuffers(
        int vaoId,
        int xyzVboId,
        int normalsVboId,
        int uvVboId,
        int rgbaVboId,
        int flagsVboId,
        int customFloatVboId,
        int customIntVboId,
        int customShortVboId,
        int customByteVboId,
        int indexVboId)
    {
        if (glShuttingDown)
        {
            return;
        }

        int generation = Volatile.Read(ref contextGeneration);
        if (vaoId > 0)
        {
            vaoQueue.Enqueue(((long)generation << 32) | (uint)vaoId);
            Interlocked.Increment(ref pendingVaos);
        }

        EnqueueBufferWithGeneration(generation, xyzVboId);
        EnqueueBufferWithGeneration(generation, normalsVboId);
        EnqueueBufferWithGeneration(generation, uvVboId);
        EnqueueBufferWithGeneration(generation, rgbaVboId);
        EnqueueBufferWithGeneration(generation, flagsVboId);
        EnqueueBufferWithGeneration(generation, customFloatVboId);
        EnqueueBufferWithGeneration(generation, customIntVboId);
        EnqueueBufferWithGeneration(generation, customShortVboId);
        EnqueueBufferWithGeneration(generation, customByteVboId);
        EnqueueBufferWithGeneration(generation, indexVboId);
    }

    private static void EnqueueBufferWithGeneration(int generation, int bufferId)
    {
        if (bufferId <= 0)
        {
            return;
        }

        bufferQueue.Enqueue(((long)generation << 32) | (uint)bufferId);
        Interlocked.Increment(ref pendingBuffers);
    }

    public static int ComputeDynamicBatchBudget()
    {
        long total = PendingTotalCount;
        long scaled = total >> 3;
        if (scaled <= MinBatchPerFrame)
        {
            return MinBatchPerFrame;
        }
        return scaled >= MaxBatchPerFrame ? MaxBatchPerFrame : (int)scaled;
    }

    public static int DequeueVaos(Span<int> destination)
    {
        return DequeueCore(vaoQueue, ref pendingVaos, destination, excludeId: 0);
    }

    public static int DequeueBuffers(Span<int> destination, int sharedIndexBufferId)
    {
        return DequeueCore(bufferQueue, ref pendingBuffers, destination, excludeId: sharedIndexBufferId);
    }

    public static int DequeueTextures(Span<int> destination)
    {
        return DequeueCore(textureQueue, ref pendingTextures, destination, excludeId: 0);
    }

    public static void RecordReclaimed(int vaoCount, int bufferCount, int textureCount)
    {
        if (vaoCount > 0) Interlocked.Add(ref reclaimedVaos, vaoCount);
        if (bufferCount > 0) Interlocked.Add(ref reclaimedBuffers, bufferCount);
        if (textureCount > 0) Interlocked.Add(ref reclaimedTextures, textureCount);
        if ((vaoCount | bufferCount | textureCount) != 0)
        {
            Interlocked.Increment(ref drainRuns);
        }
    }

    private static int DequeueCore(
        ConcurrentQueue<long> queue,
        ref long pendingCounter,
        Span<int> destination,
        int excludeId)
    {
        int capacity = destination.Length;
        int generation = Volatile.Read(ref contextGeneration);
        int count = 0;

        while (count < capacity && queue.TryDequeue(out long packed))
        {
            Interlocked.Decrement(ref pendingCounter);
            int entryGen = (int)(packed >> 32);
            int handleId = unchecked((int)(uint)packed);
            if (entryGen != generation || handleId <= 0 || handleId == excludeId)
            {
                continue;
            }
            destination[count++] = handleId;
        }

        return count;
    }

    public static bool ShouldLogVaoLeak(bool traceEnabled)
    {
        if (traceEnabled && Interlocked.Increment(ref vaoTraceLogCount) <= TraceDebugLogBurstLimit)
        {
            return true;
        }
        return TryClaimLeakLogSlot(ref lastVaoLeakLogMs);
    }

    public static bool ShouldLogTextureLeak(bool traceEnabled)
    {
        if (traceEnabled && Interlocked.Increment(ref textureTraceLogCount) <= TraceDebugLogBurstLimit)
        {
            return true;
        }
        return TryClaimLeakLogSlot(ref lastTextureLeakLogMs);
    }

    private static bool TryClaimLeakLogSlot(ref long lastLogMs)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref lastLogMs);
        if (now - previous < LeakLogIntervalMs)
        {
            return false;
        }
        return Interlocked.CompareExchange(ref lastLogMs, now, previous) == previous;
    }

    /// <summary>
    /// Checks whether a third-party Harmony mod patched LoadedTexture.Finalize or VAO.Finalize
    /// so Optimum avoids double-deleting OpenGL object names reclaimed by that mod.
    /// </summary>
    public static void RefreshExternalFinalizerPatchDetection(Type? loadedTextureType, Type? vaoType)
    {
        loadedTextureFinalizerPatchedByMod = HasExternalHarmonyFinalizerPatch(loadedTextureType);
        vaoFinalizerPatchedByMod = HasExternalHarmonyFinalizerPatch(vaoType);
    }

    internal static void SetExternalFinalizerPatchOverridesForTest(bool loadedTexturePatched, bool vaoPatched)
    {
        loadedTextureFinalizerPatchedByMod = loadedTexturePatched;
        vaoFinalizerPatchedByMod = vaoPatched;
    }

    internal static bool HasExternalHarmonyFinalizerPatch(Type? targetType)
    {
        if (targetType == null)
        {
            return false;
        }

        MethodInfo? finalizer = targetType.GetMethod(
            "Finalize",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (finalizer == null)
        {
            return false;
        }

        MethodInfo? getPatchInfo = FindHarmonyGetPatchInfoMethod();
        if (getPatchInfo == null)
        {
            return false;
        }

        try
        {
            object? patchInfo = getPatchInfo.Invoke(null, new object[] { finalizer });
            if (patchInfo == null)
            {
                return false;
            }

            Type patchInfoType = patchInfo.GetType();
            return HasExternalPatchEntries(patchInfoType, patchInfo, "Prefixes") ||
                   HasExternalPatchEntries(patchInfoType, patchInfo, "Postfixes") ||
                   HasExternalPatchEntries(patchInfoType, patchInfo, "Transpilers") ||
                   HasExternalPatchEntries(patchInfoType, patchInfo, "Finalizers");
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindHarmonyGetPatchInfoMethod()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type? harmonyType = assemblies[i].GetType("HarmonyLib.Harmony", throwOnError: false);
            if (harmonyType == null)
            {
                continue;
            }

            MethodInfo? method = harmonyType.GetMethod(
                "GetPatchInfo",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(MethodBase) },
                null);
            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    private static bool HasExternalPatchEntries(Type patchInfoType, object patchInfo, string memberName)
    {
        object? collection = patchInfoType.GetField(memberName)?.GetValue(patchInfo)
            ?? patchInfoType.GetProperty(memberName)?.GetValue(patchInfo);
        if (collection is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        foreach (object? entry in enumerable)
        {
            if (entry == null)
            {
                continue;
            }

            string? owner = entry.GetType().GetField("owner")?.GetValue(entry) as string
                ?? entry.GetType().GetProperty("owner")?.GetValue(entry) as string;
            if (owner == null || !owner.StartsWith("Optimum", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void ResetForTests()
    {
        ClearPendingQueues();
        Volatile.Write(ref reclaimedVaos, 0L);
        Volatile.Write(ref reclaimedBuffers, 0L);
        Volatile.Write(ref reclaimedTextures, 0L);
        Volatile.Write(ref drainRuns, 0L);
        Volatile.Write(ref lastVaoLeakLogMs, -LeakLogIntervalMs);
        Volatile.Write(ref lastTextureLeakLogMs, -LeakLogIntervalMs);
        Volatile.Write(ref vaoTraceLogCount, 0L);
        Volatile.Write(ref textureTraceLogCount, 0L);
        loadedTextureFinalizerPatchedByMod = false;
        vaoFinalizerPatchedByMod = false;
        glShuttingDown = false;
        Interlocked.Increment(ref contextGeneration);
    }
}
