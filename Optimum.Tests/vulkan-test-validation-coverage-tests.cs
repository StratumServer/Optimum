using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The GPU suite runs with synchronization and best-practices validation by
/// default, and only because every context and device comes from one helper.
/// A test that builds its own options silently drops back to plain validation,
/// which is how the P2 and P4 hazards went unseen; these checks keep the
/// helper the only way in.
/// </summary>
public class VulkanTestValidationCoverageTests
{
    private const string TestProject = "Optimum.Render.Vulkan.Tests";

    [Fact]
    public void OnlyTheSharedHelperBuildsContextOptionsOrDevices()
    {
        foreach (string file in TestSources())
        {
            string name = Path.GetFileName(file);
            if (name == "GpuTest.cs") continue;
            string source = File.ReadAllText(file);
            Assert.False(source.Contains("new VulkanContextOptions", StringComparison.Ordinal),
                name + " builds its own VulkanContextOptions; use GpuTest.ContextOptions");
            Assert.False(source.Contains("new VulkanDevice", StringComparison.Ordinal),
                name + " creates its own VulkanDevice; use GpuTest.NewDevice or GpuTest.TryCreateDevice");
        }
    }

    [Fact]
    public void EveryNoErrorsIsPairedWithNoSyncHazards()
    {
        foreach (string file in TestSources())
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("ValidationAssert", StringComparison.Ordinal)) continue;
            string source = File.ReadAllText(file);
            Assert.True(
                Count(source, "ValidationAssert.NoErrors(") == Count(source, "ValidationAssert.NoSyncHazards("),
                name + ": every ValidationAssert.NoErrors needs a ValidationAssert.NoSyncHazards");
        }
    }

    [Fact]
    public void TheHelperDefaultsToSyncAndBestWithAnEnvironmentOverride()
    {
        string helper = Read(TestProject + "/GpuTest.cs");
        Assert.Contains("\"OPTIMUM_TEST_VALIDATION_FEATURES\"", helper);
        Assert.Contains("DefaultValidationFeatures = \"sync,best\"", helper);
        Assert.Contains("EnableValidation = true", helper);

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("ConfigureContextOptions?.Invoke(options);", device);
    }

    [Fact]
    public void PoisonModeIsReadAtContextCreationAndAppliedAtTheCreationSites()
    {
        string context = Read("Optimum.Render.Vulkan/Core/VulkanContext.cs");
        Assert.Contains("\"OPTIMUM_VULKAN_POISON\"", context);
        Assert.Contains("PoisonRequested(Environment.GetEnvironmentVariable(PoisonVariable))", context);

        string textures = Read("Optimum.Render.Vulkan/Core/TextureManager.cs");
        Assert.Contains("if (_context.PoisonFreshResources) Poison(texture);", textures);

        string resources = Read("Optimum.Render.Vulkan/Core/VulkanResources.cs");
        Assert.Contains("VulkanPoison.FillHostMemory(Mapped, size);", resources);
    }

    private static string[] TestSources() =>
        Directory.GetFiles(Path.GetDirectoryName(PatchReader.FindRepositoryFile(TestProject + "/GpuTest.cs"))!, "*.cs");

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
