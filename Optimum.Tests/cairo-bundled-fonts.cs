using System;
using System.IO;
using System.Runtime.InteropServices;

// The game ships Montserrat, Lora and Almendra under assets/game/fonts and registers them with Fontconfig at
// startup. Tests that measure or draw those families need the same registration.
internal static class CairoBundledFonts
{
	[DllImport ("libfontconfig.so.1")] static extern int FcConfigAppFontAddDir (IntPtr config, string dir);
	static bool registered;

	internal static bool Available { get; private set; }

	internal static void Register ()
	{
		if (registered) return;
		registered = true;
		string root = Environment.GetEnvironmentVariable ("VINTAGE_STORY");
		if (string.IsNullOrEmpty (root)) root = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), ".local", "share", "vintagestory");
		string fonts = Path.Combine (root, "assets", "game", "fonts");
		if (!Directory.Exists (fonts)) return;
		try { Available = FcConfigAppFontAddDir (IntPtr.Zero, fonts) != 0; } catch (DllNotFoundException) { }
	}
}
