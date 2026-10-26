using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using SkiaSharp;

namespace Cairo
{
	internal enum SurfaceRecordingState
	{
		Native,
		Recording,
		NativeMaterialized,
		Materializing,
		Failed,
		Sealed,
		GpuRendered,
		Disposed
	}

	/// <summary>Immutable command stream for a newly-created ARGB32 image surface.</summary>
	internal sealed class SurfaceRecorder
	{
		// Observers are diagnostic-only and never run while the recorder lock is held.
		internal static event Action<string, bool> MaterializationCompleted;

		Surface surface;
		readonly List<Command> commands = new List<Command> ();
		readonly object sync = new object ();
		SurfaceRecordingState state = SurfaceRecordingState.Recording;
		ExceptionDispatchInfo failure;
		string firstMaterializationTrigger;
		sealed class ContextState
        {
            internal readonly object Identity;
            internal ManagedGraphicsState Graphics;
            internal readonly List<RecordedGuiClip> Clips = new List<RecordedGuiClip> ();
            internal readonly Stack<RecordedGuiClip[]> SavedClips = new Stack<RecordedGuiClip[]> ();
            internal readonly Stack<Antialias> SavedAntialias = new Stack<Antialias> ();
            internal int SaveDepth;
            internal bool Unqualified;
            internal bool AntialiasConfigured = true;
            internal ContextState (Status status, object identity = null) { Identity = identity ?? new object (); Graphics = new ManagedGraphicsState (status); }
        }
        readonly ConditionalWeakTable<Context, ContextState> contextStates = new ConditionalWeakTable<Context, ContextState> ();
        ContextState currentState;
        bool defaultStateClaimed;
        ManagedGraphicsState graphicsState { get => currentState.Graphics; set => currentState.Graphics = value; }
        internal void ActivateContext (Context context, bool fresh = false)
        {
            lock (sync) {
                if (!contextStates.TryGetValue (context, out ContextState selected)) {
                    selected = !defaultStateClaimed && (!fresh || commands.Count == 0) ? currentState : new ContextState (surface.ManagedContextInitialStatus);
                    if (surface.ManagedFinished) selected.Graphics.Latch (surface.ManagedContextInitialStatus);
                    contextStates.Add (context, selected);
                    defaultStateClaimed = true;
                }
                currentState = selected;
            }
        }
        internal void MoveSurfaceOwner (Surface owner) { lock (sync) surface = owner; }

        internal Context[] GetContexts ()
        {
            lock (sync) {
                var result = new List<Context> ();
                foreach (var entry in contextStates) result.Add (entry.Key);
                return result.ToArray ();
            }
        }

				internal ManagedGraphicsState GraphicsState => graphicsState;
		readonly List<RecordedResource> resources = new List<RecordedResource>();
		readonly List<RecordedSurfaceSource> sourceBindings = new List<RecordedSurfaceSource>();
		Matrix sourceMatrix => graphicsState.SourceMatrix;
		RecordedSurfaceSource currentSource => graphicsState.SourceBinding;
		int width, height;
		internal readonly long Identity = System.Threading.Interlocked.Increment(ref nextIdentity);
		static long nextIdentity;
		List<RecordedGuiClip> clips => currentState.Clips;
		Stack<RecordedGuiClip[]> savedClips => currentState.SavedClips;
		Stack<Antialias> savedAntialias => currentState.SavedAntialias;
		int saveDepth { get => currentState.SaveDepth; set => currentState.SaveDepth = value; }
		bool unqualifiedHelperDrawing { get => currentState.Unqualified; set => currentState.Unqualified = value; }
		// Cairo's default antialias mode maps to Skia's antialias-on paint, so helper operations using that default are qualified too.
		bool antialiasConfigured { get => currentState.AntialiasConfigured; set => currentState.AntialiasConfigured = value; }

		internal void ResetContextState ()
		{
			if (State != SurfaceRecordingState.Recording)
				throw new InvalidOperationException ("Only a live managed recording target can reset its Context state.");
			ReplaceGraphicsState (new ManagedGraphicsState (surface.ManagedStatus));
			clips.Clear ();
			savedClips.Clear ();
			savedAntialias.Clear ();
			saveDepth = 0;
			unqualifiedHelperDrawing = false;
			antialiasConfigured = true;
		}

		internal void InitializeGroupFrom (SurfaceRecorder parent)
		{
			ReplaceGraphicsState (parent.graphicsState.CloneForGroup (contextState: true));
			parent.graphicsState.TransferCurrentFontOwnershipTo (graphicsState);
			foreach (RecordedGuiClip clip in parent.clips) {
				RecordedGuiClip clone = clip.Clone ();
				clips.Add (clone); resources.Add (new RecordedResource (clone));
			}
			RecordedGuiClip[][] clipStack = parent.savedClips.ToArray ();
			for (int i = clipStack.Length - 1; i >= 0; i--) {
				var frame = new RecordedGuiClip[clipStack[i].Length];
				for (int j = 0; j < frame.Length; j++) {
					frame[j] = clipStack[i][j].Clone ();
					resources.Add (new RecordedResource (frame[j]));
				}
				savedClips.Push (frame);
			}
			Antialias[] antialiasStack = parent.savedAntialias.ToArray ();
			for (int i = antialiasStack.Length - 1; i >= 0; i--) savedAntialias.Push (antialiasStack[i]);
			saveDepth = parent.saveDepth;
			unqualifiedHelperDrawing = parent.unqualifiedHelperDrawing;
			antialiasConfigured = parent.antialiasConfigured;
		}

		internal void InitializePageFrom (SurfaceRecorder parent, bool copyCommands)
		{
            currentState = new ContextState (surface.ManagedStatus, parent.currentState.Identity);
			ReplaceGraphicsState (parent.graphicsState.CloneForGroup (retainFontOwnership: true));
			foreach (RecordedGuiClip clip in parent.clips) {
				RecordedGuiClip clone = clip.Clone ();
				clips.Add (clone); resources.Add (new RecordedResource (clone));
			}
			RecordedGuiClip[][] clipStack = parent.savedClips.ToArray ();
			for (int i = clipStack.Length - 1; i >= 0; i--) {
				var frame = new RecordedGuiClip[clipStack[i].Length];
				for (int j = 0; j < frame.Length; j++) {
					frame[j] = clipStack[i][j].Clone ();
					resources.Add (new RecordedResource (frame[j]));
				}
				savedClips.Push (frame);
			}
			Antialias[] antialiasStack = parent.savedAntialias.ToArray ();
			for (int i = antialiasStack.Length - 1; i >= 0; i--) savedAntialias.Push (antialiasStack[i]);
			saveDepth = parent.saveDepth;
			unqualifiedHelperDrawing = parent.unqualifiedHelperDrawing;
			antialiasConfigured = parent.antialiasConfigured;
            foreach (var entry in parent.contextStates) {
                ContextState cloned = ReferenceEquals (entry.Value, parent.currentState) ? currentState : CloneContextState (entry.Value);
                contextStates.Add (entry.Key, cloned);
                defaultStateClaimed = true;
            }
            if (!copyCommands) {
                foreach (RecordedResource resource in parent.resources)
                    if (IsStateResource (resource.Value)) resources.Add (resource.Retain ());
                foreach (RecordedSurfaceSource binding in parent.sourceBindings) sourceBindings.Add (binding.Retain ());
            }
			if (copyCommands) {
				commands.AddRange (parent.commands);
				foreach (RecordedResource resource in parent.resources) resources.Add (resource.Retain ());
				foreach (RecordedSurfaceSource binding in parent.sourceBindings) sourceBindings.Add (binding.Retain ());
			}
		}

        static bool IsStateResource (IDisposable value) => value is Pattern || value is FontFace || value is ScaledFont || value is RecordedGuiClip;
        ContextState CloneContextState (ContextState source)
        {
            var result = new ContextState (surface.ManagedStatus, source.Identity);
            result.Graphics.ReleaseFontOwnership ();
            result.Graphics = source.Graphics.CloneForGroup (retainFontOwnership: true);
            result.SaveDepth = source.SaveDepth;
            result.Unqualified = source.Unqualified;
            result.AntialiasConfigured = source.AntialiasConfigured;
            foreach (var clip in source.Clips) {
                var clone = clip.Clone (); result.Clips.Add (clone); resources.Add (new RecordedResource (clone));
            }
            var stack = source.SavedClips.ToArray ();
            for (int i = stack.Length - 1; i >= 0; i--) {
                var frame = new RecordedGuiClip[stack[i].Length];
                for (int j = 0; j < frame.Length; j++) { frame[j] = stack[i][j].Clone (); resources.Add (new RecordedResource (frame[j])); }
                result.SavedClips.Push (frame);
            }
            var antialias = source.SavedAntialias.ToArray ();
            for (int i = antialias.Length - 1; i >= 0; i--) result.SavedAntialias.Push (antialias[i]);
            return result;
        }

		void ReplaceGraphicsState (ManagedGraphicsState replacement)
		{
			graphicsState?.ReleaseFontOwnership ();
			graphicsState = replacement;
		}

		internal void ReleaseContextFontOwnership (Context context)
		{
			lock (sync) {
				if (contextStates.TryGetValue (context, out ContextState contextState)) contextState.Graphics.ReleaseFontOwnership ();
			}
		}

				internal bool RecordNative(Action<Context> replay, IDisposable resource = null, RecordedSurfaceSource sourceBinding = null, Action<ManagedGraphicsState> stateUpdate = null)
		{
			bool added = Add(new Command(replay));
			if (added) stateUpdate?.Invoke(graphicsState);
			if (added && resource != null) resources.Add(new RecordedResource(resource));
			if (added && sourceBinding != null) sourceBindings.Add(sourceBinding);
			return added;
		}

		// A latched Cairo error (for example an unbalanced restore) turns every later drawing call into a no-op.
		bool Faulted => graphicsState.Status != Status.Success;

		internal bool RecordDrawing(Action<Context> replay, bool stroke = false, bool paintAll = false, double alpha = 1, bool clearsPath = false, ImageSurface maskSurface = null, Pattern maskPattern = null, RecordedSurfaceSource maskBinding = null, double maskX = 0, double maskY = 0)
		{
		if (State != SurfaceRecordingState.Recording) return false;
		if (Faulted) return true;
        if (surface.ManagedFinished) {
            bool noOp = graphicsState.Operator == Operator.Dest || alpha <= 0 ||
                (stroke && graphicsState.LineWidth <= 0) || HasEmptyFinishedClip () ||
                (maskSurface != null && (maskSurface.Width == 0 || maskSurface.Height == 0));
            // Cairo skips a fill without segments, but validates degenerate line/rectangle fills.
            if (!paintAll && !stroke && maskSurface == null && maskPattern == null)
                noOp |= !graphicsState.HasFillSegments;
            if (!noOp) surface.RejectFinishedDrawing ();
            if (clearsPath) graphicsState.NewPath ();
            return true;
        }
		if (stroke && graphicsState.LineWidth <= 0) {
		// Cairo draws nothing for a zero-width stroke; the path is still consumed by Stroke.
		bool ignored = Add(new Command(replay));
		if (ignored && clearsPath) graphicsState.NewPath();
		return ignored;
		}
			// These Cairo operators are not bounded by source: scaling source
			// alpha cannot replace their coverage mask. Preserve native replay
			// until the recorder has an equivalent mask compositor.
			if (alpha != 1 && graphicsState.Operator is Operator.Clear or Operator.Source
				or Operator.In or Operator.Out or Operator.DestIn or Operator.DestAtop) return false;
			RecordedGuiDrawing drawing = RecordedGuiDrawing.Capture(graphicsState, width, height, stroke, paintAll, clips.ToArray(), alpha: alpha, sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture(), maskSurface: maskSurface, maskPattern: maskPattern, maskBinding: maskBinding, maskX: maskX, maskY: maskY);
			if (!Add(new Command(c => drawing.Replay(c, replay), drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			if (clearsPath) graphicsState.NewPath();
			return true;
		}

        bool HasEmptyFinishedClip ()
        {
            if (clips.Count == 0) return false;
            SKRect bounds = clips[0].Path.Bounds;
            for (int i = 1; i < clips.Count; i++) {
                SKRect next = clips[i].Path.Bounds;
                bounds = new SKRect (Math.Max (bounds.Left, next.Left), Math.Max (bounds.Top, next.Top),
                    Math.Min (bounds.Right, next.Right), Math.Min (bounds.Bottom, next.Bottom));
            }
            // Cairo can reject empty clip bounds before validating the target. It does
            // not resolve arbitrary path intersections or target extents at this point.
            return bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top;
        }

		internal bool RecordClip(bool preserve)
		{
		if (State != SurfaceRecordingState.Recording) return false;
		if (Faulted) return true;
			var clip = new RecordedGuiClip(graphicsState);
			clips.Add(clip); resources.Add(new RecordedResource(clip));
			return RecordNative(preserve ? c => c.ClipPreserve() : c => c.Clip(), stateUpdate: preserve ? null : s => s.NewPath());
		}
		internal bool RecordResetClip() { clips.Clear(); return RecordNative(c => c.ResetClip()); }

		/// <summary>cairo_in_clip: whether a user-space point is visible through every recorded clip and inside the surface.</summary>
		internal bool InClip(double x, double y)
		{
		graphicsState.UserToDevice(ref x, ref y);
		if (x < 0 || y < 0 || x >= width || y >= height) return false;
		foreach (RecordedGuiClip clip in clips) if (!clip.Path.Contains((float)x, (float)y)) return false;
		return true;
		}

		internal bool RecordText(byte[] text, bool pathOnly)
		{
		if (Faulted) return State == SurfaceRecordingState.Recording;
            // Cairo queries text/glyph support even for an empty string. On a finished
            // target this latches the surface error without faulting the context.
            if (!pathOnly && surface.ManagedFinished) surface.LatchManagedStatus (Status.SurfaceFinished);
			using var textPreparation = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.TextPreparation);
			byte[] copy = (byte[])text.Clone();
			int terminator = Array.IndexOf(copy, (byte)0);
			int length = terminator < 0 ? copy.Length : terminator;
			string decoded;
			try { decoded = new UTF8Encoding(false, true).GetString(copy, 0, length); }
            catch (DecoderFallbackException) {
                // cairo_text_path ignores conversion failure; show_text latches it.
                if (!pathOnly) graphicsState.Latch (Status.InvalidString);
                return true;
            }
			if (pathOnly) {
			bool appended = RecordNative(c => c.TextPath(copy), stateUpdate: s => SkiaTextPath.AppendText(s, decoded));
			return appended;
			}
            if (surface.ManagedFinished) {
                graphicsState.TryGetCurrentPoint (out double x, out double y);
                TextExtents metrics = SkiaTextMetrics.TextExtents (graphicsState, decoded);
                graphicsState.MoveTo (x + metrics.XAdvance, y + metrics.YAdvance);
                return true;
            }
			graphicsState.TryGetCurrentPoint(out double pointX, out double pointY);
			PointD point = new PointD(pointX, pointY);
			using FontOptions options = graphicsState.GetFontOptions();
			SKFont font = SkiaGuiFont.Create(graphicsState, options);
			SKRect? textInkClip = options.Antialias == Antialias.Subpixel
			? GetTextInkClip(font, options, decoded, point)
			: null;
			float advance;
			RecordedGuiDrawing drawing;
			if (graphicsState.FontFileSource != null) {
				// Use the platform outlines for file faces as well as TextPath: Skia's
				// bundled hinter can differ from Cairo's system FreeType interpreter.
				font.Dispose();
				ManagedGraphicsState textState = graphicsState.CloneForGroup();
				textState.NewPath();
				textState.MoveTo(point.X, point.Y);
				SkiaTextPath.AppendText(textState, decoded);
				textState.TryGetCurrentPoint(out double endX, out _);
				advance = (float)(endX - point.X);
				drawing = RecordedGuiDrawing.Capture(graphicsState, width, height, false, false, clips.ToArray(),
					textPath: RecordedGuiDrawing.CopyPath(textState), sourceMatrix: sourceMatrix,
					sourceSnapshot: currentSource?.Capture());
				drawing.Paint.IsAntialias = options.Antialias != Antialias.None;
			} else if (SkiaTextMetrics.UsesNormalHintedAdvances(font, options, decoded)) {
				// FreeType's LIGHT target changes raster coverage but uses linear
				// advances. Keep the existing Normal-hinted Skia advances for string
				// placement while using Fontconfig's target for the glyph masks.
				SKTextBlob blob = null;
				try {
					using var advanceFont = SkiaTextMetrics.CreateAdvanceFont(font);
					using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.TextMeasurement))
						advance = advanceFont.MeasureText(decoded);
					var glyphs = new ushort[decoded.Length];
					font.GetGlyphs(decoded, glyphs);
					int glyphCount = glyphs.Length;
					var widths = new float[glyphCount];
					var bounds = new SKRect[glyphCount];
					advanceFont.GetGlyphWidths(glyphs, widths, bounds);
					var positions = new SKPoint[glyphCount];
					float x = 0;
					for (int i = 0; i < glyphCount; i++) {
						positions[i] = new SKPoint(x, 0);
						x += widths[i];
					}
					font.Subpixel = true;
					using var blobBuilder = new SKTextBlobBuilder();
					blobBuilder.AddPositionedRun(glyphs, font, positions);
					blob = blobBuilder.Build() ?? throw new InvalidOperationException("Skia failed to build a Fontconfig-hinted text run.");
					drawing = RecordedGuiDrawing.Capture(graphicsState, width, height, false, false, clips.ToArray(), sourceMatrix: sourceMatrix,
						sourceSnapshot: currentSource?.Capture(), text: decoded, textBlob: blob, textX: (float)point.X, textY: (float)point.Y,
						textInkClip: textInkClip);
					blob = null;
				} finally { blob?.Dispose(); font.Dispose(); }
			} else {
				using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.TextMeasurement))
					advance = font.MeasureText(decoded);
				drawing = RecordedGuiDrawing.Capture(graphicsState, width, height, false, false, clips.ToArray(), sourceMatrix: sourceMatrix,
						sourceSnapshot: currentSource?.Capture(), text: decoded, textFont: font, textX: (float)point.X, textY: (float)point.Y,
						textInkClip: textInkClip);
			}
			if (graphicsState.Operator == Operator.Screen && drawing.TextFont != null) {
				drawing.TextCoverage = RecordedFreeTypeText.Capture(graphicsState, drawing.TextFont, options, decoded, drawing.Clips, width, height);
			} else if (graphicsState.Operator == Operator.Screen && drawing.TextBlob != null) {
				using var coverageFont = SkiaGuiFont.Create(graphicsState, options);
				drawing.TextCoverage = RecordedFreeTypeText.Capture(graphicsState, coverageFont, options, decoded, drawing.Clips, width, height);
			}
			if (!Add(new Command(c => drawing.Replay(c, target => target.ShowText(copy)), drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			graphicsState.MoveTo(point.X + advance, point.Y);
			return true;
		}

		SKRect? GetTextInkClip(SKFont font, FontOptions options, string text, PointD point)
		{
		Matrix matrix = graphicsState.Matrix;
		if (matrix.Xy != 0 || matrix.Yx != 0 || matrix.Xx <= 0 || matrix.Yy <= 0) return null;
		TextExtents extents = SkiaTextMetrics.TextExtents(font, options, text);
			if (extents.Width <= 0 || extents.Height <= 0) return null;
			double left = matrix.Xx * (point.X + extents.XBearing) + matrix.X0;
			double top = matrix.Yy * (point.Y + extents.YBearing) + matrix.Y0;
			double right = matrix.Xx * (point.X + extents.XBearing + extents.Width) + matrix.X0;
			double bottom = matrix.Yy * (point.Y + extents.YBearing + extents.Height) + matrix.Y0;
			// Cairo clips LCD fringes to the run's ink bounds only when the run is
			// clipped by the surface. Applying this to interior text changes its
			// coverage, so keep the correction conditional on an actual edge clip.
			if (left >= 0 && top >= 0 && right <= width && bottom <= height) return null;
			float clipLeft = (float)Math.Ceiling(left);
			float clipTop = (float)Math.Ceiling(top);
			float clipRight = (float)Math.Floor(right);
			float clipBottom = (float)Math.Floor(bottom);
			return clipRight > clipLeft && clipBottom > clipTop
				? new SKRect(clipLeft, clipTop, clipRight, clipBottom)
				: null;
		}

		static bool ContainsSurrogate(string text)
		{
			foreach (char value in text) if (char.IsSurrogate(value)) return true;
			return false;
		}

		internal bool RecordGlyphs(Glyph[] glyphs, bool pathOnly)
		{
		if (Faulted) return State == SurfaceRecordingState.Recording;
            if (glyphs.Length == 0) return true;
            if (!pathOnly && surface.ManagedFinished) {
                if (graphicsState.Operator != Operator.Dest && !HasEmptyFinishedClip ())
                    surface.LatchManagedStatus (Status.SurfaceFinished);
                return true;
            }
			Glyph[] copy = (Glyph[])glyphs.Clone();
			if (pathOnly) return RecordNative(c => c.GlyphPath(copy), stateUpdate: s => SkiaTextPath.AppendGlyphs(s, copy));
			using FontOptions options = graphicsState.GetFontOptions();
			if (graphicsState.FontFileSource != null) {
				ManagedGraphicsState textState = graphicsState.CloneForGroup();
				textState.NewPath();
				SkiaTextPath.AppendGlyphs(textState, copy);
				var outlinedDrawing = RecordedGuiDrawing.Capture(graphicsState, width, height, false, false, clips.ToArray(),
					textPath: RecordedGuiDrawing.CopyPath(textState), sourceMatrix: sourceMatrix,
					sourceSnapshot: currentSource?.Capture());
				outlinedDrawing.Paint.IsAntialias = options.Antialias != Antialias.None;
				if (!Add(new Command(c => outlinedDrawing.Replay(c, target => target.ShowGlyphs(copy)), outlinedDrawing))) { outlinedDrawing.Dispose(); return false; }
				resources.Add(new RecordedResource(outlinedDrawing));
				return true;
			}
			using SKFont font = SkiaGuiFont.Create(graphicsState, options);
			var glyphIndices = new ushort[copy.Length];
			var positions = new SKPoint[copy.Length];
			for (int i = 0; i < copy.Length; i++) {
				if (copy[i].Index < 0 || copy[i].Index > ushort.MaxValue)
					throw new InvalidOperationException("Skia glyph indices are 16-bit; Cairo materialization is disabled for glyph " + copy[i].Index + ".");
				glyphIndices[i] = (ushort)copy[i].Index;
				positions[i] = new SKPoint((float)copy[i].X, (float)copy[i].Y);
			}
			using var blobBuilder = new SKTextBlobBuilder();
			blobBuilder.AddPositionedRun(glyphIndices, font, positions);
			SKTextBlob blob = blobBuilder.Build() ?? throw new InvalidOperationException("Skia failed to create a positioned glyph run.");
			var drawing = RecordedGuiDrawing.Capture(graphicsState, width, height, false, false, clips.ToArray(), sourceMatrix: sourceMatrix,
				sourceSnapshot: currentSource?.Capture(), textBlob: blob);
			if (!Add(new Command(c => drawing.Replay(c, target => target.ShowGlyphs(copy)), drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			return true;
		}

		void ClearCommands()
		{
			commands.Clear();
			foreach (IDisposable resource in resources) resource.Dispose();
			resources.Clear(); clips.Clear(); savedClips.Clear(); savedAntialias.Clear();
			foreach (var binding in sourceBindings) binding.Dispose();
			sourceBindings.Clear();
		}

		internal void ClearCommandsForPixelCheckpoint ()
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered)
					throw new InvalidOperationException ("The recording surface cannot checkpoint pixels in its current state.");
				commands.Clear ();
				for (int i = resources.Count - 1; i >= 0; i--) {
                    if (IsStateResource (resources[i].Value)) continue;
                    resources[i].Dispose (); resources.RemoveAt (i);
                }
				var activeBindings = new HashSet<RecordedSurfaceSource> (graphicsState.GetRetainedSourceBindings ());
                foreach (var entry in contextStates) activeBindings.UnionWith (entry.Value.Graphics.GetRetainedSourceBindings ());
				for (int i = sourceBindings.Count - 1; i >= 0; i--) {
					if (activeBindings.Contains (sourceBindings[i])) continue;
					sourceBindings[i].Dispose ();
					sourceBindings.RemoveAt (i);
				}
				// Keep clips, save stacks, graphics state, and active source bindings
				// alive. They may still be needed by later commands.
				state = SurfaceRecordingState.Recording;
			}
		}

		internal SurfaceRecorder (Surface surface) { this.surface = surface; currentState = new ContextState (surface.ManagedStatus); width = surface.RecordedWidth; height = surface.RecordedHeight; }
		internal void Resize (int width, int height)
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording) throw new InvalidOperationException ("Only an active managed recording can be resized.");
				this.width = width;
				this.height = height;
			}
		}
		internal SurfaceRecorderSnapshot CaptureSnapshot() { lock (sync) { if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return null; ImageSurface image = surface as ImageSurface; byte[] pixels = image?.CopyExposedPixels(); return new SurfaceRecorderSnapshot(commands.ToArray(), width, height, resources.ToArray(), Identity, pixels, pixels == null ? 0 : image.ManagedPixelStride, pixels == null ? Format.Argb32 : image.Format, surface.RecordingDeviceOffset); } }
		internal SurfaceRecordingState State { get { lock (sync) return state; } }
		internal bool HasPendingCommands { get { lock (sync) return commands.Count != 0; } }
		internal void CompleteManagedFinish ()
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) throw new InvalidOperationException ("The recorder must be live before finishing a managed surface.");
                ClearCommandsForPixelCheckpoint ();
                state = SurfaceRecordingState.Recording;
			}
		}
		internal string FirstMaterializationTrigger { get { lock (sync) return firstMaterializationTrigger; } }
		internal void SetFirstMaterializationTrigger(string trigger)
		{
			if (string.IsNullOrEmpty(trigger)) return;
			lock (sync) if ((state == SurfaceRecordingState.Recording || state == SurfaceRecordingState.Sealed || state == SurfaceRecordingState.GpuRendered) && firstMaterializationTrigger == null) firstMaterializationTrigger = trigger;
		}

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.GpuRendered) {
					firstMaterializationTrigger ??= "Recorder.Seal.Unsupported:state-" + state;
					snapshot = null; return false;
				}
				if (saveDepth != 0) {
					firstMaterializationTrigger ??= "Recorder.Seal.Unsupported:unbalanced-save-depth-" + saveDepth;
					snapshot = null; return false;
				}
				if (unqualifiedHelperDrawing) {
					firstMaterializationTrigger ??= "Recorder.Seal.Unsupported:unqualified-helper-drawing";
					snapshot = null; return false;
				}
				state = SurfaceRecordingState.Sealed;
				ImageSurface image = surface as ImageSurface;
				byte[] pixels = image?.CopyExposedPixels ();
				snapshot = new SurfaceRecorderSnapshot (commands.ToArray (), width, height, resources.ToArray(), Identity,
					pixels, pixels == null ? 0 : image.ManagedPixelStride, pixels == null ? Format.Argb32 : image.Format, surface.RecordingDeviceOffset);
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

		/// <summary>Returns a recorder sealed for a GPU render that must be discarded (for example after a context reset) to Recording so the display list can be replayed.</summary>
		internal bool AbortGpuRender() { lock (sync) { if (state != SurfaceRecordingState.Sealed) return false; state = SurfaceRecordingState.Recording; return true; } }

		internal void ResumeRecording(bool permitSealed = false) { lock (sync) { if (state == SurfaceRecordingState.GpuRendered || (permitSealed && state == SurfaceRecordingState.Sealed)) state = SurfaceRecordingState.Recording; } }

		internal bool RecordSetSourceRGBA (double r, double g, double b, double a) => RecordNative(c => c.SetSourceRGBA(r, g, b, a), stateUpdate: s => s.SetSourceRgba(r, g, b, a));
		internal bool RecordPaint () => RecordDrawing(c => c.Paint(), paintAll: true);
		internal bool RecordBlur (double range, int edge, int x1, int y1, int x2, int y2, bool full)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var blur = new RecordedBlur (range, edge, x1, y1, x2, y2, full);
			return Add (new Command (c => blur.ApplyToSurface (NativeMethods.cairo_get_target(c.NativeHandleForRecorder)), blur: blur));
		}
		internal bool RecordRectangle (double x, double y, double width, double height)
		{
			bool added = Add (new Command (CommandKind.Rectangle, x, y, width, height));
			if (added) graphicsState.Rectangle (x, y, width, height);
			return added;
		}
		internal bool RecordDemultiplyAlpha() => Add(new Command(c => {
			IntPtr target = NativeMethods.cairo_get_target(c.NativeHandleForRecorder);
			NativeMethods.cairo_surface_flush(target);
			SurfaceTransformDemulAlpha.ApplyToPixels(NativeMethods.cairo_image_surface_get_data(target), width, height);
			NativeMethods.cairo_surface_mark_dirty(target);
		}, demultiply: true));
		internal bool RecordImage(SKBitmap bitmap, int x, int y, int width, int height)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var image = new RecordedGuiImage(bitmap, x, y, width, height);
			if (!Add(new Command(c => image.Replay(NativeMethods.cairo_get_target(c.NativeHandleForRecorder)), image: image))) { image.Dispose(); return false; }
			resources.Add(new RecordedResource(image)); return true;
		}
		internal bool RecordPicture(SKPicture picture, int x, int y, int width, int height, int? tint, Action<Context> replay, bool textureColorOrder, RecordedPreparationCache.Picture pictureOwner = null)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var drawing = new RecordedGuiPicture(picture, x, y, width, height, tint, replay, textureColorOrder, pictureOwner?.Retain());
			if (!Add(new Command(replay, picture: drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing)); return true;
		}
		internal bool RecordFill ()
		{
			return RecordDrawing(c => c.Fill(), clearsPath: true);
		}
		internal bool RecordSave () { Antialias antialias = graphicsState.Antialias; bool recorded = RecordNative(c => c.Save(), stateUpdate: s => s.Save()); if (recorded) { savedClips.Push(clips.ToArray()); savedAntialias.Push(antialias); saveDepth++; } return recorded; }
		internal bool RecordRestore () { if (saveDepth == 0) return RecordNative(c => c.Restore(), stateUpdate: s => s.Restore());  savedAntialias.Pop(); clips.Clear(); clips.AddRange(savedClips.Pop()); saveDepth--; return RecordNative(c => c.Restore(), stateUpdate: s => s.Restore()); }
		internal bool RecordAntialias (Antialias value)
		{
			bool recorded = Add (new Command (CommandKind.SetAntialias, (double)value));
			if (recorded) graphicsState.Antialias = value;
			if (recorded && value != Antialias.Default) antialiasConfigured = true;
			return recorded;
		}
		internal bool RecordOperator (Operator value)
		{
			bool recorded = Add (new Command (CommandKind.SetOperator, (double)value));
			if (recorded) graphicsState.Operator = value;
			return recorded;
		}

		internal void FillRectangle (double x, double y, double width, double height, double red, double green, double blue, double alpha)
		{
			unqualifiedHelperDrawing |= !antialiasConfigured;
			if (!RecordSetSourceRGBA (red, green, blue, alpha) || !RecordRectangle (x, y, width, height) || !RecordDrawing(c => c.Fill(), clearsPath: true))
				throw new InvalidOperationException ("Recorder is no longer recording.");
		}

		bool Add (Command command)
		{
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state == SurfaceRecordingState.Disposed) throw new ObjectDisposedException (nameof (SurfaceRecorder));
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording) return false;
				if (!surface.ManagedFinished) commands.Add (surface.IsManagedOnly ? command.WithContext (currentState.Identity) : command);
				return true;
			}
		}

		internal void Materialize (IntPtr contextHandle = default)
		{
			if (Surface.AutomaticRecordingEnabled)
				throw new InvalidOperationException ("Cairo materialization is forbidden while the Skia GUI renderer is requested.");
			if (surface.IsManagedOnly)
				throw new NotSupportedException ("A managed-only image surface cannot be materialized as a native Cairo surface.");
			string trigger = null;
			ExceptionDispatchInfo materializationFailure = null;
			bool attempted = false;
			lock (sync) {
				if (state == SurfaceRecordingState.Failed) failure.Throw();
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return;
				attempted = true;
				trigger = firstMaterializationTrigger ?? "Unknown";
				state = SurfaceRecordingState.Materializing;
				try {
					SurfaceRecordingDiagnostics.Materialize();
					IntPtr native = surface.NativeHandleForMetadata;
					ThrowIfError (NativeMethods.cairo_surface_status (native), "surface before replay");
					IntPtr replayHandle = contextHandle == IntPtr.Zero ? NativeMethods.cairo_create (native) : contextHandle;
					using (var context = new Context (replayHandle, contextHandle == IntPtr.Zero)) {
						ThrowIfError (context.Status, "context creation");
						for (int i = 0; i < commands.Count; i++) {
							commands[i].Replay (context);
							// An error the recorded program itself raised (for example an unbalanced restore) is Cairo's own behavior, not a replay failure.
							if (graphicsState.Status == Status.Success) ThrowIfError (context.Status, "command replay");
							ThrowIfError (NativeMethods.cairo_surface_status (native), "surface replay");
						}
					}
					ClearCommands();
					state = SurfaceRecordingState.NativeMaterialized;
				} catch (Exception exception) {
					ClearCommands();
					failure = ExceptionDispatchInfo.Capture (exception);
					state = SurfaceRecordingState.Failed;
					materializationFailure = failure;
				}
			}
			if (attempted) {
				try { MaterializationCompleted?.Invoke(trigger, materializationFailure == null); }
				catch { /* Diagnostics must not change Cairo behavior. */ }
			}
			materializationFailure?.Throw();
		}

		internal void DisposeFromSurface ()
		{
			lock (sync) {
				ClearCommands ();
				currentState.Graphics.ReleaseFontOwnership ();
				foreach (KeyValuePair<Context, ContextState> entry in contextStates) entry.Value.Graphics.ReleaseFontOwnership ();
				state = SurfaceRecordingState.Disposed;
			}
		}

		static void ThrowIfError (Status status, string phase)
		{
			if (status != Status.Success) throw new InvalidOperationException ("Cairo " + phase + " failed with status " + status + ".");
		}

		internal readonly struct Command
		{
			internal readonly object ContextIdentity;
            internal Command WithContext (object identity) => new Command (this, identity);
            Command (Command command, object identity) { this = command; ContextIdentity = identity; }
            internal readonly Action<Context> NativeReplay;
			internal readonly RecordedGuiDrawing Drawing;
			internal readonly RecordedBlur Blur;
			internal readonly bool Demultiply;
			internal readonly RecordedGuiImage Image;
			internal readonly RecordedGuiPicture Picture;
			internal Command(Action<Context> replay, RecordedGuiDrawing drawing = null, RecordedBlur blur = null, bool demultiply = false, RecordedGuiImage image = null, RecordedGuiPicture picture = null) { ContextIdentity = null; Kind = CommandKind.Native; A = B = C = D = 0; NativeReplay = replay; Drawing = drawing; Blur = blur; Demultiply = demultiply; Image = image; Picture = picture; }
			internal readonly CommandKind Kind;
			internal readonly double A, B, C, D;
			internal Command (CommandKind kind, double a = 0, double b = 0, double c = 0, double d = 0) { ContextIdentity = null; Kind = kind; A = a; B = b; C = c; D = d; NativeReplay = null; Drawing = null; Blur = null; Demultiply = false; Image = null; Picture = null; }
			internal void Replay (Context context)
			{
				switch (Kind) {
				case CommandKind.Native: NativeReplay(context); break;
				case CommandKind.SetSourceRGBA: context.SetSourceRGBA (A, B, C, D); break;
				case CommandKind.Paint: context.Paint (); break;
				case CommandKind.Rectangle: context.Rectangle (A, B, C, D); break;
				case CommandKind.Fill: context.Fill (); break;
				case CommandKind.Save: context.Save (); break;
				case CommandKind.Restore: context.Restore (); break;
				case CommandKind.SetAntialias: context.Antialias = (Antialias)A; break;
				case CommandKind.SetOperator: context.Operator = (Operator)A; break;
				}
			}
		}

		internal enum CommandKind { Native, SetSourceRGBA, Paint, Rectangle, Fill, Save, Restore, SetAntialias, SetOperator }
	}

    internal sealed class NativeContextReplay : IDisposable
    {
        readonly Context target;
        readonly Dictionary<object, Context> contexts = new Dictionary<object, Context> ();
        internal NativeContextReplay (Context target) { this.target = target; }
        internal void Execute (SurfaceRecorder.Command command)
        {
            Context selected = target;
            if (command.ContextIdentity != null && !contexts.TryGetValue (command.ContextIdentity, out selected)) {
                selected = new Context (NativeMethods.cairo_create (NativeMethods.cairo_get_target (target.NativeHandleForRecorder)), true);
                contexts.Add (command.ContextIdentity, selected);
            }
            command.Replay (selected);
        }
        public void Dispose () { foreach (Context context in contexts.Values) context.Dispose (); }
    }

	internal sealed class SurfaceRecorderSnapshot : IDisposable
	{
		readonly SurfaceRecorder.Command[] commands;
		readonly int width, height;
		readonly RecordedResource[] resources;
		readonly byte[] pixels;
		readonly int pixelStride;
		readonly Format pixelFormat;
		readonly PointD deviceOffset;
		internal int Width => width;
		internal int Height => height;
		internal Format PixelFormat => pixelFormat;
		internal long Identity { get; }
		internal int Generation => commands.Length;
		bool disposed;
		internal SurfaceRecorderSnapshot (SurfaceRecorder.Command[] commands, int width, int height, RecordedResource[] resources, long identity, byte[] pixels = null, int pixelStride = 0, Format pixelFormat = Format.Argb32, PointD deviceOffset = default) { this.commands = commands; this.width = width; this.height = height; this.resources = resources; this.pixels = pixels; this.pixelStride = pixelStride; this.pixelFormat = pixelFormat; this.deviceOffset = deviceOffset; Identity = identity; foreach (var resource in resources) resource.Retain(); }
		internal SurfaceRecorderSnapshot Retain() => new SurfaceRecorderSnapshot(commands, width, height, resources, Identity, pixels, pixelStride, pixelFormat, deviceOffset);
		internal void ReplayNative(IntPtr target)
		{
			using (var context = new Context (NativeMethods.cairo_create (target), true)) {
				context.Operator = Operator.Clear;
				context.Paint ();
				context.Operator = Operator.Over;
				if (pixels != null) {
					NativeMethods.cairo_surface_flush (target);
					IntPtr data = NativeMethods.cairo_image_surface_get_data (target);
					int targetStride = NativeMethods.cairo_image_surface_get_stride (target);
					int rowBytes = ManagedImageSurfaceStorage.MinimumRowBytes (pixelFormat, width);
					if (data == IntPtr.Zero || targetStride < rowBytes || pixelStride < rowBytes || pixels.Length < checked (pixelStride * height))
						throw new InvalidOperationException ("The recorded image pixels do not match the native Cairo surface layout.");
					for (int y = 0; y < height; y++)
						Marshal.Copy (pixels, checked (y * pixelStride), IntPtr.Add (data, checked (y * targetStride)), rowBytes);
					NativeMethods.cairo_surface_mark_dirty (target);
				}
				using (var replay = new NativeContextReplay (context)) foreach (var command in commands) replay.Execute (command);
			}
		}
		internal void ReplayNative(Context context) { SurfaceRecordingDiagnostics.Materialize(); using var replay = new NativeContextReplay (context); foreach (var command in commands) replay.Execute (command); }
		public void Dispose() { if (disposed) return; disposed = true; foreach (var resource in resources) resource.Dispose(); }

		internal void DrawTo (SKSurface target, RecordedGpuBlur blur)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.GpuReplay);
			if (target == null) throw new ArgumentNullException (nameof (target));
			SKCanvas canvas = target.Canvas;
			int save = canvas.Save ();
			try {
				canvas.Clear (SKColors.Transparent);
				canvas.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
				DrawPixels(canvas);
				foreach (SurfaceRecorder.Command command in commands) {
					if (command.Drawing != null) command.Drawing.Draw(target, blur, null, width, height, deviceOffset);
					if (command.Blur != null) blur.Apply (command.Blur, target, width, height);
					if (command.Demultiply) blur.Demultiply(target, width, height);
					if (command.Image != null) blur.DrawImage(command.Image, target, width, height);
					if (command.Picture != null) blur.DrawPicture(command.Picture, target, width, height);
				}
			} finally { canvas.RestoreToCount (save); }
		}

		internal void DrawTo (SKSurface target)
		{
			if (target == null) throw new ArgumentNullException(nameof(target));
			SKRuntimeEffect lcdEffect = null;
			try {
				bool hasBlur = false;
				foreach (SurfaceRecorder.Command command in commands) hasBlur |= command.Blur != null || command.Demultiply || command.Image != null || command.Picture != null;
				SKCanvas canvas = target.Canvas;
				int save = canvas.Save();
				try {
					canvas.Clear(SKColors.Transparent);
					canvas.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
					DrawPixels(canvas);
					if (!hasBlur) {
						foreach (SurfaceRecorder.Command command in commands) {
							if (command.Drawing != null && command.Drawing.CanUseLcdCompositor) {
								lcdEffect ??= CreateLcdTextEffect();
								command.Drawing.Draw(target, null, lcdEffect, width, height, deviceOffset);
							} else command.Drawing?.Draw(canvas, deviceOffset: deviceOffset);
						}
						return;
					}
					SKRuntimeEffect overlayEffect = null;
					try {
						using (var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)))
						using (var properties = new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal))
						using (var rasterSurface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, properties)) {
							if (rasterSurface == null) throw new InvalidOperationException("Skia could not create a software GUI surface with pixel geometry.");
							SKCanvas raster = rasterSurface.Canvas;
							raster.Clear(SKColors.Transparent);
							raster.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
							DrawPixels(raster);
							foreach (SurfaceRecorder.Command command in commands) {
								if (command.Drawing != null && command.Drawing.CanUseLcdCompositor) {
									lcdEffect ??= CreateLcdTextEffect();
									command.Drawing.Draw(rasterSurface, null, lcdEffect, width, height, deviceOffset);
								} else command.Drawing?.Draw(raster, deviceOffset: deviceOffset);
								if (command.Blur != null) { raster.Flush(); command.Blur.ApplyToPixels(bitmap.GetPixels(), width, height); bitmap.NotifyPixelsChanged(); }
								if (command.Demultiply) { raster.Flush(); SurfaceTransformDemulAlpha.ApplyToPixels(bitmap.GetPixels(), width, height); bitmap.NotifyPixelsChanged(); }
								if (command.Image != null) { raster.Flush(); overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU image"); RecordedGpuBlur.DrawImageCpu(command.Image, rasterSurface, width, height, overlayEffect); bitmap.NotifyPixelsChanged(); }
								if (command.Picture != null) { raster.Flush(); overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU picture"); RecordedGpuBlur.DrawPictureCpu(command.Picture, rasterSurface, width, height, overlayEffect); bitmap.NotifyPixelsChanged(); }
							}
							using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) canvas.DrawBitmap(bitmap, 0, 0, paint);
						}
					} finally { overlayEffect?.Dispose(); }
				} finally { canvas.RestoreToCount(save); }
			} finally { lcdEffect?.Dispose(); }
		}

		static SKRuntimeEffect CreateLcdTextEffect()
		{
			SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(RecordedGuiDrawing.LcdTextCompositorShaderSource, out string errors);
			return effect ?? throw new NotSupportedException("Recorded LCD text compositor shader compilation failed: " + errors);
		}

		internal void DrawTo (SKCanvas canvas, float outputScale = 1f)
		{
			if (canvas == null) throw new ArgumentNullException(nameof(canvas));
			int save = canvas.Save();
			try {
				canvas.Clear(SKColors.Transparent);
				canvas.Scale (outputScale);
				canvas.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
				DrawPixels(canvas);
				bool hasBlur = false;
				foreach (SurfaceRecorder.Command command in commands) hasBlur |= command.Blur != null || command.Demultiply || command.Image != null || command.Picture != null;
				if (!hasBlur) { foreach (SurfaceRecorder.Command command in commands) command.Drawing?.Draw(canvas, deviceOffset: deviceOffset, outputScale: outputScale); return; }
				// The game's blur reads and rewrites pixels, so those segments run on a raster layer
				// (still Skia) and the result is composited back onto the target canvas.
				SKRuntimeEffect overlayEffect = null;
				try {
				using (var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul))) {
					using (var properties = new SKSurfaceProperties (SKPixelGeometry.RgbHorizontal))
					using (var rasterSurface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, properties)) {
						if (rasterSurface == null) throw new InvalidOperationException("Skia could not create a software GUI surface with pixel geometry.");
						SKCanvas raster = rasterSurface.Canvas;
						raster.Clear (SKColors.Transparent);
						raster.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
						DrawPixels(raster);
						foreach (SurfaceRecorder.Command command in commands) {
							command.Drawing?.Draw (raster, deviceOffset: deviceOffset);
							if (command.Blur != null) { raster.Flush (); command.Blur.ApplyToPixels (bitmap.GetPixels (), width, height); bitmap.NotifyPixelsChanged (); }
							if (command.Demultiply) { raster.Flush(); SurfaceTransformDemulAlpha.ApplyToPixels(bitmap.GetPixels(), width, height); bitmap.NotifyPixelsChanged(); }
							if (command.Image != null) {
								raster.Flush();
								overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU image");
								RecordedGpuBlur.DrawImageCpu(command.Image, rasterSurface, width, height, overlayEffect);
								bitmap.NotifyPixelsChanged();
							}
							if (command.Picture != null) {
								raster.Flush();
								overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU picture");
								RecordedGpuBlur.DrawPictureCpu(command.Picture, rasterSurface, width, height, overlayEffect);
								bitmap.NotifyPixelsChanged();
							}
						}
					}
					using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
						canvas.DrawBitmap (bitmap, 0, 0, paint);
				}
				} finally { overlayEffect?.Dispose(); }
			} finally { canvas.RestoreToCount(save); }
		}

		void DrawPixels(SKCanvas canvas)
		{
			if (pixels == null || !ManagedImageSurfaceStorage.CanRecordFormat (pixelFormat) || width <= 0 || height <= 0) return;
			using var bitmap = new SKBitmap (ManagedImageSurfaceStorage.SkiaInfo (pixelFormat, width, height));
			IntPtr output = bitmap.GetPixels();
			if (pixelFormat == Format.A1) {
				for (int y = 0; y < height; y++) {
					int sourceRow = checked (y * pixelStride);
					IntPtr targetRow = IntPtr.Add (output, checked (y * bitmap.RowBytes));
					for (int x = 0; x < width; x++) {
						byte alpha = (pixels[sourceRow + (x >> 3)] & (1 << (x & 7))) != 0 ? (byte)255 : (byte)0;
						Marshal.WriteByte (targetRow, x, alpha);
					}
				}
				using var alphaPaint = new SKPaint { BlendMode = SKBlendMode.Src };
				canvas.DrawBitmap (bitmap, 0, 0, alphaPaint);
				return;
			}
			int rowBytes = ManagedImageSurfaceStorage.MinimumRowBytes (pixelFormat, width);
			for (int y = 0; y < height; y++)
				Marshal.Copy(pixels, checked(y * pixelStride), IntPtr.Add(output, checked(y * bitmap.RowBytes)), rowBytes);
			using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
			canvas.DrawBitmap(bitmap, 0, 0, paint);
		}
	}

}
