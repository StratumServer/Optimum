# Handling a new Vintage Story version

Whenever Anego ships a new Vintage Story release, Optimum has two ways to
target it. **Always prefer option 1.** Only fall back to option 2 as a
temporary stopgap, and drop it the moment option 1 becomes possible.

## Option 1 (preferred): bump the pinned refs to the real upstream source

`forks.json`'s `compile` entries pin `VintagestoryApi`, `Cairo`,
`VSEssentials`, `VSSurvivalMod`, and `VSCreativeMod` to exact commits on
Anego's GitHub repos. When Anego pushes source matching the new client
version:

1. Update each `ref` in `forks.json` to the new commit, and bump
   `vintageStoryVersion`.
2. Run `make bootstrap` (now pointed at the new version by default) and
   re-apply/rebase `patches/` against the new source. Expect some patches to
   need re-basing wherever upstream touched the same lines.
3. Run `bash scripts/check-vanilla-compat.sh` and
   `bash scripts/check-patches.sh` - both are version-aware (they read the
   actual extracted client's `assets/version-X.Y.Z.txt` marker, not a
   hardcoded string) and will validate against whichever version
   `forks.json` now points at.
4. `dotnet build VintageStory.slnx -c Release` and smoke-test.
5. Delete `patches-1.22.6-bridge/` (or whatever version-numbered bridge
   directory existed for the gap) - it's now redundant.

This is preferable because the resulting source is Anego's actual code, not
a reconstruction. No engineering-judgment risk, no maintenance burden beyond
normal patch rebasing.

**Check whether this is possible with:**
```bash
git ls-remote https://github.com/anegostudios/vsapi.git HEAD
git ls-remote https://github.com/anegostudios/Cairo.git HEAD
git ls-remote https://github.com/anegostudios/vsessentialsmod.git HEAD
git ls-remote https://github.com/anegostudios/vssurvivalmod.git HEAD
git ls-remote https://github.com/anegostudios/vscreativemod.git HEAD
```
If any of these differ from the ref currently pinned in `forks.json`,
Anego has pushed something - go compare it against the compiled client to
confirm it's the matching release, then use Option 1.

## Option 2 (stopgap): reconstruct the gap from the compiled client

Used when the compiled client for the new version is public but Anego
hasn't pushed matching source yet (this was the situation for 1.22.6 as of
2026-08-05 - see `patches-1.22.6-bridge/README.md` for that specific case in
full, including two real `bootstrap.sh` bugs found while verifying it).

**Only the five `compile` repos above can need this.** `VintagestoryLib.dll`
and `Vintagestory.dll` never do: `bootstrap.sh` decompiles them itself from
whichever client archive `--version` downloads
(`vs_client_${os_arch}_${version}.tar.gz` off Anego's CDN), so a
`--version <new>` build already has real, current engine source the moment
decompilation finishes - there is no engine-side gap to reconstruct, ever.
Confirmed empirically for 1.22.6: no bridge patch was needed or written for
`VintagestoryLib`/`Vintagestory`, only for the three of five `compile` repos
that actually changed.

Procedure:

1. Get the new version's compiled client (`vs_client_linux-x64_<version>.tar.gz`
   from Anego's CDN) and decompile it with the *exact* command and ILSpy
   version `bootstrap.sh` itself uses -
   `ilspycmd <dll> --project -o <dir>` (no `--use-varnames-from-pdb`, no
   other flags). Do the same for the currently-pinned version's compiled
   client. Diffing decompiled output against hand-written or
   differently-flagged decompiled output produces false mismatches (this
   bit twice while building the 1.22.6 bridge - see
   `patches-1.22.6-bridge/README.md`'s "Bugs found and fixed").
2. For each of the five `compile` repos, diff the two decompiled trees for
   the files belonging to that repo (match by namespace/class name, not by
   the decompiler's file layout, which differs from the published source
   tree's layout). Confirm each finding against the actual decompiled text,
   not just a written summary of it.
3. **Don't skip the version-string constant.** `VintagestoryApi`'s
   `Config/GameVersion.cs` has one `const string OverallVersion` that every
   other version constant, and every mod repo's own
   `AssemblyFileVersion(GameVersion.OverallVersion)`, derives from
   symbolically. It's tempting to treat every `"1.22.5"` → `"1.22.6"` literal
   swap as noise when hunting for *behavioral* differences (correct for that
   purpose - see `DIFF-1.22.5-1.22.6.md`'s "31 pure literal-string swaps"
   note) but this one specific constant needs its own one-line bridge patch
   regardless, or the mod-API layer keeps reporting the old version even
   though the engine correctly reports the new one (this was missed on the
   first pass for 1.22.6 and only caught when asked directly why the built
   output still showed 1.22.5 - see `patches-1.22.6-bridge/README.md`'s "Why
   the mod-API layer needs its own version bump"). Verify the fix reached
   compiled output with `strings bin/Release/net10.0/<ModAssembly>.dll |
   grep 1.22`, not just by re-reading the patch.
5. Hand-apply confirmed changes on top of a checkout of the repo at its
   currently-pinned ref, and generate a patch (`git diff --no-index`,
   `a/<RepoDir>/<path>` / `b/<RepoDir>/<path>` prefixes matching
   `patches/`'s own convention) for each changed file.
6. Put those patches in a version-named directory at repo root (e.g.
   `patches-1.22.6-bridge/`), **not** inside `patches/` -
   `check-vanilla-compat.sh` scans `patches/` for wire-protocol-sensitive
   changes on the assumption that anything there is an Optimum-introduced
   divergence from vanilla; a bridge patch reproducing what Anego already
   shipped isn't that, and putting it in `patches/` produces a misleading
   allowlist entry.
7. Wire it into `bootstrap.sh`: apply the bridge directory's patches right
   before the main `patches/` loop (step 6a in the current script), gated on
   `"$version" != "$pinned_version"`, using the same `--directory=build`
   logic the main loop uses for `VintagestoryLib`/`Vintagestory` targets
   (only relevant if a future bridge ever needs it - the 1.22.6 one didn't,
   per the point above).
8. **Run the real build before trusting any of it**:
   `make bootstrap VERSION=<new> && dotnet build VintageStory.slnx -c
   Release`. Patches that apply cleanly in isolated testing can still
   collide with an existing Optimum patch once real content is involved
   (this happened for `ServerSystemLoadAndSaveGame.cs.patch` in 1.22.6 - a
   real collision, not a false one, found only by running the actual
   build). Resolve any such collision by reducing the *existing* Optimum
   patch's context (fewer lines around the insertion point) so it stops
   depending on exact adjacent content, generated fresh from a true
   pristine decompile rather than hand-edited - never by hand-patching the
   build output, which gets wiped and regenerated on every bootstrap run.
9. Run `check-vanilla-compat.sh` and `check-patches.sh` against the real
   build, not just the isolated patches.
10. Document what was and wasn't covered, and what's still unverified
    (smoke-tested? multiplayer-tested?) - don't claim more confidence than
    the verification actually performed. See `patches-1.22.6-bridge/README.md`
    for the level of detail expected here.
11. Set a reminder (the `_<version>_check` field in `forks.json` works) to
    re-check `git ls-remote` periodically and switch to Option 1 the moment
    it's possible.

## Choosing a version at build/install time

Both versions can coexist. `forks.json`'s `vintageStoryVersion` is the
default; override per-invocation:

```bash
make bootstrap VERSION=1.22.5     # current default, official source throughout
make bootstrap VERSION=1.22.6     # bridge-patched, see patches-1.22.6-bridge/
dotnet build VintageStory.slnx -c Release
```

`bootstrap.sh --version <X>` re-extracts the client and re-decompiles
`VintagestoryLib`/`Vintagestory` whenever the requested version differs from
what's already extracted in `.vanilla/` (checked via the client's own
`assets/version-X.Y.Z.txt` marker, not just directory existence - a bug
where switching versions silently kept building against the previous
version's engine DLLs was found and fixed during 1.22.6 verification).
