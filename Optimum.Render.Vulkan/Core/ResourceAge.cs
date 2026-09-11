using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Tells a resource created in the last few frames from a long-lived one, with
/// no per-resource storage.
///
/// Resource ids (<see cref="ResourceIds" />) only ever increase, so the highest id
/// issued when a frame began is a watermark: every id above the watermark of the
/// frame <c>N - 1</c> frames back was created within the last N frames. The class
/// keeps one watermark per frame in a ring.
///
/// The descriptor layer uses it to route sets naming short-lived resources (GUI
/// text, atlas tasks, fresh chunk meshes, overflow uniform copies) to the per-slot
/// arena that is reset every frame, instead of caching them in
/// <see cref="DescriptorCache" /> only to evict them moments later.
/// </summary>
internal sealed class ResourceAge
{
    public const int DefaultShortLivedFrames = 60;

    private readonly ulong[] _watermarks;
    private long _frames;
    private int _shortLivedFrames;

    public ResourceAge(int capacity = DefaultShortLivedFrames)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _watermarks = new ulong[capacity];
        _shortLivedFrames = capacity;
    }

    /// <summary>
    /// How many frames a resource counts as short-lived for, at most the ring's
    /// capacity. Zero makes every resource long-lived (the arena is never used).
    /// </summary>
    public int ShortLivedFrames
    {
        get => _shortLivedFrames;
        set
        {
            if (value < 0 || value > _watermarks.Length) throw new ArgumentOutOfRangeException(nameof(value));
            _shortLivedFrames = value;
        }
    }

    /// <summary>Frames noted so far.</summary>
    public long Frames => _frames;

    /// <summary>Records the watermark at the start of a frame: the highest resource id issued so far.</summary>
    public void NoteFrame(ulong highestIssuedId)
    {
        _watermarks[_frames % _watermarks.Length] = highestIssuedId;
        _frames++;
    }

    /// <summary>
    /// Whether <paramref name="resource" /> was created within the last
    /// <see cref="ShortLivedFrames" /> frames, the current one included. Id 0 (a
    /// permanent resource) never is. Before that many frames have been noted,
    /// every resource is: none can be older.
    /// </summary>
    public bool IsShortLived(ulong resource)
    {
        if (resource == 0 || _shortLivedFrames == 0) return false;
        if (_frames < _shortLivedFrames) return true;

        ulong watermark = _watermarks[(_frames - _shortLivedFrames) % _watermarks.Length];
        return resource > watermark;
    }

    /// <summary>Whether any resource a set names is short-lived.</summary>
    public bool NamesShortLived(DescriptorSetContents contents)
    {
        foreach (SamplerBindingValue sampler in contents.Samplers)
        {
            if (IsShortLived(sampler.Resource)) return true;
        }
        foreach (BufferBindingValue buffer in contents.Buffers)
        {
            if (IsShortLived(buffer.Resource)) return true;
        }
        return false;
    }
}
