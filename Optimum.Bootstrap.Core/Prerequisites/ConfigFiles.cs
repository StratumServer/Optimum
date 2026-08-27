using System.Text.Json;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Reads the two files that pin the decompiler: <c>.config/dotnet-tools.json</c>
/// (the exact ilspycmd version) and <c>.config/ilspycmd-compat.json</c> (the
/// accepted range). Both front ends read this once and share the result, the
/// same way <c>Get-Pinned-ILSpyVersion</c> and <c>Get-Accepted-ILSpyVersionRange</c>
/// do in <c>scripts/install-windows.ps1</c>.
/// </summary>
public static class ConfigFiles
{
    public static IlspycmdCompatibility ReadIlspycmdCompatibility(ISystemProbe probe, string repoRoot)
    {
        var fallback = IlspycmdCompatibility.Fallback;

        IlspycmdVersion min = fallback.Minimum;
        IlspycmdVersion max = fallback.Maximum;
        string pin = fallback.Pin;

        string compatText = probe.ReadText(Path.Combine(repoRoot, ".config", "ilspycmd-compat.json")) ?? string.Empty;
        if (TryReadObject(compatText, out JsonElement compat))
        {
            if (compat.TryGetProperty("minimumVersion", out var minEl)
                && IlspycmdVersion.TryParse(minEl.GetString(), out var parsedMin))
                min = parsedMin;
            if (compat.TryGetProperty("maximumVersion", out var maxEl)
                && IlspycmdVersion.TryParse(maxEl.GetString(), out var parsedMax))
                max = parsedMax;
        }

        string toolsText = probe.ReadText(Path.Combine(repoRoot, ".config", "dotnet-tools.json")) ?? string.Empty;
        if (TryReadObject(toolsText, out JsonElement tools)
            && tools.TryGetProperty("tools", out var toolsObj)
            && toolsObj.TryGetProperty("ilspycmd", out var ilspy)
            && ilspy.TryGetProperty("version", out var verEl)
            && verEl.GetString() is { Length: > 0 } parsedPin)
        {
            pin = parsedPin;
        }

        return new IlspycmdCompatibility(min, max, pin);
    }

    private static bool TryReadObject(string json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
