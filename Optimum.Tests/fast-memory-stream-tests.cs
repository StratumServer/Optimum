using System;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Tests;

public sealed class FastMemoryStreamTests
{
    [Fact]
    public void FastMemoryStream_Read_BlockCopyReturnsExpectedBytesAndAdvancesPosition()
    {
        byte[] payload = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        using var stream = new FastMemoryStream(payload, payload.Length);

        byte[] dest = new byte[8];
        int read1 = stream.Read(dest, 0, 4);
        Assert.Equal(4, read1);
        Assert.Equal(4, stream.Position);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, dest[0..4]);

        int read2 = stream.Read(dest, 4, 10);
        Assert.Equal(4, read2);
        Assert.Equal(8, stream.Position);
        Assert.Equal(payload, dest);

        // Reading at end of stream returns 0
        int readPastEnd = stream.Read(dest, 0, 4);
        Assert.Equal(0, readPastEnd);
        Assert.Equal(8, stream.Position);
    }
}
