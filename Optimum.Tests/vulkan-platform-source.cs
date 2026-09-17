using System;
using System.IO;
using System.Text;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 4: every device call that used to sit in a
/// ClientPlatformWindows branch lives in VulkanClientPlatform, split over
/// <c>Optimum.Render.Vulkan/Platform/VulkanClientPlatform*.cs</c>. Source-coverage tests that
/// pin a device-side line read it here; the GL side stays in ClientPlatformWindows.cs.
/// </summary>
internal static class VulkanPlatformSource
{
    public const string MainFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs";

    public const string ClientPlatformWindowsSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>All VulkanClientPlatform partial files, in file-name order, concatenated.</summary>
    public static string Read()
    {
        string main = PatchReader.FindRepositoryFile(MainFile);
        string directory = Path.GetDirectoryName(main)!;
        string[] files = Directory.GetFiles(directory, "VulkanClientPlatform*.cs");
        Array.Sort(files, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (string file in files)
        {
            text.Append(File.ReadAllText(file)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The donor ClientPlatformWindows source (the full file, not patch hunks).</summary>
    public static string ReadClientPlatformWindows() =>
        File.ReadAllText(PatchReader.FindRepositoryFile(ClientPlatformWindowsSource));
}
