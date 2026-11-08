using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class AssetLocationTests
{
    [Fact]
    public void DefaultDomain_ResolvesNullDomainAndInternedPath()
    {
        var loc = new AssetLocation("rock-granite");
        Assert.Equal("game", loc.Domain);
        Assert.Equal("rock-granite", loc.Path);
        Assert.True(string.IsInterned(loc.Path) != null);
    }

    [Theory]
    [InlineData("game:stone-andesite", "game", "stone-andesite")]
    [InlineData("survival:bread", "survival", "bread")]
    [InlineData("creative:creative-wrench", "creative", "creative-wrench")]
    [InlineData("xskills:skill-mining", "xskills", "skill-mining")]
    [InlineData("custom_mod:item_123", "custom_mod", "item_123")]
    public void ExplicitDomain_ResolvesDomainAndPath(string raw, string expectedDomain, string expectedPath)
    {
        var loc = new AssetLocation(raw);
        Assert.Equal(expectedDomain, loc.Domain);
        Assert.Equal(expectedPath, loc.Path);
    }

    [Theory]
    [InlineData("GAME:Rock-Granite", "game", "rock-granite")]
    [InlineData("SURVIVAL:BREAD-RYE", "survival", "bread-rye")]
    [InlineData("MyMod:Iron-Sword", "mymod", "iron-sword")]
    [InlineData("CLAY-BLUE", "game", "clay-blue")]
    public void CaseInsensitiveNormalization_ProducesLowercaseInternedStrings(string raw, string expectedDomain, string expectedPath)
    {
        var loc = new AssetLocation(raw);
        Assert.Equal(expectedDomain, loc.Domain);
        Assert.Equal(expectedPath, loc.Path);
    }

    [Fact]
    public void CommonDomains_ShareStaticInternedReference()
    {
        var loc1 = new AssetLocation("game:rock-granite");
        var loc2 = new AssetLocation("GAME:stone-andesite");
        var loc3 = new AssetLocation("game:wood-oak");

        Assert.True(object.ReferenceEquals(loc1.Domain, loc2.Domain));
        Assert.True(object.ReferenceEquals(loc1.Domain, loc3.Domain));
        Assert.True(object.ReferenceEquals(loc1.Domain, string.Intern("game")));

        var surv1 = new AssetLocation("survival:bread");
        var surv2 = new AssetLocation("SURVIVAL:water");
        Assert.True(object.ReferenceEquals(surv1.Domain, surv2.Domain));
        Assert.True(object.ReferenceEquals(surv1.Domain, string.Intern("survival")));
    }

    [Fact]
    public void SamePaths_ShareInternedReference()
    {
        var loc1 = new AssetLocation("game:rock-granite");
        var loc2 = new AssetLocation("mod:rock-granite");
        var loc3 = new AssetLocation("ROCK-GRANITE");

        Assert.True(object.ReferenceEquals(loc1.Path, loc2.Path));
        Assert.True(object.ReferenceEquals(loc1.Path, loc3.Path));
    }

    [Fact]
    public void EmptyStringAndBoundaryCases()
    {
        var empty = new AssetLocation("");
        Assert.Equal("game", empty.Domain);
        Assert.Equal("", empty.Path);

        var nullResult = AssetLocation.CreateOrNull("");
        Assert.Null(nullResult);

        var colonOnly = new AssetLocation(":");
        Assert.Equal("", colonOnly.Path);

        var leadingColon = new AssetLocation(":pathonly");
        Assert.Equal("pathonly", leadingColon.Path);

        var trailingColon = new AssetLocation("domainonly:");
        Assert.Equal("domainonly", trailingColon.Domain);
        Assert.Equal("", trailingColon.Path);
    }

    [Fact]
    public void CreateHelper_ProducesExpectedLocations()
    {
        var loc1 = AssetLocation.Create("rock-granite", "game");
        Assert.Equal("game", loc1.Domain);
        Assert.Equal("rock-granite", loc1.Path);

        var loc2 = AssetLocation.Create("mod:custom-tool", "game");
        Assert.Equal("mod", loc2.Domain);
        Assert.Equal("custom-tool", loc2.Path);
    }

    [Fact]
    public void BridgeResolve_MatchesDirectCall()
    {
        OptimumApiBridge.AssetLocationResolveToDomainAndPath("GAME:Rock-Granite", out string domain, out string path);
        Assert.Equal("game", domain);
        Assert.Equal("rock-granite", path);
        Assert.True(object.ReferenceEquals(domain, string.Intern("game")));
    }
}
