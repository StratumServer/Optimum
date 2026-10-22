using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection("GUI metrics")]
public sealed class CairoPreparationReuseTests : IDisposable
{
    readonly bool original = RecordedPreparationCache.Enabled;
    public CairoPreparationReuseTests() { _ = CairoAPI.Version; RecordedPreparationCache.Enabled = true; RecordedPreparationCache.Clear(); RecordedPreparationCache.ResetCounters(); }
    public void Dispose() { RecordedPreparationCache.Clear(); RecordedPreparationCache.ResetCounters(); RecordedPreparationCache.Enabled = original; }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void FreshSurfaceReusesTextShapesAndStrokesWithoutChangingStateOrPixels(double scale)
    {
        RecordedPreparationCache.Enabled = false;
        byte[] expected = Draw(scale, 0, out var expectedPoint);
        Assert.Equal(0, RecordedPreparationCache.Count);
        RecordedPreparationCache.Enabled = true;
        Assert.Equal(expected, Draw(scale, 0, out var coldPoint));
        long hits = RecordedPreparationCache.Hits;
        Assert.Equal(expected, Draw(scale, 0, out var warmPoint));
        Assert.Equal(expectedPoint.X, coldPoint.X); Assert.Equal(expectedPoint.Y, coldPoint.Y);
        Assert.Equal(expectedPoint.X, warmPoint.X); Assert.Equal(expectedPoint.Y, warmPoint.Y);
        Assert.True(RecordedPreparationCache.Hits > hits);
        Assert.True(RecordedPreparationCache.PathHits > 0);
        Assert.True(RecordedPreparationCache.StrokeHits > 0);
    }

    [Theory]
    [InlineData(1)] // Text
    [InlineData(2)] // Font size
    [InlineData(3)] // Translation
    [InlineData(4)] // Stroke width/dash
    [InlineData(5)] // Source color
    [InlineData(6)] // Clip
    [InlineData(7)] // Fill rule/operator
    [InlineData(8)] // Font options and slant
    [InlineData(9)] // Rotation/nonuniform scale
    [InlineData(10)] // Explicit font matrix
    [InlineData(11)] // Glyph indices and positions
    public void ChangedInputsProduceTheUncachedResult(int variant)
    {
        _ = Draw(1.5, 0, out _);
        byte[] reused = Draw(1.5, variant, out var reusedPoint);
        RecordedPreparationCache.Enabled = false;
        byte[] expected = Draw(1.5, variant, out var expectedPoint);
        Assert.Equal(expected, reused);
        Assert.Equal(expectedPoint.X, reusedPoint.X); Assert.Equal(expectedPoint.Y, reusedPoint.Y);
    }

    [Fact]
    public void RecordedSvgOwnsItsLeaseAfterCallerDisposalAndCacheClear()
    {
        using var surface = new ImageSurface(Format.Argb32, 32, 32);
        surface.BeginRecording();
        SKPicture value;
        using (var prepared = RecordedSvgGeometry.Acquire(Svg("red"), 32, 32, 32, 32))
        {
            value = prepared.Value;
            Assert.True(surface.RecordPicture(value, 0, 0, 32, 32, null, _ => { }, pictureOwner: prepared));
        }
        using var snapshot = surface.CaptureRecordedSource();
        surface.Dispose();
        RecordedPreparationCache.Clear();
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawPicture(value);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(8, 8));
    }

    [Fact]
    public void SvgContentAndViewportInvalidateWhileTintStaysOutsideTheGeometry()
    {
        using var first = RecordedSvgGeometry.Acquire(Svg("red"), 32, 32, 32, 32);
        using var same = RecordedSvgGeometry.Acquire(Svg("red"), 32, 32, 32, 32);
        Assert.Same(first.Value, same.Value);
        using var changed = RecordedSvgGeometry.Acquire(Svg("blue"), 32, 32, 32, 32);
        using var resized = RecordedSvgGeometry.Acquire(Svg("red"), 64, 64, 64, 64);
        Assert.NotSame(first.Value, changed.Value); Assert.NotSame(first.Value, resized.Value);
        Assert.Equal(1, RecordedPreparationCache.PictureHits);
    }

    [Fact]
    public void BudgetEvictsAndOversizedKeysBypassWithoutLeakingOwnership()
    {
        using var held = new RecordedPreparationCache.Geometry(new SKPath());
        for (int i = 0; i < RecordedPreparationCache.MaxEntries + 8; i++)
        {
            using var resource = new RecordedPreparationCache.Geometry(new SKPath());
            RecordedPreparationCache.Store(new RecordedPreparationCache.Key(3, new long[] { i }), resource);
        }
        Assert.Equal(RecordedPreparationCache.MaxEntries, RecordedPreparationCache.Count);
        Assert.Equal(8, RecordedPreparationCache.Evictions);
        Assert.InRange(RecordedPreparationCache.Bytes, 1, RecordedPreparationCache.MaxBytes);
        long count = RecordedPreparationCache.Count;
        RecordedPreparationCache.Store(new RecordedPreparationCache.Key(3, new long[RecordedPreparationCache.MaxKeyBytes]), held);
        Assert.Equal(count, RecordedPreparationCache.Count);
        RecordedPreparationCache.Clear();
        Assert.Equal(0, RecordedPreparationCache.Bytes);
    }

    [Fact]
    public void HashCollisionStillComparesTheCompleteInput()
    {
        uint initial = unchecked(2166136261u + 3u);
        uint high = unchecked(initial * 16777619u) ^ unchecked((initial ^ 1u) * 16777619u);
        var first = new RecordedPreparationCache.Key(3, new long[] { 0 });
        var second = new RecordedPreparationCache.Key(3, new[] { unchecked((long)((ulong)high << 32 | 1)) });
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(second));
        using var a = new RecordedPreparationCache.Geometry(new SKPath());
        using var b = new RecordedPreparationCache.Geometry(new SKPath());
        RecordedPreparationCache.Store(first, a); RecordedPreparationCache.Store(second, b);
        using var actualA = RecordedPreparationCache.Acquire<RecordedPreparationCache.Geometry>(first);
        using var actualB = RecordedPreparationCache.Acquire<RecordedPreparationCache.Geometry>(second);
        Assert.Same(a, actualA); Assert.Same(b, actualB);
    }

    [Fact]
    public void EstimatedByteBudgetEvictsBeforeEntryLimitAndKeepsLiveGeometry()
    {
        var points = Enumerable.Range(0, 100_000).Select(i => new SKPoint(i % 400, i / 400)).ToArray();
        using var heldPath = new SKPath(); heldPath.AddPoly(points);
        using var held = new RecordedPreparationCache.Geometry(new SKPath(heldPath));
        RecordedPreparationCache.Store(new RecordedPreparationCache.Key(3, new long[] { -1 }), held);
        for (int i = 0; i < 10; i++)
        {
            using var path = new SKPath(); path.AddPoly(points);
            using var resource = new RecordedPreparationCache.Geometry(new SKPath(path));
            RecordedPreparationCache.Store(new RecordedPreparationCache.Key(3, new long[] { i }), resource);
        }
        Assert.True(RecordedPreparationCache.Evictions > 0);
        Assert.InRange(RecordedPreparationCache.Count, 1, 10);
        Assert.InRange(RecordedPreparationCache.Bytes, 1, RecordedPreparationCache.MaxBytes);
        Assert.Equal(heldPath.PointCount, held.Path.PointCount);
    }

    [Fact]
    public async Task IndependentProducersCanShareAndClearImmutablePreparation()
    {
        byte[] expected = Draw(1.5, 0, out _);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                if (i % 3 == 0) RecordedPreparationCache.Clear();
                Assert.Equal(expected, Draw(1.5, 0, out var point));
            }
        })));
        Assert.InRange(RecordedPreparationCache.Count, 0, RecordedPreparationCache.MaxEntries);
    }

    static string Svg(string color) => $"<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'><rect width='32' height='32' fill='{color}'/></svg>";
    static byte[] Draw(double scale, int variant, out PointD point)
    {
        using var surface = new ImageSurface(Format.Argb32, 144, 96);
        surface.BeginRecording();
        using (var c = new Context(surface))
        {
            c.Scale(scale, scale);
            if (variant == 3) c.Translate(2, 3);
            if (variant == 9) { c.Rotate(.23); c.Scale(.85, 1.1); }
            c.Antialias = Antialias.None;
            c.Rectangle(0, 0, variant == 6 ? 38 : 84, 56); c.Clip();
            c.SetSourceRGBA(variant == 5 ? 0 : 1, 0, variant == 5 ? 1 : 0, 1);
            c.Rectangle(2, 2, 24, 10); c.Fill();
            c.SelectFontFace("DejaVu Sans", variant == 8 ? FontSlant.Italic : FontSlant.Normal, FontWeight.Normal);
            c.SetFontSize(variant == 2 ? 14 : 11);
            if (variant == 8) { using var options = new FontOptions { HintStyle = HintStyle.None, HintMetrics = HintMetrics.Off }; c.FontOptions = options; }
            if (variant == 10) c.FontMatrix = new Matrix(12, 1, 2, 13, 0, 0);
            c.ShowGlyphs(new[] { new Glyph(variant == 11 ? 38 : 36, variant == 11 ? 18 : 8, 18) });
            c.MoveTo(4, 28); c.ShowText(variant == 1 ? "changed Å" : "same Å");
            c.MoveTo(4, 44); c.TextPath("old commands");
            c.FillRule = variant == 7 ? FillRule.EvenOdd : FillRule.Winding;
            if (variant == 7) c.Operator = Operator.Source;
            c.Fill();
            c.SetSourceRGBA(0, 1, 0, 1); c.LineWidth = variant == 4 ? 3 : 1;
            c.SetDash(variant == 4 ? new double[] { 2, 3 } : new double[] { 3, 2 }, variant == 4 ? 1 : 0);
            c.MoveTo(2, 52); c.LineTo(74, 52); c.StrokePreserve();
            point = c.CurrentPoint;
        }
        using var bitmap = new SKBitmap(144, 96, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap)) Assert.True(surface.TryDrawRecordedCommands(canvas));
        var pixels = new byte[144 * 96 * 4]; Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length); return pixels;
    }
}
