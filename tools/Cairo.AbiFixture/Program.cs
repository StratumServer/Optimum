using Cairo;
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] == "--debug-state")
{
    string? before = Environment.GetEnvironmentVariable("CAIRO_DEBUG_DISPOSE");
    bool initial = CairoDebug.Enabled;
    string? after = Environment.GetEnvironmentVariable("CAIRO_DEBUG_DISPOSE");
    // Old compiled consumers can still enable tracing after initialization.
    CairoDebug.Enabled = true;
    CairoDebug.OnAllocated(new IntPtr(1));
    CairoDebug.OnDisposed<object>(new IntPtr(1), true);
    CairoDebug.Enabled = false;
    CairoDebug.Enabled = true;
    CairoDebug.OnAllocated(new IntPtr(2));
    CairoDebug.OnDisposed<object>(new IntPtr(2), true);
    CairoDebug.Enabled = initial;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { initial, before, after }));
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Cairo.AbiFixture <output.png>");
    return 2;
}

string loadedCairoPath = System.IO.Path.GetFullPath(typeof(Context).Assembly.Location);
string? expectedCairoPath = Environment.GetEnvironmentVariable("OPTIMUM_CAIRO_EXPECTED_ASSEMBLY");
if (!string.IsNullOrWhiteSpace(expectedCairoPath) &&
    !string.Equals(loadedCairoPath, System.IO.Path.GetFullPath(expectedCairoPath), StringComparison.Ordinal))
{
    Console.Error.WriteLine($"The mod resolved Cairo from '{loadedCairoPath}', expected the Optimum wrapper at '{expectedCairoPath}'.");
    return 1;
}
if (!string.Equals(typeof(Context).Assembly.GetName().Name, "cairo-sharp", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Unexpected Cairo assembly identity: {typeof(Context).Assembly.FullName}");
    return 1;
}
try {
    NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
        name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);
} catch (InvalidOperationException) {
    // The Optimum cairo-sharp wrapper installs its own deny/allow resolver in a module initializer.
}

// A third-party consumer uses the original constructors and raw row pointer.
using (var allocated = new ImageSurface(IntPtr.Zero, Format.Argb32, 2, 2, -16))
{
    if (allocated.Stride != 8 || allocated.DataPtr == IntPtr.Zero)
        throw new InvalidOperationException("Null image data did not allocate native-compatible rows.");
}
IntPtr bottomUp = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
try
{
    using var external = new ImageSurface(IntPtr.Add(bottomUp, 8), Format.Argb32, 2, 2, -8);
    using (var drawing = new Context(external)) { drawing.SetSourceRGB(1, 0, 0); drawing.Paint(); }
    _ = external.DataPtr;
    if (System.Runtime.InteropServices.Marshal.ReadInt32(bottomUp) != unchecked((int)0xffff0000))
        throw new InvalidOperationException("Bottom-up image rows did not reach the caller's buffer.");
}
finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(bottomUp); }

using (var shared = new ImageSurface(Format.Argb32, 8, 4))
using (var first = new Context(shared))
using (var second = new Context(shared))
{
    first.LineWidth = 7;
    if (second.LineWidth != 2) throw new InvalidOperationException("Independent contexts shared drawing state.");
    first.SetSourceRGB(1, 0, 0); first.Rectangle(0, 0, 3, 4);
    second.Translate(4, 0); second.SetSourceRGB(0, 0, 1); second.Rectangle(0, 0, 3, 4);
    first.Fill(); second.Fill();
    IntPtr rows = shared.DataPtr;
    if (System.Runtime.InteropServices.Marshal.ReadInt32(rows) != unchecked((int)0xffff0000)
        || System.Runtime.InteropServices.Marshal.ReadInt32(rows, 16) != unchecked((int)0xff0000ff))
        throw new InvalidOperationException("Interleaved contexts lost their draw order or paths.");
}

var disposedOwner = new ImageSurface(Format.Argb32, 2, 2);
using (var surviving = new Context(disposedOwner))
{
    disposedOwner.Dispose();
    surviving.SetSourceRGB(0, 1, 0); surviving.Paint();
    using var retained = (ImageSurface)surviving.GetTarget();
    if (BitConverter.ToUInt32(retained.Data, 0) != 0xff00ff00)
        throw new InvalidOperationException("Disposing the surface wrapper invalidated its context.");
}

using var surface = new ImageSurface(Format.Argb32, 64, 40);
using (var invalid = new ImageSurface(Format.Argb32, -1, 1))
{
    if ((int)invalid.Status != 32 || invalid.Width != 0 || invalid.Height != 0 || invalid.ReferenceCount != 0)
        throw new InvalidOperationException("The legacy image factory lost its Cairo error-surface contract.");
}
surface.WriteToPng(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-missing-" + Guid.NewGuid().ToString("N"), "image.png"));
using (var finishedSurface = new ImageSurface(Format.Argb32, 4, 4))
using (var finishedContext = new Context(finishedSurface))
{
    finishedContext.Save();
    finishedSurface.Finish();
    finishedContext.LineWidth = 0;
    finishedContext.Stroke();
    finishedContext.Rectangle(0, 0, 0, 2);
    finishedContext.Clip();
    finishedContext.Paint();
    if (finishedContext.Status != Status.Success)
        throw new InvalidOperationException("Finished-surface no-op drawing must preserve the legacy context status.");
    finishedContext.Restore();
    finishedContext.Paint();
    if (finishedContext.Status != Status.SurfaceFinished || finishedSurface.Status != Status.SurfaceFinished)
        throw new InvalidOperationException("The replacement lost the legacy finished-surface error contract.");
    using var failedFace = finishedContext.GetContextFontFace();
    if (failedFace.Status != Status.NoMemory || failedFace.FontType != FontType.Toy || failedFace.ReferenceCount != 0)
        throw new InvalidOperationException("An errored legacy context must return a nil font face.");
    using var failedFaceOptions = new FontOptions();
    using var faceFont = new ScaledFont(failedFace, new Matrix(), new Matrix(), failedFaceOptions);
    if (faceFont.Status != Status.NoMemory || faceFont.FontExtents.Height != 0)
        throw new InvalidOperationException("A nil legacy face must preserve its error when scaled.");
    using var failedFont = finishedContext.GetScaledFont();
    if (failedFont.Status != Status.SurfaceFinished || failedFont.FontExtents.Height != 0 || failedFont.FontMatrix.Xx != 1)
        throw new InvalidOperationException("An errored legacy context must return a nil scaled font.");
    using var nilContext = new Context(finishedSurface);
    if (nilContext.Status != Status.SurfaceFinished)
        throw new InvalidOperationException("A new context must inherit the finished target error.");
}
using (var textSurface = new ImageSurface(Format.Argb32, 8, 8))
using (var textContext = new Context(textSurface))
{
    textContext.MoveTo(1, 4);
    textSurface.Finish();
    textContext.ShowText("");
    textContext.ShowGlyphs(Array.Empty<Glyph>());
    if (textSurface.Status != Status.SurfaceFinished || textContext.Status != Status.Success)
        throw new InvalidOperationException("Finished text drawing must keep native surface/context errors separate.");
    textContext.TextExtents(new byte[] { 0xff });
    if (textContext.Status != Status.InvalidString || textContext.FontExtents.Height != 0)
        throw new InvalidOperationException("Invalid UTF-8 must latch the legacy error without native fallback.");
}
using var targetSurface = new ImageSurface(Format.Argb32, 16, 12);
using var clearSurface = new ImageSurface(Format.Argb32, 2, 2);
using (var clearContext = new Context(clearSurface))
{
    clearContext.SetSourceRGBA(0.9, 0.1, 0.2, 1);
    clearContext.Paint();
    clearContext.Clear();
}
if (clearSurface.Data.Any(value => value != 0))
{
    Console.Error.WriteLine("Cairo ContextUtils.Clear did not clear the managed target.");
    return 1;
}

using (var context = new Context(surface))
{
    context.Save();
    context.SetSourceRGBA(0.15, 0.3, 0.45, 1);
    context.Rectangle(4, 5, 48, 22);
    context.Fill();
    using var alphaMask = new LinearGradient(0, 0, 64, 0);
    alphaMask.AddColorStop(0, new Color(1, 1, 1, 0));
    alphaMask.AddColorStop(1, new Color(1, 1, 1, 1));
    context.Mask(alphaMask);
    context.Restore();

    context.SelectFontFace("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
    context.SetFontSize(12);
    TextExtents extents = context.TextExtents("Legacy mod");
    if (extents.XAdvance <= 0 || context.FontExtents.Height <= 0)
    {
        Console.Error.WriteLine("Cairo text measurement returned empty metrics.");
        return 1;
    }

    context.Save();
    context.Operator = Operator.Exclusion;
    context.Antialias = Antialias.Fast;
    context.SetSourceRGBA(.6, .3, .8, .7);
    context.Arc(12, 12, 8.3, 0, 6.2);
    context.Fill();
    context.Operator = Operator.Screen;
    using (var options = new FontOptions { Antialias = Antialias.Gray }) context.FontOptions = options;
    context.MoveTo(3.375, 22.625);
    context.ShowText("Legacy mod");
    context.Restore();

    string? fileFontPath = Environment.GetEnvironmentVariable("OPTIMUM_CAIRO_ABI_FONT");
    if (!string.IsNullOrWhiteSpace(fileFontPath))
    {
        string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-abi-font-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.Copy(fileFontPath, copy);
        try
        {
            using var face = Cairo.Util.FreeTypeFontFace.Create(copy, 0);
            context.Save();
            context.SetContextFontFace(face);
            context.SetFontSize(12);
            File.Delete(copy);
            face.Dispose();
            using var scaled = context.GetScaledFont();
            context.SetScaledFont(scaled);
            if (scaled.FontType != FontType.FreeType || context.TextExtents("File mod").XAdvance <= 0)
                throw new InvalidOperationException("The legacy file-font API lost its selected face.");
            context.MoveTo(2, 19);
            context.ShowText("File mod");
            context.MoveTo(2, 31);
            context.TextPath("File mod");
            context.Fill();
            context.Restore();
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
    }

    context.Antialias = Antialias.None;
    context.LineWidth = 1;
    context.SetDash(new[] { 4.0, 4.0 }, 0);
    context.SetSourceRGB(1, 1, 1);
    context.MoveTo(0, 34);
    context.LineTo(48, 34);
    context.Stroke();

    // Exercise the same public API used by precompiled third-party mods. A
    // target switch with nested groups must retain their managed target surfaces.
    context.PushGroup();
    context.SetSourceRGBA(0.1, 0.8, 0.25, 1);
    context.Rectangle(1, 1, 8, 6);
    context.Fill();
    using Surface outerGroup = context.GetGroupTarget();
    context.PushGroup();
    context.SetSourceRGBA(0.8, 0.7, 0.15, 1);
    context.Rectangle(2, 2, 6, 4);
    context.Fill();
    using Surface innerGroup = context.GetGroupTarget();

    context.SetTarget(targetSurface);
    context.SetSourceRGBA(0.75, 0.2, 0.1, 1);
    context.Rectangle(2, 2, 8, 6);
    context.Fill();
    context.SetSourceRGBA(0, 1, 0, 1);
    context.Rectangle(0, 9, 1, 1);
    context.Fill();
    context.SetSourceSurface(targetSurface, 1, 0);
    context.Rectangle(1, 9, 1, 1);
    context.Fill();

    outerGroup.WriteToPng(System.IO.Path.ChangeExtension(args[0], ".outer-group.png"));
    innerGroup.WriteToPng(System.IO.Path.ChangeExtension(args[0], ".inner-group.png"));
}

surface.Flush();
byte[] pixels = surface.Data;
if (!pixels.Any(value => value != 0))
{
    Console.Error.WriteLine("Cairo drawing produced an empty surface.");
    return 1;
}
byte[] targetPixels = targetSurface.Data;
if (!targetPixels.Any(value => value != 0))
{
    Console.Error.WriteLine("Cairo SetTarget did not draw to the managed third-party mod target.");
    return 1;
}
for (int x = 0; x < 2; x++)
{
    int pixel = 9 * targetSurface.Stride + x * 4;
    if (targetPixels[pixel + 1] < 180 || targetPixels[pixel] > 40 || targetPixels[pixel + 2] > 40 || targetPixels[pixel + 3] < 240)
    {
        Console.Error.WriteLine($"Cairo SetSourceSurface self-copy did not preserve the green pixel at x={x}: BGRA={targetPixels[pixel]},{targetPixels[pixel + 1]},{targetPixels[pixel + 2]},{targetPixels[pixel + 3]}.");
        return 1;
    }
}

surface.WriteToPng(args[0]);
string postscriptPath = System.IO.Path.ChangeExtension(args[0], ".ps");
using (var postscript = new PSSurface(postscriptPath, 64, 40))
using (var context = new Context(postscript))
{
    context.SetSourceRGBA(0.2, 0.65, 0.35, 1);
    context.Rectangle(8, 6, 36, 24);
    context.Fill();
    context.ShowPage();
    context.SetSourceRGBA(0.75, 0.25, 0.1, 1);
    context.Paint();
}
if (!File.ReadAllText(postscriptPath).StartsWith("%!PS-Adobe-3.0", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Cairo PostScript output has an invalid header.");
    return 1;
}
if (string.Equals(Environment.GetEnvironmentVariable("OPTIMUM_CAIRO_NATIVE"), "deny", StringComparison.OrdinalIgnoreCase) &&
    File.Exists("/proc/self/maps") && File.ReadAllText("/proc/self/maps").Contains("libcairo", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The managed Cairo API fixture loaded libcairo in deny mode.");
    return 1;
}
if (string.Equals(Environment.GetEnvironmentVariable("OPTIMUM_CAIRO_NATIVE"), "deny", StringComparison.OrdinalIgnoreCase) &&
    !AppDomain.CurrentDomain.GetAssemblies().Any(assembly => string.Equals(assembly.GetName().Name, "SkiaSharp", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("The managed third-party mod fixture did not load SkiaSharp.");
    return 1;
}
Console.WriteLine($"Legacy third-party mod fixture passed under OPTIMUM_CAIRO_NATIVE={Environment.GetEnvironmentVariable("OPTIMUM_CAIRO_NATIVE") ?? "unset"}: assembly={loadedCairoPath}, ContextUtils.Clear, {surface.Width}x{surface.Height}, stride={surface.Stride}, png={System.IO.Path.GetFullPath(args[0])}, ps={System.IO.Path.GetFullPath(postscriptPath)}");
return 0;
