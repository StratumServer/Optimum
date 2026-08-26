using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Verifies the installer scripts normalize bare drive-letter paths (e.g. "D:")
/// before calling GetFullPath, preventing the "not of a legal form" error on
/// Windows when a user types a path like "D:\Optimum" (which TrimEnd('\') reduces
/// to "D:" when the user only enters "D:\").
/// </summary>
public class InstallerPathNormalizationTests
{
    [Fact]
    public void GuiClickHandler_NormalizesBareDriverLetter_BeforeGetFullPath()
    {
        string script = Read("scripts/install-windows.ps1");

        // The shared normalizer must detect a bare drive letter and append a
        // backslash before any GetFullPath call.
        Assert.Contains("function Normalize-WindowsDirectoryPath", script);
        Assert.Contains("if ($normalized -match '^[A-Za-z]:$')", script);
        Assert.Contains("[System.IO.Path]::GetFullPath($normalized)", script);

        int normPos = script.IndexOf("if ($normalized -match '^[A-Za-z]:$')");
        int getFullPathPos = script.IndexOf("[System.IO.Path]::GetFullPath($normalized)");
        Assert.True(normPos < getFullPathPos,
            "Drive letter normalization must precede the first GetFullPath($dir) call");
    }

    [Fact]
    public void InvokeOptimumBuild_NormalizesBareDriverLetter()
    {
        string script = Read("scripts/install-windows.ps1");

        // Invoke-OptimumBuild must normalize bare drive letters from CLI args
        // through the shared helper.
        Assert.Contains("$InstallDir = Normalize-WindowsDirectoryPath -Path $InstallDir -Name 'InstallDir'", script);
    }

    [Fact]
    public void InstallStagedPackage_HandlesDriverRootParent()
    {
        string script = Read("scripts/install-windows.ps1");

        // Install-StagedPackage already handles the PS 5.1 Split-Path bug for
        // drive roots. Verify the guard still exists.
        Assert.Contains("if ($parentDir -match '^[A-Za-z]:$')", script);
        Assert.Contains("$parentDir = \"$parentDir\\\"", script);
    }

    [Fact]
    public void NormalizationOrder_GuiPath_TrimThenNormalizeThenValidate()
    {
        string script = Read("scripts/install-windows.ps1");

        // The shared helper owns trim, drive-root normalization, and GetFullPath.
        int helperPos = script.IndexOf("function Normalize-WindowsDirectoryPath");
        int trimPos = script.IndexOf("$normalized = $Path.Trim()", helperPos);
        int driveNorm = script.IndexOf("if ($normalized -match '^[A-Za-z]:$')", helperPos);
        int fullPath = script.IndexOf("[System.IO.Path]::GetFullPath($normalized)", helperPos);

        Assert.True(helperPos >= 0, "Path normalizer missing");
        Assert.True(trimPos > helperPos, "Trim step missing in path normalizer");
        Assert.True(driveNorm > trimPos, "Drive normalization must follow trim");
        Assert.True(fullPath > driveNorm, "GetFullPath must follow drive normalization");

        // The GUI click handler normalizes before its empty/root validation.
        int agreeForm = script.IndexOf("if ($agreeForm.ShowDialog()");
        Assert.True(agreeForm > 0, "Agree form dialog missing");

        int guiNormalizePos = script.IndexOf(
            "$dir = Normalize-WindowsDirectoryPath -Path $script:txtDir.Text -Name 'InstallDir'",
            agreeForm);
        int emptyCheck = script.IndexOf("if (-not $dir)", guiNormalizePos);
        int rootCheck = script.IndexOf("if (Test-FileSystemRoot -Path $dir)", guiNormalizePos);

        Assert.True(guiNormalizePos > agreeForm, "Path normalization missing after agree form");
        Assert.True(emptyCheck > guiNormalizePos, "Empty check must follow normalization");
        Assert.True(rootCheck > emptyCheck, "Root validation must follow the empty check");
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
