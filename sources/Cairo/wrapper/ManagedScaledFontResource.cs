using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Cairo
{
	/// <summary>The immutable Cairo identity fields for one managed scaled font.</summary>
	internal sealed class ManagedScaledFontKey : IEquatable<ManagedScaledFontKey>
	{
		readonly ManagedFontFaceResource face;
		readonly long fxx, fyx, fxy, fyy, fx0, fy0;
		readonly long cxx, cyx, cxy, cyy, cx0, cy0;
		readonly Antialias antialias;
		readonly HintStyle hintStyle;
		readonly HintMetrics hintMetrics;
		readonly SubpixelOrder subpixelOrder;

		internal ManagedScaledFontKey (ManagedFontFaceResource face, Matrix fontMatrix, Matrix ctm, FontOptions options)
		{
			this.face = face ?? throw new ArgumentNullException (nameof (face));
			if (ReferenceEquals (fontMatrix, null)) throw new ArgumentNullException (nameof (fontMatrix));
			if (ReferenceEquals (ctm, null)) throw new ArgumentNullException (nameof (ctm));
			if (options == null) throw new ArgumentNullException (nameof (options));
			if (!HasFiniteDeterminant (fontMatrix) || !HasFiniteDeterminant (ctm))
				throw new ArgumentException ("Scaled-font matrix determinants must be finite.");
			ValidateCombinedScale (fontMatrix, ctm);

			fxx = Bits (fontMatrix.Xx); fyx = Bits (fontMatrix.Yx);
			fxy = Bits (fontMatrix.Xy); fyy = Bits (fontMatrix.Yy);
			fx0 = Bits (fontMatrix.X0); fy0 = Bits (fontMatrix.Y0);
			cxx = Bits (ctm.Xx); cyx = Bits (ctm.Yx);
			cxy = Bits (ctm.Xy); cyy = Bits (ctm.Yy);
			// cairo_scaled_font keys deliberately discard CTM translation.
			cx0 = Bits (0d); cy0 = Bits (0d);
			antialias = options.Antialias;
			hintStyle = options.HintStyle;
			hintMetrics = options.HintMetrics;
			subpixelOrder = options.SubpixelOrder;
		}

		static long Bits (double value) => BitConverter.DoubleToInt64Bits (value);
		static bool HasFiniteDeterminant (Matrix value) => double.IsFinite (value.Xx * value.Yy - value.Yx * value.Xy);

		static void ValidateCombinedScale (Matrix font, Matrix ctm)
		{
			double xx = font.Xx * ctm.Xx + font.Yx * ctm.Xy;
			double yx = font.Xx * ctm.Yx + font.Yx * ctm.Yy;
			double xy = font.Xy * ctm.Xx + font.Yy * ctm.Xy;
			double yy = font.Xy * ctm.Yx + font.Yy * ctm.Yy;
			if (xx == 0d && yx == 0d && xy == 0d && yy == 0d) return;
			double determinant = xx * yy - yx * xy;
			if (!double.IsFinite (determinant) || determinant == 0d)
				throw new ArgumentException ("Combined scaled-font matrix must have a finite, invertible scale.");
		}

		public bool Equals (ManagedScaledFontKey other) => other != null && ReferenceEquals (face, other.face) &&
			fxx == other.fxx && fyx == other.fyx && fxy == other.fxy && fyy == other.fyy && fx0 == other.fx0 && fy0 == other.fy0 &&
			cxx == other.cxx && cyx == other.cyx && cxy == other.cxy && cyy == other.cyy && cx0 == other.cx0 && cy0 == other.cy0 &&
			antialias == other.antialias && hintStyle == other.hintStyle && hintMetrics == other.hintMetrics && subpixelOrder == other.subpixelOrder;

		public override bool Equals (object obj) => Equals (obj as ManagedScaledFontKey);

		public override int GetHashCode ()
		{
			unchecked {
				int hash = RuntimeHelpers.GetHashCode (face);
				hash = hash * 31 + fxx.GetHashCode (); hash = hash * 31 + fyx.GetHashCode ();
				hash = hash * 31 + fxy.GetHashCode (); hash = hash * 31 + fyy.GetHashCode ();
				hash = hash * 31 + fx0.GetHashCode (); hash = hash * 31 + fy0.GetHashCode ();
				hash = hash * 31 + cxx.GetHashCode (); hash = hash * 31 + cyx.GetHashCode ();
				hash = hash * 31 + cxy.GetHashCode (); hash = hash * 31 + cyy.GetHashCode ();
				hash = hash * 31 + cx0.GetHashCode (); hash = hash * 31 + cy0.GetHashCode ();
				hash = hash * 31 + (int)antialias; hash = hash * 31 + (int)hintStyle;
				hash = hash * 31 + (int)hintMetrics; hash = hash * 31 + (int)subpixelOrder;
				return hash;
			}
		}
	}

	/// <summary>Immutable metadata and the single face owner retained by a cache entry.</summary>
	internal sealed class ManagedScaledFontResource : IDisposable
	{
		internal readonly ManagedFontFaceResource Face;
		readonly Matrix fontMatrix;
		readonly Matrix ctm;
		readonly Antialias antialias;
		readonly HintStyle hintStyle;
		readonly HintMetrics hintMetrics;
		readonly SubpixelOrder subpixelOrder;
		int scaledReferenceCount;
		bool disposed;

		internal ManagedScaledFontResource (ManagedFontFaceResource face, Matrix fontMatrix, Matrix ctm, FontOptions options)
		{
			Face = face ?? throw new ArgumentNullException (nameof (face));
			this.fontMatrix = (Matrix)(fontMatrix ?? throw new ArgumentNullException (nameof (fontMatrix))).Clone ();
			this.ctm = (Matrix)(ctm ?? throw new ArgumentNullException (nameof (ctm))).Clone ();
			this.ctm.X0 = 0d;
			this.ctm.Y0 = 0d;
			if (options == null) throw new ArgumentNullException (nameof (options));
			antialias = options.Antialias;
			hintStyle = options.HintStyle;
			hintMetrics = options.HintMetrics;
			subpixelOrder = options.SubpixelOrder;
			Face.AddReference ();
		}

		internal Matrix FontMatrix => (Matrix)fontMatrix.Clone ();
		internal Matrix Ctm => (Matrix)ctm.Clone ();
		internal Antialias Antialias => antialias;
		internal HintStyle HintStyle => hintStyle;
		internal HintMetrics HintMetrics => hintMetrics;
		internal SubpixelOrder SubpixelOrder => subpixelOrder;
		internal int ScaledReferenceCount => Volatile.Read (ref scaledReferenceCount);

		// Cache-only reference transitions. These methods do not invoke backend code.
		internal void AddScaledReference ()
		{
			if (disposed) throw new ObjectDisposedException (nameof (ManagedScaledFontResource));
			if (scaledReferenceCount == int.MaxValue) throw new InvalidOperationException ("Managed ScaledFont reference count overflowed.");
			scaledReferenceCount++;
		}

		internal void ReleaseScaledReference ()
		{
			if (scaledReferenceCount <= 0) throw new InvalidOperationException ("Managed ScaledFont reference count underflowed.");
			scaledReferenceCount--;
		}

		public void Dispose ()
		{
			if (disposed) return;
			if (scaledReferenceCount != 0) throw new InvalidOperationException ("Cannot evict an active Managed ScaledFont resource.");
			disposed = true;
			Face.Release ();
		}
	}

	/// <summary>
	/// Managed equivalent of Cairo's scaled-font map: active entries are never capped,
	/// while 256 unused entries and one separate MRU cache reference are retained.
	/// </summary>
	internal sealed class ManagedScaledFontCache : IDisposable
	{
		const int HoldoverCapacity = 256;
		internal static readonly ManagedScaledFontCache Shared = new ManagedScaledFontCache ();
		internal sealed class Entry
		{
			internal readonly ManagedScaledFontKey Key;
			internal readonly ManagedScaledFontResource Resource;
			internal int OwnedReferences;
			internal bool HasCacheReference;
			internal LinkedListNode<Entry> HoldoverNode;
			internal Entry (ManagedScaledFontKey key, ManagedScaledFontResource resource) { Key = key; Resource = resource; }
		}

		readonly object sync = new object ();
		readonly Dictionary<ManagedScaledFontKey, Entry> entries = new Dictionary<ManagedScaledFontKey, Entry> ();
		readonly LinkedList<Entry> holdovers = new LinkedList<Entry> ();
		Entry mru;
		bool disposed;

		internal int EntryCount { get { lock (sync) return entries.Count; } }
		internal int ActiveReferenceCount { get { lock (sync) { int count = 0; foreach (Entry entry in entries.Values) count += entry.OwnedReferences; return count; } } }
		internal int HoldoverCount { get { lock (sync) return holdovers.Count; } }

		internal Lease Acquire (ManagedFontFaceResource face, Matrix fontMatrix, Matrix ctm, FontOptions options)
		{
			var key = new ManagedScaledFontKey (face, fontMatrix, ctm, options);
			List<ManagedScaledFontResource> evicted = null;
			Lease lease;
			lock (sync) {
				if (disposed) throw new ObjectDisposedException (nameof (ManagedScaledFontCache));
				if (entries.TryGetValue (key, out Entry found)) {
					if (found.HoldoverNode != null) {
						holdovers.Remove (found.HoldoverNode);
						found.HoldoverNode = null;
					}
					checked { found.OwnedReferences++; }
					if (mru != found) {
						evicted = RetireMruLocked ();
						found.Resource.AddScaledReference ();
						found.HasCacheReference = true;
						mru = found;
					}
					found.Resource.AddScaledReference ();
					lease = new Lease (this, found);
				} else {
					// Construct and validate the key before taking a face reference. The key
					// above has already rejected non-finite matrices.
					var resource = new ManagedScaledFontResource (face, fontMatrix, ctm, options);
					var created = new Entry (key, resource) { OwnedReferences = 1 };
					created.Resource.AddScaledReference ();
					if (mru != null) evicted = RetireMruLocked (evicted);
					created.Resource.AddScaledReference ();
					created.HasCacheReference = true;
					mru = created;
					entries.Add (key, created);
					lease = new Lease (this, created);
				}
			}
			DisposeEvicted (evicted);
			return lease;
		}

		public void Dispose ()
		{
			ManagedScaledFontResource[] resources;
			lock (sync) {
				if (disposed) return;
				foreach (Entry entry in entries.Values)
					if (entry.OwnedReferences != 0)
						throw new InvalidOperationException ("Cannot dispose a Managed ScaledFont cache with active leases.");
				if (mru != null && mru.HasCacheReference) {
					mru.Resource.ReleaseScaledReference ();
					mru.HasCacheReference = false;
				}
				mru = null;
				resources = new ManagedScaledFontResource[entries.Count];
				int index = 0;
				foreach (Entry entry in entries.Values) resources[index++] = entry.Resource;
				entries.Clear ();
				holdovers.Clear ();
				disposed = true;
			}
			foreach (ManagedScaledFontResource resource in resources) resource.Dispose ();
		}

		List<ManagedScaledFontResource> RetireMruLocked (List<ManagedScaledFontResource> evicted = null)
		{
			Entry previous = mru;
			if (previous == null) return evicted;
			mru = null;
			if (previous.HasCacheReference) {
				previous.Resource.ReleaseScaledReference ();
				previous.HasCacheReference = false;
			}
			if (previous.OwnedReferences == 0)
				AddHoldoverLocked (previous, ref evicted);
			return evicted;
		}

		void AddHoldoverLocked (Entry entry, ref List<ManagedScaledFontResource> evicted)
		{
			if (entry.HoldoverNode != null || entry.OwnedReferences != 0 || entry.HasCacheReference)
				throw new InvalidOperationException ("Invalid holdover transition for a Managed ScaledFont resource.");
			entry.HoldoverNode = holdovers.AddLast (entry);
			if (holdovers.Count <= HoldoverCapacity) return;
			Entry oldest = holdovers.First.Value;
			holdovers.RemoveFirst ();
			oldest.HoldoverNode = null;
			entries.Remove (oldest.Key);
			if (evicted == null) evicted = new List<ManagedScaledFontResource> (1);
			evicted.Add (oldest.Resource);
		}

		void Release (Entry entry)
		{
			List<ManagedScaledFontResource> evicted = null;
			lock (sync) {
				if (entry.OwnedReferences <= 0) throw new InvalidOperationException ("Managed ScaledFont reference count underflowed.");
				entry.OwnedReferences--;
				entry.Resource.ReleaseScaledReference ();
				if (entry.OwnedReferences == 0 && entry != mru)
					AddHoldoverLocked (entry, ref evicted);
			}
			DisposeEvicted (evicted);
		}

		Lease Retain (Entry entry)
		{
			lock (sync) {
				if (disposed) throw new ObjectDisposedException (nameof (ManagedScaledFontCache));
			if (!entries.TryGetValue (entry.Key, out Entry current) || !ReferenceEquals (current, entry) || entry.OwnedReferences <= 0)
				throw new ObjectDisposedException (nameof (Lease), "The Managed ScaledFont lease has already been released or evicted.");
				checked { entry.OwnedReferences++; }
				entry.Resource.AddScaledReference ();
				return new Lease (this, entry);
			}
		}

		static void DisposeEvicted (List<ManagedScaledFontResource> evicted)
		{
			if (evicted == null) return;
			foreach (ManagedScaledFontResource resource in evicted) resource.Dispose ();
		}

		internal sealed class Lease : IDisposable
		{
			ManagedScaledFontCache owner;
			Entry entry;
			internal ManagedScaledFontResource Resource => entry?.Resource ?? throw new ObjectDisposedException (nameof (Lease));
			internal Lease (ManagedScaledFontCache owner, Entry entry) { this.owner = owner; this.entry = entry; }
			internal Lease Retain ()
			{
				Entry current = Volatile.Read (ref entry);
				ManagedScaledFontCache cache = Volatile.Read (ref owner);
				if (current == null || cache == null) throw new ObjectDisposedException (nameof (Lease));
				return cache.Retain (current);
			}
			public void Dispose ()
			{
				Entry current = Interlocked.Exchange (ref entry, null);
				if (current == null) return;
				Interlocked.Exchange (ref owner, null)?.Release (current);
			}
		}
	}
}
