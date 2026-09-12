using System;
using System.Reflection;
using Vintagestory;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// The client platform on the Vulkan path (Vulkan-native plan, Phase 1A).
///
/// Created by <see cref="OptimumRenderBootstrap.CreatePlatform" /> in place of a
/// plain <see cref="ClientPlatformWindows" />, so windowing, input, audio, the frame
/// pacing and the API-neutral render logic (post chain, TAA windows) are inherited.
/// It owns graphics bring-up and teardown and, since Phase 1A step 4, every graphics
/// operation: the partial files override each graphics member with calls to the
/// <see cref="VulkanDevice" /> this platform created, and ClientPlatformWindows keeps
/// only the GL path.
///
/// Compiled against the donor lib and bound at runtime to the Cecil-patched one,
/// so <see cref="InitializeGraphics" /> first checks that the loaded lib really
/// declares the virtuals this class relies on, and fails the install (OpenGL
/// fallback) instead of letting a call bypass an override mid-frame.
/// </summary>
public partial class VulkanClientPlatform : ClientPlatformWindows
{
    /// <summary>
    /// The device this platform brought up in <see cref="InitializeGraphics" />. Every
    /// graphics override in the partial files calls it directly; null before a successful
    /// install and after <see cref="ShutdownGraphics" />.
    /// </summary>
    private VulkanDevice device;

    /// <summary>Test seam: the device the overrides draw with.</summary>
    internal VulkanDevice? GraphicsDevice => device;

    public const string ForceInstallFailureVariable = "OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE";
    public const string ForcedInstallFailureReason = "forced by " + ForceInstallFailureVariable;

    /// <summary>A virtual the loaded lib must declare, matched by name and parameter type names.</summary>
    internal readonly record struct ExpectedVirtual(bool OnAbstract, string Name, string[] ParameterTypeNames);

    /// <summary>
    /// The injected virtuals on <see cref="ClientPlatformAbstract" /> and the members
    /// the patcher virtualizes in place on <see cref="ClientPlatformWindows" />
    /// (<c>Optimum.Patcher/Program.cs</c>, <c>methodsToVirtualize</c>).
    /// </summary>
    internal static readonly ExpectedVirtual[] ExpectedVirtuals =
    {
        new(true, "InitializeGraphics", new[] { "IntPtr", "Int32", "Int32", "String&" }),
        new(true, "ShutdownGraphics", Array.Empty<string>()),
        new(false, "SetupDefaultFrameBuffers", Array.Empty<string>()),
        new(false, "DisposeFrameBuffers", new[] { "List`1" }),
        new(false, "RenderFullscreenTriangle", new[] { "MeshRef" }),
        new(false, "GetGraphicsCardRenderer", Array.Empty<string>()),
        new(false, "LogAndTestHardwareInfosStage2", Array.Empty<string>()),
        // Phase 1A step 3: program, uniform and UBO operations (overridden since step 4).
        new(true, "UseShaderProgram", new[] { "Int32" }),
        new(true, "DisposeShaderProgram", new[] { "ShaderProgramBase" }),
        new(true, "BindSampler", new[] { "Int32", "Int32" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Single" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Int32" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Single", "Single" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Single", "Single", "Single" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Single", "Single", "Single", "Single" }),
        new(true, "SetUniform", new[] { "Int32", "Int32", "Int32", "Int32", "Int32" }),
        new(true, "SetUniformArray1", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformArray2", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformArray3", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformArray4", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformMatrix", new[] { "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformMatrix", new[] { "Int32", "Int32", "Matrix4&" }),
        new(true, "SetUniformMatrices", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "SetUniformMatrices4x3", new[] { "Int32", "Int32", "Int32", "Single[]" }),
        new(true, "BindProgramTexture2D", new[] { "ShaderProgramBase", "String", "Int32", "Int32" }),
        new(true, "BindProgramTextureCube", new[] { "ShaderProgramBase", "String", "Int32", "Int32" }),
        new(true, "BindUBO", new[] { "UBO" }),
        new(true, "UnbindUBO", new[] { "UBO" }),
        new(true, "UpdateUBO", new[] { "UBO", "IntPtr", "Int32", "Int32", "Boolean" }),
        new(true, "DeleteUBO", new[] { "UBO" }),
        // Phase 1A step 4: TAA motion windows and FSR target selection.
        new(true, "EnableMotionDrawBuffers", Array.Empty<string>()),
        new(true, "RestorePrimaryDrawBuffers", Array.Empty<string>()),
        new(true, "EnableMotionOnlyDrawBuffers", Array.Empty<string>()),
        new(true, "ApplyOptimumMotionBlendState", Array.Empty<string>()),
        new(true, "ApplyOptimumMotionAccumulateBlendState", Array.Empty<string>()),
        new(true, "SelectFsrDrawBuffer", new[] { "FrameBufferRef" }),
        // Phase 1A step 4: framebuffer binding, clears and post-chain pass state.
        new(true, "BindCurrentFrameBuffer", new[] { "FrameBufferRef" }),
        new(true, "BindCurrentFrameBufferKeepViewport", new[] { "FrameBufferRef" }),
        new(true, "ClearBoundFrameBuffer", new[] { "FrameBufferRef", "Single[]", "Boolean", "Boolean" }),
        new(true, "ClearFrameBufferPass", new[] { "EnumFrameBuffer" }),
        new(true, "ApplyTransparentPassBlendState", Array.Empty<string>()),
        new(true, "SelectBackDrawBuffer", Array.Empty<string>()),
        new(true, "SetBlendEnabled", new[] { "Boolean" }),
        new(true, "ApplyTransparentMergeBlendState", Array.Empty<string>()),
        new(true, "ClearSsaoTarget", Array.Empty<string>()),
        new(true, "BeginFinalCompositionDrawBuffers", Array.Empty<string>()),
        new(true, "RestoreWorldDrawBuffers", new[] { "Boolean" }),
        // Phase 1A step 4: frame bracket, thick-line probe, window size, parity readback.
        new(true, "BeginFrame", Array.Empty<string>()),
        new(true, "EndFrame", Array.Empty<string>()),
        new(true, "ProbeThickLineSupport", Array.Empty<string>()),
        new(true, "OnWindowSizeChanged", new[] { "Int32", "Int32" }),
        new(true, "ReadTextureForParity", new[] { "Int32" }),
        // Phase 1A step 5: the leaf operations the render systems outside the platform issued.
        new(true, "SetDepthRange", new[] { "Single", "Single" }),
        new(true, "ClearDefaultDepth", new[] { "Single" }),
        new(true, "DeleteMeshHandle", new[] { "Int32" }),
        new(true, "DeleteVertexArrayHandles", new[] { "VAO" }),
        new(true, "SetTextureLodBias", new[] { "Int32[]", "Single" }),
        new(true, "SetSamplerLodBias", new[] { "Int32", "Single" }),
        new(true, "SetTextureDepthCompare", new[] { "Int32", "Int32" }),
        new(true, "ClearTextureRegion", new[] { "Int32", "Int32", "Int32", "Int32", "Int32", "Int32[]" }),
        new(true, "LoadTextureFromRgbaPointer", new[] { "Int32", "Int32", "IntPtr" }),
        new(true, "SetProgramSamplerUnit", new[] { "Int32", "String", "Int32" }),
        new(true, "CreateOitTargets", new[] { "FrameBufferRef", "Int32", "Int32&", "Int32&" }),
        new(true, "BeginOitAccumulation", new[] { "FrameBufferRef" }),
        new(true, "BindOitTextures", new[] { "Int32", "Int32" }),
        new(true, "GenOcclusionQuery", Array.Empty<string>()),
        new(true, "BeginOcclusionQuery", new[] { "Int32" }),
        new(true, "EndOcclusionQuery", new[] { "Int32" }),
        new(true, "TryGetOcclusionQueryResult", new[] { "Int32", "Int32&" }),
        new(true, "DeleteOcclusionQuery", new[] { "Int32" }),
        new(true, "ReadDefaultFramebuffer", new[] { "Int32", "Int32", "Int32", "Int32", "IntPtr" }),
        new(true, "get_GraphicsBackendName", Array.Empty<string>()),
        // Phase 2: render-stage bracket from ClientMain.TriggerRenderStage (contract C3).
        new(true, "BeginRenderStage", new[] { "EnumRenderStage" }),
        new(true, "EndRenderStage", new[] { "EnumRenderStage" }),
        // Phase 2 step 2: the TAA post methods declare their frame-graph passes.
        new(true, "RenderOptimumSkyMotion", Array.Empty<string>()),
        new(true, "RenderOptimumTaaResolve", Array.Empty<string>()),
        new(true, "RenderOptimumTaaSharpen", new[] { "Int32" }),
        // DLSS plan, Phase 3: the upscaler's placement - injected into
        // ClientPlatformWindows with its flags, so these arrive virtual there rather than
        // through methodsToVirtualize.
        new(false, "get_OptimumUpscalerActive", Array.Empty<string>()),
        new(false, "OptimumTryPlanUpscaleRenderSize", new[] { "Int32", "Int32", "Int32&", "Int32&" }),
        new(false, "RenderOptimumUpscale", Array.Empty<string>()),
        // "Latency seams" S3: the pre-input sleep and the frame-cap ownership flag.
        new(true, "LatencySleep", Array.Empty<string>()),
        new(true, "get_LatencyOwnsFrameCap", Array.Empty<string>()),
        new(true, "SetLatencyFrameCap", new[] { "Int32" }),
    };

    /// <summary>
    /// Non-virtual members injected into <see cref="ClientPlatformWindows" /> that the
    /// overrides read or call (the platform state behind the device framebuffer setup and
    /// the SSAO flag). A lib without them would fail with MissingMethodException mid-frame.
    /// </summary>
    internal static readonly string[] ExpectedWindowsMembers =
    {
        "OptimumRenderSsao",
        "OptimumAdoptFrameBufferSettings",
        "OptimumTaaRequested",
        "OptimumSsaoKernel",
        "SetOptimumMotionAttachmentIndex",
        "OptimumAdoptTaaTargets",
        "OptimumFinishDeviceFrameBufferSetup",
    };

    /// <summary>Test seam: the device to bring up (tests add validation capture).</summary>
    internal Func<VulkanDevice> DeviceFactory = () => new VulkanDevice();

    /// <summary>Test seam: where the crash marker goes; null means <see cref="GamePaths.DataPath" />.</summary>
    internal string? CrashMarkerDataPath;

    public VulkanClientPlatform(Logger logger) : base(logger)
    {
    }

    /// <summary>
    /// Checks the loaded lib against <see cref="ExpectedVirtuals" />. Reflection
    /// only; never throws.
    /// </summary>
    internal static bool VerifyHost(Type abstractType, Type windowsType, out string? reason)
    {
        try
        {
            if (windowsType.IsSealed)
            {
                reason = windowsType.FullName + " is sealed in the loaded VintagestoryLib (not patched for this renderer)";
                return false;
            }
            if (!windowsType.IsSubclassOf(abstractType))
            {
                reason = windowsType.FullName + " does not derive from " + abstractType.FullName;
                return false;
            }

            foreach (ExpectedVirtual expected in ExpectedVirtuals)
            {
                Type owner = expected.OnAbstract ? abstractType : windowsType;
                MethodInfo? method = FindDeclared(owner, expected);
                if (method == null || !method.IsVirtual || method.IsFinal)
                {
                    reason = "the loaded VintagestoryLib lacks the virtual " + owner.Name + "." + expected.Name +
                        " (not patched for this renderer)";
                    return false;
                }
            }

            const BindingFlags memberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (string name in ExpectedWindowsMembers)
            {
                if (windowsType.GetMember(name, memberFlags).Length == 0)
                {
                    reason = "the loaded VintagestoryLib lacks " + windowsType.Name + "." + name +
                        " (not patched for this renderer)";
                    return false;
                }
            }

            reason = null;
            return true;
        }
        catch (Exception error)
        {
            reason = "the platform self-check threw: " + error.Message;
            return false;
        }
    }

    private static MethodInfo? FindDeclared(Type owner, ExpectedVirtual expected)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in owner.GetMethods(flags))
        {
            if (method.Name != expected.Name) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != expected.ParameterTypeNames.Length) continue;
            bool matches = true;
            for (int i = 0; i < parameters.Length && matches; i++)
                matches = parameters[i].ParameterType.Name == expected.ParameterTypeNames[i];
            if (matches) return method;
        }
        return null;
    }

    internal static bool IsInstallFailureForced() =>
        Environment.GetEnvironmentVariable(ForceInstallFailureVariable) == "1";

    /// <summary>
    /// Creates the Vulkan device for the window, marks the backend Vulkan and publishes
    /// the fork graphics bridge. False leaves the caller holding a window with no graphics
    /// API, which it reopens for OpenGL with a base platform.
    /// </summary>
    public override bool InitializeGraphics(IntPtr windowHandle, int width, int height, out string reason)
    {
        string? hostReason;
        if (!VerifyHost(typeof(ClientPlatformAbstract), typeof(ClientPlatformWindows), out hostReason))
        {
            reason = hostReason!;
            return false;
        }

        if (IsInstallFailureForced())
        {
            reason = ForcedInstallFailureReason;
            return false;
        }

        reason = null!;

        // Installing is a single transition: a device this platform already brought
        // up stays, so a second call cannot displace and leak the one the client is
        // drawing with.
        if (this.device != null)
        {
            return true;
        }

        VulkanDevice? device = null;
        try
        {
            device = DeviceFactory();

            // DLSS plan, Phase 2: prepared before the device is created, because
            // NGX's instance and device extensions have to be requested at device
            // creation. Nothing happens here when the setting is off.
            PrepareUpscaler(device);

            // The marker goes down before the driver is touched: a crash inside
            // device creation is exactly the kind the next start must see. A
            // clean failure clears it again, since the caller falls back to
            // OpenGL on its own.
            OptimumRenderBootstrap.WriteCrashMarker(CrashMarkerDataPath ?? GamePaths.DataPath);

            if (!device.Initialize(windowHandle, width, height, out string failureReason))
            {
                device.Dispose();
                OptimumRenderBootstrap.ClearCrashMarker();
                reason = failureReason;
                return false;
            }

            this.device = device;
            // DLSS plan, Phase 2: NGX comes up on the device that now exists. A
            // refusal leaves the client on the Vulkan device with no upscaler, one
            // line in the log and the setting stood down - never a failed install.
            BringUpUpscaler(device);
            // Phase 2 step 2: the stage bracket drives the frame graph's pass declarations.
            RenderStageListener = new FrameGraphStageListener(this);
            OptimumRender.ActiveBackend = EnumRenderBackend.Vulkan;
            OptimumForkGraphics.Active = new VulkanForkGraphics(device);
            return true;
        }
        catch (Exception error)
        {
            try
            {
                device?.Dispose();
            }
            catch (Exception)
            {
                // The install already failed; the reason below is the useful one.
            }
            OptimumRenderBootstrap.ClearCrashMarker();
            reason = error.Message;
            return false;
        }
    }

    /// <summary>
    /// Shuts the device down, returns the backend state to OpenGL and clears the
    /// crash marker. Safe to call more than once and after a failed install.
    /// </summary>
    public override void ShutdownGraphics()
    {
        // The bridge goes first: nothing may reach a device that is being torn down.
        OptimumForkGraphics.Active = null;
        // DLSS plan, Phase 2: the vendor runtime goes before the device it was
        // initialised on - retire the feature, drain the timeline, shut NGX down.
        // The other order is a use-after-free inside the driver.
        ShutDownUpscaler();
        try
        {
            device?.Dispose();
        }
        catch (Exception)
        {
            // A driver throwing on teardown must not stop the client exiting.
        }

        device = null;
        RenderStageListener = null;
        OptimumRender.ActiveBackend = EnumRenderBackend.OpenGL;
        OptimumRender.NoGraphicsApiWindow = false;
        OptimumRenderBootstrap.ClearCrashMarker();
    }
}
