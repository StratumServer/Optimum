# Optimum Runtime Patcher - Implementation Spec

## Summary

Replace the current "patch DLLs on disk at install time" approach with a
**runtime patcher** that applies Cecil patches when the game launches, caches
the result, and shows a loading screen during the one-time patch operation.

## Goals

1. Vanilla DLLs stay byte-for-byte intact on disk - always.
2. Game updates never break the install (cache auto-invalidates).
3. All patches apply unconditionally (no more "which methods can we transplant"
   limitation - field type changes, full method rewrites, everything).
4. User sees a branded loading screen during the one-time patch (cached thereafter).
5. Zero overhead on cached launches (< 10ms to validate + load).

## Architecture

```
                  ┌─────────────────────────────────────────────────┐
                  │                 Optimum.exe                      │
                  │  (replaces Vintagestory.exe as the entry point)  │
                  └──────────────┬──────────────────────────────────┘
                                 │
                  ┌──────────────▼──────────────────────────────────┐
                  │            CacheManager                          │
                  │  1. Compute manifest hash                        │
                  │  2. Cache hit? → Load cached DLLs                │
                  │  3. Cache miss? → Show splash → Patch → Save     │
                  └──────────────┬──────────────────────────────────┘
                                 │
                  ┌──────────────▼──────────────────────────────────┐
                  │          PatchEngine (existing ILPatcher)        │
                  │  - TypeInjection                                 │
                  │  - MemberInjection                               │
                  │  - MethodTransplant                              │
                  │  - ILHooks                                       │
                  │  Reports progress via callback                   │
                  └──────────────┬──────────────────────────────────┘
                                 │
                  ┌──────────────▼──────────────────────────────────┐
                  │         AssemblyLoader                           │
                  │  Load patched assemblies into Default ALC        │
                  │  Redirect vanilla DLL loads to cached versions   │
                  └──────────────┬──────────────────────────────────┘
                                 │
                  ┌──────────────▼──────────────────────────────────┐
                  │    Vintagestory ClientProgram.Main()             │
                  │    (runs with patched assemblies in memory)      │
                  └─────────────────────────────────────────────────┘
```

## Entry Point: Optimum.exe

Optimum.exe becomes the game launcher. It:

1. Parses the same CLI args as Vintagestory.exe (passthrough).
2. Resolves the game install directory (same dir, or `--gamePath`).
3. Initializes the CacheManager.
4. Either loads from cache or patches + caches.
5. Sets up assembly resolution to intercept vanilla DLL loads.
6. Calls `ClientProgram.Main(args)` via reflection.

The existing `Vintagestory.exe` stays on disk untouched. The user (or
shortcut/launcher) invokes `Optimum.exe` instead.

## Cache System

### Cache Location

```
{GamePaths.DataPath}/.optimum/cache/
  manifest.json          ← current hash + metadata
  VintagestoryLib.dll    ← patched DLL
  VintagestoryLib.pdb    ← patched PDB (portable, from Cecil)
  VSEssentialsMod.dll    ← patched DLL (if applicable)
  VSSurvivalMod.dll      ← patched DLL (if applicable)
  VintagestoryAPI.dll    ← patched DLL (if applicable)
```

### Manifest Schema

```json
{
  "optimumVersion": "0.3.4",
  "patcherHash": "sha256:abc123...",
  "targets": [
    {
      "assembly": "VintagestoryLib.dll",
      "vanillaHash": "sha256:def456...",
      "patchedHash": "sha256:789abc...",
      "patchCount": 24
    }
  ],
  "createdAt": "2026-07-23T21:50:00Z",
  "gameVersion": "1.22.3"
}
```

### Invalidation Rules

Cache is invalid (triggers re-patch) when ANY of:
- `manifest.json` missing or unreadable.
- `optimumVersion` differs from running Optimum.exe version.
- `patcherHash` differs from SHA256 of `Optimum.Patcher.dll` or embedded
  patch data.
- Any target's `vanillaHash` differs from the actual DLL on disk.
- Any cached DLL file is missing or corrupted (hash mismatch).

### Fast Validation (< 5ms typical)

To avoid reading the full 5MB DLLs on every startup:
1. First check: `manifest.json` exists AND `optimumVersion` matches.
2. Second check: stat() the vanilla DLLs - if mtime + size match what was
   recorded, skip the hash. Only recompute SHA256 if mtime/size changed.
3. This makes cache validation ~2ms on hot filesystem.

## Splash / Loading Screen

### Mechanism

The loading screen uses the **same infrastructure as VS's "Loading shaders"
screen**: a `GuiScreen` subclass rendered by the `ScreenManager` on the main
OpenGL thread before the game finishes initializing.

However, the patcher runs BEFORE `ScreenManager` exists (it runs before the
VS assemblies are loaded). So we use a **two-phase approach**:

### Phase 1: Pre-patch Console Output (during patch)

Before any VS code is loaded, Optimum.exe outputs progress to the console
(visible in terminal launches) and sets the window title:

```
[Optimum] Applying optimizations... (1/24) SystemRenderEntities
[Optimum] Applying optimizations... (2/24) ChunkRenderer
...
[Optimum] Done. Cached to .optimum/cache/
```

### Phase 2: VS-native Loading Screen (during assembly load)

After patching completes and we begin loading the patched assemblies, we
hook into the VS startup sequence. The `ScreenManager.loadingText` field
is set to show Optimum status during the normal loading flow:

```
"Optimum - Loading optimized assemblies..."
```

This appears in the existing `GuiScreenLoadingGame` between "Loading assets"
and "Loading shaders" - seamlessly integrated.

### Alternatively: GLFW Splash Window (preferred for cache misses)

For the ~300ms patch operation on cache miss, we can open a **minimal GLFW
window** before any VS code runs (OpenTK is a direct dependency available
on disk):

```csharp
// Minimal splash - no VS dependencies required
GLFW.Init();
var window = GLFW.CreateWindow(480, 120, "Optimum", null, null);
GLFW.MakeContextCurrent(window);
// Clear to dark gray, render "Applying optimizations..." via bitmap
// Poll events to prevent "not responding"
// Close after patching completes
GLFW.DestroyWindow(window);
```

This gives visual feedback even if the game takes a moment to load after
patching. On cache hit, this window never opens.

### Decision: Which Splash to Use

| Scenario | Splash |
|----------|--------|
| Cache hit (normal launch) | None - goes straight to VS splash |
| Cache miss, terminal | Console progress output |
| Cache miss, GUI | GLFW mini-window OR Windows splash screen API |

**Recommendation:** Use the GLFW mini-window for cache misses. It's
available on all 3 platforms (VS ships OpenTK), lightweight, and familiar
to players (same look as a loading splash). Title: `"Optimum"`, body:
progress text rendered via a pre-baked bitmap font or SkiaSharp (also
shipped with VS).

## Assembly Loading Strategy

### The Problem

The .NET runtime loads assemblies eagerly when methods are JIT-compiled.
If `Optimum.exe` references `VintagestoryLib` types directly, the runtime
will load the vanilla DLL before we can substitute it.

### The Solution: AssemblyLoadContext + Reflection

1. **Optimum.exe does NOT reference VintagestoryLib/VintagestoryAPI at
   compile time.** It references only:
   - `System.*`
   - `Mono.Cecil` (for patching)
   - `OpenTK.Windowing.GraphicsLibraryFramework` (for splash, optional)

2. After patching/cache-loading, we register an `AssemblyResolve` handler:

```csharp
AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
{
    var name = new AssemblyName(args.Name).Name;
    var cached = Path.Combine(cacheDir, name + ".dll");
    if (File.Exists(cached))
        return Assembly.LoadFrom(cached);

    // Fall through to vanilla for non-patched assemblies
    var vanilla = Path.Combine(gameDir, name + ".dll");
    if (File.Exists(vanilla))
        return Assembly.LoadFrom(vanilla);

    return null;
};
```

3. Then invoke the game entry point via reflection:

```csharp
var asm = Assembly.LoadFrom(Path.Combine(cacheDir, "VintagestoryLib.dll"));
var clientProgram = asm.GetType("Vintagestory.Client.ClientProgram");
var main = clientProgram.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
main.Invoke(null, new object[] { args });
```

### PDB Support

Cecil generates portable PDB alongside the patched DLL. This means:
- Stack traces in crash reports show Optimum source lines.
- The debugger can step into patched methods.
- VS's own crash reporter (`CrashReporter`) picks up the symbols.

## Patch Engine Changes

The existing `ILPatcher.PatchWithInjection()` currently:
- Reads vanilla DLL from disk
- Reads compiled DLL from disk
- Writes patched DLL to disk

Changes needed:
1. Add an overload that returns `byte[]` (in-memory patching):
   ```csharp
   public static (byte[] assembly, byte[] pdb) PatchInMemory(
       string vanillaPath,
       string compiledPath,
       IProgress<PatchProgress> progress = null)
   ```
2. The `PatchProgress` callback enables the splash:
   ```csharp
   public record PatchProgress(int Current, int Total, string TypeName);
   ```
3. The compiled DLL (donor) can be **embedded as a resource** in Optimum.exe,
   eliminating the need for a separate `build/` directory at runtime.

## What This Unlocks

With runtime patching, **ALL patches apply unconditionally**:

| Current limitation | With runtime patcher |
|---|---|
| Lock upgrades need ~25 method transplants each | Just patch - entire DLL is rewritten |
| Field type changes can't ship | Just patch - Cecil rewrites the field |
| Every new method needs a Program.cs entry | No entries needed - entire donor DLL transplanted |
| Vanilla DLLs get modified on disk | Vanilla stays pristine |
| Game update breaks install | Cache auto-invalidates, re-patches on next launch |
| Install requires bootstrap + build | Install = drop Optimum.exe + config, done |

## Migration Plan

### Phase 1: Runtime Patcher MVP (v0.3.0) ✅ IMPLEMENTED

1. ✅ Created `Optimum.Launcher` project (the new Optimum.exe).
2. Optimum.Patcher remains a standalone CLI tool (invoked as subprocess).
3. ✅ Implemented CacheManager with hash-based invalidation.
4. ✅ Implemented AssemblyResolve loader.
5. ✅ Console-only progress (no splash yet).
6. Partial: SvgLoader wired. Lock upgrades deferred (require ~25 transplants each).
7. Platform testing pending.

### Phase 2: Splash + Polish (v0.3.1)

1. Add GLFW mini-window for cache miss feedback.
2. Embed donor DLL as resource (single-file distribution).
3. Add `--force-repatch` flag for debugging.
4. Add cache stats to the Optimum settings panel in-game.
5. Wire remaining Lock upgrades (ClientWorldMap, SystemClientTickingBlocks).

### Phase 3: Installer Simplification (v0.3.2) ✅ IMPLEMENTED

1. ✅ Installer just drops Optimum files into the VS game dir.
2. ✅ No bootstrap, no decompilation, no build step for the end user.
3. The donor DLL ships alongside as `VintagestoryLib.Donor.dll`.

## File Layout (Post-Migration)

```
Vintagestory/
  Vintagestory.exe          ← vanilla, untouched
  VintagestoryLib.dll       ← vanilla, untouched
  VintagestoryAPI.dll       ← vanilla, untouched
  ...
  Optimum.exe               ← entry point (replaces shortcut target)
  Optimum.deps.json
  Optimum.runtimeconfig.json
  .optimum/
    cache/
      manifest.json
      VintagestoryLib.dll   ← patched (auto-generated)
      VintagestoryLib.pdb
      ...
    optimum.json            ← user settings (render scale, LOD, etc.)
    optimum.log             ← patcher log
```

## Risk Assessment

| Risk | Mitigation |
|---|---|
| AssemblyResolve race (runtime loads vanilla before hook) | Optimum.exe has zero VS refs; loads everything via reflection |
| Anti-virus flags modified DLLs in cache | Cache is in user data dir, not Program Files; DLLs are generated locally, not downloaded |
| Cecil patch fails on future VS version | Fail closed: log the error, invalidate the cache, restore built-in vanilla mods, and return a nonzero exit code |
| Performance: SHA256 of 5MB DLL on every start | Use mtime+size fast-path; only hash on mismatch |
| Harmony mods conflict with transplanted methods | Same risk as today - Harmony patches IL at JIT time, after our DLL loads. No change. |
| Mod references vanilla types that we changed | Field type changes (object→Lock) are ABI-compatible for callers using `lock()` - the C# compiler generates different IL but other assemblies calling into the type don't need recompilation |

## Failure Behavior

If patching or startup validation fails for any reason:
1. Log the error to `Logs/optimum-launcher.log`.
2. Invalidate the cache manifest so the next attempt cannot reuse an incomplete patch.
3. Restore built-in vanilla mods from `.optimum/vanilla/Mods` when the launcher copied a patched mod before the failure.
4. Return a nonzero exit code without starting `Vintagestory.exe`.

`--validate-only` always invalidates the cache, patches every target, runs the Cecil evaluation-stack verifier, loads every patched assembly, reflects every type, JIT-validates every non-generic method, and verifies `ClientProgram.Main` before it returns. The launcher holds exclusive locks for the selected data path and game directory during the patch and launch process. The loader rejects required assemblies that resolve outside the patched cache. Cache read, hash, and manifest-write errors use the same abort and restore path. The Windows installer checks the package completion marker, captures the package success flag before its native exit code, runs `Optimum.exe --validate-only` from the staged package before it copies files into the selected install directory, and swaps the package through a temporary tree with rollback. A failed preflight stops installation and prints the captured launcher output.

The decompiler manifest sets an inclusive `ilspycmd` range from `10.1.0.8386` through `10.1.1.8388`. The build prefers `10.1.1.8388` for reproducible CI output. Installers reject versions below or above that range, malformed versions, and prereleases.

## Performance Budget

| Operation | Budget | Notes |
|---|---|---|
| Cache validation (hit) | < 10ms | stat + JSON parse + mtime compare |
| Full SHA256 validation | < 50ms | 5MB DLL, only on mtime mismatch |
| Cecil patch (miss) | < 500ms | Parse + rewrite + save ~5MB DLL |
| GLFW splash open/close | < 20ms | Only on cache miss |
| Assembly.LoadFrom | < 30ms | Standard .NET assembly load |
| **Total (cache hit)** | **< 15ms** | Imperceptible |
| **Total (cache miss)** | **< 600ms** | With splash, feels instant |

## Open Questions (Resolved)

1. **Donor DLL distribution:** ✅ Ships alongside as `VintagestoryLib.Donor.dll`.
   Embedding as resource deferred to Phase 2.
2. **Multiple patched assemblies:** Currently only VintagestoryLib gets
   Cecil-patched at runtime. VSEssentials/VSSurvival/API changes ship as
   pre-compiled fork DLLs copied by the installer. May unify later.
3. **Server-side:** Not planned. The runtime patcher targets the client only.
4. **Signature/hash verification:** Not yet implemented. Low priority
   since the donor DLL is locally compiled from source.
