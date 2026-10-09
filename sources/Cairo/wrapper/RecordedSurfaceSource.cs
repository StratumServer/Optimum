using System;
using System.Threading;

namespace Cairo
{
	// A binding retains the managed recording independently of the native Cairo reference.
	// Draws take immutable prefixes, so later edits or disposal cannot change earlier draws.
	internal sealed class RecordedSurfaceSource : IDisposable
	{
		Surface source;
		internal RecordedSurfaceSource(Surface source) { this.source = source; source.RetainRecordedSource(); }
		internal RecordedSurfaceSource Retain() => new RecordedSurfaceSource(source ?? throw new ObjectDisposedException(nameof(RecordedSurfaceSource)));
		internal SurfaceRecorderSnapshot Capture() => source?.CaptureRecordedSource();
		internal void Materialize(IntPtr target) { if (source == null) return; if (source.RecordingState != SurfaceRecordingState.Disposed) { source.MaterializeRecorder(); return; } using (var snapshot = Capture()) snapshot?.ReplayNative(target); }
		public void Dispose() { Interlocked.Exchange(ref source, null)?.ReleaseRecordedSource(); }
	}

	internal sealed class RecordedResource : IDisposable
	{
		IDisposable value;
		int references = 1;
		internal RecordedResource(IDisposable value) { this.value = value; }
		internal RecordedResource Retain() { Interlocked.Increment(ref references); return this; }
		public void Dispose() { if (Interlocked.Decrement(ref references) == 0) Interlocked.Exchange(ref value, null)?.Dispose(); }
	}
}
