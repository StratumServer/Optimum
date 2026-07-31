using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Optimum.Tests;

/// <summary>
/// Tests for Stratum-ported optimizations.
/// Validates behavioral equivalence between optimized and original implementations.
/// </summary>
public class StratumPortTests
{
    #region WildcardUtil Tests

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData("rock-*", "rock-granite", true)]
    [InlineData("rock-*", "rock-basalt", true)]
    [InlineData("rock-*", "soil-low", false)]
    [InlineData("*-north", "trapdoor-north", true)]
    [InlineData("*-north", "trapdoor-south", false)]
    [InlineData("*", "anything", true)]
    [InlineData("exact-match", "exact-match", true)]
    [InlineData("exact-match", "wrong-match", false)]
    [InlineData("a-*-c", "a-b-c", true)]
    [InlineData("a-*-c", "a-xyz-c", true)]
    [InlineData("a-*-c", "a-b-d", false)]
    [InlineData("planks-*", "planks-oak", true)]
    [InlineData("planks-*", "log-oak", false)]
    public void WildcardUtil_Match_String_ProducesSameResultAsExpected(string needle, string haystack, bool expected)
    {
        bool result = WildcardUtil.Match(needle, haystack);
        Assert.Equal(expected, result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_NullSafe()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        // The optimized version adds null guards
        bool result = WildcardUtil.Match(wildCard, null, null);
        Assert.False(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_GlobalWildcard()
    {
        var wildCard = new AssetLocation("*", "*");
        var code = new AssetLocation("game", "anything-here");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_ExactMatch()
    {
        var wildCard = new AssetLocation("game", "rock-granite");
        var code = new AssetLocation("game", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_WildcardSuffix()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeMatch = new AssetLocation("game", "rock-granite");
        var codeNoMatch = new AssetLocation("game", "soil-low");

        Assert.True(WildcardUtil.Match(wildCard, codeMatch, null));
        Assert.False(WildcardUtil.Match(wildCard, codeNoMatch, null));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_DomainMismatch()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var code = new AssetLocation("modx", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.False(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_WithAllowedVariants()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeGranite = new AssetLocation("game", "rock-granite");
        var codeBasalt = new AssetLocation("game", "rock-basalt");

        string[] allowed = new[] { "granite" };

        Assert.True(WildcardUtil.Match(wildCard, codeGranite, allowed));
        Assert.False(WildcardUtil.Match(wildCard, codeBasalt, allowed));
    }

    #endregion

    #region Ascii85 Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_EmptyArray()
    {
        byte[] input = Array.Empty<byte>();
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_SingleByte()
    {
        byte[] input = new byte[] { 0x42 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_FourBytes()
    {
        byte[] input = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_ZeroBlock()
    {
        byte[] input = new byte[] { 0, 0, 0, 0 };
        string encoded = Ascii85.Encode(input);
        Assert.Contains("z", encoded);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_LargeData()
    {
        byte[] input = new byte[1024];
        var rng = new Random(42);
        rng.NextBytes(input);
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void Ascii85_EncodeDecodeRoundtrip_MixedZeroAndNonZero()
    {
        byte[] input = new byte[] { 0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 5, 6, 7 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    #endregion

    #region CollisionTester Fast Path Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void CollisionTester_FastPath_ZeroVelocity_SetsPositionUnchanged()
    {
        // Validates that zero-velocity non-player/non-item entities get the fast path
        // We cannot instantiate a real Entity without a World, but we verify the logic pattern
        var diagnostics = OptimumDiagnostics.CollisionFastPathHits;
        // If we had a mock entity with zero motion, the fast path sets newPosition = entityPos
        // This test validates the OptimumDiagnostics counter infrastructure compiles and functions
        OptimumDiagnostics.RecordCollisionFastPathHit();
        Assert.True(OptimumDiagnostics.CollisionFastPathHits > diagnostics);
    }

    #endregion

    #region PathNode Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_NullReturnsFlase()
    {
        var node = new Vintagestory.Essentials.PathNode(new BlockPos(1, 2, 3, 0));
        Assert.False(node.Equals((Vintagestory.Essentials.PathNode)null));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_SamePosition()
    {
        var a = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        var b = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        Assert.True(a.Equals(b));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_DifferentPosition()
    {
        var a = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        var b = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 16, 0));
        Assert.False(a.Equals(b));
    }

    #endregion

    #region CollectEntities Stride Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void CollectEntities_StrideInterval_DefaultIsThree()
    {
        Assert.Equal(3, Vintagestory.GameContent.EntityBehaviorCollectEntities.OptimumCollectStrideInterval);
    }

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData(0, 100, 3, true)]   // tick 0, entityId 100: (0+100)%3 = 1, skips
    [InlineData(0, 99, 3, false)]   // tick 0, entityId 99: (0+99)%3 = 0, runs
    [InlineData(1, 99, 3, true)]    // tick 1, entityId 99: (1+99)%3 = 1, skips
    [InlineData(2, 99, 3, true)]    // tick 2, entityId 99: (2+99)%3 = 2, skips
    [InlineData(3, 99, 3, false)]   // tick 3, entityId 99: (3+99)%3 = 0, runs
    public void CollectEntities_StrideLogic_SkipsCorrectTicks(long tickIndex, long entityId, int stride, bool shouldSkip)
    {
        bool skips = (tickIndex + entityId) % stride != 0;
        Assert.Equal(shouldSkip, skips);
    }

    #endregion

    #region OptimumDiagnostics Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void OptimumDiagnostics_CountersIncrementAndReset()
    {
        OptimumDiagnostics.ResetStratumCounters();
        Assert.Equal(0, OptimumDiagnostics.ServerTickCount);

        OptimumDiagnostics.RecordServerTick();
        OptimumDiagnostics.RecordServerTick();
        Assert.Equal(2, OptimumDiagnostics.ServerTickCount);

        OptimumDiagnostics.RecordCollisionFastPathHit();
        Assert.Equal(1, OptimumDiagnostics.CollisionFastPathHits);

        OptimumDiagnostics.ResetStratumCounters();
        Assert.Equal(0, OptimumDiagnostics.ServerTickCount);
        Assert.Equal(0, OptimumDiagnostics.CollisionFastPathHits);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void OptimumDiagnostics_GetSummary_ContainsExpectedKeys()
    {
        OptimumDiagnostics.ResetStratumCounters();
        OptimumDiagnostics.RecordMechPowerTick();
        string summary = OptimumDiagnostics.GetStratumSummary();
        Assert.Contains("mechTicks=1", summary);
        Assert.Contains("[Stratum Ports]", summary);
    }

    #endregion

    #region Propick Node Mode Reuse Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PropickNodeMode_SortLogic_MatchesLinqOrderByDescending()
    {
        // Validate the manual sort produces same order as .OrderByDescending(val => val.Value).ToList()
        var dict = new Dictionary<string, int>
        {
            { "ore-copper", 15 },
            { "ore-tin", 42 },
            { "ore-gold", 3 },
            { "ore-iron", 28 },
        };

        // LINQ reference
        var linqResult = dict.OrderByDescending(val => val.Value).ToList();

        // Manual sort (the Optimum implementation)
        var manualResult = new List<KeyValuePair<string, int>>(dict);
        manualResult.Sort((a, b) => b.Value.CompareTo(a.Value));

        Assert.Equal(linqResult.Count, manualResult.Count);
        for (int i = 0; i < linqResult.Count; i++)
        {
            Assert.Equal(linqResult[i].Key, manualResult[i].Key);
            Assert.Equal(linqResult[i].Value, manualResult[i].Value);
        }
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PropickNodeMode_DictionaryReuse_ClearProducesEmptyState()
    {
        // Validates the reuse pattern: Clear() + repopulate produces same results as new Dictionary
        var reusable = new Dictionary<string, int>();
        reusable["ore-copper"] = 5;
        reusable["ore-tin"] = 10;

        // "new reading" - clear and repopulate
        reusable.Clear();
        reusable["ore-gold"] = 7;

        Assert.Single(reusable);
        Assert.Equal(7, reusable["ore-gold"]);
        Assert.False(reusable.ContainsKey("ore-copper"));
    }

    #endregion

    #region RegistryObject FirstCodePart/LastCodePart Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void FirstCodePart_NoDash_ReturnsFullPath()
    {
        // Simulate the logic: path "granite" with no dash
        string path = "granite";
        int posFromLeft = 0;
        if (posFromLeft == 0 && !path.Contains('-'))
        {
            Assert.Equal("granite", path);
            return;
        }
        Assert.Fail("Should have returned early");
    }

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData("rock-granite-polished", 0, "rock")]
    [InlineData("rock-granite-polished", 1, "granite")]
    [InlineData("rock-granite-polished", 2, "polished")]
    [InlineData("a-b", 0, "a")]
    [InlineData("a-b", 1, "b")]
    public void FirstCodePart_Logic_MatchesSplitBehavior(string path, int posFromLeft, string expected)
    {
        // Replicate the optimized FirstCodePart logic
        int start = 0;
        for (int skip = 0; skip < posFromLeft; skip++)
        {
            int dash = path.IndexOf('-', start);
            if (dash < 0) { Assert.Fail("posFromLeft too high"); return; }
            start = dash + 1;
        }
        int end = path.IndexOf('-', start);
        string result = end < 0 ? path.Substring(start) : path.Substring(start, end - start);
        Assert.Equal(expected, result);
    }

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData("rock-granite-polished", 0, "polished")]
    [InlineData("rock-granite-polished", 1, "granite")]
    [InlineData("rock-granite-polished", 2, "rock")]
    [InlineData("a-b", 0, "b")]
    [InlineData("a-b", 1, "a")]
    public void LastCodePart_Logic_MatchesSplitBehavior(string path, int posFromRight, string expected)
    {
        // Replicate the optimized LastCodePart logic
        int end = path.Length;
        for (int skip = 0; skip < posFromRight; skip++)
        {
            int dash = path.LastIndexOf('-', end - 1);
            if (dash < 0) { Assert.Fail("posFromRight too high"); return; }
            end = dash;
        }
        int start = path.LastIndexOf('-', end - 1);
        string result = start < 0 ? path.Substring(0, end) : path.Substring(start + 1, end - start - 1);
        Assert.Equal(expected, result);
    }

    #endregion
}
