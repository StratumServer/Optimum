using CommunityToolkit.Mvvm.ComponentModel;
using Optimum.Bootstrap.Core;

namespace Optimum.Installer.ViewModels;

/// <summary>
/// Placeholder shell view model. The five-screen flow in INSTALLER-PLAN.md
/// section 5 lands in Phase 3.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = $"Optimum installer {CoreInfo.Version}";
}
