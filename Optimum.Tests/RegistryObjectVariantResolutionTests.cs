using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;
using Vintagestory.API.Common;
using VsOrderedDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, string>;
using Vintagestory.ServerMods.NoObf;

namespace Optimum.Tests;

public class RegistryObjectVariantResolutionTests
{
    private static string VanillaFillPlaceHolder(string input, string search, string replace)
    {
        string pattern = @"\{((" + search + @")|([^\{\}]*\|" + search + @")|(" + search + @"\|[^\{\}]*)|([^\{\}]*\|" + search + @"\|[^\{\}]*))\}";
        return Regex.Replace(input, pattern, replace);
    }

    private static string VanillaFillPlaceHolderDict(string input, VsOrderedDictionary searchReplace)
    {
        foreach (var val in searchReplace)
        {
            input = VanillaFillPlaceHolder(input, val.Key, val.Value);
        }
        return input;
    }

    [Theory]
    [InlineData("rock-{rock}", "rock", "granite")]
    [InlineData("block-{rock|soil}", "rock", "granite")]
    [InlineData("block-{soil|rock}", "rock", "granite")]
    [InlineData("block-{soil|rock|sand}", "rock", "granite")]
    [InlineData("block-{rocky}", "rock", "granite")]
    [InlineData("double-{rock}-{rock}", "rock", "granite")]
    [InlineData("mixed-{rock}-{wood}", "rock", "granite")]
    [InlineData("unclosed-{rock", "rock", "granite")]
    [InlineData("no-placeholder", "rock", "granite")]
    [InlineData("", "rock", "granite")]
    public void FillPlaceHolder_Single_MatchesVanillaRegex(string input, string search, string replace)
    {
        string vanilla = VanillaFillPlaceHolder(input, search, replace);
        string optimized = RegistryObject.FillPlaceHolder(input, search, replace);

        Assert.Equal(vanilla, optimized);
    }

    [Fact]
    public void FillPlaceHolder_Dictionary_MatchesVanillaRegex()
    {
        var dict = new VsOrderedDictionary
        {
            ["rock"] = "granite",
            ["type"] = "rough",
            ["color"] = "red"
        };

        string[] inputs = new[]
        {
            "block-{rock}-{type}",
            "item-{rock|wood}-{type}-{color}",
            "pure-static-name",
            "{color|blue}-{rock}",
            "unmatched-{other}",
            "{rock}-{rock}-{rock}"
        };

        foreach (string input in inputs)
        {
            string vanilla = VanillaFillPlaceHolderDict(input, dict);
            string optimized = RegistryObject.FillPlaceHolder(input, dict);

            Assert.Equal(vanilla, optimized);
        }
    }

    [Fact]
    public void FillPlaceHolder_AssetLocation_ReplacesPathCorrectly()
    {
        var dict = new VsOrderedDictionary
        {
            ["rock"] = "granite",
            ["state"] = "carved"
        };

        var loc = new AssetLocation("survival", "stone-{rock}-{state}");
        RegistryObject.FillPlaceHolder(loc, dict);

        Assert.Equal("survival:stone-granite-carved", loc.ToString());
    }

    [Fact]
    public void RegistryObjectType_MarkDynamicTokens_DistinguishesStaticAndDynamicSubtrees()
    {
        string sampleJson = @"
        {
            ""code"": ""rock-{rock}"",
            ""class"": ""Block"",
            ""sounds"": {
                ""walk"": ""walk/stone"",
                ""byType"": {
                    ""rock-granite"": { ""walk"": ""walk/stone-hard"" },
                    ""*"": { ""walk"": ""walk/stone-default"" }
                }
            },
            ""textures"": {
                ""all"": { ""base"": ""block/stone/{rock}"" }
            },
            ""staticProps"": {
                ""hardness"": 3.5,
                ""blastResistance"": 10
            }
        }";

        var baseObj = JObject.Parse(sampleJson);
        var dynamicSet = RegistryObjectType.GetOrCreateDynamicSet();
        dynamicSet.Clear();

        try
        {
            RegistryObjectType.MarkDynamicTokens(baseObj, dynamicSet);

            Assert.Contains(baseObj, dynamicSet);
            Assert.Contains(baseObj["sounds"], dynamicSet);
            Assert.Contains(baseObj["textures"], dynamicSet);
            Assert.DoesNotContain(baseObj["staticProps"], dynamicSet);
        }
        finally
        {
            dynamicSet.Clear();
        }
    }

    [Fact]
    public void RegistryObjectType_Resolve_ProducesExactParityAndPreservesMerge()
    {
        string sampleJson = @"
        {
            ""code"": ""rock-{rock}"",
            ""class"": ""Block"",
            ""sounds"": {
                ""walk"": ""walk/stone"",
                ""place"": ""place/stone""
            },
            ""soundsByType"": {
                ""rock-granite"": { ""walk"": ""walk/stone-hard"" },
                ""*"": { ""walk"": ""walk/stone-default"" }
            },
            ""textures"": {
                ""all"": { ""base"": ""block/stone/{rock}"" }
            },
            ""staticProps"": {
                ""hardness"": 3.5
            }
        }";

        var baseObj = JObject.Parse(sampleJson);
        var dict = new VsOrderedDictionary { ["rock"] = "granite" };

        var dynamicSet = RegistryObjectType.GetOrCreateDynamicSet();
        dynamicSet.Clear();

        try
        {
            RegistryObjectType.MarkDynamicTokens(baseObj, dynamicSet);
            var resolved = (JObject)RegistryObjectType.Resolve(baseObj, "rock-granite", dict, dynamicSet);

            Assert.NotNull(resolved);
            Assert.Equal("walk/stone-hard", (string)resolved["sounds"]["walk"]);
            Assert.Equal("place/stone", (string)resolved["sounds"]["place"]);
            Assert.Null(resolved["soundsByType"]);
            Assert.Equal("block/stone/granite", (string)resolved["textures"]["all"]["base"]);
            Assert.Equal(3.5, (double)resolved["staticProps"]["hardness"]);
        }
        finally
        {
            dynamicSet.Clear();
        }
    }

    [Fact]
    public void GatherVariantsAndPopulate_DoesNotContainDeepClone()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../VSEssentials/Loading/RegistryObjectTypeLoader.cs");
        if (!File.Exists(path))
        {
            path = Path.GetFullPath("VSEssentials/Loading/RegistryObjectTypeLoader.cs");
        }

        if (File.Exists(path))
        {
            string source = File.ReadAllText(path);
            int methodIndex = source.IndexOf("void GatherVariantsAndPopulate");
            Assert.True(methodIndex >= 0);
            int methodLength = Math.Min(3000, source.Length - methodIndex);
            string methodBody = source.Substring(methodIndex, methodLength);
            Assert.DoesNotContain("DeepClone()", methodBody);
            Assert.Contains("RegistryObjectType.Resolve", methodBody);
        }
    }
}
