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

NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
    name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);

using var surface = new ImageSurface(Format.Argb32, 64, 40);
using (var context = new Context(surface))
{
    context.Save();
    context.SetSourceRGBA(0.15, 0.3, 0.45, 1);
    context.Rectangle(4, 5, 48, 22);
    context.Fill();
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
Console.WriteLine($"Legacy Cairo binary fixture passed: {surface.Width}x{surface.Height}, stride={surface.Stride}, png={System.IO.Path.GetFullPath(args[0])}");
return 0;
