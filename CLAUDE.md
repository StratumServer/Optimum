# Optimum: working rules for agents

Optimum is a performance mod for Vintage Story: a patched client (OpenGL path) plus a Vulkan
backend behind the `IOptimumGraphicsDevice` seam. Read this before touching anything. The
skills in `.claude/skills/` hold the step-by-step procedures; this file holds the rules.

## Where the truth lives (edit these, never the generated copies)

| What | Edit here | Generated from it | Ships as |
|---|---|---|---|
| Game client code | `build/VintagestoryLib/**` (decompiled + patched) | `patches/VintagestoryLib/*.patch` via `scripts/extract-patches.sh` | Cecil transplant into vanilla DLL; every changed/new method or member MUST be listed in `Optimum.Patcher/Program.cs` |
| Game API | `VintagestoryApi/**` (hand-maintained fork, git-ignored) | `sources/VintagestoryApi/**` via extract | `VintagestoryAPI-patched.dll`; new files also go in `optimum-api-contracts/optimum-api-contracts.csproj` (path `..\sources\VintagestoryApi\...`) and get a `<Compile Remove>` in both `VintagestoryApi/VintagestoryAPI.csproj` and `sources/VintagestoryApi/VintagestoryAPI.csproj` |
| Mods | `VSEssentials/`, `VSSurvivalMod/`, `VSCreativeMod/` (forks) | `patches/<mod>/*.patch` via extract | recompiled mod DLLs plus `Optimum.Patcher/mod-patcher.cs` manifests for the installed-runtime path |
| Shaders | `sources/shaders/*.vsh/.fsh` (override vanilla by file name) | shipped by `make deploy` and `scripts/package-*` | includes: `sources/shaderincludes/` (add to deploy and packagers when first used) |
| Vulkan backend | `Optimum.Render.Vulkan/**` | - | `Optimum.Render.Vulkan.dll` + `Silk.NET.*.dll` beside the client (`make deploy` copies them) |
| Vanilla reference | `_ref/**` and `.vanilla/**/assets` | read-only | - |

Never edit `patches/*.patch` or `sources/VintagestoryApi/**` by hand; extract overwrites them.
`.baseline/` is the decompiled vanilla; csproj overlays are folded into it by bootstrap, so a new
`<Compile Remove>` must also be added to `.baseline/VintagestoryApi/VintagestoryAPI.csproj` locally
or extract will keep emitting a stray csproj patch.

## Build, deploy, run, verify

```
dotnet build VintageStory.slnx -c Release          # everything
dotnet test Optimum.Render.Vulkan.Tests            # GPU tests, validation layers on (needs a GPU)
dotnet test Optimum.Tests -c Release               # source/patch coverage tests
bash scripts/extract-patches.sh && bash scripts/check-patches.sh   # after editing build/, forks, API
make deploy                                        # Cecil patch + copy into .vanilla/win-x64/vintagestory
scripts/dev/run-client.sh ["world name"]           # detached launch; RENDERER=vulkan|opengl env switches
scripts/dev/client-renderer.sh                     # which renderer ACTUALLY started (read this every time)
scripts/dev/screenshot.sh /tmp/x.png               # then look at the image with Read
scripts/dev/kill-client.sh                         # clean close; never pkill -f from a shell that mentions the process
```

Data dir: `~/.config/OptimumVintagestoryData` (`clientsettings.json`, `ModConfig/optimum.json` with
`"Renderer"`). Saves: `Saves/*.vcdbs`; pass the bare world name to `-o`, not the file name.
Settings that change what you see: `ssaa` (0.5 renders at half res on BOTH backends), `fxaa`,
`ssaoQuality`, `bloom`, `godRays`, `mipMapLevel`.

Backend diagnostics: `OPTIMUM_VULKAN_VALIDATION=1`, `OPTIMUM_RENDER_TRACE=<file>` (per-draw
trace: `program N 'name'`, `fullscreen program= tex0= target=`, `bind unit= texture=`,
`validation:` lines), `OPTIMUM_DUMP_TEXTURES=<ids> OPTIMUM_DUMP_DIR=<abs> OPTIMUM_DUMP_AFTER_SECONDS=60`
(PPM dumps of live textures; without the delay you dump the menu), `OPTIMUM_VULKAN_STATS=<file>`.

## Rules that came from real failures

1. **A launch is not a verification.** The bootstrap falls back to OpenGL silently; MangoHud only
   shows on Vulkan. Grep the log for `[Optimum] Vulkan renderer` / `[Optimum] OpenGL renderer:`
   before saying anything about rendering. A PR was merged on an OpenGL run because this was skipped.
2. **Look at pixels, then diff the two paths.** For any "X looks wrong on Vulkan": capture a baseline
   (screenshot + trace + validation log) first, then read the GL branch and the device branch of the
   same method side by side and list every state difference (sampler filter/wrap/mip/border/compare,
   blend enable vs per-attachment factors, draw-buffer masks, clears, viewports, formats). The bugs
   have all been parity gaps, never shader maths. Do not theorise from symptoms.
3. **Verify in the game, both backends, before claiming done.** Deploy, run, screenshot, compare with
   OpenGL live. Component tests passing is not evidence for the screen.
4. **Every fix gets a GPU readback test** in `Optimum.Render.Vulkan.Tests` (pattern:
   `VulkanDeviceIntegrationTests`, `AttachmentSemanticsTests`) and, for lib/patch changes, a
   source-coverage test in `Optimum.Tests` (pattern: `fsr-pipeline-coverage-tests.cs`).
5. **Process hygiene.** Launch through `scripts/dev/*.sh` (setsid wrappers). Never put `pkill -f` or
   `pgrep -f` in a command that also contains the process name in a heredoc or string: it matches the
   calling shell and the tool dies with exit 144. Close the game with the kill script (window close
   first) to avoid shutdown-race crash reports.
6. **Git.** Never `git stash`. Commit WIP on the branch with a `wip:` prefix instead. Branch from
   `main` (tracks `origin/main` = NightHammer1000/VulkanStory; `upstream` = StratumServer/Optimum).
   Commit only when asked or when a phase is verified; say what was verified in the message.
7. **Batch reads.** Read whole methods and both paths in one command (`sed -n` ranges + `rg`), not
   ten single greps. Codex found in one pass what took an afternoon of small probes.
8. **Agents cost money.** The session model is Fable. Only launch subagents with an explicit
   cheaper `model` ("sonnet", "haiku") and low effort for mechanical work; Fable does the hard parts
   itself. For plan reviews and stuck rendering bugs, hand off to Codex (`.claude/skills/codex-handoff`)
   with symptom + repro only, no theories.

## Testing notes
- `Optimum.Render.Vulkan.Tests` GPU tests must read back inside a frame; `BindFramebuffer`/`ClearColor`
  are no-ops between frames.
- Vulkan named UBOs are per-draw snapshots (fixed 2026-09-10); the uniform ring is 32 MiB.
- Shader pairs dropped in `sources/shaders/` are auto-translated by `ShaderTranslationTests`.
