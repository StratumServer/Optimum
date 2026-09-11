# Vulkan backend acceptance

The acceptance checklist for the native Vulkan backend programme (Phase 0 foundations through
Milestone 1, stable frame delivery with TAA), in the same shape as `docs/taa-acceptance.md`. Every
row is run **once per backend** unless it says otherwise, with the renderer confirmed from the log.
Nothing here passes on a launch alone (`CLAUDE.md` rule 1), and temporal or pacing claims pass on
numbers and logs only, never on screenshot pairs (section 4).

Tooling used by this document:

| Tool | What it does |
|---|---|
| `scripts/dev/run-client.sh` | detached launch, `RENDERER=vulkan\|opengl` rewrites `optimum.json` |
| `scripts/dev/client-renderer.sh` | which renderer actually started - read it every time |
| `scripts/dev/kill-client.sh` | clean close; close as soon as a row is done |
| `scripts/dev/parity-capture.sh` | launch, dump every framebuffer attachment on in-world frame N, confirm the renderer, close |
| `scripts/dev/ssim.py` | per-attachment SSIM and mean absolute difference between two dumps, allowlist-aware |
| `scripts/dev/pacing-gate.sh` | frame-pacing gate against an OpenGL baseline log (Phase 0 diagnostics stage) |
| `scripts/dev/perf-capture.sh` | frame-time capture, mean and 1% low |
| `scripts/dev/luma-diff.py` | still-frame luminance diff medians (TAA rows) |

## 0. Preconditions for every row

The fixed scene, run once per session in the world before any measurement. Without it the scene
moves on its own and every number is noise.

```
/gamemode creative
/time set 12:00
/weather set clearsky
/weather setprecip -1
/weather setw still
```

Per run, before anything is recorded:

```
scripts/dev/client-renderer.sh
jq '.Renderer, .Taa, .TaaSharpness, .TaaMipBias' ~/.config/OptimumVintagestoryData/ModConfig/optimum.json
jq '.ssaa, .fxaa, .ssaoQuality, .bloom, .godRays, .shadowMapQuality, .vsyncMode' ~/.config/OptimumVintagestoryData/clientsettings.json
```

- Same save, same window size, same `clientsettings.json` and `optimum.json` (apart from
  `Renderer`) for both backends of a row; record both `jq` outputs with the row.
- `make deploy` from the commit under test; record the commit hash.
- No other client running (`scripts/dev/kill-client.sh` prints `remaining: 0`).
- Scripted captures (`parity-capture.sh`, `pacing-gate.sh`, `perf-capture.sh`) send no chat
  commands, so the fixed scene must already be the save's state (set it once, save, quit).

## 1. Renderer confirmation, per row

Every recorded row carries the renderer line copied from the client log of that run:

- Vulkan: `[Optimum] Vulkan renderer ...`
- OpenGL: `[Optimum] OpenGL renderer: ...`

`scripts/dev/client-renderer.sh [log]` prints it; `parity-capture.sh` and `perf-capture.sh` refuse
to report when it is missing or names the other backend (the bootstrap falls back to OpenGL
silently). A row without the line, or with the wrong one, is not run - rerun it.

## 2. Acceptance rows

### Phase 0 exit

#### V0.1 GL-vs-GL noise floor
- Commands:
  ```
  scripts/dev/parity-capture.sh --renderer opengl --world "<save>" --frame 300 --out <dir>/gl-a
  scripts/dev/parity-capture.sh --renderer opengl --world "<save>" --frame 300 --out <dir>/gl-b
  scripts/dev/ssim.py <dir>/gl-a <dir>/gl-b --csv <dir>/gl-vs-gl.csv
  ```
- Pass: recorded, not gated. Two launches of one save are not bit-deterministic: world time,
  weather, entities, particles and the shadow cascades move between launches, so `ssim.py` exiting
  1 here is expected. The per-attachment SSIM is the floor that M1.6 is judged against. An
  attachment at 1.0000 in both launches is deterministic or unused on that frame.
- Record: both renderer lines, the GPU line, the table, the CSV.

#### V0.2 Vulkan-vs-GL parity table (baseline, informational)
- Commands: `parity-capture.sh --renderer vulkan ... --out <dir>/vk` with the same save and frame, then
  `scripts/dev/ssim.py <dir>/gl-a <dir>/vk --allowlist docs/parity-allowlist.md --csv <dir>/vk-vs-gl.csv`.
- Pass: recorded, not gated; the starting point for M1.6.
- Record: both renderer lines, the table, every attachment below its V0.1 floor with a one-line note.

#### V0.3 Pacing baseline, OpenGL
- Commands:
  ```
  scripts/dev/perf-capture.sh --renderer opengl --vsync off --seconds 60 --world "<save>" --label p0-gl --out <dir>/perf-gl
  scripts/dev/pacing-gate.sh --renderer opengl --fps <dir>/perf-gl/fps.log
  ```
- Pass: recorded, not gated.
- Record: renderer and GPU lines, median window mean, p99 and stddev, the fps log.

#### V0.4 Pacing baseline, Vulkan
- Commands:
  ```
  scripts/dev/perf-capture.sh --renderer vulkan --vsync off --seconds 60 --world "<save>" --label p0-vk --out <dir>/perf-vk
  scripts/dev/pacing-gate.sh --renderer vulkan --fps <dir>/perf-vk/fps.log --stats <dir>/perf-vk/vulkan-stats.log --baseline <dir>/perf-gl/fps.log
  ```
- Pass: recorded; the gate's verdict is noted but does not block Phase 0.
- Record: renderer and GPU lines, the gate table, per-second medians of the `stats.waits` and
  `stats.counters` tokens, the logs.

#### V0.5 Builds, suites, deploy output
- Commands: `dotnet build VintageStory.slnx -c Release`; `dotnet test Optimum.Tests -c Release`;
  `dotnet test Optimum.Render.Vulkan.Tests`; `bash scripts/extract-patches.sh && bash scripts/check-patches.sh`;
  `make deploy`.
- Pass: 0 errors, all suites green, 0 conflicts and 0 pending; the deployed files differ from the
  previous phase only by the stats and diagnostics changes.
- Record: the summary line of each command.

### Phase 0 exit results (2026-09-11)

Hardware: NVIDIA GeForce RTX 4070 Laptop GPU, driver 615.71.09, on every run (the GPU line is in
`docs/gpu-verification-2026-09-11/phase0/renderer-lines.txt`). Settings: `ssaa` 1.0, `fxaa` on,
`ssaoQuality` 1, `bloom` on, `godRays` 0, `shadowMapQuality` 1, `viewDistance` 256, `Taa` on,
`vsyncMode` 0 for the pacing runs. Section 0's fixed-scene commands were **not** applied (the capture
scripts send no chat commands): parity is the save as loaded at in-world frame 300, pacing is 60 s
of standing in that save.

Pacing (median of per-second windows, `pacing-gate.sh`):

| | OpenGL | Vulkan |
|---|---|---|
| mean frame time | 6.08 ms | 9.90 ms |
| p99 | 8.51 ms | 20.08 ms |
| stddev | 0.55 ms | 4.96 ms |
| gate | pass (p99 rule) | fail: p99, stddev vs baseline, blocking uploads |

Vulkan stats, per-second medians over 90 samples: frame-pacing fence waits 102 (349 ms),
**flush-frame 151 (145 ms)**, occlusion-query reads 101, blocking uploads 50 (38 ms, max 193 and
677 ms), present 102 (13 ms, max 900 ms), queue submits 252, rendering scopes 4040, barriers 6766,
dynamic-state commands 172256, uniform ring 258 KiB of 16 MiB, ReBAR fallbacks 0. `GetQueryResult`
flushes the half-recorded frame once per frame, so every frame is split and waited on at least twice.

Parity: GL-vs-GL (V0.1) is below 0.98 on 9 attachments (Primary colour 0.968, Primary colour2
0.958, motion 0.979, glow alpha 0.978, Luma 0.974, far shadow map 0.947, SSAO colour 0.974, TAA
history colour 0.968/0.969). Vulkan-vs-GL (V0.2) attachments clearly below their floor:

| attachment | GL-vs-GL | VK-vs-GL | note |
|---|---|---|---|
| 13-SSAO color1 alpha | 1.0000 | 0.0001 | GL 1.0 everywhere, Vulkan 0.0: an attachment channel the shader never writes (`CLAUDE.md` rule 9) |
| 0-Primary color2 (rgba16f) | 0.958 | 0.866 | |
| 0-Primary color0 | 0.968 | 0.886 | |
| 10-Luma, 19/20 TAA history colour | 0.974 / 0.968 | 0.903 | follows Primary colour |
| 0-Primary color4 (motion) | 0.979 | 0.907 | |
| 0-Primary color2 alpha, color1 alpha | 0.984 / 0.978 | 0.950 / 0.923 | |

### Phase 1 exit results (2026-09-11)

Deployed `f373c4a` (Phase 1A and 1B merged and reviewed). NVIDIA GeForce RTX 4070 Laptop GPU, driver
615.71.09, same settings as Phase 0, section 0's scene commands not applied. Logs in
`docs/gpu-verification-2026-09-11/phase1/` (local).

- Both renderers start; renderer and GPU lines confirmed from each client log.
- Forced install failure (`OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE=1`): "[Optimum] Vulkan unavailable,
  reopening for OpenGL: forced by OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE", then OpenGL on the RTX 4070
  rendered the world and wrote its parity dump.
- Validation with `sync,best` from load through in-world frame 300: 0 error lines.
- Pacing, same session, medians of per-second windows:

| | OpenGL | Vulkan (Phase 0 Vulkan) |
|---|---|---|
| mean | 8.97 ms | 8.21 ms (9.90) |
| p99 | 19.76 ms | 18.07 ms (20.08) |
| stddev | 4.62 ms | 3.74 ms (4.96) |
| blocking uploads per sample | - | 0 in all 90 samples (up to 193) |
| gate | - | pass: stddev vs baseline, blocking uploads, dropped mesh writes, uniform overflows; fail: p99 <= 1.5 x mean |

- **The OpenGL pacing state is bimodal between launches, independent of the build.** A/B/A in one
  session, OpenGL, 60 s each: current build 6.08 ms mean / 6.74 p99 / 0.28 stddev; Phase 0 build
  (`7685fcc`) 8.79 / 18.00 / 4.16; current build again 9.11 / 18.48 / 4.43. The same build produced
  both states, so the Phase 1 OpenGL numbers above are the slow state, not a regression. Pacing
  comparisons therefore interleave runs (see M1.1).
- Parity, Vulkan vs OpenGL, tracks Phase 0 except: far shadow map 0.845 (0.940); `0-Primary color2`
  reads SSIM 0 because some foliage writes NaN normals, on both backends (OpenGL 7992 texels, Vulkan
  1914, the OpenGL fallback run 177, same screen region; none in Phase 0), recorded for Phase 3's native
  shaders; the SSAO colour1 alpha gap (OpenGL 1.0, Vulkan 0.0) is unchanged and belongs to Phase 2.
- Not done at this exit, carried to the Milestone 1 session: a 10-minute session, a resize, alt-tab and
  minimise loop in a real window, the sun glare and the fork bridge (clouds, world map, boat water mask)
  judged on screen.

### Milestone 1 (Phase 2 exit): stable frame delivery with TAA

All numbers first, then eyes. Each row names the plan's definition of done verbatim.

#### M1.1 Pacing gate
- Commands: `scripts/dev/perf-capture.sh` once per backend on the same scene and settings (V0.3, V0.4),
  then `scripts/dev/pacing-gate.sh --renderer vulkan --fps <vk fps.log> --stats <vulkan-stats.log> --baseline <gl fps.log>`.
- Protocol: OpenGL pacing on this machine is bimodal between launches (Phase 1 exit, A/B/A). Run at
  least OpenGL, Vulkan, OpenGL, Vulkan in one session and judge each Vulkan run against its neighbouring
  OpenGL run; a pair whose two OpenGL runs disagree by more than 1 ms stddev is re-run.
- Pass: exit 0 - blocking uploads 0 in every sample, median window stddev <= baseline x 1.25,
  median window p99 <= 1.5 x median mean, dropped mesh writes 0, uniform overflows 0.
- Record: renderer and GPU lines, the gate output, both log paths.

#### M1.2 Blocking uploads and waits
- Commands: `OPTIMUM_VULKAN_STATS=<file>` over load plus a 10-minute session.
- Pass: blocking uploads 0 during load and during the session; blocking waits exactly 1 per frame.
- Record: renderer line, the stats file, the per-sample counters.

#### M1.3 Acquire ordering
- Commands: stats or trace of a presented frame.
- Pass: acquire happens after the render submit; the acquire wait stage is `TRANSFER` or
  `COLOR_ATTACHMENT_OUTPUT`.
- Record: renderer line, the stats or trace excerpt.

#### M1.4 Scopes and passes
- Commands: `OPTIMUM_VULKAN_STATS=<file>` on the fixed scene.
- Pass: `ScopesOpened == PassCount`; no layout transition inside a rendering scope.
- Record: renderer line, the counters.

#### M1.5 Validation, scripted session
- Commands: `OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best`, session:
  menu -> world -> weather -> water -> night -> resize -> shader reload -> screenshot -> exit.
- Pass: zero `[error]` lines in the validation log.
- Record: renderer line, the validation log path and its `[error]` / `[warning]` counts.

#### M1.6 Per-attachment parity, TAA off
- Commands: section 0 with `Taa: false`; in one session, two OpenGL captures (V0.1) and one Vulkan
  capture at the same frame; `ssim.py <gl-a> <gl-b>` for the floor and
  `ssim.py <gl-a> <vk> --allowlist docs/parity-allowlist.md --csv <file>`.
- Pass: for every attachment, VK-vs-GL SSIM >= min(0.98, that session's GL-vs-GL SSIM - 0.01), or an
  allowlist row whose bound holds. A fixed 0.98 alone is not reachable: launches of one save differ
  (V0.1 measured 0.947 on the far shadow map).
- Record: both renderer lines, both tables, the allowlist rows used.

#### M1.7 TAA still-frame stability
- Commands: `Taa: true`; `docs/taa-acceptance.md` section 1 (seven screenshot pairs per backend,
  `scripts/dev/luma-diff.py --median`).
- Pass: the Vulkan median is within 0.3 of the OpenGL median (reference VK 1.84 / GL 1.87,
  `docs/taa-acceptance.md` section 1).
- Record: both renderer lines, the fourteen diffs, both medians.

#### M1.8 TAA acceptance rows re-pass
- Commands: `docs/taa-acceptance.md` rows A11, A13, A14, A15, A17, A18.
- Pass: each row's own pass criterion, on both backends.
- Record: per row, as that document asks.

#### M1.9 In-game judgement
- Commands: `run-client.sh` on each backend, renderer line confirmed.
- Pass: the user judges it in game on both backends.
- Record: both renderer lines, the user's verdict and date.

## 3. Methods

### Parity dump and SSIM
- `OPTIMUM_PARITY_DUMP=<absolute dir> OPTIMUM_PARITY_FRAME=<n>`: the client counts frames rendered
  while the player is in the world (0 = the first) and on frame n, after the post chain and the
  final FSR or plain blit and before presentation, dumps every attachment of every framebuffer slot
  once, then logs `[Optimum] parity dump: <count> attachments -> <dir>`. Unset, the per-frame cost is
  one static bool check.
- File names, identical on both backends (one format string, `OptimumParityDump.FileNameFormat`):
  `<slotIndex>-<slotName>-<color<i>|depth>-<format>.<ext>`. A texture shared by two slots is written
  once, under the first slot.
- Encodings, rows bottom-up in GL order on both backends (a PPM opened in a viewer appears upside
  down): 8-bit unsigned-normalised formats as binary PPM (RGB) plus a PGM of alpha when the format
  has alpha; float and depth formats as PFM, little-endian float32 (negative scale), RGB in one file
  and alpha in `.alpha.pfm`, so HDR values and depth compare exactly.
- `scripts/dev/parity-capture.sh --renderer vulkan|opengl --world <name> --frame <n> --out <dir>`
  wraps one capture (config restored on exit, renderer line required, no chat commands).
- `scripts/dev/ssim.py <dirA> <dirB> [--allowlist docs/parity-allowlist.md] [--threshold 0.98] [--csv out.csv]`:
  SSIM with an 11x11 Gaussian window (sigma 1.5) on luminance for PPM and per channel for PFM
  (lowest channel reported), plus mean absolute difference; exits non-zero when an attachment is
  below the threshold and not allowlisted, or exists on one side only. `--self-test` checks the tool.

### Pacing gate
- `scripts/dev/pacing-gate.sh --renderer vulkan --seconds 60 --baseline <gl-log>` (built by the Phase 0
  diagnostics stage): exits non-zero unless blocking uploads = 0 in every sample, stddev <= baseline
  x 1.25, p99 <= 1.5 x mean, dropped mesh writes = 0 and uniform overflows = 0. It reads
  `OPTIMUM_FPS_LOG` (with `stddev`) and `OPTIMUM_VULKAN_STATS`.

### Validation log
- `OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best` (log:
  `$TMPDIR/optimum-vulkan-validation.log`, or set the first variable to a path). Read it before
  instrumenting anything: a bug that flickers between frames is invisible to screenshots and to
  per-frame probes (`CLAUDE.md` rule 9).

### Luminance-diff medians
- `docs/taa-acceptance.md` section 1: still camera, seven pairs one second apart per backend,
  `scripts/dev/luma-diff.py --median a1 b1 a2 b2 ...` in explicit pair order, compare medians.
  Used for the TAA still-frame row only; it is not flicker evidence.

## 4. Evidence rules

From the plan's "Verification and evidence rules":

- A launch is a verification only with the `[Optimum] Vulkan renderer` / `[Optimum] OpenGL renderer:`
  line in the log; both backends at every phase exit; game closed afterwards.
- Accepted evidence for temporal and pacing claims: the `sync,best` validation log; a multi-frame
  GPU test with Present between frames and no readback in the loop; the pacing-gate numbers;
  per-attachment numeric diffs; a 60 fps `ffmpeg -f x11grab` capture with consecutive-frame region
  diffs for anything called flicker; per-pass timestamps for anything called a stall;
  `OPTIMUM_VULKAN_POISON=1` for anything that might read undefined memory. Screenshot pairs are
  never evidence.
- Every fix: a GPU readback test in `Optimum.Render.Vulkan.Tests` (patterns
  `VulkanDeviceIntegrationTests`, `AttachmentSemanticsTests`, multi-frame `TaaResolveTests`) and a
  source-coverage test in `Optimum.Tests` (pattern `fsr-pipeline-coverage-tests.cs`).
- "OFF is vanilla" is a test, not a claim: the lib diff against `_ref/` is limited to the listed
  regions and contains no renderer-specific code.

## 5. Decision record

One entry per phase exit or milestone, appended, never edited after the fact.

| date | phase / milestone | commit | rows passed | rows failed or deferred (with reason) | evidence paths | decision |
|---|---|---|---|---|---|---|
| 2026-09-11 | Phase 0 exit | 906b40f deployed (Phase 0 merged at cdd7412) | V0.1 and V0.2 recorded, V0.3 and V0.4 recorded, V0.5 pass (build 0 errors, Optimum.Tests 1056, GPU 386 with sync,best, check-patches 0 conflicts) | section 0 fixed scene not applied; Vulkan fails the pacing gate on p99, stddev and blocking uploads (the Milestone 1 target, not a Phase 0 gate) | `docs/gpu-verification-2026-09-11/phase0/` | Phase 0 accepted; Phase 1A and 1B start. M1.6 changed to a noise-floor rule. User observed no Vulkan jitter on these runs (driver 615.71.09, sky-direction fix not deployed). |
| 2026-09-11 | Phase 1 exit (1A + 1B) | f373c4a deployed | both renderers start; forced-install-failure fallback renders on OpenGL; sync,best validation 0 errors; Vulkan blocking uploads 0 in all samples; Vulkan pacing better than Phase 0 on mean, p99 and stddev; build 0 errors, Optimum.Tests 1128, GPU 494 | Vulkan p99 fails 1.5 x mean; 10-minute session, window resize/alt-tab/minimise loop, sun glare and fork bridge on screen carried to Milestone 1; OpenGL pacing found bimodal between launches (A/B/A), not a regression | `docs/gpu-verification-2026-09-11/phase1/` | Phase 1 accepted; Phase 2 (frame graph) starts; M1.1 now interleaves runs |

## 6. Vendor matrix

Filled per phase exit. A cell holds the row ids that passed on that machine and the date; an empty
cell was not run. The Arc 140V row is filled before the Phase 4 exit.

| vendor / GPU | OS | driver | Vulkan API | forced fallback tiers tested | Phase 0 (V0.1-V0.5) | Milestone 1 (M1.1-M1.9) | notes |
|---|---|---|---|---|---|---|---|
| Intel Arc 140V | Windows | | | | | | |
| NVIDIA | Linux | | | | | | |
| Intel (Mesa ANV) | Linux | | | | | | |
| AMD RDNA | Linux | | | | | | |
