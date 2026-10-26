using System;
using System.Collections.Generic;

namespace Cairo
{
	/// <summary>Shared managed identity and ownership count for wrappers around one Cairo font face.</summary>
	internal sealed class ManagedFontFaceResource
	{
		static readonly object CacheSync = new object ();
		static readonly Dictionary<string, WeakReference<ManagedFontFaceResource>> ToyCache = new Dictionary<string, WeakReference<ManagedFontFaceResource>> (StringComparer.Ordinal);
		const int ToyCacheCleanupInterval = 64;
		static int toyCacheMissesSinceCleanup;
		int referenceCount;

		internal readonly string Family;
		internal readonly FontSlant Slant;
		internal readonly FontWeight Weight;
		internal readonly ManagedFileFontSource FileSource;
		internal uint ReferenceCount { get { lock (this) return checked ((uint)referenceCount); } }

		ManagedFontFaceResource (string family, FontSlant slant, FontWeight weight, ManagedFileFontSource fileSource)
		{
			Family = family;
			Slant = slant;
			Weight = weight;
			FileSource = fileSource;
			referenceCount = 1;
		}

		internal static ManagedFontFaceResource AcquireToy (string family, FontSlant slant, FontWeight weight)
		{
			family = FontFace.NormalizeFamily (family);
			string key = ((int)slant).ToString (System.Globalization.CultureInfo.InvariantCulture) + ":" +
				((int)weight).ToString (System.Globalization.CultureInfo.InvariantCulture) + ":" + family;
			lock (CacheSync) {
				if (ToyCache.TryGetValue (key, out WeakReference<ManagedFontFaceResource> weak) && weak.TryGetTarget (out ManagedFontFaceResource cached)) {
					cached.AddReference ();
					return cached;
				}

				if (++toyCacheMissesSinceCleanup >= ToyCacheCleanupInterval) {
					SweepDeadToyEntries ();
					toyCacheMissesSinceCleanup = 0;
				}

				var created = new ManagedFontFaceResource (family, slant, weight, null);
				ToyCache[key] = new WeakReference<ManagedFontFaceResource> (created);
				return created;
			}
		}

		// Called only while CacheSync is held, and never from a resource lock. A
		// sweep reads weak targets without taking their resource locks, preserving
		// the CacheSync -> resource lock order used by AcquireToy.
		static void SweepDeadToyEntries ()
		{
			List<string> deadKeys = null;
			foreach (KeyValuePair<string, WeakReference<ManagedFontFaceResource>> entry in ToyCache) {
				if (entry.Value.TryGetTarget (out _)) continue;
				if (deadKeys == null) deadKeys = new List<string> ();
				deadKeys.Add (entry.Key);
			}
			if (deadKeys == null) return;
			foreach (string deadKey in deadKeys) ToyCache.Remove (deadKey);
		}

		internal static ManagedFontFaceResource CreateFile (ManagedFileFontSource source)
		{
			if (source == null) throw new ArgumentNullException (nameof (source));
			return new ManagedFontFaceResource (source.Family, source.Slant, source.Weight, source);
		}

		internal void AddReference ()
		{
			lock (this) {
				if (referenceCount == int.MaxValue) throw new InvalidOperationException ("Managed FontFace reference count overflowed.");
				referenceCount++;
			}
		}

		internal void Release ()
		{
			lock (this) {
				if (referenceCount <= 0) throw new InvalidOperationException ("Managed FontFace reference count underflowed.");
				referenceCount--;
			}
		}
	}
}
