using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Seam S1: device requirements, latency detection and backend selection.
///
/// The table cases run without a GPU; the device cases check that a contributor
/// really reaches VkDeviceCreateInfo, that asking for something the driver does
/// not have is a note rather than a failed device, and that the colour-write
/// tier - whose own tests pin it - comes out the same with contributors in play.
/// </summary>
public class LatencyCapabilityTests
{
    private readonly ITestOutputHelper _output;

    public LatencyCapabilityTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ tables

    // auto takes the best backend the device really supports; a forced backend
    // the device cannot support degrades and says so (the ColorWriteTier rule).
    [Theory]
    // no vendor support at all
    [InlineData(false, 0, false, false, null, "native")]
    // NV advertised but no present id: the extension has nothing to attribute to
    [InlineData(true, 2, false, false, null, "native")]
    [InlineData(true, 2, false, true, null, "nv")]
    [InlineData(true, 3, false, true, null, "nv")]
    // AMD's feature bit is all the AMD path needs
    [InlineData(false, 0, true, false, null, "amd")]
    // both: NV first
    [InlineData(true, 2, true, true, null, "nv")]
    // forced backends
    [InlineData(true, 2, true, true, "off", "off")]
    [InlineData(true, 2, true, true, "native", "native")]
    [InlineData(false, 0, true, false, "nv", "native")]
    [InlineData(true, 2, false, true, "amd", "native")]
    [InlineData(true, 2, false, true, "nv", "nv")]
    [InlineData(false, 0, true, false, "amd", "amd")]
    public void TheBestSupportedBackendAtOrBelowTheForcedOneIsSelected(
        bool nv, uint nvRevision, bool amd, bool presentId, string? forced, string expected)
    {
        var support = new LatencyDeviceSupport(nv, nvRevision, amd, presentId, presentId2: false);
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            support, LatencyBackends.ParseBackend(forced), LatencyPresentPath.BlitFromOwned);
        Assert.Equal(expected, LatencyBackends.Token(selected));
    }

    [Fact]
    public void ADegradeIsLogged()
    {
        var notes = new List<string>();
        var support = new LatencyDeviceSupport(false, 0, false, false, false);
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            support, LatencyBackendKind.NvLowLatency2, LatencyPresentPath.BlitFromOwned, notes.Add);

        Assert.Equal(LatencyBackendKind.Native, selected);
        Assert.Single(notes);
        Assert.Contains("nv", notes[0]);
        Assert.Contains("native", notes[0]);
        _output.WriteLine(notes[0]);
    }

    [Fact]
    public void AnAvailableBackendIsNotLogged()
    {
        var notes = new List<string>();
        var support = new LatencyDeviceSupport(true, 2, false, true, false);
        Assert.Equal(LatencyBackendKind.NvLowLatency2, LatencyBackendSelector.Select(
            support, LatencyBackendKind.NvLowLatency2, LatencyPresentPath.BlitFromOwned, notes.Add));
        Assert.Empty(notes);
    }

    // A backend is only valid for the present path that drives it (seam S8):
    // both Vulkan swapchain paths host all of them, the D3D12 bridge hosts none.
    [Theory]
    // The path travels as its enum value: the enum is internal to the backend.
    [InlineData((int)LatencyPresentPath.BlitFromOwned, "nv")]
    [InlineData((int)LatencyPresentPath.DirectToSwapchain, "nv")]
    [InlineData((int)LatencyPresentPath.D3D12Bridge, "off")]
    public void APresentPathThatCannotHostTheBackendDegradesIt(int pathValue, string expected)
    {
        var path = (LatencyPresentPath)pathValue;
        var support = new LatencyDeviceSupport(true, 3, true, true, false);
        var notes = new List<string>();
        LatencyBackendKind selected = LatencyBackendSelector.Select(support, forced: null, path, notes.Add);

        Assert.Equal(expected, LatencyBackends.Token(selected));
        Assert.Contains(selected, LatencyBackendSelector.AllowedBackends(path));
        if (path == LatencyPresentPath.D3D12Bridge)
        {
            Assert.Equal(new[] { LatencyBackendKind.None }, LatencyBackendSelector.AllowedBackends(path));
            Assert.NotEmpty(notes);
        }
        else
        {
            Assert.Equal(4, LatencyBackendSelector.AllowedBackends(path).Length);
        }
    }

    [Fact]
    public void TheDegradeLadderEndsAtNone()
    {
        Assert.Equal(LatencyBackendKind.Native, LatencyBackendSelector.Degrade(LatencyBackendKind.NvLowLatency2));
        Assert.Equal(LatencyBackendKind.Native, LatencyBackendSelector.Degrade(LatencyBackendKind.AmdAntiLag));
        Assert.Equal(LatencyBackendKind.None, LatencyBackendSelector.Degrade(LatencyBackendKind.Native));
        Assert.Equal(LatencyBackendKind.None, LatencyBackendSelector.Degrade(LatencyBackendKind.None));
    }

    // VkLatencySubmissionPresentIdNV, and with it per-submit attribution, only
    // exists from revision 3; 615.71.09 is revision 2.
    [Theory]
    [InlineData(0u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    public void PerSubmitAttributionNeedsRevisionThree(uint revision, bool expected)
    {
        var support = new LatencyDeviceSupport(revision > 0, revision, false, true, false);
        Assert.Equal(expected, support.NvPerSubmitAttribution);
        Assert.Equal(3u, LatencyBackendSelector.NvPerSubmitAttributionRevision);
    }

    [Fact]
    public void TheSummaryCarriesTheBackendAndTheRevision()
    {
        string summary = LatencyBackendSelector.Summary(
            LatencyBackendKind.NvLowLatency2,
            new LatencyDeviceSupport(true, 2, false, true, false),
            presentIdEnabled: true);

        Assert.Contains("latency backend nv", summary);
        Assert.Contains("rev 2", summary);
        Assert.Contains("present id ON", summary);
    }

    // ------------------------------------------------------------------ device

    /// <summary>
    /// A contributor that asks for one extension the device really has, one it
    /// certainly does not, and chains a feature struct. It records what it saw so
    /// the test can assert on the refusal as well as on the acceptance.
    /// </summary>
    private sealed class RecordingContributor : IDeviceRequirementContributor
    {
        /// <summary>
        /// Extensions with no dependencies of their own, so enabling one cannot
        /// fail vkCreateDevice for a reason that has nothing to do with the seam.
        /// </summary>
        private static readonly string[] Candidates =
        {
            "VK_KHR_shader_non_semantic_info",
            "VK_EXT_pipeline_creation_feedback",
            "VK_KHR_push_descriptor",
            "VK_EXT_memory_priority",
        };

        public const string AbsentExtension = "VK_OPTIMUM_extension_that_does_not_exist";

        public string Name => "test";
        public string? Requested;
        public bool AbsentRequestRefused;
        public bool SawInstanceStage;
        public bool SawDeviceStage;
        public uint RequestedSpecVersion;

        public void ContributeInstanceExtensions(InstanceRequirements requirements)
        {
            SawInstanceStage = true;
            Assert.False(requirements.Request(AbsentExtension, Name));
            Assert.DoesNotContain(AbsentExtension, requirements.Enabled);
        }

        public void ContributeDeviceRequirements(DeviceRequirements requirements)
        {
            SawDeviceStage = true;
            AbsentRequestRefused = !requirements.Request(AbsentExtension, 0, Name);
            Assert.DoesNotContain(AbsentExtension, requirements.Enabled);
            // A revision nothing can satisfy is refused the same way.
            Assert.False(requirements.Request("VK_KHR_swapchain", uint.MaxValue, Name));

            foreach (string candidate in Candidates)
            {
                if (requirements.IsEnabled(candidate) || !requirements.Has(candidate)) continue;
                RequestedSpecVersion = requirements.SpecVersion(candidate);
                Assert.True(requirements.Request(candidate, 0, Name));
                Requested = candidate;
                break;
            }

            // A feature struct with nothing switched on: harmless to the device,
            // and it proves the chain builder accepts a contributor's struct
            // alongside the colour-write tier's own.
            requirements.ChainFeature(new PhysicalDeviceShaderDrawParametersFeatures
            {
                SType = StructureType.PhysicalDeviceShaderDrawParametersFeatures,
            });
        }
    }

    [SkippableFact]
    public void AContributorsExtensionsReachTheCreatedDeviceAndAMissingOneDoesNotBreakIt()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        var contributor = new RecordingContributor();
        options.RequirementContributors.Add(contributor);

        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason),
            "No usable Vulkan device: " + failureReason);

        using (context)
        {
            Assert.True(contributor.SawInstanceStage);
            Assert.True(contributor.SawDeviceStage);
            Assert.True(contributor.AbsentRequestRefused);
            Assert.DoesNotContain(RecordingContributor.AbsentExtension, context!.EnabledDeviceExtensions);
            Assert.DoesNotContain(RecordingContributor.AbsentExtension, context.EnabledInstanceExtensions);

            if (contributor.Requested != null)
            {
                _output.WriteLine("contributor asked for " + contributor.Requested +
                    " revision " + contributor.RequestedSpecVersion);
                Assert.Contains(contributor.Requested, context.EnabledDeviceExtensions);
            }
            else
            {
                _output.WriteLine("no candidate extension available on this driver; " +
                    "the refusal and chain cases still ran");
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void ContributorsDoNotDisturbTheColourWriteTier()
    {
        Skip.IfNot(VulkanContext.TryCreate(GpuTest.ContextOptions(), out VulkanContext? plain, out string? why),
            "No usable Vulkan device: " + why);

        ColorWriteTier tier;
        bool dynamicBlend;
        using (plain)
        {
            tier = plain!.Capabilities.ColorWriteTier;
            dynamicBlend = plain.Capabilities.DynamicColorBlend;
        }

        VulkanContextOptions options = GpuTest.ContextOptions();
        options.RequirementContributors.Add(new RecordingContributor());
        Assert.True(VulkanContext.TryCreate(options, out VulkanContext? withContributor, out string? failureReason),
            "the device came up without contributors but not with them: " + failureReason);

        using (withContributor)
        {
            _output.WriteLine("colour write tier " + DeviceCaps.Token(tier) +
                " -> " + DeviceCaps.Token(withContributor!.Capabilities.ColorWriteTier));
            Assert.Equal(tier, withContributor.Capabilities.ColorWriteTier);
            Assert.Equal(dynamicBlend, withContributor.Capabilities.DynamicColorBlend);
        }
    }

    [SkippableFact]
    public void TheDeviceReportsWhatTheDriverOffersForLatency()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;
            _output.WriteLine(capabilities.DeviceName + " / " + capabilities.DriverName);
            _output.WriteLine("latency support: " + capabilities.LatencySupport);
            _output.WriteLine("per-submit attribution: " + capabilities.LatencySupport.NvPerSubmitAttribution);
            _output.WriteLine("summary: " + capabilities.LatencySummary);
            _output.WriteLine("enabled device extensions: " + string.Join(", ", context.EnabledDeviceExtensions));

            // Detection is recorded either way; a revision is only reported when
            // the extension is really there.
            if (!capabilities.LatencySupport.NvLowLatency2)
            {
                Assert.Equal(0u, capabilities.LatencySupport.NvLowLatency2SpecVersion);
            }
            else
            {
                Assert.True(capabilities.LatencySupport.NvLowLatency2SpecVersion > 0);
            }

            // Headless: no swapchain, so nothing vendor-specific is enabled and
            // the selection lands on Native. Nothing that is not used is enabled.
            Assert.Equal(LatencyBackendKind.Native, capabilities.LatencyBackend);
            Assert.False(capabilities.NvLowLatency2Enabled);
            Assert.False(capabilities.AmdAntiLagEnabled);
            Assert.False(capabilities.PresentIdEnabled);
            Assert.DoesNotContain(LatencyBackendSelector.NvLowLatency2ExtensionName, context.EnabledDeviceExtensions);
            Assert.DoesNotContain(LatencyBackendSelector.AmdAntiLagExtensionName, context.EnabledDeviceExtensions);
            Assert.DoesNotContain(LatencyBackendSelector.PresentIdExtensionName, context.EnabledDeviceExtensions);
            Assert.Null(context.NvLowLatency2);
            Assert.Null(context.AmdAntiLag);
            Assert.Contains("latency backend native", capabilities.LatencySummary);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void ForcingABackendTheHeadlessDeviceCannotRunDegradesInsteadOfFailing()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.LatencyBackend = LatencyBackendKind.NvLowLatency2;

        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason),
            "No usable Vulkan device: " + failureReason);

        using (context)
        {
            Assert.Equal(LatencyBackendKind.Native, context!.Capabilities.LatencyBackend);
            Assert.False(context.Capabilities.NvLowLatency2Enabled);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }
}
