using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Vintagestory.API.Client
{
    /// <summary>
    /// The pass slots a mod may attach a declared pass to (Vulkan-native plan, Phase 5). The values
    /// equal <see cref="EnumRenderStage" />'s, so a slot is the stage whose renderers it runs after:
    /// the Vulkan platform runs every pass declared for a slot at the end of that stage's bracket,
    /// after the renderers registered through <c>RegisterRenderer</c>. The shadow stages are listed
    /// for completeness and refused by <see cref="OptimumPassContract.Validate" />: their targets
    /// are not mod handles.
    /// </summary>
    public enum EnumOptimumPass
    {
        Before = 0,
        Opaque = 1,
        OIT = 2,
        AfterOIT = 3,
        ShadowFar = 4,
        ShadowFarDone = 5,
        ShadowNear = 6,
        ShadowNearDone = 7,
        AfterPostProcessing = 8,
        AfterBlit = 9,
        Ortho = 10,
        AfterFinalComposition = 11,
        Done = 12,
    }

    /// <summary>
    /// Well-known attachment handles a declared pass reads or writes. The platform resolves each
    /// to the texture behind it for the current framebuffer set; a handle whose texture does not
    /// exist this session (the G-buffer with SSAO off, the motion attachment with TAA off) drops
    /// out of the reads, and a pass that writes one is skipped for the frame.
    /// </summary>
    public enum EnumOptimumAttachment
    {
        None = 0,
        /// <summary>Primary colour 0: the lit scene.</summary>
        PrimaryColor = 1,
        /// <summary>Primary colour 1: glow.</summary>
        PrimaryGlow = 2,
        /// <summary>Primary colour 2: SSAO G-buffer position (SSAO on only).</summary>
        PrimaryGBufferPosition = 3,
        /// <summary>Primary colour 3: SSAO G-buffer normal (SSAO on only).</summary>
        PrimaryGBufferNormal = 4,
        /// <summary>
        /// The TAA motion attachment (TAA on only). Never a declared write: a pass writes it by
        /// declaring an <see cref="OptimumMotionWriterDecl" />, which opens the motion window.
        /// </summary>
        PrimaryMotion = 5,
        /// <summary>Primary's depth, shared with Transparent.</summary>
        PrimaryDepth = 6,
        /// <summary>Transparent colour 0: OIT accumulation (RGBA16F).</summary>
        TransparentAccumulation = 7,
        /// <summary>Transparent colour 1: OIT revealage (R16F).</summary>
        TransparentRevealage = 8,
        /// <summary>Transparent colour 2: OIT glow.</summary>
        TransparentGlow = 9,
        /// <summary>The quarter-resolution liquid depth prepass. Read only.</summary>
        LiquidDepth = 10,
        /// <summary>The far shadow map. Read only.</summary>
        ShadowFarDepth = 11,
        /// <summary>The near shadow map. Read only.</summary>
        ShadowNearDepth = 12,
        /// <summary>The god-rays target. Read only.</summary>
        GodRays = 13,
        /// <summary>The low-resolution bloom blur. Read only.</summary>
        BloomLowRes = 14,
        /// <summary>The luma target. Read only.</summary>
        Luma = 15,
        /// <summary>The blurred ambient occlusion (SSAO on only). Read only.</summary>
        SsaoBlurred = 16,
        /// <summary>The window's colour. Write only.</summary>
        DefaultColor = 17,
    }

    /// <summary>The render target a set of written attachments belongs to.</summary>
    public enum EnumOptimumTarget
    {
        None = 0,
        Primary = 1,
        Transparent = 2,
        Default = 3,
    }

    /// <summary>Which motion window a motion writer opens.</summary>
    public enum EnumOptimumMotionWrite
    {
        /// <summary>Primary's default colour set plus the motion attachment (the draw shades and writes motion).</summary>
        WithColor = 0,
        /// <summary>The motion attachment alone (a velocity-only pass over geometry already shaded).</summary>
        MotionOnly = 1,
    }

    /// <summary>The draw callback of a declared pass. Render thread only.</summary>
    public delegate void OptimumPassDraw(OptimumPassDecl pass);

    /// <summary>
    /// A mod renderer's declaration that it writes motion for its draws, so its geometry does not
    /// ghost under TAA. The writer rules are those of <c>docs/vulkan.md</c> 3.2:
    /// <list type="bullet">
    /// <item>the shader writes <c>vec4(mv, reactive, writerDepth)</c> at
    /// <c>layout(location = TAAMOTIONLOCATION)</c> under <c>#if TAAMOTION &gt; 0</c> (both defines
    /// are stamped into every program);</item>
    /// <item><c>rg</c> is <c>previousPixel - currentPixel</c> in render pixels from unjittered
    /// projections; <c>b</c> is the reactive value in [0,1]; <c>a</c> is the window depth the draw
    /// puts in the depth buffer, including any depth offset;</item>
    /// <item>a draw that has no previous position still writes <c>b</c> and zeroes <c>rg</c> and
    /// <c>a</c>;</item>
    /// <item>the window is replace-blended and exists only inside the temporal window, on Primary,
    /// in <see cref="EnumOptimumPass.Opaque" /> and <see cref="EnumOptimumPass.AfterOIT" />; outside
    /// it the begin call refuses and the pixel falls back to camera reprojection.</item>
    /// </list>
    /// OpenGL ignores the declaration: the begin call returns false there.
    /// </summary>
    public sealed class OptimumMotionWriterDecl
    {
        /// <summary>A name unique within the mod, for logs and traces.</summary>
        public string Name = "";

        public EnumOptimumMotionWrite Mode;

        internal OptimumMotionWriterDecl Clone() => new OptimumMotionWriterDecl { Name = Name, Mode = Mode };
    }

    /// <summary>
    /// A pass a mod declares on the Vulkan frame graph: its slot, the attachments it reads and
    /// writes by well-known handle, its draw callback and, optionally, the motion writer its draws
    /// are. A plain data holder: registration copies it, so later edits change nothing until it is
    /// registered again. OpenGL ignores it.
    /// </summary>
    public sealed class OptimumPassDecl
    {
        /// <summary>A name unique within the mod; registering the same name again replaces the pass.</summary>
        public string Name = "";

        public EnumOptimumPass Slot;

        /// <summary>Attachments the draw samples. Pre-transitioned to shader-readable at pass entry.</summary>
        public EnumOptimumAttachment[] Reads = Array.Empty<EnumOptimumAttachment>();

        /// <summary>
        /// Attachments the draw writes, all on one target (<see cref="EnumOptimumTarget" />).
        /// Colour attachments of that target that are not listed leave the rendering scope, so the
        /// pass may sample them. <see cref="EnumOptimumAttachment.PrimaryDepth" /> is the only depth
        /// write and goes with the Primary or Transparent target.
        /// </summary>
        public EnumOptimumAttachment[] Writes = Array.Empty<EnumOptimumAttachment>();

        public OptimumPassDraw Draw;

        /// <summary>Non-null: the platform opens this motion window around <see cref="Draw" />.</summary>
        public OptimumMotionWriterDecl MotionWriter;

        internal OptimumPassDecl Clone() => new OptimumPassDecl
        {
            Name = Name,
            Slot = Slot,
            Reads = Reads == null ? Array.Empty<EnumOptimumAttachment>() : (EnumOptimumAttachment[])Reads.Clone(),
            Writes = Writes == null ? Array.Empty<EnumOptimumAttachment>() : (EnumOptimumAttachment[])Writes.Clone(),
            Draw = Draw,
            MotionWriter = MotionWriter?.Clone(),
        };
    }

    /// <summary>One registered pass: the owning mod and its (copied) declaration.</summary>
    public sealed class OptimumPassRegistration
    {
        public readonly string ModId;
        public readonly OptimumPassDecl Decl;

        internal OptimumPassRegistration(string modId, OptimumPassDecl decl)
        {
            ModId = modId;
            Decl = decl;
        }
    }

    /// <summary>The rules a declaration must satisfy, shared by registration and the platform.</summary>
    public static class OptimumPassContract
    {
        /// <summary>The target a handle belongs to as an attachment; None for read-only handles.</summary>
        public static EnumOptimumTarget TargetOf(EnumOptimumAttachment attachment)
        {
            switch (attachment)
            {
            case EnumOptimumAttachment.PrimaryColor:
            case EnumOptimumAttachment.PrimaryGlow:
            case EnumOptimumAttachment.PrimaryGBufferPosition:
            case EnumOptimumAttachment.PrimaryGBufferNormal:
            case EnumOptimumAttachment.PrimaryMotion:
                return EnumOptimumTarget.Primary;
            case EnumOptimumAttachment.TransparentAccumulation:
            case EnumOptimumAttachment.TransparentRevealage:
            case EnumOptimumAttachment.TransparentGlow:
                return EnumOptimumTarget.Transparent;
            case EnumOptimumAttachment.DefaultColor:
                return EnumOptimumTarget.Default;
            default:
                return EnumOptimumTarget.None;
            }
        }

        /// <summary>The colour slot of a target attachment, -1 for depth and read-only handles (motion: -1, it moves).</summary>
        public static int ColorSlotOf(EnumOptimumAttachment attachment)
        {
            switch (attachment)
            {
            case EnumOptimumAttachment.PrimaryColor:
            case EnumOptimumAttachment.TransparentAccumulation:
            case EnumOptimumAttachment.DefaultColor:
                return 0;
            case EnumOptimumAttachment.PrimaryGlow:
            case EnumOptimumAttachment.TransparentRevealage:
                return 1;
            case EnumOptimumAttachment.PrimaryGBufferPosition:
            case EnumOptimumAttachment.TransparentGlow:
                return 2;
            case EnumOptimumAttachment.PrimaryGBufferNormal:
                return 3;
            default:
                return -1;
            }
        }

        public static bool IsDepth(EnumOptimumAttachment attachment) =>
            attachment == EnumOptimumAttachment.PrimaryDepth || attachment == EnumOptimumAttachment.LiquidDepth ||
            attachment == EnumOptimumAttachment.ShadowFarDepth || attachment == EnumOptimumAttachment.ShadowNearDepth;

        /// <summary>The slots inside the temporal window where Primary is the drawn target.</summary>
        public static bool IsMotionWindowSlot(EnumOptimumPass slot) =>
            slot == EnumOptimumPass.Opaque || slot == EnumOptimumPass.AfterOIT;

        /// <summary>The slots that run on the window after the scene was blitted.</summary>
        public static bool IsDefaultTargetSlot(EnumOptimumPass slot) =>
            slot == EnumOptimumPass.AfterBlit || slot == EnumOptimumPass.Ortho || slot == EnumOptimumPass.Done;

        /// <summary>The target a declaration draws into; None when it names none or several.</summary>
        public static EnumOptimumTarget TargetOf(OptimumPassDecl decl)
        {
            if (decl == null) return EnumOptimumTarget.None;
            EnumOptimumTarget target = EnumOptimumTarget.None;
            bool depth = false;
            if (decl.Writes != null)
            {
                foreach (EnumOptimumAttachment write in decl.Writes)
                {
                    if (write == EnumOptimumAttachment.PrimaryDepth)
                    {
                        depth = true;
                        continue;
                    }
                    EnumOptimumTarget of = TargetOf(write);
                    if (of == EnumOptimumTarget.None) return EnumOptimumTarget.None;
                    if (target != EnumOptimumTarget.None && target != of) return EnumOptimumTarget.None;
                    target = of;
                }
            }
            if (target == EnumOptimumTarget.None && (depth || decl.MotionWriter != null)) target = EnumOptimumTarget.Primary;
            return target;
        }

        /// <summary>Checks a declaration against the contract; false with the first broken rule.</summary>
        public static bool Validate(OptimumPassDecl decl, out string reason)
        {
            reason = null;
            if (decl == null) { reason = "the declaration is null"; return false; }
            if (string.IsNullOrWhiteSpace(decl.Name)) { reason = "a pass needs a name"; return false; }
            if (decl.Draw == null) { reason = "pass '" + decl.Name + "' has no draw callback"; return false; }
            if (!Enum.IsDefined(typeof(EnumOptimumPass), decl.Slot)) { reason = "pass '" + decl.Name + "' names no slot"; return false; }
            if (decl.Slot == EnumOptimumPass.ShadowFar || decl.Slot == EnumOptimumPass.ShadowFarDone ||
                decl.Slot == EnumOptimumPass.ShadowNear || decl.Slot == EnumOptimumPass.ShadowNearDone)
            {
                reason = "pass '" + decl.Name + "': the shadow stages draw the shadow maps, which are not mod targets";
                return false;
            }

            EnumOptimumAttachment[] writes = decl.Writes ?? Array.Empty<EnumOptimumAttachment>();
            EnumOptimumAttachment[] reads = decl.Reads ?? Array.Empty<EnumOptimumAttachment>();
            if (writes.Length == 0 && decl.MotionWriter == null)
            {
                reason = "pass '" + decl.Name + "' writes nothing";
                return false;
            }
            foreach (EnumOptimumAttachment write in writes)
            {
                if (write == EnumOptimumAttachment.None || !Enum.IsDefined(typeof(EnumOptimumAttachment), write))
                {
                    reason = "pass '" + decl.Name + "' writes an unknown attachment";
                    return false;
                }
                if (write == EnumOptimumAttachment.PrimaryMotion)
                {
                    reason = "pass '" + decl.Name + "' writes PrimaryMotion directly; declare a MotionWriter instead";
                    return false;
                }
                if (write != EnumOptimumAttachment.PrimaryDepth && TargetOf(write) == EnumOptimumTarget.None)
                {
                    reason = "pass '" + decl.Name + "' writes " + write + ", which is read only";
                    return false;
                }
            }
            foreach (EnumOptimumAttachment read in reads)
            {
                if (read == EnumOptimumAttachment.None || !Enum.IsDefined(typeof(EnumOptimumAttachment), read))
                {
                    reason = "pass '" + decl.Name + "' reads an unknown attachment";
                    return false;
                }
                if (read == EnumOptimumAttachment.DefaultColor)
                {
                    reason = "pass '" + decl.Name + "' reads DefaultColor, which is write only";
                    return false;
                }
                if (Array.IndexOf(writes, read) >= 0)
                {
                    reason = "pass '" + decl.Name + "' reads and writes " + read + " (a feedback loop)";
                    return false;
                }
            }

            EnumOptimumTarget target = TargetOf(decl);
            if (target == EnumOptimumTarget.None)
            {
                reason = "pass '" + decl.Name + "' writes attachments of more than one target";
                return false;
            }
            if (target == EnumOptimumTarget.Default)
            {
                if (Array.IndexOf(writes, EnumOptimumAttachment.PrimaryDepth) >= 0)
                {
                    reason = "pass '" + decl.Name + "' writes PrimaryDepth on the default target";
                    return false;
                }
                if (!IsDefaultTargetSlot(decl.Slot))
                {
                    reason = "pass '" + decl.Name + "' writes DefaultColor in " + decl.Slot + "; only AfterBlit, Ortho and Done draw on the window";
                    return false;
                }
            }
            else if (IsDefaultTargetSlot(decl.Slot))
            {
                reason = "pass '" + decl.Name + "' writes " + target + " in " + decl.Slot + ", after the scene was blitted";
                return false;
            }

            if (decl.MotionWriter != null && !ValidateMotionWriter(decl.MotionWriter, out reason, decl.Slot, target))
            {
                reason = "pass '" + decl.Name + "': " + reason;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks a motion writer. With a slot and target (a writer on a declared pass) the window
        /// rules are checked too; a renderer's writer is checked against them at begin time.
        /// </summary>
        public static bool ValidateMotionWriter(OptimumMotionWriterDecl writer, out string reason,
            EnumOptimumPass? slot = null, EnumOptimumTarget target = EnumOptimumTarget.Primary)
        {
            reason = null;
            if (writer == null) { reason = "the motion writer is null"; return false; }
            if (string.IsNullOrWhiteSpace(writer.Name)) { reason = "a motion writer needs a name"; return false; }
            if (!Enum.IsDefined(typeof(EnumOptimumMotionWrite), writer.Mode)) { reason = "motion writer '" + writer.Name + "' has an unknown mode"; return false; }
            if (slot.HasValue && !IsMotionWindowSlot(slot.Value))
            {
                reason = "motion writer '" + writer.Name + "' in " + slot.Value + ": the motion window exists only in Opaque and AfterOIT";
                return false;
            }
            if (target != EnumOptimumTarget.Primary)
            {
                reason = "motion writer '" + writer.Name + "' draws into " + target + "; the motion attachment is Primary's";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The registry of mod-declared passes and motion writers (Vulkan-native plan, Phase 5). Stored
    /// per mod; everything a mod registered is removed when the client leaves the world (mods unload
    /// with it), or earlier through <see cref="UnregisterMod" />. The Vulkan platform reads
    /// <see cref="ForSlot" /> at each stage's end and installs the motion hooks; OpenGL reads nothing,
    /// so on OpenGL registration succeeds and has no effect.
    ///
    /// Register from the main (render) thread, typically in <c>StartClientSide</c>.
    /// </summary>
    public static class OptimumModPasses
    {
        private sealed class ModEntry
        {
            public readonly string ModId;
            public readonly List<OptimumPassRegistration> Passes = new List<OptimumPassRegistration>();
            public readonly List<OptimumMotionWriterDecl> Writers = new List<OptimumMotionWriterDecl>();
            public ICoreClientAPI Api;

            public ModEntry(string modId) => ModId = modId;

            public void OnLeaveWorld() => UnregisterMod(ModId);
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ModEntry> Mods = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        private static readonly OptimumPassRegistration[][] Slots = new OptimumPassRegistration[13][];
        private static readonly OptimumPassRegistration[] Empty = Array.Empty<OptimumPassRegistration>();
        private static bool dirty = true;
        private static long version;

        /// <summary>
        /// Installed by the Vulkan platform while its graphics are up; null on OpenGL. Opens the
        /// motion window for a registered writer and returns whether it opened.
        /// </summary>
        public static System.Func<OptimumMotionWriterDecl, bool> MotionBeginHook;

        /// <summary>Installed with <see cref="MotionBeginHook" />: closes the window it opened.</summary>
        public static Action MotionEndHook;

        /// <summary>Changes on every registration change.</summary>
        public static long Version
        {
            get { lock (Gate) return version; }
        }

        /// <summary>Registered passes over all mods.</summary>
        public static int PassCount
        {
            get
            {
                lock (Gate)
                {
                    int count = 0;
                    foreach (ModEntry entry in Mods.Values) count += entry.Passes.Count;
                    return count;
                }
            }
        }

        /// <summary>
        /// Registers (or replaces, by name) a pass for <paramref name="modId" />. False with the
        /// broken rule when the declaration does not satisfy <see cref="OptimumPassContract.Validate" />.
        /// </summary>
        public static bool Register(ICoreClientAPI capi, string modId, OptimumPassDecl decl, out string reason)
        {
            if (capi == null) throw new ArgumentNullException(nameof(capi));
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("a mod id is required", nameof(modId));
            if (!OptimumPassContract.Validate(decl, out reason)) return false;

            OptimumPassDecl copy = decl.Clone();
            lock (Gate)
            {
                ModEntry entry = EntryFor(capi, modId);
                for (int i = 0; i < entry.Passes.Count; i++)
                {
                    if (entry.Passes[i].Decl.Name == copy.Name)
                    {
                        entry.Passes.RemoveAt(i);
                        break;
                    }
                }
                entry.Passes.Add(new OptimumPassRegistration(modId, copy));
                Changed();
            }
            return true;
        }

        /// <summary>
        /// Registers a motion writer a <c>RegisterRenderer</c> renderer opens around its own draws
        /// with <see cref="BeginMotionWriter" />. The same instance is what Begin takes.
        /// </summary>
        public static bool RegisterMotionWriter(ICoreClientAPI capi, string modId, OptimumMotionWriterDecl writer, out string reason)
        {
            if (capi == null) throw new ArgumentNullException(nameof(capi));
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("a mod id is required", nameof(modId));
            if (!OptimumPassContract.ValidateMotionWriter(writer, out reason)) return false;
            lock (Gate)
            {
                ModEntry entry = EntryFor(capi, modId);
                if (!entry.Writers.Contains(writer)) entry.Writers.Add(writer);
                Changed();
            }
            return true;
        }

        /// <summary>Removes one pass of a mod; false when it had none of that name.</summary>
        public static bool Unregister(string modId, string passName)
        {
            lock (Gate)
            {
                if (modId == null || !Mods.TryGetValue(modId, out ModEntry entry)) return false;
                for (int i = 0; i < entry.Passes.Count; i++)
                {
                    if (entry.Passes[i].Decl.Name != passName) continue;
                    entry.Passes.RemoveAt(i);
                    Changed();
                    return true;
                }
                return false;
            }
        }

        /// <summary>Removes everything a mod registered and detaches from its API's LeaveWorld.</summary>
        public static void UnregisterMod(string modId)
        {
            ModEntry entry;
            lock (Gate)
            {
                if (modId == null || !Mods.TryGetValue(modId, out entry)) return;
                Mods.Remove(modId);
                Changed();
            }
            IClientEventAPI events = entry.Api?.Event;
            if (events != null) events.LeaveWorld -= entry.OnLeaveWorld;
        }

        /// <summary>The mods with at least one registration, for diagnostics.</summary>
        public static string[] RegisteredMods()
        {
            lock (Gate)
            {
                var ids = new string[Mods.Count];
                Mods.Keys.CopyTo(ids, 0);
                return ids;
            }
        }

        /// <summary>
        /// The passes declared for a slot, in registration order (mods by first registration).
        /// The array is shared and rebuilt only when a registration changes; never modify it.
        /// </summary>
        public static OptimumPassRegistration[] ForSlot(EnumOptimumPass slot)
        {
            int index = (int)slot;
            if (index < 0 || index >= Slots.Length) return Empty;
            lock (Gate)
            {
                if (dirty) Rebuild();
                return Slots[index];
            }
        }

        /// <summary>Whether <paramref name="writer" /> is registered for any mod.</summary>
        public static bool IsRegisteredWriter(OptimumMotionWriterDecl writer)
        {
            if (writer == null) return false;
            lock (Gate)
            {
                foreach (ModEntry entry in Mods.Values)
                {
                    if (entry.Writers.Contains(writer)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Opens the motion window for a registered writer around a renderer's draws. False when the
        /// window did not open (OpenGL, TAA off, outside Opaque/AfterOIT, Primary not bound, an
        /// unregistered writer); then do not call <see cref="EndMotionWriter" />.
        /// </summary>
        public static bool BeginMotionWriter(OptimumMotionWriterDecl writer)
        {
            System.Func<OptimumMotionWriterDecl, bool> hook = MotionBeginHook;
            return hook != null && IsRegisteredWriter(writer) && hook(writer);
        }

        /// <summary>Closes the window a true <see cref="BeginMotionWriter" /> opened.</summary>
        public static void EndMotionWriter()
        {
            Action hook = MotionEndHook;
            if (hook != null) hook();
        }

        private static ModEntry EntryFor(ICoreClientAPI capi, string modId)
        {
            if (!Mods.TryGetValue(modId, out ModEntry entry))
            {
                entry = new ModEntry(modId);
                Mods.Add(modId, entry);
            }
            if (entry.Api == null)
            {
                entry.Api = capi;
                IClientEventAPI events = capi.Event;
                if (events != null) events.LeaveWorld += entry.OnLeaveWorld;
            }
            return entry;
        }

        private static void Changed()
        {
            dirty = true;
            version++;
        }

        private static void Rebuild()
        {
            var lists = new List<OptimumPassRegistration>[Slots.Length];
            foreach (ModEntry entry in Mods.Values)
            {
                foreach (OptimumPassRegistration registration in entry.Passes)
                {
                    int slot = (int)registration.Decl.Slot;
                    (lists[slot] ??= new List<OptimumPassRegistration>()).Add(registration);
                }
            }
            for (int i = 0; i < Slots.Length; i++) Slots[i] = lists[i] == null ? Empty : lists[i].ToArray();
            dirty = false;
        }
    }

    /// <summary>The client-API entry points: <c>capi.RegisterOptimumPass(this, decl)</c> from a mod system.</summary>
    public static class OptimumModRenderExtensions
    {
        /// <summary>The id registrations of a mod system are stored under: its mod id, else its assembly name.</summary>
        public static string OptimumModId(ModSystem system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            string id = system.Mod?.Info?.ModID;
            return string.IsNullOrWhiteSpace(id) ? system.GetType().Assembly.GetName().Name : id;
        }

        public static bool RegisterOptimumPass(this ICoreClientAPI capi, ModSystem system, OptimumPassDecl decl, out string reason) =>
            OptimumModPasses.Register(capi, OptimumModId(system), decl, out reason);

        public static bool RegisterOptimumMotionWriter(this ICoreClientAPI capi, ModSystem system, OptimumMotionWriterDecl writer, out string reason) =>
            OptimumModPasses.RegisterMotionWriter(capi, OptimumModId(system), writer, out reason);

        public static void UnregisterOptimumPasses(this ICoreClientAPI capi, ModSystem system) =>
            OptimumModPasses.UnregisterMod(OptimumModId(system));
    }
}
