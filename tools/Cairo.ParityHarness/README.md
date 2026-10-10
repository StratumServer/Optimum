# Cairo/Skia raster parity harness

This is a Linux-only evidence tool for Optimum issue #130. It renders geometry/alpha, dashed paths and curves, nine GUI-used Cairo operators, and a DejaVu Sans text sample at scales 1.0, 1.5, and 2.0 through the current native Cairo wrapper and SkiaSharp's CPU raster backend. The operator cells cover Over, Source, In, Out, Atop, Xor, Clear, DestOver, and HardLight, with each Cairo-to-Skia blend mapping recorded in the JSON fixture metrics. For every scene and scale it writes Cairo and Skia PNGs, a repeated Cairo image, a red-channel difference PNG, and comparator metrics in JSON. Operator results are also reported per cell so a mismatch cannot hide inside the aggregate. It records Cairo and Skia text metrics separately. It does not create a GPU context and provides no GPU-performance evidence.

`gui-fixture-manifest.csv` inventories Cairo-backed GUI composition overrides from the API, Essentials, and Survival source trees. Regenerate it after source changes with `python3 tools/Cairo.ParityHarness/generate_gui_manifest.py`. Rows start as `not-rendered`; source discovery alone does not count as a rendered fixture.

`primitive-coverage-manifest.csv` records the operations exercised by the CPU harness and the fixtures still needed before considering those operations for a non-Cairo backend. `not-run` rows stay on the native Cairo path.

Recorded solid colors without an additional opacity mask use Cairo's premultiplied 16-bit-to-8-bit quantization before Skia rendering. This preserves the source bytes consumed by blur; a one-level source difference can become much larger under native partial-blur byte wrapping. `PaintWithAlpha` with opacity other than one materializes and replays through native Cairo for `Clear`, `Source`, `In`, `Out`, `DestIn` and `DestAtop`. These operators are not bounded by source, so scaling source alpha cannot replace their coverage mask. Gradient and other masked-operator comparisons remain separate compatibility diagnostics.

Generate the Cairo assembly's exported type and public/protected member inventory with:

```bash
dotnet run -c Release --project tools/Cairo.ParityHarness/Cairo.ParityHarness.csproj -- --api-manifest tools/Cairo.ParityHarness/cairo-api-manifest.json
```

When the Vintage Story font directory is available, capture the bundled font and scale matrix with:

```bash
dotnet run -c Release --project tools/Cairo.ParityHarness/Cairo.ParityHarness.csproj -- --font-matrix /path/to/assets/game/fonts "$TMPDIR/optimum-issue-130-font-matrix"
```

This mode builds a temporary Fontconfig file for the supplied font directory, verifies that Cairo resolves each requested family/style to the matching TTF, and records Cairo and Skia text metrics, codepoint advances, PNGs, and pixel deltas for every face at scales 1.0, 1.5, and 2.0. The mode is Linux-only and keeps Cairo authoritative for layout. Text deltas are diagnostic; this harness does not test `TextDrawUtil` wrapping or GUI hitboxes.

The inventory includes assembly identity, enum values, signatures, and an initial member classification. Every classification is marked for human review; this inventory alone does not establish old-binary compatibility.

Prerequisites: .NET 10 and the same native `libcairo.so.2` dependency used by the Linux game installation. A missing library or failed Cairo render is a failed run.

From the Optimum repository root:

```bash
dotnet run --project tools/Cairo.ParityHarness/Cairo.ParityHarness.csproj -- "$TMPDIR/optimum-issue-130-parity"
dotnet test Optimum.Tests/Optimum.Tests.csproj --filter FullyQualifiedName~CairoPixelComparatorTests
bash scripts/test-cairo-abi.sh /path/to/installed/VintageStory/Lib/cairo-sharp.dll
```

The ABI fixture compiles once against the supplied pre-existing game assembly, then replaces only the fixture's runtime copy with the candidate and exercises surface creation, drawing, text metrics, pixel access, and PNG output. It never overwrites the installed game.

Cross-backend differences are diagnostic; they do not by themselves indicate a functional failure. The harness models Cairo's unbounded `In` and `Out` by drawing the source into a full-size transparent Skia bitmap, then applying the clip once while compositing. It antialiases fractional clip edges, paints the destination across the clip, and quantizes source colors using Cairo's premultiplied 16-bit-to-8-bit conversion before handing them to Skia. Each operator cell reports the Cairo self-comparison, Cairo/Skia metrics at threshold one, and exact pixel metrics; the aggregate scene does the same. Intentional comparator counterexamples remain covered by `CairoPixelComparatorTests` (fill, blend, translation, and alpha conversion). Operator crops use floor/ceiling device bounds so fractional edge pixels are included. Text uses a 160-unit-wide opaque fixture and requests Skia subpixel edging; it no longer clips the sample at the right edge. The latest nine-operator aggregate maximum channel deltas at scales 1/1.5/2 are 1/8/1; the per-cell results are preserved in the generated metrics. Geometry, path and text diagnostics remain 2/3/2, 52/55/41 and 31/32/32 respectively. The run writes its metrics to `<output-dir>/metrics.json`. Cairo self-renders remain byte-identical. Functional acceptance is based on layout/measurement stability, visible content, clipping, transparency, and live interaction checks. Exit code `2` means repeated Cairo reference renders differ; `1` means setup or rendering failed. Cairo/Skia pixel deltas alone do not affect the exit code.


## Opt-in recorder preparation timings

The runtime backend `Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend` exposes `RecordingProfilingEnabled`, disabled by default independently of GPU rendering. Enable it only for a diagnostic measurement, call `ResetRecordingProfiling()` between workloads with no profiled operation in flight, then read the `RecordingShadow*`, `RecordingDrawingCapture*`, `RecordingPathCopy*`, `RecordingPathConversion*` and `RecordingStrokeOutline*` counters. Divide tick fields by `RecordingProfileTimestampFrequency` to obtain seconds. Restore the previous enabled state afterward. Reset preserves the enabled state; disabling preserves collected values.

These counters are process-wide cumulative elapsed durations, not thread CPU time or GPU time. Shadow duration includes lazy context creation and pending state-command replay; the command counter counts completed replays. Drawing capture includes path copying/conversion, paint/source capture and stroked-outline preparation. Path-copy duration includes native copying and conversion; conversion measures only the native-path-to-Skia loop. Stroke-outline duration measures `GetFillPath` preparation and setup. Nested durations must not be summed into a total. Counts include attempted operations, including exceptional exits; they retain no text or dialog content. Text measurement and `TextPath` construction before drawing capture are outside this small profiling slice. Diagnostic timings add clock and atomic-counter overhead and require an uninstrumented control run. Disabled scopes allocate no diagnostic object, read no clock and update no counters.

`GetRecordingStageDiagnostics()` also returns `<Stage>Count`/`<Stage>Ticks` for `CaptureState`, `SourceCapture`, `SourceShader`, `SourceFreeze`, `TextPreparation`, `TextMeasurement`, `FontSelection`, `GpuBlur`, `GpuReplay` and `GpuSubmission`. Text preparation measures the cache attempt; native text/font measurement remains authoritative. Source shader/freeze timings nest within source capture; blur nests within replay. Flush/submission uses asynchronous `Submit(false)`: these are host durations, not completed GPU execution times. Layout attribution requires a separate caller-level measurement.

Cairo allocation tracing is disabled when `CAIRO_DEBUG_DISPOSE` is unset or `0`. Other present values enable it, preserving the existing opt-in convention. Cairo does not rewrite this environment variable. The public `CairoDebug.Enabled` field can still enable tracing after initialization; tracing captures allocation stack traces and must be disabled in performance controls unless it is the workload being measured. Fresh-process startup tests cover environment preservation and later public-field enablement.

## Immutable preparation reuse

GPU recording reuses identical text/glyph outlines and advances, native-to-Skia path conversion, stroked outlines and SVG pictures. Keys compare complete input values, including the retained scaled font, transform, position, stroke/dash settings and SVG content/dimensions. Mutable paints, sources, clips and mod callbacks are evaluated for every draw. `ComposeElements`, `BeforeCalcBounds` and `OnComposed` still run on every recomposition; legacy Cairo commands and native reference replay remain supported. Empty or malformed text, unsuccessful contexts, user fonts and oversized inputs bypass text preparation reuse.

The shared LRU retains at most 1,024 entries with a 16 MiB **estimated** preparation-resource budget and a 128 KiB key limit. The estimate includes geometry and keys, but is not an exact process-memory limit: native scaled-font internals may use additional memory. Eviction and clearing release cache ownership; active recorded streams retain their own references. Paths are copied before mutation.

For matched controls, set `OptimumGuiGpuProbeBackend.RecordingPreparationReuseEnabled` before warmup, call `ClearRecordingPreparationCache()` before each independent workload, then `ResetRecordingPreparationCounters()` after warmup. Read `RecordingPreparationCacheHits/Misses/Evictions`, the text/path/stroke/SVG hit counters, entry count and estimated bytes. Resetting counters preserves cached resources. Restore the original enabled state afterward. Profiling remains a separate opt-in diagnostic; its conversion/stroke counts now count actual preparation misses. Compare unprofiled wall time, thread CPU time and allocations before claiming a performance improvement. These caches reduce preparation, not layout or arbitrary mod callback execution, and do not remove Cairo.
