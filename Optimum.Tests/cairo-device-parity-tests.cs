using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cairo;
using Xunit;

/// <summary>
/// Cairo.Device keeps the cairo_device_t contract (reference counting, recursive acquire/release, finish,
/// sticky status) in managed code. The lockstep test drives a native script device, the only device libcairo can
/// create without a display, and a managed device of the same type through seeded random call sequences. The
/// remaining tests need no native library. Without libcairo.so.2 the lockstep test is skipped unless
/// OPTIMUM_REQUIRE_NATIVE_CAIRO=1 turns that into a failure.
/// </summary>
public sealed class CairoDevicePolicyTests
{
	static class Native
	{
		const string Lib = "libcairo.so.2";

		[DllImport (Lib)] internal static extern IntPtr cairo_script_create ([MarshalAs (UnmanagedType.LPStr)] string filename);
		[DllImport (Lib)] internal static extern int cairo_device_acquire (IntPtr device);
		[DllImport (Lib)] internal static extern void cairo_device_release (IntPtr device);
		[DllImport (Lib)] internal static extern void cairo_device_flush (IntPtr device);
		[DllImport (Lib)] internal static extern void cairo_device_finish (IntPtr device);
		[DllImport (Lib)] internal static extern int cairo_device_status (IntPtr device);
		[DllImport (Lib)] internal static extern int cairo_device_get_type (IntPtr device);
		[DllImport (Lib)] internal static extern IntPtr cairo_device_reference (IntPtr device);
		[DllImport (Lib)] internal static extern void cairo_device_destroy (IntPtr device);
		[DllImport (Lib)] internal static extern uint cairo_device_get_reference_count (IntPtr device);
	}

	static bool NativeAvailable ()
	{
		if (OperatingSystem.IsLinux () && NativeLibrary.TryLoad ("libcairo.so.2", out var handle)) {
			NativeLibrary.Free (handle);
			return true;
		}
		Assert.False (Environment.GetEnvironmentVariable ("OPTIMUM_REQUIRE_NATIVE_CAIRO") == "1",
			"libcairo.so.2 is required (OPTIMUM_REQUIRE_NATIVE_CAIRO=1) but cannot be loaded.");
		return false;
	}

	[Fact]
	public void SeededCallSequencesMatchASystemScriptDevice ()
	{
		if (!NativeAvailable ())
			return;

		var random = new Random (0x5EED0010);
		string directory = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-device-parity-" + Guid.NewGuid ().ToString ("N"));
		Directory.CreateDirectory (directory);
		try {
			for (int scenario = 0; scenario < 400; scenario++) {
				var log = new StringBuilder ();
				IntPtr nativeDevice = Native.cairo_script_create (System.IO.Path.Combine (directory, scenario + ".cs"));
				IntPtr managedHandle = DeviceHandles.Create (DeviceType.Script);
				var managed = new Device (managedHandle);
				var nativeWrapperReference = Native.cairo_device_reference (nativeDevice);
				Assert.Equal (nativeDevice, nativeWrapperReference);
				int depth = 0;

				Assert.Equal ((int) DeviceType.Script, Native.cairo_device_get_type (nativeDevice));
				Assert.Equal (DeviceType.Script, managed.Type);

				int steps = random.Next (1, 24);
				for (int step = 0; step < steps; step++) {
					string context = $"scenario {scenario} after {log}";
					switch (random.Next (8)) {
					case 0:
					case 1: {
						int nativeStatus = Native.cairo_device_acquire (nativeDevice);
						Status managedStatus = managed.Acquire ();
						log.Append ("acquire;");
						Assert.True (nativeStatus == (int) managedStatus, $"{context}: acquire native {nativeStatus} managed {(int) managedStatus}");
						if (nativeStatus == 0) depth++;
						break;
					}
					case 2:
					case 3:
						if (depth > 0) {
							Native.cairo_device_release (nativeDevice);
							managed.Release ();
							depth--;
							log.Append ("release;");
						}
						break;
					case 4:
						Native.cairo_device_flush (nativeDevice);
						managed.Flush ();
						log.Append ("flush;");
						break;
					case 5:
						Native.cairo_device_finish (nativeDevice);
						managed.Finish ();
						log.Append ("finish;");
						break;
					case 6:
						log.Append ("status;");
						break;
					default:
						Native.cairo_device_reference (nativeDevice);
						DeviceHandles.Reference (managedHandle);
						log.Append ("reference;");
						// balance it immediately so the final destroy order stays comparable
						Native.cairo_device_destroy (nativeDevice);
						DeviceHandles.Destroy (managedHandle);
						break;
					}

					context = $"scenario {scenario} after {log}";
					int status = Native.cairo_device_status (nativeDevice);
					Assert.True (status == (int) managed.Status, $"{context}: status native {status} managed {(int) managed.Status}");
					Assert.Equal ((int) Native.cairo_device_get_reference_count (nativeDevice), DeviceHandles.ReferenceCountOf (managedHandle));
				}

				while (depth-- > 0) {
					Native.cairo_device_release (nativeDevice);
					managed.Release ();
				}

				// Dropping the wrapper reference, then the creator's: the device stays usable until the last one.
				Native.cairo_device_destroy (nativeWrapperReference);
				managed.Dispose ();
				Assert.Equal ((int) Native.cairo_device_get_reference_count (nativeDevice), DeviceHandles.ReferenceCountOf (managedHandle));
				Assert.Equal (1, DeviceHandles.ReferenceCountOf (managedHandle));
				Native.cairo_device_destroy (nativeDevice);
				DeviceHandles.Destroy (managedHandle);
				Assert.Equal (0, DeviceHandles.ReferenceCountOf (managedHandle));
			}
		} finally {
			Directory.Delete (directory, true);
		}
	}

	[Fact]
	public void FinishedDevicesReportDeviceFinishedAndStaySticky ()
	{
		IntPtr handle = DeviceHandles.Create (DeviceType.Xml);
		using var device = new Device (handle);
		Assert.Equal (DeviceType.Xml, device.Type);
		Assert.Equal (Status.Success, device.Status);
		Assert.Equal (Status.Success, device.Acquire ());
		device.Release ();

		device.Flush ();
		device.Finish ();
		device.Finish ();
		device.Flush ();
		Assert.Equal (Status.Success, device.Status);
		Assert.Equal ((Status) 37, device.Acquire ()); // CAIRO_STATUS_DEVICE_FINISHED, which Cairo.Status has no name for
		Assert.Equal ((Status) 37, device.Status);
		Assert.Equal ((Status) 37, device.Acquire ());
		DeviceHandles.Destroy (handle);
	}

	[Fact]
	public void DestroyingTheLastReferenceFinishesTheDevice ()
	{
		IntPtr handle = DeviceHandles.Create (DeviceType.Script);
		var device = new Device (handle);
		Assert.Equal (2, DeviceHandles.ReferenceCountOf (handle));
		device.Dispose ();
		device.Dispose ();
		Assert.Equal (1, DeviceHandles.ReferenceCountOf (handle));
		DeviceHandles.Destroy (handle);
		Assert.Equal (0, DeviceHandles.ReferenceCountOf (handle));
		Assert.Throws<ArgumentException> (() => new Device (handle));
		Assert.Throws<ArgumentException> (() => new Device (IntPtr.Zero));
		Assert.Throws<ArgumentException> (() => new Device (new IntPtr (0x1234)));
		Assert.Throws<ObjectDisposedException> (() => device.Acquire ());
		Assert.Throws<ObjectDisposedException> (() => device.Type);
	}

	[Fact]
	public void AcquireIsRecursiveAndExcludesOtherThreads ()
	{
		IntPtr handle = DeviceHandles.Create (DeviceType.Script);
		using var device = new Device (handle);
		Assert.Equal (Status.Success, device.Acquire ());
		Assert.Equal (Status.Success, device.Acquire ());

		using var attempted = new ManualResetEventSlim ();
		Status otherThreadStatus = Status.NoMemory;
		var other = new Thread (() => {
			attempted.Set ();
			otherThreadStatus = device.Acquire ();
			device.Release ();
		});
		other.Start ();
		Assert.True (attempted.Wait (TimeSpan.FromSeconds (5)));
		Assert.False (other.Join (150), "the second thread must wait while the device is held");

		device.Release ();
		Assert.False (other.Join (100), "one release of two acquires must not free the device");
		device.Release ();
		Assert.True (other.Join (TimeSpan.FromSeconds (5)));
		Assert.Equal (Status.Success, otherThreadStatus);
		DeviceHandles.Destroy (handle);
	}

	[Fact]
	public void ReleaseWithoutAcquireIsIgnored ()
	{
		IntPtr handle = DeviceHandles.Create (DeviceType.Script);
		using var device = new Device (handle);
		device.Release ();
		Assert.Equal (Status.Success, device.Acquire ());
		device.Release ();
		DeviceHandles.Destroy (handle);
	}
}
