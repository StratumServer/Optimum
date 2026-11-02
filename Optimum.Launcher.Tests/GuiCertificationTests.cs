using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class GuiCertificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "optimum-certificate-tests", Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(root, "game");
    private string Cache => Path.Combine(root, "cache");
    private string Data => Path.Combine(root, "data");
    private string Metadata => Path.Combine(Data, ".optimum");

    public GuiCertificationTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Metadata);
        File.WriteAllText(Path.Combine(Game, "Vintagestory.dll"), "fixture");
    }

    [Fact]
    public void MissingCertificateSelectsVanillaWithoutThrowing() =>
        Assert.Equal(new GuiCertificationDecision(false, "certificate-missing"), Validate());

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1}")]
    [InlineData("{\"Unknown\":true}")]
    public void InvalidCertificateSelectsVanilla(string json)
    {
        File.WriteAllText(Path.Combine(Metadata, GuiCertification.CertificateFile), json);
        Assert.False(Validate().Passed);
    }

    [Fact]
    public void NegativeAmdEvidenceCannotApproveSkia()
    {
        WriteCertificate("""
            {"schemaVersion":1,"scenarioId":"optimum-gui-inventory-capture-v1",
             "gpuDecision":"vanilla","gpuDecisionReasons":["native-materialization-attempted"],
             "materializationObserverStartupSmoke":{"passed":true}}
            """);
        Assert.Equal(new GuiCertificationDecision(false, "evidence-not-approved"), Validate());
    }

    [Fact]
    public void EvidenceTamperingInvalidatesCertificate()
    {
        WriteCertificate("{}");
        File.AppendAllText(Path.Combine(Metadata, GuiCertification.EvidenceFile), " ");
        Assert.Equal("evidence-hash-mismatch", Validate().Reason);
    }

    [Fact]
    public void IncompletePositiveLabelCannotApproveSkia()
    {
        WriteCertificate("""
            {"schemaVersion":1,"scenarioId":"optimum-gui-inventory-capture-v1",
             "gpuDecision":"skia-with-subpixel-cairo-fallback","gpuDecisionReasons":[],
             "materializationObserverStartupSmoke":{"passed":true}}
            """);
        Assert.False(Validate().Passed);
    }

    [Fact]
    public void FingerprintBindsContentInventorySettingsAndArguments()
    {
        string baseline = Fingerprint();
        Assert.Equal(baseline, Fingerprint());
        Assert.NotEqual(baseline, GuiCertification.ComputeFingerprint(Game, Cache, Data, ["--connect", "fixture"]));
        Directory.CreateDirectory(Path.Combine(Data, "Mods"));
        File.WriteAllText(Path.Combine(Data, "Mods", "fixture.zip"), "first");
        string withMod = Fingerprint();
        Assert.NotEqual(baseline, withMod);
        File.WriteAllText(Path.Combine(Data, "Mods", "fixture.zip"), "other");
        Assert.NotEqual(withMod, Fingerprint());
        File.WriteAllText(Path.Combine(Data, "clientsettings.json"), "{}");
        string withSettings = Fingerprint();
        File.WriteAllText(Path.Combine(Cache, "VintagestoryAPI.dll"), "changed");
        Assert.NotEqual(withSettings, Fingerprint());
    }

    [Fact]
    public void OversizeCertificateIsRejected()
    {
        File.WriteAllText(Path.Combine(Metadata, GuiCertification.CertificateFile), new string(' ', 1024 * 1024 + 1));
        Assert.False(Validate().Passed);
    }

    [Fact]
    public void OtherwiseCompleteCertificateRequiresExactLiveDeviceAndContextMatch()
    {
        WriteCertificate(CompleteSyntheticEvidence());
        var identity = new GuiDeviceIdentity("synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0);
        Assert.Equal(new GuiCertificationDecision(true, "local-device-preflight-matched", identity), Validate(identity));
        Assert.Equal("local-device-identity-mismatch", Validate(identity with { Renderer = "other renderer" }).Reason);
        Assert.Equal("local-device-identity-mismatch", Validate(identity with { Minor = 5 }).Reason);
        Assert.Equal("local-device-identity-mismatch", Validate(identity with { ProfileMask = 1 }).Reason);
        Assert.Equal("local-device-identity-mismatch", Validate(identity with { ContextFlags = 1 }).Reason);
        Assert.Equal(new GuiCertificationDecision(false, "local-device-preflight-unavailable"), Validate(null));
    }

    [Fact]
    public void CertificateRejectsPreviouslyAllowedSubpixelCairoFallbacks()
    {
        var evidence = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        void AddFallback(System.Text.Json.Nodes.JsonNode window, bool scale)
        {
            window["gpuCandidateAttempts"] = 2;
            window["gpuCandidateSuccesses"] = 1;
            window["gpuCandidateFallbacks"] = 1;
            string attempt = scale ? "materializationAttemptCount" : "materializationAttempts";
            string success = scale ? "materializationSuccessCount" : "materializationSuccesses";
            string failure = scale ? "materializationFailureCount" : "materializationFailures";
            window[attempt] = 1;
            window[success] = 1;
            window[failure] = 0;
            window["materializationCauses"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Recorder.ShowText.Unsupported:text-antialias-Subpixel"] = 1
            };
            window["candidateFallbackReasons"] = new System.Text.Json.Nodes.JsonObject
            {
                ["surface-not-recording:NativeMaterialized:first-trigger=Recorder.ShowText.Unsupported:text-antialias-Subpixel"] = 1
            };
            window["renderingDiagnostics"]!["MaterializationAttemptCount"] = 1;
            window["renderingDiagnostics"]!["MaterializationSuccessCount"] = 1;
            window["renderingDiagnostics"]!["MaterializationFailureCount"] = 0;
        }

        AddFallback(evidence["initialGuiComposition"]!, false);
        AddFallback(evidence["recompositionBenchmark"]!, false);
        foreach (var scale in evidence["guiScaleFixtures"]!.AsArray()) AddFallback(scale!, true);
        WriteCertificate(evidence.ToJsonString());
        Assert.False(Validate(new GuiDeviceIdentity("synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0)).Passed);
    }

    [Fact]
    public void CertificateAcceptsSkiaCpuRecoveryWithoutCairoMaterialization()
    {
        var evidence = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        void AddRecovery(System.Text.Json.Nodes.JsonNode window) =>
            window["candidateFallbackReasons"] = new System.Text.Json.Nodes.JsonObject
            {
                ["skia-cpu-fallback"] = 1
            };

        AddRecovery(evidence["initialGuiComposition"]!);
        AddRecovery(evidence["recompositionBenchmark"]!);
        foreach (var scale in evidence["guiScaleFixtures"]!.AsArray()) AddRecovery(scale!);
        WriteCertificate(evidence.ToJsonString());

        var identity = new GuiDeviceIdentity("synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0);
        Assert.Equal(new GuiCertificationDecision(true, "local-device-preflight-matched", identity), Validate(identity));
    }

    [Fact]
    public void CertificateRejectsUnknownCandidateFallbackReasons()
    {
        var evidence = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        evidence["initialGuiComposition"]!["candidateFallbackReasons"] = new System.Text.Json.Nodes.JsonObject
        {
            ["surface-not-recording:NativeMaterialized"] = 1
        };
        WriteCertificate(evidence.ToJsonString());

        var identity = new GuiDeviceIdentity("synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0);
        Assert.Equal("evidence-window-incomplete", Validate(identity).Reason);
    }

    [Fact]
    public void EvidenceMustCarryTheProfileFingerprintFromTheSuccessfulRun()
    {
        var evidence = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        evidence["runtimeProfileFingerprint"] = new string('0', 64);
        WriteCertificate(evidence.ToJsonString());
        Assert.Equal("evidence-profile-fingerprint-mismatch", Validate(new GuiDeviceIdentity(
            "synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0)).Reason);
    }

    [Fact]
    public void CachedRuntimeChangeInvalidatesOtherwiseCompleteCertificate()
    {
        WriteCertificate(CompleteSyntheticEvidence());
        File.AppendAllText(Path.Combine(Game, "Vintagestory.dll"), "changed");
        Assert.Equal(new GuiCertificationDecision(false, "runtime-profile-mismatch"), Validate());
    }

    [Fact]
    public void PositiveLabelDoesNotOverrideNonzeroMaterializations()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        node["recompositionBenchmark"]!["materializationAttempts"] = 1;
        WriteCertificate(node.ToJsonString());
        Assert.Equal("evidence-window-incomplete", Validate().Reason);
    }

    private string CompleteSyntheticEvidence()
    {
        // Synthetic validator input only: never a hardware approval or a shipped certificate.
        object Artifact(string name, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic fixture");
            return new { Name = name, Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), InsideStage = true };
        }
        object RuntimeArtifact(string name, string runtimePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            File.WriteAllText(runtimePath, "synthetic fixture");
            if (name == "SkiaSharp native") return Artifact(name, runtimePath);
            string stagedPath = Path.Combine(Game, "evidence-artifacts", name.Replace(' ', '-') + Path.GetExtension(runtimePath));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            File.Copy(runtimePath, stagedPath, overwrite: true);
            return Artifact(name, stagedPath);
        }
        var rendering = new { MaterializationAttemptCount = 0, MaterializationSuccessCount = 0, MaterializationFailureCount = 0 };
        var window = new
        {
            evidenceComplete = true, measuredFromBeforeFirstClientFramesThroughInventoryOpenAndSettle = true,
            gpuCandidateAttempts = 2, gpuCandidateSuccesses = 2, gpuCandidateFallbacks = 0,
            materializationAttempts = 0, materializationSuccesses = 0, materializationFailures = 0,
            candidateFallbackReasons = new System.Collections.Generic.Dictionary<string, long>(),
            materializationCauses = new System.Collections.Generic.Dictionary<string, long>(), renderingDiagnostics = rendering
        };
        var scales = new[] { 1m, 1.5m, 2m }.Select(scale => new
        {
            scale, gpuDecision = "skia-no-cairo-fallback", gpuDecisionReasons = Array.Empty<string>(),
            gpuCandidateAttempts = 2, gpuCandidateSuccesses = 2, gpuCandidateFallbacks = 0,
            materializationAttemptCount = 0, materializationSuccessCount = 0, materializationFailureCount = 0,
            candidateFallbackReasons = new System.Collections.Generic.Dictionary<string, long>(),
            materializationCauses = new System.Collections.Generic.Dictionary<string, long>(), renderingDiagnostics = rendering
        }).ToArray();
        var artifacts = new[]
        {
            RuntimeArtifact("VintagestoryLib", Path.Combine(Cache, "VintagestoryLib.dll")),
            RuntimeArtifact("VintagestoryAPI", Path.Combine(Cache, "VintagestoryAPI.dll")),
            RuntimeArtifact("Cairo", Path.Combine(Game, "Lib", "cairo-sharp.dll")),
            RuntimeArtifact("SkiaSharp", Path.Combine(Game, "Lib", "SkiaSharp.dll")),
            RuntimeArtifact("SkiaSharp native", Path.Combine(Game, "Lib", "libSkiaSharp.so")),
            Artifact("Optimum.Pharos.Gui.Tests", Path.Combine(Game, "Optimum.Pharos.Gui.Tests.dll"))
        };
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenarioId = "optimum-gui-inventory-capture-v1", gpuDecision = "skia-no-cairo-fallback",
            qualificationMode = "pharos-current-context",
            runtimeProfileFingerprint = Fingerprint(),
            optimumCommit = new string('a', 40), pharosCommit = new string('b', 40),
            gpuDecisionReasons = Array.Empty<string>(),
            materializationObserverRegistration = new { passed = true, observerRegistered = true, attemptCount = 0, successCount = 0, failureCount = 0,
                causes = new System.Collections.Generic.Dictionary<string, long>() },
            glStateSentinel = new { candidateAndProbeRestoredCapturedState = true, samplerBindingsSupported = true },
            runtimeProvenance = new { StagePath = Game, Complete = true, SamplerBindingsSupported = true, GlVendor = "synthetic vendor", GlRenderer = "synthetic renderer",
                GlVersion = "4.6 synthetic", DriverVersion = "diagnostic-only", GlContextProfileMask = 2, GlContextFlags = 0, Artifacts = artifacts },
            initialGuiComposition = window, recompositionBenchmark = window, guiScaleFixtures = scales,
            gpuPrimitiveFixture = "GpuRendered",
            gpuPrimitiveFixtureRenderedWithoutCairoReference = true,
            gpuNonSubsetFixtureDiagnostics = Enumerable.Range(1, 5).ToDictionary(index => "fixture-" + index, _ => "GpuRendered;CairoParityNotMeasured"),
            gpuSvgDiagnostics = Enumerable.Range(1, 3).Select(_ => new { rasterizerKind = "skia-gpu-candidate", rasterizer = "SvgLoader.rasterizeSvg (Skia-rasterized via GPU candidate; no materialization)", textureProduced = true, cairoReferenceCompared = false,
                materializationAttemptsAfterLoad = 0, materializationAttemptsBefore = 0, gpuCandidateAttemptsDuringLoad = 1 }).ToArray()
        });
    }

    [Fact]
    public void LinkedEvidenceIsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links requires an OS privilege on Windows.
        WriteCertificate("{}");
        string path = Path.Combine(Metadata, GuiCertification.EvidenceFile);
        string target = Path.Combine(root, "external-evidence.json");
        File.Move(path, target);
        File.CreateSymbolicLink(path, target);
        Assert.False(Validate().Passed);
    }

    [Fact]
    public void ArtifactOutsideStageIsRejectedEvenWhenInsideStageFlagIsTrue()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        string external = Path.Combine(root, "external-native.so");
        File.WriteAllText(external, "synthetic fixture");
        node["runtimeProvenance"]!["Artifacts"]![4]!["Path"] = external;
        WriteCertificate(node.ToJsonString());
        Assert.False(Validate().Passed);
    }

    [Fact]
    public void NativeLibraryCopyInsideStageMustMatchThePackagedNativeBytes()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CompleteSyntheticEvidence())!;
        string packaged = node["runtimeProvenance"]!["Artifacts"]![4]!["Path"]!.GetValue<string>();
        string loadedCopy = Path.Combine(Game, "evidence-artifacts", "libSkiaSharp.so");
        Directory.CreateDirectory(Path.GetDirectoryName(loadedCopy)!);
        File.Copy(packaged, loadedCopy);
        node["runtimeProvenance"]!["Artifacts"]![4]!["Path"] = loadedCopy;
        WriteCertificate(node.ToJsonString());
        Assert.True(Validate(new GuiDeviceIdentity("synthetic vendor", "synthetic renderer", "4.6 synthetic", 4, 6, 2, 0)).Passed);
    }

    private GuiCertificationDecision Validate(GuiDeviceIdentity? identity = null) =>
        GuiCertification.Validate(Game, Cache, Data, [], (_, _) => identity);
    private string Fingerprint() => GuiCertification.ComputeFingerprint(Game, Cache, Data, []);
    private void WriteCertificate(string evidence)
    {
        File.WriteAllText(Path.Combine(Metadata, GuiCertification.EvidenceFile), evidence);
        var certificate = new GuiCertificate(1, GuiCertification.Policy, GuiCertification.Policy,
            RuntimeInformation.RuntimeIdentifier, Fingerprint(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Metadata, GuiCertification.EvidenceFile)))));
        File.WriteAllText(Path.Combine(Metadata, GuiCertification.CertificateFile),
            JsonSerializer.Serialize(certificate, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public void Dispose() => Directory.Delete(root, true);
}
