using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Zaldaryon.Pharos.Bootstrap;
using Zaldaryon.Pharos.Core;
using Zaldaryon.Pharos.XUnit;
using Xunit;

[Collection("OptimumSvgGpuDiagnostic")]
public sealed class SvgGpuProbe : ClientScenarioBase
{
    protected override HeadlessClientOptions ClientOptions => new()
    {
        Width = 640,
        Height = 480,
        GameInstallPath = RequiredEnvironment("VINTAGE_STORY"),
        DataPath = RequiredEnvironment("OPTIMUM_PHAROS_DATA_PATH"),
        BootMode = ClientBootMode.Engine,
        ConfigureMesaEnvironment = false,
        DisableAudio = true
    };

    [ClientScenario]
    public void PublicLoadSvgMatchesNativeAndRejectsUnsupportedCases()
    {
        string dataPath = Path.GetFullPath(RequiredEnvironment("OPTIMUM_PHAROS_DATA_PATH"));
        string reportPath = Path.GetFullPath(RequiredEnvironment("OPTIMUM_PHAROS_REPORT_PATH"));
        string certificatePath = Path.Combine(dataPath, ".optimum", "gui-certification.json");
        string evidencePath = Path.Combine(dataPath, ".optimum", "gui-certification-evidence.json");
        var cases = new List<object>();
        var metadata = new Dictionary<string, object?>();
        bool completed = false;
        bool cleanupPassed = false;
        bool reportPathSafe = false;
        try
        {
            string canonicalReportPath = CanonicalPath(reportPath);
            reportPathSafe = !PathsEqual(CanonicalPath(certificatePath), canonicalReportPath)
                && !PathsEqual(CanonicalPath(evidencePath), canonicalReportPath);
            Assert.True(reportPathSafe, "Report path must not alias a certification file.");
            Assert.False(File.Exists(certificatePath), "Use a disposable datapath without an existing GUI certificate.");
            Assert.False(File.Exists(evidencePath), "Use a disposable datapath without existing GUI evidence.");
            metadata["os"] = RuntimeInformation.OSDescription;
            metadata["architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
            metadata["candidateCommit"] = Environment.GetEnvironmentVariable("OPTIMUM_CANDIDATE_COMMIT");
            metadata["pharosCommit"] = Environment.GetEnvironmentVariable("PHAROS_COMMIT");

            Client!.RunOnClientThread(() =>
            {
                PropertyInfo? metricsEnabled = null;
                FieldInfo? gpuEnabled = null;
                object? originalGpuEnabled = null;
                object? originalMetricsEnabled = null;
                MethodInfo? revoke = null;
                Type? probe = null;
                try
                {
                    string vendor = GL.GetString(StringName.Vendor) ?? "";
                    string renderer = GL.GetString(StringName.Renderer) ?? "";
                    string version = GL.GetString(StringName.Version) ?? "";
                    GL.GetInteger(GetPName.MajorVersion, out int major);
                    GL.GetInteger(GetPName.MinorVersion, out int minor);
                    GL.GetInteger(GetPName.ContextProfileMask, out int profile);
                    GL.GetInteger(GetPName.ContextFlags, out int flags);
                    Assert.NotEmpty(vendor);
                    Assert.NotEmpty(renderer);

                    Assembly contracts = Assembly.Load("Optimum.Api.Contracts");
                    Type config = RequiredType(contracts, "Vintagestory.API.Config.OptimumConfig");
                    probe = RequiredType(contracts, "Vintagestory.API.Client.OptimumGuiGpuProbe");
                    Type metrics = RequiredType(contracts, "Vintagestory.API.Config.OptimumGuiMetrics");
                    Assert.False(IsSoftwareRenderer(probe, renderer), $"Software renderer is not adapter evidence: {renderer}");

                    metadata["vendor"] = vendor;
                    metadata["renderer"] = renderer;
                    metadata["glVersion"] = version;
                    metadata["context"] = new { major, minor, profile, flags };
                    metadata["runtimeAssemblies"] = RuntimeAssemblyHashes(contracts);
                    metadata["diagnosticOnly"] = true;
                    metadata["qualificationIsInMemory"] = true;
                    metadata["certificateIssued"] = false;

                    MethodInfo qualify = RequiredMethod(config, "SetGuiGpuCertificationPassed", BindingFlags.NonPublic | BindingFlags.Static);
                    metricsEnabled = RequiredProperty(metrics, "Enabled");
                    MethodInfo resetMetrics = RequiredMethod(metrics, "Reset", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo snapshotMetrics = RequiredMethod(metrics, "Snapshot", BindingFlags.Public | BindingFlags.Static);
                    gpuEnabled = config.GetField("GuiGpuRenderEnabled")
                        ?? throw new InvalidOperationException("OptimumConfig.GuiGpuRenderEnabled is missing.");
                    revoke = RequiredMethod(config, "RevokeGuiGpuCertification", BindingFlags.NonPublic | BindingFlags.Static);
                    originalGpuEnabled = gpuEnabled.GetValue(null);
                    originalMetricsEnabled = metricsEnabled.GetValue(null);
                    metricsEnabled.SetValue(null, true);

                    var loader = new SvgLoader(Client.Client.api);
                    qualify.Invoke(null, [true, vendor, renderer, version, major, minor, profile, flags]);
                    gpuEnabled.SetValue(null, true);
                    string? probeResult = OptimumGuiGpuProbeBackend.TryRunOnce(RuntimeThreadId());
                    metadata["gpuProbe"] = probeResult;
                    Assert.True(RequiredProperty(probe, "GuiRenderingEnabled").GetValue(null) is true,
                        "The exact live GL identity did not enable the in-memory diagnostic path.");

                    IAsset supportedAsset = Asset(
                        "<rect width='24' height='32' fill='#3585cd'/><rect x='24' width='24' height='32' fill='#d7396b'/>");
                    foreach ((int textureWidth, int textureHeight) in new[] { (48, 32), (72, 48), (96, 64) })
                    {
                        gpuEnabled.SetValue(null, false);
                        using LoadedTexture nativeTexture = loader.LoadSvg(supportedAsset, textureWidth, textureHeight, textureWidth, textureHeight, null);
                        byte[] expected = ReadTexture(nativeTexture.TextureId, textureWidth, textureHeight);

                        gpuEnabled.SetValue(null, true);
                        resetMetrics.Invoke(null, null);
                        using LoadedTexture candidate = loader.LoadSvg(supportedAsset, textureWidth, textureHeight, textureWidth, textureHeight, null);
                        byte[] actual = ReadTexture(candidate.TextureId, textureWidth, textureHeight);
                        object snapshot = snapshotMetrics.Invoke(null, null)!;
                        AssertGpuCounts(snapshot, 1, 1, 0);
                        int maxDelta = ComparePixels(expected, actual, maximumDelta: 1);
                        cases.Add(new
                        {
                            path = "SvgLoader.LoadSvg",
                            textureWidth,
                            textureHeight,
                            supported = true,
                            attempts = 1,
                            successes = 1,
                            fallbacks = 0,
                            maxChannelDelta = maxDelta,
                            passed = true
                        });
                    }

                    foreach ((string name, string shape, int? tint) in new[]
                    {
                    ("alpha", "<rect width='48' height='32' fill='#3585cd' fill-opacity='.5'/>", (int?)null),
                    ("tint", "<rect width='48' height='32' fill='#3585cd'/>", (int?)unchecked((int)0xb73585cd)),
                    ("gradient", "<defs><linearGradient id='g'><stop offset='0' stop-color='#3585cd'/><stop offset='1' stop-color='#d7396b'/></linearGradient></defs><rect width='48' height='32' fill='url(#g)'/>", (int?)null),
                    ("stroke", "<rect width='48' height='32' fill='#3585cd' stroke='#d7396b' stroke-width='2'/>", (int?)null)
                })
                    {
                        IAsset unsupportedAsset = Asset(shape);
                        gpuEnabled.SetValue(null, false);
                        using LoadedTexture nativeFallback = loader.LoadSvg(unsupportedAsset, 48, 32, 48, 32, tint);
                        byte[] expected = ReadTexture(nativeFallback.TextureId, 48, 32);

                        gpuEnabled.SetValue(null, true);
                        resetMetrics.Invoke(null, null);
                        using LoadedTexture fallback = loader.LoadSvg(unsupportedAsset, 48, 32, 48, 32, tint);
                        byte[] actual = ReadTexture(fallback.TextureId, 48, 32);
                        object snapshot = snapshotMetrics.Invoke(null, null)!;
                        AssertGpuCounts(snapshot, 0, 0, 0);
                        Assert.Equal(expected, actual);
                        cases.Add(new
                        {
                            path = "SvgLoader.LoadSvg",
                            name,
                            supported = false,
                            attempts = 0,
                            successes = 0,
                            fallbacks = 0,
                            exactNativeBytes = true,
                            passed = true
                        });
                    }
                    completed = true;
                }
                finally
                {
                    try
                    {
                        if (gpuEnabled is not null) gpuEnabled.SetValue(null, false);
                        revoke?.Invoke(null, null);
                        bool revoked = probe is not null && RequiredProperty(probe, "GuiRenderingEnabled").GetValue(null) is false;
                        metadata["qualificationRevoked"] = revoked;
                        Assert.True(revoked, "The in-memory GPU qualification must be revoked after diagnostics.");
                        Assert.False(File.Exists(certificatePath), "The diagnostic must not issue a certificate.");
                        Assert.False(File.Exists(evidencePath), "The diagnostic must not issue certification evidence.");
                        cleanupPassed = true;
                    }
                    finally
                    {
                        if (gpuEnabled is not null && originalGpuEnabled is not null) gpuEnabled.SetValue(null, originalGpuEnabled);
                        if (metricsEnabled is not null && originalMetricsEnabled is not null) metricsEnabled.SetValue(null, originalMetricsEnabled);
                        Assert.True(IsSafeReportPath(reportPath, certificatePath, evidencePath),
                            "Report path became a certification file path during the run.");
                        WriteReport(reportPath, metadata, cases, completed && cleanupPassed);
                    }
                }
            });
        }
        catch
        {
            if (reportPathSafe)
            {
                try
                {
                    reportPathSafe = IsSafeReportPath(reportPath, certificatePath, evidencePath);
                }
                catch
                {
                    reportPathSafe = false;
                }

                if (reportPathSafe) WriteReport(reportPath, metadata, cases, passed: false);
            }

            throw;
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} must be set to a disposable test path.");

    private static string CanonicalPath(string path) => CanonicalPath(Path.GetFullPath(path), new HashSet<string>(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    private static string CanonicalPath(string fullPath, HashSet<string> visitedLinks)
    {
        string root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException($"Path has no root: {fullPath}");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(segment => segment.Length > 0))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            string? linkTarget = info.LinkTarget;
            if (linkTarget is null)
            {
                current = next;
                continue;
            }

            string linkPath = Path.GetFullPath(next);
            if (!visitedLinks.Add(linkPath)) throw new IOException($"Symbolic link cycle while resolving {fullPath}.");
            string target = Path.IsPathRooted(linkTarget)
                ? linkTarget
                : Path.Combine(Path.GetDirectoryName(linkPath)!, linkTarget);
            current = CanonicalPath(Path.GetFullPath(target), visitedLinks);
        }

        return Path.GetFullPath(current);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSafeReportPath(string reportPath, string certificatePath, string evidencePath)
    {
        string canonicalReportPath = CanonicalPath(reportPath);
        return !PathsEqual(CanonicalPath(certificatePath), canonicalReportPath)
            && !PathsEqual(CanonicalPath(evidencePath), canonicalReportPath);
    }

    private static Type RequiredType(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException($"Required runtime type is missing: {name}");

    private static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags) =>
        type.GetMethod(name, flags) ?? throw new InvalidOperationException($"Required runtime method is missing: {type.FullName}.{name}");

    private static PropertyInfo RequiredProperty(Type type, string name) =>
        type.GetProperty(name) ?? throw new InvalidOperationException($"Required runtime property is missing: {type.FullName}.{name}");

    private static bool IsSoftwareRenderer(Type probeType, string renderer)
    {
        MethodInfo method = RequiredMethod(probeType, "IsSoftwareRenderer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return method.Invoke(null, [renderer]) is not false;
    }

    private static int RuntimeThreadId()
    {
        Type? type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(assembly =>
        {
            try { return assembly.GetTypes(); }
            catch { return Array.Empty<Type>(); }
        }).FirstOrDefault(candidate => candidate.Name == "RuntimeEnv");
        FieldInfo? field = type?.GetField("MainThreadId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        PropertyInfo? property = type?.GetProperty("MainThreadId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return (int)(field?.GetValue(null) ?? property?.GetValue(null)
            ?? throw new InvalidOperationException("RuntimeEnv.MainThreadId is missing."));
    }

    private static Dictionary<string, string> RuntimeAssemblyHashes(Assembly contracts)
    {
        var assemblies = new Dictionary<string, Assembly?>
        {
            ["api"] = typeof(ICoreClientAPI).Assembly,
            ["game"] = typeof(SvgLoader).Assembly,
            ["contracts"] = contracts,
            ["pharos"] = typeof(ClientScenarioBase).Assembly,
            ["opentkGraphics"] = typeof(GL).Assembly,
            ["cairoSharp"] = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "cairo-sharp"),
            ["skiaSharp"] = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "SkiaSharp")
        };
        return assemblies.Where(pair => pair.Value is not null && !pair.Value.IsDynamic && File.Exists(pair.Value.Location))
            .ToDictionary(pair => pair.Key, pair => Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(pair.Value!.Location))));
    }

    private static void AssertGpuCounts(object snapshot, long attempts, long successes, long fallbacks)
    {
        Type type = snapshot.GetType();
        Assert.Equal(attempts, (long)(RequiredProperty(type, "GpuCandidateAttemptCount").GetValue(snapshot)
            ?? throw new InvalidOperationException("Attempt count is null.")));
        Assert.Equal(successes, (long)(RequiredProperty(type, "GpuCandidateSuccessCount").GetValue(snapshot)
            ?? throw new InvalidOperationException("Success count is null.")));
        Assert.Equal(fallbacks, (long)(RequiredProperty(type, "GpuCandidateFallbackCount").GetValue(snapshot)
            ?? throw new InvalidOperationException("Fallback count is null.")));
    }

    private static IAsset Asset(string shape, int width = 48, int height = 32) => new Asset(Encoding.UTF8.GetBytes(
        $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'>{shape}</svg>"),
        new AssetLocation("game:pharos-svg-diagnostic.svg"), null);

    private static byte[] ReadTexture(int texture, int width, int height)
    {
        Assert.Equal(ErrorCode.NoError, GL.GetError());
        GL.GetInteger(GetPName.TextureBinding2D, out int oldTexture);
        GL.GetInteger(GetPName.PixelPackBufferBinding, out int oldBuffer);
        GL.GetInteger(GetPName.PackAlignment, out int oldAlignment);
        GL.GetInteger(GetPName.PackRowLength, out int oldRowLength);
        GL.GetInteger(GetPName.PackSkipRows, out int oldSkipRows);
        GL.GetInteger(GetPName.PackSkipPixels, out int oldSkipPixels);
        byte[] data = new byte[checked(width * height * 4)];
        try
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
            Assert.Equal(ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, oldTexture);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, oldBuffer);
            GL.PixelStore(PixelStoreParameter.PackAlignment, oldAlignment);
            GL.PixelStore(PixelStoreParameter.PackRowLength, oldRowLength);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, oldSkipRows);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, oldSkipPixels);
        }
        GL.GetInteger(GetPName.TextureBinding2D, out int restoredTexture);
        GL.GetInteger(GetPName.PixelPackBufferBinding, out int restoredBuffer);
        GL.GetInteger(GetPName.PackAlignment, out int restoredAlignment);
        GL.GetInteger(GetPName.PackRowLength, out int restoredRowLength);
        GL.GetInteger(GetPName.PackSkipRows, out int restoredSkipRows);
        GL.GetInteger(GetPName.PackSkipPixels, out int restoredSkipPixels);
        Assert.Equal(oldTexture, restoredTexture);
        Assert.Equal(oldBuffer, restoredBuffer);
        Assert.Equal(oldAlignment, restoredAlignment);
        Assert.Equal(oldRowLength, restoredRowLength);
        Assert.Equal(oldSkipRows, restoredSkipRows);
        Assert.Equal(oldSkipPixels, restoredSkipPixels);
        Assert.Equal(ErrorCode.NoError, GL.GetError());
        return data;
    }

    private static int ComparePixels(byte[] expected, byte[] actual, int maximumDelta)
    {
        Assert.Equal(expected.Length, actual.Length);
        int worst = 0;
        for (int index = 0; index < expected.Length; index++)
        {
            int delta = Math.Abs(expected[index] - actual[index]);
            worst = Math.Max(worst, delta);
            Assert.InRange(delta, 0, maximumDelta);
        }
        return worst;
    }

    private static void WriteReport(string path, Dictionary<string, object?> metadata, List<object> cases, bool passed)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var report = new { schemaVersion = 1, status = passed ? "passed" : "failed", metadata, cases };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
