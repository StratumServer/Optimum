using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

/// <summary>
/// The "a newer installer is available" banner. Non-blocking: the user can keep
/// going or take the update, which downloads and restarts.
/// </summary>
public sealed partial class UpdateBannerViewModel(IUpdateService updates, string version, Action<Action> post) : ViewModelBase
{
    public string Message => $"Installer {version} is available.";

    [ObservableProperty]
    private bool _updating;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private bool _dismissed;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private async Task Update()
    {
        Updating = true;
        Error = null;
        try
        {
            // On success ApplyAsync restarts the process and never returns.
            await updates.ApplyAsync(p => post(() => Progress = p));
        }
        catch (Exception ex)
        {
            Updating = false;
            Error = $"Update failed: {ex.Message}. You can keep installing and update later.";
        }
    }

    [RelayCommand]
    private void Dismiss() => Dismissed = true;
}
