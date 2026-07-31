using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Vintagestory.GameContent;

/// <summary>
/// Manages a GL_TEXTURE_2D_ARRAY for map page rendering. Each layer holds
/// one 256x256 page (8x8 chunks). A free-list tracks available layers and
/// LRU eviction reclaims layers when the array fills.
///
/// Created when the map opens; destroyed when the map closes or the game
/// shuts down. The texture lives on the render thread; all methods must
/// call from the main/render thread.
/// </summary>
public sealed class OptimumMapTextureArray : IDisposable
{
    public const int PageSize = OptimumMapPageCache.PagePixelSize; // 256
    private const int GL_TEXTURE_2D_ARRAY = 35866;
    private const int GL_RGBA8 = 32856;
    private const int GL_RGBA = 6408;
    private const int GL_UNSIGNED_BYTE = 5121;
    private const int GL_NEAREST = 9728;
    private const int GL_LINEAR = 9729;
    private const int GL_CLAMP_TO_EDGE = 33071;
    private const int GL_TEXTURE_MIN_FILTER = 10241;
    private const int GL_TEXTURE_MAG_FILTER = 10240;
    private const int GL_TEXTURE_WRAP_S = 10242;
    private const int GL_TEXTURE_WRAP_T = 10243;

    public int TextureId { get; private set; }
    public int MaxLayers { get; }

    private readonly Stack<int> _freeList;
    private readonly Dictionary<long, int> _pageToLayer; // packed page coord -> layer index
    private readonly LinkedList<long> _lruOrder; // front = most recently used
    private readonly Dictionary<long, LinkedListNode<long>> _lruNodes;
    private bool _disposed;

    public OptimumMapTextureArray(int maxLayers)
    {
        MaxLayers = maxLayers;
        _freeList = new Stack<int>(maxLayers);
        _pageToLayer = new Dictionary<long, int>(maxLayers);
        _lruOrder = new LinkedList<long>();
        _lruNodes = new Dictionary<long, LinkedListNode<long>>(maxLayers);

        // Initialize free list (all layers available)
        for (int i = maxLayers - 1; i >= 0; i--)
        {
            _freeList.Push(i);
        }

        // Create the GL texture array
        TextureId = GL.GenTexture();
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, TextureId);
        GL.TexImage3D(
            (TextureTarget)GL_TEXTURE_2D_ARRAY,
            0,
            (PixelInternalFormat)GL_RGBA8,
            PageSize,
            PageSize,
            maxLayers,
            0,
            (PixelFormat)GL_RGBA,
            (PixelType)GL_UNSIGNED_BYTE,
            IntPtr.Zero
        );
        GL.TexParameter((TextureTarget)GL_TEXTURE_2D_ARRAY, (TextureParameterName)GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        GL.TexParameter((TextureTarget)GL_TEXTURE_2D_ARRAY, (TextureParameterName)GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        GL.TexParameter((TextureTarget)GL_TEXTURE_2D_ARRAY, (TextureParameterName)GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        GL.TexParameter((TextureTarget)GL_TEXTURE_2D_ARRAY, (TextureParameterName)GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, 0);
    }

    /// <summary>
    /// Upload a page's pixels into a layer. Returns the layer index assigned.
    /// If no free layers remain, evicts the least recently used page.
    /// </summary>
    public int UploadPage(long pageKey, int[] pixels)
    {
        if (_disposed) return -1;
        if (pixels == null || pixels.Length != PageSize * PageSize) return -1;

        // Check if this page already has a layer
        if (_pageToLayer.TryGetValue(pageKey, out int existingLayer))
        {
            TouchLru(pageKey);
            UploadToLayer(existingLayer, pixels);
            return existingLayer;
        }

        int layer;
        if (_freeList.Count > 0)
        {
            layer = _freeList.Pop();
        }
        else
        {
            // Evict the least recently used (back of list)
            long evictKey = _lruOrder.Last!.Value;
            layer = _pageToLayer[evictKey];
            RemoveFromTracking(evictKey);
        }

        _pageToLayer[pageKey] = layer;
        AddToLru(pageKey);
        UploadToLayer(layer, pixels);
        return layer;
    }

    /// <summary>
    /// Get the layer index for a page. Returns -1 if not loaded.
    /// </summary>
    public int GetLayer(long pageKey)
    {
        if (_pageToLayer.TryGetValue(pageKey, out int layer))
        {
            TouchLru(pageKey);
            return layer;
        }
        return -1;
    }

    /// <summary>
    /// Check if a page is loaded in the texture array.
    /// </summary>
    public bool HasPage(long pageKey) => _pageToLayer.ContainsKey(pageKey);

    /// <summary>
    /// Remove a page from the texture array (e.g. on invalidation).
    /// </summary>
    public void RemovePage(long pageKey)
    {
        if (!_pageToLayer.TryGetValue(pageKey, out int layer)) return;
        RemoveFromTracking(pageKey);
        _freeList.Push(layer);
    }

    /// <summary>
    /// Number of layers currently in use.
    /// </summary>
    public int UsedLayers => _pageToLayer.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (TextureId != 0)
        {
            GL.DeleteTexture(TextureId);
            TextureId = 0;
        }

        _pageToLayer.Clear();
        _freeList.Clear();
        _lruOrder.Clear();
        _lruNodes.Clear();
    }

    private void UploadToLayer(int layer, int[] pixels)
    {
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, TextureId);
        GL.TexSubImage3D(
            (TextureTarget)GL_TEXTURE_2D_ARRAY,
            0,        // mip level
            0, 0,     // x, y offset within the layer
            layer,    // z offset = layer index
            PageSize,
            PageSize,
            1,        // depth = 1 layer
            (PixelFormat)GL_RGBA,
            (PixelType)GL_UNSIGNED_BYTE,
            pixels
        );
    }

    private void AddToLru(long key)
    {
        var node = _lruOrder.AddFirst(key);
        _lruNodes[key] = node;
    }

    private void TouchLru(long key)
    {
        if (_lruNodes.TryGetValue(key, out var node))
        {
            _lruOrder.Remove(node);
            _lruOrder.AddFirst(node);
        }
    }

    private void RemoveFromTracking(long key)
    {
        _pageToLayer.Remove(key);
        if (_lruNodes.TryGetValue(key, out var node))
        {
            _lruOrder.Remove(node);
            _lruNodes.Remove(key);
        }
    }
}
