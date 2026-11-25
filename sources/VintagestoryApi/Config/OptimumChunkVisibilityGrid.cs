using System;

namespace Vintagestory.API.Config;

/// <summary>
/// Flat-grid breadth-first chunk visibility walk for the client ChunkCuller.
///
/// Algorithm and layout after tehtelev's flat-grid BFS for ChunkCuller (entry-face
/// mask, monotonic traversal, one enqueue per cell, a camera-centred array instead
/// of a hash set). The caller (ChunkCuller's Optimum patch) fills <see cref="Cells"/>
/// under the world chunk lock via <see cref="SetCell"/>, then calls <see cref="Run"/>
/// outside the lock and marks the chunks listed via <see cref="ReachedCell"/> visible.
///
/// The class holds no reference to client types, so the shipped algorithm is the
/// one the unit tests exercise. It is allocated and resized only on the culling
/// thread at the start of a pass; callers pass the dimensions in, so a view
/// distance change can never tear a pass.
///
/// Cell encoding: bit 15 = a chunk is loaded at the cell; bits 0-14 = the vanilla
/// ClientChunk.traversability bits (see <see cref="PairBit"/>). A chunk whose
/// traversability data is not fresh, and an unloaded cell, are stored as
/// <see cref="AllPass"/> and never block the walk (the vanilla rule).
///
/// Faces use BlockFacing.Index order: N=0, E=1, S=2, W=3, U=4, D=5.
/// </summary>
public sealed class OptimumChunkVisibilityGrid
{
    /// <summary>Chunk Y stride per Vintage Story dimension (32,768 blocks / 32 blocks per chunk).</summary>
    public const int ChunksPerDimension = 1024;

    /// <summary>Cell flag: a chunk is loaded at this cell.</summary>
    public const ushort LoadedBit = 0x8000;

    /// <summary>All 15 face pairs traversable. Used for stale data (vanilla returns true).</summary>
    public const ushort AllPass = 0x7FFF;

    private const byte ReachedBit = 64;

    /// <summary>
    /// Minimum time between recalculations while the camera stays in one chunk. Terrain meshes arrive
    /// in bursts while streaming; 150 ms keeps a newly revealed chunk from waiting visibly and still
    /// merges a burst into one pass (constant from the original flat-grid proposal).
    /// </summary>
    public const long RecalcCooldownMs = 150;

    /// <summary>Cooldown while the regeneration queue is longer than <see cref="BigBacklogThreshold"/>: the view keeps changing under a long backlog.</summary>
    public const long BigBacklogCooldownMs = 1000;

    /// <summary>Regeneration queue length above which <see cref="BigBacklogCooldownMs"/> applies.</summary>
    public const int BigBacklogThreshold = 2000;

    /// <summary>Change of the regeneration queue length that counts as significant (vanilla's own threshold).</summary>
    public const int SignificantQueueDelta = 10;

    /// <summary>Bit index of the face pair (from, to) in a traversability mask, or -1 for from == to.</summary>
    internal static readonly int[] PairBit = new int[36];

    // [f * 64 + entries]: the OR of the bits of pairs (e, f) over the entry faces in `entries`.
    private static readonly ushort[] EntryMask = new ushort[6 * 64];

    private static readonly int[] FaceDx = { 0, 1, 0, -1, 0, 0 };
    private static readonly int[] FaceDy = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] FaceDz = { -1, 0, 1, 0, 0, 0 };
    private static readonly int[] FaceOpposite = { 2, 3, 0, 1, 5, 4 };

    static OptimumChunkVisibilityGrid()
    {
        for (int i = 0; i < 36; i++)
        {
            PairBit[i] = -1;
        }

        // Matches ClientChunk.traversabilityMapping in the 1.22.7 decompile.
        Set(0, 1, 0); // N-E
        Set(0, 3, 1); // N-W
        Set(0, 2, 2); // N-S
        Set(0, 4, 3); // N-U
        Set(0, 5, 4); // N-D
        Set(1, 2, 5); // E-S
        Set(1, 3, 6); // E-W
        Set(1, 4, 7); // E-U
        Set(1, 5, 8); // E-D
        Set(2, 3, 9); // S-W
        Set(2, 4, 10); // S-U
        Set(2, 5, 11); // S-D
        Set(3, 4, 12); // W-U
        Set(3, 5, 13); // W-D
        Set(4, 5, 14); // U-D

        for (int f = 0; f < 6; f++)
        {
            for (int entries = 0; entries < 64; entries++)
            {
                ushort m = 0;
                for (int e = 0; e < 6; e++)
                {
                    if ((entries & (1 << e)) != 0 && e != f)
                    {
                        m |= (ushort)(1 << PairBit[e * 6 + f]);
                    }
                }
                EntryMask[f * 64 + entries] = m;
            }
        }

        static void Set(int a, int b, int bit)
        {
            PairBit[a * 6 + b] = bit;
            PairBit[b * 6 + a] = bit;
        }
    }

    /// <summary>Horizontal grid radius in chunks. Grid width is 2 * RadiusXZ + 1.</summary>
    public int RadiusXZ { get; private set; }

    /// <summary>Vertical grid radius in chunks. Grid height is 2 * RadiusY + 1.</summary>
    public int RadiusY { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int CellCount { get; private set; }

    /// <summary>Per-cell traversability snapshot, filled by the caller. Index = (ly * Width + lz) * Width + lx.</summary>
    internal ushort[] Cells = Array.Empty<ushort>();

    /// <summary>Cells reached by the last <see cref="Run"/>, in BFS order; only the first <see cref="QueueLength"/> are valid.</summary>
    internal int[] Queue = Array.Empty<int>();

    public int QueueLength { get; private set; }

    private byte[] state = Array.Empty<byte>();

    /// <summary>
    /// Sizes the grid and clears the cell snapshot. Arrays only grow, so a steady view
    /// distance allocates nothing after the first pass. Call on the culling thread only.
    /// </summary>
    public void Prepare(int radiusXZ, int radiusY)
    {
        if (radiusXZ < 1)
        {
            radiusXZ = 1;
        }
        if (radiusY < 1)
        {
            radiusY = 1;
        }

        RadiusXZ = radiusXZ;
        RadiusY = radiusY;
        Width = 2 * radiusXZ + 1;
        Height = 2 * radiusY + 1;
        CellCount = Width * Width * Height;

        if (Cells.Length < CellCount)
        {
            Cells = new ushort[CellCount];
            state = new byte[CellCount];
            Queue = new int[CellCount];
        }
        else
        {
            Array.Clear(Cells, 0, CellCount);
        }
        QueueLength = 0;
    }

    public int IndexOf(int lx, int ly, int lz) => (ly * Width + lz) * Width + lx;

    /// <summary>Stores the encoded snapshot of one cell (see <see cref="EncodeCell"/>). The index must be inside the prepared grid.</summary>
    public void SetCell(int index, ushort encoded) => Cells[index] = encoded;

    /// <summary>The i-th cell reached by the last <see cref="Run"/>, as a grid index. i must be below <see cref="QueueLength"/>.</summary>
    public int ReachedCell(int i) => Queue[i];

    public static ushort EncodeCell(ushort traversability, bool fresh)
        => (ushort)(LoadedBit | (fresh ? (traversability & AllPass) : AllPass));

    /// <summary>
    /// Walks outward from the camera cell and records every reached cell in <see cref="Queue"/>.
    /// Uses monotonic traversal (never stepping against the camera offset on an axis with a
    /// non-zero offset), per-cell entry-face masks, and vanilla's camera-above-world bounds rule.
    /// </summary>
    /// <param name="camX">Camera chunk X (world chunk coordinates).</param>
    /// <param name="camY">Camera chunk Y.</param>
    /// <param name="camZ">Camera chunk Z.</param>
    /// <param name="mapSizeX">World size in chunks along X (valid chunk x is 0 .. mapSizeX - 1).</param>
    /// <param name="mapSizeY">World size in chunks along Y.</param>
    /// <param name="mapSizeZ">World size in chunks along Z.</param>
    /// <param name="useOcclusion">false: only adjacency and bounds limit the walk.</param>
    public void Run(int camX, int camY, int camZ, int mapSizeX, int mapSizeY, int mapSizeZ, bool useOcclusion)
    {
        if (CellCount == 0 || Cells.Length < CellCount || Queue.Length < CellCount)
        {
            // Run before Prepare: nothing to walk. The caller marks no chunk beyond its own seed.
            QueueLength = 0;
            return;
        }

        int w = Width;
        int h = Height;
        int r = RadiusXZ;
        int ry = RadiusY;
        ushort[] cells = Cells;
        byte[] st = state;
        int[] queue = Queue;
        bool aboveWorld = camY >= mapSizeY;

        Array.Clear(st, 0, CellCount);

        int cx = r;
        int cy = ry;
        int cz = r;
        int head = 0;
        int tail = 0;
        int start = (cy * w + cz) * w + cx;
        st[start] = ReachedBit;
        queue[tail++] = start;

        while (head < tail)
        {
            int idx = queue[head++];
            int lx = idx % w;
            int rest = idx / w;
            int lz = rest % w;
            int ly = rest / w;

            int ox = lx - cx;
            int oy = ly - cy;
            int oz = lz - cz;
            int dist = (ox < 0 ? -ox : ox) + (oy < 0 ? -oy : oy) + (oz < 0 ? -oz : oz);

            int cell = cells[idx];
            int trav = cell & AllPass;
            int entries = st[idx] & 63;
            // Vanilla: the traversal test applies to loaded chunks beyond the camera's
            // first ring; unloaded chunks never block.
            bool check = useOcclusion && dist > 1 && (cell & LoadedBit) != 0;

            for (int f = 0; f < 6; f++)
            {
                int fdx = FaceDx[f];
                int fdy = FaceDy[f];
                int fdz = FaceDz[f];

                if (fdx != 0 && ox != 0 && ((ox > 0) != (fdx > 0)))
                {
                    continue;
                }
                if (fdy != 0 && oy != 0 && ((oy > 0) != (fdy > 0)))
                {
                    continue;
                }
                if (fdz != 0 && oz != 0 && ((oz > 0) != (fdz > 0)))
                {
                    continue;
                }

                int nlx = lx + fdx;
                int nly = ly + fdy;
                int nlz = lz + fdz;
                if ((uint)nlx >= (uint)w || (uint)nlz >= (uint)w || (uint)nly >= (uint)h)
                {
                    continue;
                }

                int wx = camX + (nlx - cx);
                int wy = camY + (nly - cy);
                int wz = camZ + (nlz - cz);
                if (!(wx >= 0 && wy >= 0 && wz >= 0 && wx < mapSizeX && wy < mapSizeY && wz < mapSizeZ)
                    && !(aboveWorld && wy > 0))
                {
                    continue;
                }

                if (check && (trav & EntryMask[f * 64 + entries]) == 0)
                {
                    continue;
                }

                int nidx = (nly * w + nlz) * w + nlx;
                byte m = st[nidx];
                st[nidx] = (byte)(m | ReachedBit | (1 << FaceOpposite[f]));
                if ((m & ReachedBit) == 0)
                {
                    queue[tail++] = nidx;
                }
            }
        }

        QueueLength = tail;
    }

    /// <summary>The throttle with the named constants of this class.</summary>
    public static bool ShouldRecalculate(bool sameChunk, int regenCount, int lastRegenCount, ref bool pending,
        long nowMs, long lastRecalcMs)
        => ShouldRecalculate(sameChunk, regenCount, lastRegenCount, ref pending, nowMs, lastRecalcMs,
            RecalcCooldownMs, BigBacklogCooldownMs, BigBacklogThreshold, SignificantQueueDelta);

    /// <summary>
    /// Throttle for repeated recalculation while the camera stays in one chunk.
    /// The camera chunk changing always recalculates. Otherwise a significant change of
    /// the regeneration queue (at least <paramref name="significantDelta"/> entries, or the
    /// queue draining to zero) sets <paramref name="pending"/>, which stays set until a
    /// recalculation runs, and the recalculation waits for the cooldown.
    /// </summary>
    public static bool ShouldRecalculate(bool sameChunk, int regenCount, int lastRegenCount, ref bool pending,
        long nowMs, long lastRecalcMs, long cooldownMs, long bigBacklogCooldownMs, int bigBacklogThreshold,
        int significantDelta)
    {
        if (!sameChunk)
        {
            pending = false;
            return true;
        }

        int delta = regenCount - lastRegenCount;
        if (delta < 0)
        {
            delta = -delta;
        }
        if (delta >= significantDelta || (regenCount == 0 && lastRegenCount != 0))
        {
            pending = true;
        }

        long minInterval = regenCount > bigBacklogThreshold ? bigBacklogCooldownMs : cooldownMs;
        if (!pending || nowMs - lastRecalcMs < minInterval)
        {
            return false;
        }

        pending = false;
        return true;
    }

    /// <summary>
    /// Converts <c>EntityPlayer.CameraPos.Y</c> to the internal chunk Y coordinate for
    /// <paramref name="currentDimension"/>. <c>PlayerCamera</c> updates <c>CameraPos.Y</c> on the
    /// main thread in two writes (local Y, then <c>+= DimensionYAdjustment</c>) while the culler
    /// runs on its own thread; if the culler reads <c>CameraPos.Y</c> mid-update or right after a
    /// dimension change, this reconciles the dimension offset against <paramref name="currentDimension"/>.
    /// </summary>
    public static int ResolveCameraChunkY(double cameraY, int currentDimension)
    {
        if (currentDimension < 0)
        {
            currentDimension = 0;
        }
        int dimOffset = currentDimension * ChunksPerDimension;
        int camChunkY = (int)(cameraY / 32.0);
        if (currentDimension > 0 && camChunkY < dimOffset - (ChunksPerDimension / 2))
        {
            int localY = camChunkY >= 0 ? camChunkY % ChunksPerDimension : camChunkY;
            camChunkY = localY + dimOffset;
        }
        else if (camChunkY >= dimOffset + ChunksPerDimension)
        {
            camChunkY = (camChunkY % ChunksPerDimension) + dimOffset;
        }
        return camChunkY;
    }
}
