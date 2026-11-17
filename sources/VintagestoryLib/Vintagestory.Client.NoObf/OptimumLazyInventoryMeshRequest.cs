using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Vintagestory.Client.NoObf;

/// <summary>
/// Lambda-free main-thread marshal request for off-thread callers of
/// GetDefaultBlockMeshRef / GetDefaultItemMeshRef (for example BEGroundStorage.OnTesselation
/// when tessellating glowing ground storage on a chunk worker thread).
/// </summary>
internal sealed class OptimumLazyInventoryMeshRequest
{
    private readonly ShapeTesselatorManager manager;
    private readonly Block block;
    private readonly Item item;
    private readonly int renderVariant;
    private readonly ManualResetEventSlim completed = new ManualResetEventSlim(false);
    private MultiTextureMeshRef result;

    internal OptimumLazyInventoryMeshRequest(ShapeTesselatorManager manager, Block block)
    {
        this.manager = manager;
        this.block = block;
    }

    internal OptimumLazyInventoryMeshRequest(ShapeTesselatorManager manager, Item item, int renderVariant)
    {
        this.manager = manager;
        this.item = item;
        this.renderVariant = renderVariant;
    }

    internal void ExecuteOnMainThread()
    {
        try
        {
            result = block != null
                ? manager.EnsureBlockInventoryMeshRefOnMainThread(block)
                : manager.EnsureItemInventoryMeshRefOnMainThread(item, renderVariant);
        }
        finally
        {
            completed.Set();
        }
    }

    internal MultiTextureMeshRef Wait(ClientMain game)
    {
        try
        {
            while (!completed.Wait(20))
            {
                if (game.disposed || game.threadsShouldExit)
                {
                    return null;
                }
            }
            return result;
        }
        finally
        {
            completed.Dispose();
        }
    }
}
