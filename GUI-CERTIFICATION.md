# GUI certification before client entry

The launcher reads `DATA/.optimum/gui-certification.json` and its companion
`gui-certification-evidence.json` after patch/cache resolution and before
`ClientProgram.Main`. Missing, invalid, negative or incompatible evidence selects
vanilla GUI composition. The game continues launching. The environment/config GPU
request cannot approve a certificate. The launcher clears approval before every
validation; the runtime gate starts denied, also for direct client launches.

The private runner resolves the runtime fingerprint through the patched launcher
before and after the isolated Pharos test. It issues local evidence only after
that test exits successfully with exactly one pass and the runtime fingerprint
stays unchanged. There is no launcher command that accepts a caller-supplied
success code to issue approval. The launcher validates the certificate before
`ClientProgram.Main`, runs a hidden OpenGL probe in a child process with an
eight-second deadline, and requires exact vendor, renderer, GL version, major,
minor, context profile mask, and context flags. Missing display, native failure,
timeout, software renderer, incomplete output, or any mismatch selects vanilla.
The real game context repeats the same comparison before every candidate texture;
a mismatch revokes approval for that launch. GL context profile identity requires
OpenGL 3.2 or newer. The existing Skia readback/state probe remains required.

The certificate is a local review artifact, not a cryptographic authorization
boundary against code running as the user. The private wrapper derives checkout
HEAD IDs from Git and records loaded artifact hashes; the hashes identify the
actual staged binaries even when a checkout has local edits. `DriverVersion` is
optional diagnostic text and is not trusted or compared. The live GL version,
vendor, renderer and context profile are compared, but these strings do not
provide physical-device UUID attestation. A certificate applies only to the
local staged runtime/profile. Do not copy certificates between machines.

The certificate is a local review artifact, not a cryptographic authorization
boundary against code running as the user. Schema 1 uses these exact property
names: `SchemaVersion`, `Policy`, `ProfileId`, `RuntimeIdentifier`,
`RuntimeFingerprint`, `EvidenceSha256`. Policy and profile are both
`exact-local-gui-v1`; hashes are SHA-256 hexadecimal strings. JSON is bounded to
1 MiB; duplicate properties and unknown certificate fields are rejected.

The fingerprint binds launcher arguments, runtime identifier, root runtime
assemblies/configuration, Lib, Mods, assets, patched cache contents and datapath
Mods/ModConfig/assets/clientsettings.json. File additions/removals change it;
linked inputs are unsupported. Evidence must identify the exact staged Lib/API,
Cairo and Skia dependencies and pass initial composition, recomposition, scales
1/1.5/2, primitive and non-subset fixtures, SVG GPU coverage and observer checks.
Counters must show positive successful candidates and no unrecognized candidate
fallbacks or native materializations. The only certified native fallback is a
successful `Recorder.ShowText.Unsupported:text-antialias-Subpixel` replay. It
keeps that surface on Cairo while other eligible surfaces continue through Skia.
Changing only the decision string cannot approve evidence.

The evidence also carries the runtime/profile fingerprint captured before the
Pharos process; launcher validation requires it to match the certificate and
current profile. The wrapper removes old approval first, enforces a 15-minute
test deadline, and removes a newly written certificate if launcher validation
fails. This is fail-closed operational binding, not protection against a
malicious process with the same user's filesystem access.

The current evidence corpus is an inventory/fixture profile, not exhaustive GUI
coverage. Arbitrary mod callbacks, external paths, future GUI states, native
escapes, and device/context changes cannot be universally certified.
`--validate-only` continues to validate assemblies without creating an OpenGL
context, granting GUI approval or starting the client.

Cairo still owns the public GUI drawing API, context state, glyph IDs and glyph
positions. The runtime API patcher now routes `CairoFont.GetTextExtents` and
`GetFontExtents` through Skia metrics, using the same Fontconfig face match as the
text recorder. The runtime patcher also routes `TextDrawUtil` font-based
`Lineize` and `GetQuantityTextLines` through those Skia metrics, without creating
temporary Cairo contexts. `CairoFont.FontMeasuringContext`, `SetupContext`, and
the context-based `TextDrawUtil` overloads remain Cairo-backed. Compatible Gray
text and the tested Subpixel matrix rasterize as Skia text blobs. Unsupported Gray
faces keep Cairo outlines; unsupported Subpixel faces materialize the original
call through Cairo. The tested matrix and remaining limits are recorded in Stage
B3 through Stage D below.

Policy: preserve the requested antialias mode. `CairoFont.SetupContext` requests
`Antialias.Subpixel` for GUI text. Skia LCD rendering passes the local visual policy
for the tested regular `sans-serif` and `Montserrat` fixtures. Other font cases
retain Cairo's exact Subpixel output through the already certified per-surface
fallback, which does not revoke GPU approval for other surfaces. Gray text stays
on Cairo-derived outlines because direct Skia rasterization exceeded local visual
parity in five fixtures. All other native materializations, failed Subpixel
replays, and unsupported GPU fallbacks still revoke approval. Regression tests
cover the Skia LCD subset, exact Cairo fallbacks, and unchanged Cairo Gray output.

Certification also requires the live GL `Antialias.Best` rectangle fixture to read
back from the candidate GPU texture and match the native Cairo pixels within one
channel value. The assertion runs inside the Pharos test before its evidence can be
written; the report records that result and launcher validation requires it.

## SVG rasterization coverage

NanoSVG still parses each document. Visible solid fills (non-zero and even-odd)
and solid strokes, including fill and shape opacity, are rasterized by Skia's CPU
rasterizer. The raster then goes through NanoSVG's integer unpremultiply and
defringe steps and the tint or red/blue conversion of `SvgLoader.rasterizeSvg`, so
callers receive the same straight-alpha RGBA bytes. `DrawSvg` into a recording
surface records the original `ColorOver` loop over these bytes as a pixel step;
native materialization replays the original NanoSVG route. `LoadSvg` writes the
bytes into a candidate texture through the certified GPU path. Gradients and
dashed strokes continue through NanoSVG. The focused tests compare the bytes with
NanoSVG: flat pixels within one channel value, antialiased edges within the GUI
edge policy.

Run the CPU parser and parity tests with the platform's Vintage Story native
library directory available to the loader. This checks the CPU rasterization
only. It does not certify GPU output on Intel, AMD or NVIDIA.

```sh
export VINTAGE_STORY=/path/to/vintagestory
LD_LIBRARY_PATH="$VINTAGE_STORY/Lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet test Optimum.Tests/Optimum.Tests.csproj -c Release \
  --filter FullyQualifiedName~CairoSvgRecorderTests
```

On Windows, add `Vintagestory\Lib` to `PATH` before running the same
`dotnet test` command. After this test passes, run the private Pharos GPU suite
on each physical adapter using the Intel and NVIDIA steps below. Keep each
machine's report separate and let the exact-device certification gate decide
whether GPU rendering is eligible.

## Intel and NVIDIA verification

Use the same protocol independently on physical Intel and NVIDIA adapters;
record actual GL identity and driver, OS, staged runtime hashes, harness commit,
profile configuration and datapath. Do not report a software renderer or a
successful run on another adapter as vendor coverage.

The full inventory scenario lives in the private workspace. Apply the scenario
gate shipped on this branch from the private workspace root before running it:

```sh
git apply --check /path/to/optimum/docs/Pharos/issue-130-subpixel-fallback-gate.patch
git apply /path/to/optimum/docs/Pharos/issue-130-subpixel-fallback-gate.patch
```

The gate accepts only successful `Recorder.ShowText.Unsupported:text-antialias-Subpixel`
materializations with the matching `surface-not-recording:NativeMaterialized:first-trigger=Recorder.ShowText.Unsupported:text-antialias-Subpixel`
fallback. It also requires the fallback histogram sum to equal the fallback
counter for initial composition, recomposition, and every scale. The branch's
launcher validator applies the same checks to the report before approving the
local device profile.

Run the committed standalone Pharos diagnostic on each physical adapter using
the commands in `Optimum.Pharos.SvgDiagnostic/README.md`. It calls the patched
public `SvgLoader.LoadSvg` method and compares complete GPU texture readbacks
with the native NanoSVG route at 1x, 1.5x and 2x texture sizes. Each run writes
a separate JSON report with the live GL identity, context and loaded assembly
hashes. The diagnostic grants the live identity in memory, revokes it at the
end of the test, and never writes a certificate.

On Windows, also run the private full GUI suite against the staged candidate
in a separate evidence directory for each adapter:

```powershell
.\run-gui.ps1 -OptimumRoot <candidate-checkout> `
  -OutputRoot <adapter-specific-evidence-directory> `
  -Filter FullyQualifiedName~NanoSvgNativeCompatibility
```

That private runner writes the test log, TRX results, runtime manifest and
`svg-parity.json`. Keep those files with the physical adapter's report. Its
SVG matrix exercises the recorder fixtures directly and supplements the
standalone public-route diagnostic. For a supported solid SVG (tiled,
translucent, tinted or stroked), require one candidate attempt, one success and
zero fallbacks. For gradients and dashed strokes, require zero candidate
attempts and exact native fallback bytes. A passing CPU parser test or an in-memory identity
qualification is diagnostic only; neither produces an approved hardware
certificate.

1. Build and stage the exact candidate runtime. Use a disposable datapath/world
   and retain its configuration. Keep the authenticated user datapath intact.
2. Run the private Pharos GUI suite against that stage. Capture the startup
   observer, initial composition, recomposition, scales 1/1.5/2, primitive,
   non-subset, SVG, GL state and loaded-assembly provenance evidence. Keep native
   diagnostic references/readbacks outside production measurement windows.
3. Apply a process deadline and bounded repetitions. Run one GPU process at a
   time; stop on context loss, driver reset, hang or unexplained GL errors.
4. Verify launcher behavior with absent certificate, malformed JSON, synthetic
   negative AMD evidence, changed evidence bytes, changed cached DLL, changed
   mod/configuration and a cache hit. Each must select vanilla while allowing
   normal client entry. Keep measured reports unchanged.
5. With a complete synthetic report, verify exact live identity match, each
   vendor/renderer/version mismatch, missing display, software renderer and
   timeout. Each failure must select vanilla and allow normal client entry.
   Keep negative cases synthetic; do not hand-edit measured reports into approvals.

Local regression commands:

```sh
dotnet test Optimum.Launcher.Tests/Optimum.Launcher.Tests.csproj
dotnet test Optimum.Tests/Optimum.Tests.csproj --filter FullyQualifiedName~OptimumGuiGpuProbeTests
make check-patches
make check-shaders
make patch-il
```

A passing unit suite proves certificate rejection, input binding and runtime
eligibility logic. It does not constitute Intel/NVIDIA/AMD hardware evidence.

## Stage A Subpixel fallback fix, AMD retest (2026-10-17)

The earlier run on `70f626a35329d8f3a284569358816e95bfbad680` selected `vanilla` after the first successful Subpixel text replay revoked GPU eligibility for every GUI surface. The recorder now keeps Cairo's Subpixel rendering on the surface that needs it while allowing separate surfaces to use Skia. The launcher accepts only successful `Recorder.ShowText.Unsupported:text-antialias-Subpixel` materializations and the matching `surface-not-recording:NativeMaterialized:first-trigger=Recorder.ShowText.Unsupported:text-antialias-Subpixel` candidate fallback. It rejects all other causes, failed materializations, missing counters, or unbalanced candidate totals.

I rebuilt the staged Vintage Story 1.22.7 runtime from the candidate branch and reran the full inventory scenario on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core. The scenario passed and selected `skia-with-subpixel-cairo-fallback` with no decision reasons. The launcher accepted the local profile certificate. Initial composition recorded 38 candidate attempts, 19 Skia successes, and 19 allowed Subpixel fallbacks. Ten recompositions recorded 360 attempts, 144 successes, and 216 allowed fallbacks. Scales 1, 1.5, and 2 recorded respectively 12/30, 108/171, and 109/173 candidate successes/attempts; all remaining candidates fell back only for successful Subpixel text materialization. There were no materialization failures. The primitive, all five non-subset fixtures, and all three SVG comparisons passed; SVG output remained within one channel value.

The report used the local private Pharos checkout at `4966732269f02408df314ae155bb7e6f3817f9d9`. The harness changes used for this run are included as an applyable patch in this branch. After adding fallback histogram-to-counter equality checks, I reran the scenario against Optimum commit `5931c3cabe3bd59712986baf9bc02ea5971aa8b8`: it passed 1/1 with no decision reasons, balanced counters in every phase, and launcher certificate validation. Intel and NVIDIA physical runs remain required. Their earlier `7bba055` reports predate the Subpixel policy change and do not validate this behavior. Use the same exact fallback allowlist and launcher certificate validation on each adapter. No GPU performance gain is claimed by this functional scenario.

## Stage A Intel and NVIDIA certification (2026-10-17)

On Optimum commit `8b915e5b72071f590f3a4685f4a71dbe8d6d7def`, the full inventory GUI scenario passed twice in separate processes on each physical adapter. Intel used UHD 620 with Mesa 26.0.8-1ubuntu0.3 and OpenGL 4.6 Core. NVIDIA used GeForce MX150 with driver 580.178.04 and OpenGL 4.3.0. Each run selected `skia-with-subpixel-cairo-fallback`, reported no decision reasons, and passed local launcher certificate validation. All fixture families passed, with no materialization failures. Initial composition recorded 19 Skia successes and 19 allowed Subpixel fallbacks. Ten recompositions recorded 144 successes and 216 allowed fallbacks. Scales 1, 1.5 and 2 recorded 12/30, 108/171 and 109/173 successes/attempts. The three SVG comparisons passed within one channel value. None of the four GUI logs contained a `Cairo.ScaledFont is leaking` warning.

The separate public Pharos SVG diagnostic passed all 14 cases on each adapter using Pharos v0.5.0. Its 12 supported solid SVG calls each used one candidate attempt and success with no fallback; maximum observed channel delta was 17, within the diagnostic's edge policy. Gradient and dashed-stroke cases used exact native output. This diagnostic does not issue a hardware certificate.

The resource fix disposes recorded Cairo scaled fonts and Skia paths when recorded drawing commands are cleared or released. After the fix, bootstrap and patch validation passed with 130 applied patches and no failures; shader validation passed; build completed with zero warnings and errors; IL patching reported 146/146 required methods. Test suites passed: Optimum 986 (34 skipped), Launcher 65 (4 skipped), CLI 24, Bootstrap.Core 156 and Installer 78. The Intel GPU-disabled control also passed the inventory scenario and selected `vanilla`. `make check` could not run because PowerShell (`pwsh`), required by packaging checks, is not installed. No GPU performance gain is claimed by these functional results.


## Stage B Subpixel raster migration, AMD retest (2026-10-17)

Subpixel glyphs that pass the local font/weight matrix now use Skia LCD text blobs. Cairo still provides glyph IDs, positions, current-point advance, face family and style. The Skia route resolves `sans-serif` and `Montserrat` through the same Fontconfig configuration Cairo uses, loads the matched font file/index, positions those Cairo glyphs in device space, and avoids Cairo outline construction. Bold faces and Lora/Almendra remain exact per-surface Cairo fallbacks because their local LCD comparison exceeded the 96 filtered-difference limit. Gray text remains on Cairo outlines. An attempted direct Skia Gray raster failed five existing local-AA fixtures and was reverted.

The branch regression passed all 110 `CairoRecorder` tests, then all five project suites: Optimum 991 passed/34 skipped, Launcher 69, CLI 24, Bootstrap.Core 156, and Installer 78. Build completed with zero warnings and errors. Patch validation reported 81 applied source patches, 53 Cecil patches, and 22 runtime donors compiled; shader validation and `make check` passed.

The full Pharos inventory scenario passed on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core. The launcher accepted the staged profile certificate and the report selected `skia-with-subpixel-cairo-fallback` with no decision reasons. Initial composition recorded 38/38 GPU candidates and recomposition 360/360. GUI scales 1, 1.5, and 2 recorded 30/30, 159/171, and 161/173 successes. The remaining 24 scale candidates were successful Subpixel Cairo fallbacks; all eight materializations succeeded, with zero failures. The primitive, five non-subset fixtures, and SVG comparisons passed. The initial local source run preceded publication; I repeated it after publishing commit `99696d23ff4a9c82c0db386adb626574c752594d`, and the report now records that exact Optimum commit with the same pass result. Both reports use the local Pharos checkout at `4966732269f02408df314ae155bb7e6f3817f9d9`; that checkout has uncommitted harness changes, so the scenario is a successful AMD runtime retest with this reproducibility limitation. Intel and NVIDIA must run this exact change before the previous adapter reports can cover it. No performance gain is claimed.

### Stage B2: wider Skia LCD font matrix (2026-10-17)

Skia text hinting now uses `Slight`, which passed the fixed local LCD policy for the existing regular `sans-serif` and `Montserrat` fixtures and three additional cases: Lora normal and bold, and Almendra normal. The recorder still gets face selection, glyph IDs and positions from Cairo, but these five tested face/weight cases now rasterize through Skia text blobs without Cairo glyph outlines. `sans-serif` bold remains on the exact per-surface Cairo fallback because its filtered difference was 98.5, above the fixed limit of 96. Gray text and untested faces remain on Cairo. The focused `CairoRecorderTextTests` passed 23/23 and the full `Optimum.Tests` project passed 991 with 34 existing skips. Build passed with zero warnings and errors; patch checks reported 81 source, 53 Cecil and 22 runtime donor patches; shader validation passed.

The staged Pharos inventory scenario passed 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core, against Optimum commit `c27c19daefdea1557ba05e5546eac8bc46d1ed37`. It selected `skia-with-subpixel-cairo-fallback` with no decision reasons, and the launcher validated the local profile certificate. Initial composition was 38/38 GPU candidates, ten recompositions were 360/360, and scales 1, 1.5 and 2 recorded 30/30, 159/171 and 161/173. The 24 scale fallbacks and all 8 materializations were successful Subpixel Cairo fallbacks. The primitive, five non-subset fixtures and three SVG comparisons passed. The Pharos checkout was dirty at `4966732269f02408df314ae155bb7e6f3817f9d9`, so note this harness reproducibility limitation. Intel and NVIDIA still need fresh runs against this behavior. No performance gain is claimed.

### Stage B3: Gray text raster migration (2026-10-17)

Compatible Gray text cases now rasterize as Skia antialiased text blobs. Cairo continues to provide font selection, glyph IDs, positions and current-point advances. The tested matrix covers `sans-serif` normal and bold, `Montserrat` normal and bold, `Lora` normal and bold, and `Almendra` normal at the listed fixture sizes in `CairoRecorderTextTests`; these cases no longer build Cairo glyph outlines. Unsupported Gray faces such as `serif` keep the Cairo outline path. Subpixel text keeps the existing Skia LCD matrix and exact per-surface Cairo fallback for unsupported combinations. This changes rasterization only; text shaping and layout remain Cairo-owned, and untested sizes, scripts and platform font configurations remain unqualified.

The focused `CairoRecorderTextTests` passed 24/24. The full `Optimum.Tests` suite passed 991 with 34 existing skips after the Gray raster change; the final added fallback assertion also passed in the focused suite. Build completed with zero warnings and errors, patch validation reported 81 source patches, 53 Cecil patches and 22 runtime donors, shader validation passed, and IL patching reported 146/146 required methods.

The full staged Pharos inventory scenario passed 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core, against Optimum commit `e425e3d9b3fd2668a4772542e80dfeb3c57dee34`. It selected `skia-with-subpixel-cairo-fallback` with no decision reasons, and the launcher validated the local profile certificate. Initial composition recorded 38/38 candidates, ten recompositions recorded 360/360, and GUI scales 1, 1.5 and 2 recorded 30/30, 159/171 and 161/173 successes/attempts. All eight materializations succeeded with zero failures and the exact Subpixel fallback cause; there were no Gray fallback materializations. The primitive fixture, five non-subset fixtures and three SVG comparisons passed. The report uses Pharos checkout `4966732269f02408df314ae155bb7e6f3817f9d9`, which has uncommitted harness changes and limits commit-level reproducibility. Intel and NVIDIA still need physical runs against this behavior. No performance gain is claimed.


### Stage C: Skia font metrics and Screen composition (2026-10-17)

The runtime API patcher now rewrites `CairoFont.GetTextExtents` and
`GetFontExtents` to call a Skia helper in the candidate `cairo-sharp.dll`. The
helper resolves the same Fontconfig face as the recorder, does not cache faces
before bundled fonts register, and retains the existing Cairo return types. The
patcher also makes `CairoFont.Dispose` safe when measurement left its Cairo font
options unset. `CairoFont.FontMeasuringContext`, `SetupContext`, Cairo context
layout, glyph generation, and unsupported text fallbacks still use Cairo.

The recorder now keeps the `Screen` operator on the Skia path for supported
fills, strokes and paint operations. Text and `PaintWithAlpha` retain their prior
fallback. `Multiply`, `Darken`, `Lighten`, `Difference` and `Exclusion` exceeded
the existing one-channel parity limit and remain on Cairo. Linux and macOS shell
packages now ship the candidate Cairo wrapper, matching PowerShell packaging.

Validation passed on this machine: 1,020 `Optimum.Tests` passed and 34 skipped
with `LD_LIBRARY_PATH` including the installed game `Lib`; Launcher passed 69,
CLI 24, Bootstrap.Core 156 and Installer 78. Build reported zero warnings and
errors. Patch validation reported 82 applied patches, 53 Cecil patches and 22
runtime donor patches; shader validation and `make check` passed. These are CPU
and integration tests. They do not certify the updated runtime on a physical GPU.
No performance gain is claimed.

### Stage D: runtime font-based text layout and overlay dependency (2026-10-17)

The runtime API patcher now clones the vanilla `TextDrawUtil.Lineize(Context, ... )`
algorithm into a private helper that receives `CairoFont`. It replaces only the
`Context.TextExtents` and `Context.FontExtents` calls with `CairoFont` metric calls.
The `CairoFont` overloads of `Lineize` and `GetQuantityTextLines` call this helper,
so they no longer create temporary `ImageSurface` or `Context` instances. The
public `Context` overload is unchanged. Cairo's `CairoFont` static measuring
context, `SetupContext`, shaping/layout outside these two overloads, glyph
generation, drawing, and unsupported-operation fallbacks remain dependencies.

The overlay package now includes the candidate `cairo-sharp.dll` beside its API
contract donor, where `ApiPatcher` resolves the helper assembly. Overlay
installation copies that wrapper into `game/Lib` and backs up the prior wrapper;
rollback restores it, or removes the candidate if the game originally had none.

The serialized API patcher test passed and executed font-based layout across
multiple line-break modes, explicit CRLF/tab text, empty/null input and offsets.
It compares line text, bounds, ascent and spacing with the source implementation,
checks that the `Context` overload IL is unchanged, and verifies the rewritten
methods have no `Context` or `ImageSurface` construction. The installer
patch/rollback tests passed 10/10. Full suites passed: Optimum 1,021 with 34
skipped, Launcher 69, CLI 24, Bootstrap.Core 156 and Installer 78. `make check`,
`make build` (zero warnings/errors), patch validation (82 source, 53 Cecil, 22
runtime donors), shader validation, and `make patch-il` (146/146 required methods)
passed. The Linux overlay was packaged in a disposable directory and confirmed to
place the wrapper beside the API donor.

No physical GPU certification was run for this head. Prior AMD, Intel and NVIDIA
reports predate it. The CPU and IL gates do not qualify GPU parity or Cairo
removal. No performance gain is claimed.

### Stage E: non-pixel surface queries and invalidation (2026-10-17)

`Surface.Content`, `Surface.SurfaceType`, and the `DeviceOffset` getter now query
their native metadata without materializing a recorded display list. The
`MarkDirty(Rectangle)` overload now matches `MarkDirty()`: while the recorder is
`Recording`, `Sealed`, or `GpuRendered`, it skips replay because no native pixels
were exposed for Cairo to reload. Once materialized, rectangular invalidation
continues to call Cairo. These metadata calls still use Cairo P/Invoke and this
stage does not remove the native library dependency.

Regression tests verify metadata and rectangular invalidation preserve recording,
sealed and GPU-rendered states, and that later CPU readback still matches Cairo.
The full `Optimum.Tests` suite passed 1,023 with 34 skipped. Patch validation
reported 82 source, 53 Cecil and 22 runtime donor patches with no conflicts. No
GPU behavior changed and no performance claim is made.

### Stage F: managed matrix math and rectangle capture (2026-10-17)

`Cairo.Matrix` operations now use managed double-precision math. Fixed-grid
rectangle capture no longer creates a temporary Cairo image surface/context.
Focused matrix and surface-recorder tests passed 22/22. Build, shader, patch and
IL checks passed; the IL patcher reported 146/146 required methods. The full
`Optimum.Tests` run was limited by missing `nanosvg.so`; excluding the 17
NanoSVG cases, 1,010 passed and 34 were skipped. Launcher passed 69, CLI 24,
Bootstrap.Core 156 and Installer 78. This head was not tested on physical GPU
hardware.

### Stage G: solid mask recording (2026-10-17)

The recorder now handles valid solid masks for `Over`, `Atop` and `DestOver`.
The focused mask/parity/fallback tests passed 11/11. Build passed with zero
warnings/errors, patch validation reported 84 source, 53 Cecil and 22 runtime
donor patches with no conflicts, and IL patching reported 146/146 required
methods. `Optimum.Tests` passed 1,019 with 34 skipped when excluding the 17
NanoSVG cases that cannot load without `nanosvg.so`. The GUI metrics test
collection is serialized after it exposed a shared-counter race. No physical
GPU test was run for this head.

Stages F and G do not certify a hardware adapter or remove native Cairo. Context
and surface construction, general graphics/path state, pattern capture, text
shaping and unsupported text fallback, CPU pixel access, and public native
handles remain Cairo dependencies. Third-party native-pointer interoperability
is not covered by repository tests. Run this exact head on AMD, Intel and NVIDIA
before extending prior hardware evidence to it.
