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
/// plain <see cref="ClientPlatformWindows" />, so windowing, input, audio and the
/// frame loop are inherited. In this step it only owns graphics bring-up and
/// teardown; the base's existing <see cref="OptimumRender.Device" /> branches keep
/// rendering until later steps move them into overrides here.
///
/// Compiled against the donor lib and bound at runtime to the Cecil-patched one,
/// so <see cref="InitializeGraphics" /> first checks that the loaded lib really
/// declares the virtuals this class relies on, and fails the install (OpenGL
/// fallback) instead of letting a call bypass an override mid-frame.
/// </summary>
public class VulkanClientPlatform : ClientPlatformWindows
{
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
    /// Creates the Vulkan device for the window and publishes it as
    /// <see cref="OptimumRender.Device" />. False leaves the caller holding a window
    /// with no graphics API, which it reopens for OpenGL with a base platform.
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

        // Installing is a single transition: a device that is already published
        // stays, so a second call cannot displace and leak the one the client
        // is drawing with.
        if (OptimumRender.Device != null) return true;

        VulkanDevice? device = null;
        try
        {
            device = DeviceFactory();

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

            OptimumRender.Device = device;
            OptimumRender.ActiveBackend = EnumRenderBackend.Vulkan;
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
        try
        {
            OptimumRender.Device?.Dispose();
        }
        catch (Exception)
        {
            // A driver throwing on teardown must not stop the client exiting.
        }

        OptimumRender.Device = null;
        OptimumRender.ActiveBackend = EnumRenderBackend.OpenGL;
        OptimumRender.NoGraphicsApiWindow = false;
        OptimumRenderBootstrap.ClearCrashMarker();
    }
}
