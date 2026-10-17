using System;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class StrayRootAssemblyCleanupTests
{
    [Fact]
    public void CleanupStrayRootAssembliesDeletesRootCopyWhenLibCopyExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        string libDir = Path.Combine(root, "Lib");
        Directory.CreateDirectory(libDir);

        string rootOpenAl = Path.Combine(root, "OpenTK.Audio.OpenAL.dll");
        string libOpenAl = Path.Combine(libDir, "OpenTK.Audio.OpenAL.dll");
        File.WriteAllText(rootOpenAl, "root-dummy");
        File.WriteAllText(libOpenAl, "lib-dummy");

        try
        {
            Program.CleanupStrayRootAssemblies(root);
            Assert.False(File.Exists(rootOpenAl), "Root OpenTK.Audio.OpenAL.dll should be removed when Lib copy exists.");
            Assert.True(File.Exists(libOpenAl), "Lib OpenTK.Audio.OpenAL.dll must remain intact.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupStrayRootAssembliesPreservesRootCopyWhenLibCopyMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string rootOpenAl = Path.Combine(root, "OpenTK.Audio.OpenAL.dll");
        File.WriteAllText(rootOpenAl, "root-only");

        try
        {
            Program.CleanupStrayRootAssemblies(root);
            Assert.True(File.Exists(rootOpenAl), "Root OpenTK.Audio.OpenAL.dll should not be deleted if no Lib fallback exists.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupStrayRootAssembliesClearsReadOnlyAttributeAndDeletes()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        string libDir = Path.Combine(root, "Lib");
        Directory.CreateDirectory(libDir);

        string rootOpenAl = Path.Combine(root, "OpenTK.Audio.OpenAL.dll");
        string libOpenAl = Path.Combine(libDir, "OpenTK.Audio.OpenAL.dll");
        File.WriteAllText(rootOpenAl, "root-read-only");
        File.SetAttributes(rootOpenAl, FileAttributes.ReadOnly);
        File.WriteAllText(libOpenAl, "lib-copy");

        try
        {
            Program.CleanupStrayRootAssemblies(root);
            Assert.False(File.Exists(rootOpenAl), "Read-only root assembly should have attributes cleared and be deleted.");
            Assert.True(File.Exists(libOpenAl), "Lib copy should remain intact.");
        }
        finally
        {
            if (File.Exists(rootOpenAl))
                File.SetAttributes(rootOpenAl, FileAttributes.Normal);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupStrayRootAssembliesPreservesOtherRootAssemblies()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        string libDir = Path.Combine(root, "Lib");
        Directory.CreateDirectory(libDir);

        string rootOptimum = Path.Combine(root, "Optimum.dll");
        string libOptimum = Path.Combine(libDir, "Optimum.dll");
        File.WriteAllText(rootOptimum, "optimum-root");
        File.WriteAllText(libOptimum, "optimum-lib");

        try
        {
            Program.CleanupStrayRootAssemblies(root);
            Assert.True(File.Exists(rootOptimum), "Unrelated root assemblies must not be deleted.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveUnmanagedLibraryReturnsZeroForUnknownLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        try
        {
            IntPtr result = Program.ResolveUnmanagedLibrary(root, "nonexistent-library-xyz");
            Assert.Equal(IntPtr.Zero, result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveUnmanagedLibraryResolvesNativeOpenAlFromLib()
    {
        var repoRoot = FindRepositoryRoot();
        string vanillaLinuxOpenAl = Path.Combine(repoRoot.FullName, ".vanilla", "linux-x64", "vintagestory", "Lib", "libopenal.so.1");
        if (!File.Exists(vanillaLinuxOpenAl))
            return;

        string root = Path.Combine(Path.GetTempPath(), "optimum-stray-tests", Guid.NewGuid().ToString("N"));
        string libDir = Path.Combine(root, "Lib");
        Directory.CreateDirectory(libDir);
        File.Copy(vanillaLinuxOpenAl, Path.Combine(libDir, "libopenal.so.1"));

        try
        {
            IntPtr handle = Program.ResolveUnmanagedLibrary(root, "openal32");
            Assert.NotEqual(IntPtr.Zero, handle);
            NativeLibrary.Free(handle);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }
}
