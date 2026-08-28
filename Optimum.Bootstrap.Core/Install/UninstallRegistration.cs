using System.Runtime.Versioning;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// Registers and removes the Windows "Apps &amp; features" uninstall entry
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1</c>),
/// matching <c>scripts/install-windows.ps1</c>. A no-op on Linux and macOS, where
/// the install manifest and the <c>.desktop</c> entry are the record.
/// </summary>
public static class UninstallRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1";

    /// <summary>Returns the registry key path when it was written, null otherwise.</summary>
    public static string? Register(string installDirectory, string version)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return RegisterWindows(installDirectory, version);
    }

    public static void Unregister(string? keyPath)
    {
        if (keyPath is null || !OperatingSystem.IsWindows())
            return;
        UnregisterWindows(keyPath);
    }

    [SupportedOSPlatform("windows")]
    private static string? RegisterWindows(string installDirectory, string version)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            string exe = Path.Combine(installDirectory, "Optimum.exe");
            string uninstaller = Path.Combine(installDirectory, "uninstall.ps1");
            key.SetValue("DisplayName", "Optimum");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "Zaldaryon");
            key.SetValue("InstallLocation", installDirectory);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString",
                $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{uninstaller}\" -InstallDir \"{installDirectory}\" -Force");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return KeyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows(string keyPath)
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            /* best effort */
        }
    }
}
