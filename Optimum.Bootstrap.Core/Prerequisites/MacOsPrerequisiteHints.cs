using System.Runtime.InteropServices;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>Manual macOS recovery steps that do not depend on Homebrew.</summary>
public static class MacOsPrerequisiteHints
{
    private const string CommandLineToolsUrl = "https://developer.apple.com/download/all/";
    private const string PythonUrl = "https://www.python.org/downloads/macos/";
    private const string DotnetArm64Url = "https://aka.ms/dotnet/10.0/dotnet-sdk-osx-arm64.pkg";
    private const string DotnetX64Url = "https://aka.ms/dotnet/10.0/dotnet-sdk-osx-x64.pkg";
    private const string PowerShellUrl = "https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-macos";

    public static (string Instructions, string? Url) For(string command, Architecture architecture)
    {
        string arch = architecture == Architecture.Arm64 ? "Arm64 (Apple Silicon)" : "x64 (Intel)";
        return command switch
        {
            "dotnet" => ($"Download and install the .NET 10 SDK for macOS {arch}, then rescan.",
                architecture == Architecture.Arm64 ? DotnetArm64Url : DotnetX64Url),
            "pwsh" => ($"Install PowerShell 7 for macOS {arch} using the matching official .pkg, then rescan.", PowerShellUrl),
            "python3" => ("Install the macOS universal2 Python 3 package from python.org, then rescan.", PythonUrl),
            "git" or "perl" or "curl" or "tar" or "chmod" =>
                ("Install Apple's Xcode Command Line Tools with `xcode-select --install`, then rescan. You can also download them from Apple Developer Downloads.", CommandLineToolsUrl),
            _ => ($"Install {command} for macOS {arch} using a compatible package or the tool vendor's instructions, then rescan.", null),
        };
    }
}
