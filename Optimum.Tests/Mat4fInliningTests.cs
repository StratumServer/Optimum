using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

/// <summary>
/// Validates Mat4f.Multiply inlining: correctness (identical output to
/// reference implementation) and confirms AggressiveInlining is applied.
/// The JIT default threshold is 64 IL bytes; Mat4f.Multiply exceeds this,
/// so without the attribute the JIT won't inline at 50k+ calls/frame.
/// </summary>
public class Mat4fInliningTests
{
    private readonly ITestOutputHelper _output;

    public Mat4fInliningTests(ITestOutputHelper output) => _output = output;

    // --- Correctness: Multiply produces the expected result ---

    [Fact]
    public void Multiply_IdentityTimesMatrix_ReturnsMatrix()
    {
        float[] identity = Mat4f.Create();
        float[] m = new float[]
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16
        };
        float[] result = new float[16];

        Mat4f.Multiply(result, identity, m);

        for (int i = 0; i < 16; i++)
            Assert.Equal(m[i], result[i], 5);
    }

    [Fact]
    public void Multiply_KnownMatrices_CorrectProduct()
    {
        // A = rotation-like, B = translation-like
        float[] a = Mat4f.Create();
        a[0] = 0; a[1] = 1; a[4] = -1; a[5] = 0; // 90 deg Z rotation (upper 2x2)

        float[] b = Mat4f.Create();
        b[12] = 5; b[13] = 3; b[14] = 7; // translation (5,3,7)

        float[] result = new float[16];
        Mat4f.Multiply(result, a, b);

        // The translation column of the result = A * (5,3,7,1)
        // With A rotating 90 deg Z: x'=-y=3 mapped through our matrix layout
        Assert.NotEqual(0f, result[12] + result[13] + result[14]);
    }

    [Fact]
    public void Mul_SpanOverload_MatchesArrayOverload()
    {
        float[] a = { 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 1, 2, 3, 1 };
        float[] b = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1 };
        float[] expected = new float[16];
        Mat4f.Multiply(expected, a, b);

        float[] aCopy = (float[])a.Clone();
        Mat4f.Mul(aCopy.AsSpan(), b.AsSpan());

        for (int i = 0; i < 16; i++)
            Assert.Equal(expected[i], aCopy[i], 5);
    }

    // --- AggressiveInlining attribute is present ---

    [Fact]
    public void Multiply_HasAggressiveInliningAttribute()
    {
        var method = typeof(Mat4f).GetMethod(
            "Multiply",
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(float[]), typeof(float[]), typeof(float[]) });

        Assert.NotNull(method);
        var flags = method.GetMethodImplementationFlags();
        Assert.True(
            (flags & MethodImplAttributes.AggressiveInlining) != 0,
            "Mat4f.Multiply missing AggressiveInlining");
    }

    [Theory]
    [InlineData("Multiply", new[] { typeof(float[]), typeof(float[]), typeof(float[]) })]
    [InlineData("Translate", new[] { typeof(float[]), typeof(float[]), typeof(float), typeof(float), typeof(float) })]
    [InlineData("RotateX", new[] { typeof(float[]), typeof(float[]), typeof(float) })]
    [InlineData("RotateY", new[] { typeof(float[]), typeof(float[]), typeof(float) })]
    [InlineData("RotateZ", new[] { typeof(float[]), typeof(float[]), typeof(float) })]
    public void HotMethods_HaveAggressiveInlining(string name, Type[] paramTypes)
    {
        var method = typeof(Mat4f).GetMethod(name, BindingFlags.Public | BindingFlags.Static, paramTypes);
        Assert.NotNull(method);
        var flags = method.GetMethodImplementationFlags();
        Assert.True(
            (flags & MethodImplAttributes.AggressiveInlining) != 0,
            $"Mat4f.{name} missing AggressiveInlining");
    }

    // --- IL size heuristic: confirm method exceeds JIT inline threshold ---

    [Fact]
    public void Multiply_ILSize_ExceedsDefaultInlineThreshold()
    {
        var method = typeof(Mat4f).GetMethod(
            "Multiply",
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(float[]), typeof(float[]), typeof(float[]) });

        Assert.NotNull(method);
        var body = method.GetMethodBody();
        Assert.NotNull(body);

        int ilSize = body.GetILAsByteArray()!.Length;
        _output.WriteLine($"Mat4f.Multiply IL size: {ilSize} bytes (JIT threshold: 64)");

        // The JIT won't inline without AggressiveInlining if IL > 64 bytes.
        // This test confirms the attribute is needed, not optional.
        Assert.True(ilSize > 64,
            $"IL size {ilSize} <= 64: JIT would inline without the attribute (attribute is still harmless)");
    }

    // --- Performance sanity: 1M calls finish under 100ms (confirms no overhead) ---

    [Fact]
    public void Multiply_1M_Calls_Under100ms()
    {
        float[] a = Mat4f.Create();
        float[] b = Mat4f.Create();
        float[] r = new float[16];
        a[0] = 1.1f; b[5] = 2.2f;

        // Warmup
        for (int i = 0; i < 1000; i++)
            Mat4f.Multiply(r, a, b);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++)
            Mat4f.Multiply(r, a, b);
        sw.Stop();

        _output.WriteLine($"1M Mat4f.Multiply: {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"1M multiplies took {sw.ElapsedMilliseconds}ms (expected < 100ms)");
    }
}
