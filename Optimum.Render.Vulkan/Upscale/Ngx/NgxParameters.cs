using System;
using System.Runtime.InteropServices;

namespace Optimum.Render.Vulkan.Core;

/// <summary>The parameter-name strings of <c>nvsdk_ngx_defs.h</c> and
/// <c>nvsdk_ngx_defs_dlssg.h</c> that the availability spike reads.</summary>
internal static class NgxParameterNames
{
    public const string SuperSamplingAvailable = "SuperSampling.Available";
    public const string SuperSamplingNeedsUpdatedDriver = "SuperSampling.NeedsUpdatedDriver";
    public const string SuperSamplingMinDriverVersionMajor = "SuperSampling.MinDriverVersionMajor";
    public const string SuperSamplingMinDriverVersionMinor = "SuperSampling.MinDriverVersionMinor";
    public const string SuperSamplingFeatureInitResult = "SuperSampling.FeatureInitResult";

    public const string FrameGenerationAvailable = "FrameGeneration.Available";
    public const string FrameGenerationNeedsUpdatedDriver = "FrameGeneration.NeedsUpdatedDriver";
    public const string FrameGenerationMinDriverVersionMajor = "FrameGeneration.MinDriverVersionMajor";
    public const string FrameGenerationMinDriverVersionMinor = "FrameGeneration.MinDriverVersionMinor";
    public const string FrameGenerationFeatureInitResult = "FrameGeneration.FeatureInitResult";

    public const string FrameInterpolationAvailable = "FrameInterpolation.Available";
    public const string FrameInterpolationNeedsUpdatedDriver = "FrameInterpolation.NeedsUpdatedDriver";
    public const string FrameInterpolationMinDriverVersionMajor = "FrameInterpolation.MinDriverVersionMajor";
    public const string FrameInterpolationMinDriverVersionMinor = "FrameInterpolation.MinDriverVersionMinor";

    public const string Width = "Width";
    public const string Height = "Height";
    public const string OutWidth = "OutWidth";
    public const string OutHeight = "OutHeight";
    public const string Sharpness = "Sharpness";
    public const string PerfQualityValue = "PerfQualityValue";
    public const string RtxValue = "RTXValue";
    public const string DlssOptimalSettingsCallback = "DLSSOptimalSettingsCallback";
    public const string DlssGetDynamicMaxRenderWidth = "DLSS.Get.Dynamic.Max.Render.Width";
    public const string DlssGetDynamicMaxRenderHeight = "DLSS.Get.Dynamic.Max.Render.Height";
    public const string DlssGetDynamicMinRenderWidth = "DLSS.Get.Dynamic.Min.Render.Width";
    public const string DlssGetDynamicMinRenderHeight = "DLSS.Get.Dynamic.Min.Render.Height";
}

/// <summary>
/// An <c>NVSDK_NGX_Parameter*</c>.
///
/// The type is a C++ abstract class with no virtual destructor
/// (<c>nvsdk_ngx_params.h</c>), and the C accessors NVIDIA documents
/// (<c>NVSDK_NGX_Parameter_SetUI</c> and friends) live in the SDK's static
/// library, not in the driver's <c>libnvidia-ngx.so.1</c>. So the calls go
/// through the object's own vtable: under the Itanium C++ ABI the first
/// pointer-sized word of the object is the vptr, and the overloads occupy the
/// slots below in declaration order, with <c>this</c> as the first argument in
/// the ordinary SysV register order.
/// </summary>
internal readonly unsafe struct NgxParameters
{
    // Declaration order of nvsdk_ngx_params.h: eight Set overloads, eight Get
    // overloads, Reset. No virtual destructor is declared, so slot 0 is Set(ULL).
    private const int SlotSetUlonglong = 0;
    private const int SlotSetFloat = 1;
    private const int SlotSetDouble = 2;
    private const int SlotSetUint = 3;
    private const int SlotSetInt = 4;
    private const int SlotSetVoidPointer = 7;
    private const int SlotGetUlonglong = 8;
    private const int SlotGetFloat = 9;
    private const int SlotGetDouble = 10;
    private const int SlotGetUint = 11;
    private const int SlotGetInt = 12;
    private const int SlotGetVoidPointer = 15;

    public NgxParameters(IntPtr handle) => Handle = handle;

    public IntPtr Handle { get; }

    public bool IsNull => Handle == IntPtr.Zero;

    private void** Vtable => *(void***)Handle;

    public void SetUInt(string name, uint value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)Vtable[SlotSetUint])(Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public void SetInt(string name, int value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)Vtable[SlotSetInt])(Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public void SetFloat(string name, float value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float, void>)Vtable[SlotSetFloat])(Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public void SetDouble(string name, double value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double, void>)Vtable[SlotSetDouble])(Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public void SetULong(string name, ulong value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, void>)Vtable[SlotSetUlonglong])(Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public void SetVoidPointer(string name, IntPtr value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)Vtable[SlotSetVoidPointer])(
                Handle, utf8, value);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetUInt(string name, out uint value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            uint result = 0;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint*, NgxResult>)
                Vtable[SlotGetUint])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetInt(string name, out int value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            int result = 0;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int*, NgxResult>)
                Vtable[SlotGetInt])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetFloat(string name, out float value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            float result = 0;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float*, NgxResult>)
                Vtable[SlotGetFloat])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetDouble(string name, out double value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            double result = 0;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double*, NgxResult>)
                Vtable[SlotGetDouble])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetULong(string name, out ulong value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            ulong result = 0;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong*, NgxResult>)
                Vtable[SlotGetUlonglong])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public NgxResult GetVoidPointer(string name, out IntPtr value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            IntPtr result = IntPtr.Zero;
            NgxResult status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, NgxResult>)
                Vtable[SlotGetVoidPointer])(Handle, utf8, &result);
            value = result;
            return status;
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }
}
