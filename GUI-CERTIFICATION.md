# GUI certification before client entry

The launcher reads `DATA/.optimum/gui-certification.json` and its companion
`gui-certification-evidence.json` after patch/cache resolution and before
`ClientProgram.Main`. Missing, invalid, negative or incompatible evidence selects
vanilla GUI composition. The game continues launching. The environment/config GPU
request cannot approve a certificate. The launcher clears approval before every
validation; the runtime gate starts denied, also for direct client launches.

The private runner resolves the runtime fingerprint through the patched launcher
before and after the isolated Pharos test. It issues local evidence only after
that test exits successfully with exactly one pass and the runtime fingerprint
stays unchanged. There is no launcher command that accepts a caller-supplied
success code to issue approval. The launcher validates the certificate before
`ClientProgram.Main`, runs a hidden OpenGL probe in a child process with an
eight-second deadline, and requires exact vendor, renderer, GL version, major,
minor, context profile mask, and context flags. Missing display, native failure,
timeout, software renderer, incomplete output, or any mismatch selects vanilla.
The real game context repeats the same comparison before every candidate texture;
a mismatch revokes approval for that launch. GL context profile identity requires
OpenGL 3.2 or newer. The existing Skia readback/state probe remains required.

The certificate is a local review artifact, not a cryptographic authorization
boundary against code running as the user. The private wrapper derives checkout
HEAD IDs from Git and records loaded artifact hashes; the hashes identify the
actual staged binaries even when a checkout has local edits. `DriverVersion` is
optional diagnostic text and is not trusted or compared. The live GL version,
vendor, renderer and context profile are compared, but these strings do not
provide physical-device UUID attestation. A certificate applies only to the
local staged runtime/profile. Do not copy certificates between machines.

The certificate is a local review artifact, not a cryptographic authorization
boundary against code running as the user. Schema 1 uses these exact property
names: `SchemaVersion`, `Policy`, `ProfileId`, `RuntimeIdentifier`,
`RuntimeFingerprint`, `EvidenceSha256`. Policy and profile are both
`exact-local-gui-v1`; hashes are SHA-256 hexadecimal strings. JSON is bounded to
1 MiB; duplicate properties and unknown certificate fields are rejected.

The fingerprint binds launcher arguments, runtime identifier, root runtime
assemblies/configuration, Lib, Mods, assets, patched cache contents and datapath
Mods/ModConfig/assets/clientsettings.json. File additions/removals change it;
linked inputs are unsupported. Evidence must identify the exact staged Lib/API,
Cairo and Skia dependencies and pass initial composition, recomposition, scales
1/1.5/2, primitive and non-subset fixtures, SVG GPU coverage and observer checks.
Counters must show positive successful candidates and zero fallback or native
materialization. Changing only the decision string cannot approve evidence.

The evidence also carries the runtime/profile fingerprint captured before the
Pharos process; launcher validation requires it to match the certificate and
current profile. The wrapper removes old approval first, enforces a 15-minute
test deadline, and removes a newly written certificate if launcher validation
fails. This is fail-closed operational binding, not protection against a
malicious process with the same user's filesystem access.

The current evidence corpus is an inventory/fixture profile, not exhaustive GUI
coverage. Arbitrary mod callbacks, external paths, future GUI states, native
escapes, and device/context changes cannot be universally certified.
`--validate-only` continues to validate assemblies without creating an OpenGL
context, granting GUI approval or starting the client.

Text keeps Cairo's native rasterization. The recording backend does not translate
font and glyph operations into Skia; unsupported text operations materialize the
recorded prefix and execute through Cairo with the requested `FontOptions`, including
`Antialias.Subpixel`. A native materialization revokes the active GPU approval even
when the GPU request or metrics collection is temporarily disabled. Subsequent GUI
composition therefore uses the normal Cairo path. The regression test asserts that
the Subpixel text path materializes natively and produces the same bytes as direct
vanilla Cairo. A profile that materializes text during its measured workload cannot
receive a Skia-only certificate.

Certification also requires the live GL `Antialias.Best` rectangle fixture to read
back from the candidate GPU texture and match the native Cairo pixels within one
channel value. The assertion runs inside the Pharos test before its evidence can be
written; the report records that result and launcher validation requires it.

## SVG rasterization coverage

The recorder accepts only integer aligned, disjoint solid rectangles that cover
the full drawing target. `DrawSvg` can record this subset when it has no tint.
`LoadSvg` further requires fully opaque fills because its native texture path
uploads straight RGBA bytes, while Skia renders premultiplied pixels. Tint,
transparency in `LoadSvg`, strokes, gradients, overlap, clipping, uncovered
pixels, fractional edges and unsupported shapes continue through NanoSVG. The
focused test checks pixel parity for accepted drawing cases and checks that the
other cases remain on the native path.

Run the CPU parser and parity tests with the platform's Vintage Story native
library directory available to the loader. This checks the SVG subset only. It
does not certify GPU output on Intel, AMD or NVIDIA.

```sh
export VINTAGE_STORY=/path/to/vintagestory
LD_LIBRARY_PATH="$VINTAGE_STORY/Lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet test Optimum.Tests/Optimum.Tests.csproj -c Release \
  --filter FullyQualifiedName~CairoSvgRecorderTests
```

On Windows, add `Vintagestory\Lib` to `PATH` before running the same
`dotnet test` command. After this test passes, run the private Pharos GPU suite
on each physical adapter using the Intel and NVIDIA steps below. Keep each
machine's report separate and let the exact-device certification gate decide
whether GPU rendering is eligible.

## Intel and NVIDIA verification

Use the same protocol independently on physical Intel and NVIDIA adapters;
record actual GL identity and driver, OS, staged runtime hashes, harness commit,
profile configuration and datapath. Do not report a software renderer or a
successful run on another adapter as vendor coverage.

Run the committed standalone Pharos diagnostic on each physical adapter using
the commands in `Optimum.Pharos.SvgDiagnostic/README.md`. It calls the patched
public `SvgLoader.LoadSvg` method and compares complete GPU texture readbacks
with the native NanoSVG route at 1x, 1.5x and 2x texture sizes. Each run writes
a separate JSON report with the live GL identity, context and loaded assembly
hashes. The diagnostic grants the live identity in memory, revokes it at the
end of the test, and never writes a certificate.

On Windows, also run the private full GUI suite against the staged candidate
in a separate evidence directory for each adapter:

```powershell
.\run-gui.ps1 -OptimumRoot <candidate-checkout> `
  -OutputRoot <adapter-specific-evidence-directory> `
  -Filter FullyQualifiedName~NanoSvgNativeCompatibility
```

That private runner writes the test log, TRX results, runtime manifest and
`svg-parity.json`. Keep those files with the physical adapter's report. Its
SVG matrix exercises the recorder fixtures directly and supplements the
standalone public-route diagnostic. For an eligible opaque tiled SVG, require
one candidate attempt, one success and zero fallbacks. For tint, alpha and
other unsupported shapes, require zero candidate attempts and exact native
fallback bytes. A passing CPU parser test or an in-memory identity
qualification is diagnostic only; neither produces an approved hardware
certificate.

1. Build and stage the exact candidate runtime. Use a disposable datapath/world
   and retain its configuration. Keep the authenticated user datapath intact.
2. Run the private Pharos GUI suite against that stage. Capture the startup
   observer, initial composition, recomposition, scales 1/1.5/2, primitive,
   non-subset, SVG, GL state and loaded-assembly provenance evidence. Keep native
   diagnostic references/readbacks outside production measurement windows.
3. Apply a process deadline and bounded repetitions. Run one GPU process at a
   time; stop on context loss, driver reset, hang or unexplained GL errors.
4. Verify launcher behavior with absent certificate, malformed JSON, AMD-negative
   evidence, changed evidence bytes, changed cached DLL, changed mod/configuration
   and a cache hit. Each must select vanilla while allowing normal client entry.
5. With a complete synthetic report, verify exact live identity match, each
   vendor/renderer/version mismatch, missing display, software renderer and
   timeout. Each failure must select vanilla and allow normal client entry.
   Keep the real AMD report negative; do not hand-edit reports into approvals.

Local regression commands:

```sh
dotnet test Optimum.Launcher.Tests/Optimum.Launcher.Tests.csproj
dotnet test Optimum.Tests/Optimum.Tests.csproj --filter FullyQualifiedName~OptimumGuiGpuProbeTests
make check-patches
make check-shaders
make patch-il
```

A passing unit suite proves certificate rejection, input binding and runtime
eligibility logic. It does not constitute Intel/NVIDIA/AMD hardware evidence.

## Stage A Intel and NVIDIA certification (2026-10-17)

This record comes from the former `feat/issue-130-recorder-stage-a` branch. It certifies that commit (which allowed Subpixel text to fall back to Cairo), not the head of `feat/issue-130-cairo-to-skiasharp`, and neither vendor has completed full GUI certification on that branch. Bounded follow-up results are recorded below.

On Optimum commit `8b915e5b72071f590f3a4685f4a71dbe8d6d7def`, the full inventory GUI scenario passed twice in separate processes on each physical adapter. Intel used UHD 620 with Mesa 26.0.8-1ubuntu0.3 and OpenGL 4.6 Core. NVIDIA used GeForce MX150 with driver 580.178.04 and OpenGL 4.3.0. Each run selected `skia-with-subpixel-cairo-fallback`, reported no decision reasons, and passed local launcher certificate validation. All fixture families passed, with no materialization failures. Initial composition recorded 19 Skia successes and 19 allowed Subpixel fallbacks. Ten recompositions recorded 144 successes and 216 allowed fallbacks. Scales 1, 1.5 and 2 recorded 12/30, 108/171 and 109/173 successes/attempts. The three SVG comparisons passed within one channel value. None of the four GUI logs contained a `Cairo.ScaledFont is leaking` warning.

The separate public Pharos SVG diagnostic passed all 14 cases on each adapter using Pharos v0.5.0. Its 12 supported solid SVG calls each used one candidate attempt and success with no fallback; maximum observed channel delta was 17, within the diagnostic's edge policy. Gradient and dashed-stroke cases used exact native output. This diagnostic does not issue a hardware certificate.

The resource fix disposes recorded Cairo scaled fonts and Skia paths when recorded drawing commands are cleared or released. After the fix, bootstrap and patch validation passed with 130 applied patches and no failures; shader validation passed; build completed with zero warnings and errors; IL patching reported 146/146 required methods. Test suites passed: Optimum 986 (34 skipped), Launcher 65 (4 skipped), CLI 24, Bootstrap.Core 156 and Installer 78. The Intel GPU-disabled control also passed the inventory scenario and selected `vanilla`. `make check` could not run because PowerShell (`pwsh`), required by packaging checks, is not installed. No GPU performance gain is claimed by these functional results.

## Linux Intel/NVIDIA bounded follow-up (2026-10-27)

This is a failed certification attempt on `feat/issue-130-cairo-to-skiasharp`, built from `4734ff2` plus the Fontconfig normal-weight and native-shim test-link changes subsequently committed as `68279bf` and `065ee2d`. The measured wrapper SHA-256 is `bfe2c909a48730c9e9269d6acd4e94ed373eb4bc1f169c51b5ce1a537b10fb61`. It does not supersede earlier results or certify either vendor.

- Ubuntu 26.04.1 LTS, .NET SDK 10.0.113. Vendor-isolated EGL/X11 contexts reported `Mesa Intel(R) UHD Graphics 620 (KBL GT2)` / OpenGL 4.6 Mesa 26.0.8-1ubuntu0.3, and `NVIDIA GeForce MX150/PCIe/SSE2` / OpenGL 3.3.0 NVIDIA 580.178.04. These identities came from `glGetString` in the actual Skia GPU process, not a separate GLX check.
- The existing bounded GPU oracle probe ran 73 cases with `OPTIMUM_CAIRO_NATIVE=allow`: **67 passed, 6 failed on each GPU**. Remaining failures are five `ScreenTextPreservesFractionalPhasesAndSourceAlpha` cases (Default/Subpixel at 1.5x and 2x, Gray at 2x), and `ScreenTextHonoursSubpixelOrder(Rgb)`. The matching failures on both renderers still block certification; their root cause is not established.
- With `OPTIMUM_CAIRO_NATIVE=deny`, NVIDIA completed **30 smoke cases** with no libcairo mapping. Intel failed its first case, `Difference/None`, because readback was entirely zero. The Intel result is a failure, not native-isolation or rendering certification; its cause remains unverified.
- Native Cairo selects the configured default Inter **Medium** face for Normal; the managed Fontconfig query previously selected Inter **Regular**. Mapping Normal to `FC_WEIGHT_MEDIUM` (100), as Cairo does, corrects that selection mismatch. Existing font-selection regressions remain unchanged. This correction alone does not fix all text parity failures.
- The native-shim Makefile now creates the `libcairo.so` linker symlink before linking its test. Its ABI tests passed from an empty temporary build directory.
- After building `tools/Cairo.AbiFixture` in Release, the full `Optimum.Tests` run with native compatibility explicitly allowed reported **2,420 passed, 8 failed, 34 skipped**. Six failures match the GPU Screen-text cases above; the other two concern hinted outline geometry at a 2x context scale and a general sheared font matrix. A separate deny-mode font-selection/ABI subset passed **136/136**; it is not a full deny-mode suite. Launcher passed 69 (4 skipped), CLI 24, Bootstrap.Core 173, and Installer 86.
- Bootstrap and Release reconstruction/build passed. Patch and shader checks passed. `make check` reports missing `pwsh`; packaging prerequisites are not fully satisfied. Full Pharos routes, screenshots, persistence/performance evidence, and cross-platform parity were not certified.

The bounded probe used a two-GiB managed heap limit, a 120-second timeout per process, explicit EGL vendor JSON selection, software-renderer rejection, and serialized hardware runs. No oracle assertions, tolerances, or tests were relaxed.

## Resumed corrections and bounded certification (2026-10-28)

This record supersedes the failed 2026-10-27 attempt above for the current working tree of `feat/issue-130-cairo-to-skiasharp`. It does not certify Windows or AMD, the full Pharos 57-route matrix, or performance.

The eight remaining unit failures and the Intel cold-readback failure were corrected in the working tree. Text fixes use hinted outline advances, outline-bound unhinted affine extents, native accumulated-box overlap clipping (Plus for overlapping glyph masks, Screen with preserved fringes for disjoint ones), and FreeType `FT_LOAD_TARGET_MONO | FT_LOAD_MONOCHROME` for Antialias None, matching native Cairo. Screen, Difference and Exclusion text reuses the captured FreeType coverage image with the existing byte-rounded premultiplied separable products instead of the runtime integer blender when the source is solid; shader, snapshot, pattern and null-coverage sources retain the original blender. Eight strict native parity rows were added (Difference/Exclusion across four antialias modes); no assertions or tolerances were removed.

Final evidence: full Optimum.Tests allow run 2,436 passed, 0 failed, 34 skipped (all 2,462 original test names preserved, skip set identical); deny font/matrix/cache/ABI subset 237/0; Launcher 69 (4 skipped), CLI 24, Bootstrap.Core 173, Installer 86. Bootstrap applies 167 patches with zero failures; Release build has zero errors; patch, shader and compatibility gates pass with zero cast divergences. Serialized physical matrices with hash-locked artifacts: Intel UHD 620 (Mesa 26.0.8-1ubuntu0.3, OpenGL 4.6 Core) 81/0 allow and 30/0 deny with no libcairo mapping; NVIDIA MX150 (driver 580.178.04, OpenGL 3.3.0) 81/0 allow and 30/0 deny. Fresh `make -j8 test` in the native shim passes; `make check` and `make test-runtimeconfig` pass with the locally provisioned PowerShell 7.6.9; `package-linux.ps1 -Format none` validates 150/150 required methods.

The real Pharos GUI scenario also passed on both adapters after a harness-only correction: the negative control now builds a genuinely non-recording managed-backed surface through the internal managed-storage constructor (automatic recording previously claimed the publicly constructed surface). Both wrappers exited 0 and each `gui-gpu-evidence.json` reports decision `skia-no-cairo-fallback` with empty decision reasons. Review-evidence diagnostics added 16 extra native separable parity cases (unequal .5/.8 alpha, 50% gray, four AA modes) and four shader/null-coverage fallback assertions, all passing. Windows/AMD, the full Pharos 57-route and performance certification remain unperformed and unclaimed.
