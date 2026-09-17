using System;
using System.Collections.Generic;
using System.Globalization;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 5: the mod pass API hosted on the frame graph. A mod registers an
// OptimumPassDecl (slot, reads and writes by well-known handle, draw callback, optional motion
// writer) through OptimumModPasses; at the end of each render stage's bracket, after the stage's
// RegisterRenderer renderers, the platform runs the passes declared for that slot: it binds the
// declared target, declares the pass with the declared colour slots and reads (mod-hosted, so
// OpenSampling and AllowSplit), opens the motion window when the pass is a motion writer, calls
// the draw, ends the pass and restores the target and pass context. A RegisterRenderer renderer
// opens a registered writer's window itself through OptimumModPasses.BeginMotionWriter, which
// reaches the hooks installed here. OpenGL (ClientPlatformWindows) reads none of it.
public partial class VulkanClientPlatform
{
    /// <summary>The flags of every mod-hosted pass (plan, section D and Phase 2 step 2).</summary>
    internal const PassFlags ModPassFlags = PassFlags.OpenSampling | PassFlags.AllowSplit;

    /// <summary>Mod passes that ran their draw.</summary>
    internal long ModPassesRun { get; private set; }

    /// <summary>Mod passes skipped because a written attachment does not exist this session.</summary>
    internal long ModPassesSkipped { get; private set; }

    /// <summary>The pass whose draw is running, null outside one ("Mod/&lt;mod&gt;/&lt;name&gt;/&lt;target&gt;").</summary>
    internal string? CurrentModPass { get; private set; }

    private readonly Dictionary<OptimumPassDecl, ModPassPlan> modPassPlans = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<OptimumPassDecl> modPassFailuresLogged = new(ReferenceEqualityComparer.Instance);
    private List<FrameBufferRef>? modPassPlansFor;
    private int modPassPlansMotionIndex = int.MinValue;

    /// <summary>A registration resolved against the current framebuffer set; rebuilt when the set changes.</summary>
    private sealed class ModPassPlan
    {
        public FrameBufferRef? Target;
        public bool Runnable;
        public string SkipReason = "";
        public PassDeclaration Declaration = new();
    }

    private void InstallModPassHooks()
    {
        OptimumModPasses.MotionBeginHook = BeginModMotionWriter;
        OptimumModPasses.MotionEndHook = EndModMotionWriter;
    }

    private void RemoveModPassHooks()
    {
        if (OptimumModPasses.MotionBeginHook?.Target == this) OptimumModPasses.MotionBeginHook = null;
        if (OptimumModPasses.MotionEndHook?.Target == this) OptimumModPasses.MotionEndHook = null;
        modPassPlans.Clear();
        modPassPlansFor = null;
    }

    /// <summary>A renderer's registered writer: the window opens only inside Opaque or AfterOIT.</summary>
    private bool BeginModMotionWriter(OptimumMotionWriterDecl writer)
    {
        if (device == null || writer == null || !InRenderStage) return false;
        if (!OptimumPassContract.IsMotionWindowSlot((EnumOptimumPass)(int)CurrentRenderStage)) return false;
        return writer.Mode == EnumOptimumMotionWrite.MotionOnly ? BeginMotionOnlyWrite() : BeginMotionWrite();
    }

    private void EndModMotionWriter()
    {
        if (device == null) return;
        EndMotionWrite();
    }

    /// <summary>Runs the passes declared for <paramref name="stage" />, in registration order.</summary>
    internal void RunModPasses(EnumRenderStage stage)
    {
        if (device == null) return;
        OptimumPassRegistration[] passes = OptimumModPasses.ForSlot((EnumOptimumPass)(int)stage);
        if (passes.Length == 0) return;

        FrameBufferRef saved = CurrentFrameBuffer;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        foreach (OptimumPassRegistration registration in passes)
        {
            RunModPass(registration);
        }
        passContext = outer;
        passContextFlags = outerFlags;
        CurrentFrameBuffer = saved;
    }

    private void RunModPass(OptimumPassRegistration registration)
    {
        OptimumPassDecl decl = registration.Decl;
        ModPassPlan plan = PlanFor(registration);
        if (!plan.Runnable)
        {
            ModPassesSkipped++;
            if (modPassFailuresLogged.Add(decl))
                Logger?.Warning("[Optimum] mod pass '{0}' of {1} skipped: {2}", decl.Name, registration.ModId, plan.SkipReason);
            return;
        }

        passContext = "Mod/" + registration.ModId + "/" + decl.Name;
        passContextFlags = ModPassFlags;
        // The declared slots are the draw buffers the mod's draws write, for this pass only; every
        // draw inside it is recorded under the declaration (name, slots, reads, flags) on the
        // plan's target by the generic stated route.
        int targetId = plan.Target?.FboId ?? PassDeclaration.DefaultFramebuffer;
        uint savedDrawBuffers = stated.DrawBuffers(targetId);
        CurrentFrameBuffer = plan.Target!;
        stated.SetDrawBuffers(targetId, plan.Declaration.ColorSlots);

        bool motion = false;
        CurrentModPass = plan.Declaration.Name;
        statedPass = plan.Declaration;
        try
        {
            if (decl.MotionWriter != null)
            {
                motion = decl.MotionWriter.Mode == EnumOptimumMotionWrite.MotionOnly ? BeginMotionOnlyWrite() : BeginMotionWrite();
            }
            decl.Draw(decl);
            ModPassesRun++;
        }
        catch (Exception error)
        {
            // A mod's draw must not take the frame down with it; the pass is reported once.
            if (modPassFailuresLogged.Add(decl))
                Logger?.Error("[Optimum] mod pass '{0}' of {1} threw: {2}", decl.Name, registration.ModId, error);
        }
        finally
        {
            if (motion) EndMotionWrite();
            CurrentModPass = null;
            statedPass = null;
            stated.SetDrawBuffers(targetId, savedDrawBuffers);
        }
    }

    private ModPassPlan PlanFor(OptimumPassRegistration registration)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (!ReferenceEquals(buffers, modPassPlansFor) || modPassPlansMotionIndex != MotionAttachmentIndex)
        {
            modPassPlans.Clear();
            modPassFailuresLogged.Clear();
            modPassPlansFor = buffers;
            modPassPlansMotionIndex = MotionAttachmentIndex;
        }
        if (!modPassPlans.TryGetValue(registration.Decl, out ModPassPlan? plan))
        {
            plan = BuildModPassPlan(registration);
            modPassPlans[registration.Decl] = plan;
        }
        return plan;
    }

    /// <summary>Resolves the declared handles to the target, its colour slots and the read textures.</summary>
    private ModPassPlan BuildModPassPlan(OptimumPassRegistration registration)
    {
        OptimumPassDecl decl = registration.Decl;
        var plan = new ModPassPlan();
        EnumOptimumTarget target = OptimumPassContract.TargetOf(decl);
        int targetIndex = target switch
        {
            EnumOptimumTarget.Primary => PrimaryIndex,
            EnumOptimumTarget.Transparent => TransparentIndex,
            _ => -1,
        };

        string targetName;
        uint colorSlots = 0;
        if (target == EnumOptimumTarget.Default)
        {
            plan.Target = null;
            targetName = "Default";
            colorSlots = uint.MaxValue;
        }
        else
        {
            List<FrameBufferRef> buffers = FrameBuffers;
            FrameBufferRef? buffer = buffers != null && targetIndex >= 0 && targetIndex < buffers.Count ? buffers[targetIndex] : null;
            if (buffer == null)
            {
                plan.SkipReason = target + " does not exist";
                return plan;
            }
            plan.Target = buffer;
            targetName = targetIndex.ToString(CultureInfo.InvariantCulture);
            foreach (EnumOptimumAttachment write in decl.Writes)
            {
                if (write == EnumOptimumAttachment.PrimaryDepth)
                {
                    if (buffer.DepthTextureId <= 0)
                    {
                        plan.SkipReason = "PrimaryDepth does not exist";
                        return plan;
                    }
                    continue;
                }
                int slot = OptimumPassContract.ColorSlotOf(write);
                if (slot < 0 || buffer.ColorTextureIds == null || slot >= buffer.ColorTextureIds.Length ||
                    buffer.ColorTextureIds[slot] <= 0 || (target == EnumOptimumTarget.Primary && slot == MotionAttachmentIndex))
                {
                    plan.SkipReason = write + " does not exist this session";
                    return plan;
                }
                colorSlots |= 1u << slot;
            }
            if (decl.MotionWriter != null)
            {
                // The window writes the motion attachment on top of the declared slots; without one
                // the begin call refuses and the draw falls back to camera reprojection.
                // Undeclared colour slots stay out of the scope even though the window's draw-buffer
                // mask names them, so a pass can still sample them (the final composition's shape).
                if (MotionAttachmentIndex > -1) colorSlots |= 1u << MotionAttachmentIndex;
            }
        }

        var reads = new List<int>();
        foreach (EnumOptimumAttachment read in decl.Reads)
        {
            int id = TextureOf(read);
            if (id > 0 && !reads.Contains(id)) reads.Add(id);
        }

        plan.Declaration = new PassDeclaration
        {
            Name = "Mod/" + registration.ModId + "/" + decl.Name + "/" + targetName,
            FramebufferId = target == EnumOptimumTarget.Default
                ? PassDeclaration.DefaultFramebuffer
                : plan.Target!.FboId,
            ColorSlots = colorSlots,
            Reads = reads.ToArray(),
            Flags = ModPassFlags,
        };
        plan.Runnable = true;
        return plan;
    }

    /// <summary>The texture behind a handle in the current framebuffer set, 0 when it does not exist.</summary>
    internal int TextureOf(EnumOptimumAttachment attachment)
    {
        switch (attachment)
        {
        case EnumOptimumAttachment.PrimaryMotion:
            return MotionAttachmentIndex >= 0 ? ColourOf(PrimaryIndex, MotionAttachmentIndex) : 0;
        case EnumOptimumAttachment.PrimaryDepth:
            return DepthOf(PrimaryIndex);
        case EnumOptimumAttachment.LiquidDepth:
            return DepthOf(LiquidDepthIndex);
        case EnumOptimumAttachment.ShadowFarDepth:
            return DepthOf(ShadowFarIndex);
        case EnumOptimumAttachment.ShadowNearDepth:
            return DepthOf(ShadowNearIndex);
        case EnumOptimumAttachment.GodRays:
            return ColourOf(GodRaysIndex, 0);
        case EnumOptimumAttachment.BloomLowRes:
            return ColourOf(BlurVerticalLowResIndex, 0);
        case EnumOptimumAttachment.Luma:
            return ColourOf(LumaIndex, 0);
        case EnumOptimumAttachment.SsaoBlurred:
            return ColourOf(SsaoBlurVerticalIndex, 0);
        }
        EnumOptimumTarget target = OptimumPassContract.TargetOf(attachment);
        int slot = OptimumPassContract.ColorSlotOf(attachment);
        if (slot < 0) return 0;
        if (target == EnumOptimumTarget.Primary)
        {
            // Absent G-buffer: slot 2 is the motion attachment, which is not this handle.
            if (slot == MotionAttachmentIndex) return 0;
            return ColourOf(PrimaryIndex, slot);
        }
        return target == EnumOptimumTarget.Transparent ? ColourOf(TransparentIndex, slot) : 0;
    }

    private int ColourOf(int index, int slot)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count) return 0;
        FrameBufferRef buffer = buffers[index];
        if (buffer?.ColorTextureIds == null || slot < 0 || slot >= buffer.ColorTextureIds.Length) return 0;
        return buffer.ColorTextureIds[slot];
    }

    private int DepthOf(int index)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count || buffers[index] == null) return 0;
        return buffers[index].DepthTextureId;
    }
}
