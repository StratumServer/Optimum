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
Counters must show positive successful candidates and zero fallback or native
materialization. Changing only the decision string cannot approve evidence.

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

Cairo stays the text layout authority: font selection, `SetFontSize`, font
options, `TextExtents`, `FontExtents` and `TextPath` are Cairo state and do not
materialize. `ShowText` and `ShowGlyphs` with Gray font antialiasing are recorded
as a fill of Cairo's hinted glyph outlines (`cairo_glyph_path` with the same scaled
font) at the glyph origins Cairo's image compositor uses: whole pixels before
Cairo 1.17, quarter pixels from 1.18. Development versions in between, Default,
Subpixel and None font antialiasing, rotated or skewed text, very large glyphs and
more than 4096 glyphs per call materialize. Native replay shows the original text
again with the captured scaled font.

Policy: `CairoFont.SetupContext` requests `Antialias.Subpixel` for all GUI text.
LCD glyphs cannot be reproduced on the Skia path, so while GUI GPU recording is
eligible (`OptimumConfig.GuiGpuRecordingEligible`) Cairo receives Gray instead of
Subpixel, for measurement and drawing alike. This is a visible change from
vanilla: no LCD colour fringes while the GPU path is active. When GPU recording is
not eligible, Cairo receives the requested options unchanged and Subpixel text
materializes to native Cairo with vanilla bytes; regression tests cover both.

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
4. Verify launcher behavior with absent certificate, malformed JSON, AMD-negative
   evidence, changed evidence bytes, changed cached DLL, changed mod/configuration
   and a cache hit. Each must select vanilla while allowing normal client entry.
5. With a complete synthetic report, verify exact live identity match, each
   vendor/renderer/version mismatch, missing display, software renderer and
   timeout. Each failure must select vanilla and allow normal client entry.
   Keep the real AMD report negative; do not hand-edit reports into approvals.

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
