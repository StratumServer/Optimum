using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoScaledFontCacheTests
{
	static Matrix FontMatrix (double size) => new Matrix (size, 0, 0, size, 0, 0);
	static Matrix Identity () => new Matrix (1, 0, 0, 1, 0, 0);

	[Fact]
	public void KeyUsesFaceIdentityMatrixBitsAndCopiedOptionsButIgnoresCtmTranslation ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Keys 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			using var cache = new ManagedScaledFontCache ();
			Matrix font = FontMatrix (10);
			Matrix ctm = new Matrix (1, 0, 0, 1, 17, -31);
			using var first = cache.Acquire (face.ManagedResource, font, ctm, options);
			Matrix canonicalCtm = first.Resource.Ctm;
			Assert.Equal (0d, canonicalCtm.X0);
			Assert.Equal (0d, canonicalCtm.Y0);
			uint afterFirst = face.ReferenceCount;
			Assert.Equal (2u, afterFirst);

			Matrix translatedCtm = Identity ();
			using var translationLookup = cache.Acquire (face.ManagedResource, font, translatedCtm, options);
			Assert.Same (first.Resource, translationLookup.Resource);
			Assert.Equal (afterFirst, face.ReferenceCount);
			using (var scaledCtm = cache.Acquire (face.ManagedResource, font, new Matrix (2, 0, 0, 2, 0, 0), options))
				Assert.NotSame (first.Resource, scaledCtm.Resource);
			using (var shearedCtm = cache.Acquire (face.ManagedResource, font, new Matrix (1, 0, .3, 1, 0, 0), options))
				Assert.NotSame (first.Resource, shearedCtm.Resource);
			using (var translatedFont = cache.Acquire (face.ManagedResource, new Matrix (10, 0, 0, 10, 17, 31), ctm, options))
				Assert.NotSame (first.Resource, translatedFont.Resource);
			using (var scaledFont = cache.Acquire (face.ManagedResource, FontMatrix (12), ctm, options))
				Assert.NotSame (first.Resource, scaledFont.Resource);
			using (var shearedFont = cache.Acquire (face.ManagedResource, new Matrix (10, 0, 3, 10, 0, 0), ctm, options))
				Assert.NotSame (first.Resource, shearedFont.Resource);
			using (var zeroFont = cache.Acquire (face.ManagedResource, new Matrix (0, 0, 0, 0, 0, 0), ctm, options))
				Assert.NotSame (first.Resource, zeroFont.Resource);
			using (var nanFontTranslation = cache.Acquire (face.ManagedResource, new Matrix (10, 0, 0, 10, double.NaN, 0), ctm, options))
				Assert.NotSame (first.Resource, nanFontTranslation.Resource);
			using (var infiniteFontTranslation = cache.Acquire (face.ManagedResource, new Matrix (10, 0, 0, 10, double.PositiveInfinity, 0), ctm, options))
				Assert.NotSame (first.Resource, infiniteFontTranslation.Resource);
			using (var nanCtmTranslation = cache.Acquire (face.ManagedResource, font, new Matrix (1, 0, 0, 1, double.NaN, 0), options))
				Assert.Same (first.Resource, nanCtmTranslation.Resource);
			using (var infiniteCtmTranslation = cache.Acquire (face.ManagedResource, font, new Matrix (1, 0, 0, 1, 0, double.PositiveInfinity), options))
				Assert.Same (first.Resource, infiniteCtmTranslation.Resource);

			Matrix signedZeroFont = new Matrix (10, -0d, -0d, 10, -0d, -0d);
			using var signedZero = cache.Acquire (face.ManagedResource, signedZeroFont, ctm, options);
			Assert.NotSame (first.Resource, signedZero.Resource);

			Matrix signedZeroCtm = new Matrix (1, -0d, -0d, 1, 0, 0);
			using var signedZeroTransform = cache.Acquire (face.ManagedResource, font, signedZeroCtm, options);
			Assert.NotSame (first.Resource, signedZeroTransform.Resource);

			options.HintStyle = HintStyle.Full;
			using var differentOptions = cache.Acquire (face.ManagedResource, font, ctm, options);
			Assert.NotSame (first.Resource, differentOptions.Resource);
			options.HintStyle = HintStyle.Default;
			using var copiedOptions = cache.Acquire (face.ManagedResource, font, ctm, options);
			Assert.Same (first.Resource, copiedOptions.Resource);
			for (int value = 1; value <= 6; value++) {
				using var variant = new FontOptions { Antialias = (Antialias)value };
				using var lease = cache.Acquire (face.ManagedResource, font, ctm, variant);
				Assert.NotSame (first.Resource, lease.Resource);
			}
			for (int value = 1; value <= 4; value++) {
				using var variant = new FontOptions { HintStyle = (HintStyle)value };
				using var lease = cache.Acquire (face.ManagedResource, font, ctm, variant);
				Assert.NotSame (first.Resource, lease.Resource);
			}
			for (int value = 1; value <= 2; value++) {
				using var variant = new FontOptions { HintMetrics = (HintMetrics)value };
				using var lease = cache.Acquire (face.ManagedResource, font, ctm, variant);
				Assert.NotSame (first.Resource, lease.Resource);
			}
			for (int value = 1; value <= 4; value++) {
				using var variant = new FontOptions { SubpixelOrder = (SubpixelOrder)value };
				using var lease = cache.Acquire (face.ManagedResource, font, ctm, variant);
				Assert.NotSame (first.Resource, lease.Resource);
			}

			using var anotherFace = new FontFace ("Optimum Scaled Cache Keys Separate 20261026", FontSlant.Normal, FontWeight.Normal);
			using var differentFace = cache.Acquire (anotherFace.ManagedResource, font, ctm, options);
			Assert.NotSame (first.Resource, differentFace.Resource);

			int entries = cache.EntryCount;
			uint faceCount = face.ReferenceCount;
			Matrix invalid = FontMatrix (10);
			invalid.Xx = double.NaN;
			Assert.Throws<ArgumentException> (() => cache.Acquire (face.ManagedResource, invalid, ctm, options));
			Matrix overflowDeterminant = new Matrix (1e308, 0, 0, 2, 0, 0);
			Assert.Throws<ArgumentException> (() => cache.Acquire (face.ManagedResource, overflowDeterminant, ctm, options));
			Assert.Throws<ArgumentException> (() => cache.Acquire (face.ManagedResource, font, overflowDeterminant, options));
			Assert.Throws<ArgumentException> (() => cache.Acquire (face.ManagedResource, new Matrix (1, 0, 0, 0, 0, 0), ctm, options));
			using (var degenerateZero = cache.Acquire (face.ManagedResource, new Matrix (0, 0, 0, 0, 0, 0), ctm, options))
				Assert.NotNull (degenerateZero.Resource);
			Assert.Equal (entries, cache.EntryCount);
			Assert.Equal (faceCount, face.ReferenceCount);
		});
	}

	[Fact]
	public void UnusedEntriesPlateauAtOneMruAnd256HoldoversAndEvictOldest ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Churn 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			using var cache = new ManagedScaledFontCache ();
			Matrix ctm = Identity ();
			for (int i = 0; i < 1400; i++) {
				using (cache.Acquire (face.ManagedResource, FontMatrix (10 + i / 16d), ctm, options)) { }
				if (i < 256) Assert.Equal ((uint)(i + 2), face.ReferenceCount);
				else Assert.Equal (258u, face.ReferenceCount);
			}
			Assert.Equal (257, cache.EntryCount);
			Assert.Equal (256, cache.HoldoverCount);
		});
	}

	[Fact]
	public void HotAndActivelyOwnedEntriesSurvivePressureWhileColdEntryIsEvicted ()
	{
		RunBoth (() => {
			using var options = new FontOptions ();
			Matrix ctm = Identity ();
			using var coldCache = new ManagedScaledFontCache ();
			using var coldFace = new FontFace ("Optimum Scaled Cache Cold 20261026", FontSlant.Normal, FontWeight.Normal);
			using var pressureFace = new FontFace ("Optimum Scaled Cache Cold Pressure 20261026", FontSlant.Normal, FontWeight.Normal);
			using (coldCache.Acquire (coldFace.ManagedResource, FontMatrix (10), ctm, options)) { }
			for (int i = 0; i < 256; i++)
				using (coldCache.Acquire (pressureFace.ManagedResource, FontMatrix (20 + i / 16d), ctm, options)) { }
			Assert.Equal (2u, coldFace.ReferenceCount);
			using (coldCache.Acquire (pressureFace.ManagedResource, FontMatrix (40), ctm, options)) { }
			Assert.Equal (1u, coldFace.ReferenceCount);

			using var hotCache = new ManagedScaledFontCache ();
			using var hotFace = new FontFace ("Optimum Scaled Cache Hot 20261026", FontSlant.Normal, FontWeight.Normal);
			using var hotPressureFace = new FontFace ("Optimum Scaled Cache Hot Pressure 20261026", FontSlant.Normal, FontWeight.Normal);
			using (hotCache.Acquire (hotFace.ManagedResource, FontMatrix (10), ctm, options)) { }
			for (int i = 0; i < 1400; i++) {
				if (i % 16 == 0)
					using (hotCache.Acquire (hotFace.ManagedResource, FontMatrix (10), ctm, options)) { }
				using (hotCache.Acquire (hotPressureFace.ManagedResource, FontMatrix (20 + i / 16d), ctm, options)) { }
			}
			Assert.Equal (2u, hotFace.ReferenceCount);

			using var activeCache = new ManagedScaledFontCache ();
			using var activeFace = new FontFace ("Optimum Scaled Cache Active 20261026", FontSlant.Normal, FontWeight.Normal);
			using var activePressureFace = new FontFace ("Optimum Scaled Cache Active Pressure 20261026", FontSlant.Normal, FontWeight.Normal);
			var active = activeCache.Acquire (activeFace.ManagedResource, FontMatrix (10), ctm, options);
			for (int i = 0; i < 1400; i++)
				using (activeCache.Acquire (activePressureFace.ManagedResource, FontMatrix (20 + i / 16d), ctm, options)) { }
			Assert.Equal (2u, activeFace.ReferenceCount);
			active.Dispose ();
			Assert.Equal (2u, activeFace.ReferenceCount);
		});
	}

	[Fact]
	public void ActiveResourcesDoNotCountAgainstHoldoverCapacity ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Active Capacity 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			using var cache = new ManagedScaledFontCache ();
			var leases = new ManagedScaledFontCache.Lease[300];
			for (int i = 0; i < leases.Length; i++) leases[i] = cache.Acquire (face.ManagedResource, FontMatrix (8 + i), Identity (), options);
			Assert.Equal (301u, face.ReferenceCount);
			Assert.Equal (300, cache.EntryCount);
			Assert.Equal (0, cache.HoldoverCount);
			foreach (ManagedScaledFontCache.Lease lease in leases) lease.Dispose ();
			Assert.Equal (257, cache.EntryCount);
			Assert.Equal (256, cache.HoldoverCount);
			Assert.Equal (258u, face.ReferenceCount);
		});
	}

	[Fact]
	public void ConcurrentSameKeyAcquisitionSharesOneResourceWithoutUnderflow ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Concurrent 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			using var cache = new ManagedScaledFontCache ();
			var resources = new ConcurrentBag<ManagedScaledFontResource> ();
			Parallel.For (0, 8, _ => {
				for (int i = 0; i < 256; i++) {
					using var lease = cache.Acquire (face.ManagedResource, FontMatrix (13), Identity (), options);
					resources.Add (lease.Resource);
				}
			});
			ManagedScaledFontResource first = resources.First ();
			Assert.All (resources, resource => Assert.Same (first, resource));
			Assert.Equal (2u, face.ReferenceCount);
			Assert.Equal (1, first.ScaledReferenceCount);
		});
	}

	[Fact]
	public void ConcurrentDisposalReleasesALeaseExactlyOnce ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Concurrent Dispose 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			using var cache = new ManagedScaledFontCache ();
			var lease = cache.Acquire (face.ManagedResource, FontMatrix (14), Identity (), options);
			ManagedScaledFontResource resource = lease.Resource;
			Parallel.For (0, 64, _ => lease.Dispose ());
			Assert.Equal (2u, face.ReferenceCount);
			Assert.Equal (1, resource.ScaledReferenceCount);
		});
	}

	[Fact]
	public void ConcurrentRetainAndDisposeNeverRevivesReleasedLeaseOrLeaksOwners ()
	{
		using var face = new FontFace ("Optimum Scaled Cache Retain Dispose 20261026", FontSlant.Normal, FontWeight.Normal);
		using var options = new FontOptions ();
		using var cache = new ManagedScaledFontCache ();
		for (int iteration = 0; iteration < 64; iteration++) {
			ManagedScaledFontCache.Lease source = cache.Acquire (face.ManagedResource, FontMatrix (10 + iteration), Identity (), options);
			var retained = new ConcurrentBag<ManagedScaledFontCache.Lease> ();
			Parallel.Invoke (
				() => Parallel.For (0, 16, _ => {
					try { retained.Add (source.Retain ()); }
					catch (ObjectDisposedException) { }
				}),
				() => source.Dispose ());
			Parallel.ForEach (retained, lease => lease.Dispose ());
			Assert.Equal (0, cache.ActiveReferenceCount);
		}
		Assert.Equal (64, cache.EntryCount);
	}

	[Fact]
	public void CacheDisposalRejectsActiveLeasesThenReleasesAllHoldovers ()
	{
		RunBoth (() => {
			using var face = new FontFace ("Optimum Scaled Cache Dispose 20261026", FontSlant.Normal, FontWeight.Normal);
			using var options = new FontOptions ();
			var cache = new ManagedScaledFontCache ();
			var lease = cache.Acquire (face.ManagedResource, FontMatrix (15), Identity (), options);
			Assert.Throws<InvalidOperationException> (() => cache.Dispose ());
			Assert.Equal (1, cache.EntryCount);
			Assert.Equal (2, lease.Resource.ScaledReferenceCount);
			lease.Dispose ();
			cache.Dispose ();
			Assert.Equal (1u, face.ReferenceCount);
			Assert.Equal (0, cache.EntryCount);
			cache.Dispose ();
			Assert.Throws<ObjectDisposedException> (() => cache.Acquire (face.ManagedResource, FontMatrix (15), Identity (), options));
		});
	}

	[Fact]
	public void PublicScaledFontWrappersShareOneFaceOwnerAndOnlyChangeScaledReferences ()
	{
		using var face = new FontFace ("Optimum Scaled Wrapper Owners 20261026", FontSlant.Normal, FontWeight.Normal);
		using var options = new FontOptions ();
		Matrix font = FontMatrix (11);
		Matrix ctm = Identity ();
		uint initial = face.ManagedResource.ReferenceCount;
		var first = new ScaledFont (face, font, ctm, options);
		ManagedScaledFontResource resource = first.ManagedScaledResource;
		Assert.Equal (initial + 1, face.ManagedResource.ReferenceCount);
		var second = new ScaledFont (face, font, ctm, options);
		Assert.Same (resource, second.ManagedScaledResource);
		Assert.Equal (initial + 1, face.ManagedResource.ReferenceCount);
		Assert.Equal (3, resource.ScaledReferenceCount);
		first.Dispose ();
		Assert.Equal (2, resource.ScaledReferenceCount);
		Assert.Equal (initial + 1, face.ManagedResource.ReferenceCount);
		second.Dispose ();
		Assert.Equal (1, resource.ScaledReferenceCount);
		Assert.Equal (initial + 1, face.ManagedResource.ReferenceCount);
	}

	[Fact]
	public void ContextScaledOwnersFollowLazyMatrixChangesSaveRestoreAndCrossContextReuse ()
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		Surface.ConfigureAutomaticRecording (true);
		try {
			using var face = new FontFace ("Optimum Scaled Context Owners 20261026", FontSlant.Normal, FontWeight.Normal);
			uint baseReferences = face.ManagedResource.ReferenceCount;
			using var surface = new ImageSurface (Format.Argb32, 32, 24);
			var context = new Context (surface);
			context.SetContextFontFace (face);
			Assert.Equal (baseReferences + 1, face.ManagedResource.ReferenceCount);
			var size10 = context.GetScaledFont ();
			ManagedScaledFontResource size10Resource = size10.ManagedScaledResource;
			Assert.Equal (baseReferences + 2, face.ManagedResource.ReferenceCount);
			var repeated10 = context.GetScaledFont ();
			Assert.Same (size10Resource, repeated10.ManagedScaledResource);
			Assert.Equal (baseReferences + 2, face.ManagedResource.ReferenceCount);

			context.FontMatrix = FontMatrix (12);
			Assert.Equal (baseReferences + 2, face.ManagedResource.ReferenceCount);
			var size12 = context.GetScaledFont ();
			ManagedScaledFontResource size12Resource = size12.ManagedScaledResource;
			Assert.NotSame (size10Resource, size12Resource);
			Assert.Equal (baseReferences + 3, face.ManagedResource.ReferenceCount);
			Assert.Equal (2, size10Resource.ScaledReferenceCount);

			context.Save ();
			Assert.Equal (baseReferences + 4, face.ManagedResource.ReferenceCount);
			context.FontMatrix = FontMatrix (16);
			var size16 = context.GetScaledFont ();
			Assert.NotSame (size12Resource, size16.ManagedScaledResource);
			Assert.Equal (baseReferences + 5, face.ManagedResource.ReferenceCount);
			context.Restore ();
			var restored12 = context.GetScaledFont ();
			Assert.Same (size12Resource, restored12.ManagedScaledResource);
			Assert.Equal (baseReferences + 4, face.ManagedResource.ReferenceCount);

			using var secondSurface = new ImageSurface (Format.Argb32, 32, 24);
			var secondContext = new Context (secondSurface);
			secondContext.SetContextFontFace (face);
			secondContext.FontMatrix = FontMatrix (12);
			Assert.Equal (baseReferences + 5, face.ManagedResource.ReferenceCount);
			var crossContext12 = secondContext.GetScaledFont ();
			Assert.Same (size12Resource, crossContext12.ManagedScaledResource);
			Assert.Equal (baseReferences + 5, face.ManagedResource.ReferenceCount);
			using var thirdSurface = new ImageSurface (Format.Argb32, 32, 24);
			var thirdContext = new Context (thirdSurface);
			thirdContext.SetContextFontFace (face);
			thirdContext.FontMatrix = FontMatrix (20);
			var distinctMru = thirdContext.GetScaledFont ();
			ManagedScaledFontResource mruResource = distinctMru.ManagedScaledResource;
			using (var explicitDefaultOptions = new FontOptions ())
			using (var explicitDefault = new ScaledFont (face, FontMatrix (12), new Matrix (1, 0, 0, 1, 0, 0), explicitDefaultOptions)) {
				Assert.NotSame (size12Resource, explicitDefault.ManagedScaledResource);
				Assert.Equal (HintMetrics.Default, explicitDefault.GetManagedFontOptions ().HintMetrics);
			}
			using (var publicOptions = context.FontOptions)
				Assert.Equal (HintMetrics.Default, publicOptions.HintMetrics);
			int size12BeforeRetain = size12Resource.ScaledReferenceCount;
			int mruBeforeRetain = mruResource.ScaledReferenceCount;
			var contextRetain = context.GetScaledFont ();
			Assert.Same (size12Resource, contextRetain.ManagedScaledResource);
			Assert.Equal (size12BeforeRetain + 1, size12Resource.ScaledReferenceCount);
			Assert.Equal (HintMetrics.On, contextRetain.GetManagedFontOptions ().HintMetrics);
			contextRetain.Dispose ();
			Assert.Equal (size12BeforeRetain, size12Resource.ScaledReferenceCount);
			Assert.Equal (mruBeforeRetain, mruResource.ScaledReferenceCount);

			size10.Dispose ();
			repeated10.Dispose ();
			size12.Dispose ();
			size16.Dispose ();
			restored12.Dispose ();
			crossContext12.Dispose ();
			distinctMru.Dispose ();
			context.Dispose ();
			secondContext.Dispose ();
			thirdContext.Dispose ();
			Assert.Equal (baseReferences + 5, face.ManagedResource.ReferenceCount);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	[Fact]
	public void ImplicitTextAndNonemptyGlyphOperationsAcquireTheMeasuredCacheOwner ()
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		Surface.ConfigureAutomaticRecording (true);
		try {
			AssertImplicitOwner ("empty-extents", context => context.TextExtents (string.Empty), acquire: true);
			AssertImplicitOwner ("empty-show", context => context.ShowText (string.Empty), acquire: true);
			AssertImplicitOwner ("empty-path", context => context.TextPath (string.Empty), acquire: true);
			AssertImplicitOwner ("font-extents", context => _ = context.FontExtents, acquire: true);
			AssertImplicitOwner ("empty-glyph-extents", context => context.GlyphExtents (Array.Empty<Glyph> ()), acquire: false);
			AssertImplicitOwner ("empty-glyph-path", context => context.GlyphPath (Array.Empty<Glyph> ()), acquire: false);
			AssertImplicitOwner ("empty-show-glyphs", context => context.ShowGlyphs (Array.Empty<Glyph> ()), acquire: false);
			var oneGlyph = new[] { new Glyph (36, 2, 14) };
			AssertImplicitOwner ("glyph-extents", context => context.GlyphExtents (oneGlyph), acquire: true);
			AssertImplicitOwner ("glyph-path", context => context.GlyphPath (oneGlyph), acquire: true);
			AssertImplicitOwner ("show-glyphs", context => context.ShowGlyphs (oneGlyph), acquire: true);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	[Fact]
	public void MalformedAndFinishedTextOperationsKeepNativeAcquisitionOrder ()
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		Surface.ConfigureAutomaticRecording (true);
		try {
			AssertImplicitOwner ("invalid-extents", context => context.TextExtents (new byte[] { 0xff }), acquire: true, expectedStatus: Status.InvalidString);
			AssertImplicitOwner ("invalid-show", context => context.ShowText (new byte[] { 0xff }), acquire: true, expectedStatus: Status.InvalidString);
			AssertImplicitOwner ("invalid-path", context => context.TextPath (new byte[] { 0xff }), acquire: true);
			AssertImplicitOwner ("finished-empty-extents", context => context.TextExtents (string.Empty), acquire: true, finishSurface: true);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	static void AssertImplicitOwner (string label, Action<Context> operation, bool acquire,
		Status expectedStatus = Status.Success, bool finishSurface = false, Status expectedSurfaceStatus = Status.Success)
	{
		using var face = new FontFace ("Optimum Scaled Implicit " + label + " 20261026", FontSlant.Normal, FontWeight.Normal);
		using var surface = new ImageSurface (Format.Argb32, 32, 24);
		var context = new Context (surface);
		uint explicitOwner = face.ManagedResource.ReferenceCount;
		context.SetContextFontFace (face);
		uint before = face.ManagedResource.ReferenceCount;
		Assert.Equal (explicitOwner + 1, before);
		if (finishSurface) Assert.Equal (Status.Success, surface.Finish ());
		operation (context);
		Assert.Equal (expectedStatus, context.Status);
		Assert.Equal (expectedSurfaceStatus, surface.Status);
		Assert.Equal (before + (acquire ? 1u : 0u), face.ManagedResource.ReferenceCount);
		context.Dispose ();
		Assert.Equal (explicitOwner + (acquire ? 1u : 0u), face.ManagedResource.ReferenceCount);
	}

	static void RunBoth (Action exercise)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int denied = CairoNativeGuard.DeniedCount;
		try {
			if (CairoNativeGuard.NativeAllowed) { Surface.ConfigureAutomaticRecording (false); exercise (); }
			Surface.ConfigureAutomaticRecording (true); exercise ();
			Assert.Equal (denied, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}
}
