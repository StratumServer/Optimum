using System;
using System.IO;
using System.Reflection;

namespace Vintagestory.API.Config;

/// <summary>
/// Chooses the renderer and installs it.
///
/// The backend lives in its own assembly and is loaded by name, so no vanilla
/// assembly ever gains a reference to a renderer implementation - the client
/// only ever sees <see cref="IOptimumGraphicsDevice" /> through
/// <see cref="OptimumRender.Device" />, which is null on the OpenGL path.
///
/// The order of operations is forced by the window. A window created with no
/// graphics API cannot be handed back to OpenGL without being destroyed and
/// reopened, so the decision has to be final before the window opens.
/// <see cref="ShouldTryVulkan" /> answers that without one;
/// <see cref="Install" /> runs afterwards and can still fail, in which case the
/// caller reopens the window for OpenGL.
/// </summary>
public static class OptimumRenderBootstrap
{
    private const string AssemblyName = "Optimum.Render.Vulkan";
    private const string DeviceTypeName = "Optimum.Render.Vulkan.VulkanDevice";

    /// <summary>
    /// Written when a Vulkan session starts and removed when it shuts down
    /// cleanly. Finding one at startup means the last Vulkan run did not survive,
    /// so this session takes OpenGL rather than crash-looping the user out of
    /// their game.
    /// </summary>
    private const string CrashMarkerName = "vulkan-session.lock";

    private static Assembly _backendAssembly;
    private static string _markerPath;

    /// <summary>
    /// Set by <see cref="ShouldTryVulkan" /> when it chose Vulkan despite missing
    /// information, so the caller can say so. Null when there is nothing to warn
    /// about. This is not a failure channel - that is the reason out-parameter.
    /// </summary>
    public static string Advisory;

    /// <summary>
    /// Whether to open the window without a graphics API and try Vulkan.
    ///
    /// Answered before the window exists, and deliberately conservative: any
    /// doubt resolves to OpenGL, because OpenGL is the path that certainly works.
    /// </summary>
    public static bool ShouldTryVulkan(string dataPath, out string reason)
    {
        if (!OptimumConfig.WantsVulkanRenderer)
        {
            reason = "the renderer setting is opengl";
            return false;
        }

        if (HasCrashMarker(dataPath))
        {
            ClearCrashMarker();
            reason = "the previous Vulkan session did not shut down cleanly";
            return false;
        }

        // A mod that calls OpenGL directly cannot work on this backend, and the
        // launcher records those during its metadata scan.
        //
        // Read the scan's explicit decision rather than IsShaderFeatureDisabled:
        // that helper reports every feature as disabled when no scan report
        // exists, which would make an explicitly requested backend impossible to
        // select outside the launcher. A missing scan means "unknown", and the
        // crash marker above is what actually protects a user from a backend that
        // does not work on their machine.
        if (OptimumConfig.IsFeatureExplicitlyDisabled("Vulkan"))
        {
            reason = "a loaded mod calls OpenGL directly";
            return false;
        }

        Advisory = OptimumConfig.ShaderCompatibilityScanAvailable
            ? null
            : "mod compatibility has not been scanned; run the game through Optimum.Launcher to check it";

        string loadError;
        if (!TryLoadBackend(out loadError))
        {
            reason = loadError;
            return false;
        }

        string probeError;
        if (!ProbeSupport(out probeError))
        {
            reason = probeError;
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Creates the device against the already-open window and installs it.
    /// Returns false if it could not be created, which leaves the caller holding
    /// a window with no graphics API that it must reopen for OpenGL.
    /// </summary>
    public static bool Install(IntPtr windowHandle, int width, int height, string dataPath, out string reason)
    {
        reason = null;

        try
        {
            if (!TryLoadBackend(out reason)) return false;

            Type deviceType = _backendAssembly.GetType(DeviceTypeName);
            if (deviceType == null)
            {
                reason = DeviceTypeName + " not found in " + AssemblyName;
                return false;
            }

            object instance = Activator.CreateInstance(deviceType);
            IOptimumGraphicsDevice device = instance as IOptimumGraphicsDevice;
            if (device == null)
            {
                reason = DeviceTypeName + " does not implement IOptimumGraphicsDevice";
                return false;
            }

            string failureReason;
            if (!device.Initialize(windowHandle, width, height, out failureReason))
            {
                device.Dispose();
                reason = failureReason;
                return false;
            }

            OptimumRender.Device = device;
            OptimumRender.ActiveBackend = EnumRenderBackend.Vulkan;
            WriteCrashMarker(dataPath);
            return true;
        }
        catch (Exception error)
        {
            reason = error.Message;
            return false;
        }
    }

    /// <summary>Shuts the device down and clears the crash marker.</summary>
    public static void Shutdown()
    {
        try
        {
            if (OptimumRender.Device != null)
            {
                OptimumRender.Device.Dispose();
            }
        }
        catch (Exception)
        {
            // A driver throwing on teardown must not stop the client exiting.
        }

        OptimumRender.Device = null;
        ClearCrashMarker();
    }

    private static bool TryLoadBackend(out string reason)
    {
        reason = null;
        if (_backendAssembly != null) return true;

        try
        {
            // Beside the launcher, which is where the build deploys it.
            string candidate = Path.Combine(AppContext.BaseDirectory, AssemblyName + ".dll");
            if (File.Exists(candidate))
            {
                _backendAssembly = Assembly.LoadFrom(candidate);
            }
            else
            {
                _backendAssembly = Assembly.Load(AssemblyName);
            }
            return true;
        }
        catch (Exception error)
        {
            reason = "could not load " + AssemblyName + ": " + error.Message;
            return false;
        }
    }

    private static bool ProbeSupport(out string reason)
    {
        reason = null;

        try
        {
            Type deviceType = _backendAssembly.GetType(DeviceTypeName);
            if (deviceType == null)
            {
                reason = "the backend has no IsSupported probe";
                return false;
            }

            // The three-argument form also applies the automatic-selection
            // allow-list; the one-argument form is kept so an older backend
            // assembly still probes, just without that distinction.
            MethodInfo probe = deviceType.GetMethod("IsSupported",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(bool), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() },
                null);

            if (probe != null)
            {
                object[] arguments = new object[3];
                arguments[0] = OptimumConfig.RendererSelectedAutomatically;
                object outcome = probe.Invoke(null, arguments);
                bool allowed = outcome is bool && (bool)outcome;
                if (!allowed)
                {
                    reason = arguments[1] as string;
                    if (reason == null) reason = "no usable Vulkan device";
                }
                return allowed;
            }

            probe = deviceType.GetMethod("IsSupported", BindingFlags.Public | BindingFlags.Static);
            if (probe == null)
            {
                reason = "the backend has no IsSupported probe";
                return false;
            }

            object[] legacyArguments = new object[1];
            object result = probe.Invoke(null, legacyArguments);
            bool supported = result is bool && (bool)result;
            if (!supported)
            {
                reason = legacyArguments[0] as string;
                if (reason == null) reason = "no usable Vulkan device";
            }
            return supported;
        }
        catch (Exception error)
        {
            Exception inner = error.InnerException == null ? error : error.InnerException;
            reason = "the Vulkan probe threw: " + inner.Message;
            return false;
        }
    }

    private static bool HasCrashMarker(string dataPath)
    {
        try
        {
            _markerPath = Path.Combine(dataPath, ".optimum", CrashMarkerName);
            return File.Exists(_markerPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteCrashMarker(string dataPath)
    {
        try
        {
            _markerPath = Path.Combine(dataPath, ".optimum", CrashMarkerName);
            string directory = Path.GetDirectoryName(_markerPath);
            if (directory != null) Directory.CreateDirectory(directory);
            File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            // Losing the marker only costs a crash-loop guard, not correctness.
        }
    }

    private static void ClearCrashMarker()
    {
        try
        {
            if (_markerPath != null && File.Exists(_markerPath)) File.Delete(_markerPath);
        }
        catch (Exception)
        {
        }
    }
}
