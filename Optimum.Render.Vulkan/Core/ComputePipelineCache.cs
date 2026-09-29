using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Whether a compute program's binding is a storage image or a combined image sampler.</summary>
internal enum ComputeSlotKind
{
    Sampled,
    Storage,
}

/// <summary>One binding of a compute program's pass set.</summary>
internal readonly record struct ComputeSlot(uint Binding, ComputeSlotKind Kind);

/// <summary>What a compute program is built from.</summary>
internal sealed class ComputeProgramDescription
{
    public string Name = "compute";

    /// <summary>The compiled module (<see cref="Shaders.ShaderCompiler.CompileCompute" />), entry point <c>main</c>.</summary>
    public byte[] Spirv = Array.Empty<byte>();

    /// <summary>The pass set's bindings: set <see cref="ComputeProgram.PassSet" />, in any order.</summary>
    public ComputeSlot[] Slots = Array.Empty<ComputeSlot>();

    /// <summary>Bytes of the compute stage's push constant block; 0 for none. At most 128 (the spec minimum).</summary>
    public uint PushConstantBytes;

    /// <summary>
    /// The work group size used to size group counts from an image when the module does not
    /// state a literal one (<c>local_size_x_id</c>). A literal <c>local_size_x</c> /
    /// <c>local_size_y</c> in the module always wins (<see cref="SpirvLocalSize" />), so the
    /// two can never disagree.
    /// </summary>
    public uint LocalSizeX = 8;
    public uint LocalSizeY = 8;
}

/// <summary>
/// Reads a compute module's literal work group size (OpExecutionMode LocalSize) from its
/// SPIR-V. glslang also emits <c>gl_WorkGroupSize</c> as a composite decorated BuiltIn
/// WorkgroupSize: a plain OpConstantComposite for literal sizes, an OpSpecConstantComposite
/// when the size comes from specialization constants (<c>local_size_x_id</c>). The latter
/// overrides LocalSize (which then reads 1x1x1), so such a module has no literal size and
/// the reader says so.
/// </summary>
internal static class SpirvLocalSize
{
    private const uint MagicNumber = 0x07230203;
    private const ushort OpExecutionMode = 16;
    private const ushort OpSpecConstantComposite = 51;
    private const ushort OpDecorate = 71;
    private const uint ExecutionModeLocalSize = 17;
    private const uint DecorationBuiltIn = 11;
    private const uint BuiltInWorkgroupSize = 25;

    public static bool TryRead(ReadOnlySpan<byte> spirv, out uint x, out uint y, out uint z)
    {
        x = y = z = 0;
        if (spirv.Length < 20 || spirv.Length % 4 != 0) return false;
        ReadOnlySpan<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spirv);
        if (words[0] != MagicNumber) return false;

        bool found = false;
        uint workgroupSizeId = 0;
        var specComposites = new System.Collections.Generic.HashSet<uint>();
        int index = 5;
        while (index < words.Length)
        {
            uint word = words[index];
            int count = (int)(word >> 16);
            ushort opcode = (ushort)(word & 0xFFFF);
            if (count == 0 || index + count > words.Length) return false;
            if (opcode == OpExecutionMode && count >= 6 && words[index + 2] == ExecutionModeLocalSize)
            {
                x = words[index + 3];
                y = words[index + 4];
                z = words[index + 5];
                found = true;
            }
            else if (opcode == OpDecorate && count >= 4 && words[index + 2] == DecorationBuiltIn &&
                     words[index + 3] == BuiltInWorkgroupSize)
            {
                workgroupSizeId = words[index + 1];
            }
            else if (opcode == OpSpecConstantComposite && count >= 3)
            {
                specComposites.Add(words[index + 2]);
            }
            index += count;
        }

        if (!found || (workgroupSizeId != 0 && specComposites.Contains(workgroupSizeId)))
        {
            x = y = z = 0;
            return false;
        }
        return true;
    }
}

/// <summary>
/// A compute program: its module, the layout of its one pass set (set 0), its pipeline
/// layout with the compute push constant range, and one pipeline per set of
/// specialization constant values. Destroyed on the timeline after the last frame that
/// could have bound it.
/// </summary>
internal sealed unsafe class ComputeProgram : IDisposable
{
    /// <summary>The set a compute pass's bindings live in.</summary>
    public const uint PassSet = 0;

    public const uint MaxPushConstantBytes = 128;

    private readonly VulkanContext _context;
    private readonly Dictionary<SpecializationKey, Pipeline> _pipelines = new();
    private bool _disposed;

    public int Id { get; }
    public string Name { get; }
    public ShaderModule Module { get; }
    public DescriptorSetLayout SetLayout { get; }
    public PipelineLayout Layout { get; }
    public ComputeSlot[] Slots { get; }
    public uint PushConstantBytes { get; }
    public uint LocalSizeX { get; }
    public uint LocalSizeY { get; }

    public int PipelineCount => _pipelines.Count;

    public ComputeProgram(VulkanContext context, int id, ComputeProgramDescription description)
    {
        if (description.Spirv.Length == 0 || description.Spirv.Length % 4 != 0)
            throw new ArgumentException("compute program '" + description.Name + "' has no valid SPIR-V");
        if (description.PushConstantBytes > MaxPushConstantBytes)
            throw new ArgumentException("compute program '" + description.Name + "' pushes more than " + MaxPushConstantBytes + " bytes");

        _context = context;
        Id = id;
        Name = description.Name;
        Slots = (ComputeSlot[])description.Slots.Clone();
        PushConstantBytes = description.PushConstantBytes;
        if (SpirvLocalSize.TryRead(description.Spirv, out uint localX, out uint localY, out _))
        {
            LocalSizeX = Math.Max(1, localX);
            LocalSizeY = Math.Max(1, localY);
        }
        else
        {
            LocalSizeX = Math.Max(1, description.LocalSizeX);
            LocalSizeY = Math.Max(1, description.LocalSizeY);
        }
        Vk api = context.Api;

        fixed (byte* code = description.Spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)description.Spirv.Length,
                PCode = (uint*)code,
            };
            ShaderModule module;
            VulkanResult.Check(api.CreateShaderModule(context.Device, &moduleInfo, null, &module),
                "vkCreateShaderModule for compute program '" + Name + "'");
            Module = module;
        }

        var bindings = new DescriptorSetLayoutBinding[Slots.Length];
        for (int i = 0; i < Slots.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = Slots[i].Binding,
                DescriptorType = TypeOf(Slots[i].Kind),
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }
        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindings.Length == 0 ? null : bindingsPtr,
            };
            DescriptorSetLayout setLayout;
            Result result = api.CreateDescriptorSetLayout(context.Device, &setInfo, null, &setLayout);
            if (result != Result.Success)
            {
                api.DestroyShaderModule(context.Device, Module, null);
                VulkanResult.Check(result, "vkCreateDescriptorSetLayout for compute program '" + Name + "'");
            }
            SetLayout = setLayout;
        }

        DescriptorSetLayout set = SetLayout;
        var push = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = PushConstantBytes,
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &set,
            PushConstantRangeCount = PushConstantBytes > 0 ? 1u : 0u,
            PPushConstantRanges = PushConstantBytes > 0 ? &push : null,
        };
        PipelineLayout layout;
        Result layoutResult = api.CreatePipelineLayout(context.Device, &layoutInfo, null, &layout);
        if (layoutResult != Result.Success)
        {
            api.DestroyDescriptorSetLayout(context.Device, SetLayout, null);
            api.DestroyShaderModule(context.Device, Module, null);
            VulkanResult.Check(layoutResult, "vkCreatePipelineLayout for compute program '" + Name + "'");
        }
        Layout = layout;
    }

    public static DescriptorType TypeOf(ComputeSlotKind kind) =>
        kind == ComputeSlotKind.Storage ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler;

    public bool TryGetSlot(uint binding, out ComputeSlot slot)
    {
        foreach (ComputeSlot candidate in Slots)
        {
            if (candidate.Binding != binding) continue;
            slot = candidate;
            return true;
        }
        slot = default;
        return false;
    }

    /// <summary>The pipeline for these specialization values, compiled through the driver cache on first use.</summary>
    internal Pipeline PipelineFor(ReadOnlySpan<uint> specialization, PipelineCache driverCache, out bool compiled)
    {
        var probe = new SpecializationKey(specialization.ToArray());
        if (_pipelines.TryGetValue(probe, out Pipeline existing))
        {
            compiled = false;
            return existing;
        }

        Pipeline pipeline = Compile(specialization, driverCache);
        _pipelines[probe] = pipeline;
        compiled = true;
        return pipeline;
    }

    private Pipeline Compile(ReadOnlySpan<uint> specialization, PipelineCache driverCache)
    {
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var entries = new SpecializationMapEntry[specialization.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new SpecializationMapEntry((uint)i, (uint)(i * sizeof(uint)), sizeof(uint));
            }
            uint[] values = specialization.ToArray();

            fixed (SpecializationMapEntry* entriesPtr = entries)
            fixed (uint* valuesPtr = values)
            {
                var info = new SpecializationInfo
                {
                    MapEntryCount = (uint)entries.Length,
                    PMapEntries = entries.Length == 0 ? null : entriesPtr,
                    DataSize = (nuint)(values.Length * sizeof(uint)),
                    PData = values.Length == 0 ? null : valuesPtr,
                };
                var createInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = Module,
                        PName = entry,
                        PSpecializationInfo = entries.Length == 0 ? null : &info,
                    },
                    Layout = Layout,
                };
                Pipeline pipeline;
                Result result = _context.Api.CreateComputePipelines(_context.Device, driverCache, 1, &createInfo, null, &pipeline);
                if (result != Result.Success)
                {
                    throw new InvalidOperationException("vkCreateComputePipelines failed for '" + Name + "': " + result);
                }
                return pipeline;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Vk api = _context.Api;
        foreach (Pipeline pipeline in _pipelines.Values) api.DestroyPipeline(_context.Device, pipeline, null);
        _pipelines.Clear();
        api.DestroyPipelineLayout(_context.Device, Layout, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
        if (Module.Handle != 0) api.DestroyShaderModule(_context.Device, Module, null);
    }

    /// <summary>Specialization values compared element by element.</summary>
    private readonly struct SpecializationKey : IEquatable<SpecializationKey>
    {
        private readonly uint[] _values;
        private readonly int _hash;

        public SpecializationKey(uint[] values)
        {
            _values = values;
            var hash = new HashCode();
            foreach (uint value in values) hash.Add(value);
            _hash = hash.ToHashCode();
        }

        public bool Equals(SpecializationKey other) =>
            _hash == other._hash && _values.AsSpan().SequenceEqual(other._values);

        public override bool Equals(object? obj) => obj is SpecializationKey other && Equals(other);
        public override int GetHashCode() => _hash;
    }
}

/// <summary>
/// The compute programs of a device and their pipelines. Pipelines compile through the
/// driver's <see cref="PipelineCache" /> the graphics cache owns, so one cache file
/// warms both kinds on the next launch. Render thread only.
/// </summary>
internal sealed class ComputePipelineCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Func<PipelineCache> _driverCache;
    private readonly Dictionary<int, ComputeProgram> _programs = new();
    private int _nextId = 1;
    private bool _disposed;

    public ComputePipelineCache(VulkanContext context, Func<PipelineCache> driverCache)
    {
        _context = context;
        _driverCache = driverCache;
    }

    public int ProgramCount => _programs.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public int Create(ComputeProgramDescription description)
    {
        int id = _nextId++;
        _programs[id] = new ComputeProgram(_context, id, description);
        return id;
    }

    public ComputeProgram? Get(int id) => _programs.TryGetValue(id, out ComputeProgram? program) ? program : null;

    /// <summary>Removes a program; the caller retires it on the timeline (a submitted frame may still bind it).</summary>
    public ComputeProgram? Remove(int id) => _programs.Remove(id, out ComputeProgram? program) ? program : null;

    public Pipeline PipelineFor(ComputeProgram program, ReadOnlySpan<uint> specialization)
    {
        Pipeline pipeline = program.PipelineFor(specialization, _driverCache(), out bool compiled);
        if (compiled) Misses++;
        else Hits++;
        return pipeline;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (ComputeProgram program in _programs.Values) program.Dispose();
        _programs.Clear();
    }
}
