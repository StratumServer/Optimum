using Vintagestory.API.Config;

namespace Optimum;

/// <summary>
/// Exposes Optimum version metadata from VintagestoryAPI.
/// OptimumConfig.Version supplies the managed value. Packaging scripts read VERSION.
/// </summary>
public static class OptimumInfo
{
    public const string Version = OptimumConfig.Version;
    public const string Name = "Optimum";
    public const string Url = "https://github.com/Zaldaryon/Optimum";
    public const string DisplayTag = Name + " v" + Version;
}
