using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The one place GPU tests get a <see cref="VulkanContext" /> or a
/// <see cref="VulkanDevice" /> from.
///
/// Every context and device comes up with the validation layers and, by
/// default, synchronization validation plus best practices ("sync,best").
/// Plain validation missed the R32F-history and masked-clear bugs that only
/// synchronization validation names (TAA P2 and P4, 2026-09-10/11), so the
/// suite runs with it unless OPTIMUM_TEST_VALIDATION_FEATURES says otherwise;
/// an empty value turns the extra features off.
/// </summary>
internal static class GpuTest
{
    public const string ValidationFeaturesVariable = "OPTIMUM_TEST_VALIDATION_FEATURES";
    public const string DefaultValidationFeatures = "sync,best";

    public static string ValidationFeatures =>
        Environment.GetEnvironmentVariable(ValidationFeaturesVariable) ?? DefaultValidationFeatures;

    /// <summary>Headless, validated options; <paramref name="messages" /> receives every layer message.</summary>
    public static VulkanContextOptions ContextOptions(List<string>? messages = null) => new()
    {
        Headless = true,
        EnableValidation = true,
        ValidationFeatures = ValidationFeatures,
        DebugCallback = messages == null ? null : Recorder(messages),
    };

    /// <summary>
    /// Appends under the list's own lock: the layers call back from whichever
    /// thread made the Vulkan call, and the asserts snapshot under the same lock.
    /// </summary>
    public static Action<string> Recorder(List<string> messages) => message =>
    {
        lock (messages) messages.Add(message);
    };

    public static bool TryCreateContext(ITestOutputHelper output, List<string>? messages, out VulkanContext? context)
    {
        bool created = VulkanContext.TryCreate(ContextOptions(messages), out context, out string? failureReason);
        if (!created) output.WriteLine("Vulkan unavailable: " + failureReason);
        return created;
    }

    private static readonly ConditionalWeakTable<VulkanDevice, List<string>> DeviceMessages = new();

    /// <summary>
    /// A device, not yet initialised, whose context will come up with the
    /// suite's validation features and record every layer message for
    /// <see cref="AssertClean" />. The client's own diagnostics channel still
    /// receives them too.
    /// </summary>
    public static VulkanDevice NewDevice()
    {
        var messages = new List<string>();
        var device = new VulkanDevice { DebugMode = true };
        device.ConfigureContextOptions = options =>
        {
            options.EnableValidation = true;
            options.ValidationFeatures = ValidationFeatures;
            Action<string>? client = options.DebugCallback;
            options.DebugCallback = message =>
            {
                lock (messages) messages.Add(message);
                client?.Invoke(message);
            };
        };
        DeviceMessages.Add(device, messages);
        return device;
    }

    public static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device)
    {
        VulkanDevice created = NewDevice();
        if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device = created;
            return true;
        }

        output.WriteLine("Vulkan unavailable: " + failureReason);
        created.Dispose();
        device = null;
        return false;
    }

    /// <summary>The layer messages a device from <see cref="NewDevice" /> has recorded so far.</summary>
    public static List<string> MessagesOf(IOptimumGraphicsDevice seam) =>
        seam is VulkanDevice device && DeviceMessages.TryGetValue(device, out List<string>? messages)
            ? messages
            : new List<string>();

    /// <summary>
    /// A device's equivalent of <see cref="ValidationAssert.NoErrors" /> plus
    /// <see cref="ValidationAssert.NoSyncHazards" />: validation errors fail,
    /// synchronization hazards fail unless pinned, and whatever else the device
    /// reports as an error through GetError (failed Vulkan calls, rejected
    /// shaders) fails too.
    /// </summary>
    public static void AssertClean(IOptimumGraphicsDevice seam, [CallerFilePath] string callerFile = "")
    {
        List<string> messages = MessagesOf(seam);
        ValidationAssert.NoErrors(messages);
        ValidationAssert.NoSyncHazards(messages, callerFile);

        string? diagnostics = seam.GetError();
        if (string.IsNullOrEmpty(diagnostics)) return;

        // GetError repeats the layer messages (sanitised for the client's
        // string.Format); those were judged above, so only the rest counts here.
        string residual = diagnostics;
        foreach (string message in ValidationAssert.Snapshot(messages))
        {
            residual = residual.Replace(message.Replace('{', '[').Replace('}', ']'), "");
        }

        var remaining = new List<string>();
        foreach (string line in residual.Split('\n'))
        {
            if (line.Trim().Length > 0) remaining.Add(line);
        }
        Assert.True(remaining.Count == 0, "device diagnostics:\n" + string.Join("\n", remaining));
    }
}
