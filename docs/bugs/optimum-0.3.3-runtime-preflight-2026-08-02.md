# Optimum 0.3.3 runtime preflight

This note records the launcher failure that appeared after the 0.3.2 recovery work reached the 0.3.3 Windows package.

## Evidence

The installer log at `AppData/Local/Optimum/optimum-install-2026-08-02T2012.log` reports 95 bootstrap patches, 0 skipped patches, 0 failures, and a successful package copy. It also reports `ilspycmd` 10.1.0.8386 while the repository pins 10.1.1.8388.

The first launcher run records successful VintagestoryLib, VintagestoryAPI, and VSEssentials patches. The VSSurvivalMod subprocess rejects its output with two self-reference errors:

`GearRenderer::DisableOptimumGearRenderer references missing field optimumGearRendererFailureLogged`

The launcher then reports `vssurvivalmod patch produced no changes` and starts the vanilla fallback. The game log from the same installation shows vanilla built-in mods after that restoration. A separate current game log belongs to a different, unrelated Vintage Story install, so it does not validate the failed Optimum run.

A second review found an independent API risk after the Survival fix. The first `GetPose` fallback consumed `animationManager` and `Animator` with `brfalse`, then called the next member without an instance on the evaluation stack. ILSpy reported stack underflow in the generated method even though reflection loaded the assembly.

## Root cause

`GearRenderer` contains the vanilla type, while Optimum adds two fields and the `DisableOptimumGearRenderer` helper. The mod manifest requested the helper method but not its fields. The patcher injected the helper body during the method transplant pass, but the injector only scanned the original transplanted method for missing fields. The verifier caught the helper's unresolved field reference before it wrote the output assembly. A second strict run exposed two more incomplete manifest assumptions: the three-argument `EntityDressedHumanoid.OnTesselation` target does not exist in the 1.22.5 vanilla assembly, and `PathNode.Equals` has two one-argument overloads. The manifest now marks the first target optional and identifies the second target by its `PathNode` parameter type.

## Implementation

`MemberInjector` now walks the dependency closure of every injected helper. It copies same-type fields and helper methods into the target assembly, then scans each newly injected helper. The GearRenderer fixture asserts both fields, the helper, and an empty self-reference report. The patcher now requires every non-optional manifest item and uses complete Cecil method signatures. The verifier also resolves generic method instances against their open method definition.

`ModPatcher` now distinguishes a rejected assembly from a patch with zero changes. The launcher invalidates a failed cache, restores built-in vanilla mods, logs `Launch aborted`, and returns exit code 1. It never starts `Vintagestory.exe` after an Optimum failure. `AssemblyLoader` rejects missing required patched assemblies instead of resolving them from the game directory.

The launcher accepts `--validate-only`. It invalidates the cache, patches all four targets, loads all four assemblies, reflects their types, checks `ClientProgram.Main`, and exits without starting the game. The launcher takes an exclusive data lock, drains patcher output without pipe deadlock, and fails after a five-minute patcher timeout. The Windows installer checks the package completion marker, checks the packaging result, runs that command against the staged package, and refuses to copy the package when the command returns a nonzero status. The installer swaps the staged tree through a temporary directory and restores the previous tree when the copy fails. The installer and bootstrap scripts accept stable four-part `ilspycmd` versions from `10.1.0.8386` through `10.1.1.8388`, inclusive; the manifest keeps `10.1.1.8388` as the preferred pin.

The API fallback now stores both interface values in locals before it tests them. `IlStackVerifier` rejects evaluation-stack underflow, inconsistent branch depths, invalid returns, and invalid branch targets before any patcher writes its output. The launcher JIT-validates every non-generic method in each patched assembly during `--validate-only`, which catches invalid method bodies that type reflection cannot detect. The loader also rejects a required assembly that resolves from a path outside the cache, and the launcher locks both the selected data path and the game directory.

## Verification

The release test suite covers helper dependency injection, complete-signature matching, fail-closed launcher behavior, required cache entries, evaluation-stack validation, the staged Windows preflight, the installation rollback path, and the inclusive decompiler range. Real engine, API, Essentials, and Survival patch runs finished without self-reference or IL errors. The staged preflight JIT-validated 41,593 methods across four patched assemblies, then loaded and reflected them. A missing Survival donor returned a nonzero result and logged `Launch aborted`.
