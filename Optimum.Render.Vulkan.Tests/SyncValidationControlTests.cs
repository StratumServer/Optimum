using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The positive control for <see cref="ValidationAssert.NoSyncHazards" />. A
/// suite with no synchronization messages means nothing unless synchronization
/// validation demonstrably reports, under a SYNC- id our message tag carries,
/// when a hazard is there. This test creates one on purpose.
/// </summary>
public class SyncValidationControlTests
{
    private readonly ITestOutputHelper _output;

    public SyncValidationControlTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void AnUnsynchronisedWriteAfterWriteIsReportedUnderASyncId()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.ValidationFeatures = "sync";
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason),
            "No usable Vulkan device: " + reason);

        using (context)
        {
            Skip.IfNot(context!.ValidationEnabled, "Validation layer not installed.");
            const uint size = 16;
            using var commands = new VulkanCommands(context);
            using var textures = new TextureManager(context, commands);
            VulkanTexture a = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;
            VulkanTexture b = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;
            VulkanTexture c = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;

            commands.SubmitAndWait(commandBuffer =>
            {
                textures.TransitionTexture(commandBuffer, a, ImageLayout.General);
                textures.TransitionTexture(commandBuffer, b, ImageLayout.General);
                textures.TransitionTexture(commandBuffer, c, ImageLayout.General);

                var region = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(size, size, 1),
                };
                // Two writes to the same texels of b and a read of them, with no
                // barrier in between: the misuse the layer exists to name.
                context.Api.CmdCopyImage(commandBuffer, a.Image, ImageLayout.General, b.Image, ImageLayout.General, 1, &region);
                context.Api.CmdCopyImage(commandBuffer, c.Image, ImageLayout.General, b.Image, ImageLayout.General, 1, &region);
                context.Api.CmdCopyImage(commandBuffer, b.Image, ImageLayout.General, a.Image, ImageLayout.General, 1, &region);
            });

            List<string> snapshot = ValidationAssert.Snapshot(messages);
            foreach (string message in snapshot) _output.WriteLine(message);
            Assert.True(snapshot.Any(ValidationAssert.IsSynchronization),
                "synchronization validation reported nothing for a deliberate hazard:\n" + string.Join("\n", snapshot));
        }
    }
}
