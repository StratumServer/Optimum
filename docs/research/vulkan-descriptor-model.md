# Native descriptor and draw model

Research notes for moving the Vulkan renderer from GL emulation (per-program layouts, texture units, uniform-by-location) to a native model. Collected 2026-09-15. vulkan.gpuinfo.org refused automated access (HTTP 403), so coverage statements come from driver release notes and Mesa's feature list, not gpuinfo statistics. The architecture at the end is what the roadmap's "Fully Vulkan-native" step follows.

## 1. Descriptor set organization

- **Sets by change frequency.** Arseny Kapoulkine's layout is set 0 = per frame/view (globals plus global textures), set 1 = per material, set 2 = per draw with a dynamic UBO ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)). NVIDIA adds: use as few sets as possible and avoid gaps between binding numbers ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)).
- **Layout compatibility.** Pipeline layouts are "compatible for set N" only if sets 0..N use identically defined layouts and the push constant ranges are identical. Binding a pipeline whose layout differs at set K invalidates sets K and above, and the spec advises putting the least frequently changing sets first ([spec](https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html); the fetched page was truncated, so re-check the exact wording). Dynamic offsets are given in set order, then binding order, and must be multiples of `minUniformBufferOffsetAlignment` ([vkCmdBindDescriptorSets](https://vkdoc.net/man/vkCmdBindDescriptorSets)).
- **Allocation and update cost.**
  - Batch `vkAllocateDescriptorSets`, because each call has overhead on some drivers. Size pools in classes (for example, shadow-pass sets vs material sets) ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
  - Caching sets in a hashmap keyed by content cut frame time 38% (44 ms to 27 ms) in the Khronos sample. Avoid `FREE_DESCRIPTOR_SET_BIT`, which can force a slower allocator ([Vulkan-Samples](https://docs.vulkan.org/samples/latest/samples/performance/descriptor_management/README.html)).
  - Granite hashes the set contents to find a cached `VkDescriptorSet`, recycles sets unused for 8 frames, and keeps one pool per layout ([Themaister](https://themaister.net/blog/2019/04/20/a-tour-of-granites-vulkan-backend-part-3/)).
- **Counterpoint from Zink.** Its maintainer measured that "the most performant option was always going to be the stupidest one": new sets every draw, written with update templates from aggressive bucket allocation. That beat content caching by 30-50% FPS in Minecraft ([supergoodcode](https://www.supergoodcode.com/sad-trumpet-noises/), [supergoodcode](https://www.supergoodcode.com/description/)).
  - Takeaway: use update templates. Caching helps mainly when set contents really repeat.

## 2. Bindless, descriptor_buffer and descriptor_heap

- **Descriptor indexing (core in 1.2).**
  - Provides update-after-bind, partially bound sets, runtime-sized arrays, and `nonuniformEXT` indexing. The spec minimum is 500k update-after-bind samplers.
  - Costs: non-uniform indexing can cost GPU time, and GPU-assisted validation gets expensive ([Khronos sample](https://docs.vulkan.org/samples/latest/samples/extensions/descriptor_indexing/README.html)).
  - Benefit: removes per-draw binds and enables GPU-driven batching ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
  - DXVK 3.x already requires it, so it is safe to assume on NVIDIA, AMD and Intel ([DXVK wiki](https://github.com/doitsujin/dxvk/wiki/Driver-support)).
- **VK_EXT_descriptor_buffer.**
  - Pros: descriptors live in a buffer and are updated with memcpy; no pools.
  - Cons: no `*_DYNAMIC` descriptor types, should not be mixed with classic sets, weak tooling. Khronos calls it "a ridiculously powerful feature" that is also "an equally ridiculous foot-gun" ([Khronos](https://www.khronos.org/blog/vk-ext-descriptor-buffer)).
  - DXVK 2.7 disabled it on NVIDIA Pascal and older and on AMD RDNA2 and older with AMD's own drivers, and noted a GPU-bound performance cost ([DXVK releases](https://github.com/doitsujin/dxvk/releases)).
  - Zink uses it by default where available ([supergoodcode](https://www.supergoodcode.com/buffered/)).
- **VK_EXT_descriptor_heap (January 2026)** is its successor: one resource heap and one sampler heap, with no sets or pipeline layouts ([Khronos blog](https://www.khronos.org/blog/vulkan-introduces-roadmap-2026-and-new-descriptor-heap-extension), [Guide](https://docs.vulkan.org/guide/latest/descriptor_heap.html)).
  - Ships in: NVIDIA 610+ ([NVIDIA](https://developer.nvidia.com/blog/streamlining-resource-binding-with-end-to-end-support-for-vulkan-descriptor-heaps/)), AMD Windows 25.30.17.02 ([AMD](https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-25-30-17-02-EXPANDED-VLK-SUPPORT.html)), RADV experimental in Mesa 26.1, ANV on by default from Mesa 26.2 ([Phoronix](https://www.phoronix.com/news/Intel-ANV-Descriptor-Heap-Merge)).
  - No Intel Windows support found: it is absent from the extension listing for Intel Windows driver 32.0.101.8992 ([Geeks3D](https://www.geeks3d.com/20260815/intel-arc-graphics-driver-32-0-101-89xx/)).
  - DXVK now prefers it and has deprecated its descriptor_buffer path ([DXVK releases](https://github.com/doitsujin/dxvk/releases)).
  - Still an EXT; it may be revised on the way to KHR.
- **Conclusion for 2025-2026:** plain descriptor-indexing bindless as the baseline; descriptor_heap only as a later optional path.

## 3. Per-draw data

- **Push constants.** The guaranteed minimum is 128 bytes in 1.3 and 256 bytes in 1.4 ([1.4 proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_VERSION_1_4.html)).
  - NVIDIA recommends push constants for per-draw constants ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)). Kapoulkine warns some (mostly mobile) architectures effectively offer about 12 bytes, and prefers dynamic UBOs for transforms ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
  - Push constant ranges are part of layout compatibility ([Guide](https://docs.vulkan.org/guide/latest/push_constants.html)).
- **Dynamic offsets vs a big SSBO.**
  - Dynamic UBOs beat rewriting descriptors. Use SSBOs for arrays beyond UBO limits ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
  - One large per-frame buffer with offsets reduces the number of sets ([Vulkan-Samples](https://docs.vulkan.org/samples/latest/samples/performance/descriptor_management/README.html)).
  - Inside drivers, dynamic offsets reach shaders "via some push-like mechanism", and Intel's UBO handling has 3-4 internal paths ([gfxstrand](https://gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/)).
  - The bindless pattern is a per-draw buffer holding material and transform indices ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
- **Memory.** Keep buffers persistently mapped. Integrated GPUs expose memory that is both host-visible and device-local; ReBAR gives discrete GPUs the same; otherwise fall back to a staging copy ([VMA](https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/usage_patterns.html)).

## 4. Pipeline state

- **Dynamic state coverage.**
  - Core in 1.3 via extended_dynamic_state 1 and 2: cull mode, front face, topology, depth/stencil tests, rasterizer discard, depth bias enable, primitive restart.
  - Only via EDS3 (never core): polygon mode, blend enable/equation, write mask.
  - Vertex input needs VK_EXT_vertex_input_dynamic_state ([Guide map](https://docs.vulkan.org/guide/latest/dynamic_state_map.html)).
  - 1.4 made none of EDS3, shader_object or GPL mandatory ([1.4](https://docs.vulkan.org/features/latest/features/proposals/VK_VERSION_1_4.html)).
- **Graphics pipeline library (GPL).** Splits a pipeline into four parts, fast-links them at draw time, and compiles an optimized pipeline in the background. Needs `INDEPENDENT_SETS` layouts. NVIDIA, AMD and Intel all support fast linking ([Khronos](https://www.khronos.org/blog/reducing-draw-time-hitching-with-vk-ext-graphics-pipeline-library)).
  - ANGLE does exactly this: libraries pre-created at program link time, linking at draw time, rate-limited monolithic builds in the background ([ANGLE](https://chromium.googlesource.com/angle/angle/+/HEAD/src/libANGLE/renderer/vulkan/doc/PipelineCreation.md)).
- **VK_EXT_shader_object.** All state is dynamic. Conformance requires draws within 150% of static-pipeline CPU cost and 120% of maximally dynamic pipelines ([Khronos](https://www.khronos.org/blog/you-can-use-vulkan-without-pipelines-today)).
  - Ships in NVIDIA, RADV (Mesa 24.1), ANV (Mesa 25.3) ([Phoronix](https://www.phoronix.com/news/Intel-ANV-VK_EXT_shader_object), [Mesa](https://docs.mesa3d.org/features.txt)).
  - Not listed in that Intel Windows driver ([Geeks3D](https://www.geeks3d.com/20260815/intel-arc-graphics-driver-32-0-101-89xx/)); AMD Windows support unconfirmed. Not portable.
- **General advice:** a pipeline cache serialized to disk, pre-warmed from recorded state sets ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)). Create pipelines off the render thread and minimize `vkCmdBindPipeline`, which has both CPU and GPU cost ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)).

## 5. Submitting many small draws

- **Command buffers.** Use L×T+N command pools (L = buffered frames, T = threads) and allocate/record on the thread that fills the buffer. Don't record tiny command buffers ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)). Aim for fewer than 10 submits per frame and skip parallel recording for passes under about 100 draws ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
- **Multi-draw indirect** plus compute culling removes per-model binds, and a buffer device address passed in push constants avoids set changes ([Vulkan-Samples MDI](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html)).
- **Sorting:** by pipeline, then material, so binds are minimized ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)).

## 6. How the translation layers do it

- **ANGLE** caches pipelines in four levels: the driver's VkPipelineCache, a hashmap from GL state to pipeline (xxHash over a packed description), a transition table between neighbouring states driven by dirty bits (avoiding hashing and memcmp), and the currently bound handle ([ANGLE](https://chromium.googlesource.com/angle/angle/+/HEAD/src/libANGLE/renderer/vulkan/doc/FastOpenGLStateTransitions.md)).
- **Zink** went from content caching, to lazy per-draw sets written with templates, to descriptor_buffer. It merged six sets into two buffers (normal and bindless) and saved over 10% VRAM ([supergoodcode](https://www.supergoodcode.com/buffered/)).
- **DXVK** went from descriptor_buffer (2.7) to descriptor_heap (3.x), with GPL and EDS3 as optional features against stutter ([DXVK wiki](https://github.com/doitsujin/dxvk/wiki/Driver-support)).
- **Lesson:** all three work around unknown, arbitrary state. A native renderer can close that state set ahead of time instead ([Khronos GPL](https://www.khronos.org/blog/reducing-draw-time-hitching-with-vk-ext-graphics-pipeline-library)).

## 7. Assessment of the current design (a41efce, shared frame block)

- **Set 0 frame block with a dynamic offset: reasonable.** It only stays bound across program switches if every program uses the identical set-0 layout *and* identical push constant ranges ([spec](https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html)).
  - Re-snapshotting on each change costs one dynamic offset per bind, which drivers handle as push-like data, so it's cheap ([gfxstrand](https://gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/)).
- **Per-program layouts in sets 1-3 are the weak spot.**
  - Layouts that differ per program invalidate sets 1-3 on every program switch.
  - Building 16-unit sampler sets per draw is the hottest path Zink and Khronos identify.
  - The per-program uniform block goes through a dynamic offset. That works, but per-draw scalars would be cheaper in push constants.
- **Fixes, in order:**
  1. One global pipeline layout.
  2. Bindless textures: sampled-image array plus a small set of shared samplers.
  3. Per-draw indices and scalars in push constants (at most 128 bytes on 1.3).
  4. Per-object data in an SSBO indexed by `gl_DrawID` or `firstInstance`.
- **Pitfalls.**
  - Intel's hardware binding table has 240 entries, so bind a few large arrays rather than many sets ([gfxstrand](https://gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/)).
  - Some Intel integrated GPUs have tight descriptor limits; query them ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
  - descriptor_buffer regressed on older AMD and NVIDIA ([DXVK](https://github.com/doitsujin/dxvk/releases)).
  - AMD's Windows driver still reports Vulkan 1.3.x, so don't assume 1.4 limits ([Geeks3D](https://www.geeks3d.com/20260603/amd-radeon-adrenalin-26-6-x-graphics-driver/)).
  - Use `nonuniformEXT` where the index varies within a draw ([Khronos sample](https://docs.vulkan.org/samples/latest/samples/extensions/descriptor_indexing/README.html)).

## Native architecture for this renderer

**Now**

1. **One global pipeline layout for every program.**
   - Set 0: frame UBO with a dynamic offset, plus global textures such as shadow maps.
   - Set 1: bindless `sampler2D[]`/`texture2D[]` array with partially-bound and update-after-bind flags.
   - Set 2: storage buffers (chunk faces, per-object data).
   - One push constant range of 128 bytes or less.
   - Why: layout compatibility ([spec](https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html)); bindless ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).
   - Portable on 1.3: DXVK requires descriptor indexing on all three vendors ([DXVK wiki](https://github.com/doitsujin/dxvk/wiki/Driver-support)).
   - Check `maxPerStageDescriptorUpdateAfterBindSampledImages` at startup.
2. **Per-draw data in push constants:** model matrix or offset, tint, texture/material indices ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/)).
   - Larger per-program uniforms go in one per-frame SSBO or UBO ring addressed by an index or offset ([Vulkan-Samples](https://docs.vulkan.org/samples/latest/samples/performance/descriptor_management/README.html)).
3. **Until bindless lands:** write sets with `vkUpdateDescriptorSetWithTemplate` from bucketed per-frame pools that are reset, not freed ([supergoodcode](https://www.supergoodcode.com/sad-trumpet-noises/)).
4. **Buffers:** VMA-style sub-allocation, persistently mapped, preferring host-visible device-local memory ([VMA](https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/usage_patterns.html)).
5. **Pipelines:** a pipeline cache serialized to disk, plus pre-warming of recorded pipeline keys at load ([zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/); details in [vulkan-caching.md](vulkan-caching.md)). Keep blend and polygon mode in the key when EDS3 is missing.

**Next**

6. **Use EDS3 and vertex-input dynamic state when present** to shrink the key to program + formats ([Guide](https://docs.vulkan.org/guide/latest/dynamic_state_map.html)). Add GPL fast-linking with background optimized builds, ANGLE-style ([ANGLE](https://chromium.googlesource.com/angle/angle/+/HEAD/src/libANGLE/renderer/vulkan/doc/PipelineCreation.md)).
7. **Sort draws** by pipeline and then material, and cache the bound pipeline and state behind dirty bits ([ANGLE](https://chromium.googlesource.com/angle/angle/+/HEAD/src/libANGLE/renderer/vulkan/doc/FastOpenGLStateTransitions.md)).
   - Chunks: MDI with per-draw SSBO entries indexed by draw ID.
   - Entities and particles: instancing.
8. **Parallel recording** with per-thread pools, only for large passes ([NVIDIA](https://developer.nvidia.com/blog/vulkan-dos-donts/), [zeux](https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/)).

**Later**

9. **GPU-driven culling for chunks** with `vkCmdDrawIndexedIndirectCount` ([Vulkan-Samples](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html)).
10. **Optional descriptor_heap backend** once Intel Windows ships it and the extension settles toward KHR ([Khronos](https://www.khronos.org/blog/vulkan-introduces-roadmap-2026-and-new-descriptor-heap-extension)). Skip descriptor_buffer; DXVK is deprecating it.
11. **Shader objects** only as an optional path (NVIDIA and Mesa), never a requirement ([Phoronix](https://www.phoronix.com/news/Intel-ANV-VK_EXT_shader_object)).

**Uncertain / version-dependent**

- gpuinfo coverage percentages could not be obtained.
- AMD Windows shader_object support is unconfirmed.
- Intel Windows support for descriptor_heap and shader_object rests on one Geeks3D extension listing.
- Release dates on DXVK's release page were unreadable, so none are given.
- The 256-byte push constant minimum applies only to Vulkan 1.4 devices.
