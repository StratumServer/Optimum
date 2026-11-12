using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Cairo;
using Path = System.IO.Path;
using Xunit;

/// <summary>
/// A library mapping is process wide and never undone, so "libcairo is not mapped" cannot be asserted in a process
/// that also runs the libcairo oracle tests, whatever the test order. The default run allows the oracle, so this
/// test repeats the Cairo tests in a fresh process with OPTIMUM_CAIRO_NATIVE=deny, where the oracle tests skip and
/// every native-free assertion (no denied import, nothing in /proc/self/maps) runs against a clean process.
/// </summary>
public sealed class CairoNativeFreeLaneTests
{
	internal const string ChildVariable = "OPTIMUM_CAIRO_NATIVE_FREE_LANE";

	[NativeFreeLaneFact]
	public async Task CairoTestsPassInAFreshProcessThatDeniesLibcairo ()
	{
		string results = Path.Combine (Path.GetTempPath (), "optimum-native-free-lane-" + Guid.NewGuid ().ToString ("N"));
		try {
			var start = new ProcessStartInfo ("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
			start.ArgumentList.Add ("test");
			start.ArgumentList.Add (typeof (CairoNativeFreeLaneTests).Assembly.Location);
			start.ArgumentList.Add ("--filter");
			start.ArgumentList.Add ("FullyQualifiedName~Cairo");
			start.ArgumentList.Add ("--logger");
			start.ArgumentList.Add ("trx;LogFileName=lane.trx");
			start.ArgumentList.Add ("--results-directory");
			start.ArgumentList.Add (results);
			start.Environment[CairoNativeGuard.ModeVariable] = "deny";
			start.Environment[ChildVariable] = "1";
			using var process = Process.Start (start);
			var output = process.StandardOutput.ReadToEndAsync ();
			var errors = process.StandardError.ReadToEndAsync ();
			if (!process.WaitForExit (600000)) { process.Kill (entireProcessTree: true); throw new TimeoutException ("The native-free Cairo lane timed out."); }
			string text = await output + await errors;

			string report = Path.Combine (results, "lane.trx");
			Assert.True (File.Exists (report), "The native-free lane wrote no result file." + Environment.NewLine + text);
			XElement root = XDocument.Load (report).Root;
			XNamespace ns = root.Name.Namespace;
			XElement counters = root.Descendants (ns + "Counters").Single ();
			string[] failed = root.Descendants (ns + "UnitTestResult")
				.Where (result => (string) result.Attribute ("outcome") == "Failed")
				.Select (result => (string) result.Attribute ("testName") + ": " + ((string) result.Descendants (ns + "Message").FirstOrDefault ())?.Split ('\n')[0])
				.ToArray ();
			Assert.True (failed.Length == 0, failed.Length + " native-free Cairo tests failed:" + Environment.NewLine + string.Join (Environment.NewLine, failed.Take (25)));
			Assert.True (process.ExitCode == 0, text);
			// Guards against a filter that silently selects nothing.
			Assert.True ((int) counters.Attribute ("passed") > 500, "The native-free lane ran too few tests: " + counters);
		} finally {
			if (Directory.Exists (results)) Directory.Delete (results, recursive: true);
		}
	}
}

/// <summary>Runs where the current process cannot make the native-free assertions itself.</summary>
public sealed class NativeFreeLaneFactAttribute : FactAttribute
{
	public NativeFreeLaneFactAttribute ()
	{
		if (!string.IsNullOrEmpty (Environment.GetEnvironmentVariable (CairoNativeFreeLaneTests.ChildVariable)))
			Skip = "This process is the native-free lane.";
		else if (!CairoNativeGuard.NativeAllowed)
			Skip = "This process keeps libcairo unmapped, so the native-free assertions already run here.";
		else if (!OperatingSystem.IsLinux ())
			Skip = "Loaded libraries are only inspected through /proc/self/maps on Linux.";
	}
}
