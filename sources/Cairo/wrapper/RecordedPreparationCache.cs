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
        static long bytes, hits, misses, evictions, pathHits, strokeHits, pictureHits;
        internal static bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }
        internal static long Hits => Interlocked.Read(ref hits);
        internal static long Misses => Interlocked.Read(ref misses);
        internal static long Evictions => Interlocked.Read(ref evictions);
        internal static long PathHits => Interlocked.Read(ref pathHits);
        internal static long StrokeHits => Interlocked.Read(ref strokeHits);
        internal static long PictureHits => Interlocked.Read(ref pictureHits);
        internal static long Bytes { get { lock (sync) return bytes; } }
        internal static long Count { get { lock (sync) return entries.Count; } }
        internal static void ResetCounters() { Interlocked.Exchange(ref hits, 0); Interlocked.Exchange(ref misses, 0); Interlocked.Exchange(ref evictions, 0); Interlocked.Exchange(ref pathHits, 0); Interlocked.Exchange(ref strokeHits, 0); Interlocked.Exchange(ref pictureHits, 0); }
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
                    if (key.Kind == 3) Interlocked.Increment(ref pathHits);
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
            RecordedDashCaps.Append(path, outline, paint, intervals, offset);
            if (key != null) using (var prepared = new Geometry(new SKPath(outline))) Store(key, prepared);
            return outline;
        }
        static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    }
}
