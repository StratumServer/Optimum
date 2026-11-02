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

    [Fact]
    public void ChunkOpaqueFragmentShaderHasSingleUnderwaterEffectsCallForDarkVisionCompatibility()
    {
        string shader = File.ReadAllText(PatchReader.FindRepositoryFile("sources/shaders/chunkopaque.fsh"));
        const string underwaterPattern = "outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);";

        // Issue #167: DarkVision 1.3.7's native patch dv_chunkopaque.txt performs an exact
        // match on "outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);" with expect: 1.
        // Duplicating this line inside chunkopaque.fsh (e.g. across GREEDYMESH branches) causes
        // DarkVision to fail with "expected 1 match(es), got 2", breaking desaturation on all
        // opaque terrain blocks.
        int occurrences = CountSubstring(shader, underwaterPattern);
        Assert.Equal(1, occurrences);

        // Verify that DarkVision's dv_chunkopaque.txt patch can be applied cleanly.
        const string mainMarker = "void main()";
        Assert.Equal(1, CountSubstring(shader, mainMarker));

        // Simulate DarkVision's literal patching sequence:
        // 1. add_desat_uniforms
        string patched = InsertBefore(shader, mainMarker, "uniform float desaturationStrength;\nuniform float minUnlitBright;\n");
        // 2. add_desat_function
        patched = InsertBefore(patched, mainMarker, "vec3 applyDesaturation(vec3 col) { return col; }\n");
        // 3. add_desat_call
        patched = InsertBefore(patched, underwaterPattern, "outColor.rgb = applyDesaturation(outColor.rgb);");

        Assert.Contains("outColor.rgb = applyDesaturation(outColor.rgb);", patched);
        Assert.Contains(underwaterPattern, patched);
        Assert.Equal(1, CountSubstring(patched, "outColor.rgb = applyDesaturation(outColor.rgb);"));
    }

    [Fact]
    public void DarkVisionExactNativePatchAppliesWithoutError()
    {
        string shader = File.ReadAllText(PatchReader.FindRepositoryFile("sources/shaders/chunkopaque.fsh"));

        const string nativeDarkVisionPatchDsl = """
@@target chunkopaque.fsh
@@priority 10

@@patch add_desat_uniforms
op: insert_before
match: literal
@@pattern
void main()
@@content
uniform float desaturationStrength;
uniform float minUnlitBright;

@@end

@@patch add_desat_function
op: insert_before
match: literal
@@pattern
void main()
@@content
vec3 applyDesaturation(vec3 col)
{
    if (desaturationStrength <= 0.0) return col;
    return col;
}

@@end

@@patch add_desat_call
op: insert_before
match: literal
@@pattern
outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
@@content
outColor.rgb = applyDesaturation(outColor.rgb);
@@end
""";

        string patched = ApplyDarkVisionPatchDsl(shader, nativeDarkVisionPatchDsl);
        Assert.Contains("uniform float desaturationStrength;", patched);
        Assert.Contains("vec3 applyDesaturation(vec3 col)", patched);
        Assert.Contains("outColor.rgb = applyDesaturation(outColor.rgb);", patched);
        Assert.Contains("outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);", patched);
    }

    private static string ApplyDarkVisionPatchDsl(string code, string dsl)
    {
        string currentCode = code;
        string[] lines = dsl.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].TrimEnd('\r', '\n', ' ');
            if (line.StartsWith("@@patch "))
            {
                string patchName = line["@@patch ".Length..].Trim();
                i++;
                string op = "replace";
                string match = "literal";
                int expect = 1;

                while (i < lines.Length)
                {
                    string headerLine = lines[i].TrimEnd('\r', '\n', ' ');
                    if (headerLine == "@@pattern")
                    {
                        i++;
                        break;
                    }
                    int colon = headerLine.IndexOf(':');
                    if (colon > 0)
                    {
                        string key = headerLine[..colon].Trim().ToLowerInvariant();
                        string val = headerLine[(colon + 1)..].Trim().ToLowerInvariant();
                        if (key == "op") op = val;
                        else if (key == "match") match = val;
                        else if (key == "expect" && int.TryParse(val, out int exp)) expect = exp;
                    }
                    i++;
                }

                var patternLines = new System.Collections.Generic.List<string>();
                while (i < lines.Length)
                {
                    string patternLine = lines[i].TrimEnd('\r', '\n', ' ');
                    if (patternLine == "@@content")
                    {
                        i++;
                        break;
                    }
                    patternLines.Add(patternLine);
                    i++;
                }
                string pattern = string.Join("\n", patternLines).Trim();

                var contentLines = new System.Collections.Generic.List<string>();
                while (i < lines.Length)
                {
                    string contentLine = lines[i].TrimEnd('\r', '\n', ' ');
                    if (contentLine == "@@end")
                    {
                        i++;
                        break;
                    }
                    contentLines.Add(contentLine);
                    i++;
                }
                string content = string.Join("\n", contentLines).Trim();

                // Apply literal patch as DarkVision does
                int matches = CountSubstring(currentCode, pattern);
                if (matches != expect)
                {
                    throw new System.Exception($"Patch '{patchName}': expected {expect} match(es), got {matches}");
                }

                if (op == "insert_before")
                {
                    int idx = currentCode.IndexOf(pattern, System.StringComparison.Ordinal);
                    currentCode = currentCode[..idx] + content + "\n" + currentCode[idx..];
                }
                else if (op == "insert_after")
                {
                    int idx = currentCode.IndexOf(pattern, System.StringComparison.Ordinal);
                    currentCode = currentCode[..(idx + pattern.Length)] + "\n" + content + currentCode[(idx + pattern.Length)..];
                }
                else if (op == "replace")
                {
                    int idx = currentCode.IndexOf(pattern, System.StringComparison.Ordinal);
                    currentCode = currentCode[..idx] + content + currentCode[(idx + pattern.Length)..];
                }
            }
            else
            {
                i++;
            }
        }
        return currentCode;
    }

    private static int CountSubstring(string text, string target)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(target, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += target.Length;
        }
        return count;
    }

    private static string InsertBefore(string text, string pattern, string content)
    {
        int index = text.IndexOf(pattern, System.StringComparison.Ordinal);
        Assert.True(index >= 0, $"Pattern '{pattern}' not found");
        return text[..index] + content + "\n" + text[index..];
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
