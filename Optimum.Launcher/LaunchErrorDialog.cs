using System;
using System.Runtime.InteropServices;

namespace Optimum.Launcher;

/// <summary>
/// Displays a native Windows error dialog when the WinExe launcher aborts on an
/// interactive desktop, so fatal startup failures include both the reason and the
/// location of <c>optimum-launcher.log</c> rather than exiting silently.
/// </summary>
internal static class LaunchErrorDialog
{
    internal const string NoDialogVariable = "OPTIMUM_NO_ERROR_DIALOG";
    internal const string DialogTitle = "Optimum Launch Failed";
    internal const int MaxReasonLength = 1500;

    private const uint MbOk = 0x00000000u;
    private const uint MbIconError = 0x00000010u;
    private const uint MbSetForeground = 0x00010000u;

    internal static bool ShouldShow(
        bool isWindows,
        bool userInteractive,
        bool nonInteractiveMode,
        string? noDialogEnv,
        string? ciEnv) =>
        isWindows
        && userInteractive
        && !nonInteractiveMode
        && !string.Equals(noDialogEnv, "1", StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(ciEnv);

    internal static string FormatMessage(string reason, string? logPath)
    {
        string cleanedReason = string.IsNullOrWhiteSpace(reason)
            ? "An unexpected error prevented Optimum from starting."
            : reason.Trim();

        if (cleanedReason.Length > MaxReasonLength)
        {
            cleanedReason = cleanedReason[..MaxReasonLength].TrimEnd()
                + "...\n(See the log file for full details.)";
        }

        string body = $"Optimum could not launch Vintage Story.\n\n{cleanedReason}";
        if (!string.IsNullOrWhiteSpace(logPath))
            body += $"\n\nLog file:\n{logPath}";
        return body;
    }

    internal static void TryShow(string reason, string? logPath, bool nonInteractiveMode)
    {
        if (!ShouldShow(
                OperatingSystem.IsWindows(),
                Environment.UserInteractive,
                nonInteractiveMode,
                Environment.GetEnvironmentVariable(NoDialogVariable),
                Environment.GetEnvironmentVariable("CI")))
        {
            return;
        }

        try
        {
            _ = MessageBoxW(
                IntPtr.Zero,
                FormatMessage(reason, logPath),
                DialogTitle,
                MbOk | MbIconError | MbSetForeground);
        }
        catch
        {
            // Showing the dialog is best-effort; never mask the logged abort or exit code 1.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
