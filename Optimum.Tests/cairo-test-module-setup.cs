using System.Runtime.CompilerServices;

static class CairoTestModuleSetup
{
	// Tests toggle OptimumConfig GUI GPU eligibility in parallel collections. Surfaces in
	// unrelated tests must not start recording because of that; tests of auto-recording
	// opt in per thread through CairoGpuPolicy.AutoRecordOverride.
	[ModuleInitializer]
	internal static void DisableSurfaceAutoRecording () => Cairo.CairoGpuPolicy.AutoRecordProcessOverride = false;
}
