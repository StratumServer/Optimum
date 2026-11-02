using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cairo;
using Xunit;

/// <summary>Managed pixel storage ownership for Cairo image surfaces.</summary>
public sealed class CairoManagedImageSurfaceTests
{
	static readonly FieldInfo storageField = typeof (ImageSurface).GetField ("managedStorage", BindingFlags.Instance | BindingFlags.NonPublic);

	static CairoManagedImageSurfaceTests () { _ = CairoAPI.Version; }

	[Fact]
	public void ManagedPixelsShareNativeWritesAndDataReturnsAnIndependentCopy ()
	{
		using var surface = new ImageSurface (Format.Argb32, 7, 3);
		Assert.Equal (7, surface.Width);
		Assert.Equal (3, surface.Height);
		Assert.Equal (28, surface.Stride);
		Assert.Equal (Format.Argb32, surface.Format);

		IntPtr pixels = surface.DataPtr;
		Assert.NotEqual (IntPtr.Zero, pixels);
		Assert.All (surface.Data, value => Assert.Equal ((byte) 0, value));

		Marshal.WriteByte (pixels, 5, 0xA5);
		byte[] copy = surface.Data;
		Assert.Equal ((byte) 0xA5, copy[5]);
		copy[5] = 0x11;
		Assert.Equal ((byte) 0xA5, Marshal.ReadByte (pixels, 5));
	}

	[Fact]
	public void ManagedPixelPinLivesUntilTheLastNativeSurfaceAliasIsDestroyed ()
	{
		(AliasState state, WeakReference<ManagedImageSurfaceStorage> storage) = CreateAliasedSurface ();
		ForceCompactingCollection ();

		Assert.True (IsStorageAlive (storage));
		Assert.Equal ((byte) 0x5A, Marshal.ReadByte (state.DataPointer, 0));
		Assert.Equal ((byte) 0xA5, state.Alias.Data[4]);

		state.Alias.Dispose ();
		GC.KeepAlive (state.Alias);
		ForceCompactingCollection ();
		Assert.False (IsStorageAlive (storage));
	}

	[Fact]
	public void ZeroSizedImageSurfaceKeepsNullPixelStorageLikeCairo ()
	{
		using var surface = new ImageSurface (Format.Argb32, 0, 0);
		Assert.Equal (0, surface.Width);
		Assert.Equal (0, surface.Height);
		Assert.Equal (0, surface.Stride);
		Assert.Null (surface.Data);
		Assert.Equal (IntPtr.Zero, surface.DataPtr);
		Assert.False (GetStorage (surface).HasManagedPixels);
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static (AliasState, WeakReference<ManagedImageSurfaceStorage>) CreateAliasedSurface ()
	{
		var owner = new ImageSurface (Format.Argb32, 32, 24);
		IntPtr dataPointer = owner.DataPtr;
		Marshal.WriteByte (dataPointer, 0, 0x5A);
		Marshal.WriteByte (dataPointer, 4, 0xA5);
		var storage = GetStorage (owner);
		var weakStorage = new WeakReference<ManagedImageSurfaceStorage> (storage);
		var alias = (ImageSurface) Surface.Lookup (owner.Handle, false);
		owner.Dispose ();
		return (new AliasState (alias, dataPointer), weakStorage);
	}

	static ManagedImageSurfaceStorage GetStorage (ImageSurface surface) =>
		(ManagedImageSurfaceStorage) storageField.GetValue (surface);

	static void ForceCompactingCollection ()
	{
		GC.Collect (GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		GC.WaitForPendingFinalizers ();
		GC.Collect (GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static bool IsStorageAlive (WeakReference<ManagedImageSurfaceStorage> storage) => storage.TryGetTarget (out _);

	sealed class AliasState
	{
		internal readonly ImageSurface Alias;
		internal readonly IntPtr DataPointer;

		internal AliasState (ImageSurface alias, IntPtr dataPointer)
		{
			Alias = alias;
			DataPointer = dataPointer;
		}
	}
}
