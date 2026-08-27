using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Threading;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace Vintagestory.Server;

/// <summary>
/// Optimum parallel chunk-read pool. Holds N read-only SQLite connections against the savegame
/// so chunk column loading can fan reads out via Parallel.For. SQLite WAL mode allows many
/// concurrent readers alongside the single writer connection in GameDatabase.
///
/// Each slot has its own connection + prepared SELECT command. Slots are leased via
/// SemaphoreSlim + ConcurrentBag so callers never share a connection.
/// </summary>
internal sealed class OptimumChunkReadPool : IDisposable
{
    private readonly SqliteConnection[] connections;
    private readonly SqliteCommand[] getChunkCmds;
    private readonly SemaphoreSlim available;
    private readonly ConcurrentBag<int> freeSlots;
    private readonly object disposeLock = new object();
    private bool disposed;

    public int WorkerCount => connections.Length;
    public bool IsOpen => !disposed && connections.Length > 0;

    public OptimumChunkReadPool(string filename, int workers, bool corruptionProtection)
        : this(filename, workers, corruptionProtection, null)
    {
    }

    internal OptimumChunkReadPool(
        string filename,
        int workers,
        bool corruptionProtection,
        Action<SqliteConnection>? afterConnectionOpened)
    {
        workers = Math.Max(1, Math.Min(8, workers));
        connections = new SqliteConnection[workers];
        getChunkCmds = new SqliteCommand[workers];
        freeSlots = new ConcurrentBag<int>();
        available = new SemaphoreSlim(workers, workers);

        try
        {
            for (int i = 0; i < workers; i++)
            {
                DbConnectionStringBuilder conf = new DbConnectionStringBuilder
                {
                    { "Data Source", filename },
                    { "Pooling", "false" },
                    { "Mode", "ReadOnly" },
                };
                SqliteConnection conn = new SqliteConnection(conf.ToString());
                connections[i] = conn;
                conn.Open();
                afterConnectionOpened?.Invoke(conn);

                using (SqliteCommand pragma = conn.CreateCommand())
                {
                    pragma.CommandTimeout = 1;
                    pragma.CommandText = corruptionProtection
                        ? "PRAGMA journal_mode=WAL;PRAGMA synchronous=Normal;PRAGMA query_only=ON;"
                        : "PRAGMA query_only=ON;";
                    pragma.ExecuteNonQuery();
                }

                SqliteCommand cmd = conn.CreateCommand();
                getChunkCmds[i] = cmd;
                cmd.CommandText = "SELECT data FROM chunk WHERE position=@position";
                SqliteParameter p = cmd.CreateParameter();
                p.ParameterName = "position";
                p.DbType = DbType.UInt64;
                p.Value = 0UL;
                cmd.Parameters.Add(p);
                cmd.Prepare();

                freeSlots.Add(i);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read a single chunk's raw bytes from the database. Returns null if the chunk doesn't exist.
    /// Thread-safe: multiple callers can call this concurrently up to WorkerCount.
    /// </summary>
    public byte[] GetChunkBytes(ulong position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!available.Wait(15000))
        {
            throw new TimeoutException("[Optimum] chunk read pool: no free connection within 15s");
        }
        int slot = -1;
        try
        {
            if (!freeSlots.TryTake(out slot))
            {
                throw new InvalidOperationException("[Optimum] chunk read pool: semaphore/bag mismatch");
            }
            ObjectDisposedException.ThrowIf(disposed, this);

            SqliteCommand cmd = getChunkCmds[slot];
            cmd.Parameters["position"].Value = position;
            using (DbDataReader reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    return reader["data"] as byte[];
                }
            }
            return null;
        }
        catch (SqliteException ex)
        {
            ServerMain.Logger?.Error("[Optimum] chunk read pool slot {0} failed: {1}", slot, ex.Message);
            throw;
        }
        finally
        {
            if (slot >= 0) freeSlots.Add(slot);
            available.Release();
        }
    }

    public void Dispose()
    {
        lock (disposeLock)
        {
            if (disposed) return;
            disposed = true;
        }
        for (int i = 0; i < connections.Length; i++)
        {
            available.Wait();
        }
        for (int i = 0; i < connections.Length; i++)
        {
            try { getChunkCmds[i]?.Dispose(); } catch { }
            try { connections[i]?.Close(); } catch { }
            try { connections[i]?.Dispose(); } catch { }
            getChunkCmds[i] = null;
            connections[i] = null;
        }
        available.Dispose();
    }
}
