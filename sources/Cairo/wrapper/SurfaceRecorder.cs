using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using SkiaSharp;

namespace Cairo
{
	internal enum SurfaceRecordingState
	{
		Native,
		Recording,
		Materializing,
		NativeMaterialized,
		Failed,
		Sealed,
		GpuRendered,
		Disposed
	}

	/// <summary>
	/// One immutable display-list entry. Native replay must reproduce the exact Cairo
	/// output of the original call; Skia replay must stay within the GUI parity tolerance.
	/// </summary>
	internal abstract class RecordedCommand
	{
		internal abstract void ReplayNative (RecordedNativeReplay replay);
		internal abstract void DrawSkia (RecordedSkiaReplay replay);
	}

	internal sealed class RecordedNativeReplay
	{
		internal readonly IntPtr Context;
		internal readonly IntPtr Surface;
		internal readonly int Width, Height;
		readonly Func<Context> wrapper;
		Context managed;
		internal RecordedNativeReplay (IntPtr context, IntPtr surface, int width, int height, Func<Context> wrapper)
		{ Context = context; Surface = surface; Width = width; Height = height; this.wrapper = wrapper; }
		/// <summary>A non-recording managed wrapper over the replay context, for legacy native callbacks.</summary>
		internal Context Managed => managed ??= wrapper ();
	}

	/// <summary>Skia replay target. Pixel steps read back and rewrite the target surface.</summary>
	internal sealed class RecordedSkiaReplay
	{
		internal readonly SKSurface Target;
		internal readonly int Width, Height;
		internal readonly int Depth;
		// Rendered images of nested recording sources, shared by one root replay and
		// disposed when it ends. GPU images never outlive the candidate's GRContext.
		internal readonly Dictionary<SurfaceRecorderSnapshot, SKImage> Sources;
		/// <summary>Optional raw writer for the root target: receives Cairo-layout BGRA rows
		/// (top row first, stride = width * 4) and stores them unchanged, bypassing Skia's
		/// premultiplied image path. Null for raster and nested targets.</summary>
		internal readonly RecordedRawWriter RawWriter;
		internal RecordedSkiaReplay (SKSurface target, int width, int height, int depth, Dictionary<SurfaceRecorderSnapshot, SKImage> sources, RecordedRawWriter rawWriter = null)
		{ Target = target; Width = width; Height = height; Depth = depth; Sources = sources; RawWriter = rawWriter; }
		internal SKCanvas Canvas => Target.Canvas;

		internal SKSurface CreateOffscreen (int width, int height)
		{
			var info = new SKImageInfo (width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
			GRRecordingContext gpu = Target.Context;
			SKSurface result = gpu != null ? SKSurface.Create (gpu, false, info) : SKSurface.Create (info);
			if (result == null) throw new InvalidOperationException ("Skia could not create an offscreen GUI surface.");
			return result;
		}

		/// <summary>Reads the target as Cairo-layout premultiplied BGRA (stride = width * 4), runs a
		/// managed pixel operation and writes the exact bytes back.</summary>
		internal unsafe void ApplyPixels (RecordedPixelOperation operation, bool readBack = true)
		{
			if (Width <= 0 || Height <= 0) return;
			var info = new SKImageInfo (Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
			byte[] pixels = readBack
				? ReadBackGuarded (Width, Height, (data, rowBytes) => Target.ReadPixels (info, data, rowBytes, 0, 0))
				: new byte[checked(Width * Height * 4)];
			fixed (byte* data = pixels) {
				operation ((IntPtr)data, Width, Height, Width * 4);
				if (RawWriter != null) {
					// Pixel steps can leave colour above alpha (DemulAlpha). Store the bytes as
					// they are instead of uploading them as a premultiplied Skia image.
					RawWriter ((IntPtr)data, Width, Height);
					return;
				}
				using (SKImage image = SKImage.FromPixelCopy (info, (IntPtr)data, Width * 4))
				using (var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false }) {
					if (image == null) throw new InvalidOperationException ("Skia could not create the pixel operation image.");
					SKCanvas canvas = Target.Canvas;
					int save = canvas.Save ();
					try {
						canvas.ResetMatrix ();
						canvas.DrawImage (image, 0, 0, new SKSamplingOptions (SKFilterMode.Nearest), paint);
					} finally { canvas.RestoreToCount (save); }
				}
			}
		}

		/// <summary>
		/// Reads a tightly packed Width x Height BGRA image into a buffer followed by a canary
		/// region. A readback that writes past width * 4 * height (for example under a stale
		/// GL pack row length or skip) is detected and fails the replay instead of silently
		/// producing shifted pixels. The returned array is at least width * height * 4 bytes.
		/// </summary>
		internal static unsafe byte[] ReadBackGuarded (int width, int height, Func<IntPtr, int, bool> read)
		{
			int rowBytes = checked(width * 4);
			int size = checked(rowBytes * height);
			int guard = Math.Min (checked(rowBytes * 8 + 4096), 1 << 20);
			var buffer = new byte[checked(size + guard)];
			for (int i = size; i < buffer.Length; i++) buffer[i] = Canary (i);
			fixed (byte* data = buffer)
				if (!read ((IntPtr)data, rowBytes)) throw new InvalidOperationException ("Skia could not read back the GUI surface for a pixel operation.");
			for (int i = size; i < buffer.Length; i++)
				if (buffer[i] != Canary (i))
					throw new InvalidOperationException ("GUI surface readback wrote past its " + width + "x" + height + " destination; the GL pixel-store state is not tightly packed.");
			return buffer;
		}

		static byte Canary (int index) => (byte)(index * 31 + 0x5A);
	}

	/// <summary>Stores tightly packed Cairo-layout BGRA rows into the render target unchanged.</summary>
	internal delegate void RecordedRawWriter (IntPtr pixels, int width, int height);

	/// <summary>A pixel operation over Cairo's native-endian premultiplied ARGB32 layout.</summary>
	internal delegate void RecordedPixelOperation (IntPtr pixels, int width, int height, int stride);

	/// <summary>Managed CPU pixel transform from the game's Cairo utilities (blur, demultiply,
	/// image overlay, SVG overlay). It reproduces the original byte operation exactly on both paths.</summary>
	internal sealed class RecordedPixelStep : RecordedCommand
	{
		readonly RecordedPixelOperation operation;
		readonly bool readBack;
		internal readonly string Name;
		internal RecordedPixelStep (string name, RecordedPixelOperation operation, bool readBack = true)
		{ Name = name; this.operation = operation ?? throw new ArgumentNullException (nameof (operation)); this.readBack = readBack; }

		internal override void ReplayNative (RecordedNativeReplay replay)
		{
			NativeMethods.cairo_surface_flush (replay.Surface);
			IntPtr data = NativeMethods.cairo_image_surface_get_data (replay.Surface);
			int stride = NativeMethods.cairo_image_surface_get_stride (replay.Surface);
			if (data == IntPtr.Zero) throw new InvalidOperationException ("Cairo replay surface has no pixel data.");
			operation (data, replay.Width, replay.Height, stride);
			NativeMethods.cairo_surface_mark_dirty (replay.Surface);
		}

		internal override void DrawSkia (RecordedSkiaReplay replay) => replay.ApplyPixels (operation, readBack);
	}

	/// <summary>An operation whose Skia form differs from its legacy native replay (for example
	/// SVG drawing, whose native replay is the original NanoSVG route).</summary>
	internal sealed class RecordedLegacyStep : RecordedCommand
	{
		readonly Action<Context> nativeReplay;
		readonly RecordedPixelOperation skiaOperation;
		readonly bool readBack;
		internal RecordedLegacyStep (Action<Context> nativeReplay, RecordedPixelOperation skiaOperation, bool readBack = true)
		{
			this.nativeReplay = nativeReplay ?? throw new ArgumentNullException (nameof (nativeReplay));
			this.skiaOperation = skiaOperation ?? throw new ArgumentNullException (nameof (skiaOperation));
			this.readBack = readBack;
		}
		internal override void ReplayNative (RecordedNativeReplay replay) => nativeReplay (replay.Managed);
		internal override void DrawSkia (RecordedSkiaReplay replay) => replay.ApplyPixels (skiaOperation, readBack);
	}

	/// <summary>Display list for a newly-created ARGB32 image surface.</summary>
	internal sealed class SurfaceRecorder
	{
		// Observers are diagnostic-only and must never run while the recorder lock is held.
		internal static event Action<string, bool> MaterializationCompleted;

		// Recording surfaces keyed by their native handle. Native consumers of a pattern
		// (and the pattern capture in RecordedDrawing) resolve a native surface back to its
		// display list through this registry. Orphaned recorders hold their own native
		// reference so that the key cannot be reused while it is registered.
		static readonly Dictionary<IntPtr, SurfaceRecorder> registry = new Dictionary<IntPtr, SurfaceRecorder> ();

		readonly Surface surface;
		readonly IntPtr native;
		readonly List<RecordedCommand> commands = new List<RecordedCommand> ();
		readonly object sync = new object ();
		internal readonly int Width, Height;
		SurfaceRecordingState state = SurfaceRecordingState.Recording;
		ExceptionDispatchInfo failure;
		string firstMaterializationTrigger;
		bool orphaned;
		// Antialias for the internal FillRectangle helper (tests and direct helpers).
		Antialias helperAntialias = Antialias.Default;

		internal SurfaceRecorder (Surface surface, IntPtr native, int width, int height)
		{
			this.surface = surface; this.native = native; Width = width; Height = height;
			lock (registry) { Prune (); registry[native] = this; }
		}

		internal SurfaceRecordingState State { get { lock (sync) return state; } }
		internal string FirstMaterializationTrigger { get { lock (sync) return firstMaterializationTrigger; } }
		internal IntPtr NativeSurface => native;
		internal bool IsOrphaned { get { lock (sync) return orphaned; } }

		internal static SurfaceRecorder Lookup (IntPtr nativeSurface)
		{
			if (nativeSurface == IntPtr.Zero) return null;
			lock (registry) {
				Prune ();
				return registry.TryGetValue (nativeSurface, out SurfaceRecorder recorder) ? recorder : null;
			}
		}

		static void Prune ()
		{
			List<IntPtr> released = null;
			foreach (var entry in registry) {
				SurfaceRecorder recorder = entry.Value;
				if (recorder.IsOrphaned && NativeMethods.cairo_surface_get_reference_count (entry.Key) <= 1)
					(released ??= new List<IntPtr> ()).Add (entry.Key);
			}
			if (released == null) return;
			foreach (IntPtr key in released) {
				SurfaceRecorder recorder = registry[key];
				registry.Remove (key);
				recorder.ReleaseOrphan ();
			}
		}

		void Unregister ()
		{
			lock (registry) {
				if (registry.TryGetValue (native, out SurfaceRecorder current) && ReferenceEquals (current, this)) registry.Remove (native);
			}
		}

		internal void SetFirstMaterializationTrigger (string trigger)
		{
			if (string.IsNullOrEmpty (trigger)) return;
			lock (sync) {
				if ((state == SurfaceRecordingState.Recording || state == SurfaceRecordingState.Sealed || state == SurfaceRecordingState.GpuRendered) && firstMaterializationTrigger == null)
					firstMaterializationTrigger = trigger;
			}
		}

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state != SurfaceRecordingState.Recording) { snapshot = null; return false; }
				state = SurfaceRecordingState.Sealed;
				snapshot = new SurfaceRecorderSnapshot (commands.ToArray (), Width, Height);
				return true;
			}
		}

		internal bool CommitGpuRender ()
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Sealed) return false;
				state = SurfaceRecordingState.GpuRendered;
				return true;
			}
		}

		/// <summary>Immutable copy of the current display list, used when another recording
		/// draws this surface as a pattern source.</summary>
		internal SurfaceRecorderSnapshot CaptureSnapshot ()
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return null;
				// The list is append-only, so an unchanged count means an identical snapshot;
				// sharing it lets one replay render the source once.
				if (lastSnapshot == null || lastSnapshot.Count != commands.Count)
					lastSnapshot = new SurfaceRecorderSnapshot (commands.ToArray (), Width, Height);
				return lastSnapshot;
			}
		}
		SurfaceRecorderSnapshot lastSnapshot;

		internal bool Add (RecordedCommand command)
		{
			if (command == null) throw new ArgumentNullException (nameof (command));
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state == SurfaceRecordingState.Disposed) throw new ObjectDisposedException (nameof (SurfaceRecorder));
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording) return false;
				commands.Add (command);
				return true;
			}
		}

		internal bool RecordAntialias (Antialias value)
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording) return false;
				helperAntialias = value;
				return true;
			}
		}

		internal bool RecordPicture (SKPicture picture, int x, int y, Action<Context> nativeReplay)
		{
			if (picture == null) throw new ArgumentNullException (nameof (picture));
			if (nativeReplay == null) throw new ArgumentNullException (nameof (nativeReplay));
			return Add (new RecordedPicture (picture, x, y, nativeReplay));
		}

		/// <summary>Internal helper: fills an axis-aligned rectangle with a solid colour
		/// using Over and the helper antialias, exactly like the equivalent Cairo calls.</summary>
		internal void FillRectangle (double x, double y, double width, double height, double red, double green, double blue, double alpha)
		{
			RecordedDrawing drawing = RecordedDrawing.SolidRectangle (x, y, width, height, red, green, blue, alpha, helperAntialias);
			if (drawing == null || !Add (drawing))
				throw new InvalidOperationException ("Recorder is no longer recording.");
		}

		/// <summary>Auto-recorded surfaces: an empty list has nothing to replay, so switching it
		/// to native pixels is not a Cairo rasterization and is not reported.</summary>
		internal bool SilentWhenEmpty;
		/// <summary>The list was empty when native pixels were first requested.</summary>
		internal volatile bool ReleasedEmpty;

		internal void Materialize ()
		{
			string trigger = null;
			ExceptionDispatchInfo materializationFailure = null;
			bool attempted = false;
			bool silentRelease = false;
			lock (sync) {
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return;
				if (SilentWhenEmpty && commands.Count == 0) {
					state = SurfaceRecordingState.NativeMaterialized;
					ReleasedEmpty = true;
					silentRelease = true;
				}
			}
			if (silentRelease) {
				Unregister ();
				if (orphaned) ReleaseOrphan ();
				return;
			}
			lock (sync) {
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return;
				attempted = true;
				trigger = firstMaterializationTrigger ?? "Unknown";
				// Native replay may touch the same surface (for example, an SVG
				// DrawSvg call obtains DataPtr). Suppress nested materialization until
				// the command list has replayed once.
				state = SurfaceRecordingState.Materializing;
				try {
					ThrowIfError (NativeMethods.cairo_surface_status (native), "surface before replay");
					ReplayInto (commands, native, Width, Height);
					commands.Clear ();
					state = SurfaceRecordingState.NativeMaterialized;
				} catch (Exception exception) {
					commands.Clear ();
					failure = ExceptionDispatchInfo.Capture (exception);
					state = SurfaceRecordingState.Failed;
					materializationFailure = failure;
				}
			}
			if (attempted) {
				Unregister ();
				if (orphaned) ReleaseOrphan ();
			}

			// This is one event per first replay attempt, including failed replay. Later
			// reads see NativeMaterialized or Failed and do not report a second event.
			if (attempted) {
				try { MaterializationCompleted?.Invoke (trigger, materializationFailure == null); }
				catch { /* Diagnostics must not change Cairo behavior. */ }
			}
			materializationFailure?.Throw ();
		}

		/// <summary>Replays a command list into a native surface with a fresh Cairo context.
		/// The live recording context keeps its own native state untouched.</summary>
		internal static void ReplayInto (IList<RecordedCommand> list, IntPtr nativeSurface, int width, int height)
		{
			IntPtr cr = NativeMethods.cairo_create (nativeSurface);
			Context wrapper = null;
			try {
				ThrowIfError (NativeMethods.cairo_status (cr), "context creation");
				var replay = new RecordedNativeReplay (cr, nativeSurface, width, height, () => wrapper = new Context (cr, false));
				for (int i = 0; i < list.Count; i++) {
					list[i].ReplayNative (replay);
					ThrowIfError (NativeMethods.cairo_status (cr), "command replay");
					ThrowIfError (NativeMethods.cairo_surface_status (nativeSurface), "surface replay");
				}
			} finally {
				wrapper?.Dispose ();
				NativeMethods.cairo_destroy (cr);
			}
		}

		/// <summary>The managed surface wrapper is gone but native references remain. Keep
		/// the display list (and a native reference) until the last external reference is
		/// released or a native consumer requires the pixels.</summary>
		internal void Orphan ()
		{
			lock (sync) {
				if (orphaned || (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered)) return;
				NativeMethods.cairo_surface_reference (native);
				orphaned = true;
			}
		}

		void ReleaseOrphan ()
		{
			lock (sync) {
				if (!orphaned) return;
				orphaned = false;
				if (state == SurfaceRecordingState.Recording || state == SurfaceRecordingState.Sealed || state == SurfaceRecordingState.GpuRendered) {
					commands.Clear ();
					state = SurfaceRecordingState.Disposed;
				}
			}
			NativeMethods.cairo_surface_destroy (native);
		}

		internal void DisposeFromSurface ()
		{
			lock (sync) {
				if (orphaned) return;
				commands.Clear ();
				state = SurfaceRecordingState.Disposed;
			}
			Unregister ();
		}

		internal static void ThrowIfError (Status status, string phase)
		{
			if (status != Status.Success) throw new InvalidOperationException ("Cairo " + phase + " failed with status " + status + ".");
		}
	}

	/// <summary>Legacy SVG picture command; Skia draws the picture, native replay runs NanoSVG.</summary>
	internal sealed class RecordedPicture : RecordedCommand
	{
		readonly SKPicture picture;
		readonly int x, y;
		readonly Action<Context> nativeReplay;
		internal RecordedPicture (SKPicture picture, int x, int y, Action<Context> nativeReplay)
		{ this.picture = picture; this.x = x; this.y = y; this.nativeReplay = nativeReplay; }
		internal override void ReplayNative (RecordedNativeReplay replay) => nativeReplay (replay.Managed);
		internal override void DrawSkia (RecordedSkiaReplay replay)
		{
			SKCanvas canvas = replay.Canvas;
			int save = canvas.Save ();
			try {
				canvas.ResetMatrix ();
				canvas.Translate (x, y);
				canvas.DrawPicture (picture);
			} finally { canvas.RestoreToCount (save); }
		}
	}

	internal sealed class SurfaceRecorderSnapshot
	{
		readonly RecordedCommand[] commands;
		internal readonly int Width, Height;
		/// <summary>Nesting depth of recording surfaces used as sources (0 = none).</summary>
		internal readonly int Depth;
		internal SurfaceRecorderSnapshot (RecordedCommand[] commands, int width, int height)
		{
			this.commands = commands; Width = width; Height = height;
			int depth = 0;
			foreach (RecordedCommand command in commands)
				if (command is RecordedDrawing drawing && drawing.Source is RecordedSurfaceSource source && source.Recording != null)
					depth = Math.Max (depth, source.Recording.Depth + 1);
			Depth = depth;
		}
		internal int Count => commands.Length;

		internal void DrawTo (SKSurface target, RecordedRawWriter rawWriter = null)
		{
			var sources = new Dictionary<SurfaceRecorderSnapshot, SKImage> ();
			try { DrawTo (target, 0, sources, rawWriter); }
			finally { foreach (SKImage image in sources.Values) image.Dispose (); }
		}

		void DrawTo (SKSurface target, int depth, Dictionary<SurfaceRecorderSnapshot, SKImage> sources, RecordedRawWriter rawWriter = null)
		{
			if (target == null) throw new ArgumentNullException (nameof (target));
			if (depth > RecordedDrawing.MaxSourceDepth) throw new NotSupportedException ("Recorded surface sources are nested too deeply.");
			SKCanvas canvas = target.Canvas;
			int save = canvas.Save ();
			try {
				canvas.ResetMatrix ();
				canvas.Clear (SKColors.Transparent);
				var replay = new RecordedSkiaReplay (target, Width, Height, depth, sources, rawWriter);
				for (int i = 0; i < commands.Length; i++) commands[i].DrawSkia (replay);
			} finally { canvas.RestoreToCount (save); }
		}

		/// <summary>Renders this snapshot as an image for use as a pattern source.</summary>
		internal SKImage Render (RecordedSkiaReplay parent)
		{
			if (parent.Sources.TryGetValue (this, out SKImage cached)) return cached;
			using (SKSurface offscreen = parent.CreateOffscreen (Math.Max (1, Width), Math.Max (1, Height))) {
				DrawTo (offscreen, parent.Depth + 1, parent.Sources);
				SKImage image = offscreen.Snapshot ();
				parent.Sources[this] = image;
				return image;
			}
		}

		/// <summary>Draws onto an arbitrary canvas through a raster intermediate (tests and callers
		/// that do not own an SKSurface). Pixel operations need a readable surface.</summary>
		internal void DrawTo (SKCanvas canvas)
		{
			if (canvas == null) throw new ArgumentNullException (nameof (canvas));
			using (SKSurface raster = SKSurface.Create (new SKImageInfo (Math.Max (1, Width), Math.Max (1, Height), SKColorType.Rgba8888, SKAlphaType.Premul))) {
				if (raster == null) throw new InvalidOperationException ("Skia could not create a raster GUI surface.");
				DrawTo (raster);
				using (SKImage image = raster.Snapshot ())
				using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) {
					int save = canvas.Save ();
					try {
						canvas.ResetMatrix ();
						canvas.DrawImage (image, 0, 0, new SKSamplingOptions (SKFilterMode.Nearest), paint);
					} finally { canvas.RestoreToCount (save); }
				}
			}
		}

		internal void ReplayNative (IntPtr nativeSurface) => SurfaceRecorder.ReplayInto (commands, nativeSurface, Width, Height);
	}
}
