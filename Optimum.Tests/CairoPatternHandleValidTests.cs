using System;
using Cairo;
using Xunit;

// GuiElement.getPattern keeps a pattern only while HandleValid is true. Managed patterns have no native handle, so
// HandleValid was always false and every dialog compose decoded the background PNG again and leaked the old pattern.
[Collection ("GUI metrics")]
public sealed class CairoPatternHandleValidTests
{
	[Fact]
	public void ManagedPatternsAreValidUntilDisposed ()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		Surface.ConfigureAutomaticRecording (true);
		try {
			AssertValidUntilDisposed ();
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	static void AssertValidUntilDisposed ()
	{
		byte[] pixels = BitConverter.GetBytes (unchecked ((int) 0xff3366cc));
		using var texture = new ImageSurface (pixels, Format.Argb32, 1, 1, 4);
		Pattern[] patterns = {
			new SurfacePattern (texture),
			new SolidPattern (0.2, 0.4, 0.6, 1),
			new LinearGradient (0, 0, 4, 0),
			new RadialGradient (2, 2, 0, 2, 2, 2),
		};

		foreach (Pattern pattern in patterns) {
			Assert.Equal (IntPtr.Zero, pattern.NativeHandleForRecorder);
			Assert.True (pattern.HandleValid, pattern.GetType ().Name);
			pattern.Dispose ();
			Assert.False (pattern.HandleValid, pattern.GetType ().Name);
		}
	}

	[Fact]
	public void CachedSurfacePatternPaintsTheSameOnEverySurface ()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		Surface.ConfigureAutomaticRecording (true);
		try {
			AssertPaintsTheSameOnEverySurface ();
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	static void AssertPaintsTheSameOnEverySurface ()
	{
		byte[] pixels = BitConverter.GetBytes (unchecked ((int) 0xff3366cc));
		using var texture = new ImageSurface (pixels, Format.Argb32, 1, 1, 4);
		using var pattern = new SurfacePattern (texture) { Filter = Filter.Nearest, Extend = Extend.Repeat };

		for (int i = 0; i < 2; i++) {
			using var surface = new ImageSurface (Format.Argb32, 4, 4);
			using (var context = new Context (surface)) {
				context.SetSource (pattern);
				context.Rectangle (0, 0, 4, 4);
				context.Fill ();
			}
			surface.Flush ();
			Assert.True (pattern.HandleValid);
			Assert.Equal (unchecked ((int) 0xff3366cc), System.Runtime.InteropServices.Marshal.ReadInt32 (surface.DataPtr, 4 * 5));
		}
	}
}

// Once cached patterns are reused, a GUI scale change must drop them, or they keep the matrix of the old scale.
public sealed class GuiScaleDropsCachedPatternsTests
{
	[Fact]
	public void GuiScaleWatcherClearsTheGuiPatternCache ()
	{
		string path = Optimum.Tests.PatchReader.FindRepositoryFile ("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
		using var lib = Mono.Cecil.AssemblyDefinition.ReadAssembly (path);
		var il = System.Linq.Enumerable.ToList (System.Linq.Enumerable.Single (lib.MainModule.GetType ("Vintagestory.Client.GuiCompositeMainMenuLeft").Methods, m => m.Name == "OnGuiScaleChanged").Body.Instructions);

		int load = il.FindIndex (i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld && i.Operand is Mono.Cecil.FieldReference f && f.Name == "cachedPatterns");
		Assert.True (load >= 0, "OnGuiScaleChanged must read GuiElement.cachedPatterns");
		Assert.True (il[load + 1].Operand is Mono.Cecil.MethodReference m && m.Name == "Clear", "and clear it");
	}
}
