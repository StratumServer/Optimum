using Vintagestory.API.Datastructures;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

public class SortableQueueItemAtTests
{
    [Fact]
    public void ItemAtMatchesQueueOrderBeforeAnyWraparound()
    {
        var q = new SortableQueue<ComparableInt>();
        q.Enqueue(new ComparableInt(1));
        q.Enqueue(new ComparableInt(2));
        q.Enqueue(new ComparableInt(3));

        Assert.Equal(1, q.ItemAt(0).Value);
        Assert.Equal(2, q.ItemAt(1).Value);
        Assert.Equal(3, q.ItemAt(2).Value);
    }

    [Fact]
    public void ItemAtMatchesQueueOrderAfterWraparound()
    {
        // maxSize starts at 27; dequeue past the front repeatedly to push
        // head forward until head % maxSize > tail % maxSize (wrapped).
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < 27; i++) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 25; i++) q.Dequeue();
        // head is now 25, tail is 27. Enqueue more so tail wraps past maxSize.
        q.Enqueue(new ComparableInt(100));
        q.Enqueue(new ComparableInt(101));
        q.Enqueue(new ComparableInt(102));
        q.Enqueue(new ComparableInt(103));

        // Logical order should still be: 25, 26, 100, 101, 102, 103
        int[] expected = { 25, 26, 100, 101, 102, 103 };
        Assert.Equal(expected.Length, q.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], q.ItemAt(i).Value);
        }
    }

    [Fact]
    public void ItemAtMatchesRunForEachOrder()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < 10; i++) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = 10; i < 15; i++) q.Enqueue(new ComparableInt(i));

        var viaRunForEach = new System.Collections.Generic.List<int>();
        q.RunForEach(item => viaRunForEach.Add(item.Value));

        var viaItemAt = new System.Collections.Generic.List<int>();
        for (int i = 0; i < q.Count; i++) viaItemAt.Add(q.ItemAt(i).Value);

        Assert.Equal(viaRunForEach, viaItemAt);
    }

    [Fact]
    public void RunForEachVisitsEveryItemWhenQueueIsExactlyFull()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < q.maxSize; i++) q.Enqueue(new ComparableInt(i));

        var visited = new System.Collections.Generic.List<int>();
        q.RunForEach(item => visited.Add(item.Value));

        Assert.Equal(q.maxSize, visited.Count);
        Assert.Equal(System.Linq.Enumerable.Range(0, q.maxSize), visited);
    }

    [Fact]
    public void RunForEachVisitsEveryItemWhenFullQueueWraps()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < q.maxSize; i++) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = 0; i < 5; i++) q.Enqueue(new ComparableInt(27 + i));

        var visited = new System.Collections.Generic.List<int>();
        q.RunForEach(item => visited.Add(item.Value));

        Assert.Equal(q.maxSize, visited.Count);
        Assert.Equal(System.Linq.Enumerable.Range(5, q.maxSize - 5).Concat(System.Linq.Enumerable.Range(27, 5)), visited);
    }

    [Fact]
    public void SortOrdersEveryItemWhenQueueIsExactlyFull()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = q.maxSize - 1; i >= 0; i--) q.Enqueue(new ComparableInt(i));

        q.Sort();

        for (int i = 0; i < q.maxSize; i++) Assert.Equal(i, q.Dequeue().Value);
    }

    [Fact]
    public void SortOrdersEveryItemWhenFullQueueWraps()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = q.maxSize - 1; i >= 0; i--) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = -5; i < 0; i++) q.Enqueue(new ComparableInt(i));

        q.Sort();

        for (int i = -5; i < q.maxSize - 5; i++) Assert.Equal(i, q.Dequeue().Value);
    }

    [Fact]
    public void SortClearsReferencesFromReusableBuffer()
    {
        var q = new InspectableQueue<MergeableInt>();
        for (int i = q.maxSize - 1; i >= 0; i--) q.Enqueue(new MergeableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = 0; i < 5; i++) q.Enqueue(new MergeableInt(27 + i));

        q.Sort();

        Assert.All(q.SortBuffer, item => Assert.Null(item));
    }

    [Fact]
    public void EnqueueOrMergeFindsExistingItemWhenQueueIsExactlyFull()
    {
        var q = new SortableQueue<MergeableInt>();
        for (int i = 0; i < q.maxSize; i++) q.Enqueue(new MergeableInt(i));
        var replacement = new MergeableInt(12);

        q.EnqueueOrMerge(replacement);

        Assert.Equal(q.maxSize, q.Count);
        Assert.Equal(1, q.ItemAt(12).MergeCount);
    }

    [Fact]
    public void EnqueueOrMergeFindsExistingItemWhenFullQueueWraps()
    {
        var q = new SortableQueue<MergeableInt>();
        for (int i = 0; i < q.maxSize; i++) q.Enqueue(new MergeableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = 27; i < 32; i++) q.Enqueue(new MergeableInt(i));
        var replacement = new MergeableInt(28);

        q.EnqueueOrMerge(replacement);

        Assert.Equal(q.maxSize, q.Count);
        Assert.Equal(1, q.ItemAt(23).MergeCount);
    }

    private sealed class ComparableInt(int value) : System.IComparable<ComparableInt>
    {
        public int Value { get; } = value;
        public int CompareTo(ComparableInt other) => Value.CompareTo(other.Value);
    }

    private sealed class MergeableInt(int value) : System.IComparable<MergeableInt>, IMergeable<MergeableInt>
    {
        public int Value { get; } = value;
        public int MergeCount { get; private set; }
        public int CompareTo(MergeableInt other) => Value.CompareTo(other.Value);
        public bool MergeIfEqual(MergeableInt target)
        {
            if (Value != target.Value) return false;
            MergeCount++;
            return true;
        }
    }

    private sealed class InspectableQueue<T> : SortableQueue<T> where T : System.IComparable<T>
    {
        public T[] SortBuffer => sortBuffer;
    }
}
