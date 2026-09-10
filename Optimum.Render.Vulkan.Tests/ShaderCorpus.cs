using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Loads the game's shaders the way the client does, so translation is tested
/// against what actually reaches the driver rather than against the files on disk.
///
/// Two steps matter. ShaderRegistry expands <c>#include</c> before compiling, and
/// it de-duplicates per program, so a shader that pulls in fogandlight.fsh twice
/// gets it once. And registerDefaultShaderCodePrefixes prepends a block of
/// <c>#define</c>s whose values change which declarations survive the
/// preprocessor - a shader compiled with SSAOLEVEL 0 declares different uniforms
/// than the same file at SSAOLEVEL 2.
/// </summary>
internal static class ShaderCorpus
{
    /// <summary>
    /// The vanilla assets, or null when this checkout has not bootstrapped. The
    /// shaders are proprietary and never committed, so tests that need them skip
    /// rather than fail when they are absent.
    /// </summary>
    public static string? AssetRoot => _assetRoot ??= FindAssetRoot();
    private static string? _assetRoot;

    private static string? FindAssetRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("VINTAGE_STORY_ASSETS");
        if (!string.IsNullOrEmpty(fromEnvironment) &&
            Directory.Exists(Path.Combine(fromEnvironment, "shaders")))
        {
            return fromEnvironment;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName, ".vanilla", "win-x64", "vintagestory", "assets", "game");
            if (Directory.Exists(Path.Combine(candidate, "shaders")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The repository root, found by walking up to the solution file.</summary>
    public static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();
    private static string? _repositoryRoot;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }

    /// <summary>
    /// Every shader file, with Optimum's own overlays replacing their vanilla
    /// counterparts - which is what `make deploy` copies over the install, so it
    /// is what actually runs.
    /// </summary>
    public static Dictionary<string, string> LoadShaderFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (AssetRoot != null)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(AssetRoot, "shaders")))
            {
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        string overlays = Path.Combine(RepositoryRoot, "sources", "shaders");
        if (Directory.Exists(overlays))
        {
            foreach (string path in Directory.EnumerateFiles(overlays))
            {
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        return files;
    }

    /// <summary>
    /// The shader includes, with Optimum's own overlays replacing their vanilla
    /// counterparts - the same relationship <see cref="LoadShaderFiles" /> has,
    /// and the same one `make deploy` and the package scripts produce on disk.
    /// Without the overlay the corpus would translate the vanilla vertexwarp.vsh
    /// while the client runs Optimum's WarpState one.
    /// </summary>
    public static Dictionary<string, string> LoadIncludes()
    {
        var includes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AssetRoot != null)
        {
            string directory = Path.Combine(AssetRoot, "shaderincludes");
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    includes[Path.GetFileName(path)] = File.ReadAllText(path);
                }
            }
        }

        string overlays = Path.Combine(RepositoryRoot, "sources", "shaderincludes");
        if (Directory.Exists(overlays))
        {
            foreach (string path in Directory.EnumerateFiles(overlays))
            {
                includes[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        return includes;
    }

    /// <summary>
    /// Program base names that have both a vertex and a fragment shader, which is
    /// what ShaderRegistry requires of a loadable program.
    /// </summary>
    public static List<string> ProgramNames(Dictionary<string, string> files)
    {
        return files.Keys
            .Where(name => name.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^4])
            .Where(name => files.ContainsKey(name + ".fsh"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Regex IncludePattern =
        new(@"^#include\s+(.*)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Mirrors ShaderRegistry.HandleIncludes, including its de-duplication: a
    /// file already pulled into this program expands to nothing the second time.
    /// </summary>
    public static string ExpandIncludes(
        string code, Dictionary<string, string> includes, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return IncludePattern.Replace(code, match =>
        {
            string filename = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (!seen.Add(filename)) return "";
            return includes.TryGetValue(filename, out string? included)
                ? ExpandIncludes(included, includes, seen)
                : "";
        });
    }

    /// <summary>A set of the defines the client injects per program.</summary>
    public sealed class ShaderVariant
    {
        public string Name = "";
        public int Fxaa;
        public int SsaoLevel;
        public int Bloom;
        public int GodRays;
        public int ShadowQuality;
        public int DynLights;
        public int UseSsbo;
        public int WavingStuff = 1;
        public int FoamEffect = 1;
        public int ShinyEffect = 1;
        public int NormalView;
        public int UseOit = 1;
        public int GreedyMesh;
        public float MinBright;
        public int MaxAnimatedElements = 35;
        /// <summary>TAA motion-vector writers compiled in (OptimumConfig.EffectiveTaa).</summary>
        public int TaaMotion;
        /// <summary>Primary colour attachment the motion texture occupies: 4 with the SSAO G-buffer, 2 without.</summary>
        public int TaaMotionLocation = 2;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Variants chosen to move every define that gates a declaration. The two
    /// extremes catch the common cases; the middle rows catch the combinations
    /// where one feature is on and its neighbours are not, which is where a
    /// conditional uniform is most likely to be missed.
    /// </summary>
    public static IEnumerable<ShaderVariant> Variants()
    {
        // USEOIT tracks ShaderProgramBase.Oit, which defaults to true for every
        // program; only the second Entityanimated registration turns it off. The
        // shaders that call into oit.fsh do so unconditionally, so a global
        // USEOIT 0 is not a configuration the client can produce.
        yield return new ShaderVariant
        {
            Name = "everything-off",
            WavingStuff = 0, FoamEffect = 0, ShinyEffect = 0,
        };
        yield return new ShaderVariant
        {
            Name = "everything-on",
            Fxaa = 1, SsaoLevel = 2, Bloom = 1, GodRays = 2, ShadowQuality = 2,
            DynLights = 8, UseSsbo = 1, NormalView = 1, GreedyMesh = 1, MinBright = 0.1f,
        };
        yield return new ShaderVariant
        {
            Name = "ssao-only",
            SsaoLevel = 1, DynLights = 4,
        };
        yield return new ShaderVariant
        {
            Name = "shadows-and-ssbo",
            ShadowQuality = 2, DynLights = 8, UseSsbo = 1,
        };
        // TAA on, without the SSAO G-buffer (motion at attachment 2) and with it
        // (attachment 4): the motion-vector writers only exist in these, and the
        // two rows move the output location the same way the client does.
        yield return new ShaderVariant
        {
            Name = "taa-no-ssao",
            TaaMotion = 1, TaaMotionLocation = 2,
        };
        yield return new ShaderVariant
        {
            Name = "taa-with-ssao",
            SsaoLevel = 2, DynLights = 4, TaaMotion = 1, TaaMotionLocation = 4,
        };
    }

    /// <summary>
    /// Reproduces registerDefaultShaderCodePrefixes, including Optimum's own
    /// greedy-mesh defines from the ShaderRegistry patch.
    /// </summary>
    public static string PrefixFor(EnumShaderType stage, ShaderVariant variant)
    {
        var lines = new List<string>();

        if (stage == EnumShaderType.FragmentShader)
        {
            lines.Add($"#define FXAA {variant.Fxaa}");
            lines.Add($"#define SSAOLEVEL {variant.SsaoLevel}");
            lines.Add($"#define NORMALVIEW {variant.NormalView}");
            lines.Add($"#define BLOOM {variant.Bloom}");
            lines.Add($"#define GODRAYS {variant.GodRays}");
            lines.Add($"#define FOAMEFFECT {variant.FoamEffect}");
            lines.Add($"#define SHINYEFFECT {variant.ShinyEffect}");
            lines.Add($"#define SHADOWQUALITY {variant.ShadowQuality}");
            lines.Add($"#define DYNLIGHTS {variant.DynLights}");
            lines.Add($"#define USEOIT {variant.UseOit}");
            lines.Add($"#define GREEDYMESH {variant.GreedyMesh}");
            lines.Add($"#define GREEDYMESH_GRAD 0");
            lines.Add($"#define TAAMOTION {variant.TaaMotion}");
            lines.Add($"#define TAAMOTIONLOCATION {variant.TaaMotionLocation}");
        }
        else
        {
            lines.Add($"#define USESSBO {variant.UseSsbo}");
            lines.Add($"#define WAVINGSTUFF {variant.WavingStuff}");
            lines.Add($"#define FOAMEFFECT {variant.FoamEffect}");
            lines.Add($"#define SSAOLEVEL {variant.SsaoLevel}");
            lines.Add($"#define NORMALVIEW {variant.NormalView}");
            lines.Add($"#define SHINYEFFECT {variant.ShinyEffect}");
            lines.Add($"#define GODRAYS {variant.GodRays}");
            lines.Add($"#define MINBRIGHT {variant.MinBright.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"#define SHADOWQUALITY {variant.ShadowQuality}");
            lines.Add($"#define DYNLIGHTS {variant.DynLights}");
            lines.Add($"#define MAXANIMATEDELEMENTS {variant.MaxAnimatedElements}");
            lines.Add($"#define GREEDYMESH {variant.GreedyMesh}");
            lines.Add($"#define TAAMOTION {variant.TaaMotion}");
            lines.Add($"#define TAAMOTIONLOCATION {variant.TaaMotionLocation}");
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Builds the two stages of one program, ready for translation.</summary>
    public static List<ShaderStageSource> BuildProgram(
        string programName,
        Dictionary<string, string> files,
        Dictionary<string, string> includes,
        ShaderVariant variant)
    {
        var stages = new List<ShaderStageSource>();

        foreach ((string extension, EnumShaderType stage) in new[]
        {
            (".vsh", EnumShaderType.VertexShader),
            (".fsh", EnumShaderType.FragmentShader),
            (".gsh", EnumShaderType.GeometryShader),
        })
        {
            if (!files.TryGetValue(programName + extension, out string? code)) continue;

            stages.Add(new ShaderStageSource
            {
                Stage = stage,
                Code = ExpandIncludes(code, includes),
                PrefixCode = PrefixFor(stage, variant),
                Filename = programName + extension,
            });
        }

        return stages;
    }
}
