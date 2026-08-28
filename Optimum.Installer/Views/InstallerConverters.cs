using System;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Optimum.Installer.Views;

/// <summary>Small view-only converters for the wizard templates, resolved against SukiUI's theme resources.</summary>
public static class InstallerConverters
{
    /// <summary>True (an optional tool) → warning colour; false (a blocker) → danger colour.</summary>
    public static readonly IValueConverter OptionalOrBlockingDot =
        new FuncValueConverter<bool, IBrush?>(isOptional =>
            Brush(isOptional ? "SukiWarningColor" : "SukiDangerColor"));

    /// <summary>Log line level ("error"/"warn"/other) → its colour.</summary>
    public static readonly IValueConverter LogLevelBrush =
        new FuncValueConverter<string, IBrush?>(level => Brush(level switch
        {
            "error" => "SukiDangerColor",
            "warn" => "SukiWarningColor",
            _ => "SukiMuteText",
        }));

    /// <summary>Install outcome → the tinted chip behind the completion glyph.</summary>
    public static readonly IValueConverter OutcomeSurface =
        new FuncValueConverter<bool, IBrush?>(ok => Tint(ok ? "SukiSuccessColor" : "SukiDangerColor", 0.16));

    public static readonly IValueConverter OutcomeInk =
        new FuncValueConverter<bool, IBrush?>(ok => Brush(ok ? "SukiSuccessColor" : "SukiDangerColor"));

    public static readonly IValueConverter OutcomeGlyph =
        new FuncValueConverter<bool, string>(ok => ok ? "✓" : "!");

    private static Color? ResolveColor(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current?.ActualThemeVariant, out object? value) != true)
            return null;
        return value switch
        {
            Color c => c,
            ISolidColorBrush b => b.Color,
            _ => null,
        };
    }

    private static IBrush? Brush(string key) =>
        ResolveColor(key) is { } c ? new SolidColorBrush(c) : null;

    private static IBrush? Tint(string key, double opacity) =>
        ResolveColor(key) is { } c ? new SolidColorBrush(c, opacity) : null;
}
