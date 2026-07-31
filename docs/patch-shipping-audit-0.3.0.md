# Optimum 0.3.0 Patch Shipping Audit

This document records which 0.2.x optimizations reach the 0.3.0 package for
Vintage Story 1.22.5 and explains why a source patch can remain absent from the
final assembly.

## Shipping rule

The public package keeps the user's vanilla assemblies intact. `Optimum.exe`
creates patched copies at launch. A patch ships only when one of these paths
owns its target:

| Path | Owner |
|---|---|
| Engine transplant | `Optimum.Patcher/Program.cs` |
| API hooks | `Optimum.Patcher/api-patcher.cs` |
| Essentials and Survival donor | `Optimum.Patcher/mod-patcher.cs` |
| Shaders and translations | `scripts/package.ps1` |

The bootstrap log reports source donor patches. It does not prove that the
final staged DLL contains every donor change.

## Status

Legend: ✅ shipped, ⚠️ partial, ❌ absent from the final assembly, ◻️ source-only
cleanup.

| Patch or optimization | Status | Cause or shipping path |
|---|---:|---|
| Frame pacing, background FPS and timer resolution | ✅ | Engine Cecil targets |
| Entity, shadow, vegetation and occlusion culling | ✅ | Engine Cecil targets |
| Dynamic light cache, radius and batching | ✅ | Engine plus Essentials donor |
| Particle and weather throttling | ✅ | Engine plus Essentials donor |
| Ambient, sky, GUI and chunk scratch buffers | ✅ | Engine Cecil targets |
| Greedy meshing and optimized shaders | ✅ | Engine target plus shader overlay |
| Item culling, entity batching and AStar pool | ✅ | Essentials runtime manifest |
| Weather, map cache, treegen and path fixes | ✅ | Essentials runtime manifest |
| Survival cooking, containers, prospecting and mechanics | ✅ | Survival runtime manifest |
| Chisel LOD | ✅ | API frustum hook and Optimum bridge |
| Inventory hooks, logger initializer and version label | ✅ | API patcher rules |
| `EntityPos` and `ColorUtil` | ⚠️ | Selected callers use bridge methods; vanilla API type remains |
| `DefaultShaderUniforms` view-vector allocation | ⚠️ | Only selected consumers use the bridge |
| Animated block LOD and `GearRenderer.SkipAnimLod` | ❌ | No API or Survival runtime rule |
| `Mat4f` inlining | ❌ | No API rule for method metadata |
| `SortableQueue` and `UniqueQueue` | ❌ | Vanilla API implementation remains |
| `AnimationManager` and `AnimatorBase` allocation fixes | ❌ | No engine Cecil targets |
| `GuiDialog`, `GuiElementStatbar` and recipe scissor cache | ❌ | No engine Cecil targets |
| Psychedelic camera pitch clamp | ❌ | No runtime perception-effect rule |
| `EntityBlockFalling` mesh fix | ❌ | No Essentials manifest entry |
| Survival handbook exception containment | ❌ | No Survival manifest entry |
| Mesh pool diagnostics | ❌ | Chisel behavior ships, diagnostics do not |
| Server GC and DATAS settings | ❌ | Launcher runtimeconfig does not carry the old settings |
| `IMergeable`, `BehaviorWearable` and `EntityPlayerBot` cleanup | ◻️ | Syntax or nullable cleanup only |
| Drunk and base perception reconciliation | ◻️ | Decompiler/source reconciliation only |

## Root cause of each loss

The 0.2.x release copied complete optimized assemblies. The 0.3.0 release uses
vanilla files plus selected runtime transplants. The API patcher currently owns
only inventory hooks, chisel LOD, logger initialization and the version label.
The engine patcher and mod patcher also use explicit target manifests. A source
patch outside those lists compiles into a donor but never enters the staged
assembly.

The package also uses `Optimum.runtimeconfig.json`. The old package carried GC
settings from the compiled game output, so the new launcher must merge approved
GC and DATAS settings explicitly if the release requires them.

## Release gate

Every release must inspect the final staged API, engine and mod assemblies. The
gate must map every source patch to an engine target, API rule, runtime manifest
or asset overlay, and fail when no shipping owner exists.
