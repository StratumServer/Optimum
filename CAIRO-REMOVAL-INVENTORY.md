# Cairo removal inventory

Baseline: branch `feat/issue-130-recorder-stage-a`, commit `f4cffb5` (2026-10-17).
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

`tools/Cairo.ParityHarness/cairo-api-manifest.json` lists 81 exported types and
868 public/protected members. The manifest itself says every row needs human
review before ABI compatibility can be claimed. It omits some ABI facts such as
field layout, marshalling, and calling convention, so it is not by itself a
complete compatibility specification. Its current automatic labels cover all
868 rows: 351 `metadata`, 254 `native-only-pending`, 205
`recordable-candidate`, 39 `measurement`, and 19 `materialization-boundary`.
Every row still has `classificationReview: required`; these counts are an
initial sorting aid, not a completed manual classification.

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
| Public native API and ABI | `Context`, `Surface`, `ImageSurface`, `Pattern`, `Path`, fonts, `Region`, `Device`, PDF/PS/SVG and platform surfaces expose native pointers, ownership or backend behavior. Assembly identity is `cairo-sharp` 1.0.0.0. | Classify all 868 members and define behavior/limitations for pointer constructors, `Handle`, `DataPtr`, layouts, ownership, encoders and platform backends. Keeping names does not preserve native-pointer interoperability. |
| Other native libraries | `Cairo/Svg/SvgNativeMethods` exposes NanoSVG P/Invokes; FreeType helpers expose FreeType APIs. `RecordedText` declares Cairo and Fontconfig imports separately. | Track NanoSVG, FreeType and Fontconfig as distinct dependencies; do not count their remaining imports as Cairo or remove assets they still need. |
| Build, patch, install and rollback | Patcher rewrites APIs and hooks recorder metrics; launcher certification fingerprints `cairo-sharp`; overlay/package scripts stage it; installer backs up/restores the wrapper. Linux/macOS packages copy broad vanilla trees/bundles. | Update donors, IL contracts, artifact fingerprints, package manifests and rollback together. Audit packaged native binaries and dependency chains, not just source references. |
| Tests and certification | ABI fixture intentionally loads native Cairo; parity harness and GPU certificate test current mixed Skia/Cairo behavior. Stage F/G have CPU tests but no GPU retest for their exact head. | Keep native oracle tests separate from runtime proof. Test hardware parity for the exact published head and separately test a packaged client with Cairo loading blocked. |

## Native import inventory

`Cairo/NativeMethods.cs` contains 330 declarations and 329 unique Cairo
entrypoints. Static source analysis found 241 unique entrypoints with wrapper
callers and 88 with no wrapper source call. Six declarations with no qualified
`NativeMethods` caller have equivalent calls through separate imports in
`RecordedText`. Removing dormant declarations is cleanup only; it does not
remove the active native runtime dependency. The remaining active imports need
to be classified by the managed replacements above before deleting the native
library.

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
  Cairo-free supported GUI package. No current test proves all 868 public and
  protected members or every platform backend.

## Next work

Continue the staged managed-state and text migration with the native oracle in
tests, then replace image/source/compatibility escapes. Do not claim completion
until every public API row and every runtime/package path has an implementation
or an explicit limitation, and the packaged supported client runs with native
Cairo loading denied. Record GPU certification separately; it does not prove
Cairo independence.
