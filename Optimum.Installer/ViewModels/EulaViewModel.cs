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
    public string ConsentText => ConsentNotice.Text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _scrolledToEnd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _accepted;

    public bool CanAccept => ScrolledToEnd && Accepted;

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

    public void Reset()
    {
        ScrolledToEnd = false;
        Accepted = false;
    }
}
