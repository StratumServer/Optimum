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
    public const int CurrentSchemaVersion = 1;
    public const string ReportFileName = "shader-compatibility.json";

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
        ("enumrenderstage", "RenderHook")
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

        FinalizeReport(report);
        return report;
    }

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
        FinalizeReport(report);
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
        if (isAssembly) AddIndicators(ReadTextBytes(bytes), indicators);
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
                if (isAssembly) AddIndicators(ReadTextBytes(bytes), indicators);
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

    private static void AddIndicators(string text, HashSet<string> indicators)
    {
        string lower = text.ToLowerInvariant();
        foreach ((string token, string name) in IndicatorTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal)) indicators.Add(name);
        }
    }

    private static void FinalizeReport(ShaderCompatibilityReport report)
    {
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

        if (report.ScanFailed)
        {
            foreach (string feature in ShaderFeatures)
                AddFeatureDecision(report, feature, true, "scanner failure: conservative fallback");
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
                return null;
            }
        }

        string extension = Path.GetExtension(shader);
        if (extension is not ".fsh" and not ".vsh" and not ".gsh") return null;
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
