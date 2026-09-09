using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class MeshDrawRangeTests
{
    [Fact]
    public void PoolOffsetsArePointerSizedAndStayPairedWithTheirCounts()
    {
        // MeshDataPool allocates two ints per GL pointer and writes the low
        // word at group * 2. Spare capacity is not another group to draw.
        int[] starts = { 48, 0, 28536, 0, 21600, 0, 123456, 0 };
        int[] sizes = { 384, 5400, 144, 999 };
        var commands = new DrawIndexedIndirectCommand[3];

        MeshManager.WriteIndirectCommands(commands, starts, sizes);

        Assert.Equal(new uint[] { 12, 7134, 5400 }, Array.ConvertAll(commands, c => c.FirstIndex));
        Assert.Equal(new uint[] { 384, 5400, 144 }, Array.ConvertAll(commands, c => c.IndexCount));
        Assert.All(commands, command =>
        {
            Assert.Equal(1u, command.InstanceCount);
            Assert.Equal(0, command.VertexOffset);
            Assert.Equal(0u, command.FirstInstance);
        });
    }

    [Fact]
    public void ZeroGroupsNeedNoOffsets()
    {
        MeshManager.WriteIndirectCommands(Span<DrawIndexedIndirectCommand>.Empty,
            ReadOnlySpan<int>.Empty, ReadOnlySpan<int>.Empty);
    }

    [Fact]
    public void LargeByteOffsetsRetainBothWordsBeforeConversionToAnIndex()
    {
        var commands = new DrawIndexedIndirectCommand[2];
        MeshManager.WriteIndirectCommands(commands,
            new[] { unchecked((int)0x80000000), 0, 24, 1 }, new[] { 6, 12 });
        Assert.Equal(0x20000000u, commands[0].FirstIndex);
        Assert.Equal(0x40000006u, commands[1].FirstIndex);
    }
}
