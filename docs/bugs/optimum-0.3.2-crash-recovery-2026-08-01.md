# Optimum 0.3.2 crash recovery

This note records the fixes for the Optimum 0.3.2 reports from 2026-08-01. The reports cover two render crashes, one shared-API server crash, one Windows line-ending failure, and one Windows removal failure.

## Report matrix

| Report | Evidence | Fix |
|---|---|---|
| Komako, OIT render pass | `SystemRenderOITLayers.BeforeOIT.OnRenderFrame` called `Use()` on a disposed shader during the transparent render stage. | Validate the framebuffer and both shader programs, contain the render callback, restore the active shader, and disable only the OIT feature after a resource failure. |
| Vigilance, `TemporalStability\GearRenderer` | `GearRenderer.OnRenderFrame` dereferenced `tripodAnim.renderer` after animation setup returned without a renderer. | Guard initialization, shader loading, mesh and shader lifetime, the render callback, and the state update in both Survival donors. Add all four methods to the Survival runtime manifest. |
| ImpureSoul3, `EntityHeadController.GetPose` | The stack shows a null `animationManager.Animator` during player tessellation. | Skip head-controller construction when the animator is absent. Rewrite the vanilla API method with a Cecil fallback that returns an empty `ElementPose`. |
| ImpureSoul3, server spawn | `server-main.log` and `server-main (1).log` show `MissingMethodException` for the two-argument `Dictionary` constructor while `AnimatorBase` initializes. | Stop shipping the shared `AnimatorBase` comparer rewrite. Keep the experiment in the source donor and test it without changing the runtime API assembly. |
| ChestyLaRue, Windows setup | Git checkout settings left CRLF content in cached sources and patch files. | Set repository EOL options during every clone, normalize generated trees, normalize `patches` and `sources`, and mark tracked text files with LF attributes. |
| KandelKitty, Windows removal | The old uninstall command embedded a quoted PowerShell command inside a registry string. | Ship `uninstall.ps1`, register its file path, let a delayed command file remove a standalone package after PowerShell exits, and report cleanup failures with a log path. Legacy in-place installs remove Optimum files and leave Vintage Story files. |
| Launcher cache recovery | A damaged cached assembly could pass the old file-presence check and reach launch. | Record the patched assembly hash, validate it on every cache check, write assemblies and manifests through temporary files, and re-patch after any validation exception. |

## Render recovery

`GearRenderer.Init` returns when the thunderlord shape fails to load or `AnimationUtil` cannot create a renderer. `LoadShader` catches shader setup failures. `OnRenderFrame` checks the animation renderer, mesh, and shader lifetime, contains the render callback, restores blend and shader state, and disables the feature after a failure. `updateSuperMechState` uses the same feature boundary. The normal donor uses the source patch at [`GearRenderer.cs.patch`](../../patches/VSSurvivalMod/Systems/TemporalStability/GearRenderer.cs.patch). The Windows runtime donor uses [`GearRenderer.cs.patch`](../../patches/runtime/VSSurvivalMod/Vintagestory/GameContent/GearRenderer.cs.patch).

`Optimum.Patcher/mod-patcher.cs` selects `GearRenderer`, `Init`, `LoadShader`, `OnRenderFrame`, and `updateSuperMechState`. The launcher can transplant the guarded methods into the Survival mod copy that the package patches at startup.

## OIT recovery

`SystemRenderOITLayers.BeforeOIT` validates the transparent framebuffer, the numeric OIT shader, and the `cloudvolumetric` shader before it calls any shader method. The callback catches OpenGL and shader failures, frees its textures, restores the previous shader when possible, and disables OIT for the rest of the session. `AfterOIT` skips missing textures and contains texture binding failures. The Cecil patcher injects the feature state and transplants both nested renderer callbacks.

## Animation recovery

`EntityPlayer.OnTesselation` creates a `PlayerHeadController` when its animation manager holds an animator. `EntityHeadController.GetPose` returns an empty `ElementPose` when the manager, animator, or named pose does not exist. The source patch covers Linux, macOS, and donor builds.

Windows keeps the vanilla API assembly as its input, so [`api-patcher.cs`](../../Optimum.Patcher/api-patcher.cs) replaces the `GetPose` method body with equivalent Cecil instructions. The patcher requires one matching method and fails when the vanilla API shape changes. This keeps a version mismatch visible during packaging.

## Shared API ABI recovery

`server-main.log` and `server-main (1).log` capture `System.MissingMethodException` while MonoMod JIT-hooks `AnimatorBase`. The failing signature uses the two-argument `Dictionary` constructor with an `IEqualityComparer<string>` parameter. The old API patcher created that method reference from the patcher's .NET runtime, while the game API resolved its framework references from a different assembly scope. The server could not bind the constructor and could not spawn the player.

Optimum 0.3.3 leaves `PatchAnimatorAnimCodeComparer` outside the runtime API path. The source donor keeps the experiment for comparison, and `AnimatorAnimCodeComparerTests` keeps its IL transformation covered. The runtime patcher preserves the vanilla shared API so an integrated server follows the vanilla `AnimatorBase` path.

## Line endings

The Bash and PowerShell bootstraps normalize C#, project files, patch files, scripts, and solution files. Git clone commands set `core.autocrlf=false` and `core.eol=lf`. The repository attributes record LF for tracked text. Runtime donor preparation applies the same patch normalization before it creates a donor project.

## Windows removal

The Windows package copies `scripts/uninstall.ps1` to the package root and writes `.optimum/standalone-install`. The installer registers the copied script with `-File`, `-NoProfile`, `-NonInteractive`, and `-WindowStyle Hidden`. The script removes shortcuts and the Optimum registry entry, starts a delayed cleanup command, and leaves the vanilla installation outside the package untouched.

The standalone Windows cleanup writes a temporary log with its result. The in-place PowerShell cleanup returns exit code 1 when any Optimum path remains. The Bash cleanup reports failed file and directory removals instead of claiming success.

## Launcher cache recovery

`CacheManager` stores a SHA-256 hash for each patched assembly and rejects a manifest when the cached bytes differ. Cache assemblies, PDB files, and manifests use temporary paths followed by replacement. Cache validation treats I/O and manifest errors as a cache miss, so the launcher enters the existing patch path and reports a nonzero failure if that path cannot produce every required assembly.

## Scope limits

The `ntdll.dll` and `coreclr.dll` event entries lack a managed stack. They do not identify a separate Optimum defect, so this work does not assign those native faults to a code path. The map pre-generation question compares two designs and does not describe a failure, so it receives no code change.

The same server logs contain errors from `AttributeRenderingLibrary`, CarryOn, ForlornAdditions, MoreAnimals, ExpandedFoods, ElTrench, and other content patches. Those stacks name third-party transpilers or missing mod assets, not Optimum code. This release leaves those mod-specific errors unchanged and records them for separate compatibility work.
