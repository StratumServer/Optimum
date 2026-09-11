using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Everything the GPU needs for one linked shader program: the modules, the
/// descriptor set layouts derived from its interface, the pipeline layout, and
/// the CPU-side shadow of its uniform block.
///
/// The shadow buffer is what makes GL's uniform protocol work. The game sets
/// uniforms one at a time by name, at any point before a draw, and expects the
/// values to persist for the life of the program. So writes land in this buffer,
/// and a draw copies it into the frame's uniform ring only when something
/// changed.
/// </summary>
internal sealed unsafe class ShaderProgramResources : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public int ProgramId { get; }
    public ProgramInterfaceLayout Interface { get; }

    public Dictionary<EnumShaderType, ShaderModule> Modules { get; } = new();

    /// <summary>Set 0 uniforms, set 1 samplers, set 2 storage buffers.</summary>
    public DescriptorSetLayout[] SetLayouts { get; } = new DescriptorSetLayout[3];
    public PipelineLayout PipelineLayout { get; private set; }

    /// <summary>CPU mirror of the generated uniform block.</summary>
    public byte[] UniformShadow { get; }

    /// <summary>True when the shadow has changed since it was last uploaded.</summary>
    public bool UniformsDirty { get; private set; } = true;

    /// <summary>
    /// Which texture unit each sampler uniform points at. In GL this is just an
    /// int uniform; here it is the link between a bound texture and a descriptor.
    /// </summary>
    public Dictionary<string, int> SamplerUnits { get; } = new(StringComparer.Ordinal);

    public ShaderProgramResources(
        VulkanContext context, int programId, TranslatedProgram translated)
    {
        _context = context;
        ProgramId = programId;
        Interface = translated.Layout;
        UniformShadow = translated.Layout.CreateShadowBuffer();

        foreach (KeyValuePair<EnumShaderType, byte[]> stage in translated.Spirv)
        {
            Modules[stage.Key] = CreateModule(stage.Value);
        }

        // Sampler uniforms default to the unit matching their binding, which is
        // the order the game's own texture-location bookkeeping assigns.
        foreach (SamplerBinding sampler in Interface.Samplers)
        {
            SamplerUnits[sampler.Name] = sampler.Binding;
        }

        CreateSetLayouts();
        CreatePipelineLayout();
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (_context.Api.CreateShaderModule(_context.Device, &createInfo, null, out ShaderModule module)
                != Result.Success)
            {
                throw new InvalidOperationException("vkCreateShaderModule failed");
            }
            return module;
        }
    }

    /// <summary>
    /// Builds one layout per set. Stage visibility is set to all graphics stages
    /// rather than tracked per binding: the sets are tiny, the cost of a wider
    /// visibility is nil, and a uniform shared between stages - which GL makes
    /// routine - would otherwise need its visibility recomputed on every link.
    /// </summary>
    private void CreateSetLayouts()
    {
        const ShaderStageFlags allGraphics =
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.GeometryBit;

        var uniformBindings = new List<DescriptorSetLayoutBinding>();
        if (Interface.HasUniformBlock)
        {
            uniformBindings.Add(new DescriptorSetLayoutBinding
            {
                Binding = ProgramInterfaceLayout.DefaultBlockBinding,
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                StageFlags = allGraphics,
            });
        }
        // A block the shader declares for itself is dynamic for the same reason
        // the generated one is: the client re-uploads it between draws that are
        // only recorded, so each draw needs its own slice of the frame's uniform
        // ring, reached through an offset rather than through a set of its own.
        foreach (BlockBinding block in Interface.UniformBlocks)
        {
            uniformBindings.Add(new DescriptorSetLayoutBinding
            {
                Binding = (uint)block.Binding,
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                StageFlags = allGraphics,
            });
        }

        var samplerBindings = new List<DescriptorSetLayoutBinding>();
        foreach (SamplerBinding sampler in Interface.Samplers)
        {
            samplerBindings.Add(new DescriptorSetLayoutBinding
            {
                Binding = (uint)sampler.Binding,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = allGraphics,
            });
        }

        var storageBindings = new List<DescriptorSetLayoutBinding>();
        foreach (BlockBinding block in Interface.StorageBlocks)
        {
            storageBindings.Add(new DescriptorSetLayoutBinding
            {
                Binding = (uint)block.Binding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = allGraphics,
            });
        }

        SetLayouts[ProgramInterfaceLayout.DefaultBlockSet] = CreateSetLayout(uniformBindings);
        SetLayouts[ProgramInterfaceLayout.SamplerSet] = CreateSetLayout(samplerBindings);
        SetLayouts[ProgramInterfaceLayout.StorageSet] = CreateSetLayout(storageBindings);
    }

    private DescriptorSetLayout CreateSetLayout(List<DescriptorSetLayoutBinding> bindings)
    {
        // An empty set is still created rather than skipped, so set numbering
        // stays fixed: samplers are always set 1 whether or not the program has
        // uniforms, which keeps the rewriter's binding decisions valid.
        DescriptorSetLayoutBinding[] array = bindings.ToArray();
        fixed (DescriptorSetLayoutBinding* bindingsPtr = array)
        {
            var createInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)array.Length,
                PBindings = array.Length == 0 ? null : bindingsPtr,
            };

            if (_context.Api.CreateDescriptorSetLayout(
                    _context.Device, &createInfo, null, out DescriptorSetLayout layout) != Result.Success)
            {
                throw new InvalidOperationException("vkCreateDescriptorSetLayout failed");
            }
            return layout;
        }
    }

    private void CreatePipelineLayout()
    {
        fixed (DescriptorSetLayout* setLayouts = SetLayouts)
        {
            var createInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)SetLayouts.Length,
                PSetLayouts = setLayouts,
            };

            if (_context.Api.CreatePipelineLayout(
                    _context.Device, &createInfo, null, out PipelineLayout layout) != Result.Success)
            {
                throw new InvalidOperationException("vkCreatePipelineLayout failed");
            }
            PipelineLayout = layout;
        }
    }

    // ------------------------------------------------------------------ uniforms

    /// <summary>
    /// The first sampler location. Sampler locations run downwards from here so
    /// they can never collide with a uniform block offset, which is always zero
    /// or positive, nor with GL's "not found" answer of -1.
    /// </summary>
    private const int FirstSamplerLocation = -2;

    /// <summary>Whether a location handed out by <see cref="LocationOf" /> names a sampler.</summary>
    public static bool IsSamplerLocation(int location) => location <= FirstSamplerLocation;

    private static int SamplerIndexOf(int location) => FirstSamplerLocation - location;

    /// <summary>
    /// Resolves a uniform name to an opaque location, the way glGetUniformLocation
    /// does.
    ///
    /// Samplers are not members of the generated block - they are descriptor
    /// bindings - but the client looks every declared uniform up by name and
    /// treats a -1 as "the shader does not use this". Returning -1 for samplers
    /// would tell it that every texture uniform in the game is unused, so they
    /// get locations of their own from a disjoint range.
    /// </summary>
    public int LocationOf(string name)
    {
        if (Interface.MembersByName.TryGetValue(name, out UniformMember? member))
        {
            return member.Offset;
        }

        for (int i = 0; i < Interface.Samplers.Count; i++)
        {
            if (string.Equals(Interface.Samplers[i].Name, name, StringComparison.Ordinal))
            {
                return FirstSamplerLocation - i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Points the sampler at <paramref name="location" /> at a texture unit.
    ///
    /// GL assigns a sampler's unit by writing an int to its uniform location, so
    /// a client that resolved a location and set it as an int lands here rather
    /// than writing into the uniform block.
    /// </summary>
    public void SetSamplerUnitByLocation(int location, int unit)
    {
        int index = SamplerIndexOf(location);
        if (index < 0 || index >= Interface.Samplers.Count) return;

        SamplerUnits[Interface.Samplers[index].Name] = unit;
    }

    /// <summary>Writes raw bytes at an offset previously handed out by <see cref="LocationOf" />.</summary>
    public void SetUniform(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > UniformShadow.Length) return;

        Span<byte> destination = UniformShadow.AsSpan(offset, data.Length);
        if (data.SequenceEqual(destination)) return;

        data.CopyTo(destination);
        UniformsDirty = true;
    }

    public void MarkUniformsClean() => UniformsDirty = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Vk api = _context.Api;
        api.DestroyPipelineLayout(_context.Device, PipelineLayout, null);

        foreach (DescriptorSetLayout layout in SetLayouts)
        {
            if (layout.Handle != 0) api.DestroyDescriptorSetLayout(_context.Device, layout, null);
        }
        foreach (ShaderModule module in Modules.Values)
        {
            api.DestroyShaderModule(_context.Device, module, null);
        }
    }
}
