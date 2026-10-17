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
| Drawing state, paths and geometry | `Context` owns path, transforms, clips, groups, operators, dashes, extents and hit-testing. Recorder capture reads the native state/path. Stage F removed managed matrix arithmetic only. | Managed state/path model, differential tests, current-point/save-restore semantics, and no normal shadow-context calls. |
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

## Native import inventory

The schema-2 manifest includes **299 active `DllImport` methods** reflected from
the current assembly: 269 for `libcairo-2`, 13 for `freetype6`, 9 for
`libfontconfig.so.1`, and 8 for `nanosvg`. The 269 Cairo imports comprise 261
methods in `Cairo.NativeMethods` and 8 separate declarations in `RecordedText`.
Every `nativeImports` row is marked for manual native-header review; reflection
records declarations but does not verify that their native signatures are
correct. A separate source-to-manifest pass matched all **299/299 active
declarations** by declaring type, return/parameter types, library, entrypoint,
calling convention, direction and marshal attributes. The 629 parameters
contain 64 `IsOut` and 0 `IsIn` flags; the reflection metadata preserves these
separately from by-reference type syntax. The manifest contains 286 `Cdecl`
and 13 `Winapi` imports. This proves inventory correspondence, not native ABI
correctness.

`Cairo/NativeMethods.cs` has 330 raw `extern`/`DllImport` matches, but 69 are in
comments. After stripping comments there are **261 active declarations**, with
260 unique names because `cairo_image_surface_create_for_data` has two
overloads. Reflection reports the same 261 active `NativeMethods` imports.
Source search finds 235 unique names called as `NativeMethods.cairo_*`; six
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
