using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The extra validation checks (sync validation, best practices with the vendor
/// sets, GPU-assisted) are requested through VK_EXT_layer_settings, with the
/// deprecated VK_EXT_validation_features as the fallback for older layers, both
/// chained into vkCreateInstance's pNext. A chained struct whose instance extension
/// was never enabled is ignored by a conformant loader, so the two decisions have
/// to be made together - that was the bug.
/// </summary>
public class ValidationFeaturesTests
{
    private readonly ITestOutputHelper _output;

    public ValidationFeaturesTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<string, VulkanContext.ValidationLayerSetting> SettingsFor(string features)
    {
        var byName = new Dictionary<string, VulkanContext.ValidationLayerSetting>();
        foreach (VulkanContext.ValidationLayerSetting setting in VulkanContext.ValidationLayerSettings(features))
        {
            Assert.True(byName.TryAdd(setting.Name, setting), setting.Name + " is requested twice");
        }
        return byName;
    }

    /// <summary>
    /// The layer's own setting names (docs/research/vulkan-validation.md §1): best practices report as
    /// warnings and performance messages, so "best" also widens report_flags; the desktop vendor
    /// sets come with it, the mobile ones only on request.
    /// </summary>
    [Fact]
    public void TheFeatureNamesMapOntoTheLayersSettings()
    {
        Dictionary<string, VulkanContext.ValidationLayerSetting> settings = SettingsFor(" sync , BEST ");
        Assert.True(settings["validate_sync"].Enabled);
        Assert.True(settings.ContainsKey("syncval_message_extra_properties"));
        Assert.True(settings["validate_best_practices"].Enabled);
        Assert.True(settings.ContainsKey("validate_best_practices_nvidia"));
        Assert.True(settings.ContainsKey("validate_best_practices_amd"));
        Assert.False(settings.ContainsKey("validate_best_practices_arm"));
        Assert.Equal("error,warn,perf", settings["report_flags"].Text);

        Dictionary<string, VulkanContext.ValidationLayerSetting> mobile = SettingsFor("best,mobile");
        Assert.True(mobile.ContainsKey("validate_best_practices_arm"));
        Assert.True(mobile.ContainsKey("validate_best_practices_img"));
    }

    /// <summary>GPU-AV is advised against alongside CPU core validation, so "gpu-only" turns core off.</summary>
    [Fact]
    public void GpuOnlyTurnsCoreValidationOff()
    {
        Assert.True(SettingsFor("gpu")["gpuav_enable"].Enabled);
        Assert.False(SettingsFor("gpu").ContainsKey("validate_core"));

        Dictionary<string, VulkanContext.ValidationLayerSetting> gpuOnly = SettingsFor("gpu-only");
        Assert.True(gpuOnly["gpuav_enable"].Enabled);
        Assert.False(gpuOnly["validate_core"].Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense,,")]
    public void AnEmptyOrUnknownFeatureListSetsNothing(string? setting)
    {
        Assert.Empty(VulkanContext.ValidationLayerSettings(setting));
    }

    [Fact]
    public void TheFeatureListParsesTheDocumentedNames()
    {
        List<ValidationFeatureEnableEXT> enables =
            VulkanContext.ParseValidationFeatures(" sync , BEST ,gpu");

        Assert.Equal(new[]
        {
            ValidationFeatureEnableEXT.SynchronizationValidationExt,
            ValidationFeatureEnableEXT.BestPracticesExt,
            ValidationFeatureEnableEXT.GpuAssistedExt,
        }, enables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense,,")]
    public void AnEmptyOrUnknownFeatureListAsksForNothing(string? setting)
    {
        Assert.Empty(VulkanContext.ParseValidationFeatures(setting));
    }

    /// <summary>
    /// The real thing: an instance created with features requested must come up.
    /// Before the fix the extension was missing while the struct was chained;
    /// with the extension enabled the loader validates the struct, so a mistake
    /// in it now shows up as a failed vkCreateInstance rather than as silence.
    /// </summary>
    [SkippableFact]
    public void AnInstanceComesUpWithTheFeaturesRequested()
    {
        var messages = new List<string>();
        var options = GpuTest.ContextOptions(messages);
        // This test is about the features themselves, whatever the suite default.
        options.ValidationFeatures = "sync,best";

        bool created = VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason);
        if (!created) _output.WriteLine("Vulkan unavailable: " + failureReason);
        Skip.IfNot(created, "No usable Vulkan device.");

        using (context)
        {
            Skip.IfNot(context!.ValidationEnabled, "Validation layer not installed.");
            Assert.NotEqual(default, context.Instance);
            _output.WriteLine("layer " + context.ValidationLayerVersion + ": " + context.ValidationSettingsApplied);

            // A current layer takes the settings, not the deprecated struct.
            using var api = Vk.GetApi();
            if (VulkanContext.LayerAdvertisesExtension(api, "VK_LAYER_KHRONOS_validation", VulkanContext.LayerSettingsExtensionName))
            {
                Assert.StartsWith("layer settings validate_sync", context.ValidationSettingsApplied);
                Assert.Contains("report_flags=error,warn,perf", context.ValidationSettingsApplied);
            }
            Assert.NotEmpty(context.ValidationLayerVersion);
        }
    }

    /// <summary>
    /// The features struct is only chained when the layer really advertises
    /// VK_EXT_validation_features. Naming an extension the layer does not have
    /// fails vkCreateInstance with ErrorExtensionNotPresent, and the bootstrap
    /// answers a failed context by falling back to OpenGL without a word - so a
    /// deprecated extension would turn OPTIMUM_VULKAN_VALIDATION_FEATURES into
    /// "Vulkan silently stopped working".
    /// </summary>
    [SkippableFact]
    public void TheFeaturesExtensionIsCheckedAgainstTheLayer()
    {
        using var api = Vk.GetApi();
        const string layer = "VK_LAYER_KHRONOS_validation";

        Skip.IfNot(
            VulkanContext.LayerAdvertisesExtension(api, layer, "VK_EXT_debug_utils")
                || VulkanContext.LayerAdvertisesExtension(api, layer, VulkanContext.ValidationFeaturesExtensionName),
            "Validation layer not installed.");

        // Whatever the installed layer answers for the real extension, an
        // invented one must be answered with false rather than optimistically
        // enabled - that is the whole point of the guard.
        Assert.False(VulkanContext.LayerAdvertisesExtension(api, layer, "VK_EXT_optimum_not_a_real_extension"));
        // And a layer that is not installed advertises nothing.
        Assert.False(VulkanContext.LayerAdvertisesExtension(
            api, "VK_LAYER_OPTIMUM_not_installed", VulkanContext.ValidationFeaturesExtensionName));
    }

    /// <summary>
    /// OPTIMUM_VULKAN_VALIDATION doubles as a log path. A Windows path has no
    /// forward slash in it and used to be mistaken for the bare "on" switch,
    /// which silently redirected the log to the temp file.
    /// </summary>
    [Theory]
    [InlineData("1", "/fallback.log")]
    [InlineData("true", "/fallback.log")]
    [InlineData("/tmp/x.log", "/tmp/x.log")]
    [InlineData("C:\\logs\\vulkan.log", "C:\\logs\\vulkan.log")]
    public void TheValidationSettingIsAPathOnlyWhenItLooksLikeOne(string setting, string expected)
    {
        Assert.Equal(expected, VulkanDevice.ResolveValidationLogPath(setting, "/fallback.log"));
    }

    [Fact]
    public void AnUnsetValidationSettingMirrorsNowhere()
    {
        Assert.Null(VulkanDevice.ResolveValidationLogPath(null, "/fallback.log"));
    }
}
