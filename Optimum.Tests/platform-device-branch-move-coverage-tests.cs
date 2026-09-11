using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 4: every OptimumRender.Device branch left
/// ClientPlatformWindows and lives in VulkanClientPlatform. The base keeps only the GL path
/// and calls platform virtuals where logic both backends share (post chain, TAA windows,
/// frame loop) meets the graphics API. An override whose base member is not virtual in the
/// patched lib would be bypassed silently; one the runtime self-check does not list would
/// fail mid-frame instead of falling back to OpenGL at install.
/// </summary>
public class PlatformDeviceBranchMoveCoverageTests
{
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    [Fact]
    public void ClientPlatformWindowsHasNoDeviceBranch()
    {
        string code = StripComments(VulkanPlatformSource.ReadClientPlatformWindows());

        Assert.DoesNotContain("OptimumRender.Device", code);
        Assert.DoesNotContain("IOptimumGraphicsDevice", code);
        Assert.DoesNotContain("optimumDevice", code);
    }

    [Fact]
    public void EveryVulkanPlatformOverrideIsVirtualInThePatchedBaseAndSelfChecked()
    {
        string vulkan = StripComments(VulkanPlatformSource.Read());
        string abstractPlatform = StripComments(Read(AbstractSource));
        string windows = StripComments(VulkanPlatformSource.ReadClientPlatformWindows());
        string patcher = Read("Optimum.Patcher/Program.cs");
        string injectedAbstract = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        string virtualized = Block(patcher, "var methodsToVirtualize = new List<MethodTarget>", "};");
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(vulkan, @"public\s+override\s+(?:unsafe\s+)?[\w<>\[\].]+\s+(\w+)\s*(?:\(|$|\{)", RegexOptions.Multiline))
        {
            names.Add(match.Groups[1].Value);
        }
        Assert.True(names.Count > 90, "expected the whole graphics surface to be overridden, found " + names.Count);

        foreach (string name in names)
        {
            string member = @"\b" + Regex.Escape(name) + @"\s*(?:\(|\{|$|=>)";
            bool abstractMember = Regex.IsMatch(abstractPlatform, @"public\s+abstract\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            bool injectedVirtual = Regex.IsMatch(abstractPlatform, @"public\s+virtual\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            bool virtualizedInPlace = Regex.IsMatch(windows, @"public\s+virtual\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            Assert.True(abstractMember || injectedVirtual || virtualizedInPlace,
                name + " is overridden by VulkanClientPlatform but is neither abstract nor virtual in the base");

            if (injectedVirtual)
            {
                Assert.True(injectedAbstract.Contains("\"" + name + "\",", StringComparison.Ordinal),
                    name + " is an injected ClientPlatformAbstract virtual the patcher does not inject");
                Assert.True(selfCheck.Contains("new(true, \"" + name + "\"", StringComparison.Ordinal)
                    || selfCheck.Contains("new(true, \"get_" + name + "\"", StringComparison.Ordinal),
                    name + " is missing from VulkanClientPlatform.ExpectedVirtuals");
            }
            if (virtualizedInPlace && !abstractMember && !injectedVirtual)
            {
                Assert.True(virtualized.Contains("\"" + name + "\"", StringComparison.Ordinal),
                    name + " is virtual in the donor ClientPlatformWindows but not in methodsToVirtualize");
                Assert.True(selfCheck.Contains("new(false, \"" + name + "\"", StringComparison.Ordinal),
                    name + " is missing from VulkanClientPlatform.ExpectedVirtuals");
            }
        }
    }

    [Fact]
    public void TheThreeBaseEditsAreInPlace()
    {
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        string patcher = Read("Optimum.Patcher/Program.cs");

        string frame = Body(windows, "private void window_RenderFrame(FrameEventArgs e)");
        int begin = frame.IndexOf("BeginFrame();", StringComparison.Ordinal);
        int handler = frame.IndexOf("frameHandler.OnNewFrame(dt);", StringComparison.Ordinal);
        int end = frame.IndexOf("EndFrame();", StringComparison.Ordinal);
        Assert.True(begin >= 0 && handler > begin && end > handler);
        Assert.Contains("((GameWindow)window).SwapBuffers();", Body(windows, "public override void EndFrame()"));

        Assert.Contains("SupportsThickLines = ProbeThickLineSupport();", Body(windows, "public void Start()"));
        Assert.Contains("GL.LineWidth(1.5f);", Body(windows, "public override bool ProbeThickLineSupport()"));

        string resize = Body(windows, "private void Window_Resize()");
        int notify = resize.IndexOf("OnWindowSizeChanged(((NativeWindow)window).ClientSize.X, ((NativeWindow)window).ClientSize.Y);", StringComparison.Ordinal);
        int rebuild = resize.IndexOf("RebuildFrameBuffers();", StringComparison.Ordinal);
        Assert.True(notify >= 0 && rebuild > notify, "the window size has to reach the platform before the rebuild");

        foreach (string target in new[] { "\"window_RenderFrame\", 1)", "\"Start\", 0)", "\"Window_Resize\", 0)" })
        {
            Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", " + target, patcher);
        }

        string abstractPlatform = Read(AbstractSource);
        Assert.Equal("{ }", Regex.Replace(Body(abstractPlatform, "public virtual void BeginFrame()"), @"\s+", " ").Trim());
        Assert.Equal("{ }", Regex.Replace(Body(abstractPlatform, "public virtual void OnWindowSizeChanged(int width, int height)"), @"\s+", " ").Trim());
    }

    [Fact]
    public void TheInjectedPlatformStateAccessorsAreShippedAndSelfChecked()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        string injectedWindows = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()", "},");
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly string[] ExpectedWindowsMembers", "};");
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();

        foreach (Match match in Regex.Matches(selfCheck, "\"(\\w+)\","))
        {
            string name = match.Groups[1].Value;
            Assert.Contains("\"" + name + "\",", injectedWindows);
            Assert.Matches(new Regex(@"public\s+[\w<>\[\]]+\s+" + name + @"\b"), windows);
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
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

    private static string Read(string relativePath) =>
        System.IO.File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
