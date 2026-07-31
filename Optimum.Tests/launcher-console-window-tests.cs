using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Optimum.exe used to be an Exe (console subsystem) project, which meant a
/// terminal window stayed open for the entire game session on Windows - the
/// vanilla Vintagestory.exe is WinExe and never had one. These guard against
/// that regressing, and against new code writing straight to Console (which
/// is a silent no-op with no console attached) instead of through Logger,
/// which also durably records to {dataPath}/Logs/optimum-launcher.log.
/// </summary>
public class LauncherConsoleWindowTests
{
    [Fact]
    public void LauncherProjectHasNoConsoleWindow()
    {
        string csproj = Read("Optimum.Launcher/Optimum.Launcher.csproj");
        Assert.Contains("<OutputType>WinExe</OutputType>", csproj);
    }

    [Fact]
    public void LauncherProgramRoutesOutputThroughLogger()
    {
        string program = Read("Optimum.Launcher/Program.cs");
        Assert.DoesNotMatch(new Regex(@"Console\.(Write|Error)"), program);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
