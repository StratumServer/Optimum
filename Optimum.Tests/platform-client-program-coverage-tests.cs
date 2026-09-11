using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 1: ClientProgram.Start constructs the platform the
/// backend hands it (VulkanClientPlatform : ClientPlatformWindows), initializes graphics
/// through an injected virtual once the window is open, and on failure swaps in a plain
/// ClientPlatformWindows. Each of these fails silently when wrong: a lambda breaks the Cecil
/// transplant, a fallback that forgets ScreenManager.Platform renders through the dead
/// Vulkan platform, a member missing from Program.cs ships nothing.
/// </summary>
public class PlatformClientProgramCoverageTests
{
    private static string StartRegion()
    {
        string program = ReadLib("Vintagestory.Client/ClientProgram.cs");
        int start = program.IndexOf("private unsafe void Start(ClientProgramArgs args, string[] rawArgs)", StringComparison.Ordinal);
        int end = program.IndexOf("private GameWindowNative AttemptToOpenWindow(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Start must precede AttemptToOpenWindow");
        return program.Substring(start, end - start);
    }

    [Fact]
    public void TheStartRegionHasNoLambda()
    {
        string region = StartRegion();

        Assert.Contains("private void ConfigureClientPlatform(ClientPlatformWindows clientPlatformWindows)", region);
        Assert.Contains("private void OptimumStartSinglePlayerServer(StartServerArgs serverargs)", region);
        Assert.DoesNotContain("delegate", region);
        foreach (string line in region.Split('\n'))
        {
            // Vanilla's window-mode switch expression arms (`3 => 3,`) are not lambdas.
            if (Regex.IsMatch(line, @"^\s*(\d+|_)\s*=>\s*\d+,?\s*$")) continue;
            Assert.DoesNotContain("=>", line);
        }
    }

    [Fact]
    public void StartCreatesTheBackendPlatformAndFallsBackToTheBase()
    {
        string region = StartRegion();

        int probe = region.IndexOf("OptimumRenderBootstrap.ShouldTryVulkan(", StringComparison.Ordinal);
        int create = region.IndexOf("OptimumRenderBootstrap.CreatePlatform(logger) as ClientPlatformWindows;", StringComparison.Ordinal);
        int fallback = region.IndexOf("clientPlatformWindows = new ClientPlatformWindows(logger);", StringComparison.Ordinal);
        int configure = region.IndexOf("ConfigureClientPlatform(clientPlatformWindows);", StringComparison.Ordinal);
        int screenManager = region.IndexOf("screenManager = new ScreenManager(clientPlatformWindows);", StringComparison.Ordinal);
        int window = region.IndexOf("AttemptToOpenWindow(gameWindowSettings, val2, num3, num4, 3);", StringComparison.Ordinal);
        int initialize = region.IndexOf("clientPlatformWindows.InitializeGraphics(", StringComparison.Ordinal);

        Assert.True(probe >= 0 && create > probe, "the probe runs before the platform is created");
        Assert.True(fallback > create, "a null platform falls back to the base constructor");
        Assert.True(configure > fallback && screenManager > configure);
        Assert.True(window > screenManager && initialize > window, "graphics initialize after the window opens");
        Assert.DoesNotContain("OptimumRenderBootstrap.Install", region);
        Assert.DoesNotContain("OptimumRenderBootstrap.Shutdown", region);
    }

    [Fact]
    public void TheFallbackReassignsScreenManagerPlatform()
    {
        string region = StartRegion();

        int reason = region.IndexOf("\"[Optimum] Vulkan unavailable, reopening for OpenGL: \" + optimumInstallReason", StringComparison.Ordinal);
        Assert.True(reason >= 0, "the fallback log line keeps its shape");
        int reopen = region.IndexOf("AttemptToOpenWindow(gameWindowSettings, val2, num3, num4, 3);", reason, StringComparison.Ordinal);
        int rebuild = region.IndexOf("clientPlatformWindows = new ClientPlatformWindows(logger);", reason, StringComparison.Ordinal);
        int configure = region.IndexOf("ConfigureClientPlatform(clientPlatformWindows);", reason, StringComparison.Ordinal);
        int assign = region.IndexOf("ScreenManager.Platform = clientPlatformWindows;", reason, StringComparison.Ordinal);
        int start = region.IndexOf("screenManager.Start(args, rawArgs);", StringComparison.Ordinal);

        Assert.True(reopen > reason, "the window is reopened for OpenGL");
        Assert.True(rebuild > reopen && configure > rebuild && assign > configure,
            "the fallback builds, wires and publishes a base platform");
        Assert.True(start > assign, "the swap happens before screenManager.Start");
    }

    [Fact]
    public void ShutdownGraphicsRunsInTheFinallyBeforeTheWindowIsDisposed()
    {
        string region = StartRegion();

        int finallyBlock = region.IndexOf("finally", StringComparison.Ordinal);
        int shutdown = region.IndexOf("clientPlatformWindows.ShutdownGraphics();", StringComparison.Ordinal);
        int dispose = region.IndexOf("((NativeWindow)gameWindowNative).Dispose();", StringComparison.Ordinal);

        Assert.True(finallyBlock >= 0 && shutdown > finallyBlock && dispose > shutdown);
    }

    [Fact]
    public void TheRendererLogLinesKeepTheirShape()
    {
        string region = StartRegion();

        Assert.Contains("Console.WriteLine(\"[Optimum] Vulkan renderer: \" +", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] OpenGL renderer: \" + optimumRendererReason);", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] OpenGL renderer: selected by config\");", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] Vulkan unavailable, reopening for OpenGL: \" + optimumInstallReason);", region);
    }

    [Fact]
    public void CreatePlatformReturnsObjectAndInstallIsGone()
    {
        string bootstrap = Read("sources/VintagestoryApi/Client/optimum-render-bootstrap.cs");

        Assert.Contains("public static object CreatePlatform(object logger)", bootstrap);
        Assert.Contains("\"Optimum.Render.Vulkan.Platform.VulkanClientPlatform\"", bootstrap);
        Assert.DoesNotContain("public static bool Install(", bootstrap);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresTheGraphicsVirtuals()
    {
        string platform = ReadLib("Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual bool InitializeGraphics(IntPtr windowHandle, int width, int height, out string reason)", platform);
        Assert.Contains("public virtual void ShutdownGraphics()", platform);
    }

    [Fact]
    public void ThePatcherListsEveryNewMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        Assert.Contains("\"InitializeGraphics\",", abstractMembers);
        Assert.Contains("\"ShutdownGraphics\",", abstractMembers);

        string programMembers = Block(patcher, "[\"Vintagestory.Client.ClientProgram\"] = new()");
        Assert.Contains("\"ConfigureClientPlatform\",", programMembers);
        Assert.Contains("\"OptimumStartSinglePlayerServer\",", programMembers);

        Assert.Contains("new(\"Vintagestory.Client.ClientProgram\", \"Start\", 2)", patcher);
    }

    [Fact]
    public void VulkanClientPlatformDerivesFromClientPlatformWindows()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");

        Assert.Contains("namespace Optimum.Render.Vulkan.Platform;", platform);
        Assert.Contains("public class VulkanClientPlatform : ClientPlatformWindows", platform);
        Assert.Contains("public VulkanClientPlatform(Logger logger) : base(logger)", platform);
        Assert.Contains("public override bool InitializeGraphics(IntPtr windowHandle, int width, int height, out string reason)", platform);
        Assert.Contains("public override void ShutdownGraphics()", platform);
        Assert.Contains("\"OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE\"", platform);
        // Step 1 overrides nothing else: the base's device branches keep rendering.
        Assert.Equal(2, Regex.Matches(platform, @"^\s*(public|protected|internal)\s+override\s", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void TheRendererCompilesAgainstTheDonorWithoutShippingIt()
    {
        string renderer = Regex.Replace(Read("Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj"), @"\s+", " ");
        // Compile-only: no copy, and no NuGet or transitive project flow that
        // CopyLocalLockFileAssemblies would copy into the shared deploy output.
        Assert.Contains("<ProjectReference Include=\"..\\build\\VintagestoryLib\\VintagestoryLib.csproj\"> <Private>false</Private> <ExcludeAssets>all</ExcludeAssets> </ProjectReference>", renderer);
        Assert.Contains("<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>", renderer);

        string makefile = Read("Makefile");
        Assert.DoesNotContain("$(MOD_OUT)/VintagestoryLib", makefile);
        foreach (string script in new[] { "scripts/package-linux.sh", "scripts/package-macos.sh" })
            Assert.DoesNotContain("$MOD_OUT/VintagestoryLib", Read(script));
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
        string build = "build/VintagestoryLib/" + relativePath;
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile(build));
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
