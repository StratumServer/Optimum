using System;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Plan decision 9's one pipeline layout, shared by every program once shaders
/// target it (<see cref="SetConvention" />):
///
/// | set 0 | FrameGlobals dynamic UBO and the fixed frame textures; a normal set (dynamic buffers cannot be update-after-bind) |
/// | set 1 | the bindless texture table's layout (<see cref="BindlessTextureTable" />), not owned here |
/// | set 2 | FaceData, the animation blocks, the program record (dynamic) and the named-block range, a normal set built per draw |
/// | push  | <see cref="SetConvention.PushConstantBytes" /> for vertex and fragment |
///
/// Created once at device bring-up; destroyed at teardown after the device-idle
/// wait, before the table's set layout it names.
/// </summary>
internal sealed unsafe class SharedPipelineLayout : IDisposable
{
    public const ShaderStageFlags Stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private readonly VulkanContext _context;
    private readonly bool _ownsTextureSetLayout;
    private bool _disposed;

    public DescriptorSetLayout FrameSetLayout { get; }
    public DescriptorSetLayout TextureSetLayout { get; }
    public DescriptorSetLayout StorageSetLayout { get; }
    public PipelineLayout Layout { get; }

    /// <summary>
    /// A layout of the same shape with a set 1 layout of its own, which it owns: for
    /// programs built outside a device (tests). Pipelines built against it are
    /// compatible with any set 1 layout built from the same capacities.
    /// </summary>
    public static SharedPipelineLayout CreateStandalone(VulkanContext context)
    {
        uint[] capacities = BindlessKinds.ClampCapacities(context.Capabilities.DescriptorIndexing,
            DescriptorIndexingFloor.FrameTextures);
        DescriptorSetLayout textures = BindlessTextureTable.CreateSetLayout(context, capacities);
        try
        {
            return new SharedPipelineLayout(context, textures, ownsTextureSetLayout: true);
        }
        catch
        {
            context.Api.DestroyDescriptorSetLayout(context.Device, textures, null);
            throw;
        }
    }

    public SharedPipelineLayout(VulkanContext context, DescriptorSetLayout textureSetLayout)
        : this(context, textureSetLayout, ownsTextureSetLayout: false)
    {
    }

    private SharedPipelineLayout(VulkanContext context, DescriptorSetLayout textureSetLayout, bool ownsTextureSetLayout)
    {
        _context = context;
        _ownsTextureSetLayout = ownsTextureSetLayout;
        TextureSetLayout = textureSetLayout;
        Vk api = context.Api;

        DescriptorSetLayoutBinding[] frameBindings = FrameBindings();
        DescriptorSetLayoutBinding[] storageBindings = StorageBindings();

        FrameSetLayout = CreateSetLayout(frameBindings, "set 0 (frame)");
        try
        {
            StorageSetLayout = CreateSetLayout(storageBindings, "set 2 (storage)");
        }
        catch
        {
            api.DestroyDescriptorSetLayout(context.Device, FrameSetLayout, null);
            throw;
        }

        DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[SetConvention.SetCount];
        setLayouts[SetConvention.FrameSet] = FrameSetLayout;
        setLayouts[SetConvention.TextureSet] = TextureSetLayout;
        setLayouts[SetConvention.StorageSet] = StorageSetLayout;
        var pushConstants = new PushConstantRange
        {
            StageFlags = Stages,
            Offset = 0,
            Size = SetConvention.PushConstantBytes,
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = SetConvention.SetCount,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        PipelineLayout layout;
        Result result = api.CreatePipelineLayout(context.Device, &layoutInfo, null, &layout);
        if (result != Result.Success)
        {
            api.DestroyDescriptorSetLayout(context.Device, StorageSetLayout, null);
            api.DestroyDescriptorSetLayout(context.Device, FrameSetLayout, null);
            VulkanResult.Check(result, "vkCreatePipelineLayout for the shared layout");
        }
        Layout = layout;
    }

    /// <summary>Set 0: the FrameGlobals dynamic UBO and the fixed frame textures.</summary>
    internal static DescriptorSetLayoutBinding[] FrameBindings()
    {
        var bindings = new DescriptorSetLayoutBinding[1 + SetConvention.FrameTextures.Length];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = (uint)SetConvention.FrameGlobalsBinding,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            StageFlags = Stages,
        };
        for (int i = 0; i < SetConvention.FrameTextures.Length; i++)
        {
            bindings[1 + i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)SetConvention.FrameTextures[i].Value,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = SetConvention.FrameTextures[i].Capacity,
                StageFlags = Stages,
            };
        }
        return bindings;
    }

    /// <summary>
    /// Set 2: the storage buffers, the program record (a dynamic uniform buffer,
    /// docs/vulkan.md) and the named-block range, where a
    /// rewritten program's other named blocks sit as std140 storage buffers. Set 2 is
    /// a normal set, so the dynamic buffer is legal beside the update-after-bind set 1;
    /// the device floor counts it (<see cref="DescriptorIndexingFloor.RequiredDynamicUniformBuffers" />).
    /// </summary>
    internal static DescriptorSetLayoutBinding[] StorageBindings()
    {
        int named = SetConvention.NamedBlockLastBinding - SetConvention.NamedBlockFirstBinding + 1;
        var bindings = new DescriptorSetLayoutBinding[SetConvention.StorageBuffers.Length + 1 + named];
        int index = 0;
        foreach (SetConvention.Binding buffer in SetConvention.StorageBuffers)
        {
            bindings[index++] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)buffer.Value,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = buffer.Capacity,
                StageFlags = Stages,
            };
        }
        bindings[index++] = new DescriptorSetLayoutBinding
        {
            Binding = (uint)SetConvention.ProgramRecordBinding,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            StageFlags = Stages,
        };
        for (int binding = SetConvention.NamedBlockFirstBinding; binding <= SetConvention.NamedBlockLastBinding; binding++)
        {
            bindings[index++] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)binding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = Stages,
            };
        }
        return bindings;
    }

    /// <summary>The descriptor type set 2 declares at <paramref name="binding" />.</summary>
    public static DescriptorType StorageSetDescriptorType(uint binding) =>
        binding == SetConvention.ProgramRecordBinding ? DescriptorType.UniformBufferDynamic : DescriptorType.StorageBuffer;

    private DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding[] bindings, string what)
    {
        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };
            DescriptorSetLayout layout;
            VulkanResult.Check(_context.Api.CreateDescriptorSetLayout(_context.Device, &info, null, &layout),
                "vkCreateDescriptorSetLayout for the shared layout's " + what);
            return layout;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Vk api = _context.Api;
        api.DestroyPipelineLayout(_context.Device, Layout, null);
        api.DestroyDescriptorSetLayout(_context.Device, StorageSetLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, FrameSetLayout, null);
        if (_ownsTextureSetLayout) api.DestroyDescriptorSetLayout(_context.Device, TextureSetLayout, null);
    }
}
