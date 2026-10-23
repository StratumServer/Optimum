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

try {
    NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
        name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);
} catch (InvalidOperationException) {
    // The Optimum cairo-sharp wrapper installs its own deny/allow resolver in a module initializer.
}

using var surface = new ImageSurface(Format.Argb32, 64, 40);
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
}

surface.Flush();
byte[] pixels = surface.Data;
if (!pixels.Any(value => value != 0))
{
    Console.Error.WriteLine("Cairo drawing produced an empty surface.");
    return 1;
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
Console.WriteLine($"Legacy Cairo binary fixture passed: {surface.Width}x{surface.Height}, stride={surface.Stride}, png={System.IO.Path.GetFullPath(args[0])}, ps={System.IO.Path.GetFullPath(postscriptPath)}");
return 0;
