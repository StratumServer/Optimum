using System;
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
		// Independent of rendering diagnostics: timing is opt-in and retains no UI content.
		static int profilingEnabled;
		static long shadowAccesses, shadowTicks, shadowCreations, shadowCommands;
		static long drawingCaptures, drawingCaptureTicks, pathCopies, pathCopyTicks;
		static long pathConversions, pathConversionTicks, strokeOutlines, strokeOutlineTicks;
		internal static bool ProfilingEnabled { get => Volatile.Read(ref profilingEnabled) != 0; set => Volatile.Write(ref profilingEnabled, value ? 1 : 0); }
		internal static long ShadowAccesses => Interlocked.Read(ref shadowAccesses);
		internal static long ShadowTicks => Interlocked.Read(ref shadowTicks);
		internal static long ShadowCreations => Interlocked.Read(ref shadowCreations);
		internal static long ShadowCommands => Interlocked.Read(ref shadowCommands);
		internal static long DrawingCaptures => Interlocked.Read(ref drawingCaptures);
		internal static long DrawingCaptureTicks => Interlocked.Read(ref drawingCaptureTicks);
		internal static long PathCopies => Interlocked.Read(ref pathCopies);
		internal static long PathCopyTicks => Interlocked.Read(ref pathCopyTicks);
		internal static long PathConversions => Interlocked.Read(ref pathConversions);
		internal static long PathConversionTicks => Interlocked.Read(ref pathConversionTicks);
		internal static long StrokeOutlines => Interlocked.Read(ref strokeOutlines);
		internal static long StrokeOutlineTicks => Interlocked.Read(ref strokeOutlineTicks);
		internal enum ProfileStage { Shadow, DrawingCapture, PathCopy, PathConversion, StrokeOutline, CaptureState, SourceCapture, TextPreparation, TextMeasurement, FontSelection, GpuBlur, GpuReplay, GpuSubmission, SourceShader, SourceFreeze }
		const int StageCount = (int)ProfileStage.SourceFreeze + 1;
		static readonly long[] stageCounts = new long[StageCount], stageTicks = new long[StageCount];
		internal static IReadOnlyDictionary<string, long> StageDiagnostics {
			get {
				var result = new Dictionary<string, long>();
				foreach (ProfileStage stage in Enum.GetValues(typeof(ProfileStage))) {
					int index = (int)stage;
					result[stage + "Count"] = Interlocked.Read(ref stageCounts[index]);
					result[stage + "Ticks"] = Interlocked.Read(ref stageTicks[index]);
				}
				return result;
			}
		}
		internal static ProfileScope Profile(ProfileStage stage) => new ProfileScope(stage, ProfilingEnabled);
		// Value-type scope: disabled calls do not read the clock or update counters.
		internal struct ProfileScope : System.IDisposable
		{
			readonly ProfileStage stage;
			readonly long start;
			internal int ReplayedCommands;
			internal bool CreatedShadow;
			internal ProfileScope(ProfileStage stage, bool enabled) { this.stage = stage; start = enabled ? Stopwatch.GetTimestamp() : -1; ReplayedCommands = 0; CreatedShadow = false; }
			public void Dispose()
			{
				if (start < 0) return;
				long ticks = System.Math.Max(0, Stopwatch.GetTimestamp() - start);
				Interlocked.Increment(ref stageCounts[(int)stage]); Interlocked.Add(ref stageTicks[(int)stage], ticks);
				switch (stage) {
				case ProfileStage.Shadow:
					Interlocked.Increment(ref shadowAccesses); Interlocked.Add(ref shadowTicks, ticks);
					Interlocked.Add(ref shadowCommands, ReplayedCommands); if (CreatedShadow) Interlocked.Increment(ref shadowCreations); break;
				case ProfileStage.DrawingCapture: Interlocked.Increment(ref drawingCaptures); Interlocked.Add(ref drawingCaptureTicks, ticks); break;
				case ProfileStage.PathCopy: Interlocked.Increment(ref pathCopies); Interlocked.Add(ref pathCopyTicks, ticks); break;
				case ProfileStage.PathConversion: Interlocked.Increment(ref pathConversions); Interlocked.Add(ref pathConversionTicks, ticks); break;
				case ProfileStage.StrokeOutline: Interlocked.Increment(ref strokeOutlines); Interlocked.Add(ref strokeOutlineTicks, ticks); break;
				}
			}
		}
		// Reset only between measurements, while no profiled operation is in flight.
		internal static void ResetProfiling()
		{
			for (int i = 0; i < stageCounts.Length; i++) { Interlocked.Exchange(ref stageCounts[i], 0); Interlocked.Exchange(ref stageTicks[i], 0); }
			Interlocked.Exchange(ref shadowAccesses, 0); Interlocked.Exchange(ref shadowTicks, 0);
			Interlocked.Exchange(ref shadowCreations, 0); Interlocked.Exchange(ref shadowCommands, 0);
			Interlocked.Exchange(ref drawingCaptures, 0); Interlocked.Exchange(ref drawingCaptureTicks, 0);
			Interlocked.Exchange(ref pathCopies, 0); Interlocked.Exchange(ref pathCopyTicks, 0);
			Interlocked.Exchange(ref pathConversions, 0); Interlocked.Exchange(ref pathConversionTicks, 0);
			Interlocked.Exchange(ref strokeOutlines, 0); Interlocked.Exchange(ref strokeOutlineTicks, 0);
		}

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
