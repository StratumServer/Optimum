using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class OptionsView : UserControl
{
    public OptionsView() => AvaloniaXamlLoader.Load(this);

    private async void BrowseInstallDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm)
            return;

        string? picked = await PickFolderAsync("Choose the Optimum install folder", vm.InstallDirectory);
        if (picked is not null)
            vm.InstallDirectory = picked;
    }

    private async void BrowseDataPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm)
            return;

        string? picked = await PickFolderAsync("Choose the Vintage Story data folder", vm.DataPath);
        if (picked is not null)
            vm.DataPath = picked;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title, string? currentPath)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return null;

        try
        {
            IStorageFolder? startLocation = string.IsNullOrWhiteSpace(currentPath)
                ? null
                : await storage.TryGetFolderFromPathAsync(currentPath);
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
