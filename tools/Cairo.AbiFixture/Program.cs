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

using var surface = new ImageSurface(Format.Argb32, 64, 40);
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
