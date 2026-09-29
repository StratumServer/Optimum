using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Everything the GPU needs for one linked shader program: the modules and the
/// CPU-side shadow of its program record. Every program's pipelines are built
/// against the one shared pipeline layout (plan decision 9, <see cref="SharedPipelineLayout" />),
/// so a program owns no set layouts and no pipeline layout of its own.
///
/// The shadow buffer is what makes GL's uniform protocol work. The game sets
/// uniforms one at a time by name, at any point before a draw, and expects the
/// values to persist for the life of the program. So writes land in this buffer,
/// and a draw copies it into the frame's uniform ring only when something
/// changed since the snapshot it last took.
///
/// Values every program shares - fog, light, shadow cascades, warp, sky - are not
/// in this buffer at all: they live in the device's frame block (set 0,
/// <see cref="FrameGlobals" />), and their locations point there.
/// </summary>
internal sealed unsafe class ShaderProgramResources : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public int ProgramId { get; }
    public ProgramInterfaceLayout Interface { get; }

    public Dictionary<EnumShaderType, ShaderModule> Modules { get; } = new();

    /// <summary>The shared pipeline layout this program's pipelines are created against. Not owned.</summary>
    public PipelineLayout PipelineLayout { get; }

    /// <summary>
    /// A shared layout of the program's own, for a program built outside a device (tests):
    /// the same shape as the device's, set 1 included. Null for a device's programs.
    /// </summary>
    public SharedPipelineLayout? StandaloneLayout { get; }

    /// <summary>CPU mirror of the program record.</summary>
    public byte[] UniformShadow { get; }

    /// <summary>
    /// CPU mirror of a native program's push block when it holds members besides sampler slots; null
    /// otherwise. A draw copies it into the device's push shadow before the slots are resolved over it.
    /// </summary>
    public byte[]? PushShadow { get; }

    /// <summary>The specialization constants every pipeline of this program is created with; null for a rewritten program.</summary>
    public NativeSpecialization? Specialization { get; }

    /// <summary>Whether the program was linked from the native manifest.</summary>
    public bool IsNative { get; }

    /// <summary>Bumped by every write that changes the shadow.</summary>
    public uint UniformVersion { get; private set; } = 1;

    /// <summary>Which frame's ring holds the last snapshot of the shadow, taken at which version, where.</summary>
    public uint SnapshotFrame { get; private set; }
    public uint SnapshotVersion { get; private set; }
    public uint SnapshotOffset { get; private set; }

    /// <summary>
    /// Which texture unit each sampler uniform points at. In GL this is just an
    /// int uniform; here it is the link between a bound texture and a descriptor.
    /// </summary>
    private readonly Dictionary<string, int> _samplerIndices = new(StringComparer.Ordinal);

    /// <summary>Mutable unit assignments in sampler declaration order.</summary>
    internal int[] SamplerUnitsByIndex { get; }

    /// <summary>Declaration-order names, fixed for this linked program.</summary>
    public string[] SamplerNames { get; }

    /// <param name="sharedLayout">
    /// The device's shared pipeline layout. Programs built outside a device - in
    /// tests - pass none and get a <see cref="StandaloneLayout" /> of the same shape.
    /// </param>
    public ShaderProgramResources(
        VulkanContext context, int programId, TranslatedProgram translated, PipelineLayout sharedLayout = default)
    {
        _context = context;
        ProgramId = programId;
        Interface = translated.Layout;
        UniformShadow = translated.Layout.CreateShadowBuffer();
        PushShadow = translated.Layout.CreatePushShadow();
        Specialization = translated.Specialization;
        IsNative = translated.IsNative;

        foreach (KeyValuePair<EnumShaderType, byte[]> stage in translated.Spirv)
        {
            Modules[stage.Key] = CreateModule(stage.Value);
        }
        SourceHash = HashSpirv(translated.Spirv, translated.Specialization);

        // Sampler uniforms default to the unit matching their declaration order,
        // which is the order the game's own texture-location bookkeeping assigns.
        SamplerNames = new string[Interface.Samplers.Count];
        SamplerUnitsByIndex = new int[Interface.Samplers.Count];
        for (int i = 0; i < Interface.Samplers.Count; i++)
        {
            SamplerBinding sampler = Interface.Samplers[i];
            SamplerNames[i] = sampler.Name;
            _samplerIndices[sampler.Name] = i;
            SamplerUnitsByIndex[i] = sampler.Order;
        }

        if (sharedLayout.Handle == 0)
        {
            StandaloneLayout = SharedPipelineLayout.CreateStandalone(context);
            sharedLayout = StandaloneLayout.Layout;
        }
        PipelineLayout = sharedLayout;
    }

    /// <summary>
    /// A hash of every stage's SPIR-V, in stage order, and of a native program's specialization
    /// data. The rewriter's SPIR-V has its defines resolved; a native module's settings are its
    /// specialization constants, so they are part of the program's identity. Two programs with
    /// this hash build the same pipelines for the same state - what the pipeline-key log matches
    /// on across launches.
    /// </summary>
    public UInt128 SourceHash { get; }

    private static UInt128 HashSpirv(Dictionary<EnumShaderType, byte[]> spirv, NativeSpecialization? specialization)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var stages = new List<EnumShaderType>(spirv.Keys);
        stages.Sort();
        Span<byte> header = stackalloc byte[8];
        foreach (EnumShaderType stage in stages)
        {
            BitConverter.TryWriteBytes(header, (int)stage);
            BitConverter.TryWriteBytes(header[4..], spirv[stage].Length);
            hash.AppendData(header);
            hash.AppendData(spirv[stage]);
        }
        if (specialization != null)
        {
            foreach (NativeSpecialization.Entry entry in specialization.Entries)
            {
                BitConverter.TryWriteBytes(header, entry.Id);
                BitConverter.TryWriteBytes(header[4..], entry.Offset);
                hash.AppendData(header);
            }
            hash.AppendData(specialization.Data);
        }
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new UInt128(BitConverter.ToUInt64(digest[..8]), BitConverter.ToUInt64(digest.Slice(8, 8)));
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

    // ------------------------------------------------------------------ uniforms

    /// <summary>
    /// The first sampler location. Sampler locations run downwards from here so
    /// they can never collide with a uniform block offset, which is always zero
    /// or positive, nor with GL's "not found" answer of -1.
    /// </summary>
    private const int FirstSamplerLocation = -2;

    /// <summary>
    /// Where frame block locations start: the member's offset in the shared block
    /// plus this. Far above any per-program block, so the two ranges never meet.
    /// </summary>
    public const int FrameLocationBase = 1 << 28;

    /// <summary>
    /// Where a native program's push-member locations start: the member's offset in the push
    /// block plus this. Above any record offset and below <see cref="FrameLocationBase" />.
    /// </summary>
    public const int PushLocationBase = 1 << 27;

    /// <summary>Whether a location handed out by <see cref="LocationOf" /> is a push-block member.</summary>
    public static bool IsPushLocation(int location) => location >= PushLocationBase && location < FrameLocationBase;

    /// <summary>Whether a location handed out by <see cref="LocationOf" /> names a sampler.</summary>
    public static bool IsSamplerLocation(int location) => location <= FirstSamplerLocation;

    /// <summary>Whether a location handed out by <see cref="LocationOf" /> is in the shared frame block.</summary>
    public static bool IsFrameLocation(int location) => location >= FrameLocationBase;

    private static int SamplerIndexOf(int location) => FirstSamplerLocation - location;

    /// <summary>
    /// Resolves a uniform name to an opaque location, the way glGetUniformLocation
    /// does.
    ///
    /// Samplers are not members of the program record - they are push slots or
    /// frame textures - but the client looks every declared uniform up by name and
    /// treats a -1 as "the shader does not use this". Returning -1 for samplers
    /// would tell it that every texture uniform in the game is unused, so they
    /// get locations of their own from a disjoint range. Members of the shared
    /// frame block get a third range.
    /// </summary>
    public int LocationOf(string name)
    {
        if (Interface.FrameMemberDeclaredLengths.ContainsKey(name) &&
            FrameGlobals.TryGetMember(name, out UniformMember frameMember))
        {
            return FrameLocationBase + frameMember.Offset;
        }

        if (Interface.PushMembersByName.TryGetValue(name, out UniformMember? pushMember))
        {
            return PushLocationBase + pushMember.Offset;
        }

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

        SamplerUnitsByIndex[index] = unit;
    }

    public void SetSamplerUnitByName(string name, int unit)
    {
        if (_samplerIndices.TryGetValue(name, out int index))
            SamplerUnitsByIndex[index] = unit;
    }

    /// <summary>Writes raw bytes at an offset previously handed out by <see cref="LocationOf" />.</summary>
    public void SetUniform(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > UniformShadow.Length) return;

        Span<byte> destination = UniformShadow.AsSpan(offset, data.Length);
        if (data.SequenceEqual(destination)) return;

        data.CopyTo(destination);
        UniformVersion++;
    }

    /// <summary>Writes raw bytes at a push location previously handed out by <see cref="LocationOf" />.</summary>
    public void SetPushUniform(int location, ReadOnlySpan<byte> data)
    {
        int offset = location - PushLocationBase;
        if (PushShadow == null || offset < 0 || offset + data.Length > PushShadow.Length) return;
        data.CopyTo(PushShadow.AsSpan(offset, data.Length));
    }

    /// <summary>Whether the shadow's current contents already sit in <paramref name="frame" />'s ring.</summary>
    public bool HasSnapshotFor(uint frame) => SnapshotFrame == frame && SnapshotVersion == UniformVersion;

    public void NoteSnapshot(uint frame, uint offset)
    {
        SnapshotFrame = frame;
        SnapshotVersion = UniformVersion;
        SnapshotOffset = offset;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Vk api = _context.Api;
        // A device's shared layout belongs to the device.
        StandaloneLayout?.Dispose();
        foreach (ShaderModule module in Modules.Values)
        {
            api.DestroyShaderModule(_context.Device, module, null);
        }
    }
}
