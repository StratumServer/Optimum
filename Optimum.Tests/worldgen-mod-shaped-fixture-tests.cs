using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class WorldgenModShapedFixtureTests
{
    [Fact]
    public async Task SharedInstanceStateChangesTheFixedSeedHashWhenCapExceedsOne()
    {
        int[] columns = CreateColumns(24);
        var serial = new ModShapedGenerator(42424242);
        int[] serialResults = serial.GenerateSerial(columns);
        int[] repeatedSerialResults = serial.GenerateSerial(columns);

        Assert.Equal(Hash(serialResults), Hash(repeatedSerialResults));

        var parallel = new ModShapedGenerator(42424242);
        int[] parallelResults = await parallel.GenerateParallel(columns, 3);

        Assert.NotEqual(Hash(serialResults), Hash(parallelResults));
    }

    [Fact]
    public async Task ThreadLocalInstanceStateRestoresTheFixedSeedHash()
    {
        int[] columns = CreateColumns(24);
        var serial = new IsolatedModShapedGenerator(42424242);
        int serialHash = Hash(serial.GenerateSerial(columns));

        var parallel = new IsolatedModShapedGenerator(42424242);
        int parallelHash = Hash(await parallel.GenerateParallel(columns, 3));

        Assert.Equal(serialHash, parallelHash);
    }

    [Fact]
    public void ModShapedFixtureAssemblyForcesTheForeignAssemblyGate()
    {
        string assemblyName = typeof(ModShapedGenerator).Assembly.GetName().Name!;

        Assert.False(OptimumWorldgenSafetyGate.IsKnownSafeAssembly(assemblyName));
    }

    private static int[] CreateColumns(int count)
    {
        var columns = new int[count];
        for (int i = 0; i < columns.Length; i++) columns[i] = i - 12;
        return columns;
    }

    private static int Hash(IReadOnlyList<int> values)
    {
        uint hash = 2166136261;
        for (int i = 0; i < values.Count; i++)
        {
            hash ^= (uint)values[i];
            hash *= 16777619;
        }
        return unchecked((int)hash);
    }

    private sealed class ModShapedGenerator
    {
        private readonly int seed;
        private readonly int[] columnResults = new int[1];
        private readonly Dictionary<int, int> landformCache = new();
        private readonly object cacheLock = new();

        public ModShapedGenerator(int seed)
        {
            this.seed = seed;
        }

        public int[] GenerateSerial(IReadOnlyList<int> columns)
        {
            var results = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++) results[i] = Generate(columns[i], null);
            return results;
        }

        public async Task<int[]> GenerateParallel(IReadOnlyList<int> columns, int workerCount)
        {
            var results = new int[columns.Count];
            for (int start = 0; start < columns.Count; start += workerCount)
            {
                int count = Math.Min(workerCount, columns.Count - start);
                using var barrier = new Barrier(count);
                var tasks = new Task[count];
                for (int offset = 0; offset < count; offset++)
                {
                    int index = start + offset;
                    tasks[offset] = Task.Run(() => results[index] = Generate(columns[index], barrier));
                }
                await Task.WhenAll(tasks);
            }
            return results;
        }

        private int Generate(int column, Barrier? barrier)
        {
            columnResults[0] = Mix(seed, column);
            barrier?.SignalAndWait();
            lock (cacheLock)
            {
                landformCache[column] = columnResults[0];
                return landformCache[column];
            }
        }
    }

    private sealed class IsolatedModShapedGenerator
    {
        private sealed class Workspace
        {
            public readonly int[] ColumnResults = new int[1];
            public readonly Dictionary<int, int> LandformCache = new();
        }

        private readonly int seed;
        private readonly ThreadLocal<Workspace> workspaces = new(() => new Workspace());

        public IsolatedModShapedGenerator(int seed)
        {
            this.seed = seed;
        }

        public int[] GenerateSerial(IReadOnlyList<int> columns)
        {
            var results = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++) results[i] = Generate(columns[i]);
            return results;
        }

        public async Task<int[]> GenerateParallel(IReadOnlyList<int> columns, int workerCount)
        {
            var results = new int[columns.Count];
            var tasks = new Task[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() => results[index] = Generate(columns[index]));
            }
            await Task.WhenAll(tasks);
            return results;
        }

        private int Generate(int column)
        {
            Workspace workspace = workspaces.Value!;
            workspace.ColumnResults[0] = Mix(seed, column);
            workspace.LandformCache[column] = workspace.ColumnResults[0];
            return workspace.LandformCache[column];
        }
    }

    private static int Mix(int seed, int column)
    {
        unchecked
        {
            uint value = (uint)(seed + column * 0x45d9f3b);
            value ^= value >> 16;
            value *= 0x45d9f3b;
            value ^= value >> 16;
            return (int)value;
        }
    }
}
