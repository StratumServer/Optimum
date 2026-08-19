using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public class OptimumTesselationSafetyGateTests
{
    [Fact]
    public void ForeignTextureSourceForcesSingleWorker()
    {
        var types = new[] { typeof(ForeignTextureSource) };

        List<string> foreignTypes = OptimumTesselationSafetyGate.FindForeignTextureSources(types);

        Assert.Contains(typeof(ForeignTextureSource).FullName!, foreignTypes);
        Assert.Equal(1, OptimumTesselationSafetyGate.CapWorkerCount(4, types));
    }

    [Fact]
    public void KnownApiTextureSourceKeepsRequestedWorkerCount()
    {
        var types = new[] { typeof(ContainedTextureSource) };

        Assert.Empty(OptimumTesselationSafetyGate.FindForeignTextureSources(types));
        Assert.Equal(4, OptimumTesselationSafetyGate.CapWorkerCount(4, types));
    }

    private sealed class ForeignTextureSource : ITexPositionSource
    {
        public TextureAtlasPosition? this[string textureCode] => null;

        public Size2i? AtlasSize => null;
    }
}
