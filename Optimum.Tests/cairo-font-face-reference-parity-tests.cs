using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFontFaceReferenceParityTests
{
	[Fact]
	public void ToyFaceCreationAndContextOwnershipMatchNative ()
	{
		RunBoth (() => {
			const string family = "Optimum Reference Parity 20261026";
			using var first = new FontFace (family, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = first.Handle;
			uint initial = first.ReferenceCount;
			using var same = new FontFace (family, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = same.Handle;
			Assert.NotSame (first, same);
			Assert.Equal (initial + 1, first.ReferenceCount);

			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			var context = new Context (surface);
			context.SetContextFontFace (first);
			Assert.Equal (initial + 2, first.ReferenceCount);
			var selected = context.GetContextFontFace ();
			Assert.Equal (initial + 3, first.ReferenceCount);
			selected.Dispose ();
			Assert.Equal (initial + 2, first.ReferenceCount);

			context.Save ();
			Assert.Equal (initial + 3, first.ReferenceCount);
			context.SelectFontFace (family + " Changed", FontSlant.Normal, FontWeight.Normal);
			Assert.Equal (initial + 2, first.ReferenceCount);
			context.Restore ();
			Assert.Equal (initial + 2, first.ReferenceCount);

			using var secondSurface = new ImageSurface (Format.Argb32, 8, 8);
			using var secondContext = new Context (secondSurface);
			secondContext.SetContextFontFace (first);
			Assert.Equal (initial + 3, first.ReferenceCount);
			context.Dispose ();
			Assert.Equal (initial + 2, first.ReferenceCount);
			context.Dispose ();
			Assert.Equal (initial + 2, first.ReferenceCount);
			secondContext.Dispose ();
			Assert.Equal (initial + 1, first.ReferenceCount);
			same.Dispose ();
			Assert.Equal (initial, first.ReferenceCount);

			using var savedSurface = new ImageSurface (Format.Argb32, 8, 8);
			var savedContext = new Context (savedSurface);
			savedContext.SetContextFontFace (first);
			savedContext.Save ();
			savedContext.Save ();
			uint beforeDispose = first.ReferenceCount;
			savedContext.Dispose ();
			Assert.Equal (beforeDispose - 3, first.ReferenceCount);
		});
	}

	[Fact]
	public void RepeatedContextFontFaceGettersOwnIndependentReferences ()
	{
		RunBoth (() => {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var context = new Context (surface);
			context.SelectFontFace ("Optimum Getter Parity 20261026", FontSlant.Normal, FontWeight.Normal);
			using var seed = context.GetContextFontFace ();
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = seed.Handle;
			uint initial = seed.ReferenceCount;
			var first = context.GetContextFontFace ();
			var second = context.GetContextFontFace ();
			Assert.Equal (initial + 2, seed.ReferenceCount);
			first.Dispose ();
			Assert.Equal (initial + 1, seed.ReferenceCount);
			second.Dispose ();
			Assert.Equal (initial, seed.ReferenceCount);
		});
	}

	[Fact]
	public void DefaultContextFaceSharesEmptyIdentityWithoutReferencingExplicitSans ()
	{
		RunBoth (() => {
			using var sans = new FontFace ("Sans", FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = sans.Handle;
			uint sansCount = sans.ReferenceCount;
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var context = new Context (surface);
			Assert.Equal (sansCount, sans.ReferenceCount);
			using var defaultFace = context.GetContextFontFace ();
			Assert.Equal (sansCount, sans.ReferenceCount);
			if (!CairoNativeGuard.NativeAllowed || Surface.AutomaticRecordingEnabled)
				Assert.Equal ("Sans", defaultFace.Family);
			uint defaultCount = defaultFace.ReferenceCount;
			using var empty = new FontFace (string.Empty, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = empty.Handle;
			Assert.Equal (defaultCount + 1, empty.ReferenceCount);
		});
	}

	[Fact]
	public void EmptyDefaultFaceIsMaterializedOnlyByContextGetter ()
	{
		RunBoth (() => {
			using var empty = new FontFace (string.Empty, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = empty.Handle;
			uint initial = empty.ReferenceCount;
			using var firstSurface = new ImageSurface (Format.Argb32, 8, 8);
			using var secondSurface = new ImageSurface (Format.Argb32, 8, 8);
			var firstContext = new Context (firstSurface);
			var secondContext = new Context (secondSurface);
			Assert.Equal (initial, empty.ReferenceCount);
			var first = firstContext.GetContextFontFace ();
			Assert.Equal (initial + 2, empty.ReferenceCount);
			var second = secondContext.GetContextFontFace ();
			Assert.Equal (initial + 4, empty.ReferenceCount);
			first.Dispose ();
			Assert.Equal (initial + 3, empty.ReferenceCount);
			firstContext.Dispose ();
			Assert.Equal (initial + 2, empty.ReferenceCount);
			second.Dispose ();
			Assert.Equal (initial + 1, empty.ReferenceCount);
			secondContext.Dispose ();
			Assert.Equal (initial, empty.ReferenceCount);
		});
	}

	[Fact]
	public void SaveBeforeDefaultFaceGetterDoesNotCreateSavedFaceOwnership ()
	{
		RunBoth (() => {
			using var empty = new FontFace (string.Empty, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = empty.Handle;
			uint initial = empty.ReferenceCount;
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var context = new Context (surface);
			context.Save ();
			Assert.Equal (initial, empty.ReferenceCount);
			var getter = context.GetContextFontFace ();
			Assert.Equal (initial + 2, empty.ReferenceCount);
			context.Restore ();
			Assert.Equal (initial + 1, empty.ReferenceCount);
			getter.Dispose ();
			Assert.Equal (initial, empty.ReferenceCount);
		});
	}

	[Fact]
	public void NullFontFaceResetDefersDefaultFaceOwnershipUntilGetter ()
	{
		RunBoth (() => {
			using var empty = new FontFace (string.Empty, FontSlant.Normal, FontWeight.Normal);
			if (CairoNativeGuard.NativeAllowed && !Surface.AutomaticRecordingEnabled) _ = empty.Handle;
			uint initial = empty.ReferenceCount;
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var context = new Context (surface);
			using var custom = new FontFace ("Optimum Null Reset 20261026", FontSlant.Normal, FontWeight.Normal);
			context.SetContextFontFace (custom);
			context.SetContextFontFace (null);
			Assert.Equal (initial, empty.ReferenceCount);
			var getter = context.GetContextFontFace ();
			Assert.Equal (initial + 2, empty.ReferenceCount);
			getter.Dispose ();
			context.Dispose ();
			Assert.Equal (initial, empty.ReferenceCount);
		});
	}

	[Fact]
	public void PushGroupAndPopGroupPreserveContextFaceOwnership ()
	{
		RunBoth (() => {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum Group Ownership 20261026", FontSlant.Normal, FontWeight.Normal);
			var context = new Context (surface);
			context.SetContextFontFace (face);
			uint initial = face.ReferenceCount;
			context.Save ();
			Assert.Equal (initial + 1, face.ReferenceCount);
			context.PushGroup ();
			Assert.Equal (initial + 2, face.ReferenceCount);
			context.SelectFontFace ("Optimum Group Alternate 20261026", FontSlant.Normal, FontWeight.Normal);
			Assert.Equal (initial + 1, face.ReferenceCount);
			using (Pattern group = context.PopGroup ()) { }
			Assert.Equal (initial + 1, face.ReferenceCount);
			context.Restore ();
			Assert.Equal (initial, face.ReferenceCount);
			context.PushGroup ();
			Assert.Equal (initial + 1, face.ReferenceCount);
			using (Pattern group = context.PopGroup ()) { }
			Assert.Equal (initial, face.ReferenceCount);
			context.Dispose ();
			Assert.Equal (initial - 1, face.ReferenceCount);
		});
	}

	[Fact]
	public void DisposingContextInsideGroupReleasesCurrentAndSavedFaceReferences ()
	{
		RunBoth (() => {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum Group Disposal 20261026", FontSlant.Normal, FontWeight.Normal);
			var context = new Context (surface);
			context.SetContextFontFace (face);
			uint initial = face.ReferenceCount;
			context.PushGroup ();
			Assert.Equal (initial + 1, face.ReferenceCount);
			context.Dispose ();
			Assert.Equal (initial - 1, face.ReferenceCount);
		});
	}

	[Fact]
	public void PdfPageTransitionRetainsOneContextFaceOwner ()
	{
		RunBoth (() => {
			string path = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-font-page-" + Guid.NewGuid ().ToString ("N") + ".pdf");
			try {
				using var surface = new PdfSurface (path, 8, 8);
				using var face = new FontFace ("Optimum Page Ownership 20261026", FontSlant.Normal, FontWeight.Normal);
				var context = new Context (surface);
				context.SetContextFontFace (face);
				uint initial = face.ReferenceCount;
				context.ShowPage ();
				Assert.Equal (initial, face.ReferenceCount);
				context.Dispose ();
				Assert.Equal (initial - 1, face.ReferenceCount);
			} finally { if (System.IO.File.Exists (path)) System.IO.File.Delete (path); }
		});
	}

	[Fact]
	public void RepeatedScaledFontGettersDoNotAddPerWrapperFaceReferences ()
	{
		RunBoth (() => {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum Scaled Getter 20261026", FontSlant.Normal, FontWeight.Normal);
			using var context = new Context (surface);
			context.SetContextFontFace (face);
			using var first = context.GetScaledFont ();
			uint afterFirst = face.ReferenceCount;
			using var second = context.GetScaledFont ();
			Assert.Equal (afterFirst, face.ReferenceCount);
		});
	}

	[Fact]
	public void SetTargetReleasesOwnershipFromThePreviousContextState ()
	{
		if (CairoNativeGuard.NativeAllowed) return; // The allow mode intentionally routes SetTarget through Cairo materialization.
		RunBoth (() => {
			using var firstSurface = new ImageSurface (Format.Argb32, 8, 8);
			using var secondSurface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum SetTarget Face 20261026", FontSlant.Normal, FontWeight.Normal);
			var context = new Context (firstSurface);
			context.SetContextFontFace (face);
			uint initial = face.ReferenceCount;
			context.SetTarget (secondSurface);
			Assert.Equal (initial - 1, face.ReferenceCount);
			context.Dispose ();
			Assert.Equal (initial - 1, face.ReferenceCount);
		});
	}

	[Fact]
	public void SettingSameTargetResetsAndReleasesContextFaceOwnership ()
	{
		if (CairoNativeGuard.NativeAllowed) return; // The allow mode intentionally routes SetTarget through Cairo materialization.
		RunBoth (() => {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum SetTarget Reset 20261026", FontSlant.Normal, FontWeight.Normal);
			using var context = new Context (surface);
			context.SetContextFontFace (face);
			uint initial = face.ReferenceCount;
			context.SetTarget (surface);
			Assert.Equal (initial - 1, face.ReferenceCount);
		});
	}

	[Fact]
	public void DisposingContextReleasesFaceOwnershipAndKeepsRecordedPixels ()
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var face = new FontFace ("Optimum Retained Replay 20261026", FontSlant.Normal, FontWeight.Normal);
			var context = new Context (surface);
			context.SetContextFontFace (face);
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 8, 8);
			context.Fill ();
			uint beforeDispose = face.ReferenceCount;
			context.Dispose ();
			Assert.Equal (beforeDispose - 1, face.ReferenceCount);
			byte[] pixels = surface.Data;
			Assert.NotNull (pixels);
			Assert.True (pixels[2] > 0 && pixels[3] > 0, "Recorded pixels must remain available after Context disposal.");
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	[Fact]
	public void ToyFaceCacheSweepsDeadFamilyKeysAfterGcChurn ()
	{
		int before = ToyCacheEntryCount ();
		var churned = new WeakReference<ManagedFontFaceResource>[512];
		for (int i = 0; i < churned.Length; i++)
			churned[i] = CreateDisposedToyFace ("Optimum Cache Churn 20261026 " + i);

		CollectGarbage ();
		Assert.All (churned, weak => Assert.False (weak.TryGetTarget (out _), "Disposed toy-face resource should be collectible."));

		// Seed another batch, collect it, then make enough misses to guarantee
		// periodic sweeps observe dead targets rather than live temporary locals.
		for (int i = 0; i < 128; i++)
			CreateDisposedToyFace ("Optimum Cache Sweep Seed 20261026 " + i);
		CollectGarbage ();
		for (int i = 0; i < 64; i++)
			CreateDisposedToyFace ("Optimum Cache Sweep Seed Trigger 20261026 " + i);
		CollectGarbage ();
		for (int i = 0; i < 64; i++)
			CreateDisposedToyFace ("Optimum Cache Sweep Dead Trigger 20261026 " + i);
		CollectGarbage ();

		Assert.InRange (ToyCacheEntryCount (), 0, before + 64);
	}

	[Fact]
	public void ConcurrentDistinctToyFacesKeepIndependentReferenceCounts ()
	{
		Parallel.For (0, 8, worker => {
			for (int i = 0; i < 96; i++) {
				string family = "Optimum Concurrent Face " + worker + " " + i;
				using var first = new FontFace (family, FontSlant.Normal, FontWeight.Normal);
				using var same = new FontFace (family, FontSlant.Normal, FontWeight.Normal);
				using var distinct = new FontFace (family + " other", FontSlant.Normal, FontWeight.Normal);
				Assert.Equal (2u, first.ReferenceCount);
				Assert.Equal (1u, distinct.ReferenceCount);
			}
		});

		const string sharedFamily = "Optimum Concurrent Shared Face 20261026";
		using var anchor = new FontFace (sharedFamily, FontSlant.Normal, FontWeight.Normal);
		Parallel.For (0, 8, _ => {
			for (int i = 0; i < 96; i++) {
				using var same = new FontFace (sharedFamily, FontSlant.Normal, FontWeight.Normal);
			}
		});
		Assert.Equal (1u, anchor.ReferenceCount);
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static WeakReference<ManagedFontFaceResource> CreateDisposedToyFace (string family)
	{
		var face = new FontFace (family, FontSlant.Normal, FontWeight.Normal);
		var weak = new WeakReference<ManagedFontFaceResource> (face.ManagedResource);
		face.Dispose ();
		return weak;
	}

	static void CollectGarbage ()
	{
		GC.Collect ();
		GC.WaitForPendingFinalizers ();
		GC.Collect ();
	}

	static int ToyCacheEntryCount ()
	{
		FieldInfo field = typeof (ManagedFontFaceResource).GetField ("ToyCache", BindingFlags.NonPublic | BindingFlags.Static);
		var cache = Assert.IsAssignableFrom<IDictionary> (field.GetValue (null));
		return cache.Count;
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
