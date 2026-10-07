using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cairo;
using SkiaSharp;

namespace Cairo.ParityHarness;

internal static class Program
{
    private const int LogicalWidth = 96;
    private const int TextLogicalWidth = 160;
    private const int LogicalHeight = 72;
    private static readonly double[] Scales = [1.0, 1.5, 2.0];
    private const string TextSample = "Café Ångström";
    private static string? selectedFontFile;

    private sealed record RenderResult(PixelImage Image, object? Metrics);

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--api-manifest")
        {
            WriteApiManifest(System.IO.Path.GetFullPath(args[1]));
            return 0;
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This first-slice harness currently supports Linux only; native library names must be verified before adding other platforms.");
            return 1;
        }
        NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, ResolveCairo);
        var output = args.Length > 0 ? System.IO.Path.GetFullPath(args[0]) : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-issue-130-parity");
        Directory.CreateDirectory(output);

        try
        {
            var results = new List<object>();
            var cairoReferenceDeterministic = true;
            var fontInfo = ResolveFontInfo();
            foreach (var scale in Scales)
            foreach (var scene in new[] { "geometry", "paths", "porter-duff", "text" })
            {
                var name = $"{scene}-scale-{scale:0.0}".Replace('.', '_');
                var cairo = RenderCairo(scene, scale, System.IO.Path.Combine(output, $"{name}-cairo.png"));
                var cairoRepeat = RenderCairo(scene, scale, System.IO.Path.Combine(output, $"{name}-cairo-repeat.png"));
                var skia = RenderSkia(scene, scale, System.IO.Path.Combine(output, $"{name}-skia.png"));
                var self = PixelComparator.Compare(cairo.Image, cairoRepeat.Image);
                var cross = PixelComparator.Compare(cairo.Image, skia.Image, threshold: 1);
                var crossPixelExact = PixelComparator.Compare(cairo.Image, skia.Image, threshold: 0);
                WriteDifference(System.IO.Path.Combine(output, $"{name}-difference.png"), cairo.Image, skia.Image);
                var operatorResults = scene == "porter-duff"
                    ? Enumerable.Range(0, 6).Select(i =>
                    {
                        var logicalX = 4 + i * 15;
                        var cairoCell = CropLogical(cairo.Image, logicalX, 8, 14, 56, scale);
                        var skiaCell = CropLogical(skia.Image, logicalX, 8, 14, 56, scale);
                        return new
                        {
                            operation = new[] { "Over", "Source", "In", "Out", "Atop", "Xor" }[i],
                            cairoSample = SampleRgba(cairo.Image, (int)((logicalX + 7) * scale), (int)((8 + 30) * scale)),
                            skiaSample = SampleRgba(skia.Image, (int)((logicalX + 7) * scale), (int)((8 + 30) * scale)),
                            cairoBackgroundSample = SampleRgba(cairo.Image, (int)((logicalX + 1) * scale), (int)((8 + 2) * scale)),
                            skiaBackgroundSample = SampleRgba(skia.Image, (int)((logicalX + 1) * scale), (int)((8 + 2) * scale)),
                            comparison = PixelComparator.Compare(cairoCell, skiaCell, threshold: 1),
                            pixelExactComparison = PixelComparator.Compare(cairoCell, skiaCell, threshold: 0)
                        };
                    }).ToArray()
                    : null;
                // Cairo repeatability is a harness validity gate. Cross-backend raster
                // differences are retained as diagnostics; user-visible behavior is
                // assessed through layout and live GUI fixtures, not byte equality.
                cairoReferenceDeterministic &= self.WithinThreshold;
                results.Add(new
                {
                    scene,
                    scale,
                    width = cairo.Image.Width,
                    height = cairo.Image.Height,
                    cairoSelfComparison = self,
                    cairoVsSkia = cross,
                    cairoVsSkiaPixelExact = crossPixelExact,
                    operatorComparisons = operatorResults,
                    cairoMetrics = cairo.Metrics,
                    skiaMetrics = skia.Metrics
                });
                Console.WriteLine($"{scene} @ {scale:0.0}: Cairo repeat max {self.MaxChannelDelta}; Cairo/Skia max {cross.MaxChannelDelta}, {cross.PixelsOverThreshold}/{cross.PixelsCompared} pixels >1, RMS {cross.RmsChannelError:F4}, alpha RMS {cross.RmsAlphaError:F4}; exact mismatches {crossPixelExact.PixelsOverThreshold}/{crossPixelExact.PixelsCompared}.");
                if (operatorResults is not null)
                    foreach (var item in operatorResults)
                        Console.WriteLine($"  {item.operation}: max {item.comparison.MaxChannelDelta}, {item.comparison.PixelsOverThreshold}/{item.comparison.PixelsCompared} pixels >1, RMS {item.comparison.RmsChannelError:F4}, alpha RMS {item.comparison.RmsAlphaError:F4}; exact mismatches {item.pixelExactComparison.PixelsOverThreshold}/{item.pixelExactComparison.PixelsCompared}.");
            }

            var report = new
            {
                schemaVersion = 2,
                generatedUtc = DateTimeOffset.UtcNow,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                cairoVersion = NativeCairoVersion(),
                nativeCairoLibrary = "libcairo.so.2 (resolved by explicit Linux-only DllImport resolver)",
                cairoAssembly = typeof(Context).Assembly.GetName().FullName,
                skiaVersion = typeof(SKBitmap).Assembly.GetName().Version?.ToString(),
                backend = "Skia raster (CPU); no GPU context or GPU evidence",
                pixelLayout = nameof(PixelLayout.Bgra8888Premultiplied),
                comparisonThresholdPerChannel = 1,
                comparisonPolicy = "Cairo repeat-render equality is required; Cairo/Skia pixel deltas are diagnostic and do not fail the run. Functional behavior requires separate layout and live-GUI validation.",
                cairoReferenceDeterministic,
                selectedFont = fontInfo,
                results,
                exitMeaning = "2 = Cairo reference renders differ; 1 = setup/render failure; 0 = artifacts generated and Cairo reference is deterministic (cross-backend deltas are informational)"
            };
            File.WriteAllText(System.IO.Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Artifacts and metrics: {output}");
            return cairoReferenceDeterministic ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Parity harness failed: {ex}");
            return 1;
        }
    }

    private static RenderResult RenderCairo(string scene, double scale, string path)
    {
        var width = (int)Math.Ceiling(LogicalWidthFor(scene) * scale);
        var height = (int)Math.Ceiling(LogicalHeight * scale);
        using var surface = new ImageSurface(Cairo.Format.Argb32, width, height);
        object? metrics = null;
        using (var context = new Context(surface))
        {
            context.Scale(scale, scale);
            metrics = DrawCairo(scene, context);
        }
        surface.Flush();
        surface.WriteToPng(path);
        return new RenderResult(PixelComparator.FromCairoArgb32(width, height, surface.Stride, surface.Data), metrics);
    }

    private static RenderResult RenderSkia(string scene, double scale, string path)
    {
        var width = (int)Math.Ceiling(LogicalWidthFor(scene) * scale);
        var height = (int)Math.Ceiling(LogicalHeight * scale);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        object? metrics;
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
                metrics = DrawSkia(scene, canvas, paint, scale, width, height);
            canvas.Flush();
        }
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(path, data.ToArray());
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new RenderResult(PixelComparator.FromSkiaBgraPremultiplied(width, height, bitmap.RowBytes, bytes), metrics);
    }

    private static object? DrawCairo(string scene, Context context)
    {
        switch (scene)
        {
            case "geometry":
                DrawGeometry((x, y, w, h, color) =>
                {
                    context.SetSourceRGBA(color.R, color.G, color.B, color.A);
                    context.Rectangle(x, y, w, h);
                    context.Fill();
                });
                return null;
            case "paths":
                context.SetSourceRGBA(0.12, 0.3, 0.8, 0.8);
                context.LineWidth = 2.25;
                context.SetDash([4, 2, 1, 3], 0.5);
                context.MoveTo(8.25, 52.5);
                context.CurveTo(20, 4, 55, 68, 86.5, 16);
                context.Stroke();
                context.SetDash([], 0);
                context.SetSourceRGBA(0.9, 0.32, 0.08, 0.65);
                context.Arc(49, 36, 13.5, 0.2, Math.PI * 1.6);
                context.ClosePath();
                context.Fill();
                return null;
            case "porter-duff":
                DrawCairoOperators(context);
                return new { operators = new[] { "Over", "Source", "In", "Out", "Atop", "Xor" } };
            case "text":
                context.SetSourceRGBA(0.12, 0.18, 0.24, 1);
                context.Rectangle(0, 0, TextLogicalWidth, LogicalHeight);
                context.Fill();
                context.SelectFontFace("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
                context.SetFontSize(17);
                var textExtents = context.TextExtents(TextSample);
                var fontExtents = context.FontExtents;
                context.SetSourceRGBA(0.08, 0.16, 0.36, 1);
                context.MoveTo(7, 39);
                context.ShowText(TextSample);
                return new { family = "DejaVu Sans", text = TextSample, textExtents = new { textExtents.XBearing, textExtents.YBearing, textExtents.Width, textExtents.Height, textExtents.XAdvance, textExtents.YAdvance }, fontExtents = new { fontExtents.Ascent, fontExtents.Descent, fontExtents.Height } };
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown fixture.");
        }
    }

    private static object? DrawSkia(string scene, SKCanvas canvas, SKPaint paint, double scale, int width, int height)
    {
        switch (scene)
        {
            case "geometry":
                DrawGeometry((x, y, w, h, color) =>
                {
                    paint.Color = ToColor(color);
                    canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
                });
                return null;
            case "paths":
                paint.Color = ToColor((0.12, 0.3, 0.8, 0.8));
                paint.StrokeWidth = 2.25f;
                paint.Style = SKPaintStyle.Stroke;
                paint.PathEffect = SKPathEffect.CreateDash([4, 2, 1, 3], 0.5f);
                using (var path = new SKPath())
                {
                    path.MoveTo(8.25f, 52.5f);
                    path.CubicTo(20, 4, 55, 68, 86.5f, 16);
                    canvas.DrawPath(path, paint);
                }
                paint.PathEffect?.Dispose();
                paint.PathEffect = null;
                paint.Color = ToColor((0.9, 0.32, 0.08, 0.65));
                paint.Style = SKPaintStyle.Fill;
                using (var path = new SKPath())
                {
                    var startDegrees = (float)(0.2 * 180.0 / Math.PI);
                    var sweepDegrees = (float)((Math.PI * 1.6 - 0.2) * 180.0 / Math.PI);
                    path.AddArc(new SKRect(35.5f, 22.5f, 62.5f, 49.5f), startDegrees, sweepDegrees);
                    path.Close();
                    canvas.DrawPath(path, paint);
                }
                return null;
            case "porter-duff":
                DrawSkiaOperators(canvas, paint, scale, width, height);
                return new { operators = new[] { "Over", "Source", "In", "Out", "Atop", "Xor" } };
            case "text":
                paint.Color = ToColor((0.12, 0.18, 0.24, 1));
                canvas.DrawRect(0, 0, TextLogicalWidth, LogicalHeight, paint);
                using (var typeface = SKTypeface.FromFile(selectedFontFile ?? throw new InvalidOperationException("No explicit font file was resolved.")) ?? throw new InvalidOperationException("Skia could not load the selected DejaVu Sans font file."))
                using (var font = new SKFont(typeface, 17))
                {
                    font.Edging = SKFontEdging.SubpixelAntialias;
                    paint.Color = ToColor((0.08, 0.16, 0.36, 1));
                    paint.Style = SKPaintStyle.Fill;
                    var measuredWidth = font.MeasureText(TextSample, paint);
                    var metrics = font.Metrics;
                    canvas.DrawText(TextSample, 7, 39, font, paint);
                    return new { family = typeface.FamilyName, text = TextSample, measuredWidth, ascent = metrics.Ascent, descent = metrics.Descent, leading = metrics.Leading };
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown fixture.");
        }
    }

    private static void DrawGeometry(Action<double, double, double, double, (double R, double G, double B, double A)> rect)
    {
        rect(8, 7, 80, 58, (0.12, 0.18, 0.24, 1));
        rect(17.25, 14.5, 34, 23, (0.9, 0.22, 0.1, 0.72));
        rect(39, 28, 37, 25, (0.1, 0.7, 0.42, 0.55));
        rect(52, 10, 12, 46, (0.25, 0.4, 0.95, 0.35));
    }

    private static void DrawCairoOperators(Context context)
    {
        var operators = new[] { Operator.Over, Operator.Source, Operator.In, Operator.Out, Operator.Atop, Operator.Xor };
        for (var i = 0; i < operators.Length; i++)
        {
            var x = 4 + i * 15;
            context.Save();
            context.Rectangle(x, 8, 14, 56);
            context.Clip();
            context.Operator = Operator.Over;
            context.SetSourceRGBA(0.12, 0.18, 0.24, 1);
            context.Paint();
            context.Operator = operators[i];
            context.SetSourceRGBA(0.9, 0.2, 0.12, 0.65);
            context.Rectangle(x + 2.25, 18, 9.5, 26);
            context.Fill();
            context.Restore();
        }
    }

    private static void DrawSkiaOperators(SKCanvas canvas, SKPaint paint, double scale, int width, int height)
    {
        var blends = new[] { SKBlendMode.SrcOver, SKBlendMode.Src, SKBlendMode.SrcIn, SKBlendMode.SrcOut, SKBlendMode.SrcATop, SKBlendMode.Xor };
        for (var i = 0; i < blends.Length; i++)
        {
            var x = 4 + i * 15;
            if (blends[i] is SKBlendMode.SrcIn or SKBlendMode.SrcOut)
            {
                // Cairo's IN and OUT are unbounded by the source mask. Build the transparent
                // source independently, then apply the clip exactly once during composition.
                using var sourceBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var sourceCanvas = new SKCanvas(sourceBitmap))
                using (var sourcePaint = new SKPaint { IsAntialias = true, Color = ToColor((0.9, 0.2, 0.12, 0.65)) })
                {
                    sourceCanvas.Clear(SKColors.Transparent);
                    sourceCanvas.Scale((float)scale);
                    sourceCanvas.DrawRect(x + 2.25f, 18, 9.5f, 26, sourcePaint);
                }

                canvas.Save();
                canvas.ResetMatrix();
                canvas.ClipRect(new SKRect((float)(x * scale), (float)(8 * scale), (float)((x + 14) * scale), (float)(64 * scale)), SKClipOperation.Intersect, antialias: true);
                using (var backgroundPaint = new SKPaint { BlendMode = SKBlendMode.SrcOver, Color = ToColor((0.12, 0.18, 0.24, 1)) })
                    canvas.DrawPaint(backgroundPaint);
                using (var compositePaint = new SKPaint { BlendMode = blends[i] })
                    canvas.DrawBitmap(sourceBitmap, 0, 0, compositePaint);
                canvas.Restore();
                continue;
            }

            canvas.Save();
            canvas.ClipRect(new SKRect(x, 8, x + 14, 64), SKClipOperation.Intersect, antialias: true);
            paint.BlendMode = SKBlendMode.SrcOver;
            paint.Color = ToColor((0.12, 0.18, 0.24, 1));
            canvas.DrawPaint(paint);
            paint.Color = ToColor((0.9, 0.2, 0.12, 0.65));
            paint.BlendMode = blends[i];
            canvas.DrawRect(x + 2.25f, 18, 9.5f, 26, paint);
            canvas.Restore();
        }
        paint.BlendMode = SKBlendMode.SrcOver;
    }

    private static SKColor ToColor((double R, double G, double B, double A) color)
    {
        var alpha = CairoByte(color.A);
        return new SKColor(
            UnpremultiplyForSkia(CairoByte(color.R * color.A), alpha),
            UnpremultiplyForSkia(CairoByte(color.G * color.A), alpha),
            UnpremultiplyForSkia(CairoByte(color.B * color.A), alpha),
            alpha);
    }

    private static int LogicalWidthFor(string scene) => scene == "text" ? TextLogicalWidth : LogicalWidth;

    private static byte CairoByte(double value) => (byte)(Math.Clamp((int)Math.Floor(Math.Clamp(value, 0, 1) * 65535 + 0.5), 0, 65535) >> 8);

    private static byte UnpremultiplyForSkia(byte premultiplied, byte alpha)
    {
        if (alpha == 0) return 0;
        var estimate = (int)Math.Round(premultiplied * 255d / alpha);
        var first = Math.Max(0, estimate - 2);
        var last = Math.Min(255, estimate + 2);
        for (var channel = first; channel <= last; channel++)
            if ((channel * alpha + 127) / 255 == premultiplied) return (byte)channel;
        return (byte)Math.Clamp(estimate, 0, 255);
    }

    private static PixelImage Crop(PixelImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > image.Width || y + height > image.Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Crop must fit inside the source image.");
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(image.Pixels, checked((y + row) * image.Stride + x * 4), pixels, row * stride, stride);
        return new PixelImage(width, height, stride, image.Layout, pixels);
    }

    private static PixelImage CropLogical(PixelImage image, double x, double y, double width, double height, double scale)
    {
        var left = (int)Math.Floor(x * scale);
        var top = (int)Math.Floor(y * scale);
        var right = (int)Math.Ceiling((x + width) * scale);
        var bottom = (int)Math.Ceiling((y + height) * scale);
        return Crop(image, left, top, right - left, bottom - top);
    }

    private static string SampleRgba(PixelImage image, int x, int y)
    {
        var offset = checked(y * image.Stride + x * 4);
        return $"R{image.Pixels[offset + 2]} G{image.Pixels[offset + 1]} B{image.Pixels[offset]} A{image.Pixels[offset + 3]}";
    }

    private static void WriteApiManifest(string path)
    {
        var assembly = typeof(Context).Assembly;
        var exportedTypes = assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var members = new List<object>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in exportedTypes)
        {
            foreach (var member in type.GetMembers(flags).Where(IsPublicOrProtected).OrderBy(MemberSignature, StringComparer.Ordinal))
            {
                members.Add(new
                {
                    declaringType = type.FullName,
                    signature = MemberSignature(member),
                    classification = Classify(member),
                    classificationReview = "required"
                });
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow,
            assemblyIdentity = assembly.GetName().FullName,
            targetFramework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName,
            exportedTypeCount = exportedTypes.Length,
            publicAndProtectedMemberCount = members.Count,
            classificationNote = "Automated initial classification; every row requires human review before ABI compatibility is claimed.",
            types = exportedTypes.Select(type => new
            {
                name = type.FullName,
                kind = type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsInterface ? "interface" : "class",
                baseType = type.BaseType?.FullName,
                enumValues = type.IsEnum ? Enum.GetNames(type).Select(name => new { name, value = Convert.ToInt64(Enum.Parse(type, name)) }) : null
            }),
            members
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote public/protected ABI inventory: {exportedTypes.Length} types, {members.Count} members, assembly {assembly.GetName().FullName}.");
    }

    private static bool IsPublicOrProtected(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(accessor => accessor.IsPublic || accessor.IsFamily || accessor.IsFamilyOrAssembly),
        EventInfo eventInfo => new[] { eventInfo.AddMethod, eventInfo.RemoveMethod, eventInfo.RaiseMethod }.Where(method => method is not null).Any(method => method!.IsPublic || method.IsFamily || method.IsFamilyOrAssembly),
        _ => false
    };

    private static string MemberSignature(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => $"{(constructor.IsStatic ? "static " : "")}{constructor.DeclaringType?.FullName}({string.Join(", ", constructor.GetParameters().Select(ParameterSignature))})",
        MethodInfo method => $"{method.ReturnType.FullName} {method.Name}{GenericArity(method)}({string.Join(", ", method.GetParameters().Select(ParameterSignature))})",
        PropertyInfo property => $"{property.PropertyType.FullName} {property.Name}{IndexParameters(property)} [{string.Join(",", property.GetAccessors(nonPublic: true).Where(IsPublicOrProtected).Select(accessor => accessor.Name))}]",
        EventInfo eventInfo => $"event {eventInfo.EventHandlerType?.FullName} {eventInfo.Name}",
        FieldInfo field => $"{(field.IsLiteral ? "const " : "field ")}{field.FieldType.FullName} {field.Name}{(field.IsLiteral ? $" = {field.GetRawConstantValue()}" : "")}",
        _ => $"{member.MemberType} {member.Name}"
    };

    private static string ParameterSignature(ParameterInfo parameter) =>
        $"{(parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : "")}{(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType)?.FullName}{(parameter.IsOptional ? " = optional" : "")}";

    private static string IndexParameters(PropertyInfo property) => property.GetIndexParameters().Length == 0
        ? ""
        : $"[{string.Join(", ", property.GetIndexParameters().Select(ParameterSignature))}]";

    private static string GenericArity(MethodInfo method) => method.IsGenericMethodDefinition ? $"`{method.GetGenericArguments().Length}" : "";

    private static string Classify(MemberInfo member)
    {
        var name = member.Name;
        var declaringType = member.DeclaringType?.Name ?? "";
        if (name.Contains("Data", StringComparison.OrdinalIgnoreCase) || name is "Handle" or "GetTarget" or "SetTarget" or "GetSource" or "CopyPath" or "AppendPath")
            return "materialization-boundary";
        if (name.Contains("Extents", StringComparison.OrdinalIgnoreCase) || name.Contains("Measure", StringComparison.OrdinalIgnoreCase) || name.StartsWith("In", StringComparison.Ordinal))
            return "measurement";
        if (member is FieldInfo || member.DeclaringType?.IsEnum == true || member is PropertyInfo && declaringType is not "Context")
            return "metadata";
        if (declaringType is "Context" or "Path" or "Matrix" or "Pattern" || member is MethodInfo method && method.Name.StartsWith("set_", StringComparison.Ordinal))
            return "recordable-candidate";
        return "native-only-pending";
    }

    private static string NativeCairoVersion()
    {
        var pointer = cairo_version_string();
        return Marshal.PtrToStringAnsi(pointer) ?? throw new InvalidOperationException("Native Cairo returned a null version string.");
    }

    private static object ResolveFontInfo()
    {
        var matched = RunTool("fc-match", "-f", "%{family}|%{style}|%{file}", "DejaVu Sans").Split('|');
        if (matched.Length != 3 || string.IsNullOrWhiteSpace(matched[2]) || !File.Exists(matched[2]))
            throw new InvalidOperationException("fontconfig did not resolve the requested DejaVu Sans face to an installed file.");
        selectedFontFile = matched[2];
        var version = RunTool("fc-query", "-f", "%{fontversion}", matched[2]);
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("fontconfig returned no font version for the selected face.");
        return new { requested = "DejaVu Sans", family = matched[0], style = matched[1], file = matched[2], fontVersion = version };
    }

    private static string RunTool(string fileName, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Could not start required metadata tool '{fileName}'.");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000)) throw new TimeoutException($"Metadata tool '{fileName}' did not finish within five seconds.");
        if (process.ExitCode != 0) throw new InvalidOperationException($"Metadata tool '{fileName}' exited with code {process.ExitCode}.");
        return output.Trim();
    }

    private static IntPtr ResolveCairo(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(libraryName, "libcairo-2", StringComparison.Ordinal) ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero;

    private static void WriteDifference(string path, PixelImage expected, PixelImage actual)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(expected.Width, expected.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var offset = y * expected.Stride + x * 4;
            var delta = Math.Max(Math.Max(Math.Abs(expected.Pixels[offset] - actual.Pixels[offset]), Math.Abs(expected.Pixels[offset + 1] - actual.Pixels[offset + 1])),
                Math.Max(Math.Abs(expected.Pixels[offset + 2] - actual.Pixels[offset + 2]), Math.Abs(expected.Pixels[offset + 3] - actual.Pixels[offset + 3])));
            bitmap.SetPixel(x, y, new SKColor((byte)delta, 0, 0, 255));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    [DllImport("libcairo.so.2", EntryPoint = "cairo_version_string")]
    private static extern IntPtr cairo_version_string();
}
