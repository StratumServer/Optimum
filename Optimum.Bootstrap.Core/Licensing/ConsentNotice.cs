using System.Reflection;

namespace Optimum.Bootstrap.Core.Licensing;

/// <summary>
/// The decompilation and license notice a user must accept before a build.
/// Posture C in INSTALLER-PLAN.md: the GUI gates on a checkbox and
/// <c>Optimum.Cli build</c> refuses without <c>--acknowledge-decompile</c>. The
/// text is a draft pending a legal review before the first release; it must stay
/// consistent with <c>LICENSE-SCOPE.md</c> and <c>NOTICE</c>.
/// </summary>
public static class ConsentNotice
{
    private const string ResourceName = "Optimum.Bootstrap.Core.Licensing.consent-notice.md";

    /// <summary>The flag name the CLI requires and RiftLauncher passes.</summary>
    public const string AcknowledgeFlag = "--acknowledge-decompile";

    public static string Text { get; } = Load();

    private static string Load()
    {
        Assembly assembly = typeof(ConsentNotice).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded consent notice '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd() + "\n";
    }
}
