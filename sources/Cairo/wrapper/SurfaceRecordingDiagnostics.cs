using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Cairo
{
	internal static class SurfaceRecordingDiagnostics
	{
		internal static bool Enabled;
		static long cpuRasterizations, materializations, dependencyRenders, dependencyCacheHits;
		static readonly ConcurrentDictionary<string, long> materializationCauses = new ConcurrentDictionary<string, long>();
		static readonly ConcurrentDictionary<string, long> fallbackReasons = new ConcurrentDictionary<string, long>();
		static readonly ConcurrentDictionary<string, long> rasterizationCauses = new ConcurrentDictionary<string, long>();
		internal static IReadOnlyDictionary<string, long> RasterizationCauses => new Dictionary<string, long>(rasterizationCauses);
		internal static IReadOnlyDictionary<string, long> MaterializationCauses => new Dictionary<string, long>(materializationCauses);
		internal static IReadOnlyDictionary<string, long> FallbackReasons => new Dictionary<string, long>(fallbackReasons);
		internal static void Fallback(string reason) { if (Enabled) fallbackReasons.AddOrUpdate(reason ?? "unknown", 1, (_, count) => count + 1); }
		internal static long CpuRasterizations => Interlocked.Read(ref cpuRasterizations);
		internal static long Materializations => Interlocked.Read(ref materializations);
		internal static long DependencyRenders => Interlocked.Read(ref dependencyRenders);
		internal static long DependencyCacheHits => Interlocked.Read(ref dependencyCacheHits);
		internal static void Rasterize() { if (Enabled) { Interlocked.Increment(ref cpuRasterizations); string cause = string.Join(" <- ", new StackTrace(1, false).GetFrames().Take(8).Select(frame => frame.GetMethod().DeclaringType?.FullName + "." + frame.GetMethod().Name)); rasterizationCauses.AddOrUpdate(cause, 1, (_, count) => count + 1); } }
		internal static void Materialize() { if (Enabled) { Interlocked.Increment(ref materializations); string cause = string.Join(" <- ", new StackTrace(2, false).GetFrames().Take(8).Select(frame => frame.GetMethod().DeclaringType?.FullName + "." + frame.GetMethod().Name)); materializationCauses.AddOrUpdate(cause, 1, (_, count) => count + 1); } }
		internal static void RenderDependency() { if (Enabled) Interlocked.Increment(ref dependencyRenders); }
		internal static void ReuseDependency() { if (Enabled) Interlocked.Increment(ref dependencyCacheHits); }
		internal static void Reset() { Interlocked.Exchange(ref cpuRasterizations, 0); Interlocked.Exchange(ref materializations, 0); Interlocked.Exchange(ref dependencyRenders, 0); Interlocked.Exchange(ref dependencyCacheHits, 0); materializationCauses.Clear(); fallbackReasons.Clear(); rasterizationCauses.Clear(); }
	}
}
