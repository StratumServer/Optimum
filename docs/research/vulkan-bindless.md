# Bindless texture descriptors on desktop Vulkan 1.3

Research notes for implementing decision 9 of the Vulkan-native plan (one pipeline layout, bindless
textures). Collected 2026-09-15. Every claim carries a URL. **[Inference]** marks reasoning not taken from a
source; **[Uncertain]** marks something that could not be verified from a primary source. Device limits come
from the Vulkan Hardware Database (gpuinfo, default "recent (1y)" filter) and the Mesa `main` tree at commit
`8860343e1ecc` (2026-09-15). Companion note: [vulkan-descriptor-model.md](vulkan-descriptor-model.md).

---

## 1. Combined `sampler2D[]` vs separate `texture2D[]` + `sampler[]`; view types, shadow and integer textures

**Guidance and engine practice**

- **Khronos Vulkan-Samples** shows both forms, combined `uniform sampler2D Combined[]` and separate
  `uniform texture2D Tex[]; uniform sampler Samp[]`. Separate arrays help when several shaders pair the same
  textures with different samplers. Texture indices travel in push constants; descriptor memory is treated
  as a ring buffer. https://github.com/KhronosGroup/Vulkan-Samples/blob/main/samples/extensions/descriptor_indexing/README.adoc
- **NVIDIA**: "Prefer using combined image and sampler descriptors" and "Do not exceed 1M active descriptors
  and 2K samplers in total for the whole application". Push constants are "the fastest way to transfer
  per-draw varying constants". https://developer.nvidia.com/blog/advanced-api-performance-descriptors
- **Granite** (2026, descriptor-heap path) keeps resources and samplers apart: about 1M resource descriptors
  and 4096 samplers. Sampler slots come from an index allocator because the sampler heap is too small to
  allocate linearly; combined pairs go through `useCombinedImageSamplerIndex`.
  https://themaister.net/blog/2026/03/29/walking-backwards-into-the-future-a-look-at-descriptor-heap-in-granite/
- **VK_EXT_descriptor_heap** uses separate sampler and resource heaps; its proposal notes that "multiple
  vendors have dedicated image and sampler heaps".
  https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_descriptor_heap.adoc
- **Bevy** uses global binding arrays, "one for each type of resource". Resources are de-duplicated and
  reference counted, and materials are allocated into slabs. The limit is 2048 resources per binding on
  non-Metal. Fallback resources were dropped once wgpu gained partially-bound arrays.
  https://github.com/bevyengine/bevy/pull/17898
  - Bevy falls back to non-bindless per material when "Intel Iris Xe … [has] low limits on the numbers of
    samplers per shader". https://github.com/bevyengine/bevy/pull/17155
- **nvpro GLSL generator** uses one array per GLSL type (`sampler2D = 0, texture2D = 1, usamplerBuffer = 2`).
  https://developer.nvidia.com/blog/improved-glsl-syntax-vulkans-descriptorset-indexing/
- **Parizet's bindless write-up** declares `sampler2D[]`, `usampler2D[]`, `sampler3D[]` and `usampler3D[]` at
  the same set/binding (aliasing), with a free-list per set.
  https://www.vincentparizet.com/blog/posts/vulkan_bindless_descriptors/
  - **[Uncertain]** Not confirmed from the spec that aliasing different sampled types on one binding is
    portable and validation-clean.
- **Wicked Engine** passes descriptor indices through push constants (search-result excerpt; the article
  returned 404). https://wickedengine.net/2021/04/bindless-descriptors/
- **Other engines**
  - vkguide.dev: the GPU-driven chapter covers unbounded texture arrays indexed from buffers; no dedicated
    bindless chapter found. https://vkguide.dev/docs/gpudriven/gpu_driven_engines/
  - wgpu exposes this as `TEXTURE_BINDING_ARRAY`, `PARTIALLY_BOUND_BINDING_ARRAY` and
    `SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING`. https://docs.rs/wgpu/latest/wgpu/struct.Features.html
  - DXVK 3.0 uses VK_EXT_descriptor_heap by default and deprecates its 2.7 descriptor-buffer path.
    https://github.com/doitsujin/dxvk/releases/tag/v3.0
  - vkd3d-proton needs "at least 1000000 UpdateAfterBind descriptors for all types except UniformBuffer".
    https://github.com/HansKristian-Work/vkd3d-proton/blob/master/README.md
  - **[Uncertain]** Godot, Filament and The Forge: no primary source found on their bindless texture schemes.

**Hard rules that force one array per type** (spec, Texel Input Validation,
https://docs.vulkan.org/spec/latest/chapters/textures.html)

- An `OpImage*Dref*` instruction with a sampler whose `compareEnable = VK_FALSE` gives a poison texel value,
  and so does a non-Dref instruction with `compareEnable = VK_TRUE`.
- If the image view type does not match the SPIR-V `Arrayed` flag (array vs non-array, cube vs cube array),
  texel values are undefined.
- If the signedness of the sample operation does not match the image format, the result is undefined.
- The validation layers do not check the Dref/compareEnable match; there is no VUID.
  https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/3641
- **[Inference]** Consequences for this renderer:
  - Each GLSL sampler type needs its own binding: `sampler2D`, `sampler2DArray`, `sampler3D`, `samplerCube`,
    the shadow variants, and `usampler*`/`isampler*`.
  - A shadow slot must be written with a comparison sampler.
  - A depth image that is also read without comparison needs a second slot in the plain 2D array with a
    non-compare sampler.
  - Integer textures should use nearest-filter samplers. **[Uncertain]** The exact VUID for linear filtering
    of integer formats was not pulled.

## 2. Flags, features, limits, sizing

**Layout, pool and allocation**

- A layout with `VK_DESCRIPTOR_SET_LAYOUT_CREATE_UPDATE_AFTER_BIND_POOL_BIT` must be allocated from a pool
  created with `VK_DESCRIPTOR_POOL_CREATE_UPDATE_AFTER_BIND_BIT`. Such layouts "have alternate limits".
  https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html
- **Per-binding flags** (same page):
  - `UPDATE_AFTER_BIND_BIT`: updates made between bind and submit are used and "do not invalidate the
    command buffer".
  - `PARTIALLY_BOUND_BIT`: descriptors that are not dynamically used "need not contain valid descriptors".
    "If a descriptor is not dynamically used, any resource referenced by the descriptor is not considered to
    be referenced during command execution."
  - `VARIABLE_DESCRIPTOR_COUNT_BIT`: only on the highest-numbered binding in the layout.
- `UNIFORM_BUFFER_DYNAMIC`, `STORAGE_BUFFER_DYNAMIC` and `INPUT_ATTACHMENT` cannot be update-after-bind; the
  spec summary says this also applies at the pipeline-layout level.
  https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html
  - **[Uncertain]** The exact pipeline-layout VUID text was not obtained. Query
    `maxDescriptorSetUpdateAfterBindUniformBuffersDynamic`; ANV reports `MAX_DYNAMIC_BUFFERS / 2`
    (anv_physical_device.c).
- Each descriptor type needs its own update-after-bind feature: `descriptorBindingSampledImageUpdateAfterBind`
  for SAMPLED_IMAGE and COMBINED_IMAGE_SAMPLER, `descriptorBindingStorageBufferUpdateAfterBind` for SSBOs, and
  so on. https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html
- Immutable samplers cannot be changed; on COMBINED_IMAGE_SAMPLER bindings with immutable samplers the
  sampler part of an update is ignored (same page).

**Features**

- Roadmap 2022 (inherited by 2024 and 2026) requires `descriptorIndexing`, all `*ArrayNonUniformIndexing`
  features for sampled images, storage buffers and storage images, `descriptorBindingSampledImageUpdateAfterBind`,
  `descriptorBindingStorageImageUpdateAfterBind`, `descriptorBindingStorageBufferUpdateAfterBind`,
  `descriptorBindingUpdateUnusedWhilePending`, `descriptorBindingPartiallyBound`,
  `descriptorBindingVariableDescriptorCount` and `runtimeDescriptorArray`.
  https://docs.vulkan.org/spec/latest/appendices/roadmap.html
- **[Uncertain]** Whether plain Vulkan 1.3 core (without the roadmap profile) makes these mandatory. Query
  them anyway.
- A dynamic index that is uniform per draw needs the 1.0 feature `shaderSampledImageArrayDynamicIndexing`;
  per-invocation (non-uniform) indices need `shaderSampledImageArrayNonUniformIndexing`.
  https://chunkstories.xyz/blog/a-note-on-descriptor-indexing/ ,
  https://docs.vulkan.org/guide/latest/extensions/VK_EXT_descriptor_indexing.html
- Target devices: the Arc 140V on Windows (driver 101.8724) and Lunar Lake on Mesa 26.1.7 both report
  `runtimeDescriptorArray`, `descriptorBindingPartiallyBound`, `descriptorBindingVariableDescriptorCount` and
  `descriptorBindingSampledImageUpdateAfterBind`. https://vulkan.gpuinfo.org/displayreport.php?id=48570 ,
  https://vulkan.gpuinfo.org/displayreport.php?id=51163
  - The Windows report also lists `shaderSampledImageArrayNonUniformIndexing`.
  - Both list VK_EXT_descriptor_buffer. **[Uncertain]** Neither showed VK_EXT_descriptor_heap in the
    extracted text.

**Limits**

- Definitions: https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceDescriptorIndexingProperties.html.
  The per-stage limit counts COMBINED_IMAGE_SAMPLER, SAMPLED_IMAGE and UNIFORM_TEXEL_BUFFER across all sets in
  the pipeline layout (search-result excerpt of
  https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineLayoutCreateInfo.html).
- The Vulkan-Samples README says the "min-spec here is 500k".
  https://github.com/KhronosGroup/Vulkan-Samples/blob/main/samples/extensions/descriptor_indexing/README.adoc

`maxPerStageDescriptorUpdateAfterBindSampledImages` by vendor (vendor attribution from the 100 most recent
reports per value on gpuinfo's `listreports.php?property=…&value=…`; distributions from
https://vulkan.gpuinfo.org/displaycoreproperty.php?core=1.2&name=maxperstagedescriptorupdateafterbindsampledimages&platform=windows
and `platform=linux`):

| Platform / driver | Value |
|---|---|
| NVIDIA Windows and Linux | 1,048,576 |
| AMD Windows | 4,294,967,295 |
| Intel Windows (Arc A/B, Arc 140V, "Intel Graphics") | 33,554,432 (140V report 48570; A770 report 37286) |
| RADV | 8,388,606 |
| ANV Gfx12.5+ (DG2, MTL, ARL, BMG, LNL) | 33,554,432 images; 67,108,864 samplers |
| ANV pre-12.5 (Skylake–Tiger/Alder Lake Iris Xe) | 201,326,592 images; 402,653,184 samplers |
| llvmpipe | 1,000,000 / 1,015,808 |
| SwiftShader | 500,000 |

**Where the Mesa values come from**

- **ANV**
  - Gfx ≥ 12.5 uses "extended bindless" direct descriptors: `intel_has_extended_bindless` is `verx10 >= 125`
    (src/intel/dev/intel_device_info.h). Older generations use indirect descriptors (anv_physical_device.c).
  - The limit is heap size divided by descriptor size. The bindless surface-state pool is 2 GiB or 4 GiB and
    the indirect descriptor pool 3 GiB (src/intel/vulkan/anv_va.c).
  - A source comment says ≤ Gfx12.0 is practically about 500K live image views.
  - https://gitlab.freedesktop.org/mesa/mesa/-/blob/main/src/intel/vulkan/anv_physical_device.c
- **RADV** sizes descriptor sets to stay addressable in 2 GiB, counting 32 bytes per sampler and 64 per
  sampled image. https://gitlab.freedesktop.org/mesa/mesa/-/blob/main/src/amd/vulkan/radv_physical_device.c
- **Pre-Skylake Intel**: the 240-entry binding table is a hardware limit there.
  https://gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/

**How the ANV limits derive**

- **Pre-Gfx12.5 image limit (confirmed from source):** `struct anv_address_range_descriptor` is a `uint64_t
  address` plus two `uint32_t` fields, 16 bytes. The indirect descriptor pool is 3 GiB (`anv_va.c`), and 3 GiB /
  16 B = 201,326,592, exactly what gpuinfo shows for Skylake through Tiger/Alder Lake on Linux.
  https://gitlab.freedesktop.org/mesa/mesa/-/blob/main/src/intel/vulkan/anv_private.h ,
  https://gitlab.freedesktop.org/mesa/mesa/-/blob/main/src/intel/vulkan/anv_va.c
- **Pre-Gfx12.5 sampler limit [Inference]:** `struct anv_sampled_image_descriptor` starts with a `uint32_t image`
  field holding a 20-bit SURFACE_STATE index; if the struct is 8 bytes, 3 GiB / 8 B = 402,653,184, matching the
  reported sampler limit. The rest of the struct was not read.
- **Gfx12.5+ (DG2, MTL, ARL, BMG, Lunar Lake 140V) [Inference]:** 33,554,432 images equals a 2 GiB bindless
  surface-state pool divided by a 64-byte surface state; 67,108,864 samplers would mean a 32-byte sampler state.
  The `ANV_SURFACE_STATE_SIZE` / `ANV_SAMPLER_STATE_SIZE` defines were not found.
- None of this changes the recommendation: every Intel target reports tens of millions of update-after-bind
  sampled-image descriptors, so array size is bounded by memory and write cost, not by the driver.

**Other limits that matter**

- `maxSamplerAllocationCount`: 4000 on NVIDIA and Intel Windows (including the 140V); 1,048,576 on AMD
  Windows; 65,536 on ANV and RADV.
  https://vulkan.gpuinfo.org/displaydevicelimit.php?name=maxSamplerAllocationCount&platform=windows
- `maxPushConstantsSize`: 256 on about 95% of devices; 128 on older AMD Windows (RX 560/570) and SwiftShader.
  https://vulkan.gpuinfo.org/displaydevicelimit.php?name=maxPushConstantsSize&platform=windows ; 128 is the spec
  minimum. https://github.com/KhronosGroup/Vulkan-Guide/blob/main/chapters/push_constants.adoc
- The non-update-after-bind `maxPerStageDescriptorSamplers` is 16 or 64 on older Intel Windows iGPUs: HD
  520/530/630 report 16; UHD 620/730 and Iris Xe report 64.
  https://vulkan.gpuinfo.org/displaydevicelimit.php?name=maxPerStageDescriptorSamplers&platform=windows
  - **[Uncertain]** Their update-after-bind sampler limits were not checked.

**Sizing**

- **[Inference]** Every desktop target allows millions of descriptors, so the practical cap is memory and CPU
  write cost. At RADV's roughly 96 bytes per combined descriptor, 65,536 slots is about 6 MB.
- A variable count is fixed at allocation, so growing the array means allocating a new set.

## 3. Index lifetime, deferred free, placeholders, validation

- **Destroying resources.** `vkDestroyImageView` requires "All submitted commands that refer to imageView must
  have completed execution" (VUID-vkDestroyImageView-imageView-01026).
  https://docs.vulkan.org/refpages/latest/refpages/source/vkDestroyImageView.html
  - For PARTIALLY_BOUND bindings only dynamically accessed descriptors count as referenced; destroying
    unaccessed ones is fine (clarified in the 1.3.210 spec update).
    https://github.com/KhronosGroup/Vulkan-Docs/issues/1794
- **Updating while in flight.** Update-after-bind covers updates between bind and submit;
  `UPDATE_UNUSED_WHILE_PENDING` covers updating descriptors that pending command buffers do not use.
  https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html
  - **[Inference]** Rewriting a slot that a submitted, unfinished command buffer may still sample is not
    covered. Slot reuse must wait until those frames finish.
- **Existing practice**
  - Batch texture writes at end of frame, and "be sure to not change a used resource in command buffers that
    are running". https://jorenjoestar.github.io/post/vulkan_bindless_texture/
  - Free-list per set. https://www.vincentparizet.com/blog/posts/vulkan_bindless_descriptors/
  - Slab allocation with de-duplication and reference counts. https://github.com/bevyengine/bevy/pull/17898
- **Placeholders vs partially bound.** Bevy dropped fallback resources once partially-bound arrays were
  available. https://github.com/bevyengine/bevy/pull/17898
  - **[Inference]** Still write a 1×1 placeholder into freed and unallocated slots: an accidental read then
    shows a visible colour instead of undefined behaviour, for one write per free.
- **Validation**
  - CPU validation cannot know runtime indices; with PARTIALLY_BOUND or UPDATE_AFTER_BIND the checks move to
    GPU-AV. https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/docs/gpu_av_descriptor_indexing.md
  - GPU-AV instruments shaders for out-of-bounds indices, uninitialized descriptors and destroyed descriptors,
    then post-processes accessed descriptors on the CPU after submit (same page).
  - Settings keys: `gpuav_enable`, `gpuav_descriptor_checks`, `gpuav_post_process_descriptor_indexing`,
    `gpuav_shader_instrumentation`, `gpuav_select_instrumented_shaders`.
    https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/layers/VkLayer_khronos_validation.json.in
  - GPU-AV adds runtime overhead.
    https://github.com/KhronosGroup/Vulkan-Samples/blob/main/samples/extensions/descriptor_indexing/README.adoc
  - A 2019 LunarG deck said GPU-AV waited for queue idle after each submit; **[Uncertain]** whether that still
    holds. https://www.lunarg.com/wp-content/uploads/2019/09/GPU-Assisted-Validation-v5_Feb_20.pdf

## 4. When `nonuniformEXT` is required

- **Rule.** All invocations in an invocation group using the same dynamic index need no annotation; different
  indices need `nonuniformEXT` and the `NonUniform` decoration.
  https://docs.vulkan.org/guide/latest/extensions/VK_EXT_descriptor_indexing.html
- **Spec VUIDs.** VUID-RuntimeSpirv-None-10148 and -subgroupSize-10149 require `NonUniform` on the resource
  operand (the pointer or sampled image) when the resource is not uniform within the invocation group.
  VUID-...-SampledImageArrayNonUniformIndexing-10135 requires dynamically uniform indexing without that
  capability. https://docs.vulkan.org/refpages/latest/refpages/source/RuntimeSpirv.html
- **Practical reading.**
  - Treat the invocation group as the whole draw call or dispatch. Only `gl_DrawID` is explicitly dynamically
    uniform; `gl_InstanceIndex` indexing needs `NonUniform`.
    https://anki3d.org/resource-uniformity-bindless-access-in-vulkan/
  - **[Inference]** A push-constant or per-draw UBO index is constant across the draw, so no `nonuniformEXT` is
    needed. An index from a vertex attribute, instance data or per-pixel data does need it.
- **GLSL and glslang.**
  - GL_EXT_nonuniform_qualifier: "Constructors and builtin functions … will not generate nonuniform results."
    https://github.com/KhronosGroup/GLSL/blob/main/extensions/ext/GL_EXT_nonuniform_qualifier.txt
  - glslang had a bug placing the decoration on the wrong values, addressed via PR #1762.
    https://github.com/KhronosGroup/glslang/issues/1760
  - `nonuniformEXT` on a texture index inside if/else or a ternary produces invalid SPIR-V (issue opened
    2024-03-29; **[Uncertain]** fix status). https://github.com/KhronosGroup/glslang/issues/3561
  - **[Inference]** Combined arrays avoid the `sampler2D(tex[i], s)` constructor path. Where nonuniform is
    used, check with spirv-dis that the sampled-image operand carries `NonUniform`.

## 5. Performance pitfalls

- **Non-uniform indexing is not native on most drivers.** `shaderSampledImageArrayNonUniformIndexingNative =
  VK_FALSE` means a non-uniformly indexed instruction "may execute multiple times".
  https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceDescriptorIndexingProperties.html
  - It is false on ANV and RADV (source), on Intel Windows (140V and A770 reports), and on about 25% of Windows
    and 63% of Linux reports.
    https://vulkan.gpuinfo.org/displaycoreproperty.php?core=1.2&name=shadersampledimagearraynonuniformindexingnative&platform=windows
  - AMD's compiler adds extra instructions when `NonUniform` is present.
    https://anki3d.org/resource-uniformity-bindless-access-in-vulkan/
  - **[Inference]** Keep indices per-draw uniform on hot paths.
- **Implicit LOD with divergent indices.** `quadDivergentImplicitLod` is false on ANV and RADV (source) and true
  on the 140V Windows driver.
  - **[Inference]** If the index varies inside a 2×2 quad, use explicit-LOD or gradient sampling.
- **Update-after-bind cost.**
  - Update-after-bind stops the driver consuming descriptors at record time and allows updates from multiple
    threads. https://developer.arm.com/community/arm-community-blogs/b/mobile-graphics-and-gaming-blog/posts/vulkan-descriptor-indexing
  - UBOs are deliberately not required to support update-after-bind because of implementation cost.
    https://gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/
  - DXVK 3.0 reports AMD RDNA1/2 Windows drivers can only use its "slow legacy binding model" with severe
    performance problems. https://github.com/doitsujin/dxvk/releases/tag/v3.0
- **Push constant vs UBO index.**
  - NVIDIA calls push constants the fastest per-draw path and advises few sets and tightly packed bindings.
    https://developer.nvidia.com/blog/advanced-api-performance-descriptors ,
    https://developer.nvidia.com/blog/vulkan-dos-donts/
  - **[Inference]** GPU-side cost is the same either way because both are uniform; the difference is CPU
    overhead.
- **Samplers.** Keep the unique sampler count small: 4000 allocation limit on NVIDIA and Intel Windows
  (gpuinfo above), and NVIDIA's "2K samplers" advice.

## 6. Known driver bugs and quirks

- **Intel Windows.** Bevy crashed on Arc (Core Ultra 7 155H, driver 101.6458) with "Binding count declared
  with exactly 2048 items, but 6 items were provided". https://github.com/bevyengine/bevy/issues/18098
  - A wgpu-level check that fires when partially-bound support is not detected. **[Uncertain]** Whether the
    root cause is the driver.
- **AMD Windows**
  - `binding_array` sampling corruption on Radeon 6800 XT with driver 23.11.1, fixed on the naga SPIR-V
    back-end side (PR #4766). https://github.com/gfx-rs/wgpu/issues/4762
  - Historical report: non-uniform sampler-array indexing plus `discard` caused device lost. **[Uncertain]**
    Date and fix unknown; the thread now redirects.
    https://community.amd.com/t5/opengl-vulkan/likely-driver-bug-in-vulkan-on-windows-when-using-ext-descriptor/m-p/243766
- **Mesa and others**
  - vkd3d-proton recommends RADV with Mesa ≥ 22.0 and NVIDIA ≥ 535 "to fix various bugs", and has not tested
    Intel. https://github.com/HansKristian-Work/vkd3d-proton/blob/master/README.md
  - An AMD driver crash from 2019 has since been fixed. https://chunkstories.xyz/blog/a-note-on-descriptor-indexing/
- **Nothing found** for 2024–2026 on large-array bugs specific to ANV, RADV or Intel Windows beyond the above.
  **[Uncertain]** That could be a search gap.
- **Future direction.** VK_EXT_descriptor_heap is meant "to completely replace" descriptor sets.
  https://www.khronos.org/blog/vulkan-introduces-roadmap-2026-and-new-descriptor-heap-extension
  - NVIDIA 610+ drivers support it.
    https://developer.nvidia.com/blog/streamlining-resource-binding-with-end-to-end-support-for-vulkan-descriptor-heaps/
  - DXVK requires NVIDIA 595.84+ for its heap path. https://github.com/doitsujin/dxvk/releases/tag/v3.0

---

## Implementation for this renderer

The sourced basis is cited above; everything in this section is **[Inference]** built on it.

**Set layout**

- **Set 0** (normal pool, no update-after-bind): frame UBO as `UNIFORM_BUFFER_DYNAMIC` plus frame textures.
  Dynamic buffers cannot be update-after-bind.
- **Set 1** (layout flag `UPDATE_AFTER_BIND_POOL`, pool flag `UPDATE_AFTER_BIND`): combined-image-sampler
  arrays, one binding per GLSL sampled type, every binding `PARTIALLY_BOUND | UPDATE_AFTER_BIND`. Starting
  sizes, all clamped against the device limits:

| Binding | Type | Size |
|---|---|---|
| 0 | `sampler2D` | 16384 |
| 1 | `sampler2DArray` | 1024 |
| 2 | `samplerCube` | 256 |
| 3 | `sampler3D` | 256 |
| 4 | `usampler2D` | 1024 |
| 5 | `isampler2D` | 256 |
| 6 | `sampler2DShadow` | 128 |
| 7 | `sampler2DArrayShadow` | 64 |
| 8 (optional) | `samplerCubeShadow` | 64 |

- The highest-numbered binding may also carry `VARIABLE_DESCRIPTOR_COUNT`; optional, since fixed sizes keep
  the design simple and memory stays in the single-digit MB range.
- **Set 2**: storage buffers, update-after-bind (`descriptorBindingStorageBufferUpdateAfterBind`) if they are
  rewritten while bound; otherwise a normal set.

**Why combined arrays.** GL-style `sampler2D` ties sampler state to the texture, and compare mode is a texture
parameter in GL. Combined arrays keep that model, need one index per sampler uniform, and follow NVIDIA's
preference. Sampler objects stay de-duplicated in a sampler cache, well under 4000. Reserve a shared
`sampler[]` only for post-process or compute passes if needed; revisit separate arrays when moving to
descriptor heap.

**Limits check at startup**

- Sum all set-1 counts plus set-0 textures; require the sum ≤ `maxPerStageDescriptorUpdateAfterBindSampledImages`
  and ≤ `maxDescriptorSetUpdateAfterBindSampledImages`.
- Same checks against the `*Samplers` limits, since combined descriptors count against both.
- `maxDescriptorSetUpdateAfterBindUniformBuffersDynamic` ≥ 1, because set 0 shares a pipeline layout with an
  update-after-bind set.
- `maxPushConstantsSize` ≥ 128.

**Features to enable**

- `runtimeDescriptorArray`, `descriptorBindingPartiallyBound`, `descriptorBindingSampledImageUpdateAfterBind`,
  and `descriptorBindingStorageBufferUpdateAfterBind` if set 2 uses it.
- `descriptorBindingVariableDescriptorCount` if used.
- `shaderSampledImageArrayDynamicIndexing` (1.0 feature).
- `shaderSampledImageArrayNonUniformIndexing` only if non-uniform paths exist.

**Fallback tiers**

1. No update-after-bind: fixed-size arrays in a normal pool; write only sets that no recording or executable
   command buffer has bound (for example one set copy per frame in flight, each updated before recording).
2. No runtime arrays or partial binding: sized arrays filled entirely with placeholders.
3. None of these features: the current per-program layout path (decision 9 says such a device stays on
   OpenGL instead; decide when implementing).

All listed target devices report the required features.

**Slot allocator and deferred free**

- One allocator per binding, each a LIFO free-list of `uint` slots. Slot 0 is reserved for a placeholder:
  1×1 magenta for colour arrays, 1×1 depth with a compare sampler for shadow arrays, a 1×1 integer texture for
  `usampler`/`isampler`.
- **Allocate:** pop a slot and queue the write. Flush all queued writes with one `vkUpdateDescriptorSets` per
  frame on the render thread, before recording any draw that uses the index. Update-after-bind makes this
  legal while set 1 is bound.
- **Free:** push `(binding, slot, view/image, retireSerial)` to a pending queue, where `retireSerial` is the last
  frame or submission that could have used the slot.
- **Each frame start**, for entries whose serial the GPU has finished (the timeline semaphore already tracks
  this):
  1. Write the placeholder into the slot.
  2. Destroy the view and image.
  3. Return the slot to the free-list.
- Never rewrite a live slot in place. A texture re-upload that changes the image gets a new slot, and the old
  one retires. In this renderer, transient aliasing (`TextureManager.Rebind`/`RestoreBindings`) swaps the image
  behind a texture id per frame, so a rebind must resolve to the physical texture's slot rather than
  rewriting the client texture's slot.

**Shaders**

- Rewrite `uniform sampler2D name;` to a field `uint name_idx;` in a push-constant block (or a per-draw record
  when a program exceeds its push-constant budget).
- Rewrite `texture(name, uv)` to `texture(uTex2D[pc.name_idx], uv)`, and the equivalent for other types.
- Shared declarations: `#extension GL_EXT_nonuniform_qualifier : require` and
  `layout(set=1,binding=0) uniform sampler2D uTex2D[];` etc.
- No `nonuniformEXT` for these per-draw indices. Only where an index comes from per-vertex, per-instance or
  per-pixel data, and there prefer explicit-LOD or gradient sampling.
- GLSL 330 constant-indexed sampler arrays (`uniform sampler2D a[4]`) become one index per element.
  **[Uncertain]** GLSL 3.30 was assumed to allow only constant indices on sampler arrays; not re-sourced.

**Validation plan**

- CPU validation layer always on in debug runs.
- `spirv-val` on every rewritten shader; assert with `spirv-dis` that no unexpected `NonUniform` appears and that
  any intended one sits on the sampled-image operand.
- Dedicated GPU-AV runs with `gpuav_enable`, `gpuav_descriptor_checks` and
  `gpuav_post_process_descriptor_indexing`, using `gpuav_select_instrumented_shaders` to limit cost.
- Engine-side debug asserts, because the layers do not check these:
  - Dref usage matches `compareEnable`.
  - Image view type matches the destination binding.
  - Integer formats go only to `usampler`/`isampler` bindings with nearest samplers.
- Smoke tests on NVIDIA, AMD Windows, Intel Windows (Arc 140V), ANV and RADV.
