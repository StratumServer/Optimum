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
        bool createDesktopShortcut)
    {
        InstallDirectory = installDirectory;
        DataPathSummary = dataPath ?? "Use the standard Vintage Story data folder";
        VersionSummary = version ?? "Recommended version";
        ShortcutSummary = (createMenuEntry, createDesktopShortcut) switch
        {
            (true, true) => "Application menu entry and desktop shortcut",
            (true, false) => "Application menu entry",
            (false, true) => "Desktop shortcut",
            _ => "No shortcuts",
        };
    }

    public void Reset()
    {
        ScrolledToEnd = false;
        Accepted = false;
    }

    private static string PlainText(string markdown) => string.Join('\n',
        markdown.Split('\n').Select(line =>
        {
            string text = line.StartsWith('#') ? line.TrimStart('#', ' ') : line;
            return text.Replace("`", string.Empty, StringComparison.Ordinal);
        }));
}
