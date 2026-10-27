using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class CairoModCompatibilityTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optimum-cairo-compat-tests", Guid.NewGuid().ToString("N"));

    public CairoModCompatibilityTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // Best effort cleanup in tests
        }
    }

    [Fact]
    public void AssemblyLoader_ResolvesCairoSharpAliasToCairoSharpDll()
    {
        var gameDir = Path.Combine(_tempRoot, "game");
        var cacheDir = Path.Combine(_tempRoot, "cache");
        var libDir = Path.Combine(gameDir, "Lib");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(cacheDir);

        // Find existing cairo-sharp.dll in build output or base directory
        var candidateLocations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cairo-sharp.dll"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net10.0", "cairo-sharp.dll"),
        };

        string? sourceCairo = null;
        foreach (var loc in candidateLocations)
        {
            if (File.Exists(loc))
            {
                sourceCairo = Path.GetFullPath(loc);
                break;
            }
        }

        Assert.True(sourceCairo != null && File.Exists(sourceCairo), $"Could not find built cairo-sharp.dll: {sourceCairo}");
        var targetCairo = Path.Combine(libDir, "cairo-sharp.dll");
        File.Copy(sourceCairo, targetCairo, overwrite: true);

        using var loader = new AssemblyLoader(cacheDir, gameDir);

        // Resolving legacy "CairoSharp" must return cairo-sharp.dll
        var resolved = loader.ResolveAssembly("CairoSharp");
        Assert.NotNull(resolved);
        Assert.Equal("cairo-sharp", resolved.GetName().Name);

        // Repeated resolve should return cached instance
        var cached = loader.ResolveAssembly("CairoSharp");
        Assert.Same(resolved, cached);

        // Resolving "cairo-sharp" directly should also return the same instance
        var direct = loader.ResolveAssembly("cairo-sharp");
        Assert.Same(resolved, direct);
    }

    [Fact]
    public void AssemblyLoader_ResolvesCairoSharpWhenCairoSharpLoadedFirst()
    {
        var gameDir = Path.Combine(_tempRoot, "game");
        var cacheDir = Path.Combine(_tempRoot, "cache");
        var libDir = Path.Combine(gameDir, "Lib");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(cacheDir);

        var candidateLocations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cairo-sharp.dll"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net10.0", "cairo-sharp.dll"),
        };

        string? sourceCairo = null;
        foreach (var loc in candidateLocations)
        {
            if (File.Exists(loc))
            {
                sourceCairo = Path.GetFullPath(loc);
                break;
            }
        }

        Assert.True(sourceCairo != null && File.Exists(sourceCairo));
        File.Copy(sourceCairo, Path.Combine(libDir, "cairo-sharp.dll"), overwrite: true);

        using var loader = new AssemblyLoader(cacheDir, gameDir);

        var direct = loader.ResolveAssembly("cairo-sharp");
        Assert.NotNull(direct);

        var alias = loader.ResolveAssembly("CairoSharp");
        Assert.NotNull(alias);
        Assert.Same(direct, alias);
    }

    [Fact]
    public void Program_ResolveUnmanagedLibrary_ResolvesCairoVariants()
    {
        var gameDir = Path.Combine(_tempRoot, "game");
        var libDir = Path.Combine(gameDir, "Lib");
        Directory.CreateDirectory(libDir);

        // Missing library returns IntPtr.Zero without throwing
        var missingHandle = Program.ResolveUnmanagedLibrary(gameDir, "libcairo-2");
        Assert.Equal(IntPtr.Zero, missingHandle);

        var unknownHandle = Program.ResolveUnmanagedLibrary(gameDir, "some_unknown_lib");
        Assert.Equal(IntPtr.Zero, unknownHandle);

        // If the native shim exists in tools/cairo-native-shim/, test loading it
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var shimSource = Path.Combine(repoRoot, "tools", "cairo-native-shim", "libcairo.so.2");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists(shimSource))
        {
            File.Copy(shimSource, Path.Combine(libDir, "libcairo.so.2"), overwrite: true);

            var handle1 = Program.ResolveUnmanagedLibrary(gameDir, "libcairo-2");
            Assert.NotEqual(IntPtr.Zero, handle1);
            NativeLibrary.Free(handle1);

            var handle2 = Program.ResolveUnmanagedLibrary(gameDir, "libcairo.so.2");
            Assert.NotEqual(IntPtr.Zero, handle2);
            NativeLibrary.Free(handle2);

            var handle3 = Program.ResolveUnmanagedLibrary(gameDir, "cairo");
            Assert.NotEqual(IntPtr.Zero, handle3);
            NativeLibrary.Free(handle3);
        }
    }
}
