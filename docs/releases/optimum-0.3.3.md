# Optimum 0.3.3

Optimum 0.3.3 targets Vintage Story 1.22.5 and carries the crash recovery work from the 0.3.2 reports.

## Fixes

- `SystemRenderOITLayers` validates its framebuffer and shader resources, contains OpenGL failures, restores the active shader, and disables only OIT when the transparent render stage fails.
- `GearRenderer` checks shape creation, renderer creation, mesh and shader lifetime, render callbacks, and state updates before it uses them. The Survival runtime manifest selects `Init`, `LoadShader`, `OnRenderFrame`, and `updateSuperMechState`. The callback restores blend and shader state after a failure.
- `EntityPlayer` skips head-controller setup when the client has no animator. `EntityHeadController.GetPose` returns an empty `ElementPose` when the animation manager, animator, or named pose is missing.
- The API fallback stores the animation manager and animator in locals before each null branch. The patcher runs an evaluation-stack verifier before it writes any generated assembly, so invalid branch stacks fail the patch.
- The runtime API patcher leaves the shared `AnimatorBase` implementation unchanged. The source donor retains the comparer experiment for comparison, while tests cover the IL transformation without shipping it into the integrated server.
- The launcher validates the hash of every cached patched assembly and writes cache files through temporary paths. A malformed manifest triggers a re-patch. An I/O or hash error aborts startup instead of hiding the failure.
- The runtime patcher requires every manifest type, member, interface, method, and hook. It matches methods by return type, generic arity, instance convention, and parameter types. It marks the unavailable three-argument `EntityDressedHumanoid.OnTesselation` target as optional for the 1.22.5 donor.
- IL hooks identify the target declaring type and parameter list. A same-name overload cannot receive a hook by accident.
- `GearRenderer` helper injection now carries its two state fields into the Survival output. The patcher rejects incomplete output before it writes a DLL.
- `Optimum.exe --validate-only` invalidates the cache, rebuilds all four patched assemblies, loads every assembly, reflects every type, JIT-validates every non-generic method, and verifies `ClientProgram.Main`. The launcher holds exclusive locks for both the data path and game directory during this work and during the game process.
- The launcher drains patcher output through concurrent readers, stops a hung patcher after five minutes, restores vanilla built-in mods through temporary files, and reports rollback failures.
- Cache read, hash, and manifest-write failures abort through the same restore path. Assembly loading rejects an entry that resolves from a path outside the patched cache.
- The Windows installer checks package completion, checks the package process result, runs the full runtime preflight, and swaps the staged tree through a temporary directory with rollback.
- The installers accept stable four-part `ilspycmd` versions from `10.1.0.8386` through `10.1.1.8388`, inclusive, while the repository keeps `10.1.1.8388` as the preferred pin. Versions outside the range and prereleases stop before decompilation.
- Bash and PowerShell bootstrap scripts normalize patch, source, generated, and runtime-donor files to LF. Git clone commands set the repository EOL policy before each checkout.
- The Windows package ships a standalone uninstaller that removes its own folder after PowerShell exits, records cleanup results, reports failed paths, and leaves the user's vanilla Vintage Story installation intact.

## Reports reviewed

The review covered the Vigilance and ImpureSoul3 render stacks, the ChestyLaRue Windows checkout report, the KandelKitty uninstall report, `server-main.log`, `server-main (1).log`, and `message.txt`. The server logs also contain third-party Harmony and JSON patch failures. Those stacks name other mods and receive no Optimum code change in this release.

## Validation

The official .NET 10 SDK and ilspycmd 10.1.1.8388 produced the donor tree. Patch validation reported 52 source patches, 43 Cecil patches, zero pending patches, and zero conflicts. Exact runtime donor validation applied 22 patches and compiled both donor projects. The solution built with zero warnings and errors. The test suite passed 454 tests with 34 skips. Launcher tests passed 6 tests. Real runtime validation patched the engine with 54 of 54 required methods and one hook, the Essentials donor with 24 of 24 required methods, and the Survival donor with 11 required methods plus one documented optional target. A staged `--validate-only` run JIT-validated 15,192 engine methods, 8,535 API methods, 4,146 Essentials methods, and 13,720 Survival methods before it loaded and reflected all four patched assemblies. A second staged run with the Survival donor removed returned exit code 1 and did not start the game.
