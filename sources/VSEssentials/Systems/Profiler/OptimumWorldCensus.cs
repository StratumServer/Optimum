#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.GameContent;

/// <summary>
/// Engine, chunk, MeshDataPool, entity, shader, GUI and Optimum diagnostic state, read through
/// reflection accessors resolved once per type and member. The light census (counters only) runs
/// once per second; the detailed census (chunk scan, BlockEntity histogram, shaders, Optimum
/// summaries) runs only while a dialog tab shows it or an export needs it.
/// </summary>
public sealed class OptimumWorldCensus
{
    private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly ICoreClientAPI capi;
    private readonly Type? configType;
    private readonly Type? diagnosticsType;
    private readonly Type? chunkStreamStatsType;
    private readonly Type? circuitBreakerType;
    private readonly Dictionary<(Type, string), System.Func<object?, object?>?> accessors = new();
    private MethodInfo[]? summaryMethods;
    private Type? isPackedChunkType;
    private MethodInfo? isPacked;
    private object[] chunkScratch = Array.Empty<object>();

    public OptimumWorldCensus(ICoreClientAPI capi)
    {
        this.capi = capi;
        Assembly apiAsm = typeof(ICoreClientAPI).Assembly;
        configType = apiAsm.GetType("Vintagestory.API.Config.OptimumConfig");
        diagnosticsType = apiAsm.GetType("Vintagestory.API.Config.OptimumDiagnostics");
        chunkStreamStatsType = apiAsm.GetType("Vintagestory.API.Config.OptimumChunkStreamStats");
        circuitBreakerType = apiAsm.GetType("Vintagestory.API.Config.OptimumCircuitBreakerRegistry");
    }

    public string OptimumVersion => Read(configType, null, "Version") as string ?? "unknown";

    public ProfilerCensusSnapshot Collect(OptimumGpuTimerRing? gl, bool includeDetails)
    {
        object? world = capi.World;
        if (world == null)
        {
            return new ProfilerCensusSnapshot { OptimumVersion = OptimumVersion };
        }

        Type wt = world.GetType();
        Type? runtimeStats = wt.Assembly.GetType("Vintagestory.Client.RuntimeStats");
        object? worldMap = Read(wt, world, "WorldMap");
        int entLoaded = 0, entRendered = 0, entShadow = 0, entAnim = 0;
        foreach (Entity ent in capi.World.LoadedEntities.Values)
        {
            entLoaded++;
            if (ent.IsRendered) entRendered++;
            if (ent.IsShadowRendered) entShadow++;
            if (ent.AnimManager?.Animator?.ActiveAnimationCount > 0) entAnim++;
        }

        var guis = new List<string>();
        foreach (GuiDialog dialog in capi.Gui.OpenedGuis)
        {
            guis.Add(dialog.GetType().Name);
        }

        ChunkScan scan = includeDetails ? ScanChunks(worldMap) : default;
        (int shaders, int shaderErrors) = includeDetails ? CountShaders(wt) : (0, 0);

        return new ProfilerCensusSnapshot
        {
            GlVendor = gl?.GlVendor ?? "",
            GlRenderer = gl?.GlRenderer ?? "",
            GlVersion = gl?.GlVersion ?? "",
            OptimumVersion = OptimumVersion,
            ChunksTotal = Read(worldMap?.GetType(), worldMap, "chunks") is ICollection chunks ? chunks.Count : 0,
            ChunksEmpty = scan.Empty,
            ChunksUnpacked = scan.Unpacked,
            ChunksAwaitingTessellation = Read(runtimeStats, null, "chunksAwaitingTesselation") as int? ?? 0,
            ChunksTesselatedPerSecond = Read(runtimeStats, null, "chunksTesselatedPerSecond") as int? ?? 0,
            ChunksTesselatedTotal = Read(runtimeStats, null, "chunksTesselatedTotal") as int? ?? 0,
            DirtyQueueCount = CountOf(wt, world, "dirtyChunks") + CountOf(wt, world, "dirtyChunksPriority"),
            UploadedQueueCount = CountOf(wt, world, "tesselatedChunks") + CountOf(wt, world, "tesselatedChunksPriority"),
            EntitiesLoaded = entLoaded,
            EntitiesRendered = entRendered,
            EntitiesShadowRendered = entShadow,
            EntitiesActiveAnimators = entAnim,
            ShaderCount = shaders,
            ShaderLoadErrors = shaderErrors,
            OpenedGuiCount = guis.Count,
            OpenedGuis = guis,
            MeshPools = CollectMeshPools(wt, world),
            ShadowFarRenderersSummary = includeDetails ? SummarizeShadowFarRenderers(wt, world) : "",
            TopBlockEntities = scan.TopBlockEntities ?? (IReadOnlyList<KeyValuePair<string, int>>)Array.Empty<KeyValuePair<string, int>>(),
        };
    }

    /// <summary>
    /// Adds Optimum's toggles, counters and summaries. They only read static, thread-safe
    /// diagnostics, so exports run this on their worker; it does not touch the accessor cache.
    /// </summary>
    public ProfilerCensusSnapshot WithDiagnostics(ProfilerCensusSnapshot census) => census with
    {
        OptimumToggles = Safely(CollectToggles, Array.Empty<KeyValuePair<string, string>>()),
        OptimumCounters = Safely(CollectCounters, Array.Empty<ProfilerOptimumCounterStat>()),
        OptimumSummaries = Safely(CollectSummaries, Array.Empty<KeyValuePair<string, string>>()),
    };

    /// <summary>
    /// A diagnostics dictionary mutated by the game while a worker enumerates it throws; that
    /// section is left empty and the rest of the bundle is still written.
    /// </summary>
    private static IReadOnlyList<T> Safely<T>(System.Func<IReadOnlyList<T>> collect, IReadOnlyList<T> fallback)
    {
        try
        {
            return collect();
        }
        catch (Exception e) when (e is InvalidOperationException or TargetInvocationException)
        {
            return fallback;
        }
    }

    private struct ChunkScan
    {
        public int Empty;
        public int Unpacked;
        public List<KeyValuePair<string, int>>? TopBlockEntities;
    }

    /// <summary>
    /// Copies chunk references under the world map lock (which chunk loading, culling and
    /// compression threads share) and inspects them after releasing it.
    /// </summary>
    private ChunkScan ScanChunks(object? worldMap)
    {
        var scan = new ChunkScan();
        Type? wmt = worldMap?.GetType();
        if (Read(wmt, worldMap, "chunks") is not IDictionary chunks || Read(wmt, worldMap, "chunksLock") is not object chunksLock)
        {
            return scan;
        }

        int count;
        lock (chunksLock)
        {
            count = chunks.Count;
            if (chunkScratch.Length < count)
            {
                chunkScratch = new object[count + count / 4];
            }
            chunks.Values.CopyTo(chunkScratch, 0);
        }

        var beCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (chunkScratch[i] is not IWorldChunk chunk)
            {
                continue;
            }
            if (chunk.Empty) scan.Empty++;
            if (IsPacked(chunk) == false) scan.Unpacked++;
            foreach (BlockEntity be in chunk.BlockEntities.Values)
            {
                string n = be.GetType().Name;
                beCounts[n] = beCounts.TryGetValue(n, out int c) ? c + 1 : 1;
            }
        }
        Array.Clear(chunkScratch, 0, count);
        scan.TopBlockEntities = beCounts.OrderByDescending(k => k.Value).Take(12).ToList();
        return scan;
    }

    private bool? IsPacked(object chunk)
    {
        Type t = chunk.GetType();
        if (t != isPackedChunkType)
        {
            isPackedChunkType = t;
            isPacked = t.GetMethod("IsPacked", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        }
        return isPacked?.Invoke(chunk, null) as bool?;
    }

    private IReadOnlyList<ProfilerMeshPoolPassStat> CollectMeshPools(Type wt, object world)
    {
        object? chunkRenderer = Read(wt, world, "chunkRenderer");
        if (Read(chunkRenderer?.GetType(), chunkRenderer, "poolsByRenderPass") is not Array passes)
        {
            return Array.Empty<ProfilerMeshPoolPassStat>();
        }

        var result = new List<ProfilerMeshPoolPassStat>(passes.Length);
        for (int p = 0; p < passes.Length; p++)
        {
            int poolCount = 0, usedVerts = 0, capVerts = 0, allocTris = 0, rendTris = 0, groups = 0;
            if (passes.GetValue(p) is Array managers)
            {
                foreach (object? manager in managers)
                {
                    if (Read(manager?.GetType(), manager, "pools") is not IList pools)
                    {
                        continue;
                    }
                    poolCount += pools.Count;
                    foreach (object? pool in pools)
                    {
                        if (pool is MeshDataPool mp)
                        {
                            usedVerts += mp.UsedVertices;
                            capVerts += mp.VerticesPoolSize;
                            allocTris += mp.AllocatedTris;
                            rendTris += mp.RenderedTriangles;
                            groups += mp.indicesGroupsCount;
                        }
                    }
                }
            }
            result.Add(new ProfilerMeshPoolPassStat
            {
                Pass = ((EnumChunkRenderPass)p).ToString(),
                PoolCount = poolCount,
                UsedVertices = usedVerts,
                CapacityVertices = capVerts,
                AllocatedTris = allocTris,
                RenderedTris = rendTris,
                Groups = groups,
            });
        }
        return result;
    }

    private string SummarizeShadowFarRenderers(Type wt, object world)
    {
        object? eventManager = Read(wt, world, "eventManager");
        if (Read(eventManager?.GetType(), eventManager, "renderersByStage") is not Array stages ||
            stages.GetValue((int)EnumRenderStage.ShadowFar) is not IList handlers)
        {
            return "";
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object? handler in handlers)
        {
            string name = Read(handler?.GetType(), handler, "ProfilingName") as string ?? "?";
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }
        return string.Join(", ", counts.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key}x{k.Value}"));
    }

    private (int Count, int Errors) CountShaders(Type wt)
    {
        Type? registry = wt.Assembly.GetType("Vintagestory.Client.NoObf.ShaderRegistry");
        if (Read(registry, null, "shaderPrograms") is not Array programs)
        {
            return (0, 0);
        }
        int count = 0, errors = 0;
        foreach (object? program in programs)
        {
            if (program == null) continue;
            count++;
            if (Read(program.GetType(), program, "LoadError") is true) errors++;
        }
        return (count, errors);
    }

    private IReadOnlyList<KeyValuePair<string, string>> CollectToggles()
    {
        var list = new List<KeyValuePair<string, string>>();
        if (configType?.GetMethod("DescribeToggles", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is IEnumerable toggles)
        {
            foreach (object? item in toggles)
            {
                if (item is ValueTuple<string, string> vt)
                {
                    list.Add(new KeyValuePair<string, string>(vt.Item1, vt.Item2));
                }
            }
        }
        return list;
    }

    private IReadOnlyList<ProfilerOptimumCounterStat> CollectCounters()
    {
        var list = new List<ProfilerOptimumCounterStat>();
        if (diagnosticsType?.GetField("Counters", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IDictionary counters)
        {
            foreach (DictionaryEntry de in counters)
            {
                if (de.Value?.GetType().GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Instance)?.Invoke(de.Value, null) is ValueTuple<long, long> hs)
                {
                    long total = hs.Item1 + hs.Item2;
                    list.Add(new ProfilerOptimumCounterStat
                    {
                        Name = de.Key?.ToString() ?? "?",
                        Hits = hs.Item1,
                        Skips = hs.Item2,
                        SkipRatePct = total > 0 ? hs.Item2 * 100.0 / total : 0.0,
                    });
                }
            }
        }
        list.Sort((a, b) => (b.Hits + b.Skips).CompareTo(a.Hits + a.Skips));
        return list;
    }

    private IReadOnlyList<KeyValuePair<string, string>> CollectSummaries()
    {
        summaryMethods ??= (diagnosticsType?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length == 0 &&
                        m.Name.StartsWith("Get", StringComparison.Ordinal) && m.Name.EndsWith("Summary", StringComparison.Ordinal))
            .Concat(new[]
            {
                chunkStreamStatsType?.GetMethod("GetSummary", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes),
                circuitBreakerType?.GetMethod("GetBreakersSummary", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes),
            }.OfType<MethodInfo>())
            .ToArray();

        var list = new List<KeyValuePair<string, string>>(summaryMethods.Length);
        foreach (MethodInfo m in summaryMethods)
        {
            try
            {
                if (m.Invoke(null, null) is string s && s.Length > 0)
                {
                    list.Add(new KeyValuePair<string, string>(m.DeclaringType == diagnosticsType ? m.Name : m.DeclaringType!.Name, s));
                }
            }
            catch (TargetInvocationException)
            {
                // A summary that throws is skipped; the rest of the census stays usable.
            }
        }
        return list;
    }

    private int CountOf(Type t, object target, string name)
    {
        object? value = Read(t, target, name);
        if (value is ICollection c)
        {
            return c.Count;
        }
        return value != null && Read(value.GetType(), value, "Count") is int n ? n : 0;
    }

    private object? Read(Type? type, object? target, string name)
    {
        if (type == null)
        {
            return null;
        }
        if (!accessors.TryGetValue((type, name), out System.Func<object?, object?>? get))
        {
            FieldInfo? fi = type.GetField(name, AnyMember);
            PropertyInfo? pi = fi == null ? type.GetProperty(name, AnyMember) : null;
            get = fi != null ? fi.GetValue : pi != null && pi.GetIndexParameters().Length == 0 ? pi.GetValue : null;
            accessors[(type, name)] = get;
        }
        return get?.Invoke(target);
    }
}
