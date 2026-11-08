using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class MeshDataZeroCopyCastTests
{
    [Fact]
    public void AsBytes_FloatArray_MatchesBlockCopy()
    {
        float[] floats = new float[] { 1.0f, 2.5f, -3.14f, 100.0f };
        ReadOnlySpan<byte> span = OptimumApiBridge.AsBytes(floats);

        byte[] expected = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, expected, 0, expected.Length);

        Assert.Equal(expected.Length, span.Length);
        Assert.True(span.SequenceEqual(expected));
    }

    [Fact]
    public void AsBytes_IntArray_SliceMatchesBlockCopy()
    {
        int[] ints = new int[] { 10, 20, 30, 40, 50 };
        ReadOnlySpan<byte> span = OptimumApiBridge.AsBytes(ints, 1, 3);

        byte[] expected = new byte[3 * sizeof(int)];
        Buffer.BlockCopy(ints, 1 * sizeof(int), expected, 0, expected.Length);

        Assert.Equal(expected.Length, span.Length);
        Assert.True(span.SequenceEqual(expected));
    }

    [Fact]
    public void AsBytes_ShortArray_MatchesBlockCopy()
    {
        short[] shorts = new short[] { 100, -200, 300, -400 };
        ReadOnlySpan<byte> span = OptimumApiBridge.AsBytes(shorts.AsSpan());

        byte[] expected = new byte[shorts.Length * sizeof(short)];
        Buffer.BlockCopy(shorts, 0, expected, 0, expected.Length);

        Assert.Equal(expected.Length, span.Length);
        Assert.True(span.SequenceEqual(expected));
    }

    [Fact]
    public void AsBytes_NullArray_ReturnsEmptySpan()
    {
        float[]? nullFloats = null;
        ReadOnlySpan<byte> span = OptimumApiBridge.AsBytes(nullFloats);
        Assert.True(span.IsEmpty);

        ReadOnlySpan<byte> sliceSpan = OptimumApiBridge.AsBytes(nullFloats, 0, 0);
        Assert.True(sliceSpan.IsEmpty);
    }
}
