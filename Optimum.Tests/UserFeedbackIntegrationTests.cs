using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cairo;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class UserFeedbackIntegrationTests
{
    private static readonly string[] UserFeedbackAllowedFolders = ["ModConfig/", "ModData/", "Logs/"];
    private static readonly string[] UserFeedbackAllowedExtensions = [".json", ".txt", ".log", ".cfg", ".yml", ".yaml", ".ini"];
    private static readonly string[] UserFeedbackCommonWords =
    [
        "crash", "error", "exception", "warning", "client", "server", "player",
        "world", "chunk", "thread", "memory", "network", "render", "packet",
        "failed", "timeout", "null"
    ];
    private static readonly string[] UserFeedbackSharedRoots =
    [
        "Vintagestory", "System", "Microsoft", "HarmonyLib", "Newtonsoft",
        "ProtoBuf", "Cairo", "SkiaSharp", "OpenTK", "MonoMod", "Mono"
    ];

    [Fact]
    public void BundledContentModMetadataMatchesReleaseConventions()
    {
        string version = File.ReadAllText(PatchReader.FindRepositoryFile("VERSION")).Trim();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/mods/optimum/modinfo.json")));
        JsonElement root = doc.RootElement;

        Assert.Equal("content", root.GetProperty("type").GetString());
        Assert.Equal("optimum", root.GetProperty("modid").GetString());
        Assert.Equal("Optimum", root.GetProperty("name").GetString());
        Assert.Equal(version, root.GetProperty("version").GetString());
        Assert.Equal("Client", root.GetProperty("side").GetString());
        Assert.False(root.GetProperty("requiredOnServer").GetBoolean());
        Assert.False(root.GetProperty("requiredOnClient").GetBoolean());
    }

    [Fact]
    public void BundledUserFeedbackManifestObeysAllValidationRules()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(
            PatchReader.FindRepositoryFile("sources/mods/optimum/assets/optimum/config/userfeedback.json")));
        JsonElement root = doc.RootElement;

        var destinations = root.GetProperty("destinations").EnumerateArray().ToArray();
        Assert.Contains(destinations, d =>
            d.GetProperty("type").GetString() == "github" &&
            d.GetProperty("repo").GetString() == "StratumServer/Optimum");

        var files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.NotEmpty(files);
        Assert.True(files.Length <= 8);
        foreach (string relPath in files)
        {
            Assert.True(UserFeedbackAllowedFolders.Any(folder => relPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)),
                $"File '{relPath}' must start with an allowed UserFeedback folder.");
            string ext = System.IO.Path.GetExtension(relPath).ToLowerInvariant();
            Assert.Contains(ext, UserFeedbackAllowedExtensions);
            Assert.DoesNotContain("chat", relPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audit", relPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("optimum", relPath, StringComparison.OrdinalIgnoreCase);
        }

        var logMatch = root.GetProperty("logMatch").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.InRange(logMatch.Length, 1, 8);
        foreach (string term in logMatch)
        {
            Assert.Equal(term.Trim(), term);
            Assert.InRange(term.Length, 4, 40);
            Assert.True(term.Count(char.IsLetterOrDigit) >= 4, $"logMatch '{term}' needs >= 4 alphanumerics.");
            Assert.False(UserFeedbackCommonWords.Any(w => term.StartsWith(w, StringComparison.OrdinalIgnoreCase)),
                $"logMatch '{term}' overlaps blocked common words.");
            Assert.False(UserFeedbackSharedRoots.Any(r => term.StartsWith(r, StringComparison.OrdinalIgnoreCase)),
                $"logMatch '{term}' starts with a blocked shared root.");
        }
    }

    [Fact]
    public void CollectEventBusPayloadIsStructuredBoundedValidJsonAndPathFree()
    {
        var root = new TreeAttribute();
        OptimumStatusModSystem.PopulateUserFeedbackContext(root);

        Assert.True(root.HasAttribute("optimum"));
        ITreeAttribute opt = root.GetTreeAttribute("optimum");
        Assert.NotNull(opt);
        Assert.Equal(OptimumConfig.Version, opt.GetString("version"));
        Assert.False(opt.HasAttribute("files"), "Optimum must not inject reserved 'files' key via event bus.");
        Assert.True(opt.HasAttribute("guiBackendConfigured"));
        Assert.True(opt.HasAttribute("guiBackendEffective"));
        Assert.True(opt.HasAttribute("renderScale"));
        Assert.True(opt.HasAttribute("toggles"));
        Assert.True(opt.HasAttribute("ecosystemMods"));
        Assert.True(opt.HasAttribute("interop"));
        Assert.True(opt.HasAttribute("shaderCompat"));
        Assert.True(opt.HasAttribute("threadPool"));
        Assert.True(opt.HasAttribute("counters"));
        Assert.True(opt.HasAttribute("breakers"));

        string json = root.ToJsonToken();
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        Assert.InRange(utf8.Length, 100, 16384);

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.DoesNotContain("/home/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\Users\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Users/", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsTabAndPatcherWireUserFeedbackOpenEventSafely()
    {
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string lang = File.ReadAllText(PatchReader.FindRepositoryFile("sources/lang/en.json"));

        Assert.Contains("handler.IsIngame && handler.Api?.ModLoader?.IsModEnabled(\"userfeedback\") == true", patch);
        Assert.Contains("onOptimumUserFeedbackClicked", patch);
        Assert.Contains("\"userfeedback:open\"", patch);
        Assert.Contains("\"onOptimumUserFeedbackClicked\"", patcher);
        Assert.Contains("\"optimum-userfeedback-button\"", lang);
        Assert.Contains("\"optimum-userfeedback-tooltip\"", lang);
    }

    [Fact]
    public void PackagingDeployAndUninstallScriptsStageBundledContentMod()
    {
        foreach (string relPath in new[]
        {
            "Makefile",
            "scripts/package-linux.sh",
            "scripts/package-linux.ps1",
            "scripts/package-macos.sh",
            "scripts/package-macos.ps1",
            "scripts/package.ps1",
            "scripts/package-overlay.sh"
        })
        {
            string script = File.ReadAllText(PatchReader.FindRepositoryFile(relPath));
            Assert.Contains("sources/mods/optimum", script);
            Assert.Contains("Mods/optimum", script);
        }

        Assert.Contains("Mods/optimum", File.ReadAllText(PatchReader.FindRepositoryFile("scripts/uninstall.sh")));
        Assert.Contains("Mods\\optimum", File.ReadAllText(PatchReader.FindRepositoryFile("scripts/uninstall.ps1")));
    }

    [NanoSvgFact]
    public void RatingStyleSvgAndCpuFallbackWorkOnUndisposedContextGetTargetAlias()
    {
        const string ratingSvg = """
<svg version="1.2" xmlns="http://www.w3.org/2000/svg" viewBox="16 0 20 20" width="40" height="40">
  <path fill-rule="evenodd" fill="#57554e" d="m18 4 h14 v12 h-14 z m4 4 h6 v4 h-6 z"/>
  <path fill="none" stroke="#35ff00" stroke-width="2" stroke-linecap="round" d="M 20 12 Q 26 16 30 10"/>
</svg>
""";

        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 40, 40);
        using (var ctx = new Context(surface))
        {
            var alias = (ImageSurface)ctx.GetTarget();
            Assert.NotNull(alias);
            using var picture = RecordedSvgGeometry.Acquire(ratingSvg, 40, 40, 40, 40);
            Assert.True(alias.RecordPicture(picture.Value, 0, 0, 40, 40, null, _ => { }, textureColorOrder: false, pictureOwner: picture));
        }

        byte[] recordedPixels = surface.Data;
        Assert.Equal(40 * 40 * 4, recordedPixels.Length);
        Assert.Contains(recordedPixels, b => b != 0);

        // Also exercise the CPU rasterize fallback path via DataPtr + MarkDirty on an undisposed alias.
        using var fallbackSurface = ImageSurface.CreateForRecordedGui(Format.Argb32, 40, 40);
        using (var ctx = new Context(fallbackSurface))
        {
            var alias = (ImageSurface)ctx.GetTarget();
            Assert.NotNull(alias);
            Marshal.WriteInt32(alias.DataPtr, 4 * (10 * 40 + 10), unchecked((int)0xFF35FF00));
            ((Surface)alias).MarkDirty();
        }

        byte[] fallbackPixels = fallbackSurface.Data;
        int offset = 4 * (10 * 40 + 10);
        Assert.Equal(0x00, fallbackPixels[offset + 0]);
        Assert.Equal(0xFF, fallbackPixels[offset + 1]);
        Assert.Equal(0x35, fallbackPixels[offset + 2]);
        Assert.Equal(0xFF, fallbackPixels[offset + 3]);
    }
}
