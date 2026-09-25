using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // ------------------------------------------------------------------- barriers

    /// <summary>
    /// The frame thread's barriers for sampled textures and feedback snapshots:
    /// every texture a draw samples moves in one barrier command.
    /// </summary>
    private Graph.BarrierBatcher _barriers = null!;

    private void SnapshotColorAttachment(CommandBuffer commandBuffer, int textureId, VulkanTexture source)
    {
        if (_sampledTextureOverrides.ContainsKey(textureId)) return;

        _targets.EndRendering(commandBuffer);
        // A pooled ReadSelf copy for this pass (FeedbackCopyPool).
        int copyId = _readSelfCopies.Acquire(new Graph.FeedbackCopyDesc(source.Width, source.Height, source.Format,
            source.MipLevels, source.Layers, source.Cube));
        VulkanStats.NoteReadSelfCopy();
        VulkanTexture copy = _textures.Get(copyId)!;
        copy.State = source.State;

        // Source, copy, and any texture this draw already queued: one command.
        _textures.Require(_barriers, commandBuffer, source, Graph.ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commandBuffer, copy, Graph.ResourceUsage.TransferDst);
        _barriers.Flush(commandBuffer);
        for (uint level = 0; level < source.MipLevels; level++)
        {
            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(source.Aspect, level, 0, source.Layers),
                DstSubresource = new ImageSubresourceLayers(copy.Aspect, level, 0, copy.Layers),
                Extent = new Extent3D(Math.Max(1u, source.Width >> (int)level),
                    Math.Max(1u, source.Height >> (int)level), 1),
            };
            _context.Api.CmdCopyImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
                copy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        }
        _textures.Require(_barriers, commandBuffer, copy, Graph.ResourceUsage.SampleFragment);
        _barriers.Flush(commandBuffer);
        _sampledTextureOverrides.Add(textureId, copyId);
        if (RenderTrace.Enabled)
            RenderTrace.Write("snapshot texture=" + textureId + " copy=" + copyId +
                " size=" + source.Width + "x" + source.Height + " mips=" + source.MipLevels);
        // EnsureRendering transitions the source back to its attachment layout.
    }

    /// <summary>
    /// Copies a client UBO's shadow into this frame's uniform ring, so the draw
    /// about to be recorded reads the contents the client uploaded for it rather
    /// than whatever the last upload of the frame left behind.
    ///
    /// One snapshot serves every draw that follows with the block unchanged: the
    /// pairing of frame and version is what makes a thousand chunk draws sharing
    /// one block cost one copy rather than a thousand. A new frame invalidates it
    /// because the ring's cursor is reset. A partial submit does not: the frame
    /// stays in the same slot, the cursor keeps counting, and the snapshot's bytes
    /// are untouched until that slot starts its next frame.
    /// </summary>
    private bool TrySnapshotClientBlock(
        ClientUniformBuffer ubo, ShaderProgramResources program, out uint offset)
    {
        if (ubo.HasSnapshotFor(_frameCounter))
        {
            offset = ubo.SnapshotOffset;
            return true;
        }

        if (!_frames.Current.TryAllocateUniforms(ubo.Shadow.Length, out RingAllocation allocation))
        {
            ReportUniformExhaustion(program, "block '" + ubo.BlockName + "'");
            offset = 0;
            return false;
        }

        fixed (byte* source = ubo.Shadow)
        {
            System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                ubo.Shadow.Length, ubo.Shadow.Length);
        }
        ubo.NoteSnapshot(_frameCounter, allocation.Offset);
        offset = allocation.Offset;
        return true;
    }

    /// <summary>
    /// Reports that the frame's uniform ring ran out. Said once per frame so a
    /// long frame does not flood the log.
    /// </summary>
    private void ReportUniformExhaustion(ShaderProgramResources program, string what)
    {
        if (_uniformExhaustionReportedFrame == _frameCounter) return;

        _uniformExhaustionReportedFrame = _frameCounter;
        string message = VulkanContext.ErrorPrefix + "uniform ring exhausted in frame " + _frameCounter +
            " (" + _frames.Current.UniformBytesUsed + " of " + _frames.Current.UniformCapacity +
            " bytes used) at a draw with program " + program.ProgramId +
            " '" + ProgramNameOf(program.ProgramId) + "' for " + what;
        AddDiagnostic(SanitiseForClientLog(message));
        MirrorValidationMessage(message);
    }

    // ----------------------------------------------- shared layout: what is bound

    /// <summary>
    /// What the current recording holds at each set of the shared pipeline layout (plan
    /// decision 9), and the push bytes it last received. Every program's pipelines are
    /// built against the one layout, so binding another program's pipeline disturbs none
    /// of it: set 1 is bound once per recording, set 0 and set 2 only when their set or
    /// dynamic offset changes, and push constants only when the bytes do. A new recording
    /// (another serial) or a raw bind outside this path (<see cref="ForgetBoundDescriptors" />)
    /// starts over.
    /// </summary>
    private ulong _boundSerial;
    private DescriptorSet _boundFrameSet;
    private uint _boundFrameOffset;
    private bool _boundTextureSet;
    private DescriptorSet _boundStorageSet;
    private uint _boundRecordOffset;
    private int _pushedLength;
    private readonly byte[] _pushShadow = new byte[SetConvention.PushConstantBytes];
    private readonly byte[] _pushedBytes = new byte[SetConvention.PushConstantBytes];

    /// <summary>
    /// Set 0's frame textures as the last draw that samples each resolved them, in
    /// <see cref="SetConvention.FrameTextures" /> order; an empty value is that binding's
    /// placeholder. Only a program that samples a frame texture reads the binding, and it
    /// resolves it again first, so a value left from another program is never read.
    /// A texture's deletion clears its values (any thread, hence the lock).
    /// </summary>
    private readonly SamplerBindingValue[] _frameTextureValues = new SamplerBindingValue[SetConvention.FrameTextures.Length];

    /// <summary>The client texture id each <see cref="_frameTextureValues" /> entry was resolved from.</summary>
    private readonly int[] _frameTextureIds = new int[SetConvention.FrameTextures.Length];
    private readonly object _frameTextureLock = new();

    /// <summary>Whether set 1's placeholders have been put in the layout their descriptors name.</summary>
    private bool _bindlessPlaceholdersReadable;

    /// <summary>Binds of set 0 and set 1 this device recorded. Tests only.</summary>
    internal long FrameSetBindsForTests { get; private set; }
    internal long TextureSetBindsForTests { get; private set; }

    /// <summary>Forgets what the recording holds bound, after a bind this path did not make.</summary>
    private void ForgetBoundDescriptors() => _boundSerial = 0;

    private void SyncBoundDescriptors(CommandBuffer commandBuffer)
    {
        FrameSlot slot = _frames.Current;
        ulong serial = slot.CommandBuffer.Handle == commandBuffer.Handle ? slot.RecordingSerial : 0;
        if (serial != 0 && serial == _boundSerial) return;

        _boundSerial = serial;
        _boundFrameSet = default;
        _boundFrameOffset = 0;
        _boundTextureSet = false;
        _boundStorageSet = default;
        _boundRecordOffset = 0;
        _pushedLength = 0;
    }

    private void ForgetFrameTexture(ulong textureId)
    {
        lock (_frameTextureLock)
        {
            for (int i = 0; i < _frameTextureValues.Length; i++)
            {
                if (_frameTextureValues[i].Resource != textureId) continue;
                _frameTextureValues[i] = default;
                _frameTextureIds[i] = 0;
            }
        }
    }

    private static int FrameTextureIndex(int binding)
    {
        for (int i = 0; i < SetConvention.FrameTextures.Length; i++)
        {
            if (SetConvention.FrameTextures[i].Value == binding) return i;
        }
        throw new ArgumentOutOfRangeException(nameof(binding), binding, "not a frame texture binding");
    }

    private static TextureKind KindOf(SamplerBinding sampler)
    {
        if (!sampler.IsFrameTexture) return sampler.Kind;
        BindlessKinds.TryFromGlslType(sampler.TypeName, out TextureKind kind);
        return kind;
    }

    /// <summary>Set 0's placeholder for a frame texture: the bindless table's placeholder of the declared kind.</summary>
    private SamplerBindingValue FrameTexturePlaceholder(int index)
    {
        SetConvention.Binding frame = SetConvention.FrameTextures[index];
        BindlessKinds.TryFromGlslType(frame.GlslType, out TextureKind kind);
        VulkanTexture placeholder = _textures.Get(_bindless!.PlaceholderTextureId(kind))!;
        return new SamplerBindingValue((uint)frame.Value, placeholder.View,
            _textures.Samplers.Get(BindlessKinds.EffectiveState(SamplerState.Default, kind)), placeholder.Id);
    }

    /// <summary>
    /// Takes a snapshot of the shared frame block when it changed and returns its ring
    /// offset. Every draw that follows in the frame reads the same snapshot and only the
    /// dynamic offset moves when a value does.
    /// </summary>
    private uint SnapshotFrameGlobals(ShaderProgramResources program)
    {
        if (_frameGlobalsSnapshotFrame == _frameCounter && _frameGlobalsSnapshotVersion == _frameGlobalsVersion)
        {
            return _frameGlobalsSnapshotOffset;
        }
        if (!_frames.Current.TryAllocateUniforms(_frameGlobals.Length, out RingAllocation allocation))
        {
            ReportUniformExhaustion(program, "the shared frame block");
            return 0;
        }
        fixed (byte* source = _frameGlobals)
        {
            System.Buffer.MemoryCopy(source, (void*)allocation.Pointer, _frameGlobals.Length, _frameGlobals.Length);
        }
        _frameGlobalsSnapshotFrame = _frameCounter;
        _frameGlobalsSnapshotVersion = _frameGlobalsVersion;
        _frameGlobalsSnapshotOffset = allocation.Offset;
        return allocation.Offset;
    }

    /// <summary>
    /// Binds the three sets of the shared layout for a draw whose push shadow already holds
    /// its sampler slots: the frame set, the texture set with the push block, and the storage
    /// set. Every native draw binds through here.
    /// </summary>
    private void BindProgramSets(CommandBuffer commandBuffer, ShaderProgramResources program, int meshId)
    {
        Vk api = _context.Api;
        SharedPipelineLayout shared = _sharedLayout!;
        SyncBoundDescriptors(commandBuffer);

        // Set 0: the frame block and the fixed frame textures.
        if (program.Interface.UsesFrameBlock || program.Interface.UsesFrameTextures)
        {
            uint offset = SnapshotFrameGlobals(program);
            var samplers = new SamplerBindingValue[SetConvention.FrameTextures.Length];
            lock (_frameTextureLock)
            {
                for (int i = 0; i < samplers.Length; i++)
                {
                    samplers[i] = _frameTextureValues[i].View.Handle != 0 ? _frameTextureValues[i] : FrameTexturePlaceholder(i);
                }
            }
            var contents = new DescriptorSetContents(0, SetConvention.FrameSet, samplers,
                new[]
                {
                    new BufferBindingValue((uint)SetConvention.FrameGlobalsBinding, _frames.UniformBuffer, 0,
                        (ulong)_frameGlobals.Length),
                });
            DescriptorSet frameSet = GetDescriptorSet(contents, shared.FrameSetLayout);
            if (frameSet.Handle != _boundFrameSet.Handle || offset != _boundFrameOffset)
            {
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
                    (uint)SetConvention.FrameSet, 1, &frameSet, 1, &offset);
                _boundFrameSet = frameSet;
                _boundFrameOffset = offset;
                FrameSetBindsForTests++;
            }
        }

        // Set 1 once per recording, and the slot indices when they changed.
        int pushSize = program.Interface.PushConstantSize;
        if (pushSize > 0)
        {
            if (!_boundTextureSet)
            {
                DescriptorSet textureSet = _bindless!.Set;
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
                    (uint)SetConvention.TextureSet, 1, &textureSet, 0, null);
                _boundTextureSet = true;
                TextureSetBindsForTests++;
            }
            if (pushSize > _pushedLength ||
                !_pushShadow.AsSpan(0, pushSize).SequenceEqual(_pushedBytes.AsSpan(0, pushSize)))
            {
                fixed (byte* push = _pushShadow)
                {
                    api.CmdPushConstants(commandBuffer, shared.Layout, SharedPipelineLayout.Stages, 0, (uint)pushSize, push);
                }
                _pushShadow.AsSpan(0, pushSize).CopyTo(_pushedBytes);
                _pushedLength = Math.Max(_pushedLength, pushSize);
                VulkanStats.NotePushConstantWrite();
            }
        }

        if (program.Interface.UsesStorageSet)
        {
            BindStorageSet(commandBuffer, program, meshId);
        }
    }

    /// <summary>
    /// Set 2, built per draw against the shared layout: the program record at its
    /// dynamic binding (a ring snapshot when the shadow changed), each named block from
    /// the client's UBO snapshot as a std140 storage buffer at the snapshot's offset,
    /// each storage block from the mesh's vertex buffer, and the zero-filled placeholder
    /// buffer at every binding the program does not read. A set naming a ring offset is
    /// new every frame, so it comes from the slot's arena.
    /// </summary>
    private void BindStorageSet(CommandBuffer commandBuffer, ShaderProgramResources program, int meshId)
    {
        SharedPipelineLayout shared = _sharedLayout!;
        VulkanBuffer placeholder = _placeholderUniforms!;
        var buffers = new BufferBindingValue[SetConvention.StorageSetBindingCount];
        for (int binding = 0; binding < buffers.Length; binding++)
        {
            buffers[binding] = new BufferBindingValue((uint)binding, placeholder.Handle, 0, placeholder.Size, placeholder.Id);
        }

        bool namesRingOffset = false;
        uint recordOffset = 0;
        const int record = SetConvention.ProgramRecordBinding;

        if (program.Interface.HasUniformBlock)
        {
            if (program.HasSnapshotFor(_frameCounter))
            {
                // Nothing written since this program's last draw this frame took its snapshot.
                recordOffset = program.SnapshotOffset;
            }
            else if (_frames.Current.TryAllocateUniforms(program.UniformShadow.Length, out RingAllocation allocation))
            {
                fixed (byte* source = program.UniformShadow)
                {
                    System.Buffer.MemoryCopy(source, (void*)allocation.Pointer,
                        program.UniformShadow.Length, program.UniformShadow.Length);
                }
                recordOffset = allocation.Offset;
                program.NoteSnapshot(_frameCounter, allocation.Offset);
            }
            else
            {
                // The draw reads offset zero of the ring, which is some other draw's record:
                // wrong, and for a shader that loops on a uniform count, possibly fatal.
                ReportUniformExhaustion(program, "its program record");
            }
            buffers[record] = new BufferBindingValue(record, _frames.UniformBuffer, 0, (ulong)program.UniformShadow.Length);
        }

        // A block the shader declares is fed by whichever UBO the client created under
        // that name; one it has not created yet reads the placeholder's zeroes.
        foreach (BlockBinding block in program.Interface.UniformBlocks)
        {
            ClientUniformBuffer? ubo = null;
            if (_boundUniformBuffers.TryGetValue(block.BlockName, out int handle)) _uniformBuffers.TryGetValue(handle, out ubo);
            if (ubo == null) continue;

            if (TrySnapshotClientBlock(ubo, program, out uint blockOffset))
            {
                buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, _frames.UniformBuffer,
                    blockOffset, (ulong)ubo.Shadow.Length);
                namesRingOffset = true;
                continue;
            }

            // No room left in the ring. Rather than aliasing a buffer every remaining draw
            // would share - the exact bug the ring exists to fix - this draw gets its own
            // transient copy. Counted, so a scene that lives in this path shows in the stats.
            VulkanStats.NoteUniformOverflow();
            var overflow = new VulkanBuffer(_context, (ulong)ubo.Shadow.Length,
                BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            fixed (byte* shadow = ubo.Shadow)
            {
                System.Buffer.MemoryCopy(shadow, (void*)overflow.Mapped, ubo.Shadow.Length, ubo.Shadow.Length);
            }
            buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, overflow.Handle, 0, overflow.Size, overflow.Id);
            namesRingOffset = true;
            // Released and deferred in that order: no cached set naming it may outlive it.
            _descriptors.Release(overflow.Id);
            _frames.DeferDeletion(overflow);
        }

        // The SSBO chunk path reads its vertices from the mesh's xyz buffer by gl_VertexIndex.
        foreach (BlockBinding block in program.Interface.StorageBlocks)
        {
            VulkanBuffer? buffer = meshId > 0 ? _meshes.BufferOf(meshId, MeshManager.BufferXyz) : null;
            if (buffer != null)
            {
                buffers[block.Binding] = new BufferBindingValue((uint)block.Binding, buffer.Handle, 0, buffer.Size, buffer.Id);
            }
            else if (RenderTrace.Enabled)
            {
                RenderTrace.Write("storage block '" + block.BlockName + "' on program " + program.ProgramId +
                    " has no mesh buffer (mesh " + meshId + "); it reads the placeholder");
            }
        }

        if (RenderTrace.Enabled && meshId > 0)
        {
            // Diagnostic: what this draw binds, so the emulated and the native route can be diffed per draw.
            var trace = new System.Text.StringBuilder("  sets program=").Append(program.ProgramId).Append(" mesh=").Append(meshId)
                .Append(" record=").Append(recordOffset);
            static ulong Fnv(ReadOnlySpan<byte> bytes)
            {
                ulong h = 14695981039346656037UL;
                foreach (byte b in bytes) h = (h ^ b) * 1099511628211UL;
                return h;
            }
            foreach (BlockBinding block in program.Interface.UniformBlocks)
            {
                trace.Append(' ').Append(block.BlockName).Append('@').Append(block.Binding).Append('=')
                    .Append(buffers[block.Binding].Offset).Append('/').Append(buffers[block.Binding].Resource);
                if (_boundUniformBuffers.TryGetValue(block.BlockName, out int traceHandle) &&
                    _uniformBuffers.TryGetValue(traceHandle, out ClientUniformBuffer? traceUbo))
                {
                    trace.Append(" h").Append(traceHandle).Append(":#").Append(Fnv(traceUbo.Shadow).ToString("x16"));
                }
                else
                {
                    trace.Append(" (unbound)");
                }
            }
            trace.Append(" rec#").Append(Fnv(program.UniformShadow).ToString("x16"))
                .Append(" push#").Append(Fnv(_pushShadow).ToString("x16"));
            trace.Append(" pushBytes=").Append(program.PushShadow?.Length ?? 0)
                .Append(" frameBlock=").Append(program.Interface.UsesFrameBlock)
                .Append(" frame@").Append(_frameGlobalsSnapshotOffset).Append(" v").Append(_frameGlobalsVersion)
                .Append('#').Append(Fnv(_frameGlobals).ToString("x16"));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            trace.Append(" recordBytes=").Append(program.UniformShadow.Length);
            foreach (UniformMember member in program.Interface.Members)
            {
                if (member.Name is not ("projectionMatrix" or "viewMatrix" or "modelMatrix")) continue;
                if (member.Offset < 0 || member.Offset + 64 > program.UniformShadow.Length) continue;
                var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(program.UniformShadow.AsSpan(member.Offset, 64));
                trace.Append(' ').Append(member.Name).Append('@').Append(member.Offset).Append("=[")
                    .Append(f[0].ToString("G4", inv)).Append(',').Append(f[5].ToString("G4", inv)).Append(";t=")
                    .Append(f[12].ToString("G4", inv)).Append(',').Append(f[13].ToString("G4", inv)).Append(',').Append(f[14].ToString("G4", inv)).Append(']');
            }
            RenderTrace.Write(trace.ToString());
        }

        var contents = new DescriptorSetContents(0, SetConvention.StorageSet, Array.Empty<SamplerBindingValue>(), buffers);
        DescriptorSet storageSet = namesRingOffset
            ? _descriptorArenas[_frames.Current.Index].Get(contents, shared.StorageSetLayout)
            : GetDescriptorSet(contents, shared.StorageSetLayout);
        if (storageSet.Handle == _boundStorageSet.Handle && recordOffset == _boundRecordOffset) return;

        _context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, shared.Layout,
            (uint)SetConvention.StorageSet, 1, &storageSet, 1, &recordOffset);
        _boundStorageSet = storageSet;
        _boundRecordOffset = recordOffset;
        VulkanStats.NoteStorageSetBind();
    }

    /// <summary>
    /// Records the dynamic state a draw needs and the recording does not already hold. The
    /// values come from the native pipeline's fixed state and the pass's viewport and scissor.
    /// </summary>
    private void EmitDynamicState(CommandBuffer commandBuffer, DynamicStateValues values, ulong serial,
        ColorWriteTier tier, bool dynamicBlend, int colorStates, ReadOnlySpan<AttachmentBlend> blendStates)
    {
        Vk api = _context.Api;
        uint colorWrite = values.ColorWrite;
        DynamicStateDirty dirty = _dynamicState.Update(serial, values);
        if (tier == ColorWriteTier.PipelineKey) dirty &= ~DynamicStateDirty.ColorWrite;
        if (!dynamicBlend) dirty &= ~DynamicStateDirty.ColorBlend;
        if (dirty == DynamicStateDirty.None) return;
        int extraCommands = 0;

        if ((dirty & DynamicStateDirty.ColorWrite) != 0)
        {
            if (tier == ColorWriteTier.DynamicEnable)
            {
                // All of maxColorAttachments, so the count covers every pipeline's attachments.
                Silk.NET.Core.Bool32* enables = stackalloc Silk.NET.Core.Bool32[colorStates];
                for (int i = 0; i < colorStates; i++) enables[i] = ((colorWrite >> i) & 1) != 0;
                _context.ColorWriteEnableApi!.CmdSetColorWriteEnable(commandBuffer, (uint)colorStates, enables);
            }
            else
            {
                ColorComponentFlags* masks = stackalloc ColorComponentFlags[colorStates];
                for (int i = 0; i < colorStates; i++) masks[i] = (ColorComponentFlags)((colorWrite >> (i * 4)) & 0xF);
                _context.DynamicState3Api!.CmdSetColorWriteMask(commandBuffer, 0, (uint)colorStates, masks);
            }
            extraCommands++;
        }

        if ((dirty & DynamicStateDirty.ColorBlend) != 0)
        {
            Silk.NET.Core.Bool32* blendEnables = stackalloc Silk.NET.Core.Bool32[colorStates];
            ColorBlendEquationEXT* equations = stackalloc ColorBlendEquationEXT[colorStates];
            for (int i = 0; i < colorStates; i++)
            {
                AttachmentBlend blend = i < blendStates.Length ? blendStates[i] : AttachmentBlend.Default;
                blendEnables[i] = blend.Enabled;
                equations[i] = new ColorBlendEquationEXT
                {
                    SrcColorBlendFactor = blend.SrcColor,
                    DstColorBlendFactor = blend.DstColor,
                    ColorBlendOp = blend.ColorOp,
                    SrcAlphaBlendFactor = blend.SrcAlpha,
                    DstAlphaBlendFactor = blend.DstAlpha,
                    AlphaBlendOp = blend.AlphaOp,
                };
            }
            _context.DynamicState3Api!.CmdSetColorBlendEnable(commandBuffer, 0, (uint)colorStates, blendEnables);
            _context.DynamicState3Api!.CmdSetColorBlendEquation(commandBuffer, 0, (uint)colorStates, equations);
            extraCommands += 2;
        }

        if ((dirty & DynamicStateDirty.Viewport) != 0) api.CmdSetViewport(commandBuffer, 0, 1, &values.Viewport);
        if ((dirty & DynamicStateDirty.Scissor) != 0) api.CmdSetScissor(commandBuffer, 0, 1, &values.Scissor);
        if ((dirty & DynamicStateDirty.CullMode) != 0) api.CmdSetCullMode(commandBuffer, values.CullMode);
        if ((dirty & DynamicStateDirty.FrontFace) != 0) api.CmdSetFrontFace(commandBuffer, values.FrontFace);
        if ((dirty & DynamicStateDirty.Topology) != 0) api.CmdSetPrimitiveTopology(commandBuffer, values.Topology);

        if ((dirty & DynamicStateDirty.DepthTestEnable) != 0) api.CmdSetDepthTestEnable(commandBuffer, values.DepthTest);
        if ((dirty & DynamicStateDirty.DepthWriteEnable) != 0) api.CmdSetDepthWriteEnable(commandBuffer, values.DepthWrite);
        if ((dirty & DynamicStateDirty.DepthCompareOp) != 0) api.CmdSetDepthCompareOp(commandBuffer, values.DepthCompare);

        if ((dirty & DynamicStateDirty.StencilTestEnable) != 0) api.CmdSetStencilTestEnable(commandBuffer, values.StencilTest);
        if ((dirty & DynamicStateDirty.StencilOp) != 0)
            api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
                values.StencilFail, values.StencilPass, values.StencilDepthFail, values.StencilCompare);
        if ((dirty & DynamicStateDirty.StencilCompareMask) != 0)
            api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilCompareMask);
        if ((dirty & DynamicStateDirty.StencilWriteMask) != 0)
            api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilWriteMask);
        if ((dirty & DynamicStateDirty.StencilReference) != 0)
            api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, values.StencilReference);

        if ((dirty & DynamicStateDirty.LineWidth) != 0) api.CmdSetLineWidth(commandBuffer, values.LineWidth);

        int emitted = DynamicStateCache.CommandCount(dirty & DynamicStateDirty.All) + extraCommands;
        _dynamicStateCommands += emitted;
        VulkanStats.NoteDynamicStateCommands(emitted);
    }

    /// <summary>
    /// Commands the first draw of a recording emits: the core set plus the colour
    /// write state of the tier (one command; two more with dynamic blend). Tests only.
    /// </summary>
    internal int DynamicStateCommandsPerDrawForTests =>
        VulkanStats.DynamicStateCommandsPerDraw +
        (_context.Capabilities.ColorWriteTier == ColorWriteTier.PipelineKey ? 0 : 1) +
        (_context.Capabilities.DynamicColorBlend ? 2 : 0);

    /// <summary>The colour write tier this device's draws use. Tests only.</summary>
    internal ColorWriteTier ColorWriteTierForTests => _context.Capabilities.ColorWriteTier;

    /// <summary>vkCmdBeginRendering calls of this device. Tests only.</summary>
    internal long ScopesOpenedForTests => _targets.ScopesOpened;

    /// <summary>A texture's current layout. Tests only.</summary>
    internal ImageLayout TextureLayoutForTests(int textureId) =>
        _textures.Get(textureId)?.Layout ?? ImageLayout.Undefined;

    /// <summary>Restarts that reopened an identical attachment set; must stay 0. Tests only.</summary>
    internal long MaskRestartsForTests => _targets.MaskRestarts;

    /// <summary>Restarts for a sampled, draw-buffer-excluded slot. Tests only.</summary>
    internal long FeedbackSplitsForTests => _targets.FeedbackSplits;

    /// <summary>Dynamic-state commands this device recorded. Tests only.</summary>
    internal long DynamicStateCommandsForTests => _dynamicStateCommands;

    /// <summary>False emits every dynamic-state command on every draw, as before masking. Tests only.</summary>
    internal bool DynamicStateMaskingForTests
    {
        get => _dynamicState.Enabled;
        set => _dynamicState.Enabled = value;
    }

    /// <summary>
    /// Routes a set to the current slot's arena when it names a resource created
    /// in the last <see cref="ResourceAge.ShortLivedFrames" /> frames (GUI text,
    /// atlas tasks, fresh meshes, overflow uniform copies), otherwise to the
    /// long-lived cache.
    /// </summary>
    private DescriptorSet GetDescriptorSet(DescriptorSetContents contents, DescriptorSetLayout layout) =>
        _resourceAge.NamesShortLived(contents)
            ? _descriptorArenas[_frames.Current.Index].Get(contents, layout)
            : _descriptors.Get(contents, layout);

    /// <summary>The frames a resource's sets stay in the arena; 0 sends every set to the cache. Tests only.</summary>
    internal int ShortLivedFramesForTests
    {
        get => _resourceAge.ShortLivedFrames;
        set => _resourceAge.ShortLivedFrames = value;
    }

    /// <summary>A slot's descriptor arena. Tests only.</summary>
    internal DescriptorArena DescriptorArenaForTests(int slot) => _descriptorArenas[slot];

    /// <summary>The slot the current (or last) frame records into. Tests only.</summary>
    internal int CurrentSlotForTests => _frames.Current.Index;

    /// <summary>The indirect ring's bookkeeping. Tests only.</summary>
    internal IndirectRing IndirectRingForTests => _indirectRing;

    /// <summary>Multi-draws that took an overflow buffer, and slot buffers grown at a frame boundary. Tests only.</summary>
    internal long IndirectOverflowsForTests => _indirectOverflows;
    internal long IndirectGrowthsForTests => _indirectGrowths;

    /// <summary>Replaces the ring with one whose slot buffers start at <paramref name="value" /> bytes. Before the first multi-draw only. Tests only.</summary>
    internal ulong IndirectMinimumCapacityForTests
    {
        set => _indirectRing = new IndirectRing(_frames.FramesInFlight, value);
    }

    /// <summary>
    /// The frame boundary of the indirect ring: this slot's cursor returns to 0,
    /// and its buffer grows here, and only here, when the busiest frame so far did
    /// not fit. Overflow buffers of the frames before retire on the timelines.
    /// </summary>
    private void BeginIndirectFrame(int slot)
    {
        foreach (VulkanBuffer overflow in _indirectOverflow) _frames.DeferDeletion(overflow);
        _indirectOverflow.Clear();
        _indirectOverflowCursor = 0;

        if (_indirectRing.BeginFrame(slot, out ulong capacity))
        {
            // Draws of the slot's previous frame named the old buffer; it retires
            // on the timelines like any other resource.
            _frames.DeferDeletion(_indirectBuffers[slot]!);
            _indirectBuffers[slot] = CreateIndirectBuffer(capacity);
            _indirectRing.Attach(capacity);
            _indirectGrowths++;
        }
    }

    /// <summary>Per-frame dynamic data, so the ReBAR class (a miss falls through, counted).</summary>
    private VulkanBuffer CreateIndirectBuffer(ulong size) =>
        new(_context, size,
            BufferUsageFlags.IndirectBufferBit,
            MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPoolClass.ReBar);

    /// <summary>
    /// Hands out a region of the current slot's indirect-command buffer for one
    /// multi-draw.
    ///
    /// The commands are written on the CPU when the draw is recorded and read by
    /// the GPU when it executes, which is later - after every other draw of the
    /// frame has been recorded too. So each draw needs its own region: writing
    /// them all at offset zero meant every multi-draw in a frame executed with
    /// the ranges of whichever was recorded last, and the chunk pass is hundreds
    /// of them.
    ///
    /// Regions are bump-allocated per slot and never wrap (Phase 1B step 6): the
    /// cursor resets only at the slot's next frame start. A frame that outgrows
    /// its slot's buffer continues in an overflow buffer, counted, and the slot
    /// grows at its next frame boundary.
    /// </summary>
    private VulkanBuffer AllocateIndirect(int groupCount, out ulong offset)
    {
        ulong needed = (ulong)Math.Max(groupCount, 1) * (ulong)sizeof(DrawIndexedIndirectCommand);
        int slot = _indirectRing.Current;

        if (_indirectRing.NeedsBuffer(needed, out ulong capacity))
        {
            // Nothing recorded names a buffer the slot never had, so creating one is safe mid-frame.
            _indirectBuffers[slot] = CreateIndirectBuffer(capacity);
            _indirectRing.Attach(capacity);
        }

        if (_indirectRing.TryAllocate(needed, out offset)) return _indirectBuffers[slot]!;

        _indirectOverflows++;
        VulkanStats.NoteIndirectOverflow();
        VulkanBuffer? current = _indirectOverflow.Count == 0 ? null : _indirectOverflow[^1];
        if (current == null || _indirectOverflowCursor + needed > current.Size)
        {
            current = CreateIndirectBuffer(_indirectRing.CapacityFor(Math.Max(needed, _indirectRing.CapacityOf(slot))));
            _indirectOverflow.Add(current);
            _indirectOverflowCursor = 0;
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("indirect overflow: slot " + slot + " capacity " + _indirectRing.CapacityOf(slot) +
                    " frame usage " + _indirectRing.FrameUsageOf(slot) + "; overflow buffer " + current.Size);
            }
        }

        offset = _indirectOverflowCursor;
        _indirectOverflowCursor += needed;
        return current;
    }
}
