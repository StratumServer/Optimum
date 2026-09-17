# Vulkan validation and best practices

Research notes for validating the Vulkan 1.3 renderer (Silk.NET, C#) against current Khronos and vendor guidance. Collected 2026-09-15. The design derived from it is the milestone plan at the end, which the roadmap's "Best-practice validation" step follows.

## 1. What the Khronos validation layer can do

**Validation areas and their settings.** These are layer settings in `VkLayer_khronos_validation.json` (https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/layers/VkLayer_khronos_validation.json.in):

- **Core checks (on by default):** `validate_core`, `check_image_layout`, `check_command_buffer`, `check_object_in_use`, `check_query`, `check_shaders` (runs spirv-val, with caching), `stateless_param`, `object_lifetime`, `thread_safety`, `unique_handles`.
- **Synchronization (off by default):** `validate_sync`. Related settings:
  - `syncval_full_validation` checks accesses across all command buffers.
  - `syncval_shader_accesses_heuristic` is documented as "may produce false-positives".
  - `syncval_message_extra_properties` adds key/value fields you can filter on.
- **Best practices (off by default):** `validate_best_practices`, plus `validate_best_practices_arm`, `_amd`, `_img` and `_nvidia`.
- **Legacy API warnings:** `legacy_detection` with `legacy_detection_mode`.
- **GPU-AV:** `gpuav_enable` with sub-checks for descriptor indexing, buffer-device-address out-of-bounds, indirect draw/dispatch buffers, vertex attribute fetch out-of-bounds, a shader sanitizer (for example divide-by-zero), plus `gpuav_safe_mode`.
- **Debug printf:** `printf_enable`, `printf_to_stdout`, `printf_buffer_size` (default 1024).

**What each check catches and its limits.**

- **Sync validation** reports five hazard types (read-after-write, write-after-read, write-after-write, and two racing variants). It does not track exact shader descriptor use, memory aliasing, indirect buffers or host memory access (https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/docs/syncval_usage.md).
- **GPU-AV** needs Vulkan 1.1+, one free descriptor set slot, and the features `fragmentStoresAndAtomics`, `vertexPipelineStoresAndAtomics` and `timelineSemaphore`.
  - The GPU-AV documentation strongly advises against running it together with CPU core validation because of the slowdown.
  - It has a fast "regression mode" and a slower, crash-avoiding "debug mode" (https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/docs/gpu_validation.md).
  - SDK 1.4.341 added "Scoped GPU-AV" and GPU-AV coverage for descriptor heap and descriptor buffer (https://www.lunarg.com/lunarg-releases-vulkan-sdk-1-4-341-0/).
- **Debug printf** uses one descriptor set and some device memory. Its messages arrive at INFO severity with message ID `0x4fe1fef9`. Presets exist: `VK_LAYER_PRINTF_ONLY_PRESET=1` and `VK_LAYER_PRINTF_ENABLE=1` (https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/docs/debug_printf.md).
- **CPU-side bindless checks** can't know which descriptors a shader actually uses, so they are expensive and prone to false positives. LunarG points to GPU-AV for this (Vulkanised 2023, slide 29: https://vulkan.org/user/pages/09.events/vulkanised-2023/vulkanised_2023_using_vulkan_validation_effectively.pdf).

**Vendor best-practice checks.** The main best-practices document does not list them. The check lists live in the layer source and in vendor posts:

- **Arm:** more than 4x MSAA, using `vkCmdResolveImage` instead of resolving in the render pass, index-buffer ordering that thrashes the vertex cache. Arm says these warnings can be noisy on other GPUs (https://developer.arm.com/community/arm-community-blogs/b/mobile-graphics-and-gaming-blog/posts/arm-best-practice-warnings-in-vulkan-sdk).
- **AMD:** flags that should or shouldn't be used, and clearing with "fast" colors (https://gpuopen.com/learn/vulkan-best-practice-layer/).
- **NVIDIA:** includes checks such as `CreateDevice-PageableDeviceLocalMemory` and `AllocateMemory-SetPriority` (see issue #8276 in section 2).
- The older enable string `VALIDATION_CHECK_ENABLE_VENDOR_SPECIFIC_AMD` (or `_ARM`) set through `VK_LAYER_ENABLES` still appears in these posts.

**How to configure.**

- **VK_EXT_validation_features is deprecated** in favour of VK_EXT_layer_settings (https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_validation_features.html). The renderer's current `"sync,best"` path should move over.
- **Precedence:** environment variables, then `vk_layer_settings.txt`, then VK_EXT_layer_settings (available since 1.3.272).
- **Settings file lookup:** the working directory, then `VK_LAYER_SETTINGS_PATH` (a directory or a file).
- **Environment variable names, strongest first:** `VK_KHRONOS_VALIDATION_<SETTING>`, `VK_VALIDATION_<SETTING>`, `VK_<SETTING>`. The `VK_LAYER_<SETTING>` form is deprecated.
- **Enabling layers from outside the app:** `VK_LOADER_LAYERS_ENABLE=*validation` (loader 1.3.234+).
- Source for the four items above: https://github.com/KhronosGroup/Vulkan-Utility-Libraries/blob/main/docs/layer_configuration.md
- **Open question:** the settings JSON lists environment names like `VK_LAYER_KHRONOS_VALIDATION_VALIDATE_BEST_PRACTICES`. Test which spelling the pinned SDK version actually honours.

**Cost.**

- LunarG: "Don't enable all areas at once (it will be slow), pick one of Core / Shader-Based / Synchronization / Best Practices", then fix what each reports and re-run the Standard preset (Vulkanised 2023, slide 17).
- GPU-AV reads data back from the GPU and instruments shaders (gpu_validation.md above).
- No authoritative slowdown multipliers were found; measure on our own workload. A "2–5x overhead" figure is sometimes attributed to the Vulkanised 2023 slides, but it does not appear in them.

## 2. Running validation in CI and in-game

**Defaults** (https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/layers/vk_layer_settings.txt):

- `report_flags = error,warn`. Add `perf` and `info` when running best practices, because best-practice messages come out as Warning or Performance severity (Vulkanised 2023, slide 19).
- `enable_message_limit = true` with `duplicate_message_limit = 10`. In CI, count messages in our own callback rather than relying only on what the layer prints.
- `message_id_filter` is empty and takes a comma-separated list of VUID names or hex IDs.

**Filtering.**

- Use the layer's built-in filter rather than dropping messages in the callback; LunarG says built-in filtering is faster.
- It is still fine to use the callback to "trigger failures in your unit test framework" (slide 23).
- For sync messages, filter on the structured extra-properties fields, not on message text (syncval_usage.md).

**Known false positives.** Suppress each one by exact ID, with a link to the upstream issue and a layer version to re-check against:

- NVIDIA `BindMemory-NoPriority` fires even when priority is set through `VkMemoryPriorityAllocateInfoEXT` (https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/8276).
- `vkWaitForFences` with a zero timeout (https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/1788).
- Small-allocation warnings from UI code such as ImGui (https://github.com/ocornut/imgui/issues/4238).
- A fence reset after `vkDeviceWaitIdle` when the fence was a present fence (https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/8376).
- Several sync-validation issues around sync2 access flags and timeline semaphores (https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/7456, https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/7457).

**Engine practice.** Godot runs with `--gpu-validation`, and `--gpu-abort` quits on the first error (https://docs.godotengine.org/en/latest/engine_details/development/debugging/vulkan/vulkan_validation_layers.html). No documented CI validation setups were found for DXVK, Vulkan-Samples or Filament.

## 3. Other review tools

- **vkconfig:** GUI presets for validation, synchronization and best practices, with a message-mute list (Vulkanised 2023, slides 7 and 16).
- **GFXReconstruct:** capture a known-good session, then replay it after code or driver changes to catch regressions (https://www.lunarg.com/mastering-gfxreconstuct-part-1/).
- **RenderDoc:** can replay a capture with API validation turned on (https://renderdoc.org/docs/window/capture_attach.html).
- **Nsight Aftermath:** GPU crash dumps and checkpoints through `VK_NV_device_diagnostics_config` (https://docs.nvidia.com/nsight-aftermath/SDK/index.html). Nsight's injection has been reported to cause validation errors of its own, so don't validate while Nsight is attached (https://forums.developer.nvidia.com/t/new-versions-of-nsight-add-flags-behind-the-scene-that-cause-vulkan-validation-errors/216730).
- **AMD:** the Radeon Developer Tool Suite includes RGP (GPU profiler), RMV (memory visualizer, uses debug names) and RGA (https://gpuopen.com/news/introducing-radeon-developer-tool-suite/). Radeon GPU Detective uses debug-utils labels for crash triage (https://gpuopen.com/learn/rgd-1-1-vulkan-support/).
- **Arm Performance Studio / Frame Advisor:** documented for Vulkan 1.0–1.2, so it is of limited use for a desktop 1.3 renderer (https://learn.arm.com/learning-paths/mobile-graphics-and-gaming/ams/).
- **Intel GPA:** 2025.1 is the final release and the tool is being discontinued; don't plan around it (https://www.intel.com/content/www/us/en/developer/articles/release-notes/gpa/2024-4.html).
- **Vulkan Guide chapters to audit against:** Synchronization, synchronization2, Swapchain Semaphore Reuse, Memory Allocation, descriptor indexing, WSI, Pipeline Cache, Robustness, Formats, Threading, debug utils, Deprecated (https://docs.vulkan.org/guide/latest/index.html).

## 4. Audit checklist

- **Memory:** sub-allocate; use dedicated allocations for large render targets; set memory priority where supported (https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/usage_patterns.html); enable pageable device-local memory on NVIDIA (https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_pageable_device_local_memory.html). VK_EXT_memory_budget: no source fetched yet, unverified.
- **Command buffers:** reset whole pools rather than individual buffers, never allocate and free per frame (measured at 28.8% of frame time), use `ONE_TIME_SUBMIT`, one pool per thread (https://docs.vulkan.org/samples/latest/samples/performance/command_buffer_usage/README.html).
- **Swapchain:**
  - Keep one present semaphore per swapchain image, indexed by the acquired image index. Validation has flagged unsafe reuse since SDK 1.4.313 (https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html).
  - `swapchain_maintenance1` is now KHR and adds present fences, per-present mode changes and releasing acquired images (https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_swapchain_maintenance1.html).
  - `FIFO_LATEST_READY` is a present mode worth considering (https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_present_mode_fifo_latest_ready.html).
  - Also review recreation with `oldSwapchain` (no source fetched yet).
- **Pipeline cache:** check the header's vendorID, deviceID and pipelineCacheUUID before loading; persist the cache robustly (https://docs.vulkan.org/guide/latest/pipeline_cache.html). Details in [vulkan-caching.md](vulkan-caching.md).
- **Device loss:** VK_EXT_device_fault, and the newer VK_KHR_device_fault, which can be queried at any time and also reports non-fatal faults (https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_device_fault.html).
- **Debug naming:** name objects and label command buffers, which improves validation messages and tool output (Vulkanised 2023, slides 20–25).
- **Descriptors, image layouts and usage flags, format feature checks, queue usage:** run core validation plus GPU-AV (for bindless), and review against the Formats and Threading chapters.

## 5. Linux, Mesa and CI

- **Lavapipe:** Vulkan 1.3 conformant and exposes all Vulkan 1.4 core extensions (not yet submitted for 1.4 conformance). It runs on Windows and Linux and is aimed at CI runners without a GPU (https://vulkan.org/user/pages/09.events/vulkanised-2025/T5-Lucas-Fryzek-Igalia.pdf). Windows builds are available (https://github.com/jakoch/rasterizers).
- **Pinning the driver:** set `VK_DRIVER_FILES` (loader 1.3.207+; `VK_ICD_FILENAMES` is deprecated) or `VK_LOADER_DRIVERS_SELECT` (1.3.234+) (https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderInterfaceArchitecture.md).
- **Mesa environment variables** (https://docs.mesa3d.org/envvars.html):
  - `MESA_VK_ABORT_ON_DEVICE_LOSS`
  - `MESA_VK_WSI_HEADLESS_SWAPCHAIN`, which suits the headless harness
  - `MESA_VK_DEVICE_SELECT`
  - `RADV_DEBUG=hang` (writes hang dumps), `RADV_DEBUG=syncshaders`
  - `ANV_DEBUG`
- **Expect lavapipe-specific failures** in the layer itself (https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/7731). Treat lavapipe as a correctness gate, not a stand-in for vendor best-practice or performance checks.

## Milestone plan for this renderer

1. **Move to VK_EXT_layer_settings (P0).** Replace `"sync,best"` with explicit settings, and log the layer version and the settings actually applied.
   - *Exit:* tests set `validate_sync`, `validate_best_practices` and the vendor settings through VK_EXT_layer_settings, and skip with a clear reason when a setting is missing.
   - Sources: validation_features deprecation refpage; layer_configuration.md.
2. **Split the validation runs by area, per LunarG's advice (P0).**
   - (a) Standard core validation plus thread safety.
   - (b) Sync only, with `syncval_full_validation` and `syncval_message_extra_properties`.
   - (c) Best practices plus all four vendor settings, with `report_flags=error,warn,perf,info`.
   - (d) GPU-AV with core off, nightly.
   - *Exit:* each run fails on any message at warning severity or above that isn't in the suppression list.
   - Sources: Vulkanised 2023 slides 17 and 19; gpu_validation.md.
3. **Suppression policy (P0).** Keep a versioned `message_id_filter` list; each entry records the VUID, the upstream issue link, the layer version and a re-check date. The Arm and IMG checks are advisory on desktop GPUs.
   - *Exit:* at most a handful of documented entries.
   - Sources: issues #8276 and #1788; the Arm blog.
4. **Headless harness runs (P1).** Drive the real client for N frames, including a resize (swapchain recreation) and a scene load, on NVIDIA and AMD (Windows) plus RADV and ANV (Linux).
   - *Exit:* zero errors or warnings across the full session. Intel on Windows is lower priority, and its tool support is ending.
5. **Lavapipe CI lane (P1).** Pin the driver with `VK_DRIVER_FILES` and use `MESA_VK_WSI_HEADLESS_SWAPCHAIN` and `MESA_VK_ABORT_ON_DEVICE_LOSS`.
   - *Exit:* core and sync validation are clean on every PR.
   - Caveat: lavapipe's feature set differs from GPUs, so gate its tests on capabilities.
   - Sources: Mesa envvars page; loader docs.
6. **Review against Khronos guidance (P1).** Work through the section 4 checklist against the listed Guide chapters and the command-buffer sample. The swapchain semaphore-per-image fix is mandatory.
   - *Exit:* a signed-off checklist with evidence for each item.
7. **Robustness and tooling (P2).**
   - Debug names on all objects, and command buffer labels.
   - VK_EXT/KHR device_fault reporting on `VK_ERROR_DEVICE_LOST`.
   - Optional Aftermath integration.
   - A GFXReconstruct capture of a reference scene, replayed on driver updates.
   - *Exit:* a deliberately triggered device loss produces a fault report.

**Version notes.** GPU-AV scope and the semaphore-reuse VUID depend on the SDK version (1.4.313 and 1.4.341). Pin the SDK or layer version in CI.
