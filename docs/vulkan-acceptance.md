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

#### V0.1 GL-vs-GL parity identity
- Commands:
  ```
  scripts/dev/parity-capture.sh --renderer opengl --world "<save>" --frame 300 --out /tmp/parity/gl-a
  scripts/dev/parity-capture.sh --renderer opengl --world "<save>" --frame 300 --out /tmp/parity/gl-b
  scripts/dev/ssim.py /tmp/parity/gl-a /tmp/parity/gl-b --csv /tmp/parity/gl-vs-gl.csv
  ```
- Pass: `ssim.py` exits 0 **without** `--allowlist`, every attachment reports 1.0000 and no file
  exists on one side only. Anything below 1.000 means the capture is not deterministic (the scene
  moved, or an attachment holds undefined contents) and every later parity number is void until it
  is explained.
- Record: both renderer lines, the table, the CSV path, the commit.

#### V0.2 Vulkan-vs-GL parity table (baseline, informational)
- Commands: `parity-capture.sh --renderer vulkan ... --out /tmp/parity/vk` with the same save and
  frame, then `scripts/dev/ssim.py /tmp/parity/gl-a /tmp/parity/vk --allowlist docs/parity-allowlist.md --csv /tmp/parity/vk-vs-gl.csv`.
- Pass: recorded, not gated - this is the starting point Milestone 1 row M1.6 is judged against.
- Record: both renderer lines, the full table, every attachment below 0.98 with a one-line note.

#### V0.3 Pacing baseline, OpenGL
- Commands: `scripts/dev/pacing-gate.sh --renderer opengl --seconds 60` on the fixed scene (the
  OpenGL run records the baseline log the Vulkan gate reads).
- Pass: recorded, not gated.
- Record: renderer line, mean, stddev, p99, the log path.

#### V0.4 Pacing baseline, Vulkan
- Commands: `scripts/dev/pacing-gate.sh --renderer vulkan --seconds 60 --baseline <V0.3 log>`.
- Pass: recorded; the gate's verdict is noted but does not block Phase 0.
- Record: renderer line, mean, stddev, p99, blocking uploads, dropped mesh writes, uniform
  overflows per sample, the log path.

#### V0.5 Builds, suites, deploy output
- Commands: `dotnet build VintageStory.slnx -c Release`; `dotnet test Optimum.Tests -c Release`;
  `dotnet test Optimum.Render.Vulkan.Tests`; `bash scripts/extract-patches.sh && bash scripts/check-patches.sh`;
  `make deploy`.
- Pass: 0 errors, all suites green, 0 conflicts and 0 pending; the deployed files differ from the
  previous phase only by the stats and diagnostics changes.
- Record: the summary line of each command.

### Milestone 1 (Phase 2 exit): stable frame delivery with TAA

All numbers first, then eyes. Each row names the plan's definition of done verbatim.

#### M1.1 Pacing gate
- Commands: `scripts/dev/pacing-gate.sh --renderer vulkan --seconds 60 --baseline <OpenGL log of the same scene>`.
- Pass: exit 0 - blocking uploads 0 in every sample, stddev <= baseline x 1.25, p99 <= 1.5 x mean,
  dropped mesh writes 0, uniform overflows 0.
- Record: renderer line, the gate output, both log paths.

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
- Commands: section 0 with `Taa: false`; `parity-capture.sh` once per backend at the same frame;
  `scripts/dev/ssim.py <gl> <vk> --allowlist docs/parity-allowlist.md --csv <file>`.
- Pass: `ssim.py` exits 0 - SSIM >= 0.98 on every attachment, or an allowlist row whose bound holds.
- Record: both renderer lines, the table, the allowlist rows used.

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

## 6. Vendor matrix

Filled per phase exit. A cell holds the row ids that passed on that machine and the date; an empty
cell was not run. The Arc 140V row is filled before the Phase 4 exit.

| vendor / GPU | OS | driver | Vulkan API | forced fallback tiers tested | Phase 0 (V0.1-V0.5) | Milestone 1 (M1.1-M1.9) | notes |
|---|---|---|---|---|---|---|---|
| Intel Arc 140V | Windows | | | | | | |
| NVIDIA | Linux | | | | | | |
| Intel (Mesa ANV) | Linux | | | | | | |
| AMD RDNA | Linux | | | | | | |
