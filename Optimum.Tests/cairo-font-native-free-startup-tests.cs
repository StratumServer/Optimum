using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

public sealed class CairoFontNativeFreeStartupTests
{
	[Fact]
	public void StaticInitializationAndMeasurementStayOnSkiaWhenCairoIsDenied ()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		float previousScale = RuntimeEnv.GUIScale;
		RuntimeEnv.GUIScale = 1;
		try {
			CairoBundledFonts.Register ();
			using (var font = new CairoFont (16, "sans-serif")) {
				FontExtents fontExtents = font.GetFontExtents ();
				TextExtents textExtents = font.GetTextExtents ("Skia metrics");
				Assert.True (fontExtents.Height > 0);
				Assert.True (textExtents.XAdvance > 0);
			}
			Assert.Null (CairoFont.FontMeasuringContext);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			string[] mappings = CairoNativeGuard.LoadedLibcairoMappings ();
			if (mappings != null) Assert.Empty (mappings);
		} finally {
			RuntimeEnv.GUIScale = previousScale;
		}
	}
}
