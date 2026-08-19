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

        // The GUI click handler must detect a bare drive letter after TrimEnd
        // and append a backslash BEFORE any GetFullPath call.
        Assert.Contains("if ($dir -match '^[A-Za-z]:$')", script);
        Assert.Contains("$dir = \"$dir\\\"", script);

        // The normalization must appear BEFORE the VS-overlap GetFullPath check.
        int normPos = script.IndexOf("if ($dir -match '^[A-Za-z]:$')");
        int getFullPathPos = script.IndexOf("[System.IO.Path]::GetFullPath($dir)");
        Assert.True(normPos < getFullPathPos,
            "Drive letter normalization must precede the first GetFullPath($dir) call");
    }

    [Fact]
    public void InvokeOptimumBuild_NormalizesBareDriverLetter()
    {
        string script = Read("scripts/install-windows.ps1");

        // Invoke-OptimumBuild must also normalize bare drive letters from CLI args.
        Assert.Contains("if ($InstallDir -match '^[A-Za-z]:$')", script);
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

        // Expected order in btnInstall click handler (search from the agree form):
        // 1. Trim + TrimEnd
        // 2. Empty check
        // 3. Bare drive letter normalization
        // 4. GetFullPath for VS overlap check
        int agreeForm = script.IndexOf("if ($agreeForm.ShowDialog()");
        Assert.True(agreeForm > 0, "Agree form dialog missing");

        int trimPos = script.IndexOf("$dir = $script:txtDir.Text.Trim().TrimEnd", agreeForm);
        int emptyCheck = script.IndexOf("if (-not $dir)", trimPos);
        int driveNorm = script.IndexOf("if ($dir -match '^[A-Za-z]:$')", trimPos);
        int fullPath = script.IndexOf("[System.IO.Path]::GetFullPath($dir)", trimPos);

        Assert.True(trimPos > agreeForm, "Trim step missing after agree form");
        Assert.True(emptyCheck > trimPos, "Empty check must follow trim");
        Assert.True(driveNorm > emptyCheck, "Drive normalization must follow empty check");
        Assert.True(fullPath > driveNorm, "GetFullPath must follow drive normalization");
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
