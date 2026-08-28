using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Optimum.Installer.Views;

/// <summary>Small view-only converters, resolved against SukiUI's theme resources.</summary>
public static class InstallerConverters
{
    /// <summary>Prerequisite status label colour: optional → info blue, blocker → warning.</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<bool, IBrush?>(isOptional =>
            Brush(isOptional ? "SukiInformationColor" : "SukiWarningColor"));

    /// <summary>Install outcome → the completion info bar severity.</summary>
    public static readonly IValueConverter OutcomeSeverity =
        new FuncValueConverter<bool, NotificationType>(ok =>
            ok ? NotificationType.Success : NotificationType.Error);

    /// <summary>Install outcome → the colour of the completion badge.</summary>
    public static readonly IValueConverter OutcomeBrush =
        new FuncValueConverter<bool, IBrush?>(ok =>
            Brush(ok ? "SukiSuccessColor" : "SukiDangerColor"));

    /// <summary>Install outcome → the badge glyph.</summary>
    public static readonly IValueConverter OutcomeGlyph =
        new FuncValueConverter<bool, string>(ok => ok ? "✓" : "!");

    /// <summary>Log line level ("error"/"warn"/other) → its colour.</summary>
    public static readonly IValueConverter LogLevelBrush =
        new FuncValueConverter<string, IBrush?>(level => Brush(level switch
        {
            "error" => "SukiDangerColor",
            "warn" => "SukiWarningColor",
            _ => "SukiLowText",
        }));

    private static IBrush? Brush(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current?.ActualThemeVariant, out object? value) != true)
            return null;
        return value switch
        {
            Color c => new SolidColorBrush(c),
            IBrush b => b,
            _ => null,
        };
    }
}
