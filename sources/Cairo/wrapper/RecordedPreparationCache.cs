using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using SkiaSharp;

namespace Cairo
{
    // CPU-only immutable preparation. No GL resources, live sources, clips or mod callbacks.
    internal static class RecordedPreparationCache
    {
        internal const int MaxEntries = 1024;
        internal const long MaxBytes = 16 * 1024 * 1024;
        internal const int MaxKeyBytes = 128 * 1024;
        static int enabled = 1;
        static readonly UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
        static readonly object sync = new object();
        static readonly Dictionary<Key, Entry> entries = new Dictionary<Key, Entry>();
        static readonly LinkedList<Key> order = new LinkedList<Key>();
        static long bytes, hits, misses, evictions, textHits, pathHits, strokeHits, pictureHits;
        internal static bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }
        internal static long Hits => Interlocked.Read(ref hits);
        internal static long Misses => Interlocked.Read(ref misses);
        internal static long Evictions => Interlocked.Read(ref evictions);
        internal static long TextHits => Interlocked.Read(ref textHits);
        internal static long PathHits => Interlocked.Read(ref pathHits);
        internal static long StrokeHits => Interlocked.Read(ref strokeHits);
        internal static long PictureHits => Interlocked.Read(ref pictureHits);
        internal static long Bytes { get { lock (sync) return bytes; } }
        internal static long Count { get { lock (sync) return entries.Count; } }
        internal static void ResetCounters() { Interlocked.Exchange(ref hits, 0); Interlocked.Exchange(ref misses, 0); Interlocked.Exchange(ref evictions, 0); Interlocked.Exchange(ref textHits, 0); Interlocked.Exchange(ref pathHits, 0); Interlocked.Exchange(ref strokeHits, 0); Interlocked.Exchange(ref pictureHits, 0); }
        internal static void Clear()
        {
            lock (sync) { foreach (var entry in entries.Values) entry.Resource.Dispose(); entries.Clear(); order.Clear(); bytes = 0; }
        }

        internal sealed class Key : IEquatable<Key>
        {
            internal readonly int Kind;
            readonly long[] words;
            readonly byte[] data;
            readonly string text;
            readonly int hash;
            internal readonly long Bytes;
            internal Key(int kind, long[] words, byte[] data = null, string text = null)
            {
                Kind = kind; this.words = words; this.data = data; this.text = text;
                Bytes = 96L + words.Length * 8L + (data?.Length ?? 0) + (text?.Length ?? 0) * 2L;
                unchecked { uint h = (uint)kind + 2166136261u; foreach (long word in words) { h = (h ^ (uint)word) * 16777619; h = (h ^ (uint)(word >> 32)) * 16777619; } if (data != null) foreach (byte b in data) h = (h ^ b) * 16777619; if (text != null) foreach (char c in text) h = (h ^ c) * 16777619; hash = (int)h; }
            }
            public override int GetHashCode() => hash;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public bool Equals(Key other)
            {
                if (other == null || Kind != other.Kind || words.Length != other.words.Length || text != other.text || (data?.Length ?? -1) != (other.data?.Length ?? -1)) return false;
                for (int i = 0; i < words.Length; i++) if (words[i] != other.words[i]) return false;
                if (data != null) for (int i = 0; i < data.Length; i++) if (data[i] != other.data[i]) return false;
                return true;
            }
        }
        sealed class Entry
        {
            internal readonly Resource Resource;
            internal readonly LinkedListNode<Key> Node;
            internal readonly long Bytes;
            internal Entry(Resource resource, LinkedListNode<Key> node, long bytes) { Resource = resource; Node = node; Bytes = bytes; }
        }
        internal abstract class Resource : IDisposable
        {
            int references = 1;
            internal abstract long Bytes { get; }
            internal Resource Retain() { Interlocked.Increment(ref references); return this; }
            public void Dispose() { if (Interlocked.Decrement(ref references) == 0) Release(); }
            protected abstract void Release();
        }
        internal sealed class Geometry : Resource
        {
            internal readonly SKPath Path;
            internal Geometry(SKPath path) { Path = path; }
            internal override long Bytes => PathBytes(Path);
            protected override void Release() => Path.Dispose();
        }
        internal sealed class Text : Resource
        {
            internal readonly Path NativePath;
            internal readonly SKPath Outline;
            internal readonly TextExtents Extents;
            readonly ScaledFont font;
            readonly long nativeBytes;
            internal Text(Path path, SKPath outline, TextExtents extents, ScaledFont font, long nativeBytes) { NativePath = path; Outline = outline; Extents = extents; this.font = font; this.nativeBytes = nativeBytes; }
            internal override long Bytes => PathBytes(Outline) + nativeBytes + 1024;
            protected override void Release() { NativePath.Dispose(); Outline.Dispose(); font.Dispose(); }
        }
        internal sealed class Picture : Resource
        {
            internal readonly SKPicture Value;
            internal Picture(SKPicture picture) { Value = picture; }
            internal override long Bytes => Math.Max(256L, Value.ApproximateBytesUsed);
            protected override void Release() => Value.Dispose();
        }
        static long PathBytes(SKPath path) => 256L + path.PointCount * 16L + path.VerbCount * 4L;
        internal static T Acquire<T>(Key key) where T : Resource
        {
            if (!Enabled || key == null || key.Bytes > MaxKeyBytes) return null;
            lock (sync) {
                if (entries.TryGetValue(key, out Entry entry)) {
                    order.Remove(entry.Node); order.AddLast(entry.Node); Interlocked.Increment(ref hits);
                    if (key.Kind == 1 || key.Kind == 2) Interlocked.Increment(ref textHits);
                    else if (key.Kind == 3) Interlocked.Increment(ref pathHits);
                    else if (key.Kind == 4) Interlocked.Increment(ref strokeHits);
                    else if (key.Kind == 5) Interlocked.Increment(ref pictureHits);
                    return (T)entry.Resource.Retain();
                }
                Interlocked.Increment(ref misses); return null;
            }
        }
        internal static void Store(Key key, Resource resource)
        {
            if (!Enabled || key == null || key.Bytes > MaxKeyBytes) return;
            long cost = checked(key.Bytes + resource.Bytes + 128);
            if (cost > MaxBytes) return;
            lock (sync) {
                if (entries.ContainsKey(key)) return; // A racing producer keeps its own uncached result.
                while (entries.Count >= MaxEntries || bytes + cost > MaxBytes) {
                    Key oldest = order.First.Value; Entry old = entries[oldest];
                    entries.Remove(oldest); order.RemoveFirst(); bytes -= old.Bytes; old.Resource.Dispose(); Interlocked.Increment(ref evictions);
                }
                entries.Add(key, new Entry(resource.Retain(), order.AddLast(key), cost)); bytes += cost;
            }
        }
        internal static SKPath StrokeOutline(SKPath path, SKPaint paint, float[] intervals, float offset)
        {
            Key key = null;
            if (Enabled) {
                if (path.VerbCount <= 1024) {
                    var values = new List<long>(16 + path.PointCount * 2 + path.VerbCount);
                    values.Add(Bits(paint.StrokeWidth)); values.Add(Bits(paint.StrokeMiter)); values.Add((int)paint.StrokeCap); values.Add((int)paint.StrokeJoin); values.Add(Bits(offset)); values.Add((int)path.FillType); values.Add(intervals?.Length ?? 0);
                    if (intervals != null) foreach (float value in intervals) values.Add(Bits(value));
                    using (var iterator = path.CreateRawIterator()) {
                        var points = new SKPoint[4]; SKPathVerb verb;
                        while ((verb = iterator.Next(points)) != SKPathVerb.Done) {
                            values.Add((int)verb);
                            int count = verb == SKPathVerb.Move ? 1 : verb == SKPathVerb.Line ? 2 : verb == SKPathVerb.Quad || verb == SKPathVerb.Conic ? 3 : verb == SKPathVerb.Cubic ? 4 : 0;
                            for (int i = 0; i < count; i++) { values.Add(Bits(points[i].X)); values.Add(Bits(points[i].Y)); }
                            if (verb == SKPathVerb.Conic) values.Add(Bits(iterator.ConicWeight()));
                        }
                    }
                    key = new Key(4, values.ToArray());
                }
                using (Geometry cached = Acquire<Geometry>(key)) if (cached != null) return new SKPath(cached.Path);
            }
            using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.StrokeOutline);
            var outline = new SKPath();
            if (!paint.GetFillPath(path, outline)) { outline.Dispose(); return null; }
            if (key != null) using (var prepared = new Geometry(new SKPath(outline))) Store(key, prepared);
            return outline;
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePathInfo { internal Status Status; internal IntPtr Data; internal int Count; }
        static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
        internal static Text PrepareText(Context context, byte[] text, Glyph[] glyphs = null)
        {
            if (!Enabled || (text?.Length ?? 0) > MaxKeyBytes / 2 || (glyphs?.Length ?? 0) > MaxKeyBytes / 32) return null;
            if (glyphs == null) {
                if (text == null || text.Length == 0 || text[0] == 0) return null;
                int end = Array.IndexOf(text, (byte)0);
                try { strictUtf8.GetCharCount(text, 0, end < 0 ? text.Length : end); }
                catch (DecoderFallbackException) { return null; } // Preserve native handling of malformed input.
            }
            if (NativeMethods.cairo_status(context.NativeHandleForRecorder) != Status.Success) return null;
            IntPtr handle = NativeMethods.cairo_get_scaled_font(context.NativeHandleForRecorder);
            FontType type = NativeMethods.cairo_scaled_font_get_type(handle);
            if ((int)type < 0 || (int)type > 3 || NativeMethods.cairo_scaled_font_status(handle) != Status.Success) return null;
            Matrix m = context.Matrix;
            PointD point = context.HasCurrentPoint ? context.CurrentPoint : new PointD(0, 0);
            var words = new long[9 + (glyphs?.Length ?? 0) * 3];
            words[0] = handle.ToInt64(); words[1] = Bits(m.Xx); words[2] = Bits(m.Yx); words[3] = Bits(m.Xy); words[4] = Bits(m.Yy); words[5] = Bits(m.X0); words[6] = Bits(m.Y0); words[7] = Bits(point.X); words[8] = Bits(point.Y);
            if (glyphs != null) for (int i = 0; i < glyphs.Length; i++) { words[9+i*3] = glyphs[i].Index; words[10+i*3] = Bits(glyphs[i].X); words[11+i*3] = Bits(glyphs[i].Y); }
            var key = new Key(glyphs == null ? 1 : 2, words, text == null ? null : (byte[])text.Clone());
            Text cached = Acquire<Text>(key); if (cached != null) return cached;
            Path path = null; SKPath outline = null; ScaledFont font = null;
            using (Path old = context.CopyPath()) {
                try {
                    TextExtents extents = glyphs == null ? context.TextExtents(text) : default;
                    if (context.Status != Status.Success) return null;
                    context.NewPath();
                    if (glyphs == null) { context.MoveTo(point); context.TextPath(text); } else context.GlyphPath(glyphs);
                    if (context.Status != Status.Success) return null;
                    path = context.CopyPath(); outline = RecordedGuiDrawing.CopyPath(path.Handle);
                    // Retain the actual immutable scaled font: its pointer cannot be recycled while cached.
                    font = new ScaledFont(handle, false);
                    int count = Marshal.PtrToStructure<NativePathInfo>(path.Handle).Count;
                    var result = new Text(path, outline, extents, font, Math.Max(0L, count) * 16L + 32);
                    path = null; outline = null; font = null;
                    try { Store(key, result); return result; } catch { result.Dispose(); throw; }
                } finally { context.NewPath(); context.AppendPath(old); path?.Dispose(); outline?.Dispose(); font?.Dispose(); }
            }
        }
        internal static void AppendTextPath(Context context, byte[] text, Glyph[] glyphs = null)
        {
            using (Text prepared = PrepareText(context, text, glyphs)) {
                if (prepared == null) { if (glyphs == null) context.TextPath(text); else context.GlyphPath(glyphs); }
                else context.AppendPath(prepared.NativePath);
            }
        }
    }
}
