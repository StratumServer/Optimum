using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 2: the TAA/FSR members are virtual on
/// ClientPlatformAbstract, ClientPlatformWindows overrides them with the OpenGL bodies,
/// and no lib code casts the platform to ClientPlatformWindows any more. A cast left
/// behind fails silently on the Vulkan platform only in the sense that it still works
/// (VulkanClientPlatform derives from ClientPlatformWindows) while bypassing the design:
/// the next step's overrides would be reached by some callers and not others.
/// </summary>
public class PlatformTaaVirtualsCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string WindowsPath = "Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>The virtual, its neutral body on the abstract, and the override signature.</summary>
    private static readonly (string Virtual, string NeutralBody, string Override)[] Members =
    {
        ("public virtual int MotionAttachmentIndex", "return -1;", "public override int MotionAttachmentIndex"),
        ("public virtual bool OptimumMotionWriteActive", "return false;", "public override bool OptimumMotionWriteActive"),
        ("public virtual bool TaaTargetsReady", "return false;", "public override bool TaaTargetsReady"),
        ("public virtual bool TaaResolvedThisFrame", "return false;", "public override bool TaaResolvedThisFrame"),
        ("public virtual FrameBufferRef TaaHistory(int parity)", "return null;", "public override FrameBufferRef TaaHistory(int parity)"),
        ("public virtual bool BeginMotionWrite()", "return false;", "public override bool BeginMotionWrite()"),
        ("public virtual void EndMotionWrite()", "", "public override void EndMotionWrite()"),
        ("public virtual bool BeginMotionOnlyWrite()", "return false;", "public override bool BeginMotionOnlyWrite()"),
        ("public virtual void EndMotionOnlyWrite()", "", "public override void EndMotionOnlyWrite()"),
        ("public virtual bool RenderOptimumSkyMotion()", "return false;", "public override bool RenderOptimumSkyMotion()"),
        ("public virtual bool RenderOptimumTaaResolve()", "return false;", "public override bool RenderOptimumTaaResolve()"),
        ("public virtual int RenderOptimumTaaSharpen(int resolvedScene)", "return resolvedScene;", "public override int RenderOptimumTaaSharpen(int resolvedScene)"),
        ("public virtual bool OptimumFsrBlitActive()", "return false;", "public override bool OptimumFsrBlitActive()"),
        ("public virtual void DisableOptimumTaa(string reason)", "", "public override void DisableOptimumTaa(string reason)"),
    };

    private static readonly Regex PlatformCast = new(@"\b(as|is)\s+ClientPlatformWindows\b|\(\s*ClientPlatformWindows\s*\)\s*[\w(]");

    // The one conversion the plan keeps: the reflective factory returns object.
    private const string AllowedCreation = "OptimumRenderBootstrap.CreatePlatform(logger) as ClientPlatformWindows;";

    [Fact]
    public void NoLibCodeCastsThePlatformToClientPlatformWindows()
    {
        var offenders = new List<string>();
        int allowed = 0;
        foreach ((string file, string line) in LibSourceLines())
        {
            if (!PlatformCast.IsMatch(line)) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
            if (file.EndsWith("ClientProgram.cs", StringComparison.Ordinal) || file.EndsWith("ClientProgram.cs.patch", StringComparison.Ordinal))
            {
                if (trimmed.EndsWith(AllowedCreation, StringComparison.Ordinal))
                {
                    allowed++;
                    continue;
                }
            }
            offenders.Add(file + ": " + trimmed);
        }

        Assert.True(offenders.Count == 0, "casts to ClientPlatformWindows remain:\n" + string.Join("\n", offenders));
        Assert.Equal(1, allowed);
    }

    [Fact]
    public void TheSevenFormerCastSitesCallThroughTheAbstractPlatform()
    {
        string chunk = ReadLib("Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Equal(3, Regex.Matches(chunk, Regex.Escape("ClientPlatformAbstract optimumPlatform = platform;")).Count);

        foreach (string system in new[] { "SystemRenderEntities.cs", "SystemRenderDecals.cs", "SystemRenderParticles.cs" })
        {
            string source = ReadLib("Vintagestory.Client.NoObf/" + system);
            Assert.Single(Regex.Matches(source, Regex.Escape("ClientPlatformAbstract optimumPlatform = game.Platform;")));
        }

        string clientMain = ReadLib("Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("Platform.RenderOptimumSkyMotion();", clientMain);
        Assert.DoesNotContain("optimumSkyMotionPlatform", clientMain);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresEveryTaaMemberWithANeutralBody()
    {
        string platform = ReadLib(AbstractPath);

        foreach ((string signature, string neutral, _) in Members)
        {
            string body = Body(platform, signature);
            string inner = Regex.Replace(body, @"\s+", " ").Trim();
            string expected = signature.Contains('(')
                ? (neutral.Length == 0 ? "{ }" : "{ " + neutral + " }")
                : "{ get { " + neutral + " } }";
            Assert.True(inner == expected, signature + " is not neutral: " + inner);
        }
    }

    [Fact]
    public void ClientPlatformWindowsOverridesEveryTaaMember()
    {
        string platform = ReadLib(WindowsPath);

        foreach ((_, _, string signature) in Members)
        {
            Assert.Single(Regex.Matches(platform, Regex.Escape(signature) + @"(?![\w])"));
        }

        // The state members read private fields, and nothing hides them with a
        // non-virtual declaration of the same name.
        Assert.Contains("private int optimumMotionAttachmentIndex = -1;", platform);
        Assert.Contains("private bool optimumMotionWriteActive;", platform);
        Assert.Contains("private bool optimumTaaTargetsReady;", platform);
        Assert.Contains("private bool optimumTaaResolvedThisFrame;", platform);
        Assert.DoesNotContain("private bool TaaTargetsReady;", platform);
        Assert.DoesNotContain("{ get; private set; }", Body(platform, "public override int MotionAttachmentIndex"));
        Assert.DoesNotContain("internal bool RenderOptimumSkyMotion()", platform);
    }

    [Fact]
    public void ThePatcherInjectsTheVirtualsAndTheOverrideFields()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        foreach (string name in new[]
        {
            "MotionAttachmentIndex", "OptimumMotionWriteActive", "TaaTargetsReady", "TaaResolvedThisFrame",
            "TaaHistory", "BeginMotionWrite", "EndMotionWrite", "BeginMotionOnlyWrite", "EndMotionOnlyWrite",
            "RenderOptimumSkyMotion", "RenderOptimumTaaResolve", "RenderOptimumTaaSharpen", "OptimumFsrBlitActive",
            "DisableOptimumTaa",
        })
        {
            Assert.Contains("\"" + name + "\",", abstractMembers);
        }

        string windowsMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()");
        foreach (string field in new[]
        {
            "optimumMotionAttachmentIndex", "optimumTaaTargetsReady", "optimumTaaResolvedThisFrame", "optimumMotionWriteActive",
        })
        {
            Assert.Contains("\"" + field + "\",", windowsMembers);
        }
    }

    private static IEnumerable<(string File, string Line)> LibSourceLines()
    {
        string root = RepositoryRoot();
        string lib = Path.Combine(root, "build", "VintagestoryLib");
        if (Directory.Exists(lib))
        {
            foreach (string file in Directory.EnumerateFiles(lib, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(lib, file);
                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                foreach (string line in File.ReadLines(file))
                    yield return (relative, line);
            }
            yield break;
        }

        // Un-bootstrapped checkout: every Optimum line in the lib is an added patch line.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "patches", "VintagestoryLib"), "*.patch", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                    yield return (Path.GetFileName(file), line.Substring(1));
            }
        }
    }

    private static string RepositoryRoot()
    {
        string patcher = PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs");
        return Path.GetDirectoryName(Path.GetDirectoryName(patcher)!)!;
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
