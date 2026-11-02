using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Optimum.Installer.Views;

/// <summary>Small view-only converters, resolved against the native Avalonia
/// Fluent theme resources (WinUI-style tokens). No custom palette.</summary>
public static class InstallerConverters
{
    /// <summary>Prerequisite status label colour: optional → accent, blocker → caution.</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<bool, IBrush?>(isOptional =>
            Brush(isOptional ? "SystemAccentColorLight1" : "SystemFillColorCautionBrush"));

    /// <summary>Install outcome → the completion banner severity.</summary>
    public static readonly IValueConverter OutcomeSeverity =
        new FuncValueConverter<bool, NotificationType>(ok =>
            ok ? NotificationType.Success : NotificationType.Error);

    /// <summary>Install outcome → the colour of the completion badge: a success
    /// green on pass, the critical red on failure (standard Fluent status colours).</summary>
    public static readonly IValueConverter OutcomeBrush =
        new FuncValueConverter<bool, IBrush?>(ok =>
            Brush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"));

    /// <summary>Install outcome → the badge glyph.</summary>
    public static readonly IValueConverter OutcomeGlyph =
        new FuncValueConverter<bool, string>(ok => ok ? "✓" : "!");

    /// <summary>A reached step's circle is filled with the system accent; a pending one is hollow.</summary>
    public static readonly IValueConverter StepCircleBrush =
        new FuncValueConverter<bool, IBrush?>(reached =>
            reached ? Brush("SystemAccentColor") : new SolidColorBrush(Colors.Transparent));

    /// <summary>A pending step's circle has a 1.5px outline; a reached one has none.</summary>
    public static readonly IValueConverter StepCircleBorder =
        new FuncValueConverter<bool, Thickness>(reached => new Thickness(reached ? 0 : 1.5));

    /// <summary>The current step's label is emphasised; others are regular weight.</summary>
    public static readonly IValueConverter StepWeight =
        new FuncValueConverter<bool, FontWeight>(current => current ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Log line level ("error"/"warn"/other) → its colour.</summary>
    public static readonly IValueConverter LogLevelBrush =
        new FuncValueConverter<string, IBrush?>(level => Brush(level switch
        {
            "error" => "SystemFillColorCriticalBrush",
            "warn" => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
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
