using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Creates graphics pipelines on demand and remembers them.
///
/// Vulkan wants pipeline state baked ahead of time; GL lets it change one call
/// before a draw. Bridging that is the job here: state changes are recorded by
/// <see cref="GlStateTracker" />, and the first draw that needs a given
/// combination compiles a pipeline for it. Because Vulkan 1.3 makes viewport,
/// scissor, cull, front face, depth and stencil dynamic, the combinations that
/// remain are few - roughly a few hundred across the whole game - and after the
/// first minutes of play the cache stops growing.
///
/// A driver-side <see cref="Silk.NET.Vulkan.PipelineCache" /> backs it so that
/// even those first compiles are cheap on a second run.
/// </summary>
internal sealed unsafe class GraphicsPipelineCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
    private readonly Silk.NET.Vulkan.PipelineCache _driverCache;
    private bool _disposed;

    /// <summary>How many pipelines have been compiled, for diagnostics.</summary>
    public int Count => _pipelines.Count;

    /// <summary>How many lookups were served from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>How many lookups had to compile.</summary>
    public long Misses { get; private set; }

    public GraphicsPipelineCache(VulkanContext context, byte[]? initialData = null)
    {
        _context = context;

        fixed (byte* data = initialData)
        {
            var createInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initialData?.Length ?? 0),
                PInitialData = initialData is { Length: > 0 } ? data : null,
            };

            // A rejected blob is not an error: the driver simply starts cold.
            if (context.Api.CreatePipelineCache(
                    context.Device, &createInfo, null, out Silk.NET.Vulkan.PipelineCache cache) == Result.Success)
            {
                _driverCache = cache;
            }
        }
    }

    /// <summary>Everything a pipeline needs that is not already in the key.</summary>
    internal sealed class PipelineRequest
    {
        public required ShaderProgramResources Program { get; init; }
        public required VertexLayoutDescription VertexLayout { get; init; }
        public required RenderTargetFormats Targets { get; init; }
        public required AttachmentBlend[] Blend { get; init; }
        public required PolygonMode PolygonMode { get; init; }
        public required PrimitiveTopology Topology { get; init; }
    }

    public Pipeline Get(PipelineKey key, PipelineRequest request)
    {
        if (_pipelines.TryGetValue(key, out Pipeline existing))
        {
            Hits++;
            return existing;
        }

        Misses++;
        Pipeline pipeline = Create(request);
        _pipelines[key] = pipeline;
        return pipeline;
    }

    private Pipeline Create(PipelineRequest request)
    {
        Vk api = _context.Api;
        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var stages = new List<PipelineShaderStageCreateInfo>();
        foreach (KeyValuePair<EnumShaderType, ShaderModule> module in request.Program.Modules)
        {
            stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = module.Key switch
                {
                    EnumShaderType.VertexShader => ShaderStageFlags.VertexBit,
                    EnumShaderType.FragmentShader => ShaderStageFlags.FragmentBit,
                    _ => ShaderStageFlags.GeometryBit,
                },
                Module = module.Value,
                PName = entryPoint,
            });
        }

        var bindings = new VertexInputBindingDescription[request.VertexLayout.Bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            VertexBinding binding = request.VertexLayout.Bindings[i];
            bindings[i] = new VertexInputBindingDescription
            {
                Binding = binding.Binding,
                Stride = binding.Stride,
                InputRate = binding.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex,
            };
        }

        var attributes = new VertexInputAttributeDescription[request.VertexLayout.Attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            VertexAttribute attribute = request.VertexLayout.Attributes[i];
            attributes[i] = new VertexInputAttributeDescription
            {
                Location = attribute.Location,
                Binding = attribute.Binding,
                Format = attribute.Format,
                Offset = attribute.Offset,
            };
        }

        var blendAttachments = new PipelineColorBlendAttachmentState[request.Targets.ColorFormats.Length];
        for (int i = 0; i < blendAttachments.Length; i++)
        {
            AttachmentBlend blend = i < request.Blend.Length ? request.Blend[i] : AttachmentBlend.Default;
            blendAttachments[i] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blend.Enabled,
                SrcColorBlendFactor = blend.SrcColor,
                DstColorBlendFactor = blend.DstColor,
                ColorBlendOp = blend.ColorOp,
                SrcAlphaBlendFactor = blend.SrcAlpha,
                DstAlphaBlendFactor = blend.DstAlpha,
                AlphaBlendOp = blend.AlphaOp,
                ColorWriteMask = blend.WriteMask,
            };
        }

        // Everything Vulkan 1.3 lets us change without a new pipeline. Keeping
        // this list wide is what keeps the cache small.
        var dynamicStates = new[]
        {
            DynamicState.Viewport,
            DynamicState.Scissor,
            DynamicState.LineWidth,
            DynamicState.CullMode,
            DynamicState.FrontFace,
            DynamicState.PrimitiveTopology,
            DynamicState.DepthTestEnable,
            DynamicState.DepthWriteEnable,
            DynamicState.DepthCompareOp,
            DynamicState.StencilTestEnable,
            DynamicState.StencilOp,
            DynamicState.StencilCompareMask,
            DynamicState.StencilWriteMask,
            DynamicState.StencilReference,
        };

        try
        {
            fixed (PipelineShaderStageCreateInfo* stagesPtr = stages.ToArray())
            fixed (VertexInputBindingDescription* bindingsPtr = bindings)
            fixed (VertexInputAttributeDescription* attributesPtr = attributes)
            fixed (PipelineColorBlendAttachmentState* blendPtr = blendAttachments)
            fixed (DynamicState* dynamicPtr = dynamicStates)
            fixed (Format* colorFormatsPtr = request.Targets.ColorFormats)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)bindings.Length,
                    PVertexBindingDescriptions = bindings.Length == 0 ? null : bindingsPtr,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = attributes.Length == 0 ? null : attributesPtr,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = request.Topology,
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
                    PolygonMode = request.PolygonMode,
                    // Cull mode and front face are dynamic; these are placeholders.
                    CullMode = CullModeFlags.None,
                    FrontFace = GlStateTracker.FrontFace,
                    LineWidth = 1.0f,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };

                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = false,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                    StencilTestEnable = false,
                };

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = (uint)blendAttachments.Length,
                    PAttachments = blendAttachments.Length == 0 ? null : blendPtr,
                };

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates = dynamicPtr,
                };

                // Dynamic rendering names the attachment formats here, so there
                // is no render pass or framebuffer object anywhere in the design.
                var renderingInfo = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = (uint)request.Targets.ColorFormats.Length,
                    PColorAttachmentFormats = request.Targets.ColorFormats.Length == 0 ? null : colorFormatsPtr,
                    DepthAttachmentFormat = request.Targets.DepthFormat,
                };

                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &renderingInfo,
                    StageCount = (uint)stages.Count,
                    PStages = stagesPtr,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = request.Program.PipelineLayout,
                };

                Result result = api.CreateGraphicsPipelines(
                    _context.Device, _driverCache, 1, &createInfo, null, out Pipeline pipeline);

                if (result != Result.Success)
                {
                    throw new InvalidOperationException("vkCreateGraphicsPipelines failed: " + result);
                }
                return pipeline;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    /// <summary>
    /// The driver's cache blob, to be written next to the SPIR-V cache so the
    /// next run starts warm.
    /// </summary>
    public byte[] SerializeDriverCache()
    {
        if (_driverCache.Handle == 0) return Array.Empty<byte>();

        nuint size = 0;
        _context.Api.GetPipelineCacheData(_context.Device, _driverCache, ref size, null);
        if (size == 0) return Array.Empty<byte>();

        var data = new byte[(int)size];
        fixed (byte* dataPtr = data)
        {
            _context.Api.GetPipelineCacheData(_context.Device, _driverCache, ref size, dataPtr);
        }
        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Vk api = _context.Api;
        foreach (Pipeline pipeline in _pipelines.Values)
        {
            api.DestroyPipeline(_context.Device, pipeline, null);
        }
        _pipelines.Clear();

        if (_driverCache.Handle != 0)
        {
            api.DestroyPipelineCache(_context.Device, _driverCache, null);
        }
    }
}
