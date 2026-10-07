# Cairo/Skia raster parity harness

This is a Linux-only evidence tool for Optimum issue #130. It renders geometry/alpha, dashed paths and curves, six Porter-Duff operators, and a DejaVu Sans text sample at scales 1.0, 1.5, and 2.0 through the current native Cairo wrapper and SkiaSharp's CPU raster backend. For every scene and scale it writes Cairo and Skia PNGs, a repeated Cairo image, a red-channel difference PNG, and comparator metrics in JSON. Porter-Duff results are also reported per operation so a mismatch cannot hide inside the aggregate. It records Cairo and Skia text metrics separately. It does not create a GPU context and provides no GPU-performance evidence.

`gui-fixture-manifest.csv` inventories Cairo-backed GUI composition overrides from the API, Essentials, and Survival source trees. Regenerate it after source changes with `python3 tools/Cairo.ParityHarness/generate_gui_manifest.py`. Rows start as `not-rendered`; source discovery alone does not count as a rendered fixture.

`primitive-coverage-manifest.csv` records the operations exercised by the CPU harness and the fixtures still needed before considering those operations for a non-Cairo backend. `not-run` rows stay on the native Cairo path.

Generate the Cairo assembly's exported type and public/protected member inventory with:

```bash
dotnet run -c Release --project tools/Cairo.ParityHarness/Cairo.ParityHarness.csproj -- --api-manifest tools/Cairo.ParityHarness/cairo-api-manifest.json
```

The inventory includes assembly identity, enum values, signatures, and an initial member classification. Every classification is marked for human review; this inventory alone does not establish old-binary compatibility.

Prerequisites: .NET 10 and the same native `libcairo.so.2` dependency used by the Linux game installation. A missing library or failed Cairo render is a failed run.

From the Optimum repository root:

```bash
dotnet run --project tools/Cairo.ParityHarness/Cairo.ParityHarness.csproj -- "$TMPDIR/optimum-issue-130-parity"
dotnet test Optimum.Tests/Optimum.Tests.csproj --filter FullyQualifiedName~CairoPixelComparatorTests
bash scripts/test-cairo-abi.sh /path/to/installed/VintageStory/Lib/cairo-sharp.dll
```

The ABI fixture compiles once against the supplied pre-existing game assembly, then replaces only the fixture's runtime copy with the candidate and exercises surface creation, drawing, text metrics, pixel access, and PNG output. It never overwrites the installed game.

Cross-backend differences are diagnostic; they do not by themselves indicate a functional failure. The harness models Cairo's unbounded `In` and `Out` by drawing the source into a full-size transparent Skia bitmap, then applying the clip once while compositing. It antialiases fractional clip edges, paints the destination across the clip, and quantizes source colors using Cairo's premultiplied 16-bit-to-8-bit conversion before handing them to Skia. It compares both the strict threshold of one channel value and exact equality, and crops operator cells using floor/ceiling device bounds so fractional edge pixels are included. Text uses a 160-unit-wide opaque fixture and requests Skia subpixel edging; it no longer clips the sample at the right edge. Latest diagnostic deltas at scales 1/1.5/2: geometry max 2/3/2; paths 52/55/41; operators 1/4/1; text 31/32/32. The run writes its metrics to `<output-dir>/metrics.json`. Cairo self-renders remain byte-identical. Functional acceptance is based on layout/measurement stability, visible content, clipping, transparency, and live interaction checks. Exit code `2` means repeated Cairo reference renders differ; `1` means setup or rendering failed. Cairo/Skia pixel deltas alone do not affect the exit code.
