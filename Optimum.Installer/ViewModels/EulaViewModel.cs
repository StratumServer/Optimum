using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core.Licensing;

namespace Optimum.Installer.ViewModels;

/// <summary>
/// The mandatory consent modal. Posture C in INSTALLER-PLAN.md: the user must
/// scroll to the end and tick the box before the build can start.
/// </summary>
public sealed partial class EulaViewModel : ViewModelBase
{
    public string ConsentText { get; } = PlainText(ConsentNotice.Text);

    [ObservableProperty]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private string _dataPathSummary = "Use the standard Vintage Story data folder";

    [ObservableProperty]
    private string _versionSummary = "Recommended version";

    [ObservableProperty]
    private string _shortcutSummary = "Application menu entry";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(ScrollHint))]
    private bool _scrolledToEnd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _accepted;

    public bool CanAccept => ScrolledToEnd && Accepted;

    public string ScrollHint => ScrolledToEnd
        ? "Notice read. Confirm your acceptance to start."
        : "Scroll to the end of the notice to continue.";

    public event Action? AcceptRequested;
    public event Action? DeclineRequested;

    [RelayCommand]
    private void Accept()
    {
        if (CanAccept)
            AcceptRequested?.Invoke();
    }

    [RelayCommand]
    private void Decline() => DeclineRequested?.Invoke();

    public void Configure(
        string installDirectory,
        string? dataPath,
        string? version,
        bool createMenuEntry,
        bool createDesktopShortcut,
        bool registerUriHandler = false)
    {
        InstallDirectory = installDirectory;
        DataPathSummary = dataPath ?? "Use the standard Vintage Story data folder";
        VersionSummary = version ?? "Recommended version";
        ShortcutSummary = (createMenuEntry, createDesktopShortcut, registerUriHandler) switch
        {
            (true, true, false) => "Application menu entry and desktop shortcut",
            (true, false, false) => "Application menu entry",
            (false, true, false) => "Desktop shortcut",
            (false, false, false) => "No shortcuts",
            (true, true, true) => "Application menu entry, desktop shortcut, and ModDB URI handler",
            (true, false, true) => "Application menu entry and ModDB URI handler",
            (false, true, true) => "Desktop shortcut and ModDB URI handler",
            (false, false, true) => "ModDB URI handler",
        };
    }

    public void Reset()
    {
        ScrolledToEnd = false;
        Accepted = false;
    }

    internal static string PlainText(string markdown)
    {
        string[] rawLines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(rawLines.Length);
        bool previousWasHeading = false;

        foreach (string rawLine in rawLines)
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                output.Add(string.Empty);
                previousWasHeading = false;
                continue;
            }

            bool isHeading = trimmed.StartsWith('#');
            string cleaned = (isHeading ? trimmed.TrimStart('#', ' ') : trimmed)
                .Replace("`", string.Empty, StringComparison.Ordinal);

            bool startsListItem = IsListItemStart(cleaned);
            if (!isHeading && !startsListItem && !previousWasHeading && output.Count > 0 && output[^1].Length > 0)
            {
                output[^1] = output[^1] + " " + cleaned;
            }
            else
            {
                output.Add(cleaned);
            }

            previousWasHeading = isHeading;
        }

        return string.Join('\n', output);
    }

    private static bool IsListItemStart(string line)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            return true;

        int dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot <= 0)
            return false;

        for (int i = 0; i < dot; i++)
        {
            if (!char.IsAsciiDigit(line[i]))
                return false;
        }

        return true;
    }
}
