using System;
using System.Runtime.InteropServices;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The NGX result codes of <c>nvsdk_ngx_defs.h</c> (SDK 310.9.1, API 0x15).
/// Success is 1, not 0, and every failure is <c>0xBAD00000 | n</c>.
/// </summary>
internal enum NgxResult : uint
{
    Success = 0x1,
    Fail = 0xBAD00000,
    FailFeatureNotSupported = Fail | 1,
    FailPlatformError = Fail | 2,
    FailFeatureAlreadyExists = Fail | 3,
    FailFeatureNotFound = Fail | 4,
    FailInvalidParameter = Fail | 5,
    FailScratchBufferTooSmall = Fail | 6,
    FailNotInitialized = Fail | 7,
    FailUnsupportedInputFormat = Fail | 8,
    FailRWFlagMissing = Fail | 9,
    FailMissingInput = Fail | 10,
    FailUnableToInitializeFeature = Fail | 11,
    FailOutOfDate = Fail | 12,
    FailOutOfGPUMemory = Fail | 13,
    FailUnsupportedFormat = Fail | 14,
    FailUnableToWriteToAppDataPath = Fail | 15,
    FailUnsupportedParameter = Fail | 16,
    FailDenied = Fail | 17,
    FailNotImplemented = Fail | 18,

    // Optimum's own codes, shaped like NGX failures (top 12 bits 0xBAD, so
    // NVSDK_NGX_FAILED and Succeeded() treat them as failures) but in the
    // 0xBAD1 range the driver never uses. The first three are the shim's, and
    // are defined in native/optimum-ngx/optimum_ngx.h with these values.

    /// <summary>The shim loaded, but the NGX runtime could not be dlopen()ed.</summary>
    FailShimRuntimeMissing = 0xBAD10001,
    /// <summary>The NGX runtime loaded but does not export the entry point asked for.</summary>
    FailShimEntryPointMissing = 0xBAD10002,
    /// <summary>A null handle or output pointer reached the shim; nothing was called.</summary>
    FailShimInvalidArgument = 0xBAD10003,
    /// <summary>The shim itself is absent or has the wrong ABI version; see <see cref="NgxShim.Diagnosis" />.</summary>
    FailShimMissing = 0xBAD1FFFF,
}

/// <summary>NVSDK_NGX_Feature; only the two Optimum cares about are named.</summary>
internal enum NgxFeature
{
    SuperSampling = 1,
    FrameGeneration = 11,
    RayReconstruction = 13,
}

/// <summary>NVSDK_NGX_EngineType. Optimum is CUSTOM: no NVIDIA-issued application id.</summary>
internal enum NgxEngineType
{
    Custom = 0,
    Unreal = 1,
    Unity = 2,
    Omniverse = 3,
}

/// <summary>NVSDK_NGX_PerfQuality_Value, in header order.</summary>
internal enum NgxPerfQuality
{
    MaxPerf = 0,
    Balanced = 1,
    MaxQuality = 2,
    UltraPerformance = 3,
    UltraQuality = 4,
    Dlaa = 5,
}

/// <summary>
/// NVSDK_NGX_Feature_Support_Result: a bitfield, 0 meaning "supported".
/// </summary>
[Flags]
internal enum NgxFeatureSupport
{
    Supported = 0,
    CheckNotPresent = 1,
    DriverVersionUnsupported = 2,
    AdapterUnsupported = 4,
    OsVersionBelowMinimum = 8,
    NotImplemented = 16,
}

/// <summary>NVSDK_NGX_PathListInfo. <c>Path</c> is <c>wchar_t const* const*</c> on
/// both operating systems, so on Linux the strings are UTF-32, not UTF-8.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxPathListInfo
{
    public IntPtr Path;
    public uint Length;
}

/// <summary>NVSDK_NGX_LoggingInfo (SDK version 0x14 and later).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxLoggingInfo
{
    public IntPtr LoggingCallback;
    public int MinimumLoggingLevel;
    public byte DisableOtherLoggingSinks;
}

/// <summary>NVSDK_NGX_FeatureCommonInfo: where NGX looks for the feature
/// libraries besides the application directory.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxFeatureCommonInfo
{
    public NgxPathListInfo PathListInfo;
    public IntPtr InternalData;
    public NgxLoggingInfo LoggingInfo;
}

/// <summary>NVSDK_NGX_ProjectIdDescription.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxProjectIdDescription
{
    public IntPtr ProjectId;
    public NgxEngineType EngineType;
    public IntPtr EngineVersion;
}

/// <summary>
/// NVSDK_NGX_Application_Identifier. The union is 24 bytes (the project
/// description) at offset 8, so the sequential layout below is byte-identical.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxApplicationIdentifier
{
    /// <summary>0 = application id, 1 = project id. Optimum always uses 1.</summary>
    public int IdentifierType;
    public NgxProjectIdDescription ProjectDesc;
}

/// <summary>NVSDK_NGX_FeatureDiscoveryInfo: what the pre-init queries take.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxFeatureDiscoveryInfo
{
    /// <summary>NVSDK_NGX_Version_API = <see cref="NgxInterop.VersionApi" />.</summary>
    public int SdkVersion;
    public NgxFeature FeatureId;
    public NgxApplicationIdentifier Identifier;
    /// <summary>wchar_t* (UTF-32 on Linux).</summary>
    public IntPtr ApplicationDataPath;
    /// <summary>const NVSDK_NGX_FeatureCommonInfo*.</summary>
    public IntPtr FeatureInfo;
}

/// <summary>NVSDK_NGX_FeatureRequirement.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NgxFeatureRequirement
{
    public NgxFeatureSupport FeatureSupported;
    public uint MinHwArchitecture;
    public fixed byte MinOsVersion[255];
}

/// <summary>VkExtensionProperties, declared here so the NGX surface does not
/// depend on which Silk.NET namespace happens to be imported.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NgxExtensionProperties
{
    public fixed byte ExtensionName[256];
    public uint SpecVersion;

    public string Name
    {
        get
        {
            fixed (byte* name = ExtensionName) return Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        }
    }
}

/// <summary>
/// The Vulkan NGX entry points as Optimum calls them: through
/// <see cref="NgxShim" /> (<c>native/optimum-ngx</c>), which is a real shared
/// object and therefore a call site NGX accepts. The <c>Direct*</c> members
/// below P/Invoke the driver's <c>libnvidia-ngx.so.1</c> without the shim and
/// exist only for diagnosis behind
/// <see cref="AllowDirectCallsVariable" />: on Linux they take the process
/// down (see <see cref="ManagedCallSiteIsSupported" />).
///
/// Two entry points differ from the header prototypes, because the header
/// declares the SDK-side wrapper and the driver exports the core function the
/// wrapper forwards to. The difference was read out of the wrapper's
/// disassembly (<c>nvsdk_ngx_vulkan_lib.o</c>), not guessed:
/// <c>NVSDK_NGX_VULKAN_Init_with_ProjectID(..., Device, GIPA, GDPA,
/// FeatureInfo, SDKVersion)</c> tail-calls
/// <c>NVSDK_NGX_VULKAN_Init_ProjectID(..., Device, SDKVersion, FeatureInfo)</c>
/// when GIPA and GDPA are null - no function-pointer arguments, and the last
/// two arguments swapped. Everything else is a pass-through.
/// </summary>
internal static unsafe class NgxInterop
{
    /// <summary>The NVIDIA driver library that exports NVSDK_NGX_VULKAN_*.</summary>
    public const string LibraryName = "libnvidia-ngx.so.1";

    /// <summary>NVSDK_NGX_VERSION_API_MACRO for SDK 310.9.1.</summary>
    public const int VersionApi = 0x0000015;

    /// <summary>
    /// Bypasses the shim and P/Invokes the driver directly, for diagnosis only.
    /// On Linux this takes the process down; see
    /// <see cref="ManagedCallSiteIsSupported" />.
    /// </summary>
    public const string AllowDirectCallsVariable = "OPTIMUM_NGX_ALLOW_DIRECT_CALLS";

    /// <summary>Whether the diagnostic direct path is switched on for this process.</summary>
    public static bool UseDirectCalls =>
        Environment.GetEnvironmentVariable(AllowDirectCallsVariable) == "1";

    /// <summary>Whether the driver library can be loaded at all.</summary>
    public static bool IsDriverLibraryPresent()
    {
        if (!NativeLibrary.TryLoad(LibraryName, out IntPtr handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>
    /// Whether NGX may be called from managed code in this process.
    ///
    /// On Linux it may not, and this is the spike's central result (2026-09-12,
    /// driver 615.71.09). libnvidia-ngx resolves the calling module from its own
    /// return address; when that address is not inside a loaded ELF object it
    /// builds a std::wstring from a null path and aborts the process with
    /// <c>terminate called after throwing an instance of 'std::logic_error' /
    /// what(): basic_string::_M_construct null not valid</c>. A .NET P/Invoke
    /// stub is JIT-compiled into anonymous memory, so every direct call from C#
    /// lands there.
    ///
    /// It is the call site, not our marshalling, that decides: the identical
    /// calls with the identical structs succeed from a C executable
    /// (<c>Init_ProjectID</c> = Success, DLSS SR and DLSS-G both Available) and
    /// abort from the same executable as soon as they are reached through a
    /// trampoline in an anonymous <c>mmap</c> page - which is exactly what the
    /// JIT stub is. Init and the pre-init discovery queries both abort.
    ///
    /// So NGX needs a call site inside a real shared object, and since
    /// 2026-09-12 it has one: <c>native/optimum-ngx</c>, bound by
    /// <see cref="NgxShim" />. This property is therefore no longer "is the
    /// platform capable" but "can this process reach NGX at all" - the shim is
    /// loadable and speaks the expected ABI version, or the diagnostic direct
    /// path is switched on with <see cref="AllowDirectCallsVariable" /> (which
    /// still takes the process down on Linux, and is kept only so the finding
    /// above can be reproduced).
    /// </summary>
    public static bool ManagedCallSiteIsSupported => NgxShim.IsAvailable || UseDirectCalls;

    /// <summary>One line for a log or a test report saying how NGX is reached, or why it is not.</summary>
    public static string ManagedCallSiteDiagnosis => UseDirectCalls
        ? AllowDirectCallsVariable + "=1: calling " + LibraryName + " directly, bypassing the shim. " +
          "On Linux this aborts the process inside NGX - diagnosis only."
        : NgxShim.IsAvailable
            ? "NGX is reached through the native shim: " + NgxShim.Diagnosis
            : "NGX is unreachable from this process: " + NgxShim.Diagnosis + " libnvidia-ngx resolves its " +
              "caller's module from the return address and aborts when that is a JIT stub in anonymous " +
              "memory, which is what every .NET P/Invoke call site is, so the shim is not optional.";

    // --------------------------------------------------------------- dispatch
    //
    // Every entry point below goes through the shim. The Direct* twins keep the
    // old behaviour for OPTIMUM_NGX_ALLOW_DIRECT_CALLS=1, which exists only to
    // reproduce the spike's finding.

    public static NgxResult InitProjectId(
        IntPtr projectId,
        NgxEngineType engineType,
        IntPtr engineVersion,
        IntPtr applicationDataPath,
        IntPtr instance,
        IntPtr physicalDevice,
        IntPtr device,
        int sdkVersion,
        NgxFeatureCommonInfo* featureInfo) => UseDirectCalls
        ? DirectInitProjectId(projectId, engineType, engineVersion, applicationDataPath,
            instance, physicalDevice, device, sdkVersion, featureInfo)
        : NgxShim.IsAvailable
            ? NgxShim.InitProjectId(projectId, engineType, engineVersion, applicationDataPath,
                instance, physicalDevice, device, sdkVersion, featureInfo)
            : NgxResult.FailShimMissing;

    public static NgxResult Shutdown1(IntPtr device) => UseDirectCalls
        ? DirectShutdown1(device)
        : NgxShim.IsAvailable ? NgxShim.Shutdown(device) : NgxResult.FailShimMissing;

    public static NgxResult GetCapabilityParameters(out IntPtr parameters)
    {
        if (UseDirectCalls) return DirectGetCapabilityParameters(out parameters);
        if (NgxShim.IsAvailable) return NgxShim.GetCapabilityParameters(out parameters);
        parameters = IntPtr.Zero;
        return NgxResult.FailShimMissing;
    }

    public static NgxResult AllocateParameters(out IntPtr parameters)
    {
        if (UseDirectCalls) return DirectAllocateParameters(out parameters);
        if (NgxShim.IsAvailable) return NgxShim.AllocateParameters(out parameters);
        parameters = IntPtr.Zero;
        return NgxResult.FailShimMissing;
    }

    public static NgxResult DestroyParameters(IntPtr parameters) => UseDirectCalls
        ? DirectDestroyParameters(parameters)
        : NgxShim.IsAvailable ? NgxShim.DestroyParameters(parameters) : NgxResult.FailShimMissing;

    public static NgxResult GetFeatureRequirements(
        IntPtr instance, IntPtr physicalDevice,
        NgxFeatureDiscoveryInfo* discovery, NgxFeatureRequirement* outSupported) => UseDirectCalls
        ? DirectGetFeatureRequirements(instance, physicalDevice, discovery, outSupported)
        : NgxShim.IsAvailable
            ? NgxShim.GetFeatureRequirements(instance, physicalDevice, discovery, outSupported)
            : NgxResult.FailShimMissing;

    public static NgxResult GetFeatureInstanceExtensionRequirements(
        NgxFeatureDiscoveryInfo* discovery,
        uint* outExtensionCount,
        NgxExtensionProperties** outExtensionProperties) => UseDirectCalls
        ? DirectGetFeatureInstanceExtensionRequirements(discovery, outExtensionCount, outExtensionProperties)
        : NgxShim.IsAvailable
            ? NgxShim.GetFeatureInstanceExtensionRequirements(
                discovery, outExtensionCount, outExtensionProperties)
            : NgxResult.FailShimMissing;

    public static NgxResult GetFeatureDeviceExtensionRequirements(
        IntPtr instance,
        IntPtr physicalDevice,
        NgxFeatureDiscoveryInfo* discovery,
        uint* outExtensionCount,
        NgxExtensionProperties** outExtensionProperties) => UseDirectCalls
        ? DirectGetFeatureDeviceExtensionRequirements(
            instance, physicalDevice, discovery, outExtensionCount, outExtensionProperties)
        : NgxShim.IsAvailable
            ? NgxShim.GetFeatureDeviceExtensionRequirements(
                instance, physicalDevice, discovery, outExtensionCount, outExtensionProperties)
            : NgxResult.FailShimMissing;

    // ------------------------------------------------- the diagnostic direct path

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_Init_ProjectID",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectInitProjectId(
        IntPtr projectId,
        NgxEngineType engineType,
        IntPtr engineVersion,
        IntPtr applicationDataPath,
        IntPtr instance,
        IntPtr physicalDevice,
        IntPtr device,
        int sdkVersion,
        NgxFeatureCommonInfo* featureInfo);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_Shutdown1",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectShutdown1(IntPtr device);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetCapabilityParameters",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectGetCapabilityParameters(out IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_AllocateParameters",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectAllocateParameters(out IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_DestroyParameters",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectDestroyParameters(IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectGetFeatureRequirements(
        IntPtr instance,
        IntPtr physicalDevice,
        NgxFeatureDiscoveryInfo* discovery,
        NgxFeatureRequirement* outSupported);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectGetFeatureInstanceExtensionRequirements(
        NgxFeatureDiscoveryInfo* discovery,
        uint* outExtensionCount,
        NgxExtensionProperties** outExtensionProperties);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern NgxResult DirectGetFeatureDeviceExtensionRequirements(
        IntPtr instance,
        IntPtr physicalDevice,
        NgxFeatureDiscoveryInfo* discovery,
        uint* outExtensionCount,
        NgxExtensionProperties** outExtensionProperties);

    /// <summary>The header's own NVSDK_NGX_SUCCEED.</summary>
    public static bool Succeeded(NgxResult result) => ((uint)result & 0xFFF00000u) != (uint)NgxResult.Fail;

    /// <summary>Result code plus what <c>nvsdk_ngx_defs.h</c> says it means.</summary>
    public static string Describe(NgxResult result) => result switch
    {
        NgxResult.Success => "Success (0x1)",
        NgxResult.FailFeatureNotSupported =>
            "FAIL_FeatureNotSupported (0xBAD00001): not supported by this system, hardware or graphics API",
        NgxResult.FailPlatformError =>
            "FAIL_PlatformError (0xBAD00002): error inside the graphics API, OS or a system library such as NvAPI",
        NgxResult.FailFeatureAlreadyExists => "FAIL_FeatureAlreadyExists (0xBAD00003)",
        NgxResult.FailFeatureNotFound => "FAIL_FeatureNotFound (0xBAD00004)",
        NgxResult.FailInvalidParameter =>
            "FAIL_InvalidParameter (0xBAD00005): a parameter had the wrong value or type, or was missing",
        NgxResult.FailScratchBufferTooSmall => "FAIL_ScratchBufferTooSmall (0xBAD00006)",
        NgxResult.FailNotInitialized => "FAIL_NotInitialized (0xBAD00007): NVSDK_NGX_Init was not called first",
        NgxResult.FailUnsupportedInputFormat => "FAIL_UnsupportedInputFormat (0xBAD00008)",
        NgxResult.FailRWFlagMissing => "FAIL_RWFlagMissing (0xBAD00009)",
        NgxResult.FailMissingInput => "FAIL_MissingInput (0xBAD0000A)",
        NgxResult.FailUnableToInitializeFeature => "FAIL_UnableToInitializeFeature (0xBAD0000B)",
        NgxResult.FailOutOfDate =>
            "FAIL_OutOfDate (0xBAD0000C): the driver or the feature library is too old for this API call",
        NgxResult.FailOutOfGPUMemory => "FAIL_OutOfGPUMemory (0xBAD0000D)",
        NgxResult.FailUnsupportedFormat => "FAIL_UnsupportedFormat (0xBAD0000E)",
        NgxResult.FailUnableToWriteToAppDataPath =>
            "FAIL_UnableToWriteToAppDataPath (0xBAD0000F): the application data path is not writable",
        NgxResult.FailUnsupportedParameter => "FAIL_UnsupportedParameter (0xBAD00010)",
        NgxResult.FailDenied => "FAIL_Denied (0xBAD00011)",
        NgxResult.FailNotImplemented => "FAIL_NotImplemented (0xBAD00012)",
        NgxResult.FailShimRuntimeMissing =>
            "shim: the NGX runtime (" + LibraryName + ") could not be loaded (0xBAD10001)",
        NgxResult.FailShimEntryPointMissing =>
            "shim: the NGX runtime does not export this entry point (0xBAD10002)",
        NgxResult.FailShimInvalidArgument =>
            "shim: a null handle or output pointer; nothing was called (0xBAD10003)",
        NgxResult.FailShimMissing =>
            "shim: " + NgxShim.LibraryName + " is absent or has the wrong ABI version (0xBAD1FFFF)",
        _ => "0x" + ((uint)result).ToString("X8"),
    };
}
