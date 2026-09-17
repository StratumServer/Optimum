using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// The generic native draw: any program, drawn with the state the client stated through this
// platform's virtuals (StatedRenderState) - the last route of every draw virtual. The dedicated
// routes (chunks, entities, sky, particles, GUI, clouds, the post chain) keep their own contracts
// and run first; this one takes everything they do not recognise: mod renderers with their own
// programs, the vanilla programs without a dedicated route (aurora, block highlights, held item,
// lines, wireframe, the debug views) and the seams' neutral bodies behind the route switches.
//
// What it states, and from where: StatedDraw (the target the client addressed, its attached colour
// slots, the stated fixed-function state and texture units). All of it is client statements, none
// read back from the device.
// The clears (ClearTargetColor, ClearTargetDepth) are stated the same way: an explicit target, and
// OpenGL's rules - a clear writes only a selected draw buffer, not through an all-false colour
// mask, and a depth clear honours the depth mask. No state reaches the device any more; a draw
// this route cannot record is dropped and reported once.
// Pinned by Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>The fixed-function state the client stated, with OpenGL's semantics.</summary>
    internal readonly StatedRenderState stated = new();

    private readonly HashSet<int> statedRefusalReported = new();

    /// <summary>The program the client last used (glUseProgram); 0: none.</summary>
    internal int statedProgram;

    /// <summary>The pass a running mod pass declared: the generic draws inside it are recorded under it.</summary>
    internal PassDeclaration? statedPass;

    /// <summary>Draws the generic route recorded, and draws it could not record. Tests read them.</summary>
    internal long StatedDrawsForTests { get; private set; }

    internal long StatedRefusalsForTests { get; private set; }

    // ------------------------------------------------------------------ recording helpers

    /// <summary>The draw buffers the client selects for a framebuffer (0 is the default target).</summary>
    internal void StateDrawBuffers(int framebufferId, int mask)
    {
        stated.SetDrawBuffers(framebufferId == 0 ? PassDeclaration.DefaultFramebuffer : framebufferId, (uint)mask);
    }

    /// <summary><c>glBlendEquationi</c> + <c>glBlendFuncSeparatei</c>.</summary>
    internal void StateSlotBlend(int slot, int equation, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        stated.SetSlotBlend(slot, equation, srcColor, dstColor, srcAlpha, dstAlpha);
    }

    /// <summary><c>glBlendFuncSeparatei</c> alone: the attachment's equation stays.</summary>
    internal void StateSlotBlendFunc(int slot, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        stated.SetSlotFunc(slot, srcColor, dstColor, srcAlpha, dstAlpha);
    }

    /// <summary>Blend on with a mode's functions on every attachment, or off with the functions kept.</summary>
    internal void StateBlend(bool on, EnumBlendMode mode)
    {
        stated.SetBlendEnabled(on);
        if (on) stated.SetBlendMode(mode);
    }

    /// <summary>A viewport the client states (the fork bridge, and the platform's own full-target binds).</summary>
    internal void NoteForkViewport(int x, int y, int width, int height) =>
        stated.Viewport = new Rect2D(new Offset2D(x, y), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));

    internal void NoteForkTexture(int unit, int textureId) => stated.BindTexture(unit, textureId);

    internal void NoteForkDrawBuffers(int framebufferId, int mask) =>
        stated.SetDrawBuffers(framebufferId == 0 ? PassDeclaration.DefaultFramebuffer : framebufferId, (uint)mask);

    /// <summary>The viewport the client last stated: what every native pass that keeps the viewport draws with.</summary>
    private Rect2D StatedViewport() => stated.Viewport;

    /// <summary>The target the client's next draw or clear addresses: a fork's bound id, else CurrentFrameBuffer, else the default.</summary>
    internal int CurrentTargetId => forkFramebuffer > 0
        ? forkFramebuffer
        : CurrentFrameBuffer != null ? CurrentFrameBuffer.FboId : PassDeclaration.DefaultFramebuffer;

    /// <summary>A colour clear as OpenGL does it: only a selected draw buffer, never through an all-false colour mask.</summary>
    internal void ClearTargetColor(int framebufferId, int slot, float r, float g, float b, float a)
    {
        if (framebufferId == 0) framebufferId = PassDeclaration.DefaultFramebuffer;
        if (((stated.DrawBuffers(framebufferId) >> slot) & 1) == 0 || stated.ColorMask == 0) return;
        device.ClearNativeColor(framebufferId, slot, r, g, b, a);
    }

    /// <summary>A depth clear as OpenGL does it: not with depth writes off.</summary>
    internal void ClearTargetDepth(int framebufferId, float depth)
    {
        if (framebufferId == 0) framebufferId = PassDeclaration.DefaultFramebuffer;
        if (!stated.DepthWrite) return;
        device.ClearNativeDepth(framebufferId, depth);
    }

    // ------------------------------------------------------------------------- the route

    /// <summary>
    /// Records one draw of the current program natively from the stated state. A null
    /// <paramref name="vao" /> is the fullscreen triangle; <paramref name="starts" /> is a
    /// pool's multi-draw. False: nothing was recorded (reported once per program).
    /// </summary>
    private bool TryDrawStated(VAO? vao, int instances, int[]? starts, int[]? sizes, int groupCount)
    {
        if (vao != null && (vao.VaoId == 0 || vao.Disposed)) return false;
        return RecordStatedDraw(vao?.VaoId ?? 0, instances, starts, sizes, groupCount);
    }

    /// <summary>The generic draw of a device mesh (0: the fullscreen triangle) into the current target.</summary>
    internal bool RecordStatedDraw(int meshId, int instances, int[]? starts, int[]? sizes, int groupCount)
    {
        if (device == null || statedProgram <= 0) return false;
        int programId = statedProgram;

        int framebufferId = CurrentTargetId;
        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        PassDeclaration? declared = statedPass != null && statedPass.FramebufferId == framebufferId ? statedPass : null;
        bool drawn = StatedDraw.Record(device, stated, programId, framebufferId,
            meshId, instances, starts, sizes, groupCount, out string? refusal, declared);
        SetPassContext(outer, outerFlags);
        if (drawn)
        {
            StatedDrawsForTests++;
            return true;
        }
        RuntimeStats.drawCallsCount--;
        return refusal == null ? false : Refuse(programId, refusal);
    }

    private bool Refuse(int programId, string reason)
    {
        StatedRefusalsForTests++;
        if (statedRefusalReported.Add(programId))
        {
            ShaderProgramBase? current = ShaderProgramBase.CurrentShaderProgram;
            string name = current != null && current.ProgramId == programId ? current.PassName ?? "" : "#" + programId;
            Logger.Warning("Optimum: a draw of program '{0}' was dropped: {1}", name, reason);
        }
        return false;
    }

}
