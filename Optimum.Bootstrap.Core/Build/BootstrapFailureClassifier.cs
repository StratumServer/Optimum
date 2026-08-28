using System.Text.RegularExpressions;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// Decides whether a failed <c>bootstrap</c> run failed while applying patches
/// (so the caller gets <see cref="FailureReason.PatchConflict"/>) or earlier,
/// during download or decompile (<see cref="FailureReason.DecompileFailed"/>).
/// The distinction matters to RiftLauncher, which maps the reason to a message.
/// </summary>
public static partial class BootstrapFailureClassifier
{
    public static FailureReason Classify(string bootstrapOutput) =>
        PatchFailure().IsMatch(bootstrapOutput)
            ? FailureReason.PatchConflict
            : FailureReason.DecompileFailed;

    [GeneratedRegex(
        @"patch (failed|does not apply)|hunk\s.*FAILED|error:\s.*\.patch|\.rej\b|patch application (failed|aborted)|failed to apply|Applying .* patch .* failed",
        RegexOptions.IgnoreCase)]
    private static partial Regex PatchFailure();
}
