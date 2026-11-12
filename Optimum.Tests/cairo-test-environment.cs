using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cairo;
using Xunit;

// The product default keeps libcairo unmapped. Most Cairo tests compare the managed implementation against
// libcairo as an oracle, so the test process opts into the native backend unless the caller already chose a mode.
// An external OPTIMUM_CAIRO_NATIVE value (deny, managed, allow) always wins, which keeps the native-free lanes
// runnable: OPTIMUM_CAIRO_NATIVE=deny dotnet test, or the child process started by CairoNativeFreeLaneTests.
internal static class CairoTestEnvironment
{
	internal const int ExpectationVersion = 11804;

	delegate int VersionFunction ();

	// Evaluated once, after the module initializer, so every skip decision sees the same mode.
	static readonly Lazy<int> nativeVersion = new Lazy<int> (ReadNativeVersion);
	static readonly Lazy<bool> nanoSvgAvailable = new Lazy<bool> (ProbeNanoSvg);
	static bool libraryMissing;

#pragma warning disable CA2255 // The mode must be chosen before any test or static constructor touches Cairo.
	[ModuleInitializer]
	internal static void Initialize ()
	{
		if (!string.IsNullOrEmpty (Environment.GetEnvironmentVariable (CairoNativeGuard.ModeVariable))) return;
		// Without a loadable libcairo the oracle cannot run; staying in the managed default lets those tests
		// skip instead of failing on a missing library.
		if (TryLoadCairo (out _)) Environment.SetEnvironmentVariable (CairoNativeGuard.ModeVariable, "allow");
		else libraryMissing = true;
	}
#pragma warning restore CA2255

	/// <summary>The libcairo oracle is permitted by the mode and the library loads.</summary>
	internal static bool NativeCairoAvailable => NativeVersion != 0;

	/// <summary>cairo_version() of the loaded libcairo, or 0 when the oracle is unavailable.</summary>
	internal static int NativeVersion => nativeVersion.Value;

	internal static bool NanoSvgAvailable => nanoSvgAvailable.Value;

	// OPTIMUM_REQUIRE_NATIVE_CAIRO=1 turns a missing oracle into failures instead of skips.
	internal static string NativeCairoSkipReason =>
		NativeCairoAvailable || Environment.GetEnvironmentVariable ("OPTIMUM_REQUIRE_NATIVE_CAIRO") == "1" ? null
		: libraryMissing || CairoNativeGuard.NativeAllowed ? "Needs libcairo as an oracle, and libcairo cannot be loaded on this host."
		: "Needs libcairo as an oracle; " + CairoNativeGuard.ModeVariable + "=" + CairoNativeGuard.Mode + " keeps it unmapped.";

	internal static string ExpectationVersionSkipReason =>
		!NativeCairoAvailable ? NativeCairoSkipReason
		: NativeVersion >= ExpectationVersion ? null
		: "Expectations were captured from libcairo " + Format (ExpectationVersion) + "; this host has " + Format (NativeVersion) + ".";

	internal static string NanoSvgSkipReason =>
		NanoSvgAvailable ? null
		: "Needs the nanosvg native library from the game Lib directory next to the test assembly (bootstrap the game files, then rebuild).";

	/// <summary>Outside allow mode nothing may have mapped libcairo; the mappings are only readable on Linux.</summary>
	internal static void AssertLibcairoUnmapped ()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		if (CairoNativeGuard.LoadedLibcairoMappings () is string[] mappings) Assert.Empty (mappings);
	}

	static string Format (int version) => version / 10000 + "." + version / 100 % 100 + "." + version % 100;

	static int ReadNativeVersion ()
	{
		// Never probe outside allow mode: loading libcairo here would break every "nothing mapped" assertion.
		if (!CairoNativeGuard.NativeAllowed || !TryLoadCairo (out IntPtr library)) return 0;
		if (!NativeLibrary.TryGetExport (library, "cairo_version", out IntPtr export)) return 0;
		return Marshal.GetDelegateForFunctionPointer<VersionFunction> (export) ();
	}

	// Same names the wrapper's import resolver ends up loading.
	static bool TryLoadCairo (out IntPtr library)
	{
		string name = OperatingSystem.IsLinux () ? "libcairo.so.2"
			: OperatingSystem.IsMacOS () ? "libcairo.2.dylib"
			: "libcairo-2";
		return NativeLibrary.TryLoad (name, typeof (CairoAPI).Assembly, null, out library);
	}

	static bool ProbeNanoSvg () => NativeLibrary.TryLoad ("nanosvg", typeof (CairoAPI).Assembly, null, out _);
}

/// <summary>Runs only when libcairo is usable as an oracle; otherwise the test is reported as skipped.</summary>
public sealed class NativeCairoFactAttribute : FactAttribute
{
	public NativeCairoFactAttribute () { Skip = CairoTestEnvironment.NativeCairoSkipReason; }
}

/// <inheritdoc cref="NativeCairoFactAttribute"/>
public sealed class NativeCairoTheoryAttribute : TheoryAttribute
{
	public NativeCairoTheoryAttribute () { Skip = CairoTestEnvironment.NativeCairoSkipReason; }
}

/// <summary>Runs only against a libcairo at least as new as the release the expectations were captured from.</summary>
public sealed class NativeCairoExpectationFactAttribute : FactAttribute
{
	public NativeCairoExpectationFactAttribute () { Skip = CairoTestEnvironment.ExpectationVersionSkipReason; }
}

/// <inheritdoc cref="NativeCairoExpectationFactAttribute"/>
public sealed class NativeCairoExpectationTheoryAttribute : TheoryAttribute
{
	public NativeCairoExpectationTheoryAttribute () { Skip = CairoTestEnvironment.ExpectationVersionSkipReason; }
}

/// <summary>Runs only when the nanosvg native library can be loaded.</summary>
public sealed class NanoSvgFactAttribute : FactAttribute
{
	public NanoSvgFactAttribute () { Skip = CairoTestEnvironment.NanoSvgSkipReason; }
}

/// <inheritdoc cref="NanoSvgFactAttribute"/>
public sealed class NanoSvgTheoryAttribute : TheoryAttribute
{
	public NanoSvgTheoryAttribute () { Skip = CairoTestEnvironment.NanoSvgSkipReason; }
}
