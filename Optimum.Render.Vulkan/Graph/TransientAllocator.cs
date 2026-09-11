using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>What a transient image has to be: two leases with equal descriptions may share one image.</summary>
internal readonly record struct TransientImageDesc(uint Width, uint Height, Format Format, uint MipLevels = 1, uint Layers = 1);

/// <summary>
/// One transient lifetime served by a physical image for the current frame.
/// </summary>
/// <param name="TextureId">The physical image (a texture id in the device's table).</param>
/// <param name="Slot">The <see cref="TransientPlacement" /> slot for this frame.</param>
/// <param name="FirstPass">First pass of the lifetime, inclusive.</param>
/// <param name="LastPass">Last pass of the lifetime, inclusive.</param>
/// <param name="Aliased">An earlier lease of this frame already used the same image.</param>
/// <param name="Bytes">The image's memory size.</param>
internal readonly record struct TransientLease(int TextureId, int Slot, int FirstPass, int LastPass, bool Aliased, ulong Bytes);

/// <summary>
/// What <see cref="TransientAllocator" /> needs from the device. An interface so placement
/// and pooling can be tested without one (<c>TransientAllocatorTests</c>).
/// </summary>
internal interface ITransientBacking
{
    /// <summary>Creates an image in the Transient memory pool class and returns its texture id.</summary>
    int Create(TransientImageDesc desc);

    /// <summary>Releases an image; the device retires it on the timeline.</summary>
    void Destroy(int textureId);

    ulong BytesOf(int textureId);

    /// <summary>The description of a texture that can be served by a transient image.</summary>
    bool TryDescribe(int textureId, out TransientImageDesc desc);

    /// <summary>The image's contents stop mattering: its next use transitions from UNDEFINED.</summary>
    void Discard(int textureId);

    /// <summary>Makes <paramref name="logicalTextureId" /> resolve to the physical image until <see cref="RestoreBindings" />.</summary>
    void Rebind(int logicalTextureId, int physicalTextureId);

    /// <summary>Undoes every <see cref="Rebind" />.</summary>
    void RestoreBindings();
}

/// <summary>
/// Physical backing for frame-graph transients (Phase 2 step 4).
///
/// The graph acquires a transient in frame order with its pass lifetime
/// (<see cref="Acquire" />) and gets the physical image that serves it this frame. Images
/// come from the Transient memory pool class and are kept across frames, one pool per
/// <see cref="TransientImageDesc" />: the k-th slot of a description in a frame takes that
/// description's k-th image, so a frame shaped like the last one creates nothing.
///
/// <b>Aliasing off</b> (the default): every lease gets its own image and keeps its
/// contents like any texture. <b>Aliasing on</b> (<c>OPTIMUM_VULKAN_ALIAS=1</c>): leases
/// are placed with <see cref="TransientPlacement" />, so leases whose pass lifetimes do not
/// overlap share an image, and every lease discards: its first use this frame transitions
/// from UNDEFINED (the previous lease's uses stay on that barrier's source side).
///
/// Placement stays stable while a frame streams in: leases arrive with non-decreasing first
/// passes and their placement ids increase, so <see cref="TransientPlacement.Place" />'s
/// start order is arrival order and a later lease never moves an earlier one.
///
/// Framebuffer slots 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 18 and 21 (the post chain) opt in:
/// their colour textures are created in the Transient pool and registered
/// (<see cref="OptIn" />), and the graph serves them through <see cref="Bind" />.
/// </summary>
internal sealed class TransientAllocator
{
    public const string AliasVariable = "OPTIMUM_VULKAN_ALIAS";

    /// <summary>Frames a pooled image may go unused before it is released.</summary>
    public const int IdleFrames = 120;

    /// <summary>The client framebuffer slots whose colour textures are transient.</summary>
    public static readonly int[] PostChainSlots = { 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 18, 21 };

    public static bool IsPostChainSlot(int slot) => Array.IndexOf(PostChainSlots, slot) >= 0;

    /// <summary>Aliasing is off unless <c>OPTIMUM_VULKAN_ALIAS=1</c>.</summary>
    public static bool AliasingFromEnvironment() => Environment.GetEnvironmentVariable(AliasVariable) == "1";

    private sealed class PhysicalImage
    {
        public int TextureId;
        public ulong Bytes;
        public long LastUsedFrame;
    }

    private readonly ITransientBacking _backing;
    private readonly Dictionary<TransientImageDesc, List<PhysicalImage>> _pools = new();
    private readonly Dictionary<TransientImageDesc, int> _formatIds = new();
    private readonly Dictionary<TransientImageDesc, int> _usedThisFrame = new();
    private readonly List<TransientInterval> _intervals = new();
    private readonly List<PhysicalImage> _slotImages = new();
    private readonly List<TransientLease> _leases = new();
    private readonly Dictionary<int, int> _optedIn = new();
    private bool _aliasing;
    private long _frame;
    private int _lastFirstPass = -1;

    public TransientAllocator(ITransientBacking backing, bool aliasing)
    {
        _backing = backing ?? throw new ArgumentNullException(nameof(backing));
        _aliasing = aliasing;
    }

    /// <summary>Whether leases share images. Changes only between frames.</summary>
    public bool Aliasing
    {
        get => _aliasing;
        set
        {
            if (_leases.Count > 0) throw new InvalidOperationException("aliasing changes between frames only");
            _aliasing = value;
        }
    }

    /// <summary>The leases handed out since <see cref="BeginFrame" />, in order.</summary>
    public IReadOnlyList<TransientLease> Leases => _leases;

    /// <summary>Physical images held across frames.</summary>
    public int PhysicalImageCount
    {
        get
        {
            int count = 0;
            foreach (List<PhysicalImage> pool in _pools.Values) count += pool.Count;
            return count;
        }
    }

    /// <summary>Bytes of the physical images held.</summary>
    public ulong PhysicalBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (List<PhysicalImage> pool in _pools.Values)
            {
                foreach (PhysicalImage image in pool) bytes += image.Bytes;
            }
            return bytes;
        }
    }

    /// <summary>Bytes of the opted-in logical textures (their own images).</summary>
    public ulong OptedInBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (int id in _optedIn.Keys) bytes += _backing.BytesOf(id);
            return bytes;
        }
    }

    /// <summary>Bytes of this frame's leases served by an image an earlier lease already used.</summary>
    public ulong AliasedBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (TransientLease lease in _leases)
            {
                if (lease.Aliased) bytes += lease.Bytes;
            }
            return bytes;
        }
    }

    public int AliasedLeaseCount
    {
        get
        {
            int count = 0;
            foreach (TransientLease lease in _leases)
            {
                if (lease.Aliased) count++;
            }
            return count;
        }
    }

    /// <summary>Registers a client texture as a transient resource of framebuffer <paramref name="framebufferSlot" />.</summary>
    public void OptIn(int logicalTextureId, int framebufferSlot)
    {
        if (logicalTextureId <= 0) return;
        _optedIn[logicalTextureId] = framebufferSlot;
    }

    /// <summary>Drops a deleted texture from the opt-in set.</summary>
    public void Forget(int logicalTextureId) => _optedIn.Remove(logicalTextureId);

    public bool IsOptedIn(int textureId) => _optedIn.ContainsKey(textureId);

    /// <summary>The framebuffer slot an opted-in texture belongs to, or -1.</summary>
    public int SlotOf(int textureId) => _optedIn.TryGetValue(textureId, out int slot) ? slot : -1;

    public int OptedInCount => _optedIn.Count;

    /// <summary>
    /// Ends the previous frame's leases and bindings, and releases images that went
    /// unused for <see cref="IdleFrames" /> frames. Call once per frame, before the
    /// first <see cref="Acquire" />.
    /// </summary>
    public void BeginFrame()
    {
        _backing.RestoreBindings();
        _intervals.Clear();
        _slotImages.Clear();
        _usedThisFrame.Clear();
        _leases.Clear();
        _lastFirstPass = -1;
        _frame++;
        Trim();
    }

    /// <summary>
    /// The physical image serving a transient with pass lifetime
    /// [<paramref name="firstPass" />, <paramref name="lastPass" />] this frame.
    /// Leases arrive in frame order: a first pass below an earlier lease's is rejected.
    /// </summary>
    public TransientLease Acquire(TransientImageDesc desc, int firstPass, int lastPass)
    {
        if (firstPass < 0) throw new ArgumentOutOfRangeException(nameof(firstPass), firstPass, "negative pass");
        if (lastPass < firstPass) throw new ArgumentOutOfRangeException(nameof(lastPass), lastPass, "ends before it starts");
        if (firstPass < _lastFirstPass)
        {
            throw new InvalidOperationException(
                "transients are acquired in frame order: first pass " + firstPass + " after " + _lastFirstPass);
        }
        _lastFirstPass = firstPass;

        int slot;
        if (_aliasing)
        {
            _intervals.Add(new TransientInterval(_intervals.Count, BucketOf(desc), firstPass, lastPass));
            int[] slots = TransientPlacement.Place(_intervals);
            slot = slots[slots.Length - 1];
        }
        else
        {
            slot = _slotImages.Count;
        }

        bool aliased = slot < _slotImages.Count;
        PhysicalImage image;
        if (aliased)
        {
            image = _slotImages[slot];
        }
        else
        {
            // Placement numbers slots densely in start order, which is arrival order here.
            if (slot != _slotImages.Count)
                throw new InvalidOperationException("placement opened slot " + slot + " with " + _slotImages.Count + " open");
            image = Take(desc);
            _slotImages.Add(image);
        }

        image.LastUsedFrame = _frame;
        if (_aliasing) _backing.Discard(image.TextureId);

        var lease = new TransientLease(image.TextureId, slot, firstPass, lastPass, aliased, image.Bytes);
        _leases.Add(lease);
        return lease;
    }

    /// <summary>
    /// Serves a client texture for [<paramref name="firstPass" />, <paramref name="lastPass" />]
    /// this frame and returns the texture id that now backs it. With aliasing off the texture
    /// keeps its own image; with aliasing on its id resolves to a leased image until the next
    /// <see cref="BeginFrame" />.
    /// </summary>
    public int Bind(int logicalTextureId, int firstPass, int lastPass)
    {
        if (!_aliasing) return logicalTextureId;
        if (!_backing.TryDescribe(logicalTextureId, out TransientImageDesc desc)) return logicalTextureId;
        TransientLease lease = Acquire(desc, firstPass, lastPass);
        _backing.Rebind(logicalTextureId, lease.TextureId);
        return lease.TextureId;
    }

    private PhysicalImage Take(TransientImageDesc desc)
    {
        if (!_pools.TryGetValue(desc, out List<PhysicalImage>? pool))
        {
            pool = new List<PhysicalImage>();
            _pools.Add(desc, pool);
        }

        _usedThisFrame.TryGetValue(desc, out int index);
        _usedThisFrame[desc] = index + 1;
        if (index < pool.Count) return pool[index];

        int id = _backing.Create(desc);
        var image = new PhysicalImage { TextureId = id, Bytes = _backing.BytesOf(id), LastUsedFrame = _frame };
        pool.Add(image);
        return image;
    }

    private SizeBucket BucketOf(TransientImageDesc desc)
    {
        if (!_formatIds.TryGetValue(desc, out int id))
        {
            id = _formatIds.Count + 1;
            _formatIds.Add(desc, id);
        }
        return new SizeBucket((int)desc.Width, (int)desc.Height, id, 0);
    }

    /// <summary>Pools serve the k-th slot with the k-th image, so only trailing images can go.</summary>
    private void Trim()
    {
        foreach (List<PhysicalImage> pool in _pools.Values)
        {
            while (pool.Count > 0 && _frame - pool[pool.Count - 1].LastUsedFrame > IdleFrames)
            {
                _backing.Destroy(pool[pool.Count - 1].TextureId);
                pool.RemoveAt(pool.Count - 1);
            }
        }
    }
}
