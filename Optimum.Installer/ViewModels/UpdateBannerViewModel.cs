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

    [RelayCommand]
    private async Task Update()
    {
        Updating = true;
        try
        {
            await updates.ApplyAsync(p => post(() => Progress = p));
        }
        catch (Exception)
        {
            Updating = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => Dismissed = true;
}
