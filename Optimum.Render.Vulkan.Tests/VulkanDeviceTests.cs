using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Brings up a real Vulkan device and renders through it.
///
/// These need a working ICD, so they skip on a machine without one rather than
/// failing - the same rule the shader corpus follows for game assets. Where they
/// do run they are the only check that the SPIR-V this backend generates is
/// something a driver will actually accept, which no amount of CPU-side testing
/// can establish.
/// </summary>
public class VulkanDeviceTests
{
    private readonly ITestOutputHelper _output;

    public VulkanDeviceTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context)
    {
        var messages = new List<string>();
        var options = new VulkanContextOptions
        {
            Headless = true,
            EnableValidation = true,
            DebugCallback = messages.Add,
        };

        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created)
        {
            output.WriteLine("Vulkan unavailable: " + failureReason);
        }
        foreach (string message in messages)
        {
            output.WriteLine("[validation] " + message);
        }
        return created;
    }

    [SkippableFact]
    public void ADeviceMeetingTheBackendsRequirementsCanBeSelected()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;

            _output.WriteLine($"device      : {capabilities.DeviceName}");
            _output.WriteLine($"driver      : {capabilities.DriverName}");
            _output.WriteLine($"api         : {VulkanContext.VersionString(capabilities.ApiVersion)}");
            _output.WriteLine($"type        : {capabilities.DeviceType}");
            _output.WriteLine($"max 2D      : {capabilities.MaxImageDimension2D}");
            _output.WriteLine($"max LOD bias: {capabilities.MaxSamplerLodBias}");
            _output.WriteLine($"bound sets  : {capabilities.MaxBoundDescriptorSets}");
            _output.WriteLine($"UBO align   : {capabilities.MinUniformBufferOffsetAlignment}");

            Assert.True(capabilities.ApiVersion >= VulkanContext.MinimumApiVersion);
            Assert.True(capabilities.MultiDrawIndirect, "chunk rendering needs indirect multidraw");

            // The backend claims three descriptor sets: uniforms, samplers,
            // storage. Vulkan guarantees at least four, but assert it rather
            // than assume.
            Assert.True(capabilities.MaxBoundDescriptorSets >= 3);

            // FSR's mip bias needs the sampler LOD bias to actually do something.
            Assert.True(capabilities.MaxSamplerLodBias >= 2.0f);

            // The vanilla shadow maps go to 4096 at the top quality setting.
            Assert.True(capabilities.MaxImageDimension2D >= 4096);
        }
    }

    /// <summary>
    /// Walks every physical device on the machine and reports which of them this
    /// backend would accept.
    ///
    /// This is the vendor matrix in miniature. The requirements are deliberately
    /// modest - Vulkan 1.3 with dynamic rendering, synchronization2, scalar block
    /// layout, timeline semaphores, independent blend and indirect multidraw -
    /// and a device that fails them falls back to OpenGL rather than breaking, so
    /// the interesting output here is the list, not a pass or fail.
    /// </summary>
    [SkippableFact]
    public void EveryPhysicalDeviceIsAssessedForBackendSupport()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? probe), "No usable Vulkan device.");
        probe!.Dispose();

        int usable = 0;
        for (int index = 0; index < 8; index++)
        {
            var options = new VulkanContextOptions { Headless = true, PreferredDeviceIndex = index };
            if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason))
            {
                if (failureReason != null && failureReason.Contains("out of range", StringComparison.Ordinal))
                {
                    break;
                }
                _output.WriteLine($"device {index}: rejected - {failureReason}");
                continue;
            }

            using (context)
            {
                usable++;
                VulkanCapabilities capabilities = context!.Capabilities;
                _output.WriteLine(
                    $"device {index}: usable - {capabilities.DeviceName} " +
                    $"({capabilities.DriverName}, {VulkanContext.VersionString(capabilities.ApiVersion)}, " +
                    $"{capabilities.DeviceType})");
            }
        }

        Assert.True(usable > 0, "at least one device should be usable if the probe succeeded");
    }

    /// <summary>
    /// The end-to-end check: a GLSL 330 shader pair goes through the translator,
    /// becomes a real pipeline, renders, and the pixels come back correct.
    ///
    /// It also pins the coordinate convention. The vertex shader maps clip Y to
    /// the varying, and with no Y flip anywhere, NDC y = -1 lands in framebuffer
    /// row 0. So the first row of memory must carry the low value and the last
    /// row the high one - exactly what OpenGL produces, which is what makes
    /// render-to-texture round trips and screenshots come out unchanged.
    /// </summary>
    [SkippableFact]
    public unsafe void ATranslatedShaderRendersAndReadsBackWithGlCoordinates()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            const uint width = 64;
            const uint height = 64;
            const Format format = Format.R8G8B8A8Unorm;

            using var compiler = new ShaderCompiler();
            TranslatedProgram program = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "smoke.vsh",
                    Code = """
                        #version 330 core
                        out vec2 texCoord;
                        void main(void)
                        {
                            float x = -1.0 + float((gl_VertexID & 1) << 2);
                            float y = -1.0 + float((gl_VertexID & 2) << 1);
                            gl_Position = vec4(x, y, 0.0, 1.0);
                            texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "smoke.fsh",
                    Code = """
                        #version 330 core
                        in vec2 texCoord;
                        out vec4 outColor;
                        void main(void) { outColor = vec4(texCoord.x, texCoord.y, 0.0, 1.0); }
                        """,
                },
            }, compiler);

            Assert.True(program.Success, string.Join("; ", program.Errors));

            Vk api = context!.Api;
            using var commands = new VulkanCommands(context);
            using var target = new VulkanImage(context, width, height, format,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                ImageAspectFlags.ColorBit);
            using var readback = new VulkanBuffer(context, width * height * 4,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            ShaderModule vertexModule = CreateModule(context, program.Spirv[EnumShaderType.VertexShader]);
            ShaderModule fragmentModule = CreateModule(context, program.Spirv[EnumShaderType.FragmentShader]);

            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo };
            api.CreatePipelineLayout(context.Device, &layoutInfo, null, out PipelineLayout pipelineLayout);

            Pipeline pipeline = CreatePipeline(context, vertexModule, fragmentModule, pipelineLayout, format);

            commands.SubmitAndWait(commandBuffer =>
            {
                commands.TransitionImage(commandBuffer, target, ImageLayout.ColorAttachmentOptimal,
                    ImageAspectFlags.ColorBit);

                var attachment = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = target.View,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)),
                };

                var rendering = new RenderingInfo
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    PColorAttachments = &attachment,
                };

                api.CmdBeginRendering(commandBuffer, &rendering);
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

                // Viewport height stays positive: the backend never flips Y.
                var viewport = new Viewport(0, 0, width, height, 0, 1);
                api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height));
                api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                api.CmdDraw(commandBuffer, 3, 1, 0, 0);
                api.CmdEndRendering(commandBuffer);

                commands.TransitionImage(commandBuffer, target, ImageLayout.TransferSrcOptimal,
                    ImageAspectFlags.ColorBit);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D(width, height, 1),
                };
                api.CmdCopyImageToBuffer(commandBuffer, target.Handle, ImageLayout.TransferSrcOptimal,
                    readback.Handle, 1, &region);
            });

            var pixels = new byte[width * height * 4];
            Marshal.Copy(readback.Mapped, pixels, 0, pixels.Length);

            byte RedAt(uint x, uint y) => pixels[(y * width + x) * 4 + 0];
            byte GreenAt(uint x, uint y) => pixels[(y * width + x) * 4 + 1];
            byte AlphaAt(uint x, uint y) => pixels[(y * width + x) * 4 + 3];

            _output.WriteLine($"row 0    centre: R={RedAt(width / 2, 0)} G={GreenAt(width / 2, 0)}");
            _output.WriteLine($"row {height - 1} centre: R={RedAt(width / 2, height - 1)} G={GreenAt(width / 2, height - 1)}");

            // The triangle covers the whole target, so nothing is left cleared.
            Assert.Equal(255, AlphaAt(width / 2, height / 2));

            // X increases left to right in both APIs.
            Assert.True(RedAt(1, height / 2) < 32, "left edge should be low red");
            Assert.True(RedAt(width - 2, height / 2) > 223, "right edge should be high red");

            // The convention that matters: row 0 is NDC y = -1, so it carries the
            // low value. A backend that flipped Y would invert this, and with it
            // every render-to-texture pass and every screenshot.
            Assert.True(GreenAt(width / 2, 0) < 32,
                $"row 0 should hold texCoord.y near 0, got {GreenAt(width / 2, 0)}");
            Assert.True(GreenAt(width / 2, height - 1) > 223,
                $"last row should hold texCoord.y near 1, got {GreenAt(width / 2, height - 1)}");

            api.DestroyPipeline(context.Device, pipeline, null);
            api.DestroyPipelineLayout(context.Device, pipelineLayout, null);
            api.DestroyShaderModule(context.Device, vertexModule, null);
            api.DestroyShaderModule(context.Device, fragmentModule, null);
        }
    }

    private static unsafe ShaderModule CreateModule(VulkanContext context, byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            Result result = context.Api.CreateShaderModule(context.Device, &createInfo, null, out ShaderModule module);
            Assert.Equal(Result.Success, result);
            return module;
        }
    }

    private static unsafe Pipeline CreatePipeline(
        VulkanContext context, ShaderModule vertex, ShaderModule fragment,
        PipelineLayout layout, Format colorFormat)
    {
        Vk api = context.Api;
        byte* entryPoint = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = entryPoint,
            };

            // No vertex buffers: the fullscreen triangle is generated from
            // gl_VertexIndex, exactly as the GL path draws it.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                // Clockwise is front when GL's counter-clockwise winding is read
                // in an unflipped Vulkan framebuffer.
                FrontFace = FrontFace.Clockwise,
                LineWidth = 1.0f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };

            var dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            // Dynamic rendering: attachment formats are named here instead of by
            // a render-pass object.
            Format format = colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &format,
            };

            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = layout,
            };

            Result result = api.CreateGraphicsPipelines(
                context.Device, default, 1, &createInfo, null, out Pipeline pipeline);
            Assert.Equal(Result.Success, result);
            return pipeline;
        }
        finally
        {
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entryPoint);
        }
    }
}
