using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

public class BlurNineTapCoverageTests
{
    // The vanilla 17-tap Gaussian kernel this shader was reduced from
    // (blur.fsh pre-Optimum, offsets -8..8), from
    // http://dev.theomader.com/gaussian-kernel-calculator/.
    private static readonly double[] VanillaWeights =
    {
        0.001422, 0.004255, 0.011001, 0.024574, 0.047431, 0.0791, 0.113978, 0.141908, 0.152663,
        0.141908, 0.113978, 0.0791, 0.047431, 0.024574, 0.011001, 0.004255, 0.001422
    };

    [Fact]
    public void VanillaKernelWeightsSumToApproximatelyOne()
    {
        double sum = 0;
        foreach (double w in VanillaWeights) sum += w;

        Assert.InRange(sum, 0.999, 1.001);
    }

    [Fact]
    public void NineTapWeightsAndOffsetsMatchTheAdjacentPairCollapseFormula()
    {
        // Reproduces the derivation documented in blur.vsh: pair adjacent
        // offsets (o1, o1+1) with kernel weights (w1, w2) into one
        // combined-weight, bilinear-sampled tap at
        //   offset = (o1*w1 + o2*w2) / (w1+w2), weight = w1+w2
        double Weight(int offset) => VanillaWeights[offset + 8];

        (double weight, double offset)[] expectedPositiveSide =
        {
            (Weight(1) + Weight(2), (1 * Weight(1) + 2 * Weight(2)) / (Weight(1) + Weight(2))),
            (Weight(3) + Weight(4), (3 * Weight(3) + 4 * Weight(4)) / (Weight(3) + Weight(4))),
            (Weight(5) + Weight(6), (5 * Weight(5) + 6 * Weight(6)) / (Weight(5) + Weight(6))),
            (Weight(7) + Weight(8), (7 * Weight(7) + 8 * Weight(8)) / (Weight(7) + Weight(8))),
        };

        double centerWeight = Weight(0);

        double sum = centerWeight;
        foreach ((double w, _) in expectedPositiveSide) sum += 2 * w;
        Assert.InRange(sum, 0.999, 1.001);

        string vshSource = File.ReadAllText(FindRepositoryFile("sources/shaders/blur.vsh"));
        string fshSource = File.ReadAllText(FindRepositoryFile("sources/shaders/blur.fsh"));

        double[] shippedOffsets = ExtractFloatArray(vshSource, "kOffsets");
        double[] shippedWeights = ExtractWeightSequence(fshSource);

        Assert.Equal(9, shippedOffsets.Length);
        Assert.Equal(9, shippedWeights.Length);

        // Shipped arrays run negative -> center -> positive; mirror the
        // positive-side expectations we derived above onto both halves.
        for (int pairIndex = 0; pairIndex < expectedPositiveSide.Length; pairIndex++)
        {
            (double expectedWeight, double expectedOffset) = expectedPositiveSide[pairIndex];

            int positiveArrayIndex = 4 + 1 + pairIndex;
            int negativeArrayIndex = 4 - 1 - pairIndex;

            AssertClose(expectedOffset, shippedOffsets[positiveArrayIndex]);
            AssertClose(-expectedOffset, shippedOffsets[negativeArrayIndex]);
            AssertClose(expectedWeight, shippedWeights[positiveArrayIndex]);
            AssertClose(expectedWeight, shippedWeights[negativeArrayIndex]);
        }

        AssertClose(0.0, shippedOffsets[4]);
        AssertClose(centerWeight, shippedWeights[4]);
    }

    [Fact]
    public void FragmentShaderDeclaresNineTapsMatchingTheVertexShaderArraySize()
    {
        string vshSource = File.ReadAllText(FindRepositoryFile("sources/shaders/blur.vsh"));
        string fshSource = File.ReadAllText(FindRepositoryFile("sources/shaders/blur.fsh"));

        Assert.Contains("out vec2 texCoords[9];", vshSource);
        Assert.Contains("in vec2 texCoords[9];", fshSource);
    }

    [Fact]
    public void VertexShaderLoopBoundIncludesTheLastTapIndex()
    {
        // The bug this task also fixes: a `i < 8` bound from -8 leaves the
        // last slot of a 17-element array unwritten. The 9-tap rewrite
        // iterates a flat 0..8 array instead, so assert the loop covers
        // the whole kOffsets/texCoords range rather than stopping short.
        string vshSource = File.ReadAllText(FindRepositoryFile("sources/shaders/blur.vsh"));

        Assert.Contains("for (int i = 0; i < 9; i++)", vshSource);
        Assert.DoesNotContain("i < 8", vshSource);
    }

    private static void AssertClose(double expected, double actual)
    {
        Assert.InRange(actual, expected - 0.0001, expected + 0.0001);
    }

    private static double[] ExtractFloatArray(string source, string arrayName)
    {
        Match match = Regex.Match(source, arrayName + @"\[9\]\s*=\s*float\[9\]\(([^)]+)\)");
        Assert.True(match.Success, $"Could not find {arrayName} float[9] initializer in shader source.");

        string[] parts = match.Groups[1].Value.Split(',');
        double[] values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            values[i] = double.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static double[] ExtractWeightSequence(string source)
    {
        MatchCollection matches = Regex.Matches(source, @"texCoords\[\d+\]\)\s*\*\s*([0-9.]+);");
        double[] values = new double[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            values[i] = double.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
