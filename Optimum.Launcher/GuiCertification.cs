using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimum.Launcher;

/// <summary>A local approval of exact runtime/profile inputs, not a signature or a universal mod guarantee.</summary>
internal sealed record GuiCertificate(
    int SchemaVersion, string Policy, string ProfileId, string RuntimeIdentifier,
    string RuntimeFingerprint, string EvidenceSha256);

internal sealed record GuiCertificationDecision(bool Passed, string Reason, GuiDeviceIdentity? DeviceIdentity = null);

internal static class GuiCertification
{
    internal const string Policy = "exact-local-gui-v1";
    internal const string CertificateFile = "gui-certification.json";
    internal const string EvidenceFile = "gui-certification-evidence.json";
    internal const string ApprovedGpuDecision = "skia-no-cairo-fallback";
    private const int MaximumJsonBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static GuiCertificationDecision Validate(string gameDir, string cacheDir, string dataPath, string[] args,
        Func<int, int, GuiDeviceIdentity?>? deviceProbe = null)
    {
        try
        {
            string directory = Path.Combine(dataPath, ".optimum");
            string path = Path.Combine(directory, CertificateFile);
            if (!File.Exists(path)) return new(false, "certificate-missing");
            EnsureContainedWithoutLinks(path, dataPath);
            byte[] certificateBytes = ReadBounded(path);
            using (var certificateJson = JsonDocument.Parse(certificateBytes)) RejectDuplicateProperties(certificateJson.RootElement);
            var certificate = JsonSerializer.Deserialize<GuiCertificate>(certificateBytes, JsonOptions);
            if (certificate is null || certificate.SchemaVersion != 1 || certificate.Policy != Policy ||
                certificate.ProfileId != Policy || certificate.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
                return new(false, "certificate-profile-unsupported");
            string evidencePath = Path.Combine(directory, EvidenceFile);
            EnsureContainedWithoutLinks(evidencePath, dataPath);
            byte[] evidenceBytes = ReadBounded(evidencePath);
            if (!HashEquals(certificate.EvidenceSha256, SHA256.HashData(evidenceBytes)))
                return new(false, "evidence-hash-mismatch");
            using var evidence = JsonDocument.Parse(evidenceBytes);
            var root = evidence.RootElement;
            RejectDuplicateProperties(root);
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("scenarioId").GetString() != "optimum-gui-inventory-capture-v1" ||
                root.GetProperty("gpuDecision").GetString() != ApprovedGpuDecision ||
                root.GetProperty("gpuDecisionReasons").GetArrayLength() != 0 ||
                !root.GetProperty("materializationObserverRegistration").GetProperty("passed").GetBoolean())
                return new(false, "evidence-not-approved");
            if (root.GetProperty("qualificationMode").GetString() != "pharos-current-context")
                return new(false, "evidence-qualification-mode-unsupported");
            if (!HashEquals(certificate.RuntimeFingerprint,
                    Convert.FromHexString(root.GetProperty("runtimeProfileFingerprint").GetString() ?? "")))
                return new(false, "evidence-profile-fingerprint-mismatch");
            var observer = root.GetProperty("materializationObserverRegistration");
            if (!observer.GetProperty("observerRegistered").GetBoolean() ||
                observer.GetProperty("attemptCount").GetInt64() != 0 ||
                observer.GetProperty("successCount").GetInt64() != 0 ||
                observer.GetProperty("failureCount").GetInt64() != 0 ||
                observer.GetProperty("causes").EnumerateObject().Any() ||
                !IsCommit(root.GetProperty("optimumCommit").GetString()) ||
                !IsCommit(root.GetProperty("pharosCommit").GetString()))
                return new(false, "evidence-observer-or-commit-incomplete");
            var sentinel = root.GetProperty("glStateSentinel");
            if (!sentinel.GetProperty("candidateAndProbeRestoredCapturedState").GetBoolean() ||
                !sentinel.GetProperty("samplerBindingsSupported").GetBoolean())
                return new(false, "evidence-gl-state-incomplete");
            var provenance = root.GetProperty("runtimeProvenance");
            if (!provenance.GetProperty("Complete").GetBoolean() ||
                !provenance.GetProperty("SamplerBindingsSupported").GetBoolean() ||
                new[] { "GlVendor", "GlRenderer", "GlVersion" }.Any(name =>
                    string.IsNullOrWhiteSpace(provenance.GetProperty(name).GetString())))
                return new(false, "evidence-provenance-incomplete");
            if (!provenance.TryGetProperty("GlContextProfileMask", out var profileMaskElement) ||
                !profileMaskElement.TryGetInt32(out int evidenceProfileMask) ||
                !provenance.TryGetProperty("GlContextFlags", out var contextFlagsElement) ||
                !contextFlagsElement.TryGetInt32(out int evidenceContextFlags))
                return new(false, "evidence-context-profile-incomplete");
            if (!root.GetProperty("initialGuiComposition").GetProperty("evidenceComplete").GetBoolean() ||
                !root.GetProperty("initialGuiComposition").GetProperty("measuredFromBeforeFirstClientFramesThroughInventoryOpenAndSettle").GetBoolean() ||
                !CleanWindow(root.GetProperty("initialGuiComposition"), false) ||
                !CleanWindow(root.GetProperty("recompositionBenchmark"), false))
                return new(false, "evidence-window-incomplete");
            var scales = root.GetProperty("guiScaleFixtures");
            var expectedScales = new HashSet<decimal> { 1m, 1.5m, 2m };
            if (scales.GetArrayLength() != 3 || scales.EnumerateArray().Any(scale =>
                    !expectedScales.Remove(scale.GetProperty("scale").GetDecimal()) ||
                    scale.GetProperty("gpuDecision").GetString() != ApprovedGpuDecision ||
                    scale.GetProperty("gpuDecisionReasons").GetArrayLength() != 0 || !CleanWindow(scale, true)))
                return new(false, "evidence-scales-incomplete");
            var nonSubset = root.GetProperty("gpuNonSubsetFixtureDiagnostics");
            var svg = root.GetProperty("gpuSvgDiagnostics");
            if (root.GetProperty("gpuPrimitiveFixture").GetString() != "GpuRendered" ||
                !root.GetProperty("gpuPrimitiveFixtureParityWithinOneChannelValue").GetBoolean() ||
                Enumerable.Range(1, 5).Any(index => nonSubset.GetProperty("fixture-" + index).GetString() != "GpuRendered") ||
                svg.GetArrayLength() != 3 || svg.EnumerateArray().Any(row =>
                    row.GetProperty("rasterizerKind").GetString() != "skia-gpu-candidate" ||
                    !row.GetProperty("parityWithinOneChannelValue").GetBoolean() ||
                    row.GetProperty("materializationAttemptsAfterLoad").GetInt64() != row.GetProperty("materializationAttemptsBefore").GetInt64() ||
                    row.GetProperty("gpuCandidateAttemptsDuringLoad").GetInt64() <= 0))
                return new(false, "evidence-fixtures-incomplete");
            if (!ValidateProvenance(provenance, gameDir, cacheDir))
                return new(false, "evidence-runtime-provenance-mismatch");
            if (!HashEquals(certificate.RuntimeFingerprint,
                    Convert.FromHexString(ComputeFingerprint(gameDir, cacheDir, dataPath, args))))
                return new(false, "runtime-profile-mismatch");
            if (!TryGetRequestedContext(dataPath, out int requestedMajor, out int requestedMinor))
                return new(false, "client-gl-context-setting-invalid");
            GuiDeviceIdentity? identity = (deviceProbe ?? GuiDevicePreflight.Probe)(requestedMajor, requestedMinor);
            if (identity is null) return new(false, "local-device-preflight-unavailable");
            string vendor = provenance.GetProperty("GlVendor").GetString() ?? "";
            string renderer = provenance.GetProperty("GlRenderer").GetString() ?? "";
            string version = provenance.GetProperty("GlVersion").GetString() ?? "";
            if (!string.Equals(identity.Vendor, vendor, StringComparison.Ordinal) ||
                !string.Equals(identity.Renderer, renderer, StringComparison.Ordinal) ||
                !string.Equals(identity.Version, version, StringComparison.Ordinal) ||
                identity.ProfileMask != evidenceProfileMask || identity.ContextFlags != evidenceContextFlags ||
                !TryParseGlContextVersion(version, out int evidenceMajor, out int evidenceMinor) ||
                identity.Major != evidenceMajor || identity.Minor != evidenceMinor)
                return new(false, "local-device-identity-mismatch");
            return new(true, "local-device-preflight-matched", identity);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NotSupportedException)
        {
            return new(false, "certificate-invalid:" + ex.GetType().Name);
        }
    }

    private static bool TryGetRequestedContext(string dataPath, out int major, out int minor)
    {
        major = 4;
        minor = 3;
        string settings = Path.Combine(dataPath, "clientsettings.json");
        if (!File.Exists(settings)) return true;
        using var json = JsonDocument.Parse(ReadBounded(settings));
        if (!json.RootElement.TryGetProperty("stringSettings", out var stringSettings) ||
            !stringSettings.TryGetProperty("glContextVersion", out var context)) return true;
        string? value = context.GetString();
        if (value is null) return false;
        string[] parts = value.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out major) &&
            int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out minor) && major is >= 3 and <= 9 && minor is >= 0 and <= 9 &&
            (major > 3 || minor >= 2);
    }

    private static bool TryParseGlContextVersion(string value, out int major, out int minor)
    {
        major = minor = 0;
        int dot = value.IndexOf('.');
        if (dot < 1) return false;
        int start = dot - 1;
        while (start > 0 && char.IsDigit(value[start - 1])) start--;
        int end = dot + 1;
        while (end < value.Length && char.IsDigit(value[end])) end++;
        return int.TryParse(value.AsSpan(start, dot - start), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out major) &&
            int.TryParse(value.AsSpan(dot + 1, end - dot - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out minor);
    }

    // Hash inventories as well as bytes so added/removed mods, configs, assets and native libraries invalidate approval.
    internal static string ComputeFingerprint(string gameDir, string cacheDir, string dataPath, string[] args)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddFiles(entries, "game", gameDir, recursive: false);
        foreach (string folder in new[] { "Lib", "Mods", "assets" })
            AddFiles(entries, "game/" + folder, Path.Combine(gameDir, folder), recursive: true);
        AddFiles(entries, "cache", cacheDir, recursive: true);
        foreach (string folder in new[] { "Mods", "ModConfig", "assets" })
            AddFiles(entries, "data/" + folder, Path.Combine(dataPath, folder), recursive: true);
        string settings = Path.Combine(dataPath, "clientsettings.json");
        entries["data/clientsettings.json"] = File.Exists(settings) ? HashFile(settings) : "missing";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
            hash.AppendData(bytes);
        }
        Append(Policy);
        Append(RuntimeInformation.RuntimeIdentifier);
        string[] normalizedArgs = NormalizeLaunchArguments(args);
        Append(normalizedArgs.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string argument in normalizedArgs) Append(argument);
        Append("inputs");
        foreach (var entry in entries) { Append(entry.Key); Append(entry.Value); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string[] NormalizeLaunchArguments(string[] args)
    {
        var normalized = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--dataPath", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                normalized.Add("--dataPath");
                normalized.Add("<data-path>");
                i++;
            }
            else if (args[i].StartsWith("--dataPath=", StringComparison.OrdinalIgnoreCase))
                normalized.Add("--dataPath=<data-path>");
            else normalized.Add(args[i]);
        }
        return normalized.ToArray();
    }

    private static bool IsCommit(string? value) => value is { Length: >= 40 and <= 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidateProvenance(JsonElement provenance, string gameDir, string cacheDir)
    {
        string stagePath = provenance.GetProperty("StagePath").GetString() ?? "";
        if (!Path.IsPathFullyQualified(stagePath) || !Directory.Exists(stagePath)) return false;
        EnsureContainedWithoutLinks(stagePath, Path.GetPathRoot(stagePath)!);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VintagestoryLib"] = Path.Combine(cacheDir, "VintagestoryLib.dll"),
            ["VintagestoryAPI"] = Path.Combine(cacheDir, "VintagestoryAPI.dll"),
            ["Cairo"] = ResolveDependency("cairo-sharp.dll"),
            ["SkiaSharp"] = ResolveDependency("SkiaSharp.dll")
        };
        string libDir = Path.Combine(gameDir, "Lib");
        if (Directory.Exists(libDir)) EnsureContainedWithoutLinks(libDir, gameDir);
        var nativeCandidates = Directory.Exists(libDir)
            ? Directory.EnumerateFiles(libDir, "libSkiaSharp*", SearchOption.TopDirectoryOnly).ToArray()
            : Array.Empty<string>();
        if (nativeCandidates.Length == 0) return false;
        string ResolveDependency(string name)
        {
            foreach (string directory in new[] { cacheDir, gameDir, Path.Combine(gameDir, "Lib") })
                if (File.Exists(Path.Combine(directory, name))) return Path.Combine(directory, name);
            throw new FileNotFoundException("Required certified dependency is missing: " + name);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matchedRuntimeArtifacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in provenance.GetProperty("Artifacts").EnumerateArray())
        {
            string name = artifact.GetProperty("Name").GetString() ?? "";
            if (!seen.Add(name) || !artifact.GetProperty("InsideStage").GetBoolean()) return false;
            string path = artifact.GetProperty("Path").GetString() ?? "";
            EnsureContainedWithoutLinks(path, stagePath);
            byte[] artifactHash = Convert.FromHexString(HashFile(path));
            if (!HashEquals(artifact.GetProperty("Sha256").GetString(), artifactHash)) return false;
            if (expected.TryGetValue(name, out string? required))
            {
                if (!HashEquals(artifact.GetProperty("Sha256").GetString(), Convert.FromHexString(HashFile(required)))) return false;
                matchedRuntimeArtifacts.Add(name);
            }
            else if (name == "SkiaSharp native")
            {
                if (!nativeCandidates.Any(candidate =>
                    HashEquals(artifact.GetProperty("Sha256").GetString(), Convert.FromHexString(HashFile(candidate))))) return false;
                matchedRuntimeArtifacts.Add(name);
            }
        }
        return expected.Keys.All(matchedRuntimeArtifacts.Contains) && matchedRuntimeArtifacts.Contains("SkiaSharp native") &&
            seen.Contains("Optimum.Pharos.Gui.Tests");
    }

    private static bool CleanWindow(JsonElement window, bool scale)
    {
        long attempts = window.GetProperty("gpuCandidateAttempts").GetInt64();
        long successes = window.GetProperty("gpuCandidateSuccesses").GetInt64();
        long fallbacks = window.GetProperty("gpuCandidateFallbacks").GetInt64();
        string suffix = scale ? "Count" : "s";
        var causes = window.GetProperty("materializationCauses");
        var fallbackReasons = window.GetProperty("candidateFallbackReasons");
        var rendering = window.GetProperty("renderingDiagnostics");
        long materializationAttempts = window.GetProperty("materializationAttempt" + suffix).GetInt64();
        long materializationSuccesses = window.GetProperty("materializationSuccess" + (scale ? "Count" : "es")).GetInt64();
        long materializationFailures = window.GetProperty("materializationFailure" + suffix).GetInt64();
        // A certificate permits GPU candidates and explicit Skia CPU recovery.
        // Cairo materialization and every unrecognized fallback remain denied.
        return causes.EnumerateObject().Count() == 0 && HasOnlySkiaCpuFallbacks(fallbackReasons) &&
            materializationAttempts == 0 && materializationSuccesses == 0 && materializationFailures == 0 &&
            attempts > 0 && fallbacks == 0 && successes == attempts &&
            rendering.GetProperty("MaterializationAttemptCount").GetInt64() == materializationAttempts &&
            rendering.GetProperty("MaterializationSuccessCount").GetInt64() == materializationSuccesses &&
            rendering.GetProperty("MaterializationFailureCount").GetInt64() == materializationFailures;
    }

    private static bool HasOnlySkiaCpuFallbacks(JsonElement fallbackReasons)
    {
        foreach (var reason in fallbackReasons.EnumerateObject())
        {
            if (reason.Name != "skia-cpu-fallback" || reason.Value.ValueKind != JsonValueKind.Number ||
                !reason.Value.TryGetInt64(out long count) || count <= 0)
                return false;
        }

        return true;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate certification/evidence property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private static void AddFiles(SortedDictionary<string, string> entries, string prefix, string directory, bool recursive)
    {
        if (!Directory.Exists(directory)) { entries[prefix + "/"] = "missing"; return; }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked runtime/profile directories are not supported by certification.");
        foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked runtime/profile inputs are not supported by certification.");
            string key = prefix + "/" + Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                if (recursive) AddFiles(entries, key, path, true);
            }
            else
            {
                // Cache metadata is not loaded. Game root logs/settings are mutable and are not runtime inputs.
                string extension = Path.GetExtension(path);
                if (prefix == "game" && extension is not (".dll" or ".exe" or ".json" or ".so" or ".dylib")) continue;
                if (prefix == "cache" && Path.GetFileName(path) == "manifest.json") continue;
                entries[key] = HashFile(path);
            }
        }
    }

    private static void EnsureContainedWithoutLinks(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Certification input is outside the staged profile.");
        string current = fullRoot;
        foreach (string part in new[] { "" }.Concat(relative.Split(Path.DirectorySeparatorChar)))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked certification inputs are not supported.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HashEquals(string? expected, byte[] actual) =>
        expected is { Length: 64 } && Convert.FromHexString(expected).AsSpan().SequenceEqual(actual);

    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumJsonBytes) throw new InvalidDataException("Certification JSON exceeds its size limit.");
        using var memory = new MemoryStream();
        // Read at most limit+1 even if a file grows during validation.
        byte[] buffer = new byte[8192];
        while (memory.Length <= MaximumJsonBytes)
        {
            int count = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumJsonBytes + 1 - (int)memory.Length));
            if (count == 0) return memory.ToArray();
            memory.Write(buffer, 0, count);
        }
        throw new InvalidDataException("Certification JSON exceeds its size limit.");
    }
}
