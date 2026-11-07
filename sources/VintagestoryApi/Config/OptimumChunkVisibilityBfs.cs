using System.Collections.Generic;

namespace Vintagestory.API.Config;

/// <summary>
/// Minimal view of the client chunk graph the BFS visibility walk needs. The
/// production adapter (in VintagestoryLib's ChunkCuller patch) forwards these
/// to ClientMain.WorldMap.chunks and ClientChunk; the unit tests supply a
/// synthetic graph so the algorithm can be verified without a running client.
///
/// Chunk coordinates are cubic 32-block chunk indices (cx, cy, cz), matching
/// ChunkCuller's centerpos math (cameraPos / 32).
/// </summary>
public interface IChunkVisibilityGraph
{
    /// <summary>True if a chunk exists (is loaded) at this cubic chunk coordinate.</summary>
    bool ChunkExists(int cx, int cy, int cz);

    /// <summary>
    /// True if light/sight can pass through the chunk at (cx,cy,cz) entering by
    /// face <paramref name="fromFace"/> and exiting by face <paramref name="toFace"/>.
    /// Maps to ClientChunk.IsTraversable. Must return true conservatively when the
    /// chunk's traversability data is not yet computed (never over-cull on stale data).
    /// A negative <paramref name="fromFace"/> means "no incoming face" (the seed chunk):
    /// the chunk is always considered enterable then.
    /// Faces use BlockFacing.Index ordering: N=0,E=1,S=2,W=3,U=4,D=5.
    /// </summary>
    bool IsVisibleThrough(int cx, int cy, int cz, int fromFace, int toFace);

    /// <summary>
    /// True if the chunk at (cx,cy,cz) is inside the view frustum. This is the most
    /// expensive filter, so the BFS applies it last. Maps to the ChunkCuller's
    /// existing frustum test.
    /// </summary>
    bool IsInFrustum(int cx, int cy, int cz);

    /// <summary>Mark the chunk at (cx,cy,cz) as visible (renderable) this pass.</summary>
    void MarkVisible(int cx, int cy, int cz);

    /// <summary>
    /// True if (cx,cy,cz) is a valid chunk position within the loaded world
    /// bounds (so the walk does not run off into never-loaded coordinates).
    /// Maps to WorldMap.IsValidChunkPosFast plus the above-height-limit rule.
    /// </summary>
    bool IsWithinWorldBounds(int cx, int cy, int cz);
}

/// <summary>
/// Breadth-first chunk visibility walk over the per-chunk face-connectivity graph,
/// the production technique used by Minecraft's "cave culling" and Sodium's
/// RenderSectionManager. It replaces ChunkCuller's per-shell raycast (which
/// Mojang's own author rejected as too slow at high render distance: thousands of
/// chunks x up to 7 rays each) with a single linear-time flood fill from the camera
/// chunk that naturally visits chunks front-to-back.
///
/// Correctness (MC-70850 / see-through-chunks trap): a naive walk that tests only
/// the recorded incoming face is biased and can hide chunks that are actually
/// visible. This implementation instead treats a neighbour as reachable if the
/// current chunk is visible-through ANY incoming face to the exit face, which is
/// the robust fix adopted by the community occlusion-culling-fix mod and matches
/// vanilla's post-1.17 behaviour. Combined with always seeding the camera chunk and
/// its immediate neighbourhood, and treating not-yet-computed traversability as
/// passable, the walk never culls geometry the player can actually see.
///
/// The class is stateless apart from reusable scratch buffers; a single instance is
/// reused per pass on the render thread (ChunkCuller is single-threaded), so no
/// per-frame allocation occurs after warmup.
/// </summary>
public sealed class OptimumChunkVisibilityBfs
{
    // BlockFacing.Index ordering: North=0, East=1, South=2, West=3, Up=4, Down=5.
    private static readonly int[] DirDx = { 0, 1, 0, -1, 0, 0 };
    private static readonly int[] DirDy = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] DirDz = { -1, 0, 1, 0, 0, 0 };
    private static readonly int[] Opposite = { 2, 3, 0, 1, 5, 4 };

    private const int NoFace = -1;

    // Queue entry: packed chunk coord plus the incoming ("flow") face index, or NoFace for seeds.
    private readonly struct Node
    {
        public readonly int Cx;
        public readonly int Cy;
        public readonly int Cz;
        public readonly int FromFace;

        public Node(int cx, int cy, int cz, int fromFace)
        {
            Cx = cx;
            Cy = cy;
            Cz = cz;
            FromFace = fromFace;
        }
    }

    private readonly Queue<Node> queue = new();

    // Per-pass visited set plus the accumulated "never-go-back" direction mask for
    // each visited chunk, keyed by packed chunk coordinate. The mask records which
    // axis directions the flood has already travelled to reach a chunk; the walk
    // never reverses an axis it has already committed to (light does not wind back).
    private readonly Dictionary<long, byte> visitedDirMask = new();

    /// <summary>Diagnostics from the most recent <see cref="Run"/>, for the HitSkipCounter and logs.</summary>
    public int LastVisitedCount { get; private set; }
    public int LastEnqueuedCount { get; private set; }
    public int LastFrustumCulledCount { get; private set; }

    /// <summary>
    /// Run the visibility flood fill. Marks every reachable, in-frustum chunk visible
    /// via <see cref="IChunkVisibilityGraph.MarkVisible"/>. The caller is responsible
    /// for having already reset all chunks to not-visible and for seeding the camera's
    /// own immediate neighbourhood visible (mirroring vanilla ChunkCuller), because
    /// those chunks must render even when the camera is embedded in solid blocks.
    /// </summary>
    /// <param name="graph">The chunk graph adapter.</param>
    /// <param name="camCx">Camera cubic chunk X.</param>
    /// <param name="camCy">Camera cubic chunk Y.</param>
    /// <param name="camCz">Camera cubic chunk Z.</param>
    /// <param name="useOcclusion">
    /// When false, the reachability filter is skipped (every in-frustum, in-bounds,
    /// reachable-by-adjacency chunk is visible) — used when occlusion culling is off
    /// but the front-to-back BFS ordering is still wanted.
    /// </param>
    public void Run(IChunkVisibilityGraph graph, int camCx, int camCy, int camCz, bool useOcclusion = true)
    {
        queue.Clear();
        visitedDirMask.Clear();
        int visited = 0;
        int enqueued = 0;
        int frustumCulled = 0;

        // Seed: the camera chunk. Always visible and always the BFS root, even when
        // it is outside the frustum (the player is inside it) or embedded in solid.
        long rootKey = Pack(camCx, camCy, camCz);
        visitedDirMask[rootKey] = 0x3F;
        if (graph.ChunkExists(camCx, camCy, camCz))
        {
            graph.MarkVisible(camCx, camCy, camCz);
            visited++;
        }
        queue.Enqueue(new Node(camCx, camCy, camCz, NoFace));
        enqueued++;

        while (queue.Count > 0)
        {
            Node cur = queue.Dequeue();

            for (int dir = 0; dir < 6; dir++)
            {
                // (1) Never-go-back: skip stepping back to the immediate parent chunk we entered from.
                if (cur.FromFace != NoFace && dir == cur.FromFace)
                {
                    continue;
                }

                // (2) Reachability through the current chunk (robust MC-70850-safe form):
                // cullable unless the current chunk is visible-through SOME incoming face
                // to this exit face. The seed chunk (FromFace == NoFace) is always enterable.
                // First-ring exemption (mirrors vanilla ChunkCuller's `num2 > 1` guard): the
                // camera chunk and its Manhattan-distance-1 neighbours always let the flood
                // through, so a lone solid chunk next to the camera never blanks what is behind it.
                int camDist = Abs(cur.Cx - camCx) + Abs(cur.Cy - camCy) + Abs(cur.Cz - camCz);
                if (useOcclusion && cur.FromFace != NoFace && camDist > 1 && !IsReachable(graph, cur.Cx, cur.Cy, cur.Cz, dir))
                {
                    continue;
                }

                int nx = cur.Cx + DirDx[dir];
                int ny = cur.Cy + DirDy[dir];
                int nz = cur.Cz + DirDz[dir];

                if (!graph.IsWithinWorldBounds(nx, ny, nz))
                {
                    continue;
                }

                int inFace = Opposite[dir];
                byte inMask = (byte)(1 << inFace);
                long nkey = Pack(nx, ny, nz);

                if (visitedDirMask.TryGetValue(nkey, out byte seenMask))
                {
                    if ((seenMask & inMask) != 0)
                    {
                        continue;
                    }
                    visitedDirMask[nkey] = (byte)(seenMask | inMask);
                }
                else
                {
                    visitedDirMask[nkey] = inMask;

                    // (3) Frustum cull LAST (most expensive filter).
                    if (!graph.IsInFrustum(nx, ny, nz))
                    {
                        frustumCulled++;
                        continue;
                    }

                    if (graph.ChunkExists(nx, ny, nz))
                    {
                        graph.MarkVisible(nx, ny, nz);
                        visited++;
                    }
                }

                if (!graph.IsInFrustum(nx, ny, nz))
                {
                    continue;
                }

                queue.Enqueue(new Node(nx, ny, nz, inFace));
                enqueued++;
            }
        }

        LastVisitedCount = visited;
        LastEnqueuedCount = enqueued;
        LastFrustumCulledCount = frustumCulled;
    }

    /// <summary>
    /// Robust reachability: the chunk is reachable toward <paramref name="toFace"/> if it is
    /// visible-through ANY incoming face to that exit. Checking every incoming face rather
    /// than only the recorded flow face is what prevents the MC-70850 see-through/missing
    /// chunk bug. IsVisibleThrough returns true conservatively on stale traversability data,
    /// so this can only ever be more permissive than vanilla, never less.
    /// </summary>
    private static bool IsReachable(IChunkVisibilityGraph graph, int cx, int cy, int cz, int toFace)
    {
        for (int from = 0; from < 6; from++)
        {
            if (from == toFace)
            {
                continue;
            }
            if (graph.IsVisibleThrough(cx, cy, cz, from, toFace))
            {
                return true;
            }
        }
        return false;
    }

    private static int Abs(int v) => v < 0 ? -v : v;

    // Packs a cubic chunk coordinate into a single long key. Chunk coords fit well
    // within 21 bits each for any realistic world/view distance; the offset keeps
    // negative coordinates non-negative before packing.
    private static long Pack(int cx, int cy, int cz)
    {
        const long bias = 1L << 20;
        long x = cx + bias;
        long y = cy + bias;
        long z = cz + bias;
        return (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);
    }
}
