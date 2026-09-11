using System;
using System.Numerics;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>One bit per dynamic-state command a draw may record.</summary>
[Flags]
internal enum DynamicStateDirty : ushort
{
    None = 0,
    Viewport = 1 << 0,
    Scissor = 1 << 1,
    CullMode = 1 << 2,
    FrontFace = 1 << 3,
    Topology = 1 << 4,
    DepthTestEnable = 1 << 5,
    DepthWriteEnable = 1 << 6,
    DepthCompareOp = 1 << 7,
    StencilTestEnable = 1 << 8,
    StencilOp = 1 << 9,
    StencilCompareMask = 1 << 10,
    StencilWriteMask = 1 << 11,
    StencilReference = 1 << 12,
    LineWidth = 1 << 13,
    /// <summary>The Vulkan 1.3 core set every pipeline declares dynamic.</summary>
    All = (1 << 14) - 1,
    /// <summary>vkCmdSetColorWriteEnableEXT or vkCmdSetColorWriteMaskEXT, per the colour write tier.</summary>
    ColorWrite = 1 << 14,
    /// <summary>vkCmdSetColorBlendEnableEXT + vkCmdSetColorBlendEquationEXT (mask tier with dynamic blend).</summary>
    ColorBlend = 1 << 15,
    /// <summary>What a fresh recording marks dirty; the device drops the bits its tier does not use.</summary>
    Everything = All | ColorWrite | ColorBlend,
}

/// <summary>The values a draw's dynamic state resolves to, already in Vulkan terms.</summary>
internal struct DynamicStateValues
{
    public Viewport Viewport;
    public Rect2D Scissor;
    public CullModeFlags CullMode;
    public FrontFace FrontFace;
    public PrimitiveTopology Topology;
    public bool DepthTest;
    public bool DepthWrite;
    public CompareOp DepthCompare;
    public bool StencilTest;
    public StencilOp StencilFail;
    public StencilOp StencilPass;
    public StencilOp StencilDepthFail;
    public CompareOp StencilCompare;
    public uint StencilCompareMask;
    public uint StencilWriteMask;
    public uint StencilReference;
    public float LineWidth;
    /// <summary>
    /// The colour write state the tier makes dynamic: enable bits (enable tier) or
    /// the effective masks packed four bits per attachment (mask tier); 0 otherwise.
    /// </summary>
    public uint ColorWrite;
    /// <summary>Interned id of the full per-attachment blend set (mask tier with dynamic blend); 0 otherwise.</summary>
    public int BlendStateId;
}

/// <summary>
/// What the command buffer being recorded already holds, so a draw emits only
/// the dynamic state that changed.
///
/// Dynamic state is command-buffer state: it survives rendering scopes and
/// pipeline binds (every pipeline declares all of these dynamic), and is
/// undefined again when a command buffer begins. The cache is keyed on the
/// recording serial of the command buffer (<see cref="FrameSlot.RecordingSerial" />),
/// so a new frame, a partial submission's continuation or a recycled handle
/// always starts from "everything dirty".
/// </summary>
internal sealed class DynamicStateCache
{
    private ulong _serial;
    private DynamicStateValues _last;

    /// <summary>False emits everything on every draw, as before masking. Tests compare the two.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Forgets what was recorded; the next draw emits everything.</summary>
    public void Invalidate() => _serial = 0;

    /// <summary>
    /// Returns the commands a draw recorded into the command buffer with
    /// <paramref name="serial" /> has to emit for <paramref name="next" />, and
    /// assumes the caller emits them. A serial of 0 is never trusted.
    /// </summary>
    public DynamicStateDirty Update(ulong serial, in DynamicStateValues next)
    {
        DynamicStateDirty dirty;
        if (!Enabled || serial == 0 || serial != _serial)
        {
            dirty = DynamicStateDirty.Everything;
        }
        else
        {
            dirty = DynamicStateDirty.None;
            if (!SameViewport(_last.Viewport, next.Viewport)) dirty |= DynamicStateDirty.Viewport;
            if (!SameRect(_last.Scissor, next.Scissor)) dirty |= DynamicStateDirty.Scissor;
            if (_last.CullMode != next.CullMode) dirty |= DynamicStateDirty.CullMode;
            if (_last.FrontFace != next.FrontFace) dirty |= DynamicStateDirty.FrontFace;
            if (_last.Topology != next.Topology) dirty |= DynamicStateDirty.Topology;
            if (_last.DepthTest != next.DepthTest) dirty |= DynamicStateDirty.DepthTestEnable;
            if (_last.DepthWrite != next.DepthWrite) dirty |= DynamicStateDirty.DepthWriteEnable;
            if (_last.DepthCompare != next.DepthCompare) dirty |= DynamicStateDirty.DepthCompareOp;
            if (_last.StencilTest != next.StencilTest) dirty |= DynamicStateDirty.StencilTestEnable;
            if (_last.StencilFail != next.StencilFail || _last.StencilPass != next.StencilPass ||
                _last.StencilDepthFail != next.StencilDepthFail || _last.StencilCompare != next.StencilCompare)
            {
                dirty |= DynamicStateDirty.StencilOp;
            }
            if (_last.StencilCompareMask != next.StencilCompareMask) dirty |= DynamicStateDirty.StencilCompareMask;
            if (_last.StencilWriteMask != next.StencilWriteMask) dirty |= DynamicStateDirty.StencilWriteMask;
            if (_last.StencilReference != next.StencilReference) dirty |= DynamicStateDirty.StencilReference;
            // Bitwise, so a NaN width is not "changed" forever.
            if (BitConverter.SingleToInt32Bits(_last.LineWidth) != BitConverter.SingleToInt32Bits(next.LineWidth))
            {
                dirty |= DynamicStateDirty.LineWidth;
            }
            if (_last.ColorWrite != next.ColorWrite) dirty |= DynamicStateDirty.ColorWrite;
            if (_last.BlendStateId != next.BlendStateId) dirty |= DynamicStateDirty.ColorBlend;
        }

        _serial = serial;
        _last = next;
        return dirty;
    }

    public static int CommandCount(DynamicStateDirty dirty) => BitOperations.PopCount((uint)dirty);

    private static bool SameViewport(in Viewport a, in Viewport b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height &&
        a.MinDepth == b.MinDepth && a.MaxDepth == b.MaxDepth;

    private static bool SameRect(in Rect2D a, in Rect2D b) =>
        a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y &&
        a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;
}
