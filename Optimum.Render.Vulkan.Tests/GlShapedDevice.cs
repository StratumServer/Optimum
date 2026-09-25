using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Platform;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The OpenGL-shaped calls the GPU tests are written in, on a bare <see cref="VulkanDevice" />:
/// set state, bind, clear, draw, read back. The device has no GL state machine any more; these
/// record what a test states into a <see cref="StatedRenderState" /> per device, exactly as
/// <see cref="VulkanClientPlatform" /> records the client's statements, and every draw goes through
/// <see cref="StatedDraw" />, the platform's generic native draw. A test therefore renders through
/// the route the client's own unrecognised draws take.
///
/// A device a <see cref="VulkanClientPlatform" /> owns shares the platform's record, program and
/// target (VulkanDevice.OwnerPlatform): a fixture that states on the device and then calls one of
/// the platform's bodies draws with that state, and the device's draws are the platform's.
///
/// Semantics kept from the removed emulation: a bind names the target the next clears, draws and
/// readbacks address (nothing is drawn before one); a declared pass (<c>DeclarePass</c>) gives the
/// following draws its name, slots, reads and flags until the next declaration, bind of another
/// target or <c>EndPass</c>; a clear honours the stated draw buffers and colour mask, and a depth
/// clear the depth mask.
/// </summary>
internal static class GlShapedDevice
{
    private sealed class Record
    {
        public StatedRenderState Stated = new();
        public VulkanClientPlatform? Platform;
        private int program;

        /// <summary>glUseProgram: the platform's record when a platform owns the device.</summary>
        public int Program
        {
            get => Platform?.statedProgram ?? program;
            set
            {
                program = value;
                if (Platform != null) Platform.statedProgram = value;
            }
        }

        public int Bound;
        public PassDeclaration? Declared;
        public string? LastRefusal;
        public long Refusals;

    }

    private static readonly ConditionalWeakTable<VulkanDevice, Record> Records = new();
    private static Record Of(VulkanDevice device)
    {
        if (!Records.TryGetValue(device, out Record? record))
        {
            record = new Record();
            Records.Add(device, record);
            Record captured = record;
            device.FramebufferDeleted += id =>
            {
                captured.Stated.ForgetFramebuffer(id);
                if (captured.Bound == id) captured.Bound = 0;
            };
        }
        if (record.Platform == null)
        {
            VulkanClientPlatform? owner = PlatformOf(device);
            if (owner != null)
            {
                record.Stated = owner.stated;
                record.Platform = owner;
            }
        }
        return record;
    }

    /// <summary>The platform that owns the device, if any.</summary>
    private static VulkanClientPlatform? PlatformOf(VulkanDevice device) => device.OwnerPlatform;

    /// <summary>The default target's id as the stated record keys it.</summary>
    private static int Key(VulkanDevice device, int framebufferId) =>
        framebufferId != 0 && framebufferId == device.DefaultFramebufferId ? PassDeclaration.DefaultFramebuffer : framebufferId;

    extension(VulkanDevice device)
    {
        /// <summary>The state the tests stated on this device.</summary>
        internal StatedRenderState StatedForTests => Of(device).Stated;

        /// <summary>Draws the stated route refused, and the last reason.</summary>
        internal long StatedRefusalsForTests => Of(device).Refusals;

        internal string? LastStatedRefusalForTests => Of(device).LastRefusal;

        // ------------------------------------------------------------ fixed function

        public void UseProgram(int programId) => Of(device).Program = programId;

        public void SetViewport(int x, int y, int width, int height) =>
            Of(device).Stated.Viewport = new Rect2D(new Offset2D(x, y),
                new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));

        /// <summary>glScissor, clipped to the positive quadrant as the platform clips it.</summary>
        public void SetScissor(int x, int y, int width, int height)
        {
            int clippedX = Math.Max(0, x);
            int clippedY = Math.Max(0, y);
            width -= clippedX - x;
            height -= clippedY - y;
            Of(device).Stated.Scissor = new Rect2D(new Offset2D(clippedX, clippedY),
                new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
        }

        public void SetScissorEnabled(bool enabled) => Of(device).Stated.ScissorEnabled = enabled;

        public bool ScissorEnabled => Of(device).Stated.ScissorEnabled;

        public void SetDepthTest(bool enabled) => Of(device).Stated.DepthTest = enabled;

        public void SetDepthMask(bool enabled) => Of(device).Stated.DepthWrite = enabled;

        public void SetDepthFunc(int glFunc) => Of(device).Stated.DepthCompare = GlEnums.CompareOpFrom(glFunc);

        public void SetCullFace(bool enabled) => Of(device).Stated.CullEnabled = enabled;

        public void SetCullFaceMode(bool back) => Of(device).Stated.CullBack = back;

        public void SetBlend(bool enabled, EnumBlendMode mode)
        {
            StatedRenderState stated = Of(device).Stated;
            stated.SetBlendEnabled(enabled);
            stated.SetBlendMode(mode);
        }

        public void SetBlendEnabled(bool enabled) => Of(device).Stated.SetBlendEnabled(enabled);

        public void SetBlendFuncSeparate(int attachment, int srcColor, int dstColor, int srcAlpha, int dstAlpha) =>
            Of(device).Stated.SetSlotFunc(attachment, srcColor, dstColor, srcAlpha, dstAlpha);

        public void SetBlendEquation(int attachment, int equation) =>
            Of(device).Stated.SetSlotEquation(attachment, equation);

        public void SetColorMask(bool r, bool g, bool b, bool a) => Of(device).Stated.SetColorMask(r, g, b, a);

        public void SetStencilTest(bool enabled) => Of(device).Stated.StencilTest = enabled;

        public void SetWireframe(bool enabled) => Of(device).Stated.Wireframe = enabled;

        public void SetLineWidth(float width) => Of(device).Stated.LineWidth = width;

        // ------------------------------------------------------------ textures

        public void BindTexture(int unit, int textureId) => Of(device).Stated.BindTexture(unit, textureId);

        public void BindTextureCube(int unit, int textureId) => Of(device).Stated.BindTexture(unit, textureId);

        public void BindSampler(int unit, int samplerId) => Of(device).Stated.BindSampler(unit, samplerId);

        // ------------------------------------------------------------ targets

        public void SetDrawBuffers(int framebufferId, int attachmentMask) =>
            Of(device).Stated.SetDrawBuffers(Key(device, framebufferId), (uint)attachmentMask);

        public void BindFramebuffer(int framebufferId)
        {
            Record record = Of(device);
            int key = Key(device, framebufferId);
            if (record.Declared != null && record.Declared.FramebufferId != key) SetDeclared(record, null);
            record.Bound = key;
            if (record.Platform == null) return;
            // A raw bind is what a fork renderer does on the platform: its draws address it too.
            if (key > 0) record.Platform.NoteForkFramebuffer(key);
            else record.Platform.CurrentFrameBuffer = null!;
        }

        public void BindDefaultFramebuffer() => device.BindFramebuffer(PassDeclaration.DefaultFramebuffer);

        /// <summary>The following draws belong to this pass (0: the bound target, -1: the default one).</summary>
        internal void DeclarePass(PassDeclaration declaration)
        {
            Record record = Of(device);
            int key = declaration.FramebufferId == PassDeclaration.BoundFramebuffer
                ? Target(record)
                : Key(device, declaration.FramebufferId);
            device.BindFramebuffer(key);
            SetDeclared(record, new PassDeclaration
            {
                Name = declaration.Name,
                FramebufferId = key,
                ColorSlots = declaration.ColorSlots,
                Reads = declaration.Reads,
                TransientSlots = declaration.TransientSlots,
                Flags = declaration.Flags,
            });
        }

        /// <summary>Ends the declared pass and closes its scope.</summary>
        internal void EndPass()
        {
            SetDeclared(Of(device), null);
            device.EndNativePass();
            device.EndStagePass();
        }

        public void ClearColor(int attachment, float r, float g, float b, float a)
        {
            Record record = Of(device);
            int target = Target(record);
            if (target == 0) return;
            if (((record.Stated.DrawBuffers(target) >> attachment) & 1) == 0 || record.Stated.ColorMask == 0) return;
            device.ClearNativeColor(target, attachment, r, g, b, a);
        }

        public void ClearDepth(float depth)
        {
            Record record = Of(device);
            int target = Target(record);
            if (target == 0 || !record.Stated.DepthWrite) return;
            device.ClearNativeDepth(target, depth);
        }

        public void ClearStencil() { }

        /// <summary>Colour attachment 0 of the bound target, in its own channel order.</summary>
        public void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)
        {
            int target = Target(Of(device));
            if (target == 0) return;
            device.ReadFramebufferColor(target, x, y, width, height, destination);
        }

        // ------------------------------------------------------------ draws

        public void DrawMesh(int meshId) => Draw(device, meshId, 1, null, null, 0);

        public void DrawMeshInstanced(int meshId, int instanceCount)
        {
            if (instanceCount > 0) Draw(device, meshId, instanceCount, null, null, 0);
        }

        public void DrawMeshMulti(int meshId, int[] indicesStarts, int[] indicesSizes, int groupCount, bool ssbo) =>
            Draw(device, meshId, 1, indicesStarts, indicesSizes, groupCount);

        public void DrawFullscreenTriangle() => Draw(device, 0, 1, null, null, 0);
    }

    /// <summary>The target a clear, draw or readback addresses: the platform's current one when a platform owns the device.</summary>
    private static int Target(Record record) => record.Platform?.CurrentTargetId ?? record.Bound;

    /// <summary>The declared pass, held where the draws read it.</summary>
    private static void SetDeclared(Record record, PassDeclaration? declared)
    {
        record.Declared = declared;
        if (record.Platform != null) record.Platform.statedPass = declared;
    }

    private static void Draw(VulkanDevice device, int meshId, int instances, int[]? starts, int[]? sizes, int groupCount)
    {
        Record record = Of(device);
        if (record.Platform != null)
        {
            record.Platform.RecordStatedDraw(meshId, instances, starts, sizes, groupCount);
            return;
        }
        if (record.Bound == 0 || record.Program <= 0) return;
        if (!StatedDraw.Record(device, record.Stated, record.Program, record.Bound, meshId, instances,
                starts, sizes, groupCount, out string? refusal, record.Declared) && refusal != null)
        {
            record.Refusals++;
            record.LastRefusal = refusal;
        }
    }
}
