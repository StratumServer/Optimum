using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// ClientProgram::Start shows several blocking startup dialogs through
/// clientPlatformWindows.XPlatInterface.ShowMessageBox. On Windows that call
/// goes through Eto.Wpf, which throws FileNotFoundException for
/// PresentationFramework 6.0.2.0 when the process runtimeconfig does not
/// reference Microsoft.WindowsDesktop.App. The exception replaced the real
/// startup error (empty mod list on a v0.3.20 client) and the user saw only
/// the assembly-load failure. Every dialog in Start must therefore log its
/// title and text at error level first, then call ShowMessageBox inside a
/// try/catch (Exception) that logs and continues.
///
/// Start is a Cecil transplant target (Optimum.Patcher/Program.cs), so the fix
/// stays inline: no lambdas and no helper methods. The assertions read the
/// patched content of the ClientProgram patch, same as the neighbouring
/// transplant tests, so they do not need a built donor.
/// </summary>
public class ClientProgramStartupDialogTests
{
    private const string PatchPath = "patches/VintagestoryLib/Vintagestory.Client/ClientProgram.cs.patch";

    // Start has four ShowMessageBox sites: missing assets, NVIDIA profile restart,
    // unclean install, and a second running instance.
    private const int ExpectedStartDialogSites = 4;

    private static string[] PatchedLines() =>
        PatchReader.ReadPatch(PatchPath).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void EveryStartShowMessageBoxCallIsGuarded()
    {
        string[] lines = PatchedLines();
        int[] sites = Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].Contains("XPlatInterface.ShowMessageBox("))
            .ToArray();

        Assert.Equal(ExpectedStartDialogSites, sites.Length);

        foreach (int site in sites)
        {
            string call = lines[site].Trim();
            Assert.True(site >= 3 && site + 4 < lines.Length, $"Not enough context around: {call}");

            Assert.True(lines[site - 1].Trim() == "{" && lines[site - 2].Trim() == "try",
                $"ShowMessageBox is not the body of a try block: {call}");
            Assert.True(lines[site + 1].Trim() == "}" && lines[site + 2].Trim().StartsWith("catch (Exception "),
                $"ShowMessageBox try block has no catch (Exception): {call}");
            Assert.Matches(new Regex(@"^\s*logger\.Error\("), lines[site + 4]);
        }
    }

    [Fact]
    public void EveryStartDialogIsLoggedBeforeItIsShown()
    {
        string[] lines = PatchedLines();
        int[] tries = Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].Trim() == "try"
                && i + 2 < lines.Length
                && lines[i + 2].Contains("XPlatInterface.ShowMessageBox("))
            .ToArray();

        Assert.Equal(ExpectedStartDialogSites, tries.Length);

        foreach (int t in tries)
        {
            Assert.True(t >= 1, "try at the start of the hunk");
            Assert.Matches(new Regex(@"^\s*logger\.Error\("), lines[t - 1]);
        }
    }

    [Fact]
    public void StartDialogGuardsAddNoLambdasOrHelpers()
    {
        string addedLines = string.Join('\n', System.IO.File
            .ReadAllLines(PatchReader.FindRepositoryFile(PatchPath))
            .Where(l => l.StartsWith('+') && !l.StartsWith("+++")));

        string[] dialogAdds = addedLines.Split('\n')
            .Where(l => l.Contains("logger.Error(") || l.Contains("ShowMessageBox"))
            .ToArray();

        Assert.NotEmpty(dialogAdds);
        Assert.DoesNotContain(dialogAdds, l => l.Contains("=>") || l.Contains("delegate"));
    }
}
