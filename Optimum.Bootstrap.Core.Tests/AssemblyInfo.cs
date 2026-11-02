using Xunit;

// Several tests change TMPDIR and XDG_* process-wide while creating temporary fixtures.
// Keep this suite serial so concurrent tests cannot inherit a temporary path that another test removes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
