using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The extra validation features (sync validation, best practices) ride on
/// VK_EXT_validation_features, chained into vkCreateInstance's pNext. A chained
/// struct whose instance extension was never enabled is ignored by a conformant
/// loader, so the two decisions have to be made together - that was the bug.
/// </summary>
public class ValidationFeaturesTests
{
    private readonly ITestOutputHelper _output;

    public ValidationFeaturesTests(ITestOutputHelper output) => _output = output;

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
        var options = new VulkanContextOptions
        {
            Headless = true,
            EnableValidation = true,
            ValidationFeatures = "sync,best",
            DebugCallback = messages.Add,
        };

        bool created = VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason);
        if (!created) _output.WriteLine("Vulkan unavailable: " + failureReason);
        Skip.IfNot(created, "No usable Vulkan device.");

        using (context)
        {
            Skip.IfNot(context!.ValidationEnabled, "Validation layer not installed.");
            Assert.NotEqual(default, context.Instance);
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
