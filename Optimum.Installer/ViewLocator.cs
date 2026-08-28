using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer;

/// <summary>Maps a <c>FooViewModel</c> to a <c>Optimum.Installer.Views.FooView</c>.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = "(no content)" };

        string name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        Type? type = Type.GetType(name);
        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
