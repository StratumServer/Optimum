#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace Vintagestory.GameContent;

/// <summary>
/// Collects engine, chunk, MeshDataPool, entity, shader, GUI, and Optimum diagnostic state
/// on demand via lazily resolved reflection handles so dormant gameplay pays zero cost.
/// </summary>
public sealed class OptimumWorldCensus
{
    private readonly ICoreClientAPI capi;
    private readonly Assembly apiAsm;

    private Type? optimumConfigType;
    private Type? optimumDiagnosticsType;
    private Type? optimumChunkStreamStatsType;
    private Type? optimumCircuitBreakerType;

    public OptimumWorldCensus(ICoreClientAPI capi)
    {
        this.capi = capi;
        this.apiAsm = typeof(ICoreClientAPI).Assembly;
        ResolveOptimumTypes();
    }

    private void ResolveOptimumTypes()
    {
        optimumConfigType = apiAsm.GetType("Vintagestory.API.Config.OptimumConfig");
        optimumDiagnosticsType = apiAsm.GetType("Vintagestory.API.Config.OptimumDiagnostics");
        optimumChunkStreamStatsType = apiAsm.GetType("Vintagestory.API.Config.OptimumChunkStreamStats");
        optimumCircuitBreakerType = apiAsm.GetType("Vintagestory.API.Config.OptimumCircuitBreakerRegistry");
    }

    public string GetOptimumVersion()
    {
        try
        {
            FieldInfo? vf = optimumConfigType?.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            if (vf?.GetValue(null) is string s && !string.IsNullOrEmpty(s))
            {
                return s;
            }
        }
        catch
        {
        }
        return "unknown";
    }

    public ProfilerCensusSnapshot Collect(string glVendor, string glRenderer, string glVersion)
    {
        int chunksTotal = 0, chunksEmpty = 0, chunksUnpacked = 0;
        int awaitingTess = 0, tessPerSec = 0, tessTotal = 0;
        int dirtyCount = 0, uploadedCount = 0;
        int entLoaded = 0, entRendered = 0, entShadow = 0, entAnim = 0;
        int shaderCount = 0, shaderErr = 0;
        var beCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var poolsOut = new List<ProfilerMeshPoolPassStat>();
        var guisOut = new List<string>();
        string shadowFarRenderers = "";

        try
        {
            object? world = capi.World;
            if (world != null)
            {
                Type wt = world.GetType();

                Type? rsType = wt.Assembly.GetType("Vintagestory.Client.RuntimeStats")
                    ?? wt.Assembly.GetType("Vintagestory.Client.NoObf.RuntimeStats")
                    ?? apiAsm.GetType("Vintagestory.API.Config.RuntimeStats");
                if (rsType != null)
                {
                    awaitingTess = ReadInt(rsType, null, "chunksAwaitingTesselation");
                    tessPerSec = ReadInt(rsType, null, "chunksTesselatedPerSecond");
                    tessTotal = ReadInt(rsType, null, "chunksTesselatedTotal");
                }

                dirtyCount = CountField(wt, world, "dirtyChunks") + CountField(wt, world, "dirtyChunksPriority");
                uploadedCount = CountField(wt, world, "tesselatedChunks") + CountField(wt, world, "tesselatedChunksPriority");

                object? worldMap = ReadMember(wt, world, "WorldMap");
                if (worldMap != null)
                {
                    Type wmt = worldMap.GetType();
                    object? chunksLock = ReadMember(wmt, worldMap, "chunksLock");
                    object? chunksObj = ReadMember(wmt, worldMap, "chunks");
                    if (chunksObj is IDictionary dict && chunksLock != null)
                    {
                        lock (chunksLock)
                        {
                            chunksTotal = dict.Count;
                            foreach (object? chunk in dict.Values)
                            {
                                if (chunk == null) continue;
                                Type ct = chunk.GetType();
                                if (ReadBool(ct, chunk, "Empty")) chunksEmpty++;
                                bool? isPacked = InvokeBool(ct, chunk, "IsPacked");
                                if (isPacked == false) chunksUnpacked++;

                                if (ReadMember(ct, chunk, "BlockEntities") is IDictionary beDict)
                                {
                                    foreach (object? be in beDict.Values)
                                    {
                                        if (be == null) continue;
                                        string n = be.GetType().Name;
                                        beCounts[n] = beCounts.TryGetValue(n, out int c) ? c + 1 : 1;
                                    }
                                }
                            }
                        }
                    }
                }

                object? chunkRenderer = ReadMember(wt, world, "chunkRenderer");
                if (chunkRenderer != null && ReadMember(chunkRenderer.GetType(), chunkRenderer, "poolsByRenderPass") is Array passArr)
                {
                    string[] passNames = Enum.GetNames(typeof(EnumChunkRenderPass));
                    for (int p = 0; p < passArr.Length; p++)
                    {
                        if (passArr.GetValue(p) is not Array mgrArr) continue;
                        int poolCount = 0, usedVerts = 0, capVerts = 0, allocTris = 0, rendTris = 0, groups = 0;
                        for (int a = 0; a < mgrArr.Length; a++)
                        {
                            object? mgr = mgrArr.GetValue(a);
                            if (mgr == null) continue;
                            if (ReadMember(mgr.GetType(), mgr, "pools") is IList pools)
                            {
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
                        poolsOut.Add(new ProfilerMeshPoolPassStat
                        {
                            Pass = p < passNames.Length ? passNames[p] : $"Pass{p}",
                            PoolCount = poolCount,
                            UsedVertices = usedVerts,
                            CapacityVertices = capVerts,
                            AllocatedTris = allocTris,
                            RenderedTris = rendTris,
                            Groups = groups,
                        });
                    }
                }

                object? evMgr = ReadMember(wt, world, "eventManager");
                if (evMgr != null && ReadMember(evMgr.GetType(), evMgr, "renderersByStage") is Array stageArr)
                {
                    int sfIdx = (int)EnumRenderStage.ShadowFar;
                    if (sfIdx >= 0 && sfIdx < stageArr.Length && stageArr.GetValue(sfIdx) is IList sfList)
                    {
                        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (object? rh in sfList)
                        {
                            if (rh == null) continue;
                            string pn = ReadMember(rh.GetType(), rh, "ProfilingName") as string ?? "?";
                            counts[pn] = counts.TryGetValue(pn, out int c) ? c + 1 : 1;
                        }
                        shadowFarRenderers = string.Join(", ", counts.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key}x{k.Value}"));
                    }
                }

                Type? srType = wt.Assembly.GetType("Vintagestory.Client.NoObf.ShaderRegistry");
                if (srType != null && ReadMember(srType, null, "shaderPrograms") is Array progs)
                {
                    for (int i = 0; i < progs.Length; i++)
                    {
                        object? sp = progs.GetValue(i);
                        if (sp == null) continue;
                        shaderCount++;
                        if (ReadBool(sp.GetType(), sp, "LoadError")) shaderErr++;
                    }
                }
            }

            if (capi.World?.LoadedEntities != null)
            {
                entLoaded = capi.World.LoadedEntities.Count;
                foreach (Entity ent in capi.World.LoadedEntities.Values)
                {
                    if (ent == null) continue;
                    if (ent.IsRendered) entRendered++;
                    if (ent.IsShadowRendered) entShadow++;
                    if (ent.AnimManager?.Animator != null && ent.AnimManager.Animator.ActiveAnimationCount > 0) entAnim++;
                }
            }

            if (capi.Gui?.OpenedGuis != null)
            {
                foreach (object? g in capi.Gui.OpenedGuis)
                {
                    if (g != null) guisOut.Add(g.GetType().Name);
                }
            }
        }
        catch
        {
            // Keep partial census resilient during shutdown/teardown
        }

        return new ProfilerCensusSnapshot
        {
            GlVendor = glVendor,
            GlRenderer = glRenderer,
            GlVersion = glVersion,
            OptimumVersion = GetOptimumVersion(),
            ChunksTotal = chunksTotal,
            ChunksEmpty = chunksEmpty,
            ChunksUnpacked = chunksUnpacked,
            ChunksAwaitingTessellation = awaitingTess,
            ChunksTesselatedPerSecond = tessPerSec,
            ChunksTesselatedTotal = tessTotal,
            DirtyQueueCount = dirtyCount,
            UploadedQueueCount = uploadedCount,
            EntitiesLoaded = entLoaded,
            EntitiesRendered = entRendered,
            EntitiesShadowRendered = entShadow,
            EntitiesActiveAnimators = entAnim,
            ShaderCount = shaderCount,
            ShaderLoadErrors = shaderErr,
            OpenedGuiCount = guisOut.Count,
            ShadowFarRenderersSummary = shadowFarRenderers,
            MeshPools = poolsOut,
            TopBlockEntities = beCounts.OrderByDescending(k => k.Value).Take(12).ToList(),
            OpenedGuis = guisOut,
            OptimumToggles = CollectOptimumToggles(),
            OptimumCounters = CollectOptimumCounters(),
            OptimumSummaries = CollectOptimumSummaries(),
        };
    }

    private IReadOnlyList<KeyValuePair<string, string>> CollectOptimumToggles()
    {
        var list = new List<KeyValuePair<string, string>>();
        try
        {
            MethodInfo? desc = optimumConfigType?.GetMethod("DescribeToggles", BindingFlags.Public | BindingFlags.Static);
            if (desc?.Invoke(null, null) is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    if (item is ValueTuple<string, string> vt)
                    {
                        list.Add(new KeyValuePair<string, string>(vt.Item1, vt.Item2));
                    }
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private IReadOnlyList<ProfilerOptimumCounterStat> CollectOptimumCounters()
    {
        var list = new List<ProfilerOptimumCounterStat>();
        try
        {
            object? dictObj = optimumDiagnosticsType?.GetField("Counters", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (dictObj is IDictionary dict)
            {
                foreach (DictionaryEntry de in dict)
                {
                    string name = de.Key?.ToString() ?? "?";
                    object? val = de.Value;
                    MethodInfo? snap = val?.GetType().GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Instance);
                    if (snap?.Invoke(val, null) is ValueTuple<long, long> tuple)
                    {
                        long hits = tuple.Item1;
                        long skips = tuple.Item2;
                        long total = hits + skips;
                        list.Add(new ProfilerOptimumCounterStat
                        {
                            Name = name,
                            Hits = hits,
                            Skips = skips,
                            SkipRatePct = total > 0 ? skips * 100.0 / total : 0.0,
                        });
                    }
                }
            }
        }
        catch
        {
        }
        return list.OrderByDescending(c => c.Hits + c.Skips).ToList();
    }

    private IReadOnlyList<KeyValuePair<string, string>> CollectOptimumSummaries()
    {
        var list = new List<KeyValuePair<string, string>>();
        try
        {
            if (optimumDiagnosticsType != null)
            {
                foreach (MethodInfo m in optimumDiagnosticsType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.ReturnType == typeof(string) && m.GetParameters().Length == 0 &&
                        m.Name.StartsWith("Get", StringComparison.Ordinal) &&
                        m.Name.EndsWith("Summary", StringComparison.Ordinal))
                    {
                        try
                        {
                            string? val = m.Invoke(null, null) as string;
                            if (!string.IsNullOrEmpty(val))
                            {
                                list.Add(new KeyValuePair<string, string>(m.Name, val));
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            InvokeSummaryIfPresent(optimumChunkStreamStatsType, "GetSummary", "ChunkStreamStats", list);
            InvokeSummaryIfPresent(optimumCircuitBreakerType, "GetBreakersSummary", "CircuitBreakers", list);
        }
        catch
        {
        }
        return list;
    }

    private static void InvokeSummaryIfPresent(Type? type, string methodName, string label, List<KeyValuePair<string, string>> list)
    {
        try
        {
            MethodInfo? m = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m?.Invoke(null, null) is string s && !string.IsNullOrEmpty(s))
            {
                list.Add(new KeyValuePair<string, string>(label, s));
            }
        }
        catch
        {
        }
    }

    private static object? ReadMember(Type t, object? target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo? fi = t.GetField(name, flags);
        if (fi != null) return fi.GetValue(target);
        PropertyInfo? pi = t.GetProperty(name, flags);
        if (pi != null && pi.GetIndexParameters().Length == 0) return pi.GetValue(target);
        return null;
    }

    private static int ReadInt(Type t, object? target, string name)
    {
        object? v = ReadMember(t, target, name);
        return v is int i ? i : 0;
    }

    private static bool ReadBool(Type t, object? target, string name)
    {
        object? v = ReadMember(t, target, name);
        return v is bool b && b;
    }

    private static bool? InvokeBool(Type t, object target, string methodName)
    {
        MethodInfo? m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (m == null) return null;
        return m.Invoke(target, null) as bool?;
    }

    private static int CountField(Type t, object target, string name)
    {
        object? val = ReadMember(t, target, name);
        if (val is ICollection c) return c.Count;
        PropertyInfo? cp = val?.GetType().GetProperty("Count");
        return cp != null ? (int)(cp.GetValue(val) ?? 0) : 0;
    }
}
