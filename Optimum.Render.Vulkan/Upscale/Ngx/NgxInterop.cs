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
/// The Vulkan NGX entry points, P/Invoked straight into the NVIDIA Linux
/// driver's own <c>libnvidia-ngx.so.1</c>, which exports all 21 of them
/// (<c>nm -D</c>). The SDK's static <c>libnvsdk_ngx.a</c> is only a dlopen
/// shim around exactly these symbols, so linking it would buy nothing and cost
/// a native build step.
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

    /// <summary>Forces the direct calls on for a re-test; see <see cref="ManagedCallSiteIsSupported" />.</summary>
    public const string AllowDirectCallsVariable = "OPTIMUM_NGX_ALLOW_DIRECT_CALLS";

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
    /// So NGX needs a call site inside a real shared object. Until Optimum has
    /// one, the paths below degrade instead of calling, and the extension
    /// requirements come from the SDK's own fallback lists (which is what this
    /// driver makes the SDK use anyway - see <see cref="NgxSession" />).
    /// Set <see cref="AllowDirectCallsVariable" /> to 1 to re-test the direct
    /// path; it will take the process down until the driver or the call site
    /// changes.
    /// </summary>
    public static bool ManagedCallSiteIsSupported =>
        !OperatingSystem.IsLinux() ||
        Environment.GetEnvironmentVariable(AllowDirectCallsVariable) == "1";

    /// <summary>One line for a log or a test report saying why, when it is not supported.</summary>
    public static string ManagedCallSiteDiagnosis => ManagedCallSiteIsSupported
        ? "NGX may be called directly from managed code in this process."
        : "libnvidia-ngx resolves its caller's module from the return address and aborts the process " +
          "when that is a JIT stub in anonymous memory, which is what every .NET P/Invoke call site is; " +
          "NGX needs a call site inside a loaded shared object (set " + AllowDirectCallsVariable +
          "=1 to re-test).";

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_Init_ProjectID",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult InitProjectId(
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
    public static extern NgxResult Shutdown1(IntPtr device);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetCapabilityParameters",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult GetCapabilityParameters(out IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_AllocateParameters",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult AllocateParameters(out IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_DestroyParameters",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult DestroyParameters(IntPtr parameters);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult GetFeatureRequirements(
        IntPtr instance,
        IntPtr physicalDevice,
        NgxFeatureDiscoveryInfo* discovery,
        NgxFeatureRequirement* outSupported);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult GetFeatureInstanceExtensionRequirements(
        NgxFeatureDiscoveryInfo* discovery,
        uint* outExtensionCount,
        NgxExtensionProperties** outExtensionProperties);

    [DllImport(LibraryName, EntryPoint = "NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern NgxResult GetFeatureDeviceExtensionRequirements(
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
        _ => "0x" + ((uint)result).ToString("X8"),
    };
}
