/*
 * Optimum's NGX shim. See optimum_ngx.h for why it exists and what it promises.
 *
 * Plain C99, no C++ runtime, no NGX headers, no link-time dependency on the
 * NVIDIA driver: the runtime is dlopen()ed and every entry point dlsym()ed at
 * first use, so this object builds and loads on any machine.
 *
 *   cc -std=c99 -O2 -fPIC -shared -fvisibility=hidden \
 *      native/optimum-ngx/optimum_ngx.c -o libOptimumNgx.so -ldl
 *
 * (`make native`, and a target in Optimum.Render.Vulkan.csproj, do this.)
 */
#include "optimum_ngx.h"

#include <stddef.h>
#include <string.h>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
   typedef HMODULE optimum_module;
#  define OPTIMUM_NGX_RUNTIME "nvngx.dll"
#else
#  include <dlfcn.h>
   typedef void *optimum_module;
#  define OPTIMUM_NGX_RUNTIME "libnvidia-ngx.so.1"
#endif

/* NVSDK_NGX_Result_Success. Success is 1, not 0. */
#define NGX_SUCCESS 1u

/* ------------------------------------------------------------------ loading */

/*
 * The entry points the managed side needs, by exported name. Everything is
 * resolved once; a symbol the runtime does not export stays null and its
 * wrapper answers OPTIMUM_NGX_RESULT_ENTRY_POINT_MISSING instead of crashing.
 */
#define OPTIMUM_NGX_ENTRY_POINTS(X)                                                    \
    X(init_project_id,      "NVSDK_NGX_VULKAN_Init_ProjectID")                         \
    X(shutdown1,            "NVSDK_NGX_VULKAN_Shutdown1")                              \
    X(get_capability_params,"NVSDK_NGX_VULKAN_GetCapabilityParameters")                \
    X(allocate_params,      "NVSDK_NGX_VULKAN_AllocateParameters")                     \
    X(destroy_params,       "NVSDK_NGX_VULKAN_DestroyParameters")                      \
    X(feature_requirements, "NVSDK_NGX_VULKAN_GetFeatureRequirements")                 \
    X(instance_extensions,  "NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements")\
    X(device_extensions,    "NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements")  \
    X(create_feature,       "NVSDK_NGX_VULKAN_CreateFeature")                          \
    X(create_feature1,      "NVSDK_NGX_VULKAN_CreateFeature1")                         \
    X(evaluate_feature,     "NVSDK_NGX_VULKAN_EvaluateFeature")                        \
    X(release_feature,      "NVSDK_NGX_VULKAN_ReleaseFeature")

struct optimum_ngx_entries
{
#define OPTIMUM_NGX_DECLARE(field, name) void *field;
    OPTIMUM_NGX_ENTRY_POINTS(OPTIMUM_NGX_DECLARE)
#undef OPTIMUM_NGX_DECLARE
};

enum { LOAD_UNTRIED = 0, LOAD_RUNNING = 1, LOAD_READY = 2, LOAD_FAILED = 3 };

static int g_load_state = LOAD_UNTRIED;
static optimum_module g_runtime = NULL;
static struct optimum_ngx_entries g_ngx;
static char g_load_error[512] = { 0 };

static void *optimum_symbol(optimum_module module, const char *name)
{
#if defined(_WIN32)
    return (void *)GetProcAddress(module, name);
#else
    return dlsym(module, name);
#endif
}

static void optimum_record_load_error(void)
{
#if defined(_WIN32)
    unsigned long code = GetLastError();
    /* No FormatMessage: a number is enough to tell "not found" from "bad image". */
    const char prefix[] = "LoadLibrary failed, GetLastError=";
    size_t at = sizeof prefix - 1;
    memcpy(g_load_error, prefix, at);
    if (at + 12 < sizeof g_load_error)
    {
        char digits[12];
        int n = 0;
        do { digits[n++] = (char)('0' + (code % 10u)); code /= 10u; } while (code != 0 && n < 11);
        while (n > 0) g_load_error[at++] = digits[--n];
    }
    g_load_error[at] = '\0';
#else
    const char *message = dlerror();
    if (message == NULL) message = "the runtime could not be loaded";
    strncpy(g_load_error, message, sizeof g_load_error - 1);
    g_load_error[sizeof g_load_error - 1] = '\0';
#endif
}

/*
 * Loads once. A second thread arriving mid-load spins on the state word rather
 * than taking a lock, which keeps this file free of pthread and of any
 * platform-specific synchronisation; the load happens once at renderer start.
 */
static int optimum_ensure_runtime(void)
{
    int state = __atomic_load_n(&g_load_state, __ATOMIC_ACQUIRE);
    while (state == LOAD_RUNNING)
    {
        state = __atomic_load_n(&g_load_state, __ATOMIC_ACQUIRE);
    }
    if (state == LOAD_READY) return 1;
    if (state == LOAD_FAILED) return 0;

    int expected = LOAD_UNTRIED;
    if (!__atomic_compare_exchange_n(&g_load_state, &expected, LOAD_RUNNING, 0,
                                     __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE))
    {
        /* Someone else got there first; re-enter and observe their result. */
        return optimum_ensure_runtime();
    }

#if defined(_WIN32)
    g_runtime = LoadLibraryA(OPTIMUM_NGX_RUNTIME);
#else
    dlerror();
    g_runtime = dlopen(OPTIMUM_NGX_RUNTIME, RTLD_NOW | RTLD_LOCAL);
#endif
    if (g_runtime == NULL)
    {
        optimum_record_load_error();
        __atomic_store_n(&g_load_state, LOAD_FAILED, __ATOMIC_RELEASE);
        return 0;
    }

#define OPTIMUM_NGX_RESOLVE(field, name) g_ngx.field = optimum_symbol(g_runtime, name);
    OPTIMUM_NGX_ENTRY_POINTS(OPTIMUM_NGX_RESOLVE)
#undef OPTIMUM_NGX_RESOLVE

    __atomic_store_n(&g_load_state, LOAD_READY, __ATOMIC_RELEASE);
    return 1;
}

/* Every wrapper starts with this: no runtime, no symbol, no call. */
#define OPTIMUM_NGX_REQUIRE(field)                                       \
    if (!optimum_ensure_runtime()) return OPTIMUM_NGX_RESULT_RUNTIME_MISSING; \
    if (g_ngx.field == NULL) return OPTIMUM_NGX_RESULT_ENTRY_POINT_MISSING;

/*
 * ...and every wrapper *ends* with this, which is the whole point of the file.
 *
 * `return f(args);` compiles to a tail call (`jmp *%rax`) at -O2: the wrapper
 * pops its own frame first, so the return address NGX reads is not this shared
 * object's - it is the managed caller's JIT stub, and NGX aborts exactly as it
 * does without a shim. Measured, not feared: the first build of this file
 * forwarded with plain `return` and the managed test still died with
 * "basic_string::_M_construct null not valid" (2026-09-12).
 *
 * Writing the result to a volatile local after the call forbids the tail call
 * in the language rather than in a build flag, so the guarantee survives being
 * compiled with different flags or a different compiler. build.sh also passes
 * -fno-optimize-sibling-calls, belt and braces.
 */
#define OPTIMUM_NGX_RETURN(expression)             \
    do {                                           \
        volatile OptimumNgxResult _result = (expression); \
        return _result;                            \
    } while (0)

uint32_t OptimumNgx_Version(void) { return OPTIMUM_NGX_SHIM_VERSION; }

const char *OptimumNgx_RuntimeName(void) { return OPTIMUM_NGX_RUNTIME; }

const char *OptimumNgx_LastLoadError(void) { return g_load_error; }

OptimumNgxResult OptimumNgx_LoadRuntime(void)
{
    return optimum_ensure_runtime() ? NGX_SUCCESS : OPTIMUM_NGX_RESULT_RUNTIME_MISSING;
}

/* ----------------------------------------------------------------- lifetime */

typedef OptimumNgxResult (*pfn_init_project_id)(
    const char *, int, const char *, const void *, void *, void *, void *, int, const void *);
typedef OptimumNgxResult (*pfn_handle)(void *);
/*
 * NVSDK_NGX_VULKAN_Shutdown1 as the driver really implements it.
 *
 * The public header (nvsdk_ngx_vk.h) declares one parameter, VkDevice. The
 * implementation in libnvidia-ngx.so.1 (driver 615.71.09) takes two: it moves
 * its *second* argument straight into the fourth argument of the internal
 * shutdown routine, and that routine stores the SDK's remaining reference count
 * through it without ever testing it for NULL:
 *
 *     NVSDK_NGX_VULKAN_Shutdown1:  mov %rsi,%rcx  ...  jmp <internal shutdown>
 *     internal shutdown:           mov %rcx,0x10(%rsp)
 *                                  ...
 *                                  mov 0x10(%rsp),%rsi
 *                                  mov %eax,(%rsi)      <-- the SIGSEGV
 *
 * The deprecated one-argument NVSDK_NGX_VULKAN_Shutdown passes `lea 0xc(%rsp)`
 * there, which is what the count is meant to land in. Called through the
 * header's prototype, %rsi holds whatever the caller happened to leave in it -
 * a writable address by luck (a C test harness), or 8192 (the .NET client and
 * the test host, every time), and then NGX writes four bytes to address 0x2000
 * and the process dies inside the driver. That is the crash both core dumps
 * show, on the *first* Shutdown1 of the process.
 *
 * So the shim always passes a real int* it owns. An extra register argument is
 * harmless if a future driver really does take one parameter, and it is the
 * only thing that makes the call defined on this one.
 */
typedef OptimumNgxResult (*pfn_shutdown1)(void *, int *);
typedef OptimumNgxResult (*pfn_out_pointer)(void **);

OptimumNgxResult OptimumNgx_VulkanInitProjectId(
    const char *projectId, int engineType, const char *engineVersion,
    const void *applicationDataPath, void *instance, void *physicalDevice, void *device,
    int sdkVersion, const void *featureCommonInfo)
{
    OPTIMUM_NGX_REQUIRE(init_project_id)
    if (projectId == NULL || device == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    OPTIMUM_NGX_RETURN(((pfn_init_project_id)g_ngx.init_project_id)(
        projectId, engineType, engineVersion, applicationDataPath,
        instance, physicalDevice, device, sdkVersion, featureCommonInfo));
}

OptimumNgxResult OptimumNgx_VulkanShutdown(void *device)
{
    /* Written by NGX with the SDK's remaining reference count; see pfn_shutdown1. */
    int remainingReferences = 0;
    OPTIMUM_NGX_REQUIRE(shutdown1)
    OPTIMUM_NGX_RETURN(((pfn_shutdown1)g_ngx.shutdown1)(device, &remainingReferences));
}

/* --------------------------------------------------------- parameter blocks */

OptimumNgxResult OptimumNgx_GetCapabilityParameters(void **outParameters)
{
    OPTIMUM_NGX_REQUIRE(get_capability_params)
    if (outParameters == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outParameters = NULL;
    OPTIMUM_NGX_RETURN(((pfn_out_pointer)g_ngx.get_capability_params)(outParameters));
}

OptimumNgxResult OptimumNgx_AllocateParameters(void **outParameters)
{
    OPTIMUM_NGX_REQUIRE(allocate_params)
    if (outParameters == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outParameters = NULL;
    OPTIMUM_NGX_RETURN(((pfn_out_pointer)g_ngx.allocate_params)(outParameters));
}

OptimumNgxResult OptimumNgx_DestroyParameters(void *parameters)
{
    OPTIMUM_NGX_REQUIRE(destroy_params)
    if (parameters == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    OPTIMUM_NGX_RETURN(((pfn_handle)g_ngx.destroy_params)(parameters));
}

/* ---------------------------------------------------------------- discovery */

typedef OptimumNgxResult (*pfn_feature_requirements)(void *, void *, const void *, void *);
typedef OptimumNgxResult (*pfn_instance_extensions)(const void *, uint32_t *, void **);
typedef OptimumNgxResult (*pfn_device_extensions)(void *, void *, const void *, uint32_t *, void **);

OptimumNgxResult OptimumNgx_GetFeatureRequirements(
    void *instance, void *physicalDevice, const void *discovery, void *outRequirement)
{
    OPTIMUM_NGX_REQUIRE(feature_requirements)
    if (discovery == NULL || outRequirement == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    OPTIMUM_NGX_RETURN(((pfn_feature_requirements)g_ngx.feature_requirements)(
        instance, physicalDevice, discovery, outRequirement));
}

OptimumNgxResult OptimumNgx_GetFeatureInstanceExtensionRequirements(
    const void *discovery, uint32_t *outCount, void **outExtensionProperties)
{
    OPTIMUM_NGX_REQUIRE(instance_extensions)
    if (discovery == NULL || outCount == NULL || outExtensionProperties == NULL)
        return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outCount = 0;
    *outExtensionProperties = NULL;
    OPTIMUM_NGX_RETURN(((pfn_instance_extensions)g_ngx.instance_extensions)(
        discovery, outCount, outExtensionProperties));
}

OptimumNgxResult OptimumNgx_GetFeatureDeviceExtensionRequirements(
    void *instance, void *physicalDevice, const void *discovery,
    uint32_t *outCount, void **outExtensionProperties)
{
    OPTIMUM_NGX_REQUIRE(device_extensions)
    if (discovery == NULL || outCount == NULL || outExtensionProperties == NULL)
        return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outCount = 0;
    *outExtensionProperties = NULL;
    OPTIMUM_NGX_RETURN(((pfn_device_extensions)g_ngx.device_extensions)(
        instance, physicalDevice, discovery, outCount, outExtensionProperties));
}

/* ------------------------------------------------------ parameter accessors */

/*
 * NVSDK_NGX_Parameter's vtable, in the declaration order of
 * nvsdk_ngx_params.h. There is no virtual destructor, so slot 0 is the first
 * Set overload. Under the Itanium C++ ABI the first pointer-sized word of the
 * object is the vptr and `this` is the first argument, which is exactly what
 * the C calls below do.
 */
enum
{
    SLOT_SET_ULL   = 0,
    SLOT_SET_F     = 1,
    SLOT_SET_D     = 2,
    SLOT_SET_UI    = 3,
    SLOT_SET_I     = 4,
    SLOT_SET_D3D11 = 5,
    SLOT_SET_D3D12 = 6,
    SLOT_SET_VP    = 7,
    SLOT_GET_ULL   = 8,
    SLOT_GET_F     = 9,
    SLOT_GET_D     = 10,
    SLOT_GET_UI    = 11,
    SLOT_GET_I     = 12,
    SLOT_GET_D3D11 = 13,
    SLOT_GET_D3D12 = 14,
    SLOT_GET_VP    = 15
};

#define VTABLE(p) (*(void ***)(p))

#define OPTIMUM_NGX_SETTER(suffix, ctype, slot)                                  \
    OptimumNgxResult OptimumNgx_ParameterSet##suffix(                            \
        void *p, const char *name, ctype value)                                  \
    {                                                                            \
        typedef void (*setter)(void *, const char *, ctype);                      \
        if (p == NULL || name == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;\
        ((setter)VTABLE(p)[slot])(p, name, value);                               \
        /* not a tail call: a value is returned after it. */                      \
        return NGX_SUCCESS;                                                      \
    }

#define OPTIMUM_NGX_GETTER(suffix, ctype, slot)                                  \
    OptimumNgxResult OptimumNgx_ParameterGet##suffix(                            \
        void *p, const char *name, ctype *value)                                 \
    {                                                                            \
        typedef OptimumNgxResult (*getter)(void *, const char *, ctype *);        \
        if (p == NULL || name == NULL || value == NULL)                          \
            return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;                          \
        OPTIMUM_NGX_RETURN(((getter)VTABLE(p)[slot])(p, name, value));           \
    }

OPTIMUM_NGX_SETTER(ULongLong, uint64_t, SLOT_SET_ULL)
OPTIMUM_NGX_SETTER(Float, float, SLOT_SET_F)
OPTIMUM_NGX_SETTER(Double, double, SLOT_SET_D)
OPTIMUM_NGX_SETTER(UInt, uint32_t, SLOT_SET_UI)
OPTIMUM_NGX_SETTER(Int, int32_t, SLOT_SET_I)
OPTIMUM_NGX_SETTER(VoidPointer, void *, SLOT_SET_VP)

OPTIMUM_NGX_GETTER(ULongLong, uint64_t, SLOT_GET_ULL)
OPTIMUM_NGX_GETTER(Float, float, SLOT_GET_F)
OPTIMUM_NGX_GETTER(Double, double, SLOT_GET_D)
OPTIMUM_NGX_GETTER(UInt, uint32_t, SLOT_GET_UI)
OPTIMUM_NGX_GETTER(Int, int32_t, SLOT_GET_I)

OptimumNgxResult OptimumNgx_ParameterGetVoidPointer(void *p, const char *name, void **value)
{
    typedef OptimumNgxResult (*getter)(void *, const char *, void **);
    if (p == NULL || name == NULL || value == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *value = NULL;
    OPTIMUM_NGX_RETURN(((getter)VTABLE(p)[SLOT_GET_VP])(p, name, value));
}

/* ----------------------------------------------------------------- features */

typedef OptimumNgxResult (*pfn_create_feature)(void *, int, void *, void **);
typedef OptimumNgxResult (*pfn_create_feature1)(void *, void *, int, void *, void **);
typedef OptimumNgxResult (*pfn_evaluate_feature)(void *, void *, void *, void *);

OptimumNgxResult OptimumNgx_CreateFeature(
    void *commandBuffer, int featureId, void *parameters, void **outHandle)
{
    OPTIMUM_NGX_REQUIRE(create_feature)
    if (parameters == NULL || outHandle == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outHandle = NULL;
    OPTIMUM_NGX_RETURN(((pfn_create_feature)g_ngx.create_feature)(
        commandBuffer, featureId, parameters, outHandle));
}

OptimumNgxResult OptimumNgx_CreateFeature1(
    void *device, void *commandBuffer, int featureId, void *parameters, void **outHandle)
{
    OPTIMUM_NGX_REQUIRE(create_feature1)
    if (parameters == NULL || outHandle == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    *outHandle = NULL;
    OPTIMUM_NGX_RETURN(((pfn_create_feature1)g_ngx.create_feature1)(
        device, commandBuffer, featureId, parameters, outHandle));
}

OptimumNgxResult OptimumNgx_EvaluateFeature(
    void *commandBuffer, void *handle, void *parameters, void *progressCallback)
{
    OPTIMUM_NGX_REQUIRE(evaluate_feature)
    if (handle == NULL || parameters == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    OPTIMUM_NGX_RETURN(((pfn_evaluate_feature)g_ngx.evaluate_feature)(
        commandBuffer, handle, parameters, progressCallback));
}

OptimumNgxResult OptimumNgx_ReleaseFeature(void *handle)
{
    OPTIMUM_NGX_REQUIRE(release_feature)
    if (handle == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;
    OPTIMUM_NGX_RETURN(((pfn_handle)g_ngx.release_feature)(handle));
}

/* --------------------------------------------------- DLSS optimal settings */

/* NVSDK_NGX_Result_FAIL_OutOfDate, what the header's helper returns with no callback. */
#define NGX_FAIL_OUT_OF_DATE 0xBAD0000Cu

static uint32_t optimum_get_uint_or(void *p, const char *name, uint32_t fallback)
{
    uint32_t value = 0;
    OptimumNgxResult result = OptimumNgx_ParameterGetUInt(p, name, &value);
    return (result == NGX_SUCCESS) ? value : fallback;
}

OptimumNgxResult OptimumNgx_DlssGetOptimalSettings(
    void *parameters, uint32_t displayWidth, uint32_t displayHeight, int perfQuality,
    uint32_t *outOptimalWidth, uint32_t *outOptimalHeight,
    uint32_t *outMinWidth, uint32_t *outMinHeight,
    uint32_t *outMaxWidth, uint32_t *outMaxHeight,
    float *outSharpness)
{
    typedef OptimumNgxResult (*pfn_optimal)(void *);

    if (parameters == NULL) return OPTIMUM_NGX_RESULT_INVALID_ARGUMENT;

    void *callback = NULL;
    OptimumNgxResult lookup =
        OptimumNgx_ParameterGetVoidPointer(parameters, "DLSSOptimalSettingsCallback", &callback);
    if (callback == NULL) return (lookup == NGX_SUCCESS) ? NGX_FAIL_OUT_OF_DATE : lookup;

    OptimumNgx_ParameterSetUInt(parameters, "Width", displayWidth);
    OptimumNgx_ParameterSetUInt(parameters, "Height", displayHeight);
    OptimumNgx_ParameterSetInt(parameters, "PerfQualityValue", perfQuality);
    OptimumNgx_ParameterSetInt(parameters, "RTXValue", 0);

    /* The callback lives inside libnvidia-ngx, so it reads the return address
       the same way; the volatile keeps this frame alive across it. */
    volatile OptimumNgxResult called = ((pfn_optimal)callback)(parameters);
    OptimumNgxResult result = called;
    if (result != NGX_SUCCESS) return result;

    uint32_t optimalWidth = optimum_get_uint_or(parameters, "OutWidth", 0);
    uint32_t optimalHeight = optimum_get_uint_or(parameters, "OutHeight", 0);

    if (outOptimalWidth) *outOptimalWidth = optimalWidth;
    if (outOptimalHeight) *outOptimalHeight = optimalHeight;
    if (outMinWidth) *outMinWidth = optimum_get_uint_or(parameters, "DLSS.Get.Dynamic.Min.Render.Width", optimalWidth);
    if (outMinHeight) *outMinHeight = optimum_get_uint_or(parameters, "DLSS.Get.Dynamic.Min.Render.Height", optimalHeight);
    if (outMaxWidth) *outMaxWidth = optimum_get_uint_or(parameters, "DLSS.Get.Dynamic.Max.Render.Width", optimalWidth);
    if (outMaxHeight) *outMaxHeight = optimum_get_uint_or(parameters, "DLSS.Get.Dynamic.Max.Render.Height", optimalHeight);
    if (outSharpness)
    {
        float sharpness = 0.0f;
        if (OptimumNgx_ParameterGetFloat(parameters, "Sharpness", &sharpness) != NGX_SUCCESS) sharpness = 0.0f;
        *outSharpness = sharpness;
    }
    return result;
}
