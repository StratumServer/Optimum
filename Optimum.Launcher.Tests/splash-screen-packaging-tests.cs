using System;
using System.IO;
using SkiaSharp;
using Xunit;

namespace Optimum.Launcher.Tests;

/// <summary>
/// The patch-progress splash screen (PatchSplashScreen.cs) pulls in OpenTK
/// (GLFW) and SkiaSharp, both of which ship native binaries under
/// runtimes/&lt;rid&gt;/native/ - confirmed present after a real build
/// (runtimes/win-x64/native/{glfw3.dll,libSkiaSharp.dll}). package.ps1 stages
/// the Windows release by copying an explicit, fixed file list for the
/// launcher; without copying that runtimes/ folder too, the staged release
/// builds and runs fine in dev but throws DllNotFoundException the moment a
/// real user's cache-miss patch run tries to open the splash. Guards against
/// that fix regressing silently.
/// </summary>
public class SplashScreenPackagingTests
{
    [Fact]
    public void LauncherCsprojReferencesRequiredNativeAssetPackages()
    {
        string csproj = Read("Optimum.Launcher/Optimum.Launcher.csproj");
        Assert.Contains("PackageReference Include=\"OpenTK\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.Win32\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.Linux\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.macOS\"", csproj);
    }

    [Fact]
    public void WindowsPackagingScriptCopiesLauncherRuntimesFolder()
    {
        string script = Read("scripts/package.ps1");
        Assert.Contains("Join-Path $launcherOut 'runtimes'", script);
    }

    [Fact]
    public void WindowsPackagingScriptCopiesManagedLauncherDependencies()
    {
        string script = Read("scripts/package.ps1");
        Assert.Contains("Get-ChildItem -Path $launcherOut -Filter '*.dll' -File", script);
        Assert.Contains("Copy-Item -Force $_.FullName $stageDir", script);
    }

    [Fact]
    public void MacSplashDisablesRetinaFramebufferBeforeCreatingWindow()
    {
        string source = Read("Optimum.Launcher/PatchSplashScreen.cs");
        int settings = source.IndexOf("var settings = new NativeWindowSettings", StringComparison.Ordinal);
        int macGuard = source.IndexOf("if (OperatingSystem.IsMacOS())", StringComparison.Ordinal);
        int hint = source.IndexOf("GLFW.WindowHint(WindowHintBool.ScaleFramebuffer, false);", StringComparison.Ordinal);
        int window = source.IndexOf("_window = new GameWindow(GameWindowSettings.Default, settings);", StringComparison.Ordinal);

        Assert.True(settings >= 0 && settings < macGuard, "The Retina hint must be set after window settings are initialized.");
        Assert.True(macGuard >= 0 && macGuard < hint, "The Retina hint must be guarded by the macOS branch.");
        Assert.True(hint >= 0 && hint < window, "The Retina hint must precede splash window creation.");
    }

    [Fact]
    public void PatchProgressTextWrapsWithinTheSplashWindowWithoutLosingCharacters()
    {
        const string text = "Applying Optimum patches: .optimum/vanilla/Mods/VSSurvivalMod.dll";
        using var typeface = SKTypeface.FromFamilyName("sans-serif");
        using var font = new SKFont(typeface, 28f);
        using var paint = new SKPaint { IsAntialias = true };

        string[] lines = PatchSplashScreen.WrapText(text, font, paint, maximumWidth: 420);

        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(font.MeasureText(line, paint) <= 420));
        Assert.Equal(text, string.Concat(lines));
    }

    [Fact]
    public void PatchProgressTextWrapsAtUnicodeTextElementBoundariesAndHandlesCrLf()
    {
        const string text = "Stage e\u0301 🧪 ready\r\nApplying patch";
        using var typeface = SKTypeface.FromFamilyName("sans-serif");
        using var font = new SKFont(typeface, 28f);
        using var paint = new SKPaint { IsAntialias = true };

        string[] lines = PatchSplashScreen.WrapText(text, font, paint, maximumWidth: 100);

        Assert.True(lines.Length > 2);
        Assert.All(lines, line => Assert.True(font.MeasureText(line, paint) <= 100));
        Assert.Equal(text.Replace("\r\n", "", StringComparison.Ordinal), string.Concat(lines));
        for (int index = 0; index + 1 < lines.Length; index++)
        {
            Assert.False(char.IsHighSurrogate(lines[index][^1]));
            Assert.False(char.IsLowSurrogate(lines[index + 1][0]));
        }
    }

    private static string Read(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root.FullName, relativePath));
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
