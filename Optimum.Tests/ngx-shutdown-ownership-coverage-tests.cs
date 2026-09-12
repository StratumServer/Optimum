using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Why the client died inside NGX on 2026-09-12, and the two things that keep it
/// from happening again.
///
/// <para><b>The crash.</b> <c>NVSDK_NGX_VULKAN_Shutdown1</c> is declared with one
/// parameter in the SDK header and implemented with two in driver 615.71.09: it
/// forwards its second argument into the internal shutdown routine, which stores
/// the SDK's remaining reference count through it without a null check. Called
/// through the header's prototype, that pointer is whatever the caller left in
/// <c>%rsi</c> - 8192 in both core dumps - and NGX writes four bytes to it. The
/// shim therefore always passes an <c>int*</c> of its own.</para>
///
/// <para><b>The lifetime.</b> Independently of the crash, the one NGX lifetime a
/// process gets has exactly one owner: <c>NgxLifetime</c>. It performs the release
/// and the drain itself, refuses a second shutdown and refuses to bring NGX up
/// again - so no settings change, and no second teardown, can reach
/// <c>Shutdown1</c>.</para>
/// </summary>
public class NgxShutdownOwnershipCoverageTests
{
    [Fact]
    public void TheShimPassesTheSecondArgumentShutdown1WritesThrough()
    {
        string shim = Read("native/optimum-ngx/optimum_ngx.c");

        Assert.Contains("typedef OptimumNgxResult (*pfn_shutdown1)(void *, int *);", shim);
        Assert.Contains("int remainingReferences = 0;", shim);
        Assert.Contains(
            "((pfn_shutdown1)g_ngx.shutdown1)(device, &remainingReferences)", shim);

        // The one-pointer typedef must not be what shutdown1 is called through any more.
        Assert.DoesNotContain("((pfn_handle)g_ngx.shutdown1)", shim);
    }

    /// <summary>
    /// One call site for the entry point that ends the lifetime, and it is the
    /// owner's. Anything else - a host, a platform, a test fixture - would be a
    /// second way to get the order wrong.
    /// </summary>
    [Fact]
    public void OnlyTheLifetimeOwnerCallsShutdown1()
    {
        var callers = new List<string>();
        foreach (string file in ManagedSources())
        {
            string text = File.ReadAllText(file);
            if (text.Contains("NgxInterop.Shutdown1(") || text.Contains("NgxShim.Shutdown("))
            {
                callers.Add(Path.GetFileName(file));
            }
        }

        // NgxInterop declares the entry point; NgxLifetime is the only thing that calls it.
        callers.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "NgxInterop.cs", "NgxLifetime.cs" }, callers);
    }

    [Fact]
    public void TheOwnerRefusesASecondShutdownAndASecondBringUp()
    {
        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");

        Assert.Contains("if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;", owner);
        Assert.Contains("if (_initialized) return NgxLifetimeOutcome.AlreadyInitialized;", owner);
        // A live feature after the release and the drain means NGX stays up: the
        // fatal pair is Shutdown1 followed by a release, never the other way round.
        Assert.Contains("return NgxLifetimeOutcome.FeatureStillLive;", owner);
    }

    /// <summary>
    /// The settings path - the tab switching the upscaler off or changing the preset,
    /// and the renderer standing the upscaler down at runtime - retires the feature
    /// and leaves NGX up. Nothing on it may shut NGX down: the process gets one
    /// lifetime, so the user would need a restart to get DLSS back.
    /// </summary>
    [Fact]
    public void ASettingsChangeRetiresTheFeatureAndNeverShutsNgxDown()
    {
        string platform = VulkanPlatformSource.Read();

        int apply = platform.IndexOf(
            "public override void ApplyOptimumUpscalerSettings()", StringComparison.Ordinal);
        Assert.True(apply > 0);
        string body = platform.Substring(apply, 700);

        Assert.Contains("upscaler.RetireFeature();", body);
        Assert.DoesNotContain("ShutDownUpscaler", body);
        Assert.DoesNotContain("upscaler.Shutdown", body);
        Assert.DoesNotContain("upscaler = null", body);

        // The host's own stand-down path is the same: a reason, not a shutdown.
        string host = Read("Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs");
        int fail = host.IndexOf("private bool Fail(string reason)", StringComparison.Ordinal);
        Assert.True(fail > 0);
        string failBody = host.Substring(fail);
        Assert.DoesNotContain("NgxLifetime.ShutDown", failBody);
    }

    /// <summary>
    /// ShutdownGraphics is the only caller of the teardown, and it can run twice -
    /// the fallback path calls it as well - so the second one must reach nothing.
    /// </summary>
    [Fact]
    public void OnlyTheGraphicsTeardownShutsTheUpscalerDown()
    {
        string platform = VulkanPlatformSource.Read();
        var sites = new List<int>();
        for (int at = 0; ; )
        {
            int found = platform.IndexOf("ShutDownUpscaler();", at, StringComparison.Ordinal);
            if (found < 0) break;
            sites.Add(found);
            at = found + 1;
        }
        Assert.Single(sites);

        int shutdownGraphics = platform.IndexOf(
            "public override void ShutdownGraphics()", StringComparison.Ordinal);
        Assert.True(shutdownGraphics > 0 && sites[0] > shutdownGraphics,
            "the only ShutDownUpscaler call must be inside ShutdownGraphics");

        // Twice through ShutdownGraphics is survivable because the host is dropped and
        // the owner refuses a second shutdown anyway.
        Assert.Contains("upscaler = null;", platform);
    }

    /// <summary>Every C# file of the backend and its tests - the only places NGX is reachable from.</summary>
    private static IEnumerable<string> ManagedSources()
    {
        string upscale = Path.GetDirectoryName(PatchReader.FindRepositoryFile(
            "Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs"))!;
        string backend = Path.GetFullPath(Path.Combine(upscale, ".."));
        string tests = Path.GetFullPath(Path.Combine(backend, "..", "Optimum.Render.Vulkan.Tests"));
        foreach (string directory in new[] { backend, tests })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
