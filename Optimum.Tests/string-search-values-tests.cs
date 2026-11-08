using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class StringSearchValuesTests
{
    private sealed class DummyRegistryObject : RegistryObject
    {
        public DummyRegistryObject(string? code)
        {
            if (code != null)
            {
                Code = new AssetLocation(code);
            }
        }
    }

    [Fact]
    public void SearchValues_Instances_ContainExpectedDelimiters()
    {
        Assert.True(OptimumApiBridge.CodePartDelimiter.Contains('-'));
        Assert.False(OptimumApiBridge.CodePartDelimiter.Contains(':'));
        Assert.False(OptimumApiBridge.CodePartDelimiter.Contains('/'));

        Assert.True(OptimumApiBridge.WildcardStar.Contains('*'));
        Assert.False(OptimumApiBridge.WildcardStar.Contains('@'));

        Assert.True(OptimumApiBridge.AssetColonDelimiter.Contains(':'));
        Assert.False(OptimumApiBridge.AssetColonDelimiter.Contains('/'));

        Assert.True(OptimumApiBridge.WildcardTriggerChars.Contains('*'));
        Assert.True(OptimumApiBridge.WildcardTriggerChars.Contains('@'));
        Assert.False(OptimumApiBridge.WildcardTriggerChars.Contains(':'));

        Assert.True(OptimumApiBridge.AssetPathDelimiters.Contains(':'));
        Assert.True(OptimumApiBridge.AssetPathDelimiters.Contains('/'));
        Assert.False(OptimumApiBridge.AssetPathDelimiters.Contains('-'));
    }

    [Theory]
    [InlineData("rock", 0, "rock")]
    [InlineData("rock-granite-rough", 0, "rock")]
    [InlineData("rock-granite-rough", 1, "granite")]
    [InlineData("rock-granite-rough", 2, "rough")]
    [InlineData("rock-granite-rough", 3, null)]
    public void RegistryObjectFirstCodePart_ExtractsPartsCorrectly(string path, int pos, string? expected)
    {
        var obj = new DummyRegistryObject("game:" + path);
        string? result = OptimumApiBridge.RegistryObjectFirstCodePart(obj, pos);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RegistryObjectFirstCodePart_NullObjectOrCode_ReturnsNull()
    {
        Assert.Null(OptimumApiBridge.RegistryObjectFirstCodePart(null, 0));
        var obj = new DummyRegistryObject(null);
        Assert.Null(OptimumApiBridge.RegistryObjectFirstCodePart(obj, 0));
    }

    [Fact]
    public void RegistryObjectFirstCodePart_NegativePos_Throws()
    {
        var obj = new DummyRegistryObject("game:rock-granite");
        Assert.Throws<IndexOutOfRangeException>(() => OptimumApiBridge.RegistryObjectFirstCodePart(obj, -1));
    }

    [Theory]
    [InlineData("rock", 0, "rock")]
    [InlineData("rock-granite-rough", 0, "rough")]
    [InlineData("rock-granite-rough", 1, "granite")]
    [InlineData("rock-granite-rough", 2, "rock")]
    [InlineData("rock-granite-rough", 3, null)]
    public void RegistryObjectLastCodePart_ExtractsPartsCorrectly(string path, int pos, string? expected)
    {
        var obj = new DummyRegistryObject("game:" + path);
        string? result = OptimumApiBridge.RegistryObjectLastCodePart(obj, pos);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RegistryObjectLastCodePart_NullObjectOrCode_ReturnsNull()
    {
        Assert.Null(OptimumApiBridge.RegistryObjectLastCodePart(null, 0));
        var obj = new DummyRegistryObject(null);
        Assert.Null(OptimumApiBridge.RegistryObjectLastCodePart(obj, 0));
    }

    [Fact]
    public void RegistryObjectLastCodePart_NegativePos_Throws()
    {
        var obj = new DummyRegistryObject("game:rock-granite");
        Assert.Throws<IndexOutOfRangeException>(() => OptimumApiBridge.RegistryObjectLastCodePart(obj, -1));
    }

    [Theory]
    [InlineData("game:rock-granite", "game", "rock-granite")]
    [InlineData("survival:planks-oak", "survival", "planks-oak")]
    [InlineData("creative:gear-copper", "creative", "gear-copper")]
    [InlineData("custommod:fancy-block", "custommod", "fancy-block")]
    [InlineData("plain-path", null, "plain-path")]
    public void AssetLocationResolveToDomainAndPath_ParsesIdentically(string raw, string? expectedDomain, string expectedPath)
    {
        OptimumApiBridge.AssetLocationResolveToDomainAndPath(raw, out string? domain, out string? path);
        Assert.Equal(expectedDomain, domain);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void AssetLocationResolveToDomainAndPath_NullRaw_ReturnsNull()
    {
        OptimumApiBridge.AssetLocationResolveToDomainAndPath(null, out string? domain, out string? path);
        Assert.Null(domain);
        Assert.Null(path);
    }
}
