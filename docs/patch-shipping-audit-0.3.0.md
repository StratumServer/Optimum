# Patch Shipping Audit for Optimum 0.3.0

This audit compares the optimization set from the 0.2.x release model with the 0.3.0 runtime-donor model for Vintage Story 1.22.5.

**Updated 2026-07-27:** Source patches restored after the 2026-07-25 baseline reset (`395f51d`) deleted 94 tracked patches. 86 patches now tracked (61 applied + 25 cecil). The ❌ rows below indicate patches that compile into a donor but have no shipping rule (Cecil target or runtime manifest entry) to reach the player's assemblies at runtime. The source patches exist and build; shipping them requires adding the corresponding Cecil/manifest rules.

## Result

The 0.3.0 bootstrap can apply a patch to a decompiled source tree while the final installer leaves the corresponding vanilla assembly untouched. The build log therefore cannot prove that a patch reaches players.

The 0.2.x package copied complete optimized assemblies into the game folder. The 0.3.0 Windows package copies a vanilla installation, then lets `Optimum.exe` patch selected assembly copies at launch. The package keeps the vanilla engine and built-in mods in place, as shown in [`scripts/package.ps1`](../scripts/package.ps1).

The release pipeline has four shipping paths:

| Path | Current role | Selection mechanism |
|---|---|---|
| Engine Cecil transplant | Ships client engine changes | Targets in `Optimum.Patcher/Program.cs` |
| API Cecil patch | Ships ABI-safe API hooks | Methods in `Optimum.Patcher/api-patcher.cs` |
| Runtime mod donor | Ships selected Essentials and Survival changes | Manifests in `Optimum.Patcher/mod-patcher.cs` |
| Asset overlay | Ships shaders and Optimum language strings | `scripts/package.ps1` |

Any patch outside those selections remains a source-only donor. The following table records the resulting status.

## Complete status inventory

Legend: ✅ ships in the final package, ⚠️ ships in part or only through a replacement path, ❌ does not ship, ◻️ source cleanup without behavior change.

| Patch or optimization | Status | Reason |
|---|---:|---|
| Background FPS, precise pacing, timer resolution | ✅ | `Program.cs` targets the client settings and platform types. |
| Entity pre-cull, shadow cull, far vegetation | ✅ | Cecil targets inject the client render members and methods. |
| Dynamic light cache, radius, batching, shader state cache | ✅ | The engine target list and the Essentials donor cover the client and entity paths. |
| Particle distance gate and weather throttling | ✅ | The engine and Essentials donor each carry their selected half. |
| Ambient, sky, GUI, chunk sorting and occlusion scratch buffers | ✅ | Cecil targets include the corresponding client types. |
| Greedy meshing and optimized chunk shaders | ✅ | The engine target list and `sources/shaders` overlay ship them. |
| Mouse wheel, disposed mesh, CreativeTab and SvgLoader fixes | ✅ | Cecil targets inject the fixes into the client assembly. |
| Entity item culling and entity light batching | ✅ | The Essentials runtime manifest names the methods and members. |
| Collect, repulse, AStar, PathNode, weather, map cache and treegen changes | ✅ | The Essentials runtime donor manifest names the affected types. |
| Survival cooking, container, prospecting and mechanical allocation changes | ✅ | The Survival runtime donor manifest names the affected types. |
| Chisel LOD | ✅ | `ApiPatcher.PatchChiselLodHook` redirects the vanilla frustum call to the Optimum bridge. |
| Inventory dirty hooks | ✅ | `ApiPatcher` injects two hooks into the vanilla API. |
| Logger initializer | ✅ | `ApiPatcher` rewrites the vanilla static initializer. |
| `+ Optimum v0.3.0` version label | ✅ | `ApiPatcher` rewrites `GameVersion.LongGameVersion`. |
| `EntityPos` and `ColorUtil` nonalloc helpers | ⚠️ | The API source patch does not enter the vanilla API DLL. Selected client callers use Optimum bridge methods. |
| `DefaultShaderUniforms` view-vector allocation | ⚠️ | The bridge serves selected callers, but the vanilla API type still allocates. |
| Animated block LOD in `AnimationUtil` | ❌ | The source patch compiles into a donor, but no API runtime rule injects it into the vanilla API. |
| `GearRenderer.SkipAnimLod` | ❌ | The runtime manifest does not target `GearRenderer`, and the field depends on the missing animation LOD path. |
| `Mat4f` aggressive inlining | ✅ | `ApiPatcher.PatchMat4fInlining` ships this. |
| `SortableQueue` reusable sort buffer and `ItemAt` | ❌ | The vanilla API DLL remains the shipped implementation. |
| `UniqueQueue` linked-list removal | ❌ | The vanilla API DLL remains the shipped implementation. |
| `AnimationManager` LINQ allocation removal | ◻️ | N/A: verified 2026-07-27 that `.Any()` calls were already removed upstream in 1.22.5. No patch needed. |
| `AnimatorBase` case-insensitive comparison optimization | ✅ | `ApiPatcher.PatchAnimatorAnimCodeComparer` ships this (fixed 2026-07-31). |
| `GuiDialog` composer and prospecting mouse fixes | ❌ | The engine patch has no corresponding Cecil target. |
| `GuiElementStatbar` decimal maximum and tooltip fix | ❌ | The engine patch has no corresponding Cecil target. |
| `SlideshowGridRecipeTextComponent` scissor cache | ❌ | The engine patch has no corresponding Cecil target. |
| Psychedelic camera pitch clamp | ❌ | The vanilla API still applies the unclamped pitch delta, and no runtime rule replaces it. |
| `EntityBlockFalling` shared-mesh fix | ◻️ | N/A: the 1.22.5 renderer rewrite eliminated the shared-mesh pattern entirely. The bug is fixed upstream. |
| Survival handbook item exception containment | ❌ | Source patch restored 2026-07-27 (`SurvivalHandbook.cs`). Compiles into the VSSurvivalMod donor but the Survival runtime manifest does not yet name the handbook page constructor path. Needs a manifest entry to ship. |
| Mesh pool upload, draw and cull diagnostics | ❌ | The chisel bridge ships, but the diagnostic members have no runtime target. |
| Server GC and DATAS runtime settings | ❌ | The package launches through `Optimum.runtimeconfig.json`, which does not carry the 0.2.x GC settings. |
| Common and server-only source patches | ⚠️ | The donors compile them, but the client package does not transplant those server paths. |
| `IMergeable`, `BehaviorWearable`, `EntityPlayerBot` source cleanup | ◻️ | These patches normalize source syntax or nullable annotations. They do not add runtime behavior. |
| `DrunkPerceptionEffect` and base `PerceptionEffect` reconciliation | ◻️ | These changes reconcile decompiler output with the vanilla baseline. |
| Psychedelic source cleanup | ❌ | Its pitch clamp changes behavior, so source cleanup cannot replace the missing runtime patch. |

## Why each loss occurs

### 1. The package keeps vanilla assemblies

[`scripts/package.ps1`](../scripts/package.ps1) copies the local Vintage Story installation into the stage directory and keeps the engine and built-in mods vanilla. The launcher stores donors under `.optimum/donors`, then patches selected copies at startup. A source patch does not reach the game unless a Cecil rule or a runtime manifest selects its target.

### 2. The API patcher has a narrow whitelist

[`Optimum.Patcher/api-patcher.cs`](../Optimum.Patcher/api-patcher.cs) currently applies four groups:

1. two inventory dirty hooks;
2. one chisel LOD hook;
3. one logger initializer rewrite;
4. one game-version label rewrite.

The patcher does not transplant arbitrary API types, change method metadata, or replace full API method bodies. That design drops `AnimationUtil`, `Mat4f`, `SortableQueue`, `UniqueQueue`, and the perception-effect changes.

### 3. The engine patcher also has a target whitelist

`Optimum.Patcher/Program.cs` lists the engine types and members that Cecil can transplant. Patches for `AnimationManager`, `GuiDialog`, `GuiElementStatbar`, and the recipe component do not appear in that target list. The bootstrap can still report those source patches as applied because it builds the donor tree. (`AnimatorBase`'s comparer fix now ships separately via `ApiPatcher.PatchAnimatorAnimCodeComparer` since it's an API-side IL patch, not an engine Cecil transplant.)

### 4. Runtime mod manifests select individual members

[`Optimum.Patcher/mod-patcher.cs`](../Optimum.Patcher/mod-patcher.cs) injects only the types, members and methods listed by `EssentialsManifest()` and `SurvivalManifest()`. `EntityBlockFalling`, handbook pages and `GearRenderer` do not appear there, so their donor implementations never enter the shipped mod DLLs.

### 5. Runtime configuration does not follow the engine donor

The old package copied runtime settings from the compiled game output. The new launcher runs through its own `Optimum.runtimeconfig.json`. Without an explicit merge step, GC server mode and dynamic adaptation settings disappear even though the managed code still builds.

## Safe source removals

The following removals do not remove an Optimum behavior:

- `IMergeable` formatting and nullable cleanup;
- `BehaviorWearable` and `EntityPlayerBot` nullable cleanup;
- `DrunkPerceptionEffect` and base `PerceptionEffect` decompiler reconciliation.

The psychedelic pitch clamp does not belong in that list. It changes camera behavior and needs a runtime migration.

## Release gate

Before publishing another installer, the release job must inspect the final staged assemblies, not only the bootstrap log. The gate must:

1. build the source donors;
2. run the source patch check;
3. run the Cecil and runtime donor patchers;
4. decompile or inspect the staged API, engine and mod DLLs;
5. assert each required optimization marker or method body;
6. compare `Optimum.runtimeconfig.json` with the approved GC settings;
7. fail when a source patch lacks a shipping owner.

The graph generated during this audit shows the same boundary: the source inventory contains many patch nodes, while the runtime patcher exposes only the small set of selected mutation paths. See [`graphify-out/GRAPH_REPORT.md`](../graphify-out/GRAPH_REPORT.md).
