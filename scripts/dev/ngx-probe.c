/*
 * Optimum DLSS spike (2026-09-12): what NGX answers on native Linux Vulkan, and
 * why the same calls cannot be made from C#.
 *
 * libnvidia-ngx.so.1 resolves its caller's module from its own return address.
 * From a normal native call site every entry point works; reached through a
 * trampoline in an anonymous mmap page - which is exactly what a .NET P/Invoke
 * stub is - Init_ProjectID and the pre-init discovery queries abort the process
 * with "basic_string::_M_construct null not valid". That is what the second
 * argument switches between, and it is the whole reason NgxInterop degrades
 * instead of calling on Linux (see NgxInterop.ManagedCallSiteIsSupported).
 *
 * No Vulkan or NGX headers are needed: the few structs are declared here with
 * the layouts nvsdk_ngx_defs.h implies, and the Vulkan entry points are taken
 * from libvulkan by dlsym. The NGX feature libraries are NVIDIA
 * redistributables and are not in this repository; pass the directory holding
 * libnvidia-ngx-dlss.so.* and libnvidia-ngx-dlssg.so.* as the first argument.
 *
 *   cc -O0 -g scripts/dev/ngx-probe.c -o /tmp/ngx-probe -ldl
 *   /tmp/ngx-probe <feature library dir> 0      # native call site: the answers
 *   /tmp/ngx-probe <feature library dir> 1 1    # JIT-like call site: aborts
 *
 * Recorded on an RTX 4070 Laptop, driver 615.71.09, X11, SDK 310.9.1:
 *   SuperSampling requirements    Success, FeatureSupported=0, MinHWArchitecture=0x160
 *   FrameGeneration requirements  Success, FeatureSupported=0, MinHWArchitecture=0x190
 *   Init_ProjectID (engine CUSTOM, our own GUID)  Success
 *   SuperSampling.Available=1     min driver 470.0
 *   FrameGeneration.Available=1   min driver 520.0
 *   optimal 2560x1490 MaxQuality  1707x993, MaxPerf 1280x745, dynamic 1280x745..2560x1490
 *   the per-feature instance and device extension queries: FAIL_NotImplemented
 */
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <dlfcn.h>
#include <stdint.h>
#include <sys/mman.h>

typedef struct { const wchar_t *const *Path; unsigned Length; } PathListInfo;
typedef struct { void *cb; int level; unsigned char disable; } LoggingInfo;
typedef struct { PathListInfo pl; void *internal; LoggingInfo log; } FeatureCommonInfo;
typedef struct { const char *ProjectId; int EngineType; const char *EngineVersion; } ProjectIdDesc;
typedef struct { int type; ProjectIdDesc desc; } AppId;
typedef struct { int sdk; int feature; AppId id; const wchar_t *dataPath; const FeatureCommonInfo *info; } Discovery;
typedef struct { char name[256]; unsigned spec; } ExtProps;
typedef struct { int supported; unsigned minArch; char minOs[255]; } FeatureRequirement;

typedef struct { int sType; const void *pNext; const char *an; uint32_t av; const char *en; uint32_t ev; uint32_t api; } AppInfo;
typedef struct { int sType; const void *pNext; uint32_t flags; const AppInfo *ai; uint32_t lc; const char *const *l; uint32_t ec; const char *const *e; } ICI;
typedef struct { int sType; const void *pNext; uint32_t flags; uint32_t family; uint32_t count; const float *prio; } DQCI;
typedef struct { int sType; const void *pNext; uint32_t flags; uint32_t qc; const DQCI *q; uint32_t lc; const char *const *l; uint32_t ec; const char *const *e; const void *feat; } DCI;

static void *tramp(void *target)
{
    unsigned char code[] = { 0x48,0x83,0xEC,0x08, 0x49,0xBB,0,0,0,0,0,0,0,0, 0x41,0xFF,0xD3, 0x48,0x83,0xC4,0x08, 0xC3 };
    memcpy(code + 6, &target, 8);
    void *p = mmap(NULL, 4096, PROT_READ|PROT_WRITE|PROT_EXEC, MAP_PRIVATE|MAP_ANONYMOUS, -1, 0);
    memcpy(p, code, sizeof code);
    return p;
}

/* NVSDK_NGX_Parameter vtable slots (nvsdk_ngx_params.h declaration order). */
typedef void (*SetUI)(void *, const char *, unsigned);
typedef void (*SetI)(void *, const char *, int);
typedef unsigned (*GetUI)(void *, const char *, unsigned *);
typedef unsigned (*GetI)(void *, const char *, int *);
typedef unsigned (*GetF)(void *, const char *, float *);
typedef unsigned (*GetVP)(void *, const char *, void **);
#define VT(p) (*(void ***)(p))
static void setui(void *p, const char *n, unsigned v) { ((SetUI)VT(p)[3])(p, n, v); }
static void seti(void *p, const char *n, int v) { ((SetI)VT(p)[4])(p, n, v); }
static unsigned getui(void *p, const char *n, unsigned *v) { return ((GetUI)VT(p)[11])(p, n, v); }
static unsigned geti(void *p, const char *n, int *v) { return ((GetI)VT(p)[12])(p, n, v); }
static unsigned getf(void *p, const char *n, float *v) { return ((GetF)VT(p)[9])(p, n, v); }
static unsigned getvp(void *p, const char *n, void **v) { return ((GetVP)VT(p)[15])(p, n, v); }

static void dumpui(void *p, const char *n)
{
    unsigned v = 0; unsigned r = getui(p, n, &v);
    if (r == 1) printf("  %-48s = %u\n", n, v); else printf("  %-48s : 0x%08X\n", n, r);
}
static void dumpi(void *p, const char *n)
{
    int v = 0; unsigned r = geti(p, n, &v);
    if (r == 1) printf("  %-48s = %d (0x%08X)\n", n, v, (unsigned)v); else printf("  %-48s : 0x%08X\n", n, r);
}

int main(int argc, char **argv)
{
    const char *dir = argv[1];
    int useTramp = argc > 2 ? atoi(argv[2]) : 0;

    void *vk = dlopen("libvulkan.so.1", RTLD_NOW);
    unsigned (*ci)(const ICI *, void *, void **) = dlsym(vk, "vkCreateInstance");
    unsigned (*epd)(void *, uint32_t *, void **) = dlsym(vk, "vkEnumeratePhysicalDevices");
    unsigned (*cd)(void *, const DCI *, void *, void **) = dlsym(vk, "vkCreateDevice");
    void (*gpdp)(void *, void *) = dlsym(vk, "vkGetPhysicalDeviceProperties");
    void (*dd)(void *, void *) = dlsym(vk, "vkDestroyDevice");

    AppInfo ai = { 0, NULL, "optimum-ngx-spike", 1, "Optimum", 1, (1u<<22)|(3u<<12) };
    const char *iexts[1] = { "VK_KHR_get_physical_device_properties2" };
    ICI ici = { 1, NULL, 0, &ai, 0, NULL, 1, iexts };
    void *inst = NULL;
    printf("vkCreateInstance = %u\n", ci(&ici, NULL, &inst));
    uint32_t n = 0; epd(inst, &n, NULL); void *pds[8]; if (n > 8) n = 8; epd(inst, &n, pds);
    void *pd = NULL;
    char props[1024];
    for (uint32_t i = 0; i < n; i++) {
        memset(props, 0, sizeof props);
        gpdp(pds[i], props);
        uint32_t vendor = *(uint32_t *)(props + 8);
        /* VkPhysicalDeviceProperties: apiVersion 0, driverVersion 4, vendorID 8,
           deviceID 12, deviceType 16, deviceName 20. */
        printf("physical device %u: vendor 0x%04X '%s'\n", i, vendor, props + 20);
        if (vendor == 0x10DE && !pd) pd = pds[i];
    }
    if (!pd) { printf("no NVIDIA physical device\n"); return 1; }

    float prio = 1.0f;
    DQCI q = { 2, NULL, 0, 0, 1, &prio };
    const char *dexts[4] = { "VK_NVX_binary_import", "VK_NVX_image_view_handle",
                             "VK_KHR_buffer_device_address", "VK_KHR_push_descriptor" };
    DCI dci = { 3, NULL, 0, 1, &q, 0, NULL, 4, dexts, NULL };
    void *dev = NULL;
    unsigned rd = cd(pd, &dci, NULL, &dev);
    printf("vkCreateDevice = %u\n", rd);
    if (rd != 0) return 1;
    fflush(stdout);

    void *ngx = dlopen("libnvidia-ngx.so.1", RTLD_NOW);
    #define SYM(name) void *name##_raw = dlsym(ngx, #name); void *name##_p = useTramp ? tramp(name##_raw) : name##_raw;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_GetFeatureRequirements)(void *, void *, const Discovery *, FeatureRequirement *); SYM(NVSDK_NGX_VULKAN_GetFeatureRequirements); T_NVSDK_NGX_VULKAN_GetFeatureRequirements NVSDK_NGX_VULKAN_GetFeatureRequirements = (T_NVSDK_NGX_VULKAN_GetFeatureRequirements)NVSDK_NGX_VULKAN_GetFeatureRequirements_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements)(void *, void *, const Discovery *, unsigned *, ExtProps **); SYM(NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements); T_NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements = (T_NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements)NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements)(const Discovery *, unsigned *, ExtProps **); SYM(NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements); T_NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements = (T_NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements)NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_Init_ProjectID)(const char *, int, const char *, const wchar_t *, void *, void *, void *, int, const FeatureCommonInfo *); SYM(NVSDK_NGX_VULKAN_Init_ProjectID); T_NVSDK_NGX_VULKAN_Init_ProjectID NVSDK_NGX_VULKAN_Init_ProjectID = (T_NVSDK_NGX_VULKAN_Init_ProjectID)NVSDK_NGX_VULKAN_Init_ProjectID_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_GetCapabilityParameters)(void **); SYM(NVSDK_NGX_VULKAN_GetCapabilityParameters); T_NVSDK_NGX_VULKAN_GetCapabilityParameters NVSDK_NGX_VULKAN_GetCapabilityParameters = (T_NVSDK_NGX_VULKAN_GetCapabilityParameters)NVSDK_NGX_VULKAN_GetCapabilityParameters_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_DestroyParameters)(void *); SYM(NVSDK_NGX_VULKAN_DestroyParameters); T_NVSDK_NGX_VULKAN_DestroyParameters NVSDK_NGX_VULKAN_DestroyParameters = (T_NVSDK_NGX_VULKAN_DestroyParameters)NVSDK_NGX_VULKAN_DestroyParameters_p;
    typedef unsigned (*T_NVSDK_NGX_VULKAN_Shutdown1)(void *); SYM(NVSDK_NGX_VULKAN_Shutdown1); T_NVSDK_NGX_VULKAN_Shutdown1 NVSDK_NGX_VULKAN_Shutdown1 = (T_NVSDK_NGX_VULKAN_Shutdown1)NVSDK_NGX_VULKAN_Shutdown1_p;

    printf("call site: %s\n", useTramp ? "anonymous mmap trampoline (what a .NET P/Invoke stub looks like)"
                                       : "the executable's own .text");
    fflush(stdout);

    wchar_t wdir[2048]; mbstowcs(wdir, dir, 2048);
    const wchar_t *paths[1] = { wdir };
    FeatureCommonInfo info; memset(&info, 0, sizeof info);
    info.pl.Path = paths; info.pl.Length = 1;
    wchar_t wdata[2048]; mbstowcs(wdata, "/tmp/ngxspike", 2048);
    system("mkdir -p /tmp/ngxspike");

    Discovery d; memset(&d, 0, sizeof d);
    d.sdk = 0x0000015; d.id.type = 1;
    d.id.desc.ProjectId = "6f8f6ad4-3f47-4a2c-9c1e-1a5f2c0d7b31";
    d.id.desc.EngineType = 0; d.id.desc.EngineVersion = "1.0.0";
    d.dataPath = wdata; d.info = &info;

    int skipDiscovery = argc > 3 ? atoi(argv[3]) : 0;
    int features[2] = { 1, 11 };
    const char *fname[2] = { "SuperSampling (DLSS SR)", "FrameGeneration (DLSS-G)" };
    for (int i = 0; i < 2 && !skipDiscovery; i++) {
        d.feature = features[i];
        unsigned c = 0; ExtProps *p = NULL;
        unsigned r = NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements(&d, &c, &p);
        printf("%s instance extensions: 0x%08X count=%u\n", fname[i], r, c);
        for (unsigned j = 0; j < c && p; j++) printf("    %s (rev %u)\n", p[j].name, p[j].spec);
        c = 0; p = NULL;
        r = NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements(inst, pd, &d, &c, &p);
        printf("%s device extensions: 0x%08X count=%u\n", fname[i], r, c);
        for (unsigned j = 0; j < c && p; j++) printf("    %s (rev %u)\n", p[j].name, p[j].spec);
        FeatureRequirement req; memset(&req, 0, sizeof req);
        r = NVSDK_NGX_VULKAN_GetFeatureRequirements(inst, pd, &d, &req);
        printf("%s requirements: 0x%08X FeatureSupported=%d MinHWArchitecture=0x%X MinOSVersion='%s'\n",
               fname[i], r, req.supported, req.minArch, req.minOs);
        fflush(stdout);
    }

    unsigned init = NVSDK_NGX_VULKAN_Init_ProjectID(
        d.id.desc.ProjectId, 0, d.id.desc.EngineVersion, wdata, inst, pd, dev, 0x0000015, &info);
    printf("NVSDK_NGX_VULKAN_Init_ProjectID = 0x%08X\n", init);
    fflush(stdout);
    if (init != 1) { dd(dev, NULL); return 1; }

    void *params = NULL;
    unsigned cp = NVSDK_NGX_VULKAN_GetCapabilityParameters(&params);
    printf("NVSDK_NGX_VULKAN_GetCapabilityParameters = 0x%08X\n", cp);
    if (cp == 1) {
        dumpui(params, "SuperSampling.Available");
        dumpui(params, "SuperSampling.NeedsUpdatedDriver");
        dumpui(params, "SuperSampling.MinDriverVersionMajor");
        dumpui(params, "SuperSampling.MinDriverVersionMinor");
        dumpi (params, "SuperSampling.FeatureInitResult");
        dumpui(params, "FrameGeneration.Available");
        dumpui(params, "FrameGeneration.NeedsUpdatedDriver");
        dumpui(params, "FrameGeneration.MinDriverVersionMajor");
        dumpui(params, "FrameGeneration.MinDriverVersionMinor");
        dumpi (params, "FrameGeneration.FeatureInitResult");
        dumpui(params, "FrameInterpolation.Available");
        dumpui(params, "FrameInterpolation.NeedsUpdatedDriver");
        dumpui(params, "FrameInterpolation.MinDriverVersionMajor");
        dumpui(params, "FrameInterpolation.MinDriverVersionMinor");

        void *cb = NULL;
        unsigned rc = getvp(params, "DLSSOptimalSettingsCallback", &cb);
        printf("  DLSSOptimalSettingsCallback: 0x%08X ptr=%p\n", rc, cb);
        if (cb) {
            int q[2] = { 2, 0 }; const char *qn[2] = { "MaxQuality", "MaxPerf" };
            for (int i = 0; i < 2; i++) {
                setui(params, "Width", 2560);
                setui(params, "Height", 1490);
                seti(params, "PerfQualityValue", q[i]);
                seti(params, "RTXValue", 0);
                unsigned r = ((unsigned (*)(void *))(useTramp ? tramp(cb) : cb))(params);
                unsigned ow=0, oh=0, maxw=0, maxh=0, minw=0, minh=0; float sharp = 0;
                getui(params, "OutWidth", &ow); getui(params, "OutHeight", &oh);
                getui(params, "DLSS.Get.Dynamic.Max.Render.Width", &maxw);
                getui(params, "DLSS.Get.Dynamic.Max.Render.Height", &maxh);
                getui(params, "DLSS.Get.Dynamic.Min.Render.Width", &minw);
                getui(params, "DLSS.Get.Dynamic.Min.Render.Height", &minh);
                getf(params, "Sharpness", &sharp);
                printf("  optimal settings 2560x1490 %s: 0x%08X -> %ux%u (min %ux%u, max %ux%u, sharpness %.3f)\n",
                       qn[i], r, ow, oh, minw, minh, maxw, maxh, sharp);
            }
        }
        printf("NVSDK_NGX_VULKAN_DestroyParameters = 0x%08X\n", NVSDK_NGX_VULKAN_DestroyParameters(params));
    }
    printf("NVSDK_NGX_VULKAN_Shutdown1 = 0x%08X\n", NVSDK_NGX_VULKAN_Shutdown1(dev));
    dd(dev, NULL);
    printf("done\n");
    return 0;
}
