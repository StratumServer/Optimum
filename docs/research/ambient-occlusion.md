# Ambient occlusion for the Vulkan path: sources, comparison, combined design

Deep research for the AO step of the Vulkan branch (`feat/vulkan-taa`), collected 2026-09-15. Extends
`docs/research/xegtao-integration.md` (read in full; referred to as "the note"). Every source below was read
from the actual paper or code (downloaded locally for the reading, not committed); the three Shadertoys linked
from bevy#19713 could not be read (Cloudflare challenge on every fetch route), so anything about their internals
is marked [Uncertain] and taken from the issue text, the Bluesky thread and the authors' descriptions.

Conventions: [Inference] = my conclusion, not a source claim. [Uncertain] = could not verify against a primary
source. Repository facts are cited as `file:line` at the current branch.

**Owner decisions taken as given** (`docs/research/xegtao-integration.md` section 0 and the branch status notes
section 4): the chosen AO is the default on Vulkan whenever TAA is active; vanilla SSAO otherwise and always on
OpenGL; AO is composed into the scene before the TAA resolve and never onto the glow attachment; jitter is
`P[8] -= 2*jx/W` (`docs/temporal-frame-contract.md` section 2); the TAA resolve invariants stay (3x3 nearest-depth disocclusion with motion from the nearest-depth tap, luminance anti-flicker 0.3x..1.2x; pinned by `TaaResolveTests` and `Optimum.Tests/taa-antiflicker-coverage-tests.cs`).

**Decisions taken on this document's questions (2026-09-15; section E records them):**
physically correct AO with no vanilla floor and no contrast boost; the hand-view draws are patched to write a class
value into `gNormal.w`; plants, grass and cross-quad blocks are flagged in the same channel; no multi-bounce in the
first version but an explicit albedo hook; the handheld default (render-res 2x2 vs half-res 3x3) is decided by the
section D numbers; the AO working term, edges and mip 0 become opt-in parity-dump and headless outputs.

---

## 0. What this renderer gives the AO pass (constraints, verified)

- **G-buffer.** Primary colour 2 = `gNormal`, colour 3 = `gPosition`, both `RGBA16F`, sampler LINEAR, wrap
  CLAMP_TO_BORDER with a white border, present only when `SSAOQuality > 0`
  (`build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs:2331-2350`; Vulkan mirror
  `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs:76-90`). Depth is `D32_SFLOAT`, 0 = near,
  not reversed (`docs/temporal-frame-contract.md` section 3.1).
- **What is in them.** `gnormal = modelViewMatrix * vec4(normal, 0)` and `gnormal.w = isLeaves ? 1 : 0`
  ("Cheap hax to make SSAO on leaves less bad looking"), `isLeaves = (renderFlags & WindModeBitMask) > 0`
  (`.vanilla/.../shaders/chunkopaque.vsh:72,102-103`); `outGPosition = vec4(camPos.xyz, fogAmount*2 + glowLevel + murkiness)`
  (`chunkopaque.fsh:96-97`). So: **view-space normal in GL convention (z toward the viewer), leaf flag in w;
  view-space position in xyz, an attenuation term in w.** Entities write `outGNormal = vec4(gnormal.xyz, 0)`
  (`entityanimated.fsh:113`), sky writes zeros (`nightsky.fsh:43-44`), cube particles put alpha in w
  (`particlescube.fsh:47`).
- **Foliage is alpha-tested** (`if (aTest < alphaTest || rgba.a < 0.005) discard;`, `chunkopaque.fsh:81`), so
  leaf and grass holes are real holes in the depth buffer. [Inference] This is what makes a thickness-aware
  integration pay off here: light legitimately passes through the holes, and only the solid texels occlude.
- **Vanilla SSAO** (`.vanilla/.../shaders/ssao.fsh`, override `sources/shaders/ssao.fsh`): half resolution
  (`frameBuffers[13]`, `num3 = 0.5f`, `ClientPlatformWindows.cs:2426-2432`), 20 or 24 hemisphere point samples of
  radius 0.9 from a 64-entry kernel, a screen-locked Bayer-128 dither rotated by golden-angle (the override advances
  it by `frac(temporalFrameIndex * (PHI-1))` when `TAAMOTION == 1`, `sources/shaders/ssao.fsh:104-115`), range check
  `depthDiff in (0, 0.2)` (leaves: `[0.02, 0.2)` plus a normal-difference test), a per-pixel normal push
  `fragPos += normal * clamp(-z/150 - 0.05, 0, 10)` for distant flicker, `distanceFade = clamp(1.2 - z/250, 0, 1)`,
  an AO floor `max(occ, 0.5 or 0.7)` and a `1 - (1-occ)*1.4` boost off leaves; then an 11-tap depth-weighted
  separable blur, 1 or 3 iterations (`bilateralblur.fsh`, `ClientPlatformWindows.cs:3482-3496`).
- **Composition today.** `ApplyOptimumSceneSsao` multiplies the blurred half-res AO into Primary colour 0 with
  `EnumBlendMode.Multiply`, `1 - AO` in alpha, before the resolve (`ClientPlatformWindows.cs:3613-3634`,
  `sources/shaders/scene-ssao.fsh`); `final.fsh` skips its own application when `optimumSsaoInScene == 1`
  (`sources/shaders/final.fsh:105-116`). `SSAOLEVEL > 1` takes the min of two vertically adjacent texels
  (`scene-ssao.fsh:10-12`), a half-res upsample fudge.
- **TAA.** Halton(2,3), `phaseCount = max(1, ceil(8 * upscale^2))` (8 at native, 32 at render scale 0.5),
  `FrameIndex` is the clock (`docs/temporal-frame-contract.md` section 2; `OptimumTemporalFrame.cs:372-373`). The
  resolve clips history to the 3x3 YCoCg mean +/- 1.25 sigma (`sources/shaders/taa-resolve.fsh:134-135`), blends with
  `alpha = mix(0.12, 0.03, w*w)` on the luminance-difference weight, `max(alpha, reactive)`
  (`taa-resolve.fsh:288-290`), and rejects on a 3x3 nearest-depth disocclusion test (contract section 4).
- **Compute.** The Vulkan device records no compute dispatch today (no `CmdDispatch` / compute pipeline in
  `Optimum.Render.Vulkan`; only the graph's stage masks already name `ComputeShaderBit`,
  `Optimum.Render.Vulkan/Graph/ResourceUsage.cs:80,168`). A compute pass kind is the first deliverable of the AO
  step (progress doc section 5 item 8) or the first version is fragment-shader based (section C.13).
- **Correction to the note.** The note says "GLSL has no workgroup-shared memory" (section 3, porting traps).
  GLSL compute shaders do have `shared` variables and `barrier()` (GLSL 4.60 spec, "Shared Variables";
  Vulkan GLSL compiles them to the SPIR-V Workgroup storage class). Bevy's `preprocess_depth.wesl` uses exactly that
  (`var<workgroup> previous_mip_depth: array<array<f32, 8>, 8>`). The prefilter can stay one dispatch. [Inference]
  The note's split into four dispatches is still a valid fallback for a fragment-only first version.

---

## A. The sources, one by one

### A.1 XeGTAO (Intel, MIT, archived 2024-04-22)

Files read: `XeGTAO.hlsli`, `XeGTAO.h`, `vaGTAO.hlsl`, README (https://github.com/GameTechDev/XeGTAO).

**Algorithm.** Three compute passes (README "Algorithm overview"):
1. *Prefilter* (`XeGTAO_PrefilterDepths16x16`): 8x8 threads, 2x2 texels each, mips 0-4 of view-space depth with
   `XeGTAO_DepthMIPFilter`: `weight_i = saturate((maxDepth - depth_i) * falloffMul + falloffAdd)`, radius scaled by
   `depthRangeScaleFactor = 0.75` ("found empirically :)"), then the weighted mean (`XeGTAO.hlsli:579-604`).
2. *Main* (`XeGTAO_MainPass`, `XeGTAO.hlsli:~240-575`): per slice `phi = (slice + noiseSlice)/sliceCount * PI`,
   per step `stepNoise = frac(noiseSample + (slice + step*stepsPerSlice) * 0.618...)` (R1 sequence),
   `s = ((step + stepNoise)/stepsPerSlice)^SampleDistributionPower + minS` with `minS = 1.3 / screenspaceRadius`
   ("avoid sampling center pixel"), `mipLevel = clamp(log2(len(sampleOffset)) - DepthMIPSamplingOffset, 0, 5)`,
   sample offset snapped to pixel centres, two depth fetches per step (both sides of the slice), horizon cosines
   initialised to `cos(n +/- PI/2)` instead of -1 ("lowHorizonCos"), sample weight
   `saturate(sampleDist * falloffMul + falloffAdd)` with `falloffFrom = R*(1-0.615)`, then
   `shc = lerp(lowHorizonCos, shc, weight)` and `horizonCos = max(horizonCos, shc)`; the arc integral is the
   GTAO paper's closed form `iarc = (cosNorm + 2*h*sin(n) - cos(2h - n)) / 4` per side, times
   `projectedNormalVecLength` after `lerp(projectedNormalVecLength, 1, 0.05)` ("I can't figure out the slight
   overdarkening on high slopes, so I'm adding this fudge", `XeGTAO.hlsli:531-532`); `visibility /= sliceCount`,
   `pow(visibility, FinalValuePower)`, `max(0.03, ...)`; a small-screen-radius fade
   `visibility += saturate((10 - screenspaceRadius)/100)*0.5`; depth bias `viewspaceZ *= 0.99999` (fp32) or
   `0.99920` (fp16). Edges: `saturate(1.25 - |dz|/(z*0.011))` after a slope adjustment, packed 2 bits each
   (`XeGTAO_CalculateEdges`, `XeGTAO_PackEdges`).
3. *Denoise* (`XeGTAO_Denoise`, `XeGTAO.hlsli:~700-820`): 3x3, two horizontal pixels per thread, centre weight
   `DenoiseBlurBeta` (1.2) on the final pass and `beta/5` otherwise, cardinal weights = the 2-bit edges made symmetric
   (`edgesC *= (edgesL.y, edgesR.x, edgesT.w, edgesB.z)`, "Works real nice with TAA"), diagonals `0.85*0.5 *`
   products of adjacent edges, and a **leak**: when 3-4 edges are set, `edginess = saturate(4 - 2.5 - sum)/1.5 * 0.5`
   is added to all edges ("reduces both spatial and temporal aliasing"). The README says "5x5 depth-aware" but the
   kernel is 3x3 (issue #6, unanswered). Working AO is stored as `visibility / 1.5` (`XE_GTAO_OCCLUSION_TERM_SCALE`,
   "raw, pre-denoised occlusion term can overshoot 1 but will later average out to 1", `XeGTAO.h:114`).

**Thin occluders.** The paper's thickness heuristic (section A.2 eq. 9) is present in two `#if 0` branches; the
active `#else` is "a version where thicknessHeuristic is completely disabled" (`XeGTAO.hlsli:496-506`);
`ThinOccluderCompensation` defaults to 0 and only scales `sampleDelta.z` in the falloff distance ("biases the
near-field bounding falloff along the view vector", README). README: "Rather than implementing the conservative
thickness heuristic from the original paper, this version increases slices while undersampling horizon searches";
auto-tune found only a small gain, so it is off. **XeGTAO has no visibility bitmask and no multi-bounce**: zero
hits for `bitmask|countbits|multibounce|albedo` in `XeGTAO.hlsli` and `vaGTAO.hlsl`; the only `Albedo` in
`XeGTAO.h:89` belongs to the reference ray-traced AO tool.

**Noise.** `index = HilbertLUT[pix % 64] + 288*(NoiseIndex % 64)`; `noise = frac(0.5 + index * R2)` with
`R2 = (0.75487766624669276005, 0.5698402909980532659114)` (`vaGTAO.hlsl:77-85`, "why 288? tried out a few and that's
the best so far"). `NoiseIndex = frameCounter % 64` when denoise is on, else 0 (`XeGTAO.h:196`). README history:
a 2-channel 64x64 tileable blue noise "worked well for spatial-only noise" but "adding temporal offsets/rotations
caused overlaps which would often show as temporal artifacts"; a 3D noise "worked well with TAA but was fairly big
in size and did not work well when using spatial-only filtering"; hence Hilbert+R2. On TAA: "we must keep temporal
variance low enough to avoid having TAA mischaracterizing this noise as features, which limits the amount of
temporal supersampling that we can leverage."

**Presets** (`vaGTAO.hlsl:101-129`): Low 1 slice x 2 steps, Medium 2x2, High 3x3, Ultra 9x3 (steps are per side,
two fetches each). **Cost** (README): High 0.56 ms at 1080p on RTX 2060, 2.39 ms at 1080p on i7-1195G7 integrated
graphics, 1.4 ms at 4K on RTX 3070; Medium ~2/3 of High, Low ~2/3 of Medium; bent normals +25%; Hilbert LUT saves ~7%.

**Defaults** (`XeGTAO.h:107-113`): Radius 0.5, RadiusMultiplier 1.457, FalloffRange 0.615, SampleDistributionPower
2.0, ThinOccluderCompensation 0, FinalValuePower 2.2, DepthMIPSamplingOffset 3.30 (README says 3.15).

**Strengths for this game.** The whole scaffold is production-hardened, MIT, and every port (Bevy, Skyrim SSGI, Luma)
keeps it: mips, noise, edges, denoise, minS, pixel snapping, fp32/fp16 bias. **Weaknesses.** Horizon integration treats
every leaf and fence as an infinitely thick wall (README "Known limitations": "depth buffer represents viewspace
heightmap, not actual geometry, causing artifacts with thin features"); archived; the D3D-only constants helper.
**Licence:** MIT, code usable with notice.

### A.2 GTAO, Jimenez, Wu, Pesce, Jarabo 2016 (Activision technical report)

Read from `PracticalRealtimeStrategiesTRfinal.pdf` (https://www.activision.com/cdn/research/PracticalRealtimeStrategiesTRfinal.pdf).

- **Integral.** `A(x) = 1/pi * int_0^pi int_{theta1}^{theta2} cos(theta - gamma)+ |sin theta| dtheta dphi` (eq. 5),
  horizons around the **view vector** (following Timonen), `gamma` = angle between the projected normal and the
  view vector; the inner integral is analytic:
  `a = 1/4 (-cos(2 theta1 - gamma) + cos(gamma) + 2 theta1 sin(gamma)) + 1/4 (-cos(2 theta2 - gamma) + cos(gamma) + 2 theta2 sin(gamma))` (eq. 7),
  multiplied by `||n_x||` (the projected-normal length) per slice (eq. 8). Horizon search eq. 6:
  `theta1 = arccos(max_s <omega_s, omega_o>+)`. "2 cos and 1 sin, plus three acos" per slice; "memory bounded".
- **Attenuation.** "we do not consider any attenuation function ... In order to minimize artifacts we employ a
  conservative attenuation strategy ... linear blending from 1 to 0 from a given, large enough distance, to the
  maximum search radius" (section 4.1). This is XeGTAO's `FalloffRange`.
- **Thickness heuristic (eq. 9).** "thin features tend to cast too much occlusion ... assumption that the thickness of
  an object is similar to their screen space size": during the search, `theta = max(theta_s, theta)` if
  `cos(theta_s) >= cos(theta_{s-1})`, else `blend(theta_{s-1}, theta_s)` with an exponential moving average. "does
  not bias the occlusion results for simple corners (e.g. walls)". Figure 4 shows leaves/branches.
- **Sampling and filtering.** Half resolution, "one direction per pixel", 4x4 spatial neighbourhood reconstructed with
  a bilateral filter (uniform weights per the Bevy header quoting the paper), "6 different rotations" reprojected
  with an exponential accumulation buffer: 4x4x6 = 96 effective directions. **0.5 ms on PS4 at 1080p.**
- **Multi-bounce (section 5, eq. 10).** `G(A, rho) = a A^3 - b A^2 + c A` with
  `a = 2.0404 rho - 0.3324`, `b = 4.7951 rho - 0.6417`, `c = 2.7552 rho + 0.6903`, fitted on seven albedos against
  three-bounce references. This is where the "multi-bounce approximation" comes from; XeGTAO does not carry it,
  Unity's port and UE4 do.
- **GTSO** (section 6): specular occlusion from the bent-normal cone and the GGX lobe; needs roughness. Not applicable
  (no PBR specular in this game).

**Licence:** paper only; the formulas are published mathematics.

### A.3 Screen Space Indirect Lighting with Visibility Bitmask, Therrien, Levesque, Gilet 2023

Read from arXiv 2301.11376 (Vis Comput 2022) and the code post https://cdrinmatane.github.io/posts/ssaovb-code/.

- **Method.** "replaces the two horizon angles by a bit field representing the binary state (occluded / un-occluded)
  of N sectors uniformly distributed around the hemisphere slice." Per sample: front angle `theta_f` at the sample,
  back angle `theta_b` at `sample - V * t` (constant thickness `t` along the view vector), both converted "from cosine
  space to angular space", shifted to the normal-centred hemisphere, and the bits between them set (`UpdateSectors`:
  `start = minHorizon * N`, `count = ceil((max - min) * N)`); AO = `1 - countbits(mask)/N`. Paper: "we used the
  round criterion which requires the sector to be half covered" (the post shows ceil/round/floor as choices; Bevy uses
  ceil, iMMERSE MXAO ceil).
- **Falloff.** "the UpdateHorizon() function from GTAO is not needed anymore, because we don't need to apply any
  falloff! The constant thickness and the bitmask is enough" (post). Paper: fixed world-space thickness
  "can cause an over-attenuation of occlusion for objects far away from the camera, so we give the option to
  increase t linearly over the distance"; "Finding an efficient heuristic to estimate an accurate thickness for each
  sample ... remains a difficult problem we leave for future work". Figure 6: fixed thickness "causes light leaks at
  depth discontinuities" where GTAO with falloff does not.
- **Cosine weighting.** "Note that we do not take the cosine weight into account in this case" (post).
- **Sectors.** 32 ("just crossed the threshold where the artifacts became almost invisible", fits a uint); 128 sectors
  cost 5-10% more.
- **Cost** (Table 1, RTX 2080, 1080p, one jittered slice per pixel, 32 sectors): radius 0.8 / 8 samples 0.49 ms GTAO
  vs 0.51 ms bitmask; radius 1 / 16 samples 0.95 vs 0.97; "about 15 GPU instructions per sample"; denoise a constant
  0.3 ms. Unreal marketplace VBAO (ARK.KRA) reports 0.45-1.60 ms at 1080p on RTX 2060 for its four quality tiers and
  "relies entirely on the engine built in TAA" for denoising (vendor claim, forum listing).
- **GI.** The same bitmask gates radiance fetches from the lit buffer per unoccluded sector; "we also sample the HDR
  light buffer and the screen space normal buffer for every sample taken".

**Strengths.** Exactly the failure mode of this game's geometry: Figure 11 (light behind bars) is a fence. Cheap.
**Weaknesses.** No cosine term, constant thickness is a global guess, leaks at discontinuities.
**Licence:** paper; the post states none, the Unity HDRP code it patches is Unity's. Ideas only; implement from the
paper's Algorithm 1 (which is what Bevy did, under MIT).

### A.4 Bevy `bevy_pbr/src/ssao` (MIT OR Apache-2.0)

Files read: `ssao.wesl`, `preprocess_depth.wesl`, `spatial_denoise.wesl`, `mod.rs` (main branch, 2026-09-15).

- Header: "Visibility Bitmask Ambient Occlusion (VBAO) ... heavily based on XeGTAO v1.30 ... and
  https://cdrinmatane.github.io/posts/ssaovb-code/ ... SSRT3".
- **Noise:** Hilbert LUT (64x64 `R16Uint`) + `288u * (frame_count % 64u)` under `TEMPORAL_JITTER`, R2 as XeGTAO.
- **Main pass:** XeGTAO's slice/step loop with `s *= s`, mip `clamp(log2(len(sample*viewport)) - 3.3, 0, 5)`,
  **no minS and no pixel snapping** (dropped from XeGTAO), depth read through a **linear** sampler on the mip chain
  (XeGTAO warns this interpolates between texels), positions through `view_from_clip` (raw NDC depth in the mips,
  so reversed-Z is generic). `processSample`: `delta_back = delta - view_vec * thickness`,
  `front_back = fast_acos(dot(normalize(.), view_vec))`, `saturate(fma(dir, -angles/PI, n))`, then `insertBits`
  from `u32(min*N)` for `ceil((max-min)*N)` bits with `N = 2 * SAMPLES_PER_SLICE_SIDE`... note: Bevy passes
  `samples_per_slice_side * 2.0` as the sector count, i.e. **the sector count equals the sample count per slice**
  (4 to 18), not 32 [read from `ssao.wesl`, `processSample(..., samples_per_slice_side * 2.0, &bitmask)`]. Visibility
  `1 - occluded/(slices * 2 * samples_per_side)`, clamp to `[0.03, 1]`. No cosine weighting, no falloff, no final
  power. [Inference] With only 4-18 sectors the mask is coarse; the paper found 32 to be the banding threshold.
- **Thickness:** `constant_object_thickness` default 0.25 ("how far behind an object a ray of light needs to be in
  order to pass behind it").
- **Presets** (`mod.rs`): Low 1x2 (4 samples), Medium 2x2 (8), High 3x3 (18), Ultra 9x3 (54), Custom.
- **Edges/denoise:** XeGTAO edges with `bias 0.25`, `scale = z * 0.011`, packed `pack4x8unorm`; one 3x3 pass, one
  pixel per thread, centre 1.2, diagonals 0.425, no leak term. Header: paper uses 4x4 uniform bilateral offset by
  +/- 1 pixel every other frame; XeGTAO 3x3 twice; Bevy 3x3 once.
- **Prefilter:** 5 mips with workgroup memory, credits SAO section 2.2, XeGTAO's weighted average.
- **Formats:** R16Float if storage-supported else R32Float for depth mips and both AO textures.
- Docs: "strongly recommended that you use SSAO in conjunction with TAA".
- **Open improvements** (bevy#19713, Elabajaba, 2025-06-18): acos-free evaluation (shadertoy 4cdfzf), occluder
  thickness heuristics (shadertoy 3clGWB + Bottosson), and GT-VBAO (Mirko Salm, shadertoy XXGSDd/Xc3yzs) which lists
  four VBAO shortcomings: slice-local sample distribution ("pole concentration near the view vector and cosine falloff
  toward horizons", fixed by CDF remapping of horizon angles), point-sample treatment (quantised arc lengths),
  perspective distortion of slice directions, and cosine-weighted hemisphere support (three options; option 3 "direct
  CDF importance sampling using invertible approximation with single random number"). Salm's own description: GT-VBAO
  "matches the results of a brute force ray-marcher" sharing the same depth-sample and thickness assumptions, and
  "supports both uniform hemisphere weighting and cosine weighted" (X post 1833211198009184650, via search snippet).
  [Uncertain] internals; the Shadertoy pages were not readable from here.
- **Bottosson's thickness thread** (Bluesky, read via the public API): constant thickness "is impossible to tweak so
  that it works for varying sizes of occluders" and "occlusion is binary, so you get quite sharp artifacts when a gap
  opens up behind an object"; his heuristic: "estimate if subsequent samples along a horizon are a part of the same
  surface or not, and using that estimate the width of the surface. I then simply use the width as the thickness
  estimate. On top of that I randomly scale the thickness to reduce artifacts." Code: shadertoy wcBGRz, 3clGWB, wcfGWB
  (licence unstated).

**Licence:** MIT OR Apache-2.0; code usable with notices. Its authors' own header names its bases, so a port from Bevy
inherits a clean chain.

### A.5 MXAO: iMMERSE (proprietary) and qUINT (all rights reserved)

Read: `MartysMods_MXAO.fx` header and structure (study only), `qUINT_mxao.fx` header, the martysmods guide.

- **Licence.** iMMERSE: "Copyright (c) Pascal Gilcher. All rights reserved. Unauthorized copying of this file, via any
  medium is strictly prohibited. Proprietary and confidential" (file header). qUINT: "Copyright (c) Pascal Gilcher /
  Marty McFly. All rights reserved." (`qUINT_mxao.fx` header); the repository has no LICENSE file (raw fetch 404).
  **Both: ideas only, nothing reproduced.**
- **What it does** (from the file's structure and UI text, and the guide): four `MXAO_AO_TYPE`s, "0: GTAO (high
  contrast, fast), 1: Solid Angle (smoother, fastest), 2: Visibility Bitmask (DX11+ only, highest quality, slower),
  3: Visibility Bitmask w/ Solid Angle"; seven sample presets as slice/step pairs; a shading rate (full, half, quarter
  = checkerboard skip by `FRAMECOUNT`); deinterleaved 2x2 / 4x4 / 5x5 tiles; a 4096x64 "temporal blue noise" seed
  texture indexed by `FRAMECOUNT % 64`; `s = ((i + jitter)/n)^2` step distribution; thickness
  `T = log(1 + r) * 0.3333` ("arbitrary thickness that looks good relative to sample radius"); horizon initialised at
  `cos(normal_angle -/+ pi/2)` with the comment "much better falloff than original GTAO :)"; in bitmask mode a
  half-occlusion `ceil(saturate(h.y - h.x) * 32)` and a comment "this almost perfectly approximates inverse transform
  sampling for cosine lobe"; a guided-filter-style spatial filter (`(mv.w - mv.x*mv.z) / (mv.y - mv.x^2)`), and its own
  temporal blend/accumulate passes. **iMMERSE MXAO has no indirect lighting**; the older qUINT MXAO had
  `MXAO_ENABLE_IL` ("Will cause a major fps hit").
- **Independently published counterparts of every idea worth keeping:**
  - horizon initialised at the hemisphere edge `cos(n +/- pi/2)`: XeGTAO `lowHorizonCos` (MIT);
  - slice weight = projected-normal length: GTAO eq. 8;
  - "solid angle" AO = the uniform-weighted formulation: GTAO paper Appendix A ("uniform ... instead of
    cosine-weighted") and Unity's `IntegrateArc_UniformWeight = 1 - cos(h)`;
  - cosine importance mapping of sectors: derivable from first principles (section C.4) and the same trick appears in
    the Skyrim SSGI (GPL, `smoothstep(0,1,(angle+n)/pi+0.5)` "using smoothstep for cos");
  - deinterleaving: ASSAO (Intel MIT, in Godot) and CACAO (MIT);
  - 64-frame blue-noise seed texture: EA FastNoise (BSD-3);
  - checkerboard shading rate: XeGTAO FAQ ("half by half or checkerboard").
- **Strength** is the product shape: integration as a switch, not a fork. Adopted as a shape.

### A.6 Alchemy AO, McGuire, Osman, Bukowski, Hennessy, HPG 2011

Read from `VV11AlchemyAO.pdf`.

- Estimator (eq. 10): `A = max(0, 1 - (2 sigma / s) * sum_i max(0, v_i . n + z_C beta) / (v_i . v_i + eps))^k`,
  `r = 0.5 m, sigma = 1, k = 1, beta = 1e-4 m`; falloff `g(t) = u t max(u,t)^-2` chosen because it "resembles the
  shifted hyperbola ... artistically desirable in StarCraft II" and cancels terms; samples on a screen disk, per-pixel
  XOR-hash rotation; treats the depth buffer as a thin shell (Loos and Sloan) rather than an infinite volume, which is
  why it shows fewer halos than volumetric obscurance. 12 spp at 720p in 3 ms on GeForce 580; two 13-tap 1D
  cross-bilateral passes; 4.5 ms on Xbox 360. Self-occlusion needs the bias `beta`.
- **For this game.** [Inference] Independent point samples extract one bit of information per fetch; the slice
  methods extract a horizon or a sector range per fetch, so at 8-18 fetches Alchemy is noisier for the same cost.
  Its lasting ideas are the thin-shell assumption and the `beta` bias, both subsumed by GTAO/VBAO.
- **Licence:** paper; the G3D reference code is BSD (search result, casual-effects G3D; the exact BSD variant is
  [Uncertain]).

### A.7 Scalable Ambient Obscurance, McGuire, Mara, Luebke, HPG 2012

Read from `McGuire12SAO.pdf`.

- Same estimator as Alchemy (eq. 1), new structure: a camera-space z mip chain (rotated-grid subsampling was the best
  filter in their Table 1; hardware mip averaging was not), mip per sample `m_i = floor(log2(h'_i / q'))` (eq. 9) with
  `q'` the screen-radius increment, spiral samples `alpha_i = (i+0.5)/s`, `theta_i = 2 pi alpha_i tau + phi`
  (`tau = 7` for `s = 9`), face normals reconstructed from depth derivatives within 0.2 degrees, a 2x2 reconstruction
  then two wide 1D bilateral passes, `z_f = -inf` for precision. **Cost** (Table 3, GTX 680): 1080p total 1.59 ms with a
  192 px guard band (z mips 0.24, sparse AO 0.78, blur 0.41); 2.26 ms on GTX 580 vs 16.1 ms for the unhierarchical
  version (7.1x). Section 2.2 is what XeGTAO/Bevy cite for their mip chain; XeGTAO replaced rotated-grid subsampling by
  a depth-weighted average.
- **Licence:** paper; G3D code BSD [Uncertain variant]. Contributes the mip idea, already in XeGTAO.

### A.8 "Low-sample GTAO + spatial denoise" as a technique

Every production implementation reviewed runs few samples and leans on a spatial pass plus a temporal accumulator:
GTAO paper 1 direction/pixel + 4x4 bilateral + 6-frame reprojection (A.2); XeGTAO 2x2 or 3x3 + one 3x3 pass with TAA
(A.1, `XeGTAO.h` v1.21 note "1-pass new ... enough when TAA enabled"); Bevy 2x2 + one 3x3 pass with TAA (A.4);
Therrien's benchmarks "one hemisphere slice per pixel jittered over multiple frames" + a 0.3 ms denoise (A.3); Unreal
`r.GTAO.NumAngles=2` at half resolution with spatial and temporal filters (artiliada.github.io "State of GTAO in
Unreal"; the spatial filter was broken in 4.26/4.27); the UE marketplace VBAO with the engine TAA only (A.3). Unity's
port uses an 8-tap separable cross-bilateral plus an AABB-clamped private history (A.10). openmw-ssao a 12-tap Poisson
depth-weighted blur plus a private history (A.9). Godot/ASSAO 3-12 taps deinterleaved plus a 4-tap edge blur x N
(A.11). The choice inside this family is only: which per-slice integration, how many denoise taps, and whether the
accumulator is the engine TAA or a private history. Here the accumulator is fixed by decision (TAA).

### A.9 openmw-ssao (zesterer, no licence: ideas only)

Read: `shaders/ssao.omwfx` in full.

- Point-sample SSAO: up to `cfg_samples` (default 30, max 400) samples on a spiral (`t += 3.88` per sample, radius
  `0.01 + fract(t*22.8)` scaled by `cfg_radius / (10 + depth^0.75 * 0.2)`), per-pixel hash rotation plus
  `fract(simulationTime * t)` per frame when temporal filtering is on; weight per sample from a depth-difference
  ignore term (`cfg_depth_compensation`, "Lower values produce 'occlusion halos'") and a depth/normal factor mix.
- **Temporal reprojection:** history in `ao_next` (rgb = ao, marker.xy, a = 1/depth); the **marker** is a hash of the
  world position (`dot(sin(wpos*0.09), 1), dot(sin(wpos*0.13), 1)`) stored beside the AO and compared after
  reprojection (`blend = temporal^0.1 / (1 + max(0, |last.yz - marker|) * inv_depth * 1e5)`); the sample count is
  reduced where the history is trusted (`samples = cfg_samples * max(1 - blend*0.9, 0)`); change-based rejection
  `reject_changed` when the new AO is far above the old.
- **Sky/water/fog/hands:** AO = 1 for `depth > far*0.99`, for pixels on the other side of the water plane than the
  camera; the final combine mixes AO out by fog coverage; hands: `if (!cfg_enable_hands && depth < 40) ssao =
  smoothstep(1, ssao, min(depth/60, 1))`.
- **Blur:** 12-tap Poisson, `weight = exp2(-|d - d_s|/d * 50)`.
- **For this game.** Every temporal trick exists because it has no engine TAA to lean on. Sky, water-plane, fog and
  hands handling are the useful ideas (all trivial and re-derivable; section C.9).

### A.10 Unity Ground Truth Ambient Occlusion (MaxwellGengYF, no licence: ideas only)

Read: `GTAO_Common.cginc`, `GTAO_Pass.cginc`.

- GTAO with the cosine arc (`IntegrateArc_CosWeight`), a **thickness blend** in the horizon loop:
  `h = (H > h) ? lerp(H, h, falloff) : lerp(H, h, thickness)` with `falloff = saturate(d^2 * 2/r^2)` (the paper's
  eq. 9 as a lerp), bent normal from the mean horizon, noise = interleaved gradient noise plus a per-pixel
  `0.25 * ((y - x) & 3)` step offset and per-frame `_AO_TemporalOffsets/_AO_TemporalDirections`; an 8-radius separable
  cross-bilateral (`exp2(-r^2 * falloff - dz^2)`); a **temporal filter** that clamps the reprojected history to a
  neighbourhood AABB and blends with `weight = saturate(response * (1 - 8*|velocity|))`, response up to 0.98; the
  paper's multi-bounce with rounded coefficients (`A = 2*albedo - 0.33, B = -4.8*albedo + 0.64, C = 2.75*albedo + 0.69`,
  `max(AO, ((AO*A + B)*AO + C)*AO)`), and GTSO reflection occlusion.
- **For this game.** The private temporal filter is the pattern rejected in C.7; the multi-bounce form is the paper's.

### A.11 Engines available locally (`~/Projekte/ReScaleFrame/references`)

| Engine | Licence (file) | AO | What was read |
|---|---|---|---|
| Unreal (`UnrealEngine`, `UnrealEngine-4.18.3`) | UE EULA (`LICENSE.md`) | The local checkouts are **4.18.3** (`Engine/Build/Build.version`): `PostProcessAmbientOcclusion.usf` is the old SSAO, **no GTAO source locally** (0 `GTAO` hits). GTAO exists since 4.24: `r.AmbientOcclusion.Method=1`, `r.GTAO.NumAngles=2`, `r.GTAO.Downsample=1`, `r.GTAO.SpatialFilter`, `r.GTAO.TemporalFilter`, `r.GTAO.ThicknessBlend`, `r.GTAO.FalloffEnd` (artiliada.github.io/2024/12/27/GTAO.html). | reference only; EULA, no code reuse |
| Godot (`godot`) | MIT | `servers/rendering/renderer_rd/shaders/effects/ssao*.glsl`: Intel ASSAO (2016 MIT header, "2020-12-05: clayjohn: convert to Vulkan and Godot"). Deinterleaved 4 passes, 3/5/12 taps x2 per preset, depth mips with `SSAO_DEPTH_MIPS_GLOBAL_OFFSET -4.3`, 2-bit packed edges, **haloing reduction** weight `clamp(-dz * neg_inv_radius + 2, 0, 1)`, **normal-based edges** `clamp(dot(n, n_neighbour) + 0.5, 0, 1)` at quality >= 2, detail AO from the 4 neighbours, adaptive importance map at level 3, blur "smart" (4-tap edge-weighted, centre 0.5) and "wide" (+/-2 px). SSIL (PR #51206): "ASSAO-like ... does not require Temporal Super Sampling", 0.3-1.0 ms on the author's hardware. | usable ideas and code (MIT) |
| Donut (`Donut`, `Streamline_Sample/donut`) | MIT (NVIDIA) | `ssao_compute_cs.hlsl`: deinterleaved 4x4, 16 spiral samples, 4x4 blue-noise rotation, `saturate(NdotV - bias) * saturate(1 - d^2/r^2)`, groupshared 24x24 blur; optional SH "directional occlusion". | usable (MIT); a point-sample baseline only |
| Skyrim CS "Screen Space GI" (`gi.cs.hlsl`, 405 lines) | GPL-3.0 (`COPYING`) | XeGTAO-derived **bitmask** (32 sectors, `countbits * 0.03125`), `smoothstep(0,1,(angle+n)/pi+0.5)` in place of the cosine mapping, AO radius gating `s < AORadius`, constant `Thickness`, GI back-thickness 300 units, STBN noise "from https://github.com/electronicarts/fastnoise 128x128x64" indexed by `FrameIndex % 64`, a **normal flip** `if (dot(viewVec, pixCenterPos) > 0) viewspaceNormal = -viewspaceNormal` ("flip foliage normal"), half/quarter-res mip floors, `DepthFade`, `AOPower`, and a private SVGF-style temporal denoiser (5-tap clamp on the history) with a radiance-disocclusion pass. | ideas only |
| Fallout 4 CS `ScreenSpaceGI/XeGTAO/*` | GPL-3.0 | the same family | ideas only |
| FidelityFX CACAO | MIT (`docs/license.md`; gpuopen.com "Open source, MIT license") | No CACAO source in the local SDK copy (only sample images); `Luma-Framework/.../ffx_cacao.h` shows 5 quality levels and `adaptiveQualityLimit`. gpuopen manual: ASSAO adaptation, 2x2 deinterleave into four quarter-res passes, importance map at HIGHEST, 4 blur passes default (2 at LOWEST), bilateral upsample. | usable (MIT); ASSAO family, superseded by GTAO for quality |
| Luma-Framework | custom MIT | ships XeGTAO ports per game (`Luma_*_XeGTAO.hlsl`) | a second MIT port to cross-check GL-style projections against [not read in depth] |

---

## B. Comparison

Scale: ++ best, + good, o neutral, - weak, -- worst, for **this** game (voxel terrain, alpha-tested foliage, fences,
1-block steps, long distances, TAA accumulator, Arc 140V budget). Cost columns are the sources' own numbers at 1080p;
"foliage" = thin geometry behaviour (halos, over-darkening behind leaves/fences).

| Source | Accuracy vs ground truth | Thin geometry | Noise at 8-18 fetches | Convergence through TAA | Cost at 1080p (source) | Edge/halo handling | Licence |
|---|---|---|---|---|---|---|---|
| XeGTAO horizon GTAO | + (paper matches MC reference; the 0.05 slope fudge and `FinalValuePower` are auto-tuned deviations) | -- (infinitely thick occluders; heuristic off by default) | + (analytic arc per slice) | ++ (designed for it: R2 64-frame index, one denoise pass) | 0.56 ms RTX 2060 High, 2.39 ms i7-1195G7 High | + (2-bit slope-aware depth edges, leak, symmetric) | MIT |
| GTAO paper 2016 | + | - (eq. 9 EMA heuristic helps leaves/branches, "conservative") | o (1 dir/pixel, needs 4x4 + 6 frames) | ++ (that is its design) | 0.5 ms PS4 half-res | o (bilateral 4x4) | paper |
| VBAO (Therrien) | + (Fig. 10 closer to RT reference than GTAO at wide radius; no cosine term) | ++ (light passes behind bars, Fig. 11; leaks at discontinuities, Fig. 6) | + (same fetches as GTAO; sectors quantise) | ++ (paper's benchmarks are 1 jittered slice) | GTAO + 0.01-0.02 ms, RTX 2080 | o (needs the GTAO edges; no falloff) | paper (code post unlicensed) |
| Bevy VBAO | o (sector count = sample count, 4-18; no cosine; linear depth sampler) | + (constant 0.25) | o | ++ | not published; XeGTAO-class | + (XeGTAO edges, 3x3 once) | MIT/Apache-2 |
| GT-VBAO (Salm) [Uncertain] | ++ (claims to match a brute-force ray marcher, uniform and cosine) | ++ (with Bottosson's width heuristic) | ? | ? | ? | ? | unlicensed Shadertoy |
| MXAO (iMMERSE) | + (bitmask + cosine lobe mode) | ++ (bitmask modes) | + | private history | not published | o | proprietary |
| Alchemy 2011 | - (aesthetic falloff, not radiometric) | o (thin shell) | -- (1 bit per fetch; 12 spp + 2x13 taps) | o | 3 ms GTX 580, 12 spp 720p | - (bias `beta`, halos at silhouettes) | paper (BSD code) |
| SAO 2012 | - (same estimator) | o | -- | o | 1.59 ms GTX 680 (9 spp + wide blur) | o | paper (BSD code) |
| openmw-ssao | - (heuristic point AO) | o | -- (30 spp when untrusted) | private history + marker | not published | + (depth compensation, fog, water, hands) | none |
| Unity GTAO | + | - (thickness lerp = paper eq. 9) | + | private AABB-clamped history | not published | o (8-tap separable) | none |
| Godot/ASSAO, CACAO | o (obscurance with `shadow_power`) | - (haloing-reduction weight only) | + (deinterleaved 6-24 taps, adaptive) | o (no temporal design; SSIL "does not require TSS") | Godot SSIL 0.3-1.0 ms; CACAO n/a | ++ (depth + normal edges, detail AO) | MIT |
| Skyrim SSGI (bitmask) | + | ++ | + | private SVGF-style history | not published | + (XeGTAO edges) | GPL |
| Vanilla VS SSAO | -- (kernel AO with a 0.5-0.7 floor and x1.4 boost) | - (leaves hack: skip near samples, normal test) | - (20-24 spp at half res) | - (screen-locked Bayer; the override rotates it) | not measured here | - (11-tap separable depth blur, min-of-2-rows) | game |

[Inference] Reading the table: the only integration that addresses the dominant geometry (thin alpha-tested foliage,
fences) is the visibility bitmask; the only production scaffold with TAA-first design and a permissive licence is
XeGTAO (and its Bevy port); everything point-sample based is dominated on noise per fetch. What the bitmask lacks
(cosine weighting, a thickness model better than one constant, halo control at discontinuities) is exactly what
GT-VBAO, Bottosson and the GTAO paper's falloff supply as ideas.

---

## C. The combined design

Pipeline: `prefilter (depth -> 5 mips) -> main (bitmask AO + edges) -> denoise (3x3) -> compose (multiply into
Primary colour 0 before the resolve)`. Components, their source, and why they beat the alternatives.

### C.1 Depth prefilter and mip selection - XeGTAO (MIT), constants from the note

- Input: Primary `D32_SFLOAT`, linearised with the note's GL derivation (`DepthUnpackConsts = (-B/2, (1-A)/2)`,
  `tan = 1/P[0][0], 1/P[1][1]`), **not** `gPosition`: at 200-500 blocks an fp16 position has 0.125-0.5 block steps,
  the D32 depth linearised in fp32 does not (note, step 2; XeGTAO fp16 caveats in the README).
- Working depth R32F, 5 mips, XeGTAO's `DepthMIPFilter` (weighted mean, `depthRangeScaleFactor 0.75`), one dispatch
  with `shared` scratch (section 0 correction; Bevy does the same in WGSL). Point sampler, NEAREST mip (XeGTAO's warning
  about linear samplers; Bevy's linear sampler is the one thing not to copy).
- Mip per sample `clamp(log2(len_px) - 3.30, 0, 4)` (XeGTAO/Bevy). SAO's rotated-grid subsampling is not needed: the
  weighted average is what XeGTAO tuned for temporal stability ("temporal stability is the first affected",
  `XeGTAO.h:112` comment).
- Why not Godot/CACAO deinterleaving: [Inference] it optimises cache behaviour for point-sample kernels; XeGTAO's README
  states the deinterleaved approach is "unsuitable due to GTAO's linear sampling pattern constraints".

### C.2 Slice and sample distribution, counts per preset - XeGTAO (MIT)

- Per slice `phi = (slice + noise.x)/N * pi`; per step R1 `stepNoise`, `s = t^2 + minS` with `minS = 1.3/screenRadius`
  and pixel-centre snapping (XeGTAO; Bevy dropped both and its issue tracker lists the resulting artefacts under
  "point sample treatment" [Inference on causality]). Two fetches per step (both slice sides).
- Presets (slices x steps per side; fetches): Low 1x2 (4), Medium 2x2 (8), High 3x3 (18), Ultra 9x3 (54) - the
  XeGTAO/Bevy table, kept identical so the published cost ratios (High : Medium : Low = 1 : 2/3 : 4/9) apply.
- Effect radius in blocks: start 0.75 with `RadiusMultiplier 1.457` folded in (XeGTAO's auto-tuned screen-space bias
  compensation); the paper's VBAO benchmarks used radius 0.8-1 (A.3). Tune in D.

### C.3 Per-slice integration - visibility bitmask (Therrien 2023 Algorithm 1, re-implemented; Bevy MIT as the reference port), switchable to XeGTAO's analytic horizon

- 32 sectors in one `uint` (paper: the banding threshold, and 128 costs 5-10% more). Not Bevy's "sectors = sample
  count".
- Front/back angles from the sample and `sample - V * t` (paper); bits set with the **round** criterion (paper's
  choice; Bevy/MXAO use ceil, which over-occludes by up to one sector per sample [Inference]).
- Visibility = `1 - popcount/32` per slice (paper), averaged over slices, weighted by the projected-normal length
  `||n_x||` per slice (GTAO eq. 8; XeGTAO `projectedNormalVecLength`) so grazing slices count less - MXAO's
  "slice weight" is this same term.
- **Switch** (specialization constant): `INTEGRATION = BITMASK_COS | BITMASK_UNIFORM | HORIZON_GTAO`. The third is
  XeGTAO's `iarc` code path unchanged, so the foliage comparison in D is apples to apples on the same fetches.

### C.4 Cosine weighting of the bitmask - derived here; the idea is GT-VBAO's (Salm) and the GTAO paper's eq. 5

The paper's bitmask counts sectors uniformly in angle ("we do not take the cosine weight into account"); ground-truth
AO is cosine-weighted (GTAO eq. 5). Two published routes: weight each unoccluded sector by its cosine arc integral
(eq. 7 evaluated on the sector bounds; 32 evaluations per slice, or a 2D LUT over `(gamma, sector)`), or **distribute
the sector boundaries by the cosine CDF** so uniform bit counting is already cosine-weighted (GT-VBAO option 3
"direct CDF importance sampling", bevy#19713; MXAO's comment "approximates inverse transform sampling for cosine
lobe"; the Skyrim SSGI's `smoothstep` is a cheap approximation of it). First-principles form, no source code involved:
on the normal-centred slice `phi in [-pi/2, pi/2]`, `p(phi) = cos(phi)/2`, `CDF(phi) = (sin(phi) + 1)/2`, so

    sectorIndex(phi) = (1 + sin(phi)) / 2 * 32,   phi = theta - n   (theta from the view vector, n the projected-normal angle)
    sin(phi) = sin(theta) cos(n) - cos(theta) sin(n),  cos(theta) = dot(h, V),  sin(theta) = +/- sqrt(1 - cos^2)

which needs **no acos per sample** (the "acos-free slice evaluation" of bevy#19713 is presumably this or equivalent
[Uncertain]; `sin(n), cos(n)` come once per slice from `cosNorm` and the sign). What this ignores: the `|sin theta|`
Jacobian in eq. 5 (Salm's "pole concentration near the view vector"). [Inference] For sectors indexed around the
normal this is a second-order bias at moderate `n`; it is one of the things D quantifies against the analytic horizon
path, which has the exact weighting. The exact per-sector eq. 7 weighting stays as a third `INTEGRATION` value only if
the CDF mapping fails that comparison.

### C.5 Thickness model - Therrien (constant, distance-scaled) + Bottosson (randomised) + a game-specific class channel

- Base: constant thickness `t` in blocks, **increased linearly with view distance** (paper's own option against
  far over-attenuation; VS terrain is viewed at 100-500 blocks, so this is not optional here).
- **Randomised thickness** per sample: `t * (0.5 + noise)` (Bottosson: "randomly scale the thickness to reduce
  artifacts"; the bitmask is binary, so a single `t` makes a hard pop when a gap opens - with TAA as accumulator a
  dithered `t` converges to a soft transition [Inference]). Draw the scale from the same R2 pair (a third R2 dimension
  or `frac(noise.y * 7)`), so it advances per frame and TAA averages it.
- **Class channel (decided):** `gNormal.w` becomes a surface class. Today it is `1` for wind-mode (leaf) blocks and
  `0` otherwise (`chunkopaque.vsh:103`); the chunk-shader patch extends the thin class to plants, grass and cross-quad
  blocks, and the hand-view draws write their own class (C.9). Proposed encoding, chosen so vanilla SSAO's
  `leavesHack = w > 0` keeps its meaning when it runs: `0` solid, `1` thin foliage, `-1` hand view. Vanilla SSAO
  never sees the extended flag on grass: the chunk shader writes the extended class only under a define stamped while
  the new AO is active (the vanilla `w` stays byte-identical on OpenGL and with TAA off) [implementation note, not a
  source]. Particles write their alpha into `w` (`particlescube.fsh:47`), which reads as "thin" - correct for them.
  For a sample landing on a thin texel use `t_thin` (about 0.05 block) instead of `t`; for solid terrain keep `t`
  (default 0.5 block: a fence post is 0.125-0.25, a full block 1). Cost: one extra fetch of `gNormal.w` at the sample
  (RGBA16F, mip 0 only), or pack the class into the sign of mip 0 of the working depth so it rides along for free
  [Inference; the prefilter must preserve it and reconstruction must `abs()` it]. Not from any source; D.3 measures
  the thin-foliage scenes with and without the channel.
- Bottosson's same-surface width estimate is the principled version ("estimate if subsequent samples along a horizon
  are a part of the same surface ... use the width as the thickness estimate"). [Uncertain] its code was unreadable;
  it needs consecutive-sample bookkeeping per slice side, so it is a switchable `THICKNESS = CONST | DIST | RANDOM |
  WIDTH` variant, implemented from the description if the class channel does not close the gap.
- **Not adopted:** XeGTAO's `ThinOccluderCompensation` (off by default, small measured gain, and it is a falloff bias,
  not a thickness); the GTAO paper's eq. 9 EMA (the bitmask supersedes it for the same fetches, Fig. 10/11);
  MXAO's `log(1 + r)/3` (a proprietary constant with no derivation).

### C.6 Falloff - none inside the bitmask (Therrien), plus XeGTAO's radius gate and fades

- No per-sample distance falloff (paper: "we don't need to apply any falloff"). Samples with `s > 1` are outside the
  radius by construction of the step distribution; the Skyrim SSGI's `s < AORadius` gate is the same thing.
- Keep XeGTAO's small-screen-radius fade (`visibility += saturate((10 - screenRadius)/100)*0.5`), and a far fade as
  vanilla's `distanceFade = clamp(1.2 - z/250, 0, 1)` (game-specific numbers, `ssao.fsh:87`) so distant terrain is not
  darkened by sub-pixel geometry; the SSGI has the same `DepthFade`.
- Leaks at depth discontinuities (paper Fig. 6) are handled by the denoiser's edges, not by a falloff: the XeGTAO
  edge/leak logic already isolates silhouettes.

### C.7 Noise sequence and its advance - XeGTAO/Bevy (MIT): Hilbert LUT + R2, `NoiseIndex = FrameIndex`

- Default: 64x64 `R16_UINT` Hilbert LUT, `index += 288 * (NoiseIndex % 64)`, R2 (XeGTAO, Bevy). `NoiseIndex =
  OptimumTemporal.Frame.FrameIndex` while a temporal consumer owns the frame (the same clock the SSAO override already
  uses, `ClientPlatformWindows.cs:3466-3477`), 0 otherwise.
- **Commensurability check** (not in any source, [Inference]): the jitter phase count is 8 at native and 32 at render
  scale 0.5 (`OptimumTemporalMath.JitterPhaseCount`); both divide 64, so each jitter phase meets only 8 (or 2) distinct
  noise tiles. XeGTAO's own users run 8-phase Halton with 64 without complaint, but 32 phases with 2 patterns each is
  new territory. Keep the cycle length a parameter (`NOISE_CYCLE = 64 | 61`) and measure the periodic residual in D;
  61 is coprime with 8 and 32.
- Switchable alternative: a 128x128x64 spatiotemporal blue-noise texture generated with EA FastNoise (BSD-3-Clause;
  the Skyrim SSGI uses exactly that shape, indexed `pix % 128, frame % 64`). NVIDIA's STBN SDK is **not** usable
  (its `License.txt` is a "Non-Commercial Use License"). XeGTAO's README recorded why plain 2D blue noise + temporal
  offsets failed; STBN is the designed answer to that, so it is worth one measurement, not the default.
- Rejected: Owen-scrambled Sobol per pixel - no reviewed AO ships it; per-pixel independent scrambling is white in
  space, which the 3x3 denoise cannot average, while Hilbert+R2 is low-discrepancy across neighbours by construction.
- TAA off: `NoiseIndex = 0`, two denoise passes (XeGTAO), but by owner decision vanilla SSAO runs instead; the code
  path stays for measurement only.

### C.8 Spatial denoise - XeGTAO (MIT) 3x3 edge-aware, one pass; ASSAO/Godot normal edges as an option

- One 3x3 pass with TAA (XeGTAO v1.21 note; Bevy), centre weight 1.2, 2-bit slope-aware depth edges, edge symmetry,
  the **leak** term (XeGTAO only; Bevy dropped it) - the leak is what keeps single-pixel-wide fence rails and grass
  blades from becoming isolated noisy pixels ("reduces both spatial and temporal aliasing").
- Optional multiplicative **normal-based edge factor** `clamp(dot(n_c, n_neighbour) + 0.5, 0, 1)` (ASSAO/Godot
  `SSAO_NORMAL_BASED_EDGES_DOT_THRESHOLD`, MIT). [Inference] At a 1-block step the depth edge already fires; at a convex
  block corner depth is continuous and only the normal flips, and AO on both faces is similar, so blurring across is
  mostly harmless. Measured, default off unless D shows bleeding along block edges.
- Not separable, not 5x5: XeGTAO's four `Gather`s cover the 3x3 for both AO and edges; a separable 5+5 is two passes
  and more bandwidth on an iGPU, and the GTAO paper's 4x4 is tied to its half-res + 6-frame design.
- Two passes when TAA is off (XeGTAO `DenoisePasses`), three ("soft") only for screenshots.

### C.9 Sky, far depth, hands, water/fog, foliage - vanilla + openmw ideas, re-derived

- **Sky:** depth == 1 (`>= 0.999999` as the resolve tests) writes AO 1 and skips the loop (vanilla `fragPos.x == 0`
  early-out; openmw `depth > far*0.99`).
- **Far:** C.6 fade; positions from D32 (C.1).
- **Hands (decided):** the hand view has its own projection (`docs/temporal-frame-contract.md` section 7.6, "Two
  views"); a sample offset computed with the world projection lands on the wrong texel for hand pixels, and vanilla
  ignores this (it projects with the world matrix too). The hand-view draws (the programs listed under "First-person
  hands, echo chamber" in contract section 6, which already reproject through `GetPrevProjection(Hand)`) are patched
  to write the hand class into `gNormal.w` (C.5); the AO pass writes visibility 1 on hand-class pixels and, as a
  sample, treats them as solid with the world reconstruction (a hand in front of a wall still occludes the wall
  approximately [Inference]). The lib/shader patch follows the patch procedure (Cecil member lists, extract, check).
  openmw's near-depth fade (`depth < 40` smoothstep) is the fallback only if the patch proves impossible.
- **Water/fog/OIT:** keep vanilla's modulation `AO_final = 1 - (1 - AO) * (1 - attenuate)` with
  `attenuate = gPosition.w + 0.75 * (1 - revealage)` (`ssao.fsh:76-82`, `:152`), applied in the **compose** pass, not
  in the AO pass, so the AO texture stays a pure visibility term for measurement; openmw's fog-coverage mix is the same
  idea.
- **Foliage:** alpha-test holes are real depth holes (section 0); the thin class drives `t_thin` (C.5); the SSGI's
  **normal flip** `if dot(V, p) > 0: n = -n` handles double-sided leaves whose stored normal faces away (re-derived; one
  compare).

### C.10 Normals and projection - G-buffer `gNormal`, the note's GL mapping

- `n = normalize(gNormal.xyz)`, mapped `(n.x, n.y, -n.z)` into XeGTAO's +Z-forward frame (note step 1; a mirror, and
  the bitmask only uses dot products and one cross product per slice whose sign is consistent under the same mirror
  applied to positions [Inference: verify with the note's step 7 frame check]). Flip toward the viewer (C.9).
- Class = `gNormal.w` (C.5): `> 0` thin (leaves, plants, grass, cross-quads, translucent particles), `< 0` hand view,
  `0` solid; entities write 0 (section 0). Sky writes zeros: unaffected because sky is skipped first.
- Constants block: the note's 96-byte layout in push constants.

### C.11 Output, tone and composition - XeGTAO formats; the AO value is the radiometric visibility, untouched (decided)

- AO: `R8_UNORM` where storage-supported else `RGBA8_UNORM` (note); edges `R8_UNORM`/`RGBA8` second channel. Full
  render resolution or half resolution + bilateral upsample (XeGTAO FAQ; the GTAO paper's production path) per preset
  (C.12).
- **Tone (decided: physically correct).** The composed value is the cosine-weighted visibility from C.3/C.4, scaled
  back from the `1.5` UNORM packing after the denoise. **No** vanilla floor (`max(occ, 0.5|0.7)`) and **no** `1.4x`
  boost. **No** `FinalValuePower`: XeGTAO's README states it "has no basis in physical light transfer, we found that
  auto-tune can use it to achieve better ground truth match" - a screen-space bias compensation tuned on Intel's
  training set, not a derivation, so it is off (`1.0`) and exists only as a measurement knob to reproduce XeGTAO's
  reference numbers. XeGTAO's `max(0.03, v)` clamp is kept (a pixel that is visible cannot have zero visibility; it also
  guards the packing). `RadiusMultiplier 1.457` is kept: it is XeGTAO's compensation of the screen-space radius bias
  *toward* the ray-traced reference ("allows us to use different value as compared to ground truth radius to counter
  inherent screen space biases", `XeGTAO.h:107`), and D.2 re-tunes it against this game's reference. The horizon
  path's `0.05` slope fudge stays only inside `HORIZON_GTAO` for parity with XeGTAO's numbers.
- **Multi-bounce (decided: not in the first version).** The GTAO paper's `G(A, rho)` (eq. 10) needs the surface
  albedo; the scene colour at this point is lit LDR radiance (`RGBA8`, no exposure path, contract section 7.5), so
  feeding it to the fit would be wrong. The compose pass keeps an explicit **albedo input hook** (`TONE = LINEAR |
  MULTIBOUNCE`, an optional albedo texture binding, `MULTIBOUNCE` refused when the binding is absent) so the generated
  PBR material stage can switch the fit on with a real albedo; the coefficients of eq. 10 are recorded in A.2.
- **Composition:** the existing `scene-ssao` multiply before the resolve, sampling the AO with nearest filtering at
  full resolution or through the bilateral upsample at half; drop the `SSAOLEVEL > 1` min-of-two-rows (it compensated
  the half-res upsample fudge); the water/fog/OIT attenuation of C.9 is applied here; never on glow (owner rule).

### C.12 Quality presets and expected cost

Arc 140V: 8 Xe2 cores at up to 1.95 GHz, ~4.2 TFLOPS FP32 peak (videocardz / cputronic figures; chipsandcheese
confirms 8 cores, 1.95 GHz, 8 MB L2, LPDDR5X). XeGTAO's iGPU figure is for the i7-1195G7 (96 EU Iris Xe, 2.39 ms High
at 1080p). [Inference] Xe2 has roughly twice that throughput and twice the L2, and this pass is bandwidth-bound
(GTAO paper, Therrien), so:

| Preset | Slices x steps (fetches) | Denoise | Resolution | Expected 1080p render-res cost | Target device |
|---|---|---|---|---|---|
| Handheld candidate A | 2x2 (8) | 1 pass | full render res | ~0.8-1.2 ms on Arc 140V [Inference]; bitmask +3-5% over horizon (Therrien's 15 instr/sample) | Arc 140V |
| Handheld candidate B | 3x3 (18) | 1 pass + bilateral upsample | half render res | ~0.4-0.6 ms [Inference: 18/8 of A's fetches on a quarter of the pixels, plus the upsample] | Arc 140V |
| Discrete | 3x3 (18) | 1 pass | full | ~0.6 ms RTX 2060-class (XeGTAO High), ~0.3 ms RTX 4070 [Inference] | dGPU |
| Screenshot | 9x3 (54) | 2 passes | full | ~3x Discrete | screenshots only |

**Handheld default (decided): chosen by the section D numbers between candidates A and B**, with no preference in
advance. The trade is more fetches per pixel at half resolution against fewer at full: candidate B has better
per-pixel convergence and a bilateral upsample that blurs across sub-pixel foliage; candidate A keeps single-pixel
grass and rails at their own resolution but with 8 fetches. D.2-D.5 decide (FLIP against the converged reference on the
foliage scenes, temporal standard deviation, cost). With an upscaler the render resolution is already 0.5-0.67 of the
display, which favours A [Inference]; it is still measured, not assumed.

### C.13 Variants kept switchable for measurement (specialization constants or macros)

`INTEGRATION = BITMASK_COS | BITMASK_UNIFORM | HORIZON_GTAO`; `SECTORS = 32` (fixed); `THICKNESS = CONST | DIST |
RANDOM | WIDTH`; `CLASS_CHANNEL = 0|1` (thin class read at the sample); `NOISE = HILBERT_R2 | STBN_FASTNOISE`,
`NOISE_CYCLE = 64 | 61`; `DENOISE_PASSES = 1|2|3`, `NORMAL_EDGES = 0|1`; `RESOLUTION = FULL | HALF_UPSAMPLE`;
`TONE = LINEAR | MULTIBOUNCE` (albedo hook, C.11) with `FINAL_POWER` as a measurement-only uniform (default 1.0).
Everything else is a uniform. Debug outputs (decided): the AO working term (pre-denoise, unscaled), the packed edges
and working-depth mip 0 are opt-in attachments of `OPTIMUM_PARITY_DUMP` and of the headless frame writer, so D reads
them from disk instead of instrumenting the shader. First version if the compute pass kind is not ready: the same
shaders as fragment passes (prefilter as four blits, main and denoise as fullscreen triangles; DiligentFX and the
ReShade port did this, note section 2) - the maths is identical, so nothing measured has to be redone.

### C.14 The owner-forwarded proposal, verified claim by claim

| # | Proposal | Verdict | Verified facts and reasons |
|---|---|---|---|
| 1a | XeGTAO's horizon search maths and its thickness heuristic | **Adopted (scaffold), rejected (heuristic)** | XeGTAO's horizon scaffold (slices, R1 steps, `s^2`, `minS`, mips, pixel snapping, edges, denoise, noise) is adopted (C.1, C.2, C.7, C.8), and its analytic horizon integral stays as the `HORIZON_GTAO` switch (C.3). Its thickness heuristic is **disabled in its own code** (`#else` branch "thicknessHeuristic is completely disabled", `XeGTAO.hlsli:496-506`; `ThinOccluderCompensation = 0`; README: auto-tune found only a small gain) and is a falloff bias along the view vector, not a thickness. The bitmask's explicit thickness replaces it (C.5). |
| 1b | A "lightweight multi-bounce ambient occlusion approximation" so crevices are not pitch black | **Rejected for the first version; albedo hook kept** | The fit is **not in XeGTAO** (0 hits for `multibounce|albedo` in `XeGTAO.hlsli`/`vaGTAO.hlsl`; the only `Albedo` in `XeGTAO.h:89` belongs to its RTAO reference tool). It is GTAO 2016 section 5 eq. 10, `G(A, rho)` with `a = 2.0404 rho - 0.3324, b = 4.7951 rho - 0.6417, c = 2.7552 rho + 0.6903`, and it needs the **albedo**; the scene colour here is lit LDR radiance, so applying the fit to it would be wrong (decided). The compose pass keeps `TONE = MULTIBOUNCE` behind an albedo binding for the PBR material stage (C.11). Crevices not going black is a property of the physically correct value itself (a visible pixel has `v >= 0.03`; no vanilla floor is needed to fake bounce). |
| 2a | MXAO's screen-space indirect lighting (colour-buffer bounce during the horizon search) | **Rejected** | MXAO is proprietary (header quoted in A.5). iMMERSE MXAO has **no IL**; only the old qUINT MXAO had `MXAO_ENABLE_IL` ("Will cause a major fps hit"). SSIL here would treat an LDR, already-lit `RGBA8` scene colour as radiance with no albedo split (contract section 7.5: "no HDR exposure path"), doubles the fetches (Therrien: the HDR light buffer and the normal buffer "for every sample taken"), and every shipped bitmask GI (Skyrim SSGI) carries a private temporal denoiser, which 3C rules out. Revisit after the ambient-term split and the PBR material stage. |
| 2b | MXAO's "normal-oriented sample distribution/weighting so flat voxel walls do not self-shadow" | **Adapted through published sources** | What MXAO has is the projected-normal slice weight (GTAO eq. 8, `||n_x||`) and the horizon initialised at the hemisphere edge `cos(n +/- pi/2)` (XeGTAO `lowHorizonCos`, MIT; MXAO's own comment calls it "much better falloff than original GTAO"); both are adopted from those sources (C.3). The bitmask itself is sector-indexed around the projected normal, so it is normal-oriented by construction. Flat-wall self-occlusion is handled by XeGTAO's depth bias (`viewspaceZ *= 0.99999`) and `minS` (never sample the centre pixel), plus the normal flip for back-facing foliage (C.9). Nothing is taken from MXAO's code. |
| 3A | Per-frame rotated directions from a 1D array of blue-noise textures or an Owen-scrambled Sobol sequence, cycling 8 or 16 frames | **Adapted** | Per-frame advance: yes, but with Hilbert+R2 and a 64-frame index (XeGTAO/Bevy), because XeGTAO's README records that tileable 2D blue noise with temporal offsets "caused overlaps which would often show as temporal artifacts". An 8- or 16-frame cycle equals or divides the 8-phase Halton jitter, so every phase would meet the same one or two patterns forever; 64 (or 61, coprime with 8 and 32, C.7) is kept. Spatiotemporal blue noise generated with EA FastNoise (BSD-3) is the measured alternative; NVIDIA's STBN SDK is under a non-commercial licence and excluded. Owen-scrambled Sobol rejected: no reviewed AO ships it, and per-pixel scrambling is white in space, which the 3x3 denoise cannot average (C.7). |
| 3B | Never feed raw AO into TAA; a separable cross-bilateral whose weight drops to zero past a depth threshold scaled by voxel size or past a few degrees of normal deviation | **Adapted** | Agreed on never composing raw AO: one XeGTAO 3x3 edge-aware pass runs before composition (C.8; XeGTAO v1.21 note and Bevy both find one pass sufficient with TAA). The separable form is rejected: two passes and more traffic on an iGPU, and XeGTAO's 3x3 with symmetric 2-bit edges and the leak term already stays sharp at 90-degree block edges (slope-adjusted depth test). Normal-angle rejection is adopted as the ASSAO/Godot optional factor `clamp(dot(n_c, n_n) + 0.5, 0, 1)` (MIT), default off pending D. A fixed voxel-size depth threshold is wrong for this geometry: XeGTAO's threshold is depth-relative (`0.011 * z`), which keeps a 1-block step an edge at 5 blocks and at 200 blocks alike. |
| 3C | Reproject the previous frame's AO with the velocity vectors and clamp the history with a tight 3x3 variance/neighbourhood box | **Rejected** | It is the second-history pattern the branch already ruled out (progress doc section 4; the note's openmw analysis): TAA is the accumulator and AO is composed before the resolve. The resolve already does the proposed clamp (3x3 YCoCg variance clip, `taa-resolve.fsh:134-135`) and a nearest-depth disocclusion test on the composed image, so voxel disocclusions are handled there. A private history would add a second reprojection, a second disocclusion test and a second ghosting source, and would then be accumulated again by DLSS/XeSS when those replace TAA (double temporal lag). Every source with a private history (Unity, openmw, Skyrim SSGI, MXAO) has one because it has no engine TAA to lean on; XeGTAO, Bevy and the UE VBAO product do not. If TAA-only convergence fails the numbers in D, the fix is a second denoise pass or half resolution, never a history. |

---

## D. Measurement plan

All runs with the implicit Vulkan layers off (MangoHud and the Lossless Scaling layer hook every Vulkan process on the test machine), renderer confirmed from the log, through
`scripts/dev/headless-capture.sh` (frames to disk, static camera or `.cam play`, `OPTIMUM_HEADLESS_FIXED_DT`).
**Inputs to every item (decided):** the AO working term, the packed edges and working-depth mip 0 are opt-in
outputs of `OPTIMUM_PARITY_DUMP` and of the headless frame writer (C.13), next to the existing attachments, so the
numbers below come from files, not from shader instrumentation.

1. **Frame and normals check** (note step 7): reconstructed view-space XY/Z from D32 against `gPosition` for pixels off
   sky and closer than 100 blocks, target < 0.1% relative error; debug views of normals, the class channel and edges
   (the hand-class pixels must be exactly the hand-view draws; the thin class must cover leaves, plants, grass and
   cross-quads and nothing else).
2. **Converged reference.** A numpy re-implementation of the bitmask and horizon integrals on captured depth + normals
   at 64 slices x 32 steps, no noise (the same height-field and thickness assumptions, so it is the algorithm's own
   ground truth; Salm's framing). Report PSNR and FLIP of (a) one noisy frame, (b) after denoise, (c) the TAA output
   after 128 static frames, against it - per `INTEGRATION`, `THICKNESS`, `TONE` variant, on three scenes: dense forest
   (leaves at 5-50 blocks), a fence with terrain behind it, a village with 1-block steps and long flat ground.
   Optional true ground truth: a voxel ray-cast AO from the save's block data (the world is voxels; this is the one
   game where real ground truth is cheap [Inference], but it is a separate tool).
3. **Thin-foliage behaviour.** Mean AO on the terrain behind the fence and under the canopy, per variant, versus (2).
   Over-darkening = ratio below 1 against the reference; halos = AO < 0.95 on ground pixels the reference leaves at 1.
   Run with `CLASS_CHANNEL = 0` and `1` on the same captures (decided): the channel stays only if the thin-foliage
   scenes move measurably toward the reference.
4. **Temporal stability.** Static camera, wind stilled, 128 frames: per-pixel temporal standard deviation of the
   resolved luminance over the centre crop and in labelled regions (leaves-far, grass, fence, flat ground, steps),
   vanilla SSAO+TAA as the baseline. Acceptance: no region worse than vanilla; `scripts/dev/taa-rejection.py` leaf-far
   rejection stays <= 1.5% (the 2026-09-11 resolve regression gate, `scripts/dev/taa-rejection.py`). Camera motion via `.cam play`: consecutive-frame diffs of the resolved
   image; a 60 fps `ffmpeg -f x11grab` capture for flicker (rule 10). Also log the resolve's mean `clipKeep` and mean
   `alpha` with AO on vs off (the XeGTAO README warning about TAA reading noise as detail, in numbers).
5. **Cost.** Per-pass GPU timestamps (prefilter, main, denoise, upsample, compose) at 1080p and 1440p render
   resolution on the Arc 140V and the RTX 4070, all presets, `INTEGRATION` and `RESOLUTION` variants; vanilla SSAO's
   three passes on the same frame as the baseline. Budget: the handheld preset <= 1.0 ms at 1080p on the 140V.
   **Handheld decision (decided to be made here):** candidates A (2x2 full render res) and B (3x3 half res + upsample)
   from C.12 are compared on (2) FLIP, (3), (4) and this item; the one that is within budget and closer to the reference
   on the foliage scenes becomes the handheld default. A tie on quality goes to the cheaper one.
6. **Noise/jitter commensurability.** At render scale 0.5 (32 phases) with `NOISE_CYCLE 64` vs `61`: the temporal
   power spectrum of a flat-ground pixel over 256 frames; a peak at period 32/64 is the failure.
7. **Cross-vendor:** NVIDIA, Intel ANV (the notebook's UHD), lavapipe as the deterministic CPU reference (note step 7);
   validation with `sync,best` clean.

**Results that change the design:** (2)/(3) horizon GTAO closer to the reference on the foliage scenes than the
bitmask -> default flips to `HORIZON_GTAO` and the bitmask stays a variant; `BITMASK_COS` not better than
`BITMASK_UNIFORM` -> drop the CDF mapping; the class channel without measurable gain in (3) -> the chunk-shader
extension is reverted and the channel keeps only the hand class; (4) worse than vanilla in any region -> second denoise
pass, then half resolution, never a history; (5) decides the handheld default between A and B; (6) shows a period ->
cycle 61; `NORMAL_EDGES` no gain in (3)/(4) -> removed; `RadiusMultiplier` re-tuned if (2) shows a systematic radius
bias against this game's reference.

---

## E. Questions and their decisions

### E.1 Decided (2026-09-15)

1. **Look (owner):** physically correct. No vanilla floor, no 1.4x contrast; the AO value is the radiometric
   visibility of the research, with XeGTAO's `FinalValuePower` off because XeGTAO's own README says it has no physical
   basis (C.11). Reason: the roadmap goes to generated PBR materials and then ray/path tracing, so the AO term has to
   be the thing those replace, not a look. The effect radius stays a tuning parameter measured against the converged
   reference (D.2), not against vanilla's strength.
2. **First-person hands:** the hand-view draws are patched to write the hand class into `gNormal.w` (lib/shader patch
   in scope, C.5/C.9). openmw's near-depth fade is the fallback only if the patch proves impossible.
3. **Class channel:** plants, grass and cross-quad blocks are flagged thin as well, through the small chunk-shader
   change (C.5). D.3 measures the thin-foliage scenes with and without it.
4. **Multi-bounce:** not in the first version; the scene colour is lit radiance, not albedo, so the Jimenez fit cannot
   take it. An explicit albedo input hook stays in the compose pass for the PBR material stage (C.11).
5. **Handheld default:** decided by the section D measurements between render-resolution 2x2 and half-resolution 3x3,
   no preference in advance (C.12, D.5).
6. **Parity dump and headless outputs:** the AO working term, the edges and mip 0 become opt-in outputs (C.13, D).

### E.2 Decided after review (2026-09-15)

1. **Compute first.** The compute pass kind is built first. It is needed anyway for the prefiltered depth chain, the
   denoiser, and the later ray-tracing and denoising roadmap; a fragment version would be thrown away.
2. **No shipped noise texture in the first version.** The `STBN_FASTNOISE` variant is a measurement variant whose
   texture is generated locally from the BSD-3 FastNoise code and never packaged. It ships, with the BSD-3 notice,
   only if section D measures it better than the Hilbert-R2 default.

---

## Sources (primary, as read)

- XeGTAO: https://github.com/GameTechDev/XeGTAO (`Source/Rendering/Shaders/XeGTAO.hlsli`, `XeGTAO.h`, `vaGTAO.hlsl`, `README.md`); issues #3, #6, #7.
- Jimenez et al. 2016: https://www.activision.com/cdn/research/PracticalRealtimeStrategiesTRfinal.pdf
- Therrien et al. 2023: https://arxiv.org/abs/2301.11376 ; code post https://cdrinmatane.github.io/posts/ssaovb-code/ ;
  Unreal VBAO product thread https://forums.unrealengine.com/t/ark-kra-vbao-visibility-bitmask-ambient-occlusion/2705204
- Bevy: https://github.com/bevyengine/bevy/tree/main/crates/bevy_pbr/src/ssao ; https://github.com/bevyengine/bevy/issues/19713 ;
  Bottosson thread at://did:plc:4x5tm73cr75gbyr7t6rzcph3/app.bsky.feed.post/3liejizfkmk2k (public API); Salm X post 1833211198009184650 (search snippet only).
- MXAO: https://github.com/martymcmodding/iMMERSE/blob/main/Shaders/MartysMods_MXAO.fx ; https://github.com/martymcmodding/qUINT ;
  https://guides.martysmods.com/shaders/immerse/mxao/ ; https://github.com/martymcmodding/iMMERSE/issues/8
- Alchemy AO: https://casual-effects.com/research/McGuire2011AlchemyAO/VV11AlchemyAO.pdf
- SAO: https://research.nvidia.com/sites/default/files/pubs/2012-06_Scalable-Ambient-Obscurance/McGuire12SAO.pdf
- openmw-ssao: https://github.com/zesterer/openmw-ssao (`shaders/ssao.omwfx`)
- Unity GTAO: https://github.com/MaxwellGengYF/Unity-Ground-Truth-Ambient-Occlusion (`Shaders/GTAO_Common.cginc`, `GTAO_Pass.cginc`)
- Godot: `~/Projekte/ReScaleFrame/references/godot/servers/rendering/renderer_rd/shaders/effects/ssao*.glsl`; SSIL https://github.com/godotengine/godot/pull/51206
- Donut: `~/Projekte/ReScaleFrame/references/Donut/shaders/passes/ssao_*.hlsl`
- Skyrim CS SSGI: `~/Projekte/ReScaleFrame/references/skyrim-community-shaders/features/Screen Space GI/Shaders/ScreenSpaceGI/gi.cs.hlsl` (GPL)
- CACAO: https://gpuopen.com/manuals/fidelityfx_sdk/fidelityfx_sdk-page_techniques_combined-adaptive-compute-ambient-occlusion/ ; https://gpuopen.com/fidelityfx-cacao/
- Unreal GTAO state: https://artiliada.github.io/2024/12/27/GTAO.html
- Noise: https://github.com/electronicarts/fastnoise (BSD-3) ; https://github.com/NVIDIAGameWorks/SpatiotemporalBlueNoiseSDK (`License.txt`: non-commercial)
- Arc 140V: https://chipsandcheese.com/p/lunar-lakes-igpu-debut-of-intels ; https://cputronic.com/gpu/intel-arc-140v
- Repository: `docs/research/xegtao-integration.md`, `docs/temporal-frame-contract.md`,
  `sources/shaders/ssao.fsh`, `scene-ssao.fsh`, `final.fsh`, `taa-resolve.fsh`, `.vanilla/.../shaders/{ssao,chunkopaque,bilateralblur}.{vsh,fsh}`,
  `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs`, `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs`.
