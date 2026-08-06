# 1.22.6 upstream bridge patches

## Verification status (2026-08-05): real build succeeded

`make bootstrap VERSION=1.22.6 && dotnet build VintageStory.slnx -c Release`
ran against the real 1.22.6 client (downloaded from
`https://cdn.vintagestory.at/gamefiles/stable/`) and **succeeded**: 95/95
`patches/` applied, 0 build errors, 0 warnings. `check-vanilla-compat.sh` and
`check-patches.sh` also ran; see "Known unrelated findings" below for the
two pre-existing gaps they surfaced (neither caused by this work).

Getting there took two real bugs fixed along the way - see "Bugs found and
fixed" below. Read that section before touching this directory again; it
explains why the scope here is smaller than an earlier draft of this README
assumed.

## What this is

`forks.json` pins `VintagestoryApi`, `VSEssentials`, `VSSurvivalMod`,
`VSCreativeMod`, and `Cairo` at commits from Anego's 1.22.5 release, and
`bootstrap.sh` clones them at those exact refs **regardless of `--version`**.
As of this writing, Anego has not pushed 1.22.6 source to those repos (`git
ls-remote` still matches the pinned refs; see `forks.json`'s
`_1.22.6_check`), even though the compiled 1.22.6 client is public.

This directory reconstructs, on top of that pinned 1.22.5 source, the
specific 1.22.6 behavior changes that a full line-by-line comparison against
the official 1.22.6 client's decompiled output confirmed exist, for the
five repos above only. See "What's NOT here and why" below for the bigger
piece this does not cover.

These patches are **not Optimum features**. Every line was checked against a
full ILSpy `10.1.1.8388` decompile of the real 1.22.6 client before being
written.

## What's NOT here and why: `VintagestoryLib`/`Vintagestory` need no bridge

An earlier version of this directory also contained nine patches for
`VintagestoryLib` (`ServerPlayer`, `ServerConfig`, `ServerMain`,
`ServerSystemLoadAndSaveGame`, `GameDatabase`, `Logger`,
`PlayerAntiAbuseMonitor`, `PlayerBlockBreaks`, `PlayerPacketMonitor`). They
were wrong to write and have been deleted.

Unlike the five GitHub-cloned repos, `VintagestoryLib.dll` and
`Vintagestory.dll` are **decompiled by `bootstrap.sh` itself** from
whichever client archive `--version` requests
(`vs_client_${os_arch}_${version}.tar.gz`, downloaded fresh from Anego's
CDN). Running `bootstrap.sh --version 1.22.6` decompiles the *real* 1.22.6
`VintagestoryLib.dll`; the confirmed engine-side changes
(`ServerPlayer`'s dimension check, `ServerConfig`'s `AntiAbuse*` fields,
`PlayerAntiAbuseMonitor`, the `--restoreLogsFolder` feature, etc.) are
already there the moment decompilation finishes. There is no 1.22.5-shaped
gap to bridge on the engine side at all - only on the five repos that clone
from a pinned ref independent of `--version`.

This was confirmed empirically, not just reasoned about: after fixing the
stale-`.vanilla` bug below, `grep`-ing the freshly decompiled
`build/VintagestoryLib/Vintagestory.Server/ServerPlayer.cs` for
`player.Entity.Pos.Dimension != blockPos.dimension` found it present with no
bridge patch applied.

## Bugs found and fixed while verifying this

Two real, pre-existing bugs in `bootstrap.sh` blocked verification and had
to be fixed (both are in `scripts/bootstrap.sh`, not in this directory):

**1. Stale `.vanilla/` reuse across versions.** The extraction step only
checked `[[ ! -d "$vanilla_dir/vintagestory" ]]` before extracting the
downloaded archive. A repo previously bootstrapped for 1.22.5 already has
that directory, so requesting `--version 1.22.6` downloaded the correct
archive but then silently kept building against the *old* 1.22.5
`VintagestoryLib.dll` - `PlayerPacketMonitor.cs` (removed in 1.22.6) was
still present, `PlayerAntiAbuseMonitor.cs` was missing, and this went
undetected without inspecting decompiled output by hand. Fixed by comparing
the extracted client's `assets/version-X.Y.Z.txt` marker against the
requested `$version` and re-extracting (plus wiping the decompile snapshot)
on mismatch.

**2. Decompile flags mismatch invalidated the first two batches of engine
bridge patches** (now deleted, see above, but the flag bug itself is a
real, general finding worth recording). The reference decompile used to
derive these patches was generated with `--use-varnames-from-pdb`.
`bootstrap.sh`'s own decompile call (`ilspycmd "$dll_path" --project -o
"$out"`) does **not** pass that flag, so its output uses ILSpy's generic
local-variable names (`list`, `num`, ...) instead of PDB-recovered ones
(`logsToMove`, ...). Patches built against the `--use-varnames-from-pdb`
reference tree failed to apply against `bootstrap.sh`'s actual output purely
because of variable-name context mismatches, with no logical conflict at
all. Anyone deriving a future patch this way must re-decompile with
`ilspycmd <dll> --project -o <dir>` (no extra flags) to match what
`bootstrap.sh` actually produces, or the comparison will report false
mismatches.

## Patches in this set

| File | 1.22.6 change | Evidence |
| --- | --- | --- |
| `VintagestoryApi/Math/BlockPos.cs.patch` | Adds `BlockPos.AddCopy(Vec3f)` overload | DIFF report: "API: `BlockPos.AddCopy(Vec3f)`" |
| `VSSurvivalMod/Systems/Auction/Auction.cs.patch` | Hardcoded 30-auction cap becomes configurable `MaxAuctionsPerPlayer` (default 100, `maxAuctionsPerPlayer` world config key) | DIFF report: "Survival mod: configurable auction limit" |
| `VSSurvivalMod/BlockEntity/BESpawner.cs.patch` | Packet 1001 (spawner config) now requires the sender to be in creative mode; audit-logs and rejects otherwise | DIFF report: "Survival mod: `BlockEntitySpawner`, creative-mode gate on network packet" |
| `VSSurvivalMod/Systems/SupportBeams/ModSystemSupportBeamPlacer.cs.patch` | Removes `BeamPlacePacket.BlockId` (server now resolves the block from the sender's held item, not a client-supplied ID); moves the claim-access check to run unconditionally and checks both beam endpoints instead of only the anchor block; adds an item-consistency check; cleans up `workspaceByPlayer` on player disconnect; fixes `GetStableMostBeam` never assigning `nearestBeam` | DIFF report: "Survival mod: `ModSystemSupportBeamPlacer`" (multiple subsections) |
| `VSEssentials/Systems/RoomRegistry.cs.patch` | `Dispose()` guards `disposableBlockAccessors` against being null before iterating/clearing it | DIFF report: "Essentials: `RoomRegistry.Dispose`, null-safety fix" |
| `VintagestoryApi/Config/GameVersion.cs.patch` | Bumps `OverallVersion` from `"1.22.5"` to `"1.22.6"` | Not in the DIFF report - deliberately excluded there as a pure literal-string swap, but needed here since this constant is the single source of truth for `ShortGameVersion`, `AssemblyVersion`, `LongGameVersion`, and (via symbolic reference, not a literal - confirmed in source) every `AssemblyFileVersion(GameVersion.OverallVersion)` in `VSSurvivalMod`/`VSEssentials`/`VSCreativeMod`'s own `AssemblyInfo.cs`, plus `BlockSchematic.GameVersion`. One-line change, cascades everywhere. See "Why the mod-API layer needs its own version bump" below. |

Not covered here because the sweep found no source-level difference:
`VSCreativeMod` and `Cairo` are confirmed identical between 1.22.5 and 1.22.6
at the decompiled level; no bridge patch needed for either.

## Why the mod-API layer needs its own version bump

The engine (`VintagestoryLib`/`Vintagestory`) self-reports as 1.22.6 for
free once decompiled from the real 1.22.6 client - every hardcoded
`"1.22.5"` → `"1.22.6"` literal swap the DIFF report found there (`Logger`,
`ClientPackets`, `ServerMain`, etc.) comes along automatically. The
mod-API layer does not get this for free: `VintagestoryApi`'s
`Config/GameVersion.cs` defines `OverallVersion = OverallMajorMinor + ".5"`
as a compile-time `const string`, and since it's pinned at the 1.22.5 ref,
that constant stays `"1.22.5"` unless bridged - even though the paired
engine is genuinely 1.22.6.

This matters more than a cosmetic label: `VSSurvivalMod`, `VSEssentials`,
and `VSCreativeMod`'s own `AssemblyInfo.cs` set
`[assembly: AssemblyFileVersion(GameVersion.OverallVersion)]` - a symbolic
reference, not a literal (confirmed by reading the actual source, not just
the decompiled/inlined view where `const` folding makes it look like a
literal). So every mod DLL's own file version, `GameVersion.ShortGameVersion`,
`GameVersion.IsCompatibleApiVersion` checks other mods might run against
Optimum, and `BlockSchematic.GameVersion` (schematic export self-labeling)
all silently stayed "1.22.5" in a 1.22.6 build until this patch was added.

Fix: `VintagestoryApi/Config/GameVersion.cs.patch` changes the one `const`
declaration. Verified the cascade reaches the compiled output: after
rebuilding, `VSSurvivalMod.dll` references
`VintagestoryAPI, Version=1.22.6.0` and contains `"1.22.6"` strings (checked
with `strings bin/Release/net10.0/VSSurvivalMod.dll`).

## `ServerSystemLoadAndSaveGame.cs`: real collision, fixed at the source

This was flagged in an earlier draft as a risk between Optimum's own
`patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch`
(parallel chunk read pool: inserts `TryStartOptimumChunkReadPool();` right
after `chunkthread.gameDatabase.UpgradeToWriteAccess();`) and 1.22.6's own
`--restoreLogsFolder` trigger (inserted right after the following
`OnWorldgenStartup +=` line) - both land in the same few-line window of the
same method. The real build confirmed it: `git apply` failed on this file
against the genuine 1.22.6-decompiled source.

Fixed by rewriting `patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch`
itself (not something in this directory - it's an Optimum patch, used for
every version) to use 1-line context instead of the default 3, generated
fresh from a true pristine decompile (no `--use-varnames-from-pdb`, matching
bug #2 above) rather than hand-edited. Verified to apply cleanly against
both a pristine 1.22.5 decompile and the real 1.22.6
`build/snapshot/VintagestoryLib` pre-fixup snapshot, then confirmed via the
actual `make bootstrap VERSION=1.22.6` run (95/95 patches applied) and a
full `dotnet build` (0 errors).

## Known unrelated findings from the verification run

`check-vanilla-compat.sh` and `check-patches.sh` surfaced two pre-existing
issues unrelated to 1.22.6 or to any change in this session:

- `FAIL patch touches multiplayer compatibility target: patches/VintagestoryLib/Vintagestory.Server/ServerSystemSendChunks.cs.patch`
  not on the allowlist. Neither this file nor `BehaviorRepulseAgents.cs`
  (next finding) were touched by any 1.22.6 work; this patch predates it.
- `FAIL repulsion patch keeps client gate`: a `check_contains` assertion
  against `patches/VSEssentials/Entity/Behavior/BehaviorRepulseAgents.cs.patch`
  failed independent of version.
- `cecil-owned.list` and `Optimum.Patcher/Program.cs` disagree on several
  entries (`check-patches.sh` warning) - a pre-existing drift between the
  two files, not something this session's changes caused.

None of these block a 1.22.6 build (the real build succeeded despite them);
they're pre-existing gaps worth a separate look, not part of this bridge.

## Retirement

Once Anego publishes real 1.22.6 source for `vsapi`/`vsessentialsmod`/
`vssurvivalmod`, delete this directory and update `forks.json`'s pinned refs
to the real commits instead. Re-run the `_1.22.6_check` procedure in
`forks.json` periodically to detect when that happens.
