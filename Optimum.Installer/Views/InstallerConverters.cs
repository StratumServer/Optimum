using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Optimum.Installer.Views;

/// <summary>Small view-only converters for the wizard templates.</summary>
public static class InstallerConverters
{
    /// <summary>True (an optional tool) → warning colour; false (a blocker) → danger colour.</summary>
    public static readonly IValueConverter OptionalOrBlockingDot =
        new FuncValueConverter<bool, IBrush?>(isOptional =>
            Resource(isOptional ? "AppWarningBrush" : "AppDangerBrush"));

    /// <summary>Log line level ("error"/"warn"/other) → its colour.</summary>
    public static readonly IValueConverter LogLevelBrush =
        new FuncValueConverter<string, IBrush?>(level => Resource(level switch
        {
            "error" => "AppDangerBrush",
            "warn" => "AppWarningBrush",
            _ => "AppMutedTextBrush",
        }));

    /// <summary>Install outcome → the tinted chip behind the completion glyph.</summary>
    public static readonly IValueConverter OutcomeSurface =
        new FuncValueConverter<bool, IBrush?>(ok => Resource(ok ? "AppSuccessSurfaceBrush" : "AppDangerSurfaceBrush"));

    public static readonly IValueConverter OutcomeInk =
        new FuncValueConverter<bool, IBrush?>(ok => Resource(ok ? "AppSuccessBrush" : "AppDangerBrush"));

    public static readonly IValueConverter OutcomeGlyph =
        new FuncValueConverter<bool, string>(ok => ok ? "✓" : "!");

    private static IBrush? Resource(string key) =>
        Application.Current?.TryGetResource(key, Application.Current?.ActualThemeVariant, out object? value) == true
            ? value as IBrush
            : null;
}
