using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Binds a program built outside a device the way the device's draw path does under
/// the shared pipeline layout (plan decision 9): each sampler's texture gets a slot in
/// a bindless table of its own and the index goes into the push block, set 1 is the
/// table's set, and set 2 carries the record buffer at its dynamic binding, a storage
/// buffer at every storage block's binding and a zero-filled placeholder everywhere else.
/// For component tests that draw through <see cref="ShaderProgramResources" /> with a
/// command buffer of their own.
/// </summary>
internal sealed unsafe class SharedLayoutTestBinding : IDisposable
{
    private sealed class StillClock : ITimelineClock
    {
        public ulong FrameRecorded => 1;
        public ulong TransferRecorded => 1;
        public ulong FrameCompleted => 0;
        public ulong TransferCompleted => 0;
    }

    /// <summary>What one sampler reads.</summary>
    public readonly record struct SampledTexture(int TextureId, SamplerState State,
        ImageLayout Layout = ImageLayout.ShaderReadOnlyOptimal);

    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly DescriptorCache _descriptors;
    private readonly VulkanBuffer _placeholder;

    public BindlessTextureTable Table { get; }

    public SharedLayoutTestBinding(VulkanContext context, TextureManager textures)
    {
        _context = context;
        _textures = textures;
        Table = new BindlessTextureTable(context, textures, new StillClock());
        _descriptors = new DescriptorCache(context);
        _placeholder = new VulkanBuffer(context, 64 * 1024,
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        new Span<byte>((void*)_placeholder.Mapped, 64 * 1024).Clear();
    }

    /// <summary>
    /// Before the rendering scope opens: the table's placeholders and every texture in
    /// <paramref name="sampled" /> that is read shader-read-only go to that layout.
    /// </summary>
    public void Transition(CommandBuffer commandBuffer, IEnumerable<SampledTexture> sampled)
    {
        for (int kind = 0; kind < BindlessKinds.Count; kind++)
        {
            VulkanTexture placeholder = _textures.Get(Table.PlaceholderTextureId((TextureKind)kind))!;
            _textures.TransitionTexture(commandBuffer, placeholder, ImageLayout.ShaderReadOnlyOptimal);
        }
        foreach (SampledTexture texture in sampled)
        {
            if (texture.Layout != ImageLayout.ShaderReadOnlyOptimal) continue;
            _textures.TransitionTexture(commandBuffer, _textures.Get(texture.TextureId)!, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    /// <summary>
    /// Binds sets 1 and 2 and pushes the slot indices for one draw of
    /// <paramref name="program" /> through its own pipeline layout. Every sampler the
    /// program declares must be in <paramref name="samplers" />; frame textures (set 0)
    /// are not supported here.
    /// </summary>
    public void Bind(CommandBuffer commandBuffer, ShaderProgramResources program,
        IReadOnlyDictionary<string, SampledTexture> samplers, VulkanBuffer? record = null, VulkanBuffer? storage = null)
    {
        Vk api = _context.Api;
        PipelineLayout layout = program.PipelineLayout;
        SharedPipelineLayout shape = program.StandaloneLayout
            ?? throw new InvalidOperationException("the program was built by a device; bind through the device");

        int pushSize = program.Interface.PushConstantSize;
        if (pushSize > 0)
        {
            var push = new byte[pushSize];
            foreach (SamplerBinding sampler in program.Interface.Samplers)
            {
                Assert.False(sampler.IsFrameTexture, "frame textures are not bound by this helper: " + sampler.Name);
                SampledTexture sampled = samplers[sampler.Name];
                uint slot = Table.Resolve(_textures.Get(sampled.TextureId), sampler.Kind, sampled.State, sampled.Layout);
                Assert.NotEqual(0u, slot);
                BitConverter.TryWriteBytes(push.AsSpan(sampler.PushOffset, ProgramInterfaceLayout.SlotBytes), slot);
            }
            Table.Flush();

            DescriptorSet textureSet = Table.Set;
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout,
                (uint)SetConvention.TextureSet, 1, &textureSet, 0, null);
            fixed (byte* bytes = push)
            {
                api.CmdPushConstants(commandBuffer, layout, SharedPipelineLayout.Stages, 0, (uint)pushSize, bytes);
            }
        }

        if (!program.Interface.UsesStorageSet) return;

        var buffers = new BufferBindingValue[SetConvention.StorageSetBindingCount];
        for (int binding = 0; binding < buffers.Length; binding++)
        {
            buffers[binding] = new BufferBindingValue((uint)binding, _placeholder.Handle, 0, _placeholder.Size, _placeholder.Id);
        }
        if (record != null)
        {
            buffers[SetConvention.ProgramRecordBinding] = new BufferBindingValue(SetConvention.ProgramRecordBinding,
                record.Handle, 0, (ulong)Math.Max(program.UniformShadow.Length, 4), record.Id);
        }
        if (storage != null)
        {
            foreach (BlockBinding block in program.Interface.StorageBlocks)
            {
                buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, storage.Handle, 0, storage.Size, storage.Id);
            }
        }

        DescriptorSet storageSet = _descriptors.Get(
            new DescriptorSetContents(0, SetConvention.StorageSet, Array.Empty<SamplerBindingValue>(), buffers),
            shape.StorageSetLayout);
        uint recordOffset = 0;
        api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout,
            (uint)SetConvention.StorageSet, 1, &storageSet, 1, &recordOffset);
    }

    public void Dispose()
    {
        _context.Api.DeviceWaitIdle(_context.Device);
        _descriptors.Dispose();
        _placeholder.Dispose();
        Table.Dispose();
    }
}
