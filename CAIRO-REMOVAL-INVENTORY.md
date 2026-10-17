# Cairo removal inventory

Baseline: branch `feat/issue-130-recorder-stage-a`, commit `fb4f526` (2026-10-17).
This inventories repository responsibilities and removal gates. It is not a
claim that native Cairo has been removed or that third-party binaries are
compatible.

## Search coverage

The audit enumerated all Git-tracked paths instead of relying on normal `rg`,
which skips ignored source overlays. It read 657 tracked text files; 125 contain
a case-insensitive `cairo` reference. It also searched the locally available
ignored source trees for `VintagestoryApi`, `VSEssentials`, `VSSurvivalMod`,
`VSCreativeMod`, and `build/VintagestoryLib`. Donors and generated snapshots are
evidence of patch inputs, not proof of runtime execution.

`tools/Cairo.ParityHarness/cairo-api-manifest.json` schema 2 lists 82 exported
types and 871 public/protected members from the current branch assembly. The
previous schema-1 file was stale: it had 81 types/868 members and omitted
`Cairo.OptimumSkiaTextMetrics` plus two methods on it and the protected
`Surface.nativeDataExposed` field. The generator now records member accessibility,
method flags, parameter/return `MarshalAs`, public `DllImport` details, type
layout and instance field offsets where reflection supports them. This run used
.NET 10.0.12, Ubuntu 26.04.1 x64, and 8-byte pointers. It records environment
because layout may differ by architecture.

The 871 automatic labels are 351 `metadata`, 254 `native-only-pending`, 205
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
| `Context`, `Graphics`, `Path`, `Matrix`, `Glyph`, `TextExtents`, `FontExtents` | 7 | 248 |
| `Surface`, `ImageSurface`, patterns/gradients, font option/face/scaled font APIs | 11 | 149 |
| All other exported Cairo, NanoSvg, FreeType, backend, and utility types | 64 | 474 |
| **Complete exported API** | **82** | **871** |

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
| Public native API and ABI | `Context`, `Surface`, `ImageSurface`, `Pattern`, `Path`, fonts, `Region`, `Device`, PDF/PS/SVG and platform surfaces expose native pointers, ownership or backend behavior. Assembly identity is `cairo-sharp` 1.0.0.0. | Classify all 871 members and define behavior/limitations for pointer constructors, `Handle`, `DataPtr`, layouts, ownership, encoders and platform backends. Keeping names does not preserve native-pointer interoperability. |
| Other native libraries | `Cairo/Svg/SvgNativeMethods` exposes NanoSVG P/Invokes; FreeType helpers expose FreeType APIs. `RecordedText` declares Cairo and Fontconfig imports separately. | Track NanoSVG, FreeType and Fontconfig as distinct dependencies; do not count their remaining imports as Cairo or remove assets they still need. |
| Build, patch, install and rollback | Patcher rewrites APIs and hooks recorder metrics; launcher certification fingerprints `cairo-sharp`; overlay/package scripts stage it; installer backs up/restores the wrapper. Linux/macOS packages copy broad vanilla trees/bundles. | Update donors, IL contracts, artifact fingerprints, package manifests and rollback together. Audit packaged native binaries and dependency chains, not just source references. |
| Tests and certification | ABI fixture intentionally loads native Cairo; parity harness and GPU certificate test current mixed Skia/Cairo behavior. Stage F/G have CPU tests but no GPU retest for their exact head. | Keep native oracle tests separate from runtime proof. Test hardware parity for the exact published head and separately test a packaged client with Cairo loading blocked. |

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
surface-recorder, and recorder-text suites now pass 149/149. This saves three
native getter calls per captured clip by static inspection; no call counter or
timing measurement was run.

## Native import inventory

The schema-2 manifest includes **296 active `DllImport` methods** reflected from
the current assembly: 266 for `libcairo-2`, 13 for `freetype6`, 9 for
`libfontconfig.so.1`, and 8 for `nanosvg`. The Cairo imports comprise 258
methods in `Cairo.NativeMethods` and 8 separate declarations in `RecordedText`.
Every `nativeImports` row is marked for manual native-header review; reflection
records declarations but does not verify that their native signatures are
correct. A separate source-to-manifest pass matched all **296/296 active
declarations** by declaring type, return/parameter types, library, entrypoint,
calling convention, direction and marshal attributes. The 623 parameters
contain 63 `IsOut` and 0 `IsIn` flags; the reflection metadata preserves these
separately from by-reference type syntax. The manifest contains 283 `Cdecl`
and 13 `Winapi` imports. This proves inventory correspondence, not native ABI
correctness.

`Cairo/NativeMethods.cs` has 328 raw `extern`/`DllImport` matches, but 69 are in
comments. After stripping comments there are **258 active declarations**, with
257 unique names because `cairo_image_surface_create_for_data` has two
overloads. Reflection reports the same 258 active `NativeMethods` imports.
Source search finds 232 unique names called as `NativeMethods.cairo_*`; six
additional names have equivalent direct imports in `RecordedText`. The other
19 unique active Cairo entrypoints have no caller found in the available
wrapper/consumer source. Those 19 may be removable internal declarations, but
removing them is cleanup only. All reachable imports and runtime-created calls
still need a managed replacement or an explicit limitation before native Cairo
can be removed. Header/platform review still needs to resolve, among other
items, Cairo boolean and `unsigned long` widths, status values discarded by
wrappers, FreeType parameter widths and calling convention, string encodings,
NanoSVG variant exports, and the fixed 24-byte glyph stride. The import table
does not include runtime dynamic loading or transitive native dependencies.

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
  Cairo-free supported GUI package. No current test proves all 871 public and
  protected members or every platform backend.

## Next work

Continue the staged managed-state and text migration with the native oracle in
tests, then replace image/source/compatibility escapes. Do not claim completion
until every public API row and every runtime/package path has an implementation
or an explicit limitation, and the packaged supported client runs with native
Cairo loading denied. Record GPU certification separately; it does not prove
Cairo independence.
