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
        string? picked = await PickFolderAsync("Choose the Optimum install folder");
        if (picked is not null && DataContext is OptionsViewModel vm)
            vm.InstallDirectory = picked;
    }

    private async void BrowseDataPath(object? sender, RoutedEventArgs e)
    {
        string? picked = await PickFolderAsync("Choose the Vintage Story data folder");
        if (picked is not null && DataContext is OptionsViewModel vm)
            vm.DataPath = picked;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return null;

        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
