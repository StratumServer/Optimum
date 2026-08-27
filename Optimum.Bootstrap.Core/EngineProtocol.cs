using System.Reflection;

namespace Optimum.Bootstrap.Core;

/// <summary>
/// The build phases the engine reports through <see cref="BootstrapProgress"/>.
/// The set is part of the engine contract in INSTALLER-PLAN.md section 4 and a
/// caller may switch on it exhaustively.
/// </summary>
public enum ProgressPhase
{
    Decompile,
    Patch,
    Verify,
    Assemble,
}

/// <summary>
/// One progress observation. <paramref name="Percent"/> is a monotonic
/// non-decreasing integer in the range 0 to 99. The engine never emits 100:
/// the caller owns the terminal 100 after its own post-validation.
/// </summary>
public readonly record struct BootstrapProgress(ProgressPhase Phase, int Percent, string Detail)
{
    public const int MaxEnginePercent = 99;
}

/// <summary>
/// The closed set of failure reasons a terminal result may carry. Kebab-case on
/// the wire (see <see cref="FailureReasonExtensions.Wire"/>). Adding a value is a
/// breaking change for a caller that switches on it exhaustively.
/// </summary>
public enum FailureReason
{
    BadInput,
    UnsupportedVersion,
    PatchConflict,
    DecompileFailed,
    AssembleFailed,
    VerificationFailed,
    OutputExists,
    Cancelled,
    EngineInternal,
}

public static class FailureReasonExtensions
{
    /// <summary>The kebab-case token used on the NDJSON wire.</summary>
    public static string Wire(this FailureReason reason) => reason switch
    {
        FailureReason.BadInput => "bad-input",
        FailureReason.UnsupportedVersion => "unsupported-version",
        FailureReason.PatchConflict => "patch-conflict",
        FailureReason.DecompileFailed => "decompile-failed",
        FailureReason.AssembleFailed => "assemble-failed",
        FailureReason.VerificationFailed => "verification-failed",
        FailureReason.OutputExists => "output-exists",
        FailureReason.Cancelled => "cancelled",
        FailureReason.EngineInternal => "engine-internal",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

/// <summary>Severity of a <c>log</c> event, shared by the build pipeline and the NDJSON stream.</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>Assembly-level facts shared by both front ends.</summary>
public static class CoreInfo
{
    /// <summary>
    /// The Optimum version, from the informational version attribute, falling
    /// back to the assembly version. Matches how <c>Optimum.Launcher</c> resolves
    /// its own version.
    /// </summary>
    public static string Version { get; } =
        typeof(CoreInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
        ?? typeof(CoreInfo).Assembly.GetName().Version?.ToString()
        ?? "dev";
}
