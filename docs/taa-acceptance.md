# TAA acceptance matrix (TAA-PLAN.md P5)

The P5 acceptance matrix as a runnable checklist. Every row is run **twice, once per
backend**, with the renderer confirmed from the log, and **TAA on vs off**. Nothing here
is passed on a launch alone: rule 1 of `CLAUDE.md` says a launch is not a verification.

Tooling used by this document:

| Tool | What it does |
|---|---|
| `scripts/dev/run-client.sh` | detached launch, `RENDERER=vulkan\|opengl` rewrites `optimum.json` |
| `scripts/dev/client-renderer.sh` | which renderer actually started - read it every time |
| `scripts/dev/screenshot.sh` | one PNG of the active window |
| `scripts/dev/kill-client.sh` | clean close; close as soon as a row is done |
| `scripts/dev/perf-capture.sh` | launch, warm up, record 30 s of frame times, close, print mean and 1% low |
| `scripts/dev/luma-diff.py` | still-frame luminance diff (parity skill section 2c) |

## 0. Preconditions for every row

Run once per session, in the world, before any measurement. Without these the scene moves
on its own and every luminance number is noise rather than temporal instability.

```
/gamemode creative          # hunger damage and mob pressure change the picture mid-run
/time set 12:00             # fixed sun angle
/weather set clearsky       # no storm lighting
/weather setprecip -1       # storms off
/weather setw still         # wind stilled: foliage sway otherwise dominates the diff
/tprivate off               # optional: keep chat overlays out of the crop
```

Renderer and TAA state, before each run:

```
scripts/dev/client-renderer.sh            # must print "[Optimum] Vulkan renderer" or "[Optimum] OpenGL renderer:"
jq '.Renderer, .Taa, .TaaSharpness, .TaaMipBias' ~/.config/OptimumVintagestoryData/ModConfig/optimum.json
```

## 1. The measurement to record

**Still-frame luminance diff** (`.claude/skills/vulkan-parity-debug/SKILL.md` section 2c).
Still camera, screenshot pairs one second apart, mean absolute luminance difference over the
centre 60% crop, **seven pairs per backend**, compare the **medians** - never a single pair.

```
for i in 1 2 3 4 5 6 7; do
  scripts/dev/screenshot.sh /tmp/taa-shots/a$i.png; sleep 1
  scripts/dev/screenshot.sh /tmp/taa-shots/b$i.png; sleep 1
done
scripts/dev/luma-diff.py --median /tmp/taa-shots/*.png
```

Reference from the TAA round: Vulkan 1.84 vs OpenGL 1.87 (medians 1.74 / 1.72). **Above ~3 on
one backend only is a real bug**; equal-but-high on both backends means the scene is still
moving, so go back to section 0. A screenshot pair cannot see one-frame alternation (P4
addendum): for shimmer suspicions read the validation log with
`OPTIMUM_VULKAN_VALIDATION=1`, not the client log.

Record per row: backend, TAA on/off, the seven diffs, the median, and the screenshot paths.

Debug views (`TaaDebugView` in `optimum.json`, read by `taa-debug.fsh`) are the diagnosis tool
when a row fails, not the pass criterion: `1` motion as colour, `2` reactive mask, `3` validity
(green written-and-matching, red rejected, black never written), `4` scene with motion overlay.

## 2. Acceptance rows

Each row: the save/scene to reach, the exact commands, what "pass" looks like, and the
measurement to record. "TAA off byte-identical" is checked once, in row A18, not per row.

### A1. moving silhouettes on contrast
- Scene: a dark tree line or a player silhouette against bright sky, camera panning slowly.
- Commands: `RENDERER=vulkan scripts/dev/run-client.sh "serene cave world"`; section 0; pan with the mouse at a constant rate; `scripts/dev/screenshot.sh /tmp/taa-shots/a1-<backend>-<taa>.png`.
- Pass: the silhouette edge is smooth while moving and shows no trailing smear behind it; the edge is not softer with TAA on than FXAA gives when still.
- Record: still-frame luminance diff median (section 1) plus one in-motion screenshot per backend for the smear check.

### A2. transparent foreground and background motion
- Scene: glass blocks or a waterfall in front of moving terrain, then the same with the transparent surface itself moving past a static background.
- Commands: section 0; place glass in creative in front of a waterfall; pan; screenshot pairs; `TaaDebugView=2` to read the reactive mask from the OIT merge.
- Pass: no ghost of the background through the transparent surface and no ghost of the transparent surface on the background; reactive is non-zero where the transparent surface is (view 2) except where a later writer legitimately claims the pixel (finding (y)).
- Record: luminance diff median; a `TaaDebugView=2` screenshot per backend.

### A3. thin fences
- Scene: a long fence run against sky, viewed at a shallow angle so posts are sub-pixel.
- Commands: section 0; build or find a fence line; still camera for the diff, then a slow strafe.
- Pass: posts stay continuous while strafing, no dropouts and no crawling; sub-pixel rails do not flicker between frames.
- Record: luminance diff median (still) plus a 2 s strafe capture per backend.

### A4. hand/world FOV
- Scene: first-person hands with an item held, world geometry behind, camera turning.
- Commands: section 0; hold a torch or a tool; turn; also toggle the hand-FOV setting in the settings GUI mid-run.
- Pass: the held item does not ghost against the turning world and the world does not ghost against the held item; the hand keeps its own previous projection (P3 hand-FOV rule).
- Record: luminance diff median; `TaaDebugView=3` screenshot (hands must be green, not black).

### A5. quern/gear
- Scene: a running quern and a mechanical-power gear network (windmill or a hand-cranked line).
- Commands: section 0; power a quern; stand so both the quern top and several gears are on screen; still camera.
- Pass: the rotating quern top and every instanced gear are sharp while turning, no smear ring, no stutter in the instance transforms.
- Record: luminance diff median with the mechanism running (this row is expected to be above the static baseline - compare the two backends to each other, not to 1.74); `TaaDebugView=1` screenshot showing motion on the moving parts only.

### A6. dropped items
- Scene: a pile of dropped items bobbing, plus one item thrown past the camera.
- Commands: section 0; `/giveblock` or drop a stack; watch the bob for 10 s.
- Pass: the bobbing items do not leave a vertical smear; a thrown item has a clean leading edge.
- Record: luminance diff median with items on screen; one in-motion screenshot per backend.

### A7. fire
- Scene: a firepit or a torch cluster, still camera.
- Commands: section 0; light a firepit; frame it centre-screen.
- Pass: flame particles stay crisp, no accumulation haze around the flame, the geometry behind the flame keeps its own anti-aliasing (the cube-particle reactive-1 question carried from P4).
- Record: luminance diff median with the fire in the crop and, for comparison, one with the fire outside the crop.

### A8. rain
- Scene: falling rain over terrain.
- Commands: section 0 except `/weather setprecip 1`; then restore `-1` afterwards.
- Pass: raindrops do not smear into streaks beyond their real length and the terrain behind them stays anti-aliased.
- Record: luminance diff median (expected high on both backends - compare backends to each other).

### A9. clouds
- Scene: volumetric clouds overhead, camera pointed up, then a slow pan along the horizon.
- Commands: section 0; look up; also set `TaaDebugView=2` to read the cloud reactive value.
- Pass: cloud edges do not shimmer and do not smear when panning; the sky's own anti-aliasing survives (the reactive-1 cost question and the `mix(coverage, 1, coverage)` curve, carried from P4).
- Record: luminance diff median looking up; `TaaDebugView=2` screenshot per backend.

### A10. aurora
- Scene: night sky with aurora active.
- Commands: `/time set 0:00`; face north; still camera.
- Pass: the aurora ribbons move without leaving a persistent trail and without stepping.
- Record: luminance diff median (expected above the static baseline; compare backends).

### A11. underwater transitions
- Scene: swim from above water to below and back, repeatedly.
- Commands: section 0; find water at least three deep; cross the surface slowly, then quickly.
- Pass: the frame after the transition shows no history from the other medium - no blue haze above water, no sky in the underwater frame; the liquid velocity pass does not break block outlines on submerged blocks or SSAO near water (carried from P4).
- Record: a screenshot of the first frame after each crossing per backend; luminance diff median while fully submerged and still.

### A12. camera modes (shake, third person, mounted)
- Scene: the same spot in first person, third person (`F5`), on a mount, and with camera shake active (mining, or an explosion).
- Commands: section 0; cycle `F5`; ride a mount; mine a block for the shake.
- Pass: every mode converges - no permanent blur in third person, no ghost of the player model, shake does not smear the world.
- Record: luminance diff median per mode, still camera, per backend.

### A13. reference rebase
- Scene: walk far enough for the camera reference position to rebase (`PlayerCamera.cs:74-76`).
- Commands: section 0; `/tp ~5000 ~ ~5000`, then walk across the rebase boundary on foot.
- Pass: the rebase frame shows a clean reset - one frame of aliasing at worst - and never a whole-screen smear; the log records a reset reason.
- Record: a screenshot of the rebase frame per backend; whether the reset reason appears.

### A14. chunk replacement
- Scene: a chunk remeshing under the camera (place and break blocks; or fly to the edge of loaded terrain and back).
- Commands: section 0; break and place a wall of blocks while looking at it.
- Pass: newly meshed geometry is anti-aliased within a few frames, with no stale history bleeding from the old mesh.
- Record: luminance diff median after the mesh settles; one screenshot of the replacement frame.

### A15. shader reload
- Scene: any world scene; reload shaders from the settings GUI (or the debug command).
- Commands: section 0; trigger a shader reload; then again with TAA on and render scale < 1.
- Pass: the reload raises a temporal reset, the image recovers within a few frames, and no NaN or black frame survives.
- Record: screenshot immediately after the reload and 2 s later, per backend.

### A16. missing-resource fallback
- Scene: TAA requested but the resolve cannot run.
- Commands: with the client closed, move `taa-resolve.fsh` out of `.vanilla/win-x64/vintagestory/assets/game/shaders/`, launch with `Taa: true`, then restore the file and `make deploy` afterwards.
- Pass: the client starts, falls back to FXAA, logs the reason, sets `TaaRuntimeDisabled`, and never renders a black or garbage frame; the settings row reflects the runtime disable.
- Record: the log lines proving the fallback, per backend; one screenshot.

### A17. normal/scaled/mega screenshots
- Scene: any settled scene.
- Commands: a normal screenshot (`F12` or the in-game binding); a scaled screenshot; a mega screenshot.
- Pass: normal and scaled screenshots match the screen; the mega capture is not corrupted by the temporal window (it uses warm-up or a spatial-only path per the plan) and shows no tile seams from stale history.
- Record: the three files per backend and whether the mega capture took the warm-up or the spatial-only path.

### A18. TAA off is byte-identical
- Scene: any fixed scene, camera parked and not touched between the two runs.
- Commands: run once with `Taa: false` on the current build and once with `Taa: false` on the pre-TAA commit, same save, same settings, same window size; compare the screenshots byte for byte (`cmp`) and with `scripts/dev/luma-diff.py`.
- Pass: `cmp` reports identical files, or the luminance diff is exactly 0.000.
- Record: the `cmp` result and the diff value, per backend.

## 3. Performance

### P1. Performance on the Arc 140V
- Scene: a fixed, repeatable walk or a parked camera in a busy scene; the same scene for all four runs.
- Commands:

```
scripts/dev/perf-capture.sh --renderer vulkan --taa off --label vk-off
scripts/dev/perf-capture.sh --renderer vulkan --taa on  --label vk-on
scripts/dev/perf-capture.sh --renderer opengl --taa off --label gl-off
scripts/dev/perf-capture.sh --renderer opengl --taa on  --label gl-on
```

  Each run confirms the renderer from the log before it reports anything, warms up 8 s after
  the `[Client Chat] Welcome` line, records 30 s and closes the client itself. Per-run output
  lands in `/tmp/optimum-perf/<label>/` (`client.log`, `fps.log`, `vulkan-stats.log`,
  `summary.csv`).
- Pass: the TAA-on frame-time cost is within the budget agreed for the handheld, and 1% lows
  do not regress more than the mean does.
- Record, per the plan: **total frame delta** (mean ms, TAA on minus off), **CPU frame time**
  (the same mean - `fps.log` is CPU-side wall time between `MainRenderLoop` entries),
  **1% lows** (the `1% low frame time` line), the **renderer name** (printed by the script from
  the log, not from the argument), and the **power mode and thermals** - these last two have no
  tooling in the repo and must be noted by hand from the handheld's own readout at the start and
  end of each run.
- **GPU pass timestamps** are also owed by the plan and have no implementation on either
  backend (no `vkCmdWriteTimestamp`, no `GL_TIME_ELAPSED` query anywhere). Until they exist,
  record `ScreenManager.FrameProfiler` marks as the per-phase stand-in and say so.

### P2. Memory at 1080p
- Commands: `OPTIMUM_VULKAN_STATS=<file>` is already set by `perf-capture.sh` on Vulkan; read the
  allocation counters from `/tmp/optimum-perf/<label>/vulkan-stats.log` and the target sizes from
  the resolve's allocation log lines.
- Pass: the measured TAA footprint matches the plan's budget: motion 15.8 MiB + two colour
  histories 31.6 MiB + aux 7.9 MiB + prev-depth 15.8 MiB.
- Record: the measured MiB per target and the total, per backend.

## 4. Still owed from P4, to be closed in this matrix

These are carried from the "Still owed for P4" block of `TAA-PLAN.md` and must be answered in
the game, on both backends, with the renderer confirmed from the log.

- **C1. liquid velocity pass**: does it remove the water ghosting it was built for, and what does
  its depth write do to block outlines on submerged blocks, to SSAO near water, and to rifts? (row A11)
- **C2. cloud reactive**: does reactive 1 on cloud-covered sky visibly cost the sky's own
  anti-aliasing, and is `mix(coverage, 1, coverage)` the right curve? (row A9)
- **C3. near-decal ghosting**: did the decal writer remove finding (o)'s ghosting? (row A1, with a decal in frame)
- **C4. mover cleanliness**: helve hammer head, resonator disc, pot lid, a falling block through
  its tumble. (row A5)
- **C5. cube-particle reactive**: does reactive 1 on faint cube particles cost the temporal AA of
  the geometry behind them? (row A7)
- **C6. vertex-warp cost**: measure evaluating the warp twice on liquid, particles and decals, and
  the P3 measurements finding (j) still owes - dense foliage, crowds, gear networks. (row P1)
- **C7. GL path of every P4 addition**: `BeginMotionOnlyWrite`'s `GL_NONE` draw-buffer array,
  `ApplyOptimumMotionAccumulateBlendState`'s `glBlendFunci`, the sky pass's depth-func dance -
  none of it has ever executed, since `Optimum.Render.Vulkan.Tests` is the only GPU harness.
  (every row, OpenGL half)

## 5. Decision

Default-on is decided **only after every row above passes on both backends**, per the plan.
Record the decision and the evidence paths in `TAA-PLAN.md` under the P5 status note.
