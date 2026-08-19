using Xunit;

namespace Optimum.Tests;

public sealed class TextureAtlasConcurrencyTests
{
    private const string Patch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/TextureAtlasManager.cs.patch";

    [Fact]
    public void AtlasOverflowUsesWorkerMembershipAndAnAtomicOneShotGate()
    {
        string source = PatchReader.ReadPatch(Patch);

        Assert.Contains("game.IsTesselationThread(Environment.CurrentManagedThreadId)", source);
        Assert.Contains("Interlocked.CompareExchange(ref atlasCreationQueued, 1, 0) == 0", source);
        Assert.Contains("Interlocked.Exchange(ref atlasCreationQueued, 0)", source);
    }
}
