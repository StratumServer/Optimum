/*
 * Optimum's NGX shim: a flat C ABI in front of NVIDIA's NGX runtime.
 *
 * Why this file exists (DLSS spike, 2026-09-12, driver 615.71.09): NGX resolves
 * its caller's module from its own return address. A .NET P/Invoke stub is JIT
 * compiled into anonymous memory, so the lookup yields a null path and NGX
 * aborts the process inside libstdc++ ("basic_string::_M_construct null not
 * valid"). The fix is a call site inside a real shared object - this one. The
 * managed side calls libOptimumNgx.so, and libOptimumNgx.so calls NGX.
 *
 * Rules this ABI keeps:
 *  - Nothing here throws, aborts or dereferences a null handle. Every entry
 *    point returns an NVSDK_NGX_Result; the shim adds its own codes for
 *    "runtime not present" and "entry point missing".
 *  - The NGX runtime is dlopen()ed at first use and every entry point is
 *    dlsym()ed, so this file builds and loads on machines with no NVIDIA
 *    driver and carries no link-time dependency on one.
 *  - Structs are never redeclared here. Everything NGX reads through a pointer
 *    (NVSDK_NGX_FeatureCommonInfo, NVSDK_NGX_FeatureDiscoveryInfo,
 *    NVSDK_NGX_FeatureRequirement) is laid out by the managed side and passed
 *    straight through, which is the layout the spike verified.
 */
#ifndef OPTIMUM_NGX_H
#define OPTIMUM_NGX_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define OPTIMUM_NGX_API __declspec(dllexport)
#else
#  define OPTIMUM_NGX_API __attribute__((visibility("default")))
#endif

/*
 * The shim ABI version. Bump it whenever an exported signature changes; the
 * managed side compares it against NgxShim.ExpectedVersion and refuses to call
 * a shim it does not recognise, so a stale libOptimumNgx.so beside the renderer
 * is a clean "unavailable" rather than a mis-called function.
 */
#define OPTIMUM_NGX_SHIM_VERSION 1u

/*
 * The shim's own result codes. They are shaped like NVSDK_NGX_Result failures
 * (top 12 bits 0xBAD) so NVSDK_NGX_FAILED and the managed Succeeded() treat
 * them as failures, but use 0xBAD1xxxx so they can never collide with the
 * driver's own 0xBAD0xxxx codes.
 */
#define OPTIMUM_NGX_RESULT_RUNTIME_MISSING     0xBAD10001u /* the NGX runtime could not be loaded */
#define OPTIMUM_NGX_RESULT_ENTRY_POINT_MISSING 0xBAD10002u /* the runtime loaded but lacks this symbol */
#define OPTIMUM_NGX_RESULT_INVALID_ARGUMENT    0xBAD10003u /* a null handle or output pointer */

typedef uint32_t OptimumNgxResult;

/* ---- shim itself ------------------------------------------------------- */

/** OPTIMUM_NGX_SHIM_VERSION of this build. Always safe to call. */
OPTIMUM_NGX_API uint32_t OptimumNgx_Version(void);

/**
 * Loads the NGX runtime if it is not loaded yet.
 * Returns NVSDK_NGX_Result_Success (1) or OPTIMUM_NGX_RESULT_RUNTIME_MISSING.
 * Idempotent, and called implicitly by every other entry point.
 */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_LoadRuntime(void);

/** The runtime file name this build looks for ("libnvidia-ngx.so.1" / "nvngx.dll"). */
OPTIMUM_NGX_API const char *OptimumNgx_RuntimeName(void);

/** dlerror()/GetLastError() text from the last failed load, or "" - never null. */
OPTIMUM_NGX_API const char *OptimumNgx_LastLoadError(void);

/* ---- lifetime ---------------------------------------------------------- */

/**
 * NVSDK_NGX_VULKAN_Init_ProjectID as the driver really exports it: no
 * vkGet*ProcAddr arguments, sdkVersion before featureInfo. (The header declares
 * the SDK-side wrapper, which tail-calls this.)
 */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_VulkanInitProjectId(
    const char *projectId, int engineType, const char *engineVersion,
    const void *applicationDataPath, /* wchar_t*, opaque here */
    void *instance, void *physicalDevice, void *device,
    int sdkVersion, const void *featureCommonInfo);

/** NVSDK_NGX_VULKAN_Shutdown1. */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_VulkanShutdown(void *device);

/* ---- parameter blocks -------------------------------------------------- */

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_GetCapabilityParameters(void **outParameters);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_AllocateParameters(void **outParameters);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_DestroyParameters(void *parameters);

/* ---- discovery --------------------------------------------------------- */

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_GetFeatureRequirements(
    void *instance, void *physicalDevice, const void *discovery, void *outRequirement);

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_GetFeatureInstanceExtensionRequirements(
    const void *discovery, uint32_t *outCount, void **outExtensionProperties);

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_GetFeatureDeviceExtensionRequirements(
    void *instance, void *physicalDevice, const void *discovery,
    uint32_t *outCount, void **outExtensionProperties);

/* ---- parameter accessors ----------------------------------------------- */
/*
 * NVSDK_NGX_Parameter is a C++ abstract class with no virtual destructor and
 * the driver exports no C accessors for it, so these dispatch through the
 * object's vtable in the declaration order of nvsdk_ngx_params.h. The dispatch
 * lives here so no managed code ever has to hold a vtable slot number.
 */

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetULongLong(void *p, const char *name, uint64_t value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetFloat(void *p, const char *name, float value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetDouble(void *p, const char *name, double value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetUInt(void *p, const char *name, uint32_t value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetInt(void *p, const char *name, int32_t value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterSetVoidPointer(void *p, const char *name, void *value);

OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetULongLong(void *p, const char *name, uint64_t *value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetFloat(void *p, const char *name, float *value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetDouble(void *p, const char *name, double *value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetUInt(void *p, const char *name, uint32_t *value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetInt(void *p, const char *name, int32_t *value);
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ParameterGetVoidPointer(void *p, const char *name, void **value);

/* ---- features ---------------------------------------------------------- */

/** NVSDK_NGX_VULKAN_CreateFeature(cmdBuffer, featureId, parameters, &handle). */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_CreateFeature(
    void *commandBuffer, int featureId, void *parameters, void **outHandle);

/** NVSDK_NGX_VULKAN_CreateFeature1(device, cmdBuffer, featureId, parameters, &handle). */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_CreateFeature1(
    void *device, void *commandBuffer, int featureId, void *parameters, void **outHandle);

/** NVSDK_NGX_VULKAN_EvaluateFeature(cmdBuffer, handle, parameters, progressCallback). */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_EvaluateFeature(
    void *commandBuffer, void *handle, void *parameters, void *progressCallback);

/** NVSDK_NGX_VULKAN_ReleaseFeature(handle). */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_ReleaseFeature(void *handle);

/* ---- DLSS optimal settings --------------------------------------------- */

/**
 * NGX_DLSS_GET_OPTIMAL_SETTINGS from nvsdk_ngx_helpers.h, which is inline C in
 * the SDK: it pulls a function pointer out of the capability parameters and
 * calls it. That call has the same return-address problem as every other NGX
 * call, so it is made from here too.
 *
 * Every out pointer may be null. Returns FAIL_OutOfDate when the parameter
 * block carries no callback (out-of-date feature library, or parameters from
 * AllocateParameters instead of GetCapabilityParameters), exactly as the
 * header's helper does.
 */
OPTIMUM_NGX_API OptimumNgxResult OptimumNgx_DlssGetOptimalSettings(
    void *parameters, uint32_t displayWidth, uint32_t displayHeight, int perfQuality,
    uint32_t *outOptimalWidth, uint32_t *outOptimalHeight,
    uint32_t *outMinWidth, uint32_t *outMinHeight,
    uint32_t *outMaxWidth, uint32_t *outMaxHeight,
    float *outSharpness);

#ifdef __cplusplus
}
#endif

#endif /* OPTIMUM_NGX_H */
