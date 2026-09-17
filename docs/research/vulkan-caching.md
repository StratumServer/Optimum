# SPIR-V and pipeline cache persistence

Research notes for persisting compiled SPIR-V and the VkPipelineCache across launches. Collected 2026-09-15. The design at the end is what the cache implementation follows.

## 1. VkPipelineCache persistence

**What the spec guarantees**

- The blob starts with `VkPipelineCacheHeaderVersionOne`: headerSize (32), headerVersion, vendorID, deviceID and a 16-byte pipelineCacheUUID. All fields are little-endian. Apps are expected to compare these against `VkPhysicalDeviceProperties`. [refpage](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCacheHeaderVersionOne.html)
- If the initial data is incompatible, "the pipeline cache will be initially empty". It is also valid usage that `pInitialData` came from `vkGetPipelineCacheData`. [VkPipelineCacheCreateInfo](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCacheCreateInfo.html)
- `vkGetPipelineCacheData` returns `VK_INCOMPLETE` if the buffer is too small, so always use the size-query-then-fetch pair. [refpage](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetPipelineCacheData.html)
- Using a cache during pipeline creation is internally synchronized. `vkMergePipelineCaches` "should … prune duplicate entries". The destination cache needs external synchronization unless `INTERNALLY_SYNCHRONIZED_MERGE` (maintenance8) is used. [merge refpage](https://docs.vulkan.org/refpages/latest/refpages/source/vkMergePipelineCaches.html), [yosoygames](https://www.yosoygames.com.ar/wp/2024/09/why-does-vkmergepipelinecaches-exist/)

**What drivers actually do** ([Kapoulkine / Roblox](https://zeux.io/2019/07/17/serializing-pipeline-cache/))

- Some drivers don't check the UUID properly and crash after a driver update.
- Some don't bump the UUID when compatibility breaks, including between 32-bit and 64-bit builds.
- One driver fails when given `initialDataSize == 0` with a non-null pointer.
- Seen on disk: partial writes, zero-filled chunks and zero-size files.

**Other crash reports**

- AMD 22.2.1+ on Windows: parallel `vkCreateGraphicsPipelines` calls corrupted the cache data, and Adrenalin crashed on `vkGetPipelineCacheData` for an empty cache. Both are known only from forum-thread titles; the AMD community site now redirects. [thread](https://community.amd.com/t5/opengl-vulkan/parallel-vkcreategraphicspipelines-calls-lead-to-corrupted/m-p/571884)
- Flutter/Impeller crashed on Snapdragon 845 with a corrupt cache file. The proposed fixes were validation, a hash check and atomic writes. [flutter#172624](https://github.com/flutter/flutter/issues/172624)

**Recommended wrapper header** ([zeux](https://zeux.io/2019/07/17/serializing-pipeline-cache/))

- Fields: magic, dataSize, dataHash, vendorID, deviceID, driverVersion, driverABI (pointer size) and UUID.
- Validate every field and the hash before handing the data to the driver.
- If validation fails or `vkCreatePipelineCache` returns an error, retry with no initial data.
- Write to a temp file, then rename. Save at steady state or on exit.
- Godot (4.1+) does the same. It writes `user://vulkan/pipelines.cache` from worker threads, triggered by growth in MB rather than on a timer, and waits for pending saves at shutdown. [godot#76348](https://github.com/godotengine/godot/pull/76348)

**Multi-GPU**

- Godot's cache wasn't prefixed by GPU, so switching GPUs invalidated it. [godot#81150](https://github.com/godotengine/godot/issues/81150)
- Key the file name by vendor, device and UUID so each GPU keeps its own cache.

**Driver implicit caches**

- RADV checks the app's cache first, then "fall[s] back to the on-disk cache" (since Mesa 17.3). [Phoronix](https://www.phoronix.com/news/RADV-Vulkan-Disk-Cache)
- Mesa's cache is `$XDG_CACHE_HOME/mesa_shader_cache`, 1 GB by default. [Mesa envvars](https://docs.mesa3d.org/envvars.html)
- NVIDIA on Linux uses `~/.cache/nvidia/GLCache` for both OpenGL and Vulkan. [NVIDIA README](https://download.nvidia.com/XFree86/Linux-x86_64/570.86.16/README/openglenvvariables.html) The 460 driver raised the default size from 128 MB to 1 GB. [dxvk#4014](https://github.com/doitsujin/dxvk/issues/4014)
- NVIDIA on Windows uses `%LOCALAPPDATA%\NVIDIA\DXCache`, with a size setting since 496.13. Forum sources only; unverified.
- Driver caches are shared, size-capped, and "usually deleted when the driver is updated". [Godot docs](https://docs.godotengine.org/en/stable/tutorials/performance/pipeline_compilations.html)
- An app cache is therefore still worth having: cheap, deterministic and under our control. On Mesa and NVIDIA it presumably adds less on top of the driver cache. The Khronos sample measured 24 ms with a cache vs 50 ms without. [Vulkan-Samples](https://docs.vulkan.org/samples/latest/samples/performance/pipeline_cache/README.html)

**Size limits**

- The spec defines no limit. Godot's TPS demo produced a 6.3 MB cache. [godot#76348](https://github.com/godotengine/godot/pull/76348)

## 2. Pipeline creation cache control (core in 1.3)

- **`FAIL_ON_PIPELINE_COMPILE_REQUIRED`:** creation returns `VK_PIPELINE_COMPILE_REQUIRED` instead of compiling.
- **`EARLY_RETURN_ON_FAILURE`:** stops a batched creation call at the first failure.
- **`EXTERNALLY_SYNCHRONIZED` (on the cache):** the driver can skip its internal locking.
- The extension exists so that "task-based game engines" can find expensive hazards before running into them. [refpage](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_pipeline_creation_cache_control.html)
- **How engines use it:** on the render thread, try creation with FAIL_ON. On a miss, queue the real compile on a worker and skip the draw or use a fallback meanwhile.
- Skipping the draw is Unreal's default for PSOs that aren't ready. [UE PSO precaching](https://dev.epicgames.com/documentation/en-us/unreal-engine/pso-precaching-for-unreal-engine)
- Per-thread caches with `EXTERNALLY_SYNCHRONIZED`, merged later, remove lock contention. [yosoygames](https://www.yosoygames.com.ar/wp/2024/09/why-does-vkmergepipelinecaches-exist/)
- NVIDIA has supported it since 442.75. [NVIDIA](https://developer.nvidia.com/vulkan-driver)

## 3. VK_KHR_pipeline_binary (released Aug 2024)

**What it is** ([Khronos blog](https://www.khronos.org/blog/bringing-explicit-pipeline-caching-control-to-vulkan), [proposal](https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_KHR_pipeline_binary.adoc))

- Each pipeline gets its own binary blobs instead of one opaque cache blob.
- The app stores three things:
  - a global key, which acts as the validity check across driver updates;
  - a map from pipeline key (`vkGetPipelineKeyKHR`) to binary keys;
  - a map from binary key to data.
- To capture, create the pipeline with `CAPTURE_DATA`. To reload, pass `VkPipelineBinaryInfoKHR`.
- The create info must match exactly.
- `VK_PIPELINE_BINARY_MISSING_KHR` is the internal-cache equivalent of a compile-required result.
- `pipelineBinaryPrefersInternalCache` means the app should not capture; the blog cites platforms like Steam here.
- The blog says ordinary apps can keep using VkPipelineCache for simplicity.

**Driver support**

| Driver | Status | Source |
|---|---|---|
| NVIDIA | 553.00 (Windows) / 550.40.70 (Linux) | [NVIDIA](https://developer.nvidia.com/vulkan-driver) |
| RADV | Merged days after release | [Phoronix](https://www.phoronix.com/news/Intel-ANV-Pipeline-Binary) |
| ANV and NVK | Mesa 26.0 | same |
| AMD Windows | Reportedly 24.9.1; unverified, the release-notes page timed out | [AMD RN](https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-24-9-1.html) |
| Intel Windows | Unknown | — |

Mesa older than 26.0 lacks it on Intel and NVK, so a fallback is mandatory.

## 4. Shader module identifier and graphics pipeline library

**VK_EXT_shader_module_identifier**

- Lets an app skip generating SPIR-V when the driver cache is warm: pass an identifier instead of the module, and the attempt has to use FAIL_ON_COMPILE_REQUIRED. [proposal](https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_shader_module_identifier.adoc)
- Check `shaderModuleIdentifierAlgorithmUUID` before trusting stored identifiers.
- Built for translation layers (>95% disk savings for D3D12-on-Vulkan).
- The flow is speculative, so it only helps if SPIR-V generation itself is the bottleneck.
- NVIDIA has supported it since 516.63. [NVIDIA](https://developer.nvidia.com/vulkan-driver)

**VK_EXT_graphics_pipeline_library (GPL)**

- Compiles stages at shader load time, then fast-links at draw time, with an optimized relink in the background. [Khronos](https://www.khronos.org/blog/reducing-draw-time-hitching-with-vk-ext-graphics-pipeline-library)
- Desktop vendors report fast linking.
- DXVK 2.7 removed its state cache as "largely unused since … GPL in DXVK 2.0". [DXVK 2.7](https://github.com/doitsujin/dxvk/releases/tag/v2.7)
- NVIDIA has supported GPL since 473.33. [NVIDIA](https://developer.nvidia.com/vulkan-driver)
- AMD Windows exposed it around 24.2.1, but DXVK reported problems. [dxvk#3859](https://github.com/doitsujin/dxvk/issues/3859)
- Khronos advises new engines to reconsider large permutation counts.
- It suits a renderer that can't predict full pipeline state, but it is a large change.

## 5. SPIR-V caching

**Godot** ([shader_rd.cpp](https://github.com/godotengine/godot/blob/master/servers/rendering/renderer_rd/shader_rd.cpp))

- The key is a SHA-256 over engine version and commit hash, stage sources and the debug-info flag, plus SHA-1 over defines, uniforms and code sections.
- Path: `name/groupSHA/sha1.<api>.cache`.
- Files start with a `GDSC` magic and a file-format version (4).
- A related issue asks for the cache folder to be versioned. [godot#63056](https://github.com/godotengine/godot/issues/63056)

**shaderc version**

- The C API exposes only `shaderc_get_spv_version`. [shaderc.h](https://github.com/google/shaderc/blob/main/libshaderc/include/shaderc/shaderc.h)
- glslang has `glslang::GetVersion()` in C++, which shaderc's C API does not expose. [glslang CHANGES](https://github.com/KhronosGroup/glslang/blob/main/CHANGES.md)
- Consequence (not documented practice): hash the loaded libshaderc binary, or its build tag, into the key.

**Options that change output** (same header): target env, SPIR-V version, optimization level, debug info, macros, auto-bind and include callbacks. All belong in the key.

**Caching preprocessed or reflection data**

- Not covered by the sources above.
- Consequence: cache reflection alongside the SPIR-V to avoid re-running reflection. Caching the preprocessed text only helps if the GLSL 330 translation itself is expensive.

## 6. Pre-warming

- **Unreal** has two approaches: bundled PSO caches record what gets drawn during play and ship that list; runtime precaching compiles PSOs asynchronously on load. [UE PSO caches](https://dev.epicgames.com/documentation/en-us/unreal-engine/optimizing-rendering-with-pso-caches-in-unreal-engine), [precaching](https://dev.epicgames.com/documentation/en-us/unreal-engine/pso-precaching-for-unreal-engine)
- **Godot 4.4** precompiles at load time and renders with an ubershader while the specialized pipeline compiles in the background. [Godot docs](https://docs.godotengine.org/en/stable/tutorials/performance/pipeline_compilations.html)
- **Steam/Fossilize** replays recorded pipeline state to warm VkPipelineCaches in the background. [Phoronix](https://www.phoronix.com/news/Steam-Vulkan-Shader-Pre-Cache)

## 7. Pitfalls for this renderer

- **Variants and mods:** content-hash the final translated source plus defines rather than using file names. A mod editing a shader then misses the cache instead of loading stale SPIR-V.
- **Settings combinations:** pipeline-key logs grow without bound; evict by LRU or last-seen time.
- **Two game instances:** both can load read-only. Each writes to a unique temp file and renames. Last writer wins, which loses entries but never corrupts. Optionally merge the on-disk cache into ours before saving.
- **Windows antivirus:** scanners briefly lock new files, which makes replace-by-rename fail. Chrome's fix was to retry `ReplaceFile`. [BleepingComputer](https://www.bleepingcomputer.com/news/security/google-chrome-fixes-antivirus-file-locking-bug-on-windows-10/) `ReplaceFileW` requires both files on the same volume. [MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- **Mesa:** the app cache and the driver's disk cache stack. Keep the app cache anyway, because users can disable or clear the Mesa cache. [Mesa envvars](https://docs.mesa3d.org/envvars.html)

## Design for this renderer

**Implement now**

1. **SPIR-V disk cache.**
   - **Key:** SHA-256 over:
     - a cache format version;
     - the translator version;
     - the libshaderc binary hash;
     - target env and SPIR-V version, optimization level, debug flag;
     - stage;
     - the sorted define list;
     - the final translated source, after includes are resolved.
   - **File:** `<cacheRoot>/spirv/<aa>/<hash>.spv`, with a small header: magic, format version, length, hash of the payload.
   - **On load:** check magic, length, hash and that the word count is a multiple of 4. On any failure, recompile and overwrite.
   - Store reflection data next to the SPIR-V.
   - Sources: [Godot](https://github.com/godotengine/godot/blob/master/servers/rendering/renderer_rd/shader_rd.cpp), [shaderc.h](https://github.com/google/shaderc/blob/main/libshaderc/include/shaderc/shaderc.h).

2. **Robust VkPipelineCache.**
   - **File:** `<cacheRoot>/pipeline/<vendor>-<device>-<uuidhex>.bin`.
   - **Header:** magic, format version, dataSize, 64-bit hash, vendorID, deviceID, driverVersion, pointer size, UUID.
   - **Validate** the wrapper and the embedded Vulkan header. Never pass size 0 with a non-null pointer.
   - **Fallback:** if validation fails or `vkCreatePipelineCache` errors, create an empty cache.
   - **Save** on shutdown, plus opportunistically from a worker when the cache has grown by N MB.
   - **Write safely:** unique temp file, then `File.Replace`/`File.Move(overwrite)`, retrying with backoff on IOException.
   - Sources: [zeux](https://zeux.io/2019/07/17/serializing-pipeline-cache/), [spec](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCacheCreateInfo.html), [godot#76348](https://github.com/godotengine/godot/pull/76348), [Chrome](https://www.bleepingcomputer.com/news/security/google-chrome-fixes-antivirus-file-locking-bug-on-windows-10/).

3. **Cache root.** `%LOCALAPPDATA%\<Game>\ShaderCache` on Windows, `$XDG_CACHE_HOME/<game>` on Linux. Per-user, outside the game install and mods folders.

4. **Async compile path.**
   - On the render thread, create with `FAIL_ON_PIPELINE_COMPILE_REQUIRED`.
   - On `VK_PIPELINE_COMPILE_REQUIRED`, enqueue a worker compile and skip that draw (or use a fallback) until the pipeline is ready.
   - Guard any per-worker caches that use `EXTERNALLY_SYNCHRONIZED`, and merge them into the main cache under a lock before saving.
   - Workaround for the AMD parallel-corruption reports: serialize `vkGetPipelineCacheData` and merges.
   - Sources: [cache_control](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_pipeline_creation_cache_control.html), [UE](https://dev.epicgames.com/documentation/en-us/unreal-engine/pso-precaching-for-unreal-engine), [yosoygames](https://www.yosoygames.com.ar/wp/2024/09/why-does-vkmergepipelinecaches-exist/).

**Next**

5. **Pipeline-key log for pre-warming.** Serialize used pipeline keys (program hash, vertex layout, formats, blend, polygon mode, topology), tagged with the settings/define hash. At startup, precompile matching entries on background threads. Cap the log with LRU. Sources: [UE bundled PSO](https://dev.epicgames.com/documentation/en-us/unreal-engine/optimizing-rendering-with-pso-caches-in-unreal-engine), [Godot](https://docs.godotengine.org/en/stable/tutorials/performance/pipeline_compilations.html).

**Defer**

6. **VK_KHR_pipeline_binary.** Optional backend when present, with VkPipelineCache as the fallback. Honor `pipelineBinaryPrefersInternalCache`. Coverage is still uneven: Mesa ANV and NVK only from 26.0, Intel Windows unknown. [Khronos](https://www.khronos.org/blog/bringing-explicit-pipeline-caching-control-to-vulkan), [Phoronix](https://www.phoronix.com/news/Intel-ANV-Pipeline-Binary)
7. **GPL (fast-link, then optimize in the background).** Only if stutter remains after items 1–5; it needs a pipeline architecture refactor. [Khronos GPL](https://www.khronos.org/blog/reducing-draw-time-hitching-with-vk-ext-graphics-pipeline-library)
8. **Skip VK_EXT_shader_module_identifier.** Once SPIR-V is cached, its benefit is marginal. [proposal](https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_shader_module_identifier.adoc)

**Uncertain / version-dependent**

- AMD Windows and Intel Windows `pipeline_binary` support.
- NVIDIA Windows DXCache defaults (forum sources only).
- Whether current drivers still have the header-validation bugs described in 2019.
- Hashing the libshaderc binary into the key is a consequence of the C API's limits, not documented practice.
