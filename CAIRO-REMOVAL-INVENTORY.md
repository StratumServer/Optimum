# Cairo removal inventory

> **Historical snapshot.** This inventory was written on the former
> `feat/issue-130-recorder-stage-a` branch (2026-10-17 to 2026-10-19) and carried
> over when that work moved to `feat/issue-130-cairo-to-skiasharp`. Its counts,
> commit IDs and "not rendered" statuses describe that branch, not the current
> one: the recorder now tracks graphics state in managed code, the native shadow
> context is gone, and several imports counted below have been removed. Use it for
> the search method, the dependency map and the removal gates, and re-measure
> before quoting a number.

Baseline for this pass: branch `feat/issue-130-recorder-stage-a`, parent commit
`0943cef` (2026-10-17).
This inventories repository responsibilities and removal gates. It is not a
claim that native Cairo has been removed or that third-party binaries are
compatible.

## Current coverage summary (2026-10-19)

These percentages use separate denominators because repository inventory,
rendered behavior, and dependency removal measure different things. They must
not be averaged into a single completion score.

| Metric | Coverage | What it proves | Remaining limit |
|---|---:|---|---|
| Tracked text files searched | 657/657 (100%) | Every tracked text file was scanned; 125 contain a Cairo reference. | Search coverage does not prove every call's runtime behavior or third-party use. |
| Exported API inventory | 82/82 types; 872/872 members (100%) | The current assembly's public/protected surface is enumerated. | Per-member semantic classifications remain marked `classificationReview: required`; no cross-platform ABI proof. |
| Active Cairo imports | 239/239 counted; 0 retired by this fixture update | The current native boundary is measured. | Most imports remain required by current Cairo-backed behavior or public compatibility paths. |
| Primitive fixtures fully rendered | 28/28 (100%) | The manifest covers geometry, state, paths, clips, sources, operators, text/layout, and escape cases. | Some fixture paths still use native Cairo materialization; GUI caret/hit testing remains in the separate 0/57 GUI fixture ledger. |
| GUI callsite fixtures rendered | 0/57 (0%) | All 57 source-derived `ComposeElements`/`ComposeTextElements` rows are listed. | Every row remains `not-rendered` and requires runtime state assessment. |
| Exact Optimum-code AMD GUI certificate | 1/1 passed for fixture commit `ad293b1a8f387e988801649539f1b070906d7291` | The mixed Skia/Cairo inventory scenario passed on Radeon RX 6650 XT. Tested `cairo-sharp.dll` SHA-256: `1f60d7943251ac63b710c70fc9881864bb3f3388660d29b462391976fd784d7f`. | It retained eight successful Subpixel Cairo materializations; it does not prove Cairo-denied operation. |
| Exact-head Intel/NVIDIA certification | 0/2 | The branch has recorded earlier vendor reports. | Neither vendor has tested the current code head. |

Primitive fixtures are complete for the operations declared in
`primitive-coverage-manifest.csv`. This does not mean the Cairo migration is
complete. The full migration remains blocked by unrendered GUI callsites,
unclassified API-manifest rows, active Cairo imports, missing Cairo-denied,
package, and ABI gates, and behavior in compiled third-party code that is not
present in this repository.

## Search coverage

The audit enumerated all Git-tracked paths instead of relying on normal `rg`,
which skips ignored source overlays. It read 657 tracked text files; 125 contain
a case-insensitive `cairo` reference. It also searched the locally available
ignored source trees for `VintagestoryApi`, `VSEssentials`, `VSSurvivalMod`,
`VSCreativeMod`, and `build/VintagestoryLib`. Donors and generated snapshots are
evidence of patch inputs, not proof of runtime execution.

`tools/Cairo.ParityHarness/cairo-api-manifest.json` schema 2 lists 82 exported
types and 872 public/protected members from the current branch assembly. The
previous schema-1 file was stale: it had 81 types/868 members and omitted
`Cairo.OptimumSkiaTextMetrics` plus two methods on it and the protected
`Surface.nativeDataExposed` field. The generator now records member accessibility,
method flags, parameter/return `MarshalAs`, public `DllImport` details, type
layout and instance field offsets where reflection supports them. This run used
.NET 10.0.12, Ubuntu 26.04.1 x64, and 8-byte pointers. It records environment
because layout may differ by architecture.

The 872 automatic labels are 351 `metadata`, 254 `native-only-pending`, 206
`recordable-candidate`, 41 `measurement`, and 20 `materialization-boundary`.
Every row still has `classificationReview: required`; the labels and reflected
facts are an initial sorting aid, not a completed manual classification or a
cross-platform ABI proof. All 11 exported value structs have a reported
marshaled size and instance-field offsets on this host. Compiled native layouts
and pointer behavior still require per-platform evidence.

## Manual source classification coverage

Three independent source reviews covered the complete exported member set. I
reconciled them against the regenerated manifest and removed duplicate coverage
for `Cairo.Graphics` and `Cairo.CairoAPI`:

| Disjoint member set | Types | Members |
|---|---:|---:|
| `Context`, `Graphics`, `Path`, `Matrix`, `Glyph`, `TextExtents`, `FontExtents` | 7 | 249 |
| `Surface`, `ImageSurface`, patterns/gradients, font option/face/scaled font APIs | 11 | 149 |
| All other exported Cairo, NanoSvg, FreeType, backend, and utility types | 64 | 474 |
| **Complete exported API** | **82** | **872** |

The reviews classified every member by managed behavior, normal native call,
materialization/fallback, pointer/ownership, or metadata. They corrected several
automatic-label errors: Matrix/Glyph/extents methods are managed; enum values
are metadata; `Surface.nativeDataExposed` is a managed field; `Surface.Handle`
can materialize while Pattern/FontOptions/FontFace/ScaledFont handles simply
expose pointers; and `Context.CopyPath`, `AppendPath`, and `GetSource` call
Cairo without materializing the target. The manifest keeps
`classificationReview: required` because its generated five-way labels have
not been replaced with those per-member semantic classifications.

The API review also recorded compatibility behavior that must not be changed by
surface-level cleanup: obsolete Context transforms with surprising direction,
`ShowText` current-point advancement versus `ShowGlyphs`, ignored Matrix
parameters on legacy glyph overloads, aliases/enum values, disposal/refcount
rules, and platform-dependent Glyph index width. One duplicate text P/Invoke
uses `[Out] Matrix` while `NativeMethods` declares `out Matrix`; both are
present in the import inventory as distinct managed signatures. The reflection
generator preserves that distinction: the former is a by-value `Cairo.Matrix`
with `isOut: true`; the latter is `out Cairo.Matrix`. Whether the runtime
marshaler gives both declarations the same native pointer behavior still needs
targeted verification. Source inspection is not a cross-platform native ABI
test.

## Runtime and API responsibility inventory

| Area | Current Cairo dependency and concrete consumers | Removal work / gate |
|---|---|---|
| Object creation and native loader | `Context(Surface)` calls `cairo_create`; image surfaces allocate Cairo storage. `CairoAPI` resolves `libcairo` and queries native version. | Replace native factories and prove startup and supported drawing work with native Cairo loading denied. |
| Drawing state, paths and geometry | `Context` owns path, transforms, clips, groups, operators, dashes, extents and hit-testing. Recorder mirrors supported draw styles, dash arrays and source hints, while it still reads native context status, CTM, current path and clips. Stage F removed managed matrix arithmetic. | Managed state/path model, differential tests, current-point/save-restore semantics, and no normal shadow-context calls. |
| Paint, patterns, masks and sources | `RecordedSource` reads native patterns, matrices, stops, surface pixels, extend/filter and source transforms. Stage G records only supported solid masks for three operators. | Managed source snapshots and parity for solid, gradients, surface sources, masks, operators, mutation/disposal and fallback boundaries. |
| Text and fonts | `CairoFont.SetupContext` and its static measuring context; `TextDrawUtil`, `TextTextureUtil`, editable caret/layout, rich text, engraved/embossed text, tabs, signs, world text; `RecordedText` glyph selection, metrics and fallbacks. | Replace metrics/layout/shaping and glyph construction while preserving widths, advances, face mapping, baselines, wrapping, caret geometry and Subpixel policy. The current Skia font/size matrix is bounded; untested combinations still fall back to Cairo. |
| Images and CPU pixel bridges | `ImageSurface.Data/DataPtr`, PNG export, flush/finish, external byte buffers, `SurfaceDrawImage.Image` bridges from `SKBitmap`, resize/overlay, blur and alpha demultiplication. Consumers include text textures, icons, dialogs, item slots and status bars. | Managed/Skia surfaces and lazy CPU readback with stride, format, premultiplication, mutation and invalidation compatibility. |
| SVG and GL upload | NanoSVG parses geometry; Cairo-backed SVG routes and `DataPtr` upload coexist with Skia recorder paths. `LoadCairoTexture`/`LoadOrUpdateCairoTexture` upload surface bytes. | Preserve SVG viewport/tint/alpha behavior, route all supported assets through Skia, and validate texture lifetime and GL state. NanoSVG is a separate native dependency. |
| World/runtime consumers | `EntityMapLayer`, `PlayerMapLayer`, `WaypointMapLayer`, `OwnedEntityMapLayer`, `OreMapLayer`; `BlockEntitySignPostRenderer`; server-side `WgenCommands.OnCmdTreemap` creates an RGB24 surface/context and writes PNG. | Cover map/sign GUI and non-GUI export fixtures. Cairo removal cannot be declared complete from menu/inventory tests alone. |
| Public native API and ABI | `Context`, `Surface`, `ImageSurface`, `Pattern`, `Path`, fonts, `Region`, `Device`, PDF/PS/SVG and platform surfaces expose native pointers, ownership or backend behavior. Assembly identity is `cairo-sharp` 1.0.0.0. | Classify all 872 members and define behavior/limitations for pointer constructors, `Handle`, `DataPtr`, layouts, ownership, encoders and platform backends. Keeping names does not preserve native-pointer interoperability. |
| Other native libraries | `Cairo/Svg/SvgNativeMethods` exposes NanoSVG P/Invokes; FreeType helpers expose FreeType APIs. `RecordedText` declares Cairo and Fontconfig imports separately. | Track NanoSVG, FreeType and Fontconfig as distinct dependencies; do not count their remaining imports as Cairo or remove assets they still need. |
| Build, patch, install and rollback | Patcher rewrites APIs and hooks recorder metrics; launcher certification fingerprints `cairo-sharp`; overlay/package scripts stage it; installer backs up/restores the wrapper. Linux/macOS packages copy broad vanilla trees/bundles. | Update donors, IL contracts, artifact fingerprints, package manifests and rollback together. Audit packaged native binaries and dependency chains, not just source references. |
| Tests and certification | ABI fixtures intentionally load native Cairo; CPU parity tests and the exact-head AMD Pharos certificate cover the current mixed Skia/Cairo runtime. No current gate denies Cairo loads. | Keep native oracle tests separate from runtime proof. Test hardware parity for each exact published head and separately test a packaged client with Cairo loading blocked. |

### Text scratch-surface reduction

`RecordedText.CaptureGlyphs` now creates its 1x1 Cairo surface and context only
when it needs Cairo outlines for a Gray text run. A run accepted as an Skia
`SKTextBlob`, including Subpixel, skips the scratch objects. By static call-path
count this avoids `cairo_image_surface_create`, `cairo_create`,
`cairo_destroy`, and `cairo_surface_destroy` per accepted text run; the count
has not been measured with native-call instrumentation. Unsupported Gray text
still uses Cairo outlines, and unsupported Subpixel text still fails closed to
the existing Cairo materialization path. This does not remove Cairo glyph
selection, metrics, font matching, or replay.

### Text slant qualification

The Skia text-blob allowlist now constrains slant as well as the already
qualified family and weight. Subpixel normal and italic runs are covered for
`sans-serif`, `Montserrat`, `Lora` normal/bold, and `Almendra` normal by native
Cairo pixel-oracle tests under the local antialias policy. A five-case italic
matrix passed with no native materialization. Gray text remains restricted to
the normal slant matrix; an italic Gray run keeps Cairo-generated outlines,
and an unqualified Subpixel oblique run keeps the exact native Cairo replay.
The focused text suite passes 31/31. This closes an allowlist gap where
previously untested italic or oblique faces could enter Skia based on family
and weight alone. It does not replace Cairo font selection, glyph shaping,
positions, or unsupported-run replay, and it is not cross-platform font proof.

### Recorded text antialias state

`RecordingGraphicsState` now mirrors the font antialias option across assignment
and Save/Restore. `FontOptions` tracks antialias values set through its managed
API, preserves them through `Copy` and Cairo's non-default `Merge` semantics,
and reports unknown when it cannot prove the mirrored value. Assigning an
unknown options object to a recording context reads its antialias value once at
assignment time. `RecordedText.CheckFont` consumes the snapshot instead of
creating a temporary `FontOptions` and calling
`cairo_get_font_options`/`cairo_font_options_get_antialias` for each text run.
By static call-path count this removes four Cairo calls and one temporary
native options object per recorded text run. Subpixel remains explicitly
recognized and routed to the Skia text blob path; unsupported antialias modes
continue to fail closed to the existing materialization path.

The focused mirror test covers assignment snapshot semantics, Copy, Merge with
default options, and Save/Restore, and verifies the mirrored value against
Cairo's native context getter. This host run has no GPU or cross-platform ABI
evidence. This optimization does not replace Cairo font selection, scaled-font
creation, glyph lookup, or other text metrics; those remain separate removal
work. Recorded font-matrix reads are mirrored separately below.

### Recorded font-matrix state

`RecordingGraphicsState` now snapshots the native font matrix once when a
context attaches to a recorder and clones it through Save/Restore. `FontMatrix`
assignments update the mirror from a defensive managed copy; `SetFontSize` and
`SetScaledFont` refresh it after Cairo accepts the change. During recording,
`Context.FontMatrix` returns the snapshot. Both text-capture checks now share
that matrix, so `RecordedText` no longer makes either of its two
`cairo_get_font_matrix` calls for each run and its duplicate direct import is
removed. This shifts the read to recorder attachment and font-size/scaled-font
updates; it does not remove Cairo's font matrix API outside recorded contexts.

The NativeMethods binding now takes a preallocated `[Out] Matrix` instead of
`out Matrix`. Calling the latter with a null managed class reference produced a
`NullReferenceException` on this .NET/Linux x64 runtime; the corrected binding
passes the allocated sequential-layout object used by the prior RecordedText
import. The focused test compares all six matrix fields with Cairo after
attachment, size change, explicit matrix assignment, scaled-font assignment,
and Save/Restore. This is Linux x64 evidence only; other runtimes and
architectures still need ABI verification.

The `CairoRecorderTextTests` suite passes 24/24 on this host, including parity
fixtures and an assertion that the eligible Subpixel case records two Skia text
blobs without captured Cairo paths. No physical GPU test was run because this
change skips unused scratch allocations and leaves the rendered commands
unchanged.

### Editable-text hit testing

`GuiElementEditableTextBase.SetCaretPos(double,double)` now measures line height
and text prefixes through `CairoFont.GetFontExtents/GetTextExtents`, which use
the existing Skia metrics helper. It no longer creates a temporary
`ImageSurface` and `Context` for each pointer-position update. The character
index loop, newline trimming, clamps and early return remain in place. This
aligns pointer hit testing with `SetCaretPos(int,int)`, which already uses the
same Skia metrics, but changes click positioning from Cairo measurements to
Skia measurements. The patch applies and compiles with the current source set;
the related font/API/text tests pass 41/41. There is not yet a direct editable
text click/drag fixture, so caret hitbox parity remains unverified.

### Remaining GUI font-measurement consumers

The source audit included ignored game-source overlays (`VintagestoryApi`,
`VSSurvivalMod`, and related client trees), not just tracked files. Direct GUI
measurements in editable-text sizing, text baselines, tab widths, engraved
letter spacing, and sign-post layout now use `CairoFont.GetFontExtents` or
`GetTextExtents`, whose implementation uses Skia. The patches apply and the
full Optimum test assembly passes 1,074 tests with 34 pre-existing skips.

`TextDrawUtil.Lineize(Context, ...)` remains a public compatibility overload
that reads `Context.TextExtents` and `Context.FontExtents`; the repository's
client sources call the `CairoFont` overload instead. It has no `CairoFont`
argument from which to resolve the exact configured face. The public Context
measurement APIs also remain for external mods.

The callsite scan across the available API, Essentials, Survival and Creative
source trees found three `TextPath` calls. `TextDrawUtil.DrawTextLine` and
`TextTextureUtil` now try `Context.TryShowTextWithStroke` first for ordinary
outlined text. A qualified face records one grayscale Skia text blob with a
stroke and fill; this matches Cairo `TextPath` geometry, whose antialiasing
comes from the context rather than LCD font rendering. The method accepts only
move-only existing paths, an empty path or `MoveTo` positioning. Unsupported
faces and contexts return to the existing Cairo sequence. The two retained
`TextPath` calls are fallback code; the third is the public `textPathMode`
branch, which external mods may use. The vanilla caller does not request
`textPathMode: true`. The concrete in-tree outlined-font consumers are item-slot
counts (`CairoFont.WhiteSmallText` with bold weight), statbar values
(`WhiteSmallText`, normal weight), and liquid-stack labels (`WhiteSmallText`
with bold weight). All use `GuiStyle.StandardFontName`, currently
`sans-serif`, which is in the Gray Skia qualification matrix for normal and bold
faces. A source scan found no in-tree assignment of `textPathMode = true`; the
field remains public and can still be set by external mods. This maps the
tracked built-in outline consumers to qualified family/weight combinations;
platform-specific Fontconfig/Skia face matching still needs vendor evidence.

`CairoRecorderTextTests.OutlinedTextUsesGraySkiaBlobWithoutMaterializing`
asserts that the qualified route records a blob without materializing, and
`OutlinedTextSkiaPathMatchesCairoTextPathWithinLocalAntialiasPolicy` covers
sans-serif normal at 0.75px and bold at 1, 1.25, 1.5 and 2px; each case compares
Skia output with Cairo and verifies byte-exact native replay. The prior
`TextPathAndFontStateDoNotMaterialize` still covers the explicit path route.
This removes the normal qualified outline route's dependency on Cairo text-to-
path, but does not remove fallback or public API dependencies.

### AMD runtime check after GUI metric migration

On 2026-10-18, the full Pharos inventory scenario passed on AMD Radeon RX
6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core, against Optimum commit
`2e4b11e445a16d01b1586f68c3d053a64aad14ab`. It opened the inventory, captured
the GUI at scales 1, 1.5 and 2, ran the recomposition benchmark, exercised the
GPU primitive, five non-subset and three SVG parity fixtures, and validated the
staged launcher profile certificate. It selected
`skia-with-subpixel-cairo-fallback` with no decision reasons. Initial
composition recorded 38/38 Skia candidates; ten recompositions recorded
360/360. Scale captures recorded 30/30, 159/171, and 161/173 Skia candidates.
All 8 Subpixel materializations succeeded, with zero failures; the primitive,
five non-subset fixtures and three SVG comparisons passed, with SVG parity
within one channel value.

The repeat run after rebuilding the disposable stage also passed
`TextPathAndFontStateDoNotMaterialize` and
`SubpixelTextMaterializesToNativeCairoAndMatchesVanillaOutput` (2 focused tests,
zero failures). The Pharos report records the same exact branch head and
`cairo-sharp` SHA-256 `704e5a3d9f9779277ddb2eac4de107a9f28245db5f0a4b2d0fc2bd3edaf66116`.

This establishes that the migrated metrics work still runs on this AMD setup
while preserving the tested Subpixel fallback. It does not establish Cairo
independence: the runtime report explicitly includes and loads `cairo-sharp`,
and the accepted result depends on Cairo for unsupported Subpixel runs. The
Pharos test assembly was built from a dirty private checkout at HEAD
`5e5b78c3a229295bd1953eb1942492092cefe673`; its artifact hash is in the local
GPU evidence report, but that HEAD does not identify the uncommitted harness
changes. Intel and NVIDIA have not run this exact Optimum commit.

### AMD retest after the TextPath audit publication

On 2026-10-18, the full Pharos inventory scenario passed again with public
Optimum HEAD `1fc9b719730bcad58cb4dcd3543f3feeed8d4d27` on AMD Radeon RX 6650 XT,
Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core. The staged `cairo-sharp.dll` SHA-256
matched the public build (`f2f79e7a282055ce1fcf6a7411086d5c54ac1b9a872dfae85d1700cea6568f50`).
GPU selection was `skia-with-subpixel-cairo-fallback` with no decision reasons;
initial composition was 38/38, ten recompositions were 360/360, and scales 1,
1.5 and 2 recorded 30/30, 159/171 and 161/173 Skia candidates. All eight
Subpixel materializations succeeded with zero failures. The primitive, five
non-subset fixtures and three SVG comparisons passed, with SVG output within
one channel value. The launcher validated its local profile certificate.

The Pharos checkout was at base HEAD
`4966732269f02408df314ae155bb7e6f3817f9d9` with local modifications, so that
base commit does not identify the exact harness source. This is a retest of
the existing mixed Cairo/Skia runtime, not Cairo-denied evidence. Intel,
NVIDIA, package-loader, Cairo-denied and cross-platform ABI checks remain open.

### AMD retest for text slant qualification

On 2026-10-18, the full staged Pharos inventory scenario passed on AMD Radeon
RX 6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core, against exact Optimum
commit `14459e739f3bba599d2617a5fa1328ba7dd6a0ea`. The launcher validated the
local staged profile certificate and selected
`skia-with-subpixel-cairo-fallback` with no decision reasons. The report had
38/38 initial composition candidates, 360/360 candidates across ten
recompositions, and scale results of 30/30, 159/171, and 161/173 at 1x, 1.5x,
and 2x. All eight allowed Subpixel fallbacks materialized successfully; there
were zero materialization failures. The primitive, five non-subset fixtures,
and three SVG comparisons passed; SVG output stayed within one channel value.

The tested `cairo-sharp.dll` SHA-256 was
`B50A1340943F4E4B712885FEDBC1F9A0463F18A977EDB365F573DDAB2A602114`.
The Pharos project built with nine existing warnings and zero errors from the
private workspace's dirty Pharos checkout; the report binds loaded artifacts
and runtime profile, but the harness source is not identified by its base
commit alone. This is AMD evidence for the exact staged wrapper, not proof of
Cairo-free execution or Intel/NVIDIA behavior. The client still loads Cairo
and uses it for the eight accepted Subpixel fallbacks.

The final public gate on this documentation update passed `make check`,
`make build` with zero warnings/errors, `make check-patches` (92 source patches,
53 Cecil patches, 22 runtime donors and zero conflicts), and
`make check-shaders`. All five test projects passed: Optimum 1,074 with 34
skips, Launcher 69, CLI 24, Bootstrap.Core 156, and Installer 78. The full
Optimum suite needs the local NanoSVG library on `LD_LIBRARY_PATH` for its SVG
tests.

### AMD retest for qualified Skia text outlines

On 2026-10-18, the full staged Pharos inventory scenario passed 1/1 against
Optimum commit `c23649f7328ae83c2f9e77077d869ddf40e2d9e9` on AMD Radeon RX
6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core. The launcher validated the
local profile certificate and selected `skia-with-subpixel-cairo-fallback`
with no decision reasons. Initial inventory composition recorded 38/38 GPU
candidates; ten recompositions recorded 360/360. Scale captures recorded
30/30 at 1x, 159/171 at 1.5x, and 161/173 at 2x. All eight
`Recorder.ShowText.Unsupported:text-antialias-Subpixel` materializations
succeeded; there were zero failures. The primitive fixture, five non-subset
fixtures, and three SVG comparisons passed. SVG output stayed within one
channel value.

The staged `cairo-sharp.dll` SHA-256 was
`6D659433DCAD905CF43ED23568249F220ED594DE3638C5B206142D26EB1771EE`;
this matched the local build artifact SHA-256 exactly. The
Pharos test assembly SHA-256 was
`3049A0666EBB94DAEA82F3752D9E275FC7C0463B14BD238C77EE83F7DE4DE5F4`. The
Pharos source checkout was based on `4966732269f02408df314ae155bb7e6f3817f9d9`
with local modifications copied into the isolated workspace. The assembly hash
identifies the executed test binary. Its build reported nine warnings and zero
errors.

The local `Optimum.Tests` suite passed 1,087 tests with 34 skips when run with
the installed client's `Lib` directory on `LD_LIBRARY_PATH`; the focused text
suite passed 37/37 across the normal/bold and stroke-width matrix. `make build`
passed with zero warnings/errors, patch
validation passed with 92 source patches, 53 Cecil patches, 22 runtime donors
and zero conflicts, and shader and vanilla compatibility checks passed. The
GPU result still loads Cairo and accepts eight successful Subpixel fallbacks.
It is not Cairo-denied evidence and does not certify Intel or NVIDIA.

### Solid-pattern source capture

`Context.RecordSourceState` now skips `CurrentMatrix()` when the native pattern
type is solid, clears the tracked source surface, and leaves the mirrored CTM
unchanged. `RecordedSource.Capture` returns a solid source before reading that
CTM; subsequent gradient/surface sources replace it, and Save/Restore restores
the corresponding snapshot. The existing source-space parity test now uses a
`SolidPattern` inside the saved surface-source sequence. This avoids one
`cairo_get_matrix` call and one managed `Matrix` allocation per recorded
`SetSource(SolidPattern)` by static inspection. No timing counter measured the
reduction. The focused Cairo recorder parity/text suites passed 91/91; patch
validation passed with 85 source patches, 53 Cecil patches, 22 runtime donors
and zero conflicts.

### Direct surface-source capture

The three `Context.SetSourceSurface`/`SetSource(Surface, ...)` overloads already
pass the source surface handle to `cairo_set_source_surface`. During recording,
they now store that same handle and the current CTM directly instead of calling
`cairo_get_source`, `cairo_pattern_get_type`, and `cairo_pattern_get_surface`
to recover information already available at the call site. A successful setter
also checks context status before trusting the supplied handle; if Cairo rejects
the source, it keeps the prior pattern-derived state as before. The pattern
matrix and filter are still read from Cairo when each draw is captured,
preserving Cairo's source offset and mutation behavior. Context detachment still
checks the stored surface pointer and materializes a source recorder before
native use.

The recorder parity, surface-recorder, and recorder-text suites passed 108/108,
including pixel-aligned surface source offsets, source snapshots, and the
transition from recording to native drawing. Successful assignments replace
three metadata calls with one status check, one CTM read and the already-known
surface pointer. No native-call counter or timing measurement is available.

### Solid source hints

The recorder graphics-state mirror now carries an immutable solid-source hint.
New contexts start with Cairo's default opaque-black source, `SetSourceRGBA`
stores the exact input channels, and Save/Restore clones the hint with the rest
of the mirrored state. `RecordedDrawing.Capture` uses the hint for recorded
draws and skips `cairo_get_source`, `cairo_pattern_status`,
`cairo_pattern_get_type`, and `cairo_pattern_get_rgba` in that path. Assigning a
`Pattern` or surface clears the hint, so mutable pattern data continues through
the native snapshot path. Cairo still receives `SetSourceRGBA`, and its context
status remains the first capture check.

`SolidSourceHintTracksCairoDefaultAndSaveRestore` passed against Cairo's native
image-surface output. The recorder parity, surface-recorder, and recorder-text
suites passed 109/109, including that state test. This removes four
source-inspection P/Invokes per supported draw by static call-path inspection.
No native-call counter, cross-platform run, or timing measurement is available.
Text, including Subpixel, is unchanged.

### Mirrored drawing style

`RecordingGraphicsState` now mirrors the Cairo defaults and wrapper setters for
operator, antialias, fill rule, tolerance, line width, cap, join, and miter
limit. Its Save/Restore snapshot includes these values. `RecordedDrawing.Capture`
uses that state after the native context status check instead of querying Cairo
for each draw. Public getters remain native, and raw context-handle access
materializes recording before callers can make untracked changes. Dash arrays
and offsets are now normalized into the recording-state mirror when
`Context.SetDash` succeeds. The mirror preserves Cairo's zero merging for
interior intervals, odd-length patterns, degenerate all-on patterns, and
positive modulo normalization of offsets. Save/Restore carries the dash
snapshot with the other stroke state. Tiny positive dash intervals below
1/64 user unit fail closed to native Cairo because the Skia path effect differs
from Cairo there.

`RecorderMirroredDashMatchesCairoNormalization` compares eight dash cases
against Cairo's native image output, including empty, odd, leading/interior
zero, degenerate, near-degenerate, and large/negative offset inputs.
`RecorderMaterializesForSubpixelDashIntervals` checks the exact native fallback
for a 0.003-unit interval. The recorder parity, surface-recorder, and
recorder-text suites passed 148/148.
The new `RecorderMirroredDrawingStateMatchesCairoAfterSaveRestore` case checks
operator, fill rule, tolerance, antialias, stroke width/cap/join/miter and
restoration against Cairo image-surface output. Static inspection shows four
fewer Cairo state queries per eligible draw and ten for strokes, including the
two removed dash queries. No native-call instrumentation, timing result, or
cross-platform run is available.

`RecordedClip.Capture` now uses the mirrored fill rule, antialias mode, and
tolerance instead of querying those three values from Cairo when recording a
clip. Public context getters still use Cairo. `RecordedClipCapturesMirroredFillRuleAntialiasAndTolerance`
checks a curved even-odd clip against native Cairo output. The recorder parity,
surface-recorder, and recorder-text suites now pass 150/150. This saves three
native getter calls per captured clip by static inspection; no call counter or
timing measurement was run.

`RecordingGraphicsState` now mirrors the current transformation matrix. The
wrapper tracks `Translate`, `Rotate`, `Scale`, `Transform`, the `Matrix` setter,
and `IdentityMatrix`, and copies the matrix during Save/Restore. Recorded draw
capture, clip capture, and source-surface assignment use that snapshot;
`Context.Matrix` returns a copy of it while recording. The native matrix getter
remains for non-recorded contexts. `RecordedText` now uses the same CTM snapshot
for text eligibility, glyph capture, Skia text-blob placement, and its retained
drawing record, so text capture no longer re-reads the matrix from Cairo.
`RecorderMatrixMirrorTracksSettersAndSaveRestore` compares the managed snapshot
field-for-field with a separate native Cairo context after every operation,
including mutation of the input matrix. The transformed clip, Subpixel/Gray
text, and recorder parity suites pass. Static inspection shows one fewer
`cairo_get_matrix` call per recorded geometry draw, clip, or source-surface
assignment, and three or four fewer matrix reads per text capture, depending
on whether Skia text-blob creation is eligible. No call counter, timing result,
or cross-platform test is available.

## Native import inventory

The schema-2 manifest includes **269 active `DllImport` methods** reflected from
the current assembly: 239 for `libcairo-2`, 13 for `freetype6`, 9 for
`libfontconfig.so.1`, and 8 for `nanosvg`. The Cairo imports comprise 233
methods in `Cairo.NativeMethods` and 6 separate declarations in `RecordedText`.
Every `nativeImports` row is marked for manual native-header review; reflection
records declarations but does not verify that their native signatures are
correct. The manifest records 574 parameters, 53 `IsOut` and 0 `IsIn` flags,
257 `Cdecl` and 13 `Winapi` imports. A source-to-manifest correspondence check
must still be paired with native-header review; it does not establish ABI
correctness.

`Cairo/NativeMethods.cs` has 302 raw `extern`/`DllImport` matches, 69 in
comments. After stripping comments there are **233 active declarations**, with
232 unique names because `cairo_image_surface_create_for_data` has two
overloads. Reflection reports the same 233 active `NativeMethods` imports. The
previous pass removed 25 declarations without a `NativeMethods.<name>` call in
the C# wrapper/consumer tree or the generated source tree used by the build.
Twenty-four removed entrypoints have no C# reference in that tree. The
remaining one, `cairo_scaled_font_get_font_face`, is still declared and called
through a separate import in `RecordedText`; the corresponding
`NativeMethods` declaration was removed because the call uses the direct
import. All 232 remaining unique `NativeMethods` names have a source caller.
Six direct Cairo imports remain in `RecordedText`, all distinct from the active
`NativeMethods` imports. The previous pass removed the duplicate
`cairo_scaled_font_status` import and reused
`NativeMethods.cairo_scaled_font_status`; both declarations had the same Cdecl
ABI signature. This pass removes the separate `cairo_get_font_matrix` import
and uses `NativeMethods.cairo_get_font_matrix` with a preallocated `[Out]`
matrix. This is a source call-site
audit, not proof against every possible runtime reflection or string-based
invocation. All reachable imports
and runtime-created calls still need a managed replacement or an explicit
limitation before native Cairo can be removed. Header/platform review still
needs to resolve, among other items, Cairo boolean and `unsigned long` widths,
status values discarded by wrappers, FreeType parameter widths and calling
convention, string encodings, NanoSVG variant exports, and the fixed 24-byte
glyph stride. The import table does not include runtime dynamic loading or
transitive native dependencies.

### Managed image stride calculation

`Surface.StrideForWidth` now calculates the same four-byte-aligned row stride
without calling `cairo_format_stride_for_width`. It accepts Cairo image formats
0 through 7, including the floating-point formats present in Cairo 1.18, and
returns `-1` for negative widths, overflow widths, and invalid formats. The
unused `NativeMethods` import was removed. This removes one Cairo import while
preserving the public method signature and `CreateForImage` behavior for the
formats supported by Cairo 1.18.4.

The 29 focused `CairoSurfaceStrideTests` passed. I compared the managed formula
against the installed Cairo 1.18.4 native function for 248 format/width pairs,
including negative values and overflow boundaries, with zero mismatches. The
native comparison is local Linux evidence; no Windows or macOS execution was
run for this pure integer calculation.

### Mirrored selected toy-font metadata

`Context.SelectFontFace` now records its family, slant and weight in the
recorder's graphics-state snapshot. Save/Restore clones the hint. Direct
`SetContextFontFace` and `SetScaledFont` assignments invalidate it, so those
paths still query and qualify the actual Cairo face. For a captured text run
after a known `SelectFontFace`, Skia qualification no longer calls
`cairo_scaled_font_get_font_face`, `cairo_font_face_get_type`,
`cairo_toy_font_face_get_family`, `cairo_toy_font_face_get_slant`, or
`cairo_toy_font_face_get_weight`: five native calls per eligible run are
skipped by static call-path inspection. The actual glyph IDs, positions,
advances, scaled font, family-to-file resolution and native fallback are
unchanged. This removes no import declarations; the public wrapper and unknown
face path still use those APIs. The focused text and context-state suites pass
37/37 and 39/39, and the full `Optimum.Tests` project passes 1,088 with 34
skips. The other four project suites also pass locally. The full staged Pharos
inventory scenario passed 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3,
OpenGL 4.6 Core, and identifies exact Optimum commit
`5cd12b655654333b77a3644df76b5d9aa1509264`. The launcher accepted the local
profile certificate and selected `skia-with-subpixel-cairo-fallback` with no
decision reasons. Initial composition passed 38/38 GPU candidates; ten
recompositions passed 360/360. Scales 1x, 1.5x and 2x passed 30/30, 159/171
and 161/173 candidates. The 24 scale fallbacks were all the supported
Subpixel Cairo fallback; all eight materializations succeeded with zero
failures. The primitive fixture, five non-subset fixtures and three SVG
comparisons passed. The tested staged `cairo-sharp.dll` SHA-256 was
`3D960EE949B19F32DDD8CDE4BC85B57EF842D89BE2240AB8CC220A4392478579`; report
SHA-256 was `F96D702304CE4F2507CC11919F07E964C4CD22CE0D3FF3B65D37E4F96A9EAD28`
and identifies Pharos commit `e37bc0062ca2e78b663096c81c182c309e505875`.
This proves the tested mixed Cairo/Skia runtime on this Radeon only. Cairo was
loaded, the 1.5x/2x surfaces retained eight exact Subpixel materializations,
and Intel/NVIDIA, Cairo-denied startup, packaging and cross-platform ABI gates
remain open.

### Mirrored Cairo current point for recorded text

The recorder now mirrors the current point in device coordinates for
wrapper-controlled `MoveTo` and `NewPath` operations. Text capture converts
that point through the mirrored CTM; a successful `ShowText` updates the mirror
from Cairo's returned advance, including after a native-query fallback. Other
path mutations invalidate the mirror, so text capture keeps the native
`cairo_has_current_point` / `cairo_get_current_point` fallback when the point
cannot be proven. For a
captured run whose point is known, this skips both native calls by static
call-path inspection. It removes no imports: non-recording contexts and
unknown path states still use Cairo. A focused test checks recorder text
origins against a separate Cairo context for a known empty path, across CTM
translations, after a native fallback following `LineTo`, and after mirror
recovery. The public `CurrentPoint` and `HasCurrentPoint` getters continue to
query Cairo. Recorder text capture consults the mirror only after Cairo status
is successful. All 39 `CairoRecorderTextTests` pass locally. The complete test
projects pass: Optimum 1,090 passed and 34 skipped, Launcher 69, CLI 24,
Bootstrap.Core 156 and Installer 78. `make build` passed with zero warnings
and errors; `make check-patches` reported 92 applied and 53 Cecil patches,
with 22 runtime donors compiled and zero conflicts. `make check`,
`make check-shaders`, `make check-compat` and `git diff --check` passed. The
first full Optimum test run lacked `libnanosvg.so` on its loader path and had
17 `DllNotFoundException` failures; rerunning with the staged runtime's
`Lib/` in `LD_LIBRARY_PATH` passed all tests.

The exact code commit `2c3208d4139873848d172f66d3fa7e36226d2396` passed the
Pharos inventory scenario 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3,
OpenGL 4.6 Core. The launcher validated the profile and selected
`skia-with-subpixel-cairo-fallback` with no decision reasons. Initial
composition passed 38/38 candidates; ten recompositions passed 360/360.
Scales 1x, 1.5x and 2x passed 30/30, 159/171 and 161/173. All 24 scale
fallbacks were the supported Subpixel Cairo fallback; eight materializations
succeeded with zero failures. The primitive fixture and five non-subset
fixtures rendered on GPU; three SVG tint cases matched within one channel
value, and the GL state sentinel passed. Pharos commit:
`e37bc0062ca2e78b663096c81c182c309e505875`. Evidence SHA-256:
`7BCA5A30FDE3B932050DFFAF99FEC78BFBD6EAF048260DB38DA9CD161611967A`; staged
`cairo-sharp.dll` SHA-256:
`F879E12A15A80C30B483C7B5DAF505C3A76781CE1C0E0FFECC8451FCA200527B`.

The getter experiment in commit `2c3208d` is superseded below. Its Radeon
scenario did not exercise a path mutation after a sticky Cairo error.

This still tests a mixed Cairo/Skia runtime. It does not reduce the 239 active
Cairo imports or certify Cairo-denied startup, packaging, cross-platform ABI,
or Intel/NVIDIA behavior.

The exact code commit `8222c58f04f809896ec7d75a210bb12d3dfada55` passed the
Pharos inventory scenario 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3,
OpenGL 4.6 Core. The launcher validated its profile and selected
`skia-with-subpixel-cairo-fallback` with no decision reasons. Initial
composition passed 38/38 candidates; ten recompositions passed 360/360.
Scales 1x, 1.5x and 2x passed 30/30, 159/171 and 161/173. All 24 scale
fallbacks were the supported Subpixel Cairo fallback; eight materializations
succeeded with zero failures. The primitive fixture and five non-subset
fixtures rendered on GPU; three SVG tint cases matched within one channel
value, and the GL state sentinel passed. Pharos commit:
`e37bc0062ca2e78b663096c81c182c309e505875`. Evidence SHA-256:
`9AC68B3653E277499A159B6FEA990FF1CE44D1F934980DFB2903262D18948800`; staged
`cairo-sharp.dll` SHA-256:
`4667DDF60780E66510FABA69CD9DA3CDD3B7AB8DDA734B7CCD71020F27D5ADCD`.

This validates the mixed Cairo/Skia path on this Radeon. It does not reduce the
239 active Cairo imports, certify Cairo-denied startup, or cover Intel/NVIDIA,
packaging or cross-platform ABI behavior.

### Subpixel fallback reason classification (2026-10-18)

`RecordedText` previously discarded the specific reason from
`TryCreateSkiaTextBlob` and labeled every rejected Subpixel text blob
`text-antialias-Subpixel`. It now preserves the specific failure. The first
exact Radeon run exposed that the local GUI certifier and Pharos assertion
accepted only the older generic reason; both rejected `text-font-parity`.
I added that exact known font-parity reason to the successful Subpixel fallback
allowlist in the runtime metrics, launcher certifier, and private Pharos
scenario. Other materialization reasons remain rejected.

The corrected code at Optimum commit
`4319b59a26964d4a59c73e2dd96b91332aba4fae` passed Pharos 1/1 on AMD Radeon RX
6650 XT, Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core. The launcher accepted local
certification and selected `skia-with-subpixel-cairo-fallback` with no
decision reasons. Initial composition passed 38/38 candidates; ten
recompositions passed 360/360. Scales 1x, 1.5x and 2x passed 30/30, 159/171,
and 161/173 candidates. All 24 scale fallback candidates used the recognized
font-parity Subpixel fallback. Eight materializations succeeded, with zero
failures. The primitive and five non-subset fixtures rendered on GPU; three
SVG tint comparisons passed within one channel value, and the GL state
sentinel passed.

The tested `cairo-sharp.dll` SHA-256 is
`7C7D7B3170E76DC6F8B27BD07B39CEA6DFF8A595FF57586B51DBA4CAEACACA19`; the
API contracts SHA-256 is
`B54BFE2E93F43DC188C54BD89E4F73EA9CD17A9F0BB94976180E56B263C68C7D`; the
evidence SHA-256 is
`F072AD926A7A496F0E535165CA8799A175E7D6345813C9E59E6F50FF9CAFDCBF`.
Pharos source was based on `4966732269f02408df314ae155bb7e6f3817f9d9`; the
private checkout has uncommitted changes, limiting harness reproducibility.
This changes no Cairo import count: 239 active imports remain. It adds
diagnostic accuracy and a narrowly classified fallback while retaining Cairo
for those Subpixel runs.

### Retain Cairo for bold sans-serif Subpixel text (2026-10-18)

I tested adding `sans-serif` Bold to the Skia Subpixel font allowlist with the
existing Cairo pixel oracle. The local test failed: the filtered edge
difference reached 98.5, above the established limit of 96. I reverted that
allowlist expansion, keeping this face on exact Cairo materialization. After
the revert, the focused `CairoRecorderTextTests` suite passed 39/39. This is a
measured boundary in the current font matrix, not a new runtime change. The
Radeon certificate remains the exact prior run for unchanged code commit
`4319b59a26964d4a59c73e2dd96b91332aba4fae`; the failed candidate was not part
of that build. Cairo imports remain at 239.

### Preserve public current-point error semantics

An experiment made `Context.CurrentPoint` and `HasCurrentPoint` read the
managed mirror. A differential test found that after `Scale(0, 1)` puts Cairo
in `InvalidMatrix`, a following `MoveTo` is ignored by Cairo while the mirror
advanced. I removed the managed getter path and kept both public getters
native. Text capture still uses the mirror only after its existing Cairo
status check, preserving the safe two-call reduction for known points. The
regression test now covers the sticky-error case; all 39 focused text tests
pass. The final `Context` patch matches the Radeon-tested implementation in
`8222c58` for text capture, while the public getters retain native behavior.
The current rebuilt wrapper has SHA-256
`b4ee5af357c66a13ad8e303e77eccad6f379d93a85d1847928f6ba37113858e9`; the
earlier Radeon artifact hash does not match this build, so it is not treated
as exact-head evidence. No additional Cairo imports were removed.

### Corrected current-point mirror Radeon retest (2026-10-18)

After reverting the public getter optimization, the exact wrapper build for
Optimum commit `7855deb5a0226aa3d0ba1d722775a97524955fa2` passed the full Pharos
inventory scenario 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3,
OpenGL 4.6 Core. The launcher accepted its local certification. The report
selects `skia-with-subpixel-cairo-fallback` with no decision reasons. Initial
composition passed 38/38 GPU candidates; ten recompositions passed 360/360.
Scales 1x, 1.5x and 2x passed 30/30, 159/171 and 161/173 candidates. The 24
scale fallbacks were successful Subpixel Cairo materializations; all eight
materializations succeeded, with zero failures. The primitive fixture and all
five non-subset fixtures rendered on GPU; all three SVG tint comparisons were
within one channel value, and the GL state sentinel passed.

The tested `cairo-sharp.dll` SHA-256 is
`B4EE5AF357C66A13AD8E303E77ECCAD6F379D93A85D1847928F6BA37113858E9`; the
evidence SHA-256 is
`4AB23212B0A3E0A49FC8708716FCC968C71AB3BFF64D8A529A7526AE9AD66EDC`.
Pharos source was based on `4966732269f02408df314ae155bb7e6f3817f9d9`; that
checkout has uncommitted changes, limiting harness reproducibility. This is
Radeon evidence for the corrected mixed Cairo/Skia runtime. It does not certify
Cairo-denied startup, packaging, Intel/NVIDIA, or cross-platform ABI behavior.
The iteration still removes zero declarations from the 239 active Cairo
imports; it only skips two current-point native calls for a recorded text run
when the mirrored point is known.

### Subpixel bold font parity follow-up (2026-10-18)

I retested the previously rejected `sans-serif` Bold Subpixel face using the
existing local Cairo pixel oracle. The established filtered-difference limit
is 96.0. The original Slight-hinting/Subpixel-position candidate measured
98.5. Normal and Full hinting with Subpixel positioning both measured 97.2;
None measured 108.9. Slight hinting with Skia subpixel glyph positioning
disabled measured 96.3, still above the limit. Force-auto-hinting and linear
metrics did not improve that result. A global switch to Normal also regressed
four already-qualified face cases (filtered differences 96.9, 104.1, 105.1,
and 97.2), so I reverted it. The production hinting and Subpixel settings and
font allowlist remain unchanged, and the exact Cairo fallback remains for
Bold. The restored approved font/layout and recorder text matrix passes 54/54.

This experiment adds no Cairo import reduction. It keeps the recorded
Subpixel matrix honest instead of weakening the established pixel gate; the
font-parity fallback observed at 1.5x and 2x remains a measured open item.

### Skia layout hinting and Radeon verification (2026-10-18)

The differential layout test found one wrap mismatch at 1.5x scale: Skia with
Slight hinting measured `Inventory items` at 176.936px / 177.432px advance,
while Cairo reported 178px / 179px. A 177.936px box therefore wrapped at
different words. Using Normal hinting for Skia measurement produces a 179px
advance with the same Fontconfig-matched face. The code change is restricted
to `OptimumSkiaTextMetrics`; the recorded text raster path keeps its existing
Subpixel settings. The regression test compares the line split from the
`CairoFont` overload with native Cairo across the existing scale and line-break
matrix.

The exact code commit `15809832fe60c7c194ac18a55dfbd77732a55780` passed the
Pharos inventory scenario 1/1 on AMD Radeon RX 6650 XT, Mesa 26.0.8-1ubuntu0.3,
OpenGL 4.6 Core. Initial composition passed 38/38 candidates; ten
recompositions passed 360/360. Scales 1x, 1.5x and 2x passed 30/30, 159/171,
and 161/173 candidates. The 24 scale fallbacks were the already classified
`text-font-parity` Subpixel Cairo fallback; all eight materializations
succeeded with zero failures. The primitive, five non-subset fixtures, three
SVG tint comparisons (within one channel value), and GL state sentinel passed.
The launcher validated the exact local profile certificate.

The tested `cairo-sharp.dll` SHA-256 is
`8d61eed94f170556d806fcee71342e62a85b7c7cdcb9ca346df790bafa4e5bde`; the API
contracts SHA-256 is
`2e856df3972d78c3a142f5afe5ee0b8c2edbbc59cd2f639bc5923204b250fd39`. Evidence
SHA-256 is
`3fc05c1ad8f562106388fee63a41a66c20d646efc6d8db9978537ffe44565916`, and the
certificate SHA-256 is
`8e1b0da013f2474c3bfd43ad10be4277e8869613a7daedf468c8bf9a075e8d7e`. Pharos
source was based on `4966732269f02408df314ae155bb7e6f3817f9d9`; that checkout
contains uncommitted changes, so this is exact Optimum-code evidence but not a
clean, independently reproducible Pharos source revision.

This iteration fixes the measured line-wrap mismatch and certifies the mixed
Cairo/Skia GUI path; it removes no Cairo imports. The inventory remains at 239
active imports, 82 exported types, and 872 public/protected members. The
1.5x/2x Subpixel text materializations remain explicitly visible in the
runtime evidence. This result does not close Cairo-denied startup, packaging,
Intel/NVIDIA, or cross-platform ABI gates.

## Runtime loading, package and rollback boundaries

The wrapper registers a native resolver when `CairoAPI` initializes. Linux
maps `libcairo-2` to `libcairo.so.2`; macOS tries an explicit dylib path and
then the system loader; Windows falls through to the default resolver. The
macOS explicit path currently combines `Assembly.Location` as if it were a
directory, so its candidate includes `cairo-sharp.dll/Lib/...`; only the
fallback might load the dylib. No macOS runtime run has established which
library path succeeds.

The launcher assembly resolver can load managed dependencies from its cache,
the game root, `Mods`, and `Lib` in priority order. It has no Cairo-specific
selection rule, and the native resolver handles OpenAL only. A stale
`cairo-sharp.dll` in an earlier location can therefore precede the candidate
in `Lib`. Runtime validation checks managed references but does not prove the
selected wrapper version, helper method binding, native library origin, or
absence of Cairo loads.

The installer backs up and restores `Lib/cairo-sharp.dll`; it does not manage
other shadow copies or native Cairo files. Standalone Linux and macOS packages
copy the vanilla installation before overlaying Optimum assemblies, so those
packages may still carry Cairo and its dependencies. Local inspection of
`libSkiaSharp.so` and `libnanosvg.so` found no direct Cairo dependency, but the
final artifacts for each RID and their transitive dependencies remain
unverified. The ABI fixture and parity harness explicitly load native Cairo,
so their successful execution cannot serve as a Cairo-denied runtime test.

The release gate still needs final-package file/import inventories per RID,
managed assembly resolution tests with stale wrapper copies in cache/root/
Mods, candidate/helper validation, install/upgrade/rollback/uninstall tests,
and startup plus GUI/map/sign/export/readback execution while every discovered
Cairo load path is denied. Compiled third-party mods and plugins remain an
external compatibility boundary.

## Explicit exclusions and unresolved compatibility

- The tracked source inventory and locally available source trees do not reveal
  calls made only by compiled third-party mods or native plugins.
- A caller that passes a real `cairo_t*`/surface pointer, P/Invokes libcairo
  itself, or expects `Context.Handle` to remain a Cairo pointer cannot use an
  opaque managed replacement and retain that behavior. Such consumers need a
  verified bridge or a documented scope decision.
- The assembly includes NanoSVG and FreeType API surface as well as Cairo API.
  Removing `libcairo` does not automatically remove those native libraries.
- A complete API-compatible managed assembly is a broader gate than a
  Cairo-free supported GUI package. No current test proves all 872 public and
  protected members or every platform backend.

## Next work

Continue the staged managed-state and text migration with the native oracle in
tests, then replace image/source/compatibility escapes. Do not claim completion
until every public API row and every runtime/package path has an implementation
or an explicit limitation, and the packaged supported client runs with native
Cairo loading denied. Record GPU certification separately; it does not prove
Cairo independence.
