using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Cairo;
using Xunit;

/// <summary>
/// CairoAPI.Version and CairoAPI.VersionString no longer call libcairo; they report the fixed API level the
/// wrapper was validated against. The tests pin the two values to each other and, where the system libcairo is at
/// least that release of the same 1.18 series, to what that library reports.
/// </summary>
public sealed class CairoVersionTests
{
	static class Native
	{
		const string Lib = "libcairo.so.2";

		[DllImport (Lib)] internal static extern int cairo_version ();
		[DllImport (Lib)] internal static extern IntPtr cairo_version_string ();
	}

	[Fact]
	public void VersionAndVersionStringDescribeTheSameRelease ()
	{
		string[] parts = CairoAPI.VersionString.Split ('.');
		Assert.Equal (3, parts.Length);
		int encoded = int.Parse (parts[0], CultureInfo.InvariantCulture) * 10000
			+ int.Parse (parts[1], CultureInfo.InvariantCulture) * 100
			+ int.Parse (parts[2], CultureInfo.InvariantCulture);
		Assert.Equal (encoded, CairoAPI.Version);
		Assert.Equal (11804, CairoAPI.Version);
		Assert.Equal ("1.18.4", CairoAPI.VersionString);
	}

	// An older patch release of the series (1.18.0 on Ubuntu 24.04) cannot equal the pinned constant, so the
	// comparison only runs from the pinned release on.
	[NativeCairoExpectationFact]
	public void FixedVersionMatchesTheSystemLibcairoOfTheSameSeries ()
	{
		if (!OperatingSystem.IsLinux ()) return; // The direct imports below name the Linux library.

		int system = Native.cairo_version ();
		string systemString = Marshal.PtrToStringAnsi (Native.cairo_version_string ());
		Assert.Equal (system / 10000 + "." + system / 100 % 100 + "." + system % 100, systemString);
		// Only compare inside the 1.18 series: a newer system cairo is a reason to revisit the constant, not a failure.
		if (system / 100 == CairoAPI.Version / 100) {
			Assert.Equal (system, CairoAPI.Version);
			Assert.Equal (systemString, CairoAPI.VersionString);
		}
	}
}
