using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Cairo;
using Xunit;

/// <summary>
/// The wrapper computes Cairo.Region in managed code. These tests pin that implementation to the system libcairo:
/// seeded random operation sequences run in lockstep on a native cairo_region_t and on Cairo.Region, and every
/// observable (extents, rectangle list, emptiness, containment, equality, statuses) must match. The coverage
/// model and lifecycle tests need no native library and run everywhere; the lockstep tests need libcairo.so.2
/// and are skipped when it is absent unless OPTIMUM_REQUIRE_NATIVE_CAIRO=1 turns that into a failure.
/// </summary>
public sealed class CairoRegionParityTests
{
	static class Native
	{
		const string Lib = "libcairo.so.2";

		[DllImport (Lib)] internal static extern IntPtr cairo_region_create ();
		[DllImport (Lib)] internal static extern IntPtr cairo_region_create_rectangle (ref RectangleInt rect);
		[DllImport (Lib)] internal static extern IntPtr cairo_region_create_rectangles ([In] RectangleInt[] rects, int count);
		[DllImport (Lib)] internal static extern IntPtr cairo_region_copy (IntPtr original);
		[DllImport (Lib)] internal static extern void cairo_region_destroy (IntPtr region);
		[DllImport (Lib)] internal static extern int cairo_region_equal (IntPtr a, IntPtr b);
		[DllImport (Lib)] internal static extern int cairo_region_status (IntPtr region);
		[DllImport (Lib)] internal static extern void cairo_region_get_extents (IntPtr region, out RectangleInt extents);
		[DllImport (Lib)] internal static extern int cairo_region_num_rectangles (IntPtr region);
		[DllImport (Lib)] internal static extern void cairo_region_get_rectangle (IntPtr region, int nth, out RectangleInt rectangle);
		[DllImport (Lib)] internal static extern int cairo_region_is_empty (IntPtr region);
		[DllImport (Lib)] internal static extern int cairo_region_contains_point (IntPtr region, int x, int y);
		[DllImport (Lib)] internal static extern int cairo_region_contains_rectangle (IntPtr region, ref RectangleInt rectangle);
		[DllImport (Lib)] internal static extern void cairo_region_translate (IntPtr region, int dx, int dy);
		[DllImport (Lib)] internal static extern int cairo_region_union (IntPtr dst, IntPtr other);
		[DllImport (Lib)] internal static extern int cairo_region_union_rectangle (IntPtr dst, ref RectangleInt rectangle);
		[DllImport (Lib)] internal static extern int cairo_region_subtract (IntPtr dst, IntPtr other);
		[DllImport (Lib)] internal static extern int cairo_region_subtract_rectangle (IntPtr dst, ref RectangleInt rectangle);
		[DllImport (Lib)] internal static extern int cairo_region_intersect (IntPtr dst, IntPtr other);
		[DllImport (Lib)] internal static extern int cairo_region_intersect_rectangle (IntPtr dst, ref RectangleInt rectangle);
		[DllImport (Lib)] internal static extern int cairo_region_xor (IntPtr dst, IntPtr other);
		[DllImport (Lib)] internal static extern int cairo_region_xor_rectangle (IntPtr dst, ref RectangleInt rectangle);
	}

	static readonly bool nativeAvailable = ProbeNative ();

	static bool ProbeNative ()
	{
		if (!OperatingSystem.IsLinux ())
			return false;
		if (NativeLibrary.TryLoad ("libcairo.so.2", out var handle)) {
			NativeLibrary.Free (handle);
			return true;
		}
		return false;
	}

	// False means the lockstep tests have nothing to compare against on this host.
	static bool RequireNative ()
	{
		if (nativeAvailable)
			return true;
		Assert.False (Environment.GetEnvironmentVariable ("OPTIMUM_REQUIRE_NATIVE_CAIRO") == "1",
			"libcairo.so.2 is required (OPTIMUM_REQUIRE_NATIVE_CAIRO=1) but cannot be loaded.");
		return false;
	}

	static RectangleInt Rect (int x, int y, int width, int height) => new RectangleInt { X = x, Y = y, Width = width, Height = height };

	static string Describe (RectangleInt r) => $"({r.X},{r.Y},{r.Width},{r.Height})";

	// One native region and one managed region driven through the same calls.
	sealed class Pair : IDisposable
	{
		internal IntPtr Native;
		internal Region Managed;

		internal Pair (IntPtr native, Region managed)
		{
			Native = native;
			Managed = managed;
		}

		internal static Pair Empty () => new Pair (CairoRegionParityTests.Native.cairo_region_create (), new Region ());

		internal static Pair FromRectangle (RectangleInt r) =>
			new Pair (CairoRegionParityTests.Native.cairo_region_create_rectangle (ref r), new Region (r));

		internal static Pair FromRectangles (RectangleInt[] rects) =>
			new Pair (CairoRegionParityTests.Native.cairo_region_create_rectangles (rects, rects.Length), new Region (rects));

		internal Pair Copy () => new Pair (CairoRegionParityTests.Native.cairo_region_copy (Native), Managed.Copy ());

		public void Dispose ()
		{
			if (Native != IntPtr.Zero)
				CairoRegionParityTests.Native.cairo_region_destroy (Native);
			Native = IntPtr.Zero;
			Managed.Dispose ();
		}
	}

	static void AssertSame (Pair pair, string context)
	{
		Native.cairo_region_get_extents (pair.Native, out var nativeExtents);
		var managedExtents = pair.Managed.Extents;
		Assert.True (nativeExtents.Equals (managedExtents), $"{context}: extents native {Describe (nativeExtents)} managed {Describe (managedExtents)}");

		int count = Native.cairo_region_num_rectangles (pair.Native);
		Assert.True (count == pair.Managed.NumRectangles, $"{context}: rectangle count native {count} managed {pair.Managed.NumRectangles}");
		for (int i = 0; i < count; i++) {
			Native.cairo_region_get_rectangle (pair.Native, i, out var nativeRect);
			var managedRect = pair.Managed.GetRectangle (i);
			Assert.True (nativeRect.Equals (managedRect), $"{context}: rectangle {i} native {Describe (nativeRect)} managed {Describe (managedRect)}");
		}

		Assert.True ((Native.cairo_region_is_empty (pair.Native) != 0) == pair.Managed.IsEmpty, $"{context}: IsEmpty");
		Assert.Equal ((Status) Native.cairo_region_status (pair.Native), pair.Managed.Status);
	}

	static void AssertQueriesSame (Pair pair, Random random, int range, string context)
	{
		for (int i = 0; i < 6; i++) {
			int x = random.Next (-range - 2, range + 3);
			int y = random.Next (-range - 2, range + 3);
			bool nativePoint = Native.cairo_region_contains_point (pair.Native, x, y) != 0;
			Assert.True (nativePoint == pair.Managed.ContainsPoint (x, y), $"{context}: ContainsPoint({x},{y}) native {nativePoint}");

			var probe = RandomRect (random, range, true);
			int nativeOverlap = Native.cairo_region_contains_rectangle (pair.Native, ref probe);
			var managedOverlap = pair.Managed.ContainsPoint (probe);
			Assert.True (nativeOverlap == (int) managedOverlap, $"{context}: contains {Describe (probe)} native {nativeOverlap} managed {(int) managedOverlap}");
		}
	}

	static RectangleInt RandomRect (Random random, int range, bool allowBad)
	{
		int x = random.Next (-range, range + 1);
		int y = random.Next (-range, range + 1);
		int width = random.Next (allowBad ? -2 : 0, range / 2 + 4);
		int height = random.Next (allowBad ? -2 : 0, range / 2 + 4);
		return Rect (x, y, width, height);
	}

	static Pair RandomPair (Random random, int range, StringBuilder log)
	{
		switch (random.Next (4)) {
		case 0:
			log.Append ("empty;");
			return Pair.Empty ();
		case 1: {
			var r = RandomRect (random, range, true);
			log.Append ("rect").Append (Describe (r)).Append (';');
			return Pair.FromRectangle (r);
		}
		default: {
			var rects = new RectangleInt[random.Next (0, 9)];
			for (int i = 0; i < rects.Length; i++)
				rects[i] = RandomRect (random, range, true);
			log.Append ("rects[");
			foreach (var r in rects) log.Append (Describe (r));
			log.Append ("];");
			return Pair.FromRectangles (rects);
		}
		}
	}

	static void ApplyRandomOperation (Pair target, Pair other, Random random, int range, StringBuilder log)
	{
		int kind = random.Next (13);
		var r = RandomRect (random, range, true);
		int nativeStatus;
		Status managedStatus;
		switch (kind) {
		case 0: nativeStatus = Native.cairo_region_union (target.Native, other.Native); managedStatus = target.Managed.Union (other.Managed); log.Append ("union;"); break;
		case 1: nativeStatus = Native.cairo_region_subtract (target.Native, other.Native); managedStatus = target.Managed.Subtract (other.Managed); log.Append ("subtract;"); break;
		case 2: nativeStatus = Native.cairo_region_intersect (target.Native, other.Native); managedStatus = target.Managed.Intersect (other.Managed); log.Append ("intersect;"); break;
		case 3: nativeStatus = Native.cairo_region_xor (target.Native, other.Native); managedStatus = target.Managed.Xor (other.Managed); log.Append ("xor;"); break;
		case 4: nativeStatus = Native.cairo_region_union_rectangle (target.Native, ref r); managedStatus = target.Managed.UnionRectangle (r); log.Append ("unionRect").Append (Describe (r)).Append (';'); break;
		case 5: nativeStatus = Native.cairo_region_subtract_rectangle (target.Native, ref r); managedStatus = target.Managed.SubtractRectangle (r); log.Append ("subtractRect").Append (Describe (r)).Append (';'); break;
		case 6: nativeStatus = Native.cairo_region_intersect_rectangle (target.Native, ref r); managedStatus = target.Managed.IntersectRectangle (r); log.Append ("intersectRect").Append (Describe (r)).Append (';'); break;
		case 7: nativeStatus = Native.cairo_region_xor_rectangle (target.Native, ref r); managedStatus = target.Managed.XorRectangle (r); log.Append ("xorRect").Append (Describe (r)).Append (';'); break;
		case 8: {
			int dx = random.Next (-range, range + 1), dy = random.Next (-range, range + 1);
			Native.cairo_region_translate (target.Native, dx, dy);
			target.Managed.Translate (dx, dy);
			log.Append ($"translate({dx},{dy});");
			return;
		}
		case 9: // operand aliasing: the destination as its own operand
			nativeStatus = Native.cairo_region_union (target.Native, target.Native); managedStatus = target.Managed.Union (target.Managed); log.Append ("unionSelf;"); break;
		case 10:
			nativeStatus = Native.cairo_region_subtract (target.Native, target.Native); managedStatus = target.Managed.Subtract (target.Managed); log.Append ("subtractSelf;"); break;
		case 11:
			nativeStatus = Native.cairo_region_xor (target.Native, target.Native); managedStatus = target.Managed.Xor (target.Managed); log.Append ("xorSelf;"); break;
		default:
			nativeStatus = Native.cairo_region_intersect (target.Native, target.Native); managedStatus = target.Managed.Intersect (target.Managed); log.Append ("intersectSelf;"); break;
		}
		Assert.Equal ((Status) nativeStatus, managedStatus);
	}

	[Fact]
	public void SeededOperationSequencesMatchSystemCairo ()
	{
		if (!RequireNative ())
			return;

		var random = new Random (0x5EED0001);
		int[] ranges = { 5, 9, 16, 40 };
		for (int scenario = 0; scenario < 6000; scenario++) {
			int range = ranges[scenario % ranges.Length];
			var log = new StringBuilder ();
			using var a = RandomPair (random, range, log);
			using var b = RandomPair (random, range, log);
			string context = $"scenario {scenario} range {range} setup {log}";
			AssertSame (a, context);
			AssertSame (b, context);
			AssertEqualsSame (a, b, context);

			int steps = random.Next (1, 8);
			for (int step = 0; step < steps; step++) {
				var stepLog = new StringBuilder (log.ToString ());
				ApplyRandomOperation (a, b, random, range, stepLog);
				context = $"scenario {scenario} range {range} ops {stepLog}";
				AssertSame (a, context);
				AssertSame (b, context);
				AssertQueriesSame (a, random, range, context);
				AssertEqualsSame (a, b, context);
				using var copy = a.Copy ();
				AssertSame (copy, context + " copy");
				AssertEqualsSame (a, copy, context + " copy");
			}
		}
	}

	static void AssertEqualsSame (Pair left, Pair right, string context)
	{
		bool nativeEqual = Native.cairo_region_equal (left.Native, right.Native) != 0;
		Assert.True (nativeEqual == left.Managed.Equals (right.Managed), $"{context}: Equals native {nativeEqual}");
		Assert.True (nativeEqual == right.Managed.Equals (left.Managed), $"{context}: reverse Equals native {nativeEqual}");
	}

	[Fact]
	public void EmptyResultsKeepTheSameExtentsOriginAsSystemCairo ()
	{
		if (!RequireNative ())
			return;

		// Empty regions are not all equal in libcairo: the origin of the last extents survives and Equals sees it.
		var rect = Rect (3, 4, 5, 6);
		var disjoint = Rect (30, 40, 2, 2);
		var steps = new Action<Pair>[] {
			p => { var r = disjoint; Native.cairo_region_intersect_rectangle (p.Native, ref r); p.Managed.IntersectRectangle (r); },
			p => { var r = rect; Native.cairo_region_subtract_rectangle (p.Native, ref r); p.Managed.SubtractRectangle (r); },
			p => { Native.cairo_region_translate (p.Native, 7, -3); p.Managed.Translate (7, -3); },
			p => { var r = Rect (0, 0, 0, 5); Native.cairo_region_union_rectangle (p.Native, ref r); p.Managed.UnionRectangle (r); },
			p => { Native.cairo_region_intersect (p.Native, p.Native); p.Managed.Intersect (p.Managed); },
			p => { Native.cairo_region_xor (p.Native, p.Native); p.Managed.Xor (p.Managed); },
		};
		for (int first = 0; first < steps.Length; first++)
		for (int second = 0; second < steps.Length; second++) {
			using var pair = Pair.FromRectangle (rect);
			using var reference = Pair.Empty ();
			steps[first] (pair);
			steps[second] (pair);
			AssertSame (pair, $"steps {first},{second}");
			AssertEqualsSame (pair, reference, $"steps {first},{second} against a fresh empty region");
		}
	}

	[Fact]
	public void DegenerateAndOverflowingRectanglesMatchSystemCairo ()
	{
		if (!RequireNative ())
			return;

		int[] coordinates = { int.MinValue, int.MinValue + 1, -1, 0, 1, 7, int.MaxValue - 1, int.MaxValue };
		int[] sizes = { int.MinValue, -1, 0, 1, 5, int.MaxValue };
		var random = new Random (0x5EED0002);
		var rects = new List<RectangleInt> ();
		foreach (int x in coordinates)
		foreach (int y in coordinates)
		foreach (int w in sizes)
		foreach (int h in sizes)
			rects.Add (Rect (x, y, w, h));

		foreach (var r in rects) {
			using var single = Pair.FromRectangle (r);
			AssertSame (single, "single " + Describe (r));
			using var base1 = Pair.FromRectangle (Rect (-3, -3, 9, 9));
			var probe = r;
			Assert.Equal ((Status) Native.cairo_region_union_rectangle (base1.Native, ref probe), base1.Managed.UnionRectangle (r));
			AssertSame (base1, "union with " + Describe (r));
		}

		for (int i = 0; i < 1500; i++) {
			var sample = new RectangleInt[random.Next (0, 7)];
			for (int j = 0; j < sample.Length; j++)
				sample[j] = rects[random.Next (rects.Count)];
			var context = new StringBuilder ("rects ");
			foreach (var r in sample) context.Append (Describe (r));
			using var pair = Pair.FromRectangles (sample);
			AssertSame (pair, context.ToString ());
		}
	}

	[Fact]
	public void SmallTranslationsOfBandedRegionsMatchSystemCairo ()
	{
		if (!RequireNative ())
			return;

		var random = new Random (0x5EED0003);
		for (int i = 0; i < 1500; i++) {
			var log = new StringBuilder ();
			using var pair = RandomPair (random, 20, log);
			int dx = random.Next (-30, 31), dy = random.Next (-30, 31);
			Native.cairo_region_translate (pair.Native, dx, dy);
			pair.Managed.Translate (dx, dy);
			AssertSame (pair, $"{log} translate({dx},{dy})");
		}
	}

	[Fact]
	public void LargeTranslationsInsideTheIntRangeMatchSystemCairo ()
	{
		if (!RequireNative ())
			return;

		int[] origins = { int.MinValue, int.MinValue + 10, -1000, 0, 1000, int.MaxValue - 40, int.MaxValue - 10 };
		int[] shifts = { int.MinValue, -2000000000, -50, -1, 1, 50, 2000000000, int.MaxValue };
		var random = new Random (0x5EED0006);
		int compared = 0;
		for (int i = 0; i < 6000; i++) {
			var rects = new RectangleInt[random.Next (1, 5)];
			for (int j = 0; j < rects.Length; j++)
				rects[j] = Rect (origins[random.Next (origins.Length)], origins[random.Next (origins.Length)], random.Next (1, 30), random.Next (1, 30));
			int dx = shifts[random.Next (shifts.Length)], dy = shifts[random.Next (shifts.Length)];
			if (!StaysInsideIntRange (rects, dx, dy))
				continue; // libcairo wraps 32-bit sums here and returns arbitrary extents; the managed region clips instead.
			compared++;
			using var pair = Pair.FromRectangles (rects);
			var context = new StringBuilder ("rects ");
			foreach (var r in rects) context.Append (Describe (r));
			context.Append ($" translate({dx},{dy})");
			Native.cairo_region_translate (pair.Native, dx, dy);
			pair.Managed.Translate (dx, dy);
			AssertSame (pair, context.ToString ());
		}
		Assert.True (compared > 1000, $"only {compared} translations stayed inside the int range");
	}

	static bool StaysInsideIntRange (RectangleInt[] rects, int dx, int dy)
	{
		foreach (var r in rects) {
			long x1 = (long) r.X + dx, y1 = (long) r.Y + dy, x2 = (long) r.X + r.Width + dx, y2 = (long) r.Y + r.Height + dy;
			if (x1 < int.MinValue || y1 < int.MinValue || x2 > int.MaxValue || y2 > int.MaxValue)
				return false;
		}
		return true;
	}

	[Fact]
	public void OutOfRangeRectangleIndexReturnsAnEmptyRectangle ()
	{
		using var region = new Region (Rect (1, 2, 3, 4));
		Assert.Equal (default (RectangleInt), region.GetRectangle (-1));
		Assert.Equal (default (RectangleInt), region.GetRectangle (1));
		Assert.Equal (Rect (1, 2, 3, 4), region.GetRectangle (0));
	}

	// ---------------------------------------------------------------- no native library needed

	const int Grid = 24;

	static bool[,] Cover (Region region)
	{
		var cells = new bool[Grid, Grid];
		for (int y = 0; y < Grid; y++)
		for (int x = 0; x < Grid; x++)
			cells[x, y] = region.ContainsPoint (x, y);
		return cells;
	}

	static void Paint (bool[,] cells, RectangleInt r, Func<bool, bool> combine)
	{
		for (int y = 0; y < Grid; y++)
		for (int x = 0; x < Grid; x++) {
			bool inside = r.Width > 0 && r.Height > 0 && x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
			if (inside) cells[x, y] = combine (cells[x, y]);
		}
	}

	static void AssertCanonical (Region region, bool[,] model, string context)
	{
		int area = 0;
		for (int y = 0; y < Grid; y++)
		for (int x = 0; x < Grid; x++) {
			Assert.True (model[x, y] == region.ContainsPoint (x, y), $"{context}: coverage of ({x},{y})");
			if (model[x, y]) area++;
		}

		int count = region.NumRectangles;
		Assert.Equal (area == 0, region.IsEmpty);
		Assert.Equal (count == 0, region.IsEmpty);

		var rects = new RectangleInt[count];
		int covered = 0;
		for (int i = 0; i < count; i++) {
			rects[i] = region.GetRectangle (i);
			Assert.True (rects[i].Width > 0 && rects[i].Height > 0, $"{context}: rectangle {i} is empty");
			covered += rects[i].Width * rects[i].Height;
		}
		Assert.Equal (area, covered);

		for (int i = 1; i < count; i++) {
			var previous = rects[i - 1];
			var current = rects[i];
			Assert.True (previous.Y < current.Y || (previous.Y == current.Y && previous.X + previous.Width < current.X), $"{context}: rectangles {i - 1},{i} not in y-x order with gaps inside a band");
			if (previous.Y == current.Y)
				Assert.Equal (previous.Height, current.Height);
			else
				Assert.True (previous.Y + previous.Height <= current.Y, $"{context}: bands overlap");
		}

		// Touching bands must differ, otherwise libcairo would have merged them.
		var bands = new List<List<RectangleInt>> ();
		foreach (var r in rects) {
			if (bands.Count == 0 || bands[bands.Count - 1][0].Y != r.Y)
				bands.Add (new List<RectangleInt> ());
			bands[bands.Count - 1].Add (r);
		}
		for (int b = 1; b < bands.Count; b++) {
			var previous = bands[b - 1];
			var current = bands[b];
			if (previous[0].Y + previous[0].Height != current[0].Y || previous.Count != current.Count)
				continue;
			bool identical = true;
			for (int i = 0; i < current.Count; i++)
				identical &= previous[i].X == current[i].X && previous[i].Width == current[i].Width;
			Assert.False (identical, $"{context}: touching bands {b - 1},{b} were not coalesced");
		}

		if (count > 0) {
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			foreach (var r in rects) {
				minX = Math.Min (minX, r.X);
				minY = Math.Min (minY, r.Y);
				maxX = Math.Max (maxX, r.X + r.Width);
				maxY = Math.Max (maxY, r.Y + r.Height);
			}
			Assert.Equal (Rect (minX, minY, maxX - minX, maxY - minY), region.Extents);
		}
	}

	[Fact]
	public void ManagedRegionAlgebraMatchesACoverageModel ()
	{
		var random = new Random (0x5EED0004);
		for (int scenario = 0; scenario < 2500; scenario++) {
			var model = new bool[Grid, Grid];
			using var region = new Region ();
			var log = new StringBuilder ();
			int steps = random.Next (1, 10);
			for (int step = 0; step < steps; step++) {
				var r = RandomRect (random, 6, false);
				r.X += 6;
				r.Y += 6;
				switch (random.Next (5)) {
				case 0: region.UnionRectangle (r); Paint (model, r, _ => true); log.Append ("U"); break;
				case 1: region.SubtractRectangle (r); Paint (model, r, _ => false); log.Append ("S"); break;
				case 2: {
					region.IntersectRectangle (r);
					var keep = new bool[Grid, Grid];
					Paint (keep, r, _ => true);
					for (int y = 0; y < Grid; y++)
					for (int x = 0; x < Grid; x++)
						model[x, y] &= keep[x, y];
					log.Append ("I");
					break;
				}
				case 3: region.XorRectangle (r); Paint (model, r, v => !v); log.Append ("X"); break;
				default: {
					var second = RandomRect (random, 6, false);
					second.X += 6;
					second.Y += 6;
					using var other = new Region (new[] { r, second });
					var otherCover = Cover (other);
					region.Union (other);
					for (int y = 0; y < Grid; y++)
					for (int x = 0; x < Grid; x++)
						model[x, y] |= otherCover[x, y];
					log.Append ("R");
					break;
				}
				}
				log.Append (Describe (r)).Append (';');
				AssertCanonical (region, model, $"scenario {scenario} {log}");
			}
		}
	}

	[Fact]
	public void ManagedRectangleListsCollapseOverlapsLikeAUnion ()
	{
		var random = new Random (0x5EED0005);
		for (int scenario = 0; scenario < 1500; scenario++) {
			var rects = new RectangleInt[random.Next (0, 12)];
			var model = new bool[Grid, Grid];
			for (int i = 0; i < rects.Length; i++) {
				rects[i] = RandomRect (random, 6, true);
				rects[i].X += 6;
				rects[i].Y += 6;
				Paint (model, rects[i], _ => true);
			}
			using var region = new Region (rects);
			AssertCanonical (region, model, $"scenario {scenario}");
		}
	}

	[Fact]
	public void RegionHandlesAreOpaqueAndReferenceCounted ()
	{
		var region = new Region (Rect (0, 0, 4, 4));
		IntPtr handle = region.Handle;
		Assert.NotEqual (IntPtr.Zero, handle);
		Assert.Equal (1, RegionHandles.ReferenceCountOf (handle));

		using (var alias = new Region (handle, false)) {
			Assert.Equal (handle, alias.Handle);
			Assert.Equal (2, RegionHandles.ReferenceCountOf (handle));
			Assert.True (alias.Equals (region));
			region.Translate (10, 0);
			Assert.Equal (Rect (10, 0, 4, 4), alias.Extents);
		}
		Assert.Equal (1, RegionHandles.ReferenceCountOf (handle));

		region.Dispose ();
		Assert.Equal (0, RegionHandles.ReferenceCountOf (handle));
		Assert.Equal (IntPtr.Zero, region.Handle);

		// An owned wrapper takes over the reference the handle was created with.
		IntPtr issued = RegionHandles.Create (new RegionGeometry ());
		Assert.Equal (1, RegionHandles.ReferenceCountOf (issued));
		using (var owner = new Region (issued, true)) {
			Assert.Equal (1, RegionHandles.ReferenceCountOf (issued));
			Assert.True (owner.IsEmpty);
		}
		Assert.Equal (0, RegionHandles.ReferenceCountOf (issued));
		Assert.Throws<ArgumentException> (() => new Region (handle, true));
		Assert.Throws<ArgumentException> (() => new Region (new IntPtr (0x1234), true));
		Assert.Throws<ArgumentException> (() => new Region (IntPtr.Zero, true));
	}

	[Fact]
	public void DisposedRegionsThrowAndCompareLikeNullHandles ()
	{
		var live = new Region (Rect (0, 0, 2, 2));
		var gone = new Region (Rect (0, 0, 2, 2));
		var alsoGone = new Region ();
		gone.Dispose ();
		alsoGone.Dispose ();
		gone.Dispose ();

		Assert.Throws<ObjectDisposedException> (() => gone.Extents);
		Assert.Throws<ObjectDisposedException> (() => gone.Status);
		Assert.Throws<ObjectDisposedException> (() => gone.Copy ());
		Assert.Throws<ObjectDisposedException> (() => live.Union (gone));
		Assert.Throws<ObjectDisposedException> (() => gone.Union (live));
		Assert.False (live.Equals (gone));
		Assert.False (gone.Equals (live));
		Assert.True (gone.Equals (alsoGone));
		Assert.False (live.Equals (null));
		Assert.False (live.Equals ("region"));
		live.Dispose ();
	}

	[Fact]
	public void CopiesAreIndependentAndSeparatelyOwned ()
	{
		using var original = new Region (new[] { Rect (0, 0, 3, 3), Rect (10, 10, 2, 2) });
		using var copy = original.Copy ();
		Assert.NotEqual (original.Handle, copy.Handle);
		Assert.True (original.Equals (copy));
		copy.UnionRectangle (Rect (4, 4, 2, 2));
		Assert.False (original.Equals (copy));
		Assert.Equal (2, original.NumRectangles);
		Assert.Equal (3, copy.NumRectangles);
	}
}
