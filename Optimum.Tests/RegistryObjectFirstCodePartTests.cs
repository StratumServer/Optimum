using System;
using Xunit;
using Vintagestory.API.Common;

namespace Optimum.Tests;

public class RegistryObjectFirstCodePartTests
{
    [Theory]
    [InlineData("granite", 0)]
    [InlineData("", 0)]
    [InlineData("rock-granite-polished", 0)]
    [InlineData("rock-granite-polished", 1)]
    [InlineData("rock-granite-polished", 2)]
    [InlineData("rock-granite-polished", 3)]
    [InlineData("a-b", int.MaxValue)]
    [InlineData("-leading", 0)]
    [InlineData("-leading", 1)]
    [InlineData("trailing-", 0)]
    [InlineData("trailing-", 1)]
    [InlineData("a--b", 0)]
    [InlineData("a--b", 1)]
    [InlineData("a--b", 2)]
    [InlineData("--", 0)]
    [InlineData("--", 1)]
    [InlineData("--", 2)]
    public void FirstCodePart_MatchesVanillaSplitBehavior(string path, int position)
    {
        AssertMatchesVanilla(path, position);
    }

    [Fact]
    public void FirstCodePart_MatchesVanillaForShortDashPatternsAndPositions()
    {
        char[] alphabet = { 'a', 'b', '-' };
        for (int length = 0; length <= 6; length++)
        {
            int pathCount = (int)Math.Pow(alphabet.Length, length);
            for (int value = 0; value < pathCount; value++)
            {
                char[] characters = new char[length];
                int remaining = value;
                for (int i = 0; i < length; i++)
                {
                    characters[i] = alphabet[remaining % alphabet.Length];
                    remaining /= alphabet.Length;
                }

                string path = new(characters);
                for (int position = -2; position <= length + 2; position++)
                {
                    AssertMatchesVanilla(path, position);
                }
            }
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void FirstCodePart_NegativePositionPreservesVanillaException(int position)
    {
        AssertMatchesVanilla("a-b", position);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void FirstCodePart_NullCodeMatchesVanilla(int position)
    {
        var block = new Block { Code = null };

        AssertBlockMatchesVanilla(block, position);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void FirstCodePart_NullPathPreservesVanillaException(int position)
    {
        AssertMatchesVanilla(null, position);
    }

    private static void AssertMatchesVanilla(string path, int position)
    {
        var block = new Block { Code = new AssetLocation("game", "placeholder") };
        block.Code.Path = path;
        AssertBlockMatchesVanilla(block, position);
    }

    private static void AssertBlockMatchesVanilla(Block block, int position)
    {
        string expected = null;
        Exception expectedException = Record.Exception(() => expected = VanillaFirstCodePart(block.Code, position));
        string actual = null;
        Exception actualException = Record.Exception(() => actual = block.FirstCodePart(position));

        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException == null)
        {
            Assert.Equal(expected, actual);
        }
    }

    private static string VanillaFirstCodePart(AssetLocation code, int position)
    {
        if (code == null) return null;
        if (position == 0 && !code.Path.Contains('-')) return code.Path;

        string[] parts = code.Path.Split('-');
        return position <= parts.Length - 1 ? parts[position] : null;
    }
}
