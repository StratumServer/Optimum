// Source: Optimum.Tests/PatchReader.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Reads an excerpt of the "after" side of a unified diff. Unchanged lines
/// between hunks are absent; callers that need a complete method must use the
/// materialized patched source or compiled IL.
/// </summary>
public static class PatchReader
{
    private static readonly Lazy<string> RepositoryRoot = new(() =>
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root from {AppContext.BaseDirectory}.");
    });

    /// <summary>
    /// Returns context and added lines concatenated across hunks. This is a
    /// patch excerpt, not the complete resulting source file.
    /// </summary>
    public static string ReadPatchedContent(string patchFilePath)
    {
        var lines = File.ReadAllLines(patchFilePath);
        var patched = lines
            .Where(line =>
            {
                // Skip diff metadata
                if (line.StartsWith("diff --git")) return false;
                if (line.StartsWith("index ")) return false;
                if (line.StartsWith("--- ")) return false;
                if (line.StartsWith("+++ ")) return false;
                if (line.StartsWith("@@ ")) return false;
                // Skip removed lines
                if (line.StartsWith("-")) return false;
                // Include added lines (strip the + prefix)
                // Include context lines (no prefix)
                return true;
            })
            .Select(line => line.StartsWith("+") ? line.Substring(1) : line);

        return string.Join("\n", patched);
    }

    /// <summary>
    /// Finds a file relative to the repository root, discovered once from the test assembly location.
    /// </summary>
    public static string FindRepositoryFile(string relativePath)
    {
        string candidate = Path.Combine(RepositoryRoot.Value, relativePath);
        if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException($"Could not find {relativePath} in {RepositoryRoot.Value}.");
    }

    /// <summary>
    /// Convenience: find a patch file and return its after-side hunk excerpt.
    /// </summary>
    public static string ReadPatch(string relativePatchPath)
    {
        return ReadPatchedContent(FindRepositoryFile(relativePatchPath));
    }
}
}

// Source: Optimum.Tests/vulkan-platform-source.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text;

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
}

namespace Optimum.Tests
{
using System.IO;
using System.Text;

/// <summary>Reads the device facade and its subject partials as one source contract.</summary>
internal static class VulkanDeviceSource
{
    private static readonly string[] Parts =
    {
        "VulkanDevice.cs",
        "VulkanDevice.Programs.cs",
        "VulkanDevice.Resources.cs",
        "VulkanDevice.Meshes.cs",
        "VulkanDevice.Binding.cs",
        "VulkanDevice.Readback.cs",
    };

    public static string Read()
    {
        string root = Path.GetDirectoryName(PatchReader.FindRepositoryFile(
            "Optimum.Render.Vulkan/VulkanDevice.cs"))!;
        var result = new StringBuilder();
        foreach (string part in Parts)
        {
            result.Append(File.ReadAllText(Path.Combine(root, part))).Append('\n');
        }
        return result.ToString();
    }
}
}

namespace Optimum.Tests
{
using System.IO;

/// <summary>The patch selection and command-line source.</summary>
internal static class PatcherSource
{
    public static string Read() =>
        File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
}
}

namespace Optimum.Tests
{
using System;
using System.IO;

/// <summary>Platform command names for the small script self-tests.</summary>
internal static class TestToolchain
{
    public static string Python => Environment.GetEnvironmentVariable("OPTIMUM_TEST_PYTHON") ??
        (OperatingSystem.IsWindows() ? "python" : "python3");

    public static string Bash
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("OPTIMUM_TEST_BASH");
            if (!string.IsNullOrEmpty(configured)) return configured;
            if (OperatingSystem.IsWindows())
            {
                string gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Git", "bin", "bash.exe");
                if (File.Exists(gitBash)) return gitBash;
            }
            return "bash";
        }
    }
}
}
