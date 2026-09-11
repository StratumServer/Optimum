using System;
using System.IO;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 1: <see cref="VulkanClientPlatform" /> replaces
/// ClientPlatformWindows on the Vulkan path. These run against the donor lib the
/// renderer compiles against, which is what the Cecil patch transplants into vanilla.
/// </summary>
public class PlatformSubstitutionTests
{
    private readonly ITestOutputHelper _output;

    public PlatformSubstitutionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ThePlatformDerivesFromClientPlatformWindows()
    {
        Assert.True(typeof(VulkanClientPlatform).IsSubclassOf(typeof(ClientPlatformWindows)));
        Assert.False(typeof(ClientPlatformWindows).IsSealed);
    }

    [Fact]
    public void TheSelfCheckPassesAgainstTheDonor()
    {
        bool ok = VulkanClientPlatform.VerifyHost(typeof(ClientPlatformAbstract), typeof(ClientPlatformWindows), out string? reason);

        Assert.True(ok, reason);
        Assert.Null(reason);
    }

    private abstract class UnpatchedAbstract
    {
    }

    private class UnpatchedWindows : UnpatchedAbstract
    {
    }

    private sealed class SealedWindows : UnpatchedAbstract
    {
    }

    [Fact]
    public void TheSelfCheckRejectsALibWithoutTheVirtuals()
    {
        Assert.False(VulkanClientPlatform.VerifyHost(typeof(UnpatchedAbstract), typeof(UnpatchedWindows), out string? missing));
        Assert.Contains("InitializeGraphics", missing);

        Assert.False(VulkanClientPlatform.VerifyHost(typeof(UnpatchedAbstract), typeof(SealedWindows), out string? sealedReason));
        Assert.Contains("sealed", sealedReason);
    }

    [Fact]
    public void ThePlatformConstructsHeadless()
    {
        // The base constructor touches no window or GL state; a null logger takes
        // its NullLogger branch, which skips the native platform interface.
        var platform = new VulkanClientPlatform(null!);

        Assert.IsAssignableFrom<ClientPlatformWindows>(platform);
        Assert.NotNull(platform.Logger);
    }

    [Fact]
    public void AForcedInstallFailureReturnsFalseWithTheReason()
    {
        var platform = new VulkanClientPlatform(null!);
        int created = 0;
        platform.DeviceFactory = () =>
        {
            created++;
            return GpuTest.NewDevice();
        };

        string? previous = Environment.GetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable);
        Environment.SetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable, "1");
        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);

            Assert.False(installed);
            Assert.Equal("forced by OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE", reason);
            Assert.Equal(0, created);
            Assert.Null(OptimumRender.Device);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable, previous);
        }
    }

    /// <summary>
    /// The install path end to end: the platform brings a validated device up,
    /// publishes it, writes the crash marker, the published device renders a known
    /// colour that reads back, and ShutdownGraphics retires device and marker.
    /// </summary>
    [SkippableFact]
    public unsafe void InitializeGraphicsPublishesARenderingDeviceAndShutdownRetiresIt()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-platform-test-" + Guid.NewGuid().ToString("N"));
        string marker = Path.Combine(dataPath, ".optimum", "vulkan-session.lock");
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };

        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");

            IOptimumGraphicsDevice? seam = OptimumRender.Device;
            Assert.IsType<VulkanDevice>(seam);
            Assert.Equal(EnumRenderBackend.Vulkan, OptimumRender.ActiveBackend);
            Assert.True(File.Exists(marker), "the crash marker is written before the driver is touched");

            int target = seam!.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(1, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.SetViewport(0, 0, 1, 1);
            seam.ClearColor(0, 1f, 0.5f, 0f, 1f);
            seam.Present();
            var pixel = new byte[4];
            fixed (byte* destination = pixel)
                seam.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)destination);

            // Red first, as PacingStatsTests reads the same clear back; 0.5 may
            // legally quantise to either 127 or 128 in UNORM8.
            Assert.Equal(255, pixel[0]);
            Assert.InRange(pixel[1], 127, 128);
            Assert.Equal(0, pixel[2]);
            Assert.Equal(255, pixel[3]);
            GpuTest.AssertClean(seam);

            platform.ShutdownGraphics();
            Assert.Null(OptimumRender.Device);
            Assert.Equal(EnumRenderBackend.OpenGL, OptimumRender.ActiveBackend);
            Assert.False(File.Exists(marker), "a clean shutdown clears the crash marker");
        }
        finally
        {
            platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
