# Local Runtime Donors Design

## Goal

Optimum must create every assembly that contains Vintage Story code on the user's machine. Public source releases may contain Optimum code, patch manifests, build scripts, and upstream locations. They must not contain Vintage Story source, vanilla assemblies, symbols, or compiled donor assemblies.

## Scope

The runtime launcher patches `VintagestoryLib.dll`, `VintagestoryAPI.dll`, `Mods/VSEssentials.dll`, and `Mods/VSSurvivalMod.dll` into the user's Optimum cache. The launcher leaves vanilla inputs untouched. `VSCreativeMod.dll` stays vanilla because the current runtime manifest has no changes for it.

The Survival donor resolves its WorldEdit types from the user's vanilla `Mods/VSCreativeMod.dll`. It never builds against the public Creative source tree, whose 1.22.5 type surface differs from the runtime assembly.

`VintagestoryLib` remains a decompile-derived donor. The 1.22.5 source release contains API, Essentials, Survival, and Creative source, but it does not contain the closed client library. Essentials and Survival may use source-derived donors only after the compatibility gate accepts them.

The public repository must contain every Optimum-owned project and script that the shipping workflow references. This surface includes `Optimum.Launcher`, `Optimum.Patcher`, launcher tests, both runtime-donor scripts, solution entries, installer integration, and package integration. The public repository must never reference an absent private project.

The private repository remains the development source of truth. The public repository receives the complete Optimum-owned shipping surface, but it receives no decompiled game source, source-release content, vanilla assembly, symbol file, or compiled donor.

## Local layout

The installer stores external inputs and outputs below the selected data directory:

```
.optimum/
  source/1.22.5/<source-hash>/
  donors/
    VintagestoryLib.Donor.dll
    VintagestoryAPI.Contracts.dll
    VSEssentials.Donor.dll
    VSSurvivalMod.Donor.dll
    manifest.json
  cache/
    VintagestoryLib.dll
    VintagestoryAPI.dll
    Mods/VSEssentials.dll
    Mods/VSSurvivalMod.dll
    manifest.json
```

The installer accepts a local source archive or directory. A future installer may download an official archive after it records a pinned URL and checksum. The public repository never stores that archive.

## Build and compatibility flow

The donor build copies the supplied source tree into an ignored work directory, applies Optimum patches, and compiles the donor. The gate checks every runtime transplant target against the exact user-owned vanilla module. A mismatch rejects the source donor and selects the existing exact-decompile donor path. The gate records the chosen source and input hashes in the donor manifest.

The source release currently differs from the installed Essentials assembly surface in at least `EntityBehaviorRepulseAgents`. The migration must treat this case as a rejection, not as a patch conflict to force through.

Before bootstrap or build, the shipping workflow reads every project path from `VintageStory.slnx` and fails with a direct error when a project file does not exist. Installer builds select shipping projects instead of editing the solution with a project-name substring. This rule keeps every test project out of release builds, including `Optimum.Launcher.Tests`.

The Windows installer creates pristine local copies of `VintagestoryAPI.dll`, `VSEssentials.dll`, `VSEssentials.pdb`, `VSSurvivalMod.dll`, and `VSSurvivalMod.pdb` in its isolated workspace. The donor builder consumes those copies and the vanilla `VSCreativeMod.dll`. It never writes to the selected Vintage Story installation.

The shipping build follows this order:

1. Copy the public source tree and the user's vanilla installation into isolated work directories.
2. Bootstrap the exact supported Vintage Story version and validate all solution project paths.
3. Build Optimum contracts, game content, launcher, patcher, and the `VintagestoryLib` donor.
4. Decompile the exact local Essentials and Survival assemblies, apply runtime patches, and build their donors.
5. Assemble the four named donor inputs and write a hash manifest.
6. Stage the vanilla game copy, the Optimum launcher, the patcher, their managed dependencies, scripts, and donor inputs.
7. Validate the stage manifest before packaging or installation.

The package script must use the runtime-launcher architecture. It must not replace vanilla game assemblies with fork build outputs. The stage keeps vanilla `VintagestoryLib.dll`, `VintagestoryAPI.dll`, `VSEssentials.dll`, `VSSurvivalMod.dll`, and `VSCreativeMod.dll` intact. The launcher creates patched cache outputs under `.optimum/cache` on first run.

## Runtime flow

The launcher hashes every vanilla input and donor input. It invalidates the cache when any hash or the Optimum version changes. It invokes the patcher with the matching mode: generic Cecil transplant for `VintagestoryLib`, API hooks for `VintagestoryAPI`, and module manifests for Essentials and Survival. The assembly resolver checks cached `Mods/` outputs before it resolves vanilla mod assemblies.

If the donor builder or runtime patcher fails, the launcher starts vanilla and preserves the original game files.

The installer selects the donor root from the effective data path. When the user supplies `DataPath`, it writes donors below `<DataPath>/.optimum/donors`. Otherwise it writes donors below `<InstallDir>/.optimum/donors`, which matches the launcher's fallback data-path resolution.

The stage contains these required Optimum artifacts:

```
Optimum.exe
Optimum.dll
Optimum.Patcher.dll
Optimum.Patcher.deps.json
.optimum/donors/VintagestoryLib.Donor.dll
.optimum/donors/VintagestoryAPI.Contracts.dll
.optimum/donors/VSEssentials.Donor.dll
.optimum/donors/VSSurvivalMod.Donor.dll
.optimum/donors/manifest.json
```

The package may include other runtime dependencies that the .NET output requires. A stage validator derives that dependency set from the launcher and patcher output directories instead of maintaining a partial handwritten list.

When a compatibility gate rejects an optional mod donor, the installer reports the rejected module and omits only that donor. The launcher falls back to the vanilla module. A missing launcher, patcher, `VintagestoryLib` donor, or API contracts donor stops packaging because those files define the shipping architecture.

## Script parity

PowerShell and Bash implement the same donor rules. They use the exact vanilla Creative assembly for Survival references, apply the same compile exclusions, perform patch checks before patch application, and expose the same output names. Tests compare their required inputs, exclusions, patch roots, and donor names to prevent drift.

The donor scripts publish completed artifacts into a caller-selected output directory. Their default output remains an ignored build directory for developer use. Installers and packagers pass the final donor directory explicitly.

## Public release gate

The public release gate checks these properties before commit or packaging:

- Every `VintageStory.slnx` project path exists in Git.
- The repository tracks the launcher, patcher, donor scripts, tests, and shipping documentation.
- Git tracks no vanilla DLL, PDB, decompiled source tree, generated donor, build output, or local data path.
- The installer build excludes every test project without mutating the checked-in solution.
- The stage contains every required Optimum artifact and retains the vanilla game modules.
- PowerShell and Bash donor preparation follow the same compatibility rules.
- Bootstrap applies every patch without failure against the supported release.

## Verification

Tests cover target selection, cache invalidation from donor changes, nested mod cache paths, module patcher command routing, and resolver lookup for cached mods. An integration run builds local donors from a user-owned 1.22.5 installation, patches all supported targets, and starts the launcher with the cache. The build records source incompatibility as an expected fallback when the gate rejects a release source tree.

Static tests parse the solution and assert that Git contains every referenced project. Packaging tests assemble a temporary stage and assert the required artifact set, donor names, vanilla module retention, and test-project exclusion. Script-parity tests compare the PowerShell and Bash donor contract. A Windows smoke test runs the isolated bootstrap, builds the shipping projects, prepares donors from a user-owned installation, and launches through `Optimum.exe`.
