# Parity allowlist

Attachments that `scripts/dev/ssim.py --allowlist docs/parity-allowlist.md` accepts below the SSIM
threshold (default 0.98) when comparing an OpenGL and a Vulkan `OPTIMUM_PARITY_DUMP` of the same
frame. The first column is the dump file name or an fnmatch glob
(`<slotIndex>-<slotName>-<color<i>|depth>-<format>.<ext>`); the third bounds the accepted deviation
with `ssim>=<x>`, `mad<=<x>` (mean absolute difference, 0-255 for PPM/PGM, raw values for PFM),
`missing` (exists on one side only) or `any`, comma separated. An allowlisted attachment outside
its bound still fails.

| attachment | reason | max accepted deviation |
|---|---|---|

A row may be added only when the difference has been explained, not merely observed: the reason
names the mechanism (for example undefined contents the vanilla pipeline never reads, or a
documented precision difference between the two APIs) with the evidence that shows it - the GL and
device branches of the method read side by side, the `sync,best` validation log, and a GPU readback
test in `Optimum.Render.Vulkan.Tests` that pins the behaviour. The bound is the tightest value the
measured dumps support, never `any` for an attachment that is sampled later in the frame, and the
row is removed as soon as the difference is fixed. A GL-vs-GL comparison never uses this file: two
OpenGL runs of the same frame must reach 1.000 on every attachment without it.
