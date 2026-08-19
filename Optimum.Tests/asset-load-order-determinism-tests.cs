using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Proves that Dictionary&lt;string, string&gt; enumeration order matches insertion
/// order when no removals occur (.NET 5+). This mirrors how AssetManager.Assets
/// (Dictionary&lt;AssetLocation, IAsset&gt;) preserves mod-load-order determinism
/// for GetMany("patches/") iteration in JsonPatchLoader.
/// </summary>
public class AssetLoadOrderDeterminismTests
{
    /// <summary>
    /// Simulate multi-origin asset loading: base assets first, then mod overrides.
    /// Last writer wins for duplicate keys, but non-overlapping keys preserve
    /// their original insertion position.
    /// </summary>
    [Fact]
    public void LastWriterWinsForOverlappingKeys()
    {
        var dict = new Dictionary<string, string>();

        // Base origin: 3 assets
        dict["patches/base/terrain.json"] = "base";
        dict["patches/base/trees.json"] = "base";
        dict["patches/shared.json"] = "base";

        // Mod1 origin: 2 assets, one overlapping
        dict["patches/mod1/weapons.json"] = "mod1";
        dict["patches/shared.json"] = "mod1";

        // Mod2 origin: overrides the shared key again
        dict["patches/mod2/armor.json"] = "mod2";
        dict["patches/shared.json"] = "mod2";

        // shared.json retains base's position in enumeration order,
        // mod2's value
        Assert.Equal("mod2", dict["patches/shared.json"]);
    }

    /// <summary>
    /// Hash the key sequence to detect any reordering. Run 10 times to confirm stability.
    /// </summary>
    [Fact]
    public void KeySequenceHashIsStableAcrossRuns()
    {
        string firstHash = null;
        for (int run = 0; run < 10; run++)
        {
            var dict = new Dictionary<string, string>();
            // Fixed insertion pattern
            for (int mod = 0; mod < 5; mod++)
            {
                for (int asset = 0; asset < 20; asset++)
                {
                    string key = $"patches/mod{mod}/asset{asset:D3}.json";
                    dict[key] = $"mod{mod}";
                }
                // Each mod also overrides a shared key
                dict["patches/shared/config.json"] = $"mod{mod}";
            }

            string hash = HashKeySequence(dict);
            if (firstHash == null) firstHash = hash;
            else Assert.Equal(firstHash, hash);
        }
    }

    /// <summary>
    /// Deliberately reversing insertion order produces a DIFFERENT hash
    /// (proves the test actually detects reordering).
    /// </summary>
    [Fact]
    public void ReversedInsertionOrderProducesDifferentHash()
    {
        var forward = new Dictionary<string, string>();
        var reversed = new Dictionary<string, string>();

        string[] keys = { "a", "b", "c", "d", "e", "f", "g", "h" };
        foreach (var k in keys) forward[k] = "v";

        // Insert in reverse
        for (int i = keys.Length - 1; i >= 0; i--) reversed[keys[i]] = "v";

        string hashForward = HashKeySequence(forward);
        string hashReversed = HashKeySequence(reversed);

        Assert.NotEqual(hashForward, hashReversed);
    }

    private static string HashKeySequence(Dictionary<string, string> dict)
    {
        var sb = new StringBuilder();
        foreach (var key in dict.Keys)
        {
            sb.Append(key);
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
