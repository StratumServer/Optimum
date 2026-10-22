using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

public sealed class DarkVisionVspShaderCompatibilityTests
{
    [Fact]
    public void CompatibilityPatchTargetsBothVspTerrainFragmentShaders()
    {
        // Both VSP terrain fragment shaders contain the wrapper and this call.
        // Keep both a tab- and space-indented form to exercise whitespace handling.
        var shaderVariants = new[]
        {
            "\toutColor.rgb = vspApplyUnderwaterEffectsAt(outColor.rgb, murkiness, vspWorldPos);",
            "    outColor.rgb = vspApplyUnderwaterEffectsAt(outColor.rgb, murkiness, vspWorldPos);"
        };
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/assets/darkvision/config/shaderpatches/optimum_vsp_terrain_desaturation.txt"));

        const string targetMarker = "outColor.rgb = vspApplyUnderwaterEffectsAt";
        Assert.Contains($"@@target_contains {targetMarker}", patch);
        Assert.Contains("@@target_type fragment", patch);
        Assert.DoesNotContain("@@target chunkopaque.fsh", patch);
        Assert.Contains("@@priority 20", patch);
        Assert.Contains("expect: 1", patch);
        Assert.DoesNotContain("expect: 0", patch);

        string pattern = ReadSection(patch, "@@pattern", "@@content");
        string content = ReadSection(patch, "@@content", "@@end");
        Assert.Contains("outColor.rgb = applyDesaturation(outColor.rgb);", content);
        foreach (string shaderCall in shaderVariants)
        {
            string shader = "vec3 vspApplyUnderwaterEffectsAt(vec3 color, float murkiness, vec3 worldPos) { }\n" + shaderCall;
            Assert.Contains(targetMarker, shader);
            Assert.True(Regex.Matches(shader, pattern, RegexOptions.Multiline).Count == 1,
                "Expected the VSP underwater call exactly once.");
        }

        const string vspTransparentShader = "texColor.rgb = vspApplyUnderwaterEffectsAt(texColor.rgb, murkiness, vspWorldPos);";
        Assert.DoesNotContain(targetMarker, vspTransparentShader);

        foreach (string script in new[]
        {
            "scripts/package-overlay.sh",
            "scripts/package.ps1",
            "scripts/package-linux.ps1",
            "scripts/package-linux.sh",
            "scripts/package-macos.ps1",
            "scripts/package-macos.sh"
        })
        {
            string packagingScript = File.ReadAllText(PatchReader.FindRepositoryFile(script));
            Assert.Contains("sources/assets", packagingScript);
        }
    }

    private static string ReadSection(string patch, string start, string end)
    {
        int startIndex = patch.IndexOf(start, System.StringComparison.Ordinal);
        Assert.True(startIndex >= 0);
        startIndex += start.Length;
        while (startIndex < patch.Length && (patch[startIndex] == '\r' || patch[startIndex] == '\n')) startIndex++;
        int endIndex = patch.IndexOf(end, startIndex, System.StringComparison.Ordinal);
        Assert.True(endIndex >= startIndex);
        return patch[startIndex..endIndex].Trim();
    }
}
