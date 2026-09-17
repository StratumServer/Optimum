using Xunit;

// Twelve test classes in this assembly mutate OptimumDiagnostics, whose counters
// are plain statics shared by the whole process while the recording context is
// [ThreadStatic]. Several also flip StutterWatchEnabled process-wide, which makes
// production animation and launch-task code record into those same counters from
// whatever else happens to be running.
//
// xunit runs collections in parallel by default, so those classes raced: over
// repeated full runs the failure moved between EntityAnimationDiagnosticsCoverage,
// LaunchTaskTimeBudget and OptimumStatus, always as a count that did not match.
// The race predates the Vulkan backend work; adding test classes changed the
// scheduling enough to surface it nearly every run.
//
// Serialising the assembly removes the whole class of failure for a suite that
// runs in well under a second. The better long-term fix is to scope the counters
// per context so the tests do not share global state at all, at which point this
// can go.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
