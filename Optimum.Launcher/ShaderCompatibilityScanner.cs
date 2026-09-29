using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimum.Launcher;

/// <summary>
/// Performs a metadata-only scan of client mods. The scanner never loads a
/// managed assembly and never chooses a replacement shader.
/// </summary>
public static class ShaderCompatibilityScanner
{
    /// <summary>
    /// 2 (2026-09-15): adds <see cref="ShaderCompatibilityReport.ShaderAssetOverrides" />,
    /// <see cref="ShaderCompatibilityReport.RewriterPrograms" />, the PlatformInternals
    /// indicator and <see cref="ShaderCompatibilityReport.OpenGlRequired" />. The launcher
    /// rescans and overwrites the report on every start; <see cref="LoadReport" />
    /// refuses any other version, so a v1 file is never read as if it carried the v2
    /// verdicts.
    /// </summary>
    public const int CurrentSchemaVersion = 2;
    public const string ReportFileName = "shader-compatibility.json";

    /// <summary>
    /// The single entry <see cref="ShaderCompatibilityReport.RewriterPrograms" /> holds
    /// when every program has to take the rewriter: a shaderincludes override, or a
    /// report the scanner could not complete.
    /// </summary>
    public const string AllPrograms = "all";

    /// <summary>
    /// Program base names the client registers from <c>assets/game/shaders</c>
    /// (vanilla plus the Optimum overrides and stages that ship there). A mod file
    /// <c>assets/&lt;domain&gt;/shaders/&lt;name&gt;.vsh|.fsh|.gsh</c> whose stem is one of
    /// these replaces that program's source, so the Vulkan path must build it through
    /// the rewriter from the mod's GLSL instead of linking the native SPIR-V. The scan
    /// also adds every stem it finds in the installed game's own shaders directory.
    /// </summary>
    private static readonly string[] BuiltInPrograms =
    [
        "aurora", "autocamera", "bilateralblur", "blit", "blockhighlights", "blur", "celestialobject",
        "chunkliquid", "chunkliquiddepth", "chunkliquidmotion", "chunkopaque", "chunkshadowmap", "chunktopsoil",
        "chunktransparent", "cloudmap", "clouds", "cloudvolumetric", "colorgrade", "debugdepthbuffer", "decals",
        "entityanimated", "final", "findbright", "fsr-easu", "fsr-rcas", "godrays", "gui", "guigear", "guitopsoil",
        "helditem", "instanced", "lines", "luma", "nightsky", "optimum-map", "particlescube", "particlesquad",
        "particlesquad2d", "scene-ssao", "shadowmapentityanimated", "sky", "ssao", "standard", "taa-debug",
        "taa-resolve", "taa-sharpen", "taa-skymotion", "texture2texture", "transparentcompose", "ui-compose",
        "upscale-ssao", "wireframe", "woittest"
    ];

    /// <summary>Program base names the scanner treats as overridable without an installed game.</summary>
    public static IReadOnlyList<string> KnownPrograms => BuiltInPrograms;

    /// <summary>
    /// Platform graphics types a Harmony patch can target. The Vulkan backend replaces
    /// the platform (<c>VulkanClientPlatform</c> overrides the graphics members) and
    /// links programs itself, so a prefix/postfix on one of these members either never
    /// runs or runs against state the Vulkan path does not use.
    /// </summary>
    private static readonly string[] PlatformInternalTypes =
    [
        "ClientPlatformWindows", "ShaderProgramBase"
    ];

    private static readonly byte[][] PlatformInternalTypesUtf16 =
        [.. PlatformInternalTypes.Select(Encoding.Unicode.GetBytes)];

    private static readonly string[] ShaderFeatures =
    [
        "GreedyMesh", "RenderScale", "GodRaysSampleCap", "EntityLightBatch",
        "EntityShaderStateCache", "Oit", "MapPageCache", "ShaderPreprocessParallel"
    ];

    private static readonly (string Token, string Name)[] IndicatorTokens =
    [
        ("shaderregistry", "ShaderRegistry"),
        ("loadshader", "LoadShader"),
        ("shaderprogram", "ShaderProgram"),
        ("harmony", "Harmony"),
        ("registerrenderer", "RenderHook"),
        ("onrenderframe", "RenderHook"),
        ("enumrenderstage", "RenderHook"),
        ("opentk.graphics", "RawOpenGL")
    ];

    private static readonly string[] OptimumBuiltInMods =
    [
        "VSEssentials.dll",
        "VSSurvivalMod.dll"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ShaderCompatibilityReport Scan(string dataPath, string gameDir, string optimumVersion)
    {
        var report = new ShaderCompatibilityReport
        {
            SchemaVersion = CurrentSchemaVersion,
            OptimumVersion = optimumVersion ?? "dev",
            GameVersionPath = SafeRelativePath(dataPath, gameDir)
        };

        try
        {
            var scannedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string modsPath in new[] { Path.Combine(dataPath, "Mods"), Path.Combine(gameDir, "Mods") })
            {
                string fullModsPath = Path.GetFullPath(modsPath);
                if (!scannedRoots.Add(fullModsPath) || !Directory.Exists(fullModsPath)) continue;

                foreach (string entry in Directory.EnumerateFileSystemEntries(fullModsPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        ScanSource(entry, dataPath, gameDir, report);
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        MarkFailed(report, SafeRelativePath(dataPath, entry), ex);
                    }
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            MarkFailed(report, "Mods", ex);
        }

        FinalizeReport(report, InstalledPrograms(gameDir));
        return report;
    }

    /// <summary>
    /// Reads a saved report, or null when there is none, it cannot be parsed, or it was
    /// written under another schema version. A v1 report lacks the v2 verdicts
    /// (rewriter programs, PlatformInternals routing), so it is invalidated rather than
    /// migrated: its absence of a verdict must not read as "nothing found".
    /// </summary>
    public static ShaderCompatibilityReport? LoadReport(string dataPath)
    {
        string path = Path.Combine(dataPath, ".optimum", ReportFileName);
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                return null;
            }
            return document.RootElement.Deserialize<ShaderCompatibilityReport>(JsonOptions);
        }
        catch (Exception ex) when (IsRecoverable(ex) || ex is JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> InstalledPrograms(string gameDir)
    {
        var programs = new HashSet<string>(BuiltInPrograms, StringComparer.OrdinalIgnoreCase);
        try
        {
            string shaders = Path.Combine(gameDir, "assets", "game", "shaders");
            if (!Directory.Exists(shaders)) return programs;
            foreach (string file in Directory.EnumerateFiles(shaders))
            {
                if (IsStageExtension(Path.GetExtension(file)))
                    programs.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // The built-in list still covers every program this build ships.
        }
        return programs;
    }

    private static bool IsStageExtension(string extension) =>
        extension.Equals(".fsh", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".vsh", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gsh", StringComparison.OrdinalIgnoreCase);

    public static string SaveReport(string dataPath, ShaderCompatibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string directory = Path.Combine(dataPath, ".optimum");
        string path = Path.Combine(directory, ReportFileName);
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
            return path;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    public static ShaderCompatibilityReport CreateConservativeReport(string optimumVersion, string reason)
    {
        var report = new ShaderCompatibilityReport
        {
            SchemaVersion = CurrentSchemaVersion,
            OptimumVersion = optimumVersion ?? "dev",
            ScanFailed = true
        };
        report.ScanErrors.Add(reason ?? "shader compatibility scan failed");
        foreach (string feature in ShaderFeatures)
        {
            report.DisabledFeatures.Add(feature);
            report.FeatureReasons[feature] = ["scanner failure: conservative fallback"];
        }
        FinalizeReport(report, null);
        return report;
    }

    private static void ScanSource(string sourcePath, string dataPath, string gameDir, ShaderCompatibilityReport report)
    {
        if (IsOptimumSource(sourcePath, gameDir)) return;

        var source = new ShaderModSource
        {
            Id = MakeSourceId(sourcePath, dataPath),
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = SafeRelativePath(dataPath, sourcePath),
            Kind = Directory.Exists(sourcePath) ? "directory" : "archive"
        };
        var shaderFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(sourcePath))
        {
            foreach (string file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    ScanDirectoryFile(sourcePath, file, shaderFiles, indicators, source, report);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    MarkFailed(report, SafeRelativePath(dataPath, file), ex);
                }
            }
        }
        else if (File.Exists(sourcePath) && IsArchive(sourcePath))
        {
            ScanArchive(sourcePath, shaderFiles, indicators, source, report);
        }
        else if (File.Exists(sourcePath))
        {
            ScanFile(sourcePath, Path.GetFileName(sourcePath), shaderFiles, indicators, source, report);
        }

        if (source.ModId?.Equals("optimum", StringComparison.OrdinalIgnoreCase) == true ||
            source.Name.Equals("Optimum", StringComparison.OrdinalIgnoreCase)) return;

        if (shaderFiles.Count == 0 && indicators.Count == 0 && source.ModId == null && source.Name == Path.GetFileNameWithoutExtension(sourcePath))
            return;

        source.ShaderFiles = shaderFiles.Order(StringComparer.OrdinalIgnoreCase).ToList();
        source.Indicators = indicators.Order(StringComparer.OrdinalIgnoreCase).ToList();
        source.SourceFingerprint = ComputeSourceFingerprint(source);
        report.Sources.Add(source);
        foreach (string shader in source.ShaderFiles)
        {
            if (!report.ShaderOwners.TryGetValue(shader, out List<string>? owners))
            {
                owners = [];
                report.ShaderOwners[shader] = owners;
            }
            if (!owners.Contains(source.Id, StringComparer.OrdinalIgnoreCase)) owners.Add(source.Id);
        }
    }

    private static void ScanDirectoryFile(string root, string file, HashSet<string> shaders, HashSet<string> indicators, ShaderModSource source, ShaderCompatibilityReport report)
    {
        string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
        ScanFile(file, relativePath, shaders, indicators, source, report);
    }

    private static void ScanFile(string file, string relativePath, HashSet<string> shaders, HashSet<string> indicators, ShaderModSource source, ShaderCompatibilityReport report)
    {
        string normalized = relativePath.Replace('\\', '/');
        string? shader = NormalizeShaderPath(normalized);
        if (shader != null) shaders.Add(shader);

        bool isModInfo = normalized.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase);
        bool isAssembly = normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (shader == null && !isModInfo && !isAssembly) return;

        byte[] bytes = File.ReadAllBytes(file);
        RecordContentHash(source, normalized, bytes);
        if (isModInfo) ReadModInfo(ReadTextBytes(bytes), source);
        if (isAssembly) AddIndicators(bytes, indicators);
    }

    private static void ScanArchive(string archivePath, HashSet<string> shaders, HashSet<string> indicators, ShaderModSource source, ShaderCompatibilityReport report)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
            {
                string relativePath = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string? shader = NormalizeShaderPath(relativePath);
                if (shader != null) shaders.Add(shader);
                bool isModInfo = relativePath.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase);
                bool isAssembly = relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (shader == null && !isModInfo && !isAssembly) continue;

                using Stream stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                byte[] bytes = memory.ToArray();
                RecordContentHash(source, relativePath, bytes);
                if (isModInfo) ReadModInfo(ReadTextBytes(bytes), source);
                if (isAssembly) AddIndicators(bytes, indicators);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            MarkFailed(report, SafeRelativePath(Path.GetDirectoryName(archivePath) ?? string.Empty, archivePath), ex);
        }
    }

    private static void ReadModInfo(string json, ShaderModSource source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            source.ModId = GetString(root, "modid") ?? GetString(root, "id");
            source.Name = GetString(root, "name") ?? source.Name;
            source.Version = GetString(root, "version");
            if (!string.IsNullOrWhiteSpace(source.ModId)) source.Id = source.ModId!;
        }
        catch (JsonException)
        {
            source.ModInfoReadFailed = true;
        }
    }

    private static void AddIndicators(byte[] bytes, HashSet<string> indicators)
    {
        string lower = ReadTextBytes(bytes).ToLowerInvariant();
        foreach ((string token, string name) in IndicatorTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal)) indicators.Add(name);
        }

        // PlatformInternals: the assembly references Harmony and names a platform
        // graphics type. Type and member references, and the type names serialized
        // into [HarmonyPatch(typeof(...))] attribute blobs, are UTF-8 in metadata;
        // AccessTools.Method("...ClientPlatformWindows:RenderMesh") is a user string,
        // which the #US heap stores as UTF-16. Both forms count. The other indicators
        // keep their UTF-8-only match so their verdicts do not move.
        if (!indicators.Contains("Harmony")) return;
        bool namesPlatform =
            PlatformInternalTypes.Any(type => lower.Contains(type.ToLowerInvariant(), StringComparison.Ordinal)) ||
            PlatformInternalTypesUtf16.Any(pattern => bytes.AsSpan().IndexOf(pattern) >= 0);
        if (namesPlatform) indicators.Add("PlatformInternals");
    }

    private static void FinalizeReport(ShaderCompatibilityReport report, HashSet<string>? installedPrograms)
    {
        AddShaderAssetOverrides(report, installedPrograms);

        // A Harmony patch on a platform graphics member is not honoured on Vulkan:
        // VulkanClientPlatform overrides those members and links programs itself,
        // so the patched GL body never runs. Like RawOpenGL this routes the session
        // to OpenGL, and like it the decision is not a ShaderFeature, so a scan
        // failure alone never forces it.
        report.OpenGlRequiredBy = report.Sources
            .Where(x => x.Indicators.Contains("RawOpenGL") || x.Indicators.Contains("PlatformInternals"))
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddFeatureDecision(report, "Vulkan", report.Sources.Any(x => x.Indicators.Contains("PlatformInternals")),
            "a mod Harmony-patches a platform graphics member (ClientPlatformWindows or ShaderProgramBase), which the Vulkan backend does not honour");

        foreach (List<string> owners in report.ShaderOwners.Values)
            owners.Sort(StringComparer.OrdinalIgnoreCase);

        foreach ((string shader, List<string> owners) in report.ShaderOwners.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool managedByOptimum = IsOptimumShader(shader);
            if (owners.Count > 1 || managedByOptimum)
            {
                var conflict = new ShaderCompatibilityConflict
                {
                    Shader = shader,
                    Owners = [.. owners],
                    Reason = managedByOptimum
                        ? "external mod owns an Optimum shader path; Optimum will not replace it"
                        : "multiple external mods claim the same shader path"
                };
                report.Conflicts.Add(conflict);
            }
        }

        AddFeatureDecision(report, "GreedyMesh", HasExternalShader(report, "chunkopaque.fsh") || HasExternalShader(report, "chunkopaque.vsh"),
            "greedy ABI requires both chunkopaque vertex and fragment shader contracts");
        AddFeatureDecision(report, "RenderScale", HasExternalShader(report, "final.fsh"),
            "external final shader owns the post-process contract");
        AddFeatureDecision(report, "GodRaysSampleCap", HasExternalShader(report, "godrays.fsh"),
            "external godrays shader owns the sample-count contract");
        bool externalShaderHooks = report.Sources.Any(x => x.Indicators.Any(IsShaderHookIndicator));
        bool externalShaderAssets = report.Sources.Any(x => x.ShaderFiles.Count > 0);
        AddFeatureDecision(report, "ShaderPreprocessParallel", externalShaderAssets || externalShaderHooks,
            "external shader assets or shader hooks can depend on load order");
        AddFeatureDecision(report, "EntityLightBatch", externalShaderHooks,
            "external render hooks can observe entity-light update ordering");
        AddFeatureDecision(report, "EntityShaderStateCache", externalShaderHooks,
            "external render hooks can observe entity shader state");
        AddFeatureDecision(report, "Oit", externalShaderHooks || HasExternalShader(report, "cloudvolumetric.fsh"),
            "external render or cloud shader owns the OIT integration point");
        AddFeatureDecision(report, "MapPageCache", externalShaderAssets || externalShaderHooks,
            "external shader assets or shader hooks can dispose registered programs during reload");

        // "Vulkan" is a backend decision, not a shader feature, and is deliberately
        // absent from ShaderFeatures: a scanner failure must not silently veto a
        // backend the user explicitly asked for. GLSL that mods ship is fine - the
        // translator compiles arbitrary sources - but a mod issuing GL calls itself
        // has no path through a Vulkan device.
        bool rawOpenGl = report.Sources.Any(x => x.Indicators.Contains("RawOpenGL"));
        AddFeatureDecision(report, "Vulkan", rawOpenGl,
            "a mod calls OpenGL directly, which the Vulkan backend cannot serve");

        // "Taa" is deliberately absent from ShaderFeatures for the same reason as
        // "Vulkan": OptimumConfig.EffectiveTaa consults IsFeatureExplicitlyDisabled,
        // so a missing scan must not silently veto a renderer feature the user
        // asked for - only an explicit verdict does.
        //
        // An external copy of any shader Optimum's motion-vector writers live in,
        // or of the vertexwarp include they evaluate twice, replaces the writer
        // with one that emits nothing to the motion attachment. The resolve would
        // then reproject those pixels by camera motion alone while everything
        // around them used real vectors, which is worse than not running TAA.
        bool externalMotionShader =
            HasExternalShader(report, "chunkopaque.vsh") || HasExternalShader(report, "chunkopaque.fsh") ||
            HasExternalShader(report, "chunktopsoil.vsh") || HasExternalShader(report, "chunktopsoil.fsh") ||
            HasExternalShader(report, "entityanimated.vsh") || HasExternalShader(report, "entityanimated.fsh") ||
            HasExternalShader(report, "standard.vsh") || HasExternalShader(report, "standard.fsh") ||
            HasExternalShader(report, "instanced.vsh") || HasExternalShader(report, "instanced.fsh") ||
            // The liquid velocity pass re-draws the liquid pools through its own
            // program and has to land on exactly the surface chunkliquid.vsh
            // shaded; an external copy of either file breaks that agreement, and
            // its vectors would then be rejected or - worse - accepted for a
            // surface half a pixel away.
            HasExternalShader(report, "chunkliquid.vsh") ||
            HasExternalShader(report, "chunkliquidmotion.vsh") || HasExternalShader(report, "chunkliquidmotion.fsh") ||
            // Cube particles write the motion attachment themselves, and the
            // OIT merge is where every transparent that cannot write it gets its
            // reactive value. An external copy of either drops that content back
            // to camera reprojection with no reactive flag at all, which ghosts
            // exactly the fast-moving, alpha-blended pixels TAA is worst at.
            HasExternalShader(report, "particlescube.vsh") || HasExternalShader(report, "particlescube.fsh") ||
            HasExternalShader(report, "transparentcompose.fsh") ||
            // A decal that no longer writes the attachment leaves the block's
            // vector behind a depth the decal itself moved, which the resolve
            // rejects; and an external sky-motion pass would decide the reactive
            // policy for every cloud pixel in the frame.
            HasExternalShader(report, "decals.vsh") || HasExternalShader(report, "decals.fsh") ||
            // Every stage Optimum owns outright: the resolve itself, the debug
            // views, the sky-motion pass and the post-resolve sharpen. An
            // external copy of any of them is not a writer that emits nothing,
            // it is a replacement resolve running against a contract (MRT
            // layout, history formats, jitter and reactive semantics) it cannot
            // know. The prefix rule covers taa-* files added after this line was
            // written, so a new stage cannot ship without a scanner rule.
            HasExternalShader(report, "taa-resolve.vsh") || HasExternalShader(report, "taa-resolve.fsh") ||
            HasExternalShader(report, "taa-debug.vsh") || HasExternalShader(report, "taa-debug.fsh") ||
            HasExternalShader(report, "taa-skymotion.vsh") || HasExternalShader(report, "taa-skymotion.fsh") ||
            HasExternalShader(report, "taa-sharpen.vsh") || HasExternalShader(report, "taa-sharpen.fsh") ||
            HasExternalShaderPrefix(report, "taa-") ||
            // The sharpen pass is an RCAS variant and shares its vertex stage and
            // lobe maths with the FSR1 pair, which is also what render scale
            // resolves through: an external copy leaves TAA sharpening either
            // doubled with FSR's own tap or gone.
            HasExternalShader(report, "fsr-rcas.vsh") || HasExternalShader(report, "fsr-rcas.fsh") ||
            HasExternalShader(report, "fsr-easu.vsh") || HasExternalShader(report, "fsr-easu.fsh") ||
            // Not just vertexwarp.vsh: ShaderRegistry merges every shaderinclude
            // into one dictionary that all the motion writers compile against, so
            // an external file anywhere in that directory can redefine a helper
            // the writers call - and unlike a shader, an include has no program
            // of its own to point the blame at.
            HasExternalShader(report, "vertexwarp.vsh") ||
            HasExternalShaderInclude(report);
        AddFeatureDecision(report, "Taa", externalMotionShader,
            "external shader owns a motion-vector writer contract");

        if (report.ScanFailed)
        {
            foreach (string feature in ShaderFeatures)
                AddFeatureDecision(report, feature, true, "scanner failure: conservative fallback");
            // A scan that did not finish cannot say which programs a mod replaced,
            // so none of them may link the native SPIR-V over a mod's source.
            report.RewriterPrograms = [AllPrograms];
        }

        report.Fingerprint = ComputeFingerprint(report);
    }

    private static void AddFeatureDecision(ShaderCompatibilityReport report, string feature, bool disabled, string reason)
    {
        if (!disabled) return;
        if (!report.DisabledFeatures.Contains(feature, StringComparer.OrdinalIgnoreCase)) report.DisabledFeatures.Add(feature);
        if (!report.FeatureReasons.TryGetValue(feature, out List<string>? reasons))
        {
            reasons = [];
            report.FeatureReasons[feature] = reasons;
        }
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }

    private static bool HasExternalShader(ShaderCompatibilityReport report, string fileName)
    {
        return report.ShaderOwners.Keys.Any(path =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when any external shader file name starts with <paramref name="prefix" />.
    /// Optimum's own stages share the "taa-" prefix, so a stage added later is
    /// covered without touching the feature decision.
    /// </summary>
    private static bool HasExternalShaderPrefix(ShaderCompatibilityReport report, string prefix)
    {
        return report.ShaderOwners.Keys.Any(path =>
            Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when any external file lands in the shaderincludes directory.
    /// NormalizeShaderPath keeps those under a "shaderincludes/" prefix.
    /// </summary>
    private static bool HasExternalShaderInclude(ShaderCompatibilityReport report)
    {
        return report.ShaderOwners.Keys.Any(path =>
            path.Replace('\\', '/').Contains("shaderincludes/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One finding per external shader asset that replaces a known program's stage
    /// by file name, or any shaderincludes file. The union lands in
    /// <see cref="ShaderCompatibilityReport.RewriterPrograms" />: sorted program base
    /// names, or the single entry <see cref="AllPrograms" /> when an include was
    /// overridden, because ShaderRegistry merges every include into the one dictionary
    /// all programs compile against and an include has no program of its own.
    /// A mod shader with a new name is not an override: it has no native blob and
    /// takes the rewriter anyway.
    /// </summary>
    private static void AddShaderAssetOverrides(ShaderCompatibilityReport report, HashSet<string>? installedPrograms)
    {
        var known = installedPrograms ?? new HashSet<string>(BuiltInPrograms, StringComparer.OrdinalIgnoreCase);
        var programs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool all = false;
        report.ShaderAssetOverrides.Clear();
        foreach ((string asset, List<string> owners) in report.ShaderOwners.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            string normalized = asset.Replace('\\', '/');
            ShaderAssetOverride finding;
            if (normalized.Contains("shaderincludes/", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
                finding = new ShaderAssetOverride { Asset = asset, Programs = [AllPrograms] };
            }
            else
            {
                string program = Path.GetFileNameWithoutExtension(normalized).ToLowerInvariant();
                if (!known.Contains(program)) continue;
                programs.Add(program);
                finding = new ShaderAssetOverride { Asset = asset, Programs = [program] };
            }
            finding.Owners = [.. owners];
            report.ShaderAssetOverrides.Add(finding);
        }
        report.RewriterPrograms = all ? [AllPrograms] : [.. programs];
    }

    private static bool IsShaderHookIndicator(string indicator) =>
        indicator is "ShaderRegistry" or "LoadShader" or "ShaderProgram" or "Harmony" or "RenderHook";

    private static bool IsOptimumShader(string shader) =>
        shader is "assets/game/shaders/chunkliquid.fsh" or "assets/game/shaders/chunkopaque.fsh" or
        "assets/game/shaders/chunkopaque.vsh" or "assets/game/shaders/final.fsh" or
        "assets/game/shaders/godrays.fsh" or "assets/game/shaders/cloudvolumetric.fsh";

    private static bool IsOptimumSource(string path, string gameDir)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Equals("Optimum", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Optimum-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string builtInModsPath = Path.GetFullPath(Path.Combine(gameDir, "Mods"));
            return OptimumBuiltInMods.Any(mod =>
                string.Equals(fullPath, Path.Combine(builtInModsPath, mod), StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? NormalizeShaderPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        int marker = normalized.IndexOf("assets/game/shaders/", StringComparison.OrdinalIgnoreCase);
        string shader;
        bool isInclude = false;
        if (marker >= 0)
        {
            shader = normalized[marker..];
        }
        else
        {
            marker = normalized.IndexOf("/shaders/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                shader = normalized[(marker + 1)..];
            }
            else if (normalized.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
            {
                shader = normalized;
            }
            else
            {
                // Optimum: shaderincludes is a first-class asset category that
                // ShaderRegistry merges into the same include dictionary as
                // shaders, so an external vertexwarp.vsh replaces Optimum's copy
                // exactly the way an external chunkopaque.vsh would - and with it
                // the WarpState overloads the motion-vector writers evaluate.
                marker = normalized.IndexOf("/shaderincludes/", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    shader = normalized[(marker + 1)..];
                    isInclude = true;
                }
                else if (normalized.StartsWith("shaderincludes/", StringComparison.OrdinalIgnoreCase))
                {
                    shader = normalized;
                    isInclude = true;
                }
                else
                {
                    return null;
                }
            }
        }

        // Optimum: the stage-extension filter is only meaningful for shaders/,
        // where a file is a vertex, fragment or geometry stage. ShaderRegistry
        // loads every shaderinclude regardless of extension - vanilla ships five
        // .ash includes next to the .fsh/.vsh ones - so an external .ash override
        // replaces a helper the motion writers compile against just the same.
        string extension = Path.GetExtension(shader);
        if (isInclude)
        {
            // Any real file counts; a directory entry (no extension) does not.
            if (extension.Length == 0) return null;
        }
        else if (extension is not ".fsh" and not ".vsh" and not ".gsh")
        {
            return null;
        }

        return shader.ToLowerInvariant();
    }

    private static string ReadTextBytes(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string MakeSourceId(string path, string dataPath)
    {
        string relative = SafeRelativePath(dataPath, path);
        string stem = Path.GetFileNameWithoutExtension(relative).Replace(' ', '-').ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..10];
        return $"{stem}-{hash}";
    }

    private static string SafeRelativePath(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or
        NotSupportedException or System.Security.SecurityException;

    private static void MarkFailed(ShaderCompatibilityReport report, string path, Exception ex)
    {
        report.ScanFailed = true;
        report.ScanErrors.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
    }

    private static string ComputeFingerprint(ShaderCompatibilityReport report)
    {
        var builder = new StringBuilder();
        builder.Append(report.SchemaVersion).Append('|').Append(report.OptimumVersion).Append('|').Append(report.ScanFailed);
        foreach (ShaderModSource source in report.Sources.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(source.Id).Append('|').Append(source.SourceFingerprint);
            foreach (string shader in source.ShaderFiles) builder.Append('|').Append(shader);
            foreach (string indicator in source.Indicators) builder.Append('|').Append(indicator);
        }
        foreach ((string shader, List<string> owners) in report.ShaderOwners.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append('|').Append(shader).Append(':').Append(string.Join(',', owners));
        foreach (string feature in report.DisabledFeatures.Order(StringComparer.OrdinalIgnoreCase)) builder.Append('|').Append(feature);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ComputeSourceFingerprint(ShaderModSource source)
    {
        string value = string.Join('|', new[]
        {
            source.Id, source.Name, source.ModId, source.Version, source.SourcePath,
            string.Join(',', source.ShaderFiles), string.Join(',', source.Indicators),
            string.Join(',', source.ContentHashes.Order(StringComparer.OrdinalIgnoreCase))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void RecordContentHash(ShaderModSource source, string path, byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        source.ContentHashes.Add($"{path}={hash}");
    }
}

public sealed class ShaderCompatibilityReport
{
    public int SchemaVersion { get; set; }
    public string OptimumVersion { get; set; } = "dev";
    public string GameVersionPath { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public bool ScanFailed { get; set; }
    public List<ShaderModSource> Sources { get; set; } = [];
    public Dictionary<string, List<string>> ShaderOwners { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ShaderCompatibilityConflict> Conflicts { get; set; } = [];
    public List<string> DisabledFeatures { get; set; } = [];
    public Dictionary<string, List<string>> FeatureReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ScanErrors { get; set; } = [];

    /// <summary>External assets that replace a known program's stage or a shaderinclude.</summary>
    public List<ShaderAssetOverride> ShaderAssetOverrides { get; set; } = [];

    /// <summary>
    /// What the Vulkan runtime consumes: program base names that must be built through
    /// the rewriter from the (mod) GLSL instead of the native SPIR-V, sorted; or the
    /// single entry <see cref="ShaderCompatibilityScanner.AllPrograms" />. Empty when
    /// every program may link natively.
    /// </summary>
    public List<string> RewriterPrograms { get; set; } = [];

    /// <summary>Sources that route the session to OpenGL (RawOpenGL or PlatformInternals).</summary>
    public List<string> OpenGlRequiredBy { get; set; } = [];

    /// <summary>True when the scan explicitly vetoes the Vulkan backend.</summary>
    public bool OpenGlRequired => DisabledFeatures.Contains("Vulkan", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// ShaderAssetOverride finding: <see cref="Asset" /> (normalized path, e.g.
/// <c>assets/mymod/shaders/chunkopaque.fsh</c>) replaces the listed programs'
/// source; <see cref="Programs" /> is <c>["all"]</c> for a shaderincludes file.
/// </summary>
public sealed class ShaderAssetOverride
{
    public string Asset { get; set; } = string.Empty;
    public List<string> Programs { get; set; } = [];
    public List<string> Owners { get; set; } = [];
}

public sealed class ShaderModSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ModId { get; set; }
    public string? Version { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public bool ModInfoReadFailed { get; set; }
    public List<string> ShaderFiles { get; set; } = [];
    public List<string> Indicators { get; set; } = [];

    [JsonIgnore]
    public List<string> ContentHashes { get; } = [];
}

public sealed class ShaderCompatibilityConflict
{
    public string Shader { get; set; } = string.Empty;
    public List<string> Owners { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
}
