using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class GuiManagerCoverageTests
{
    private const string SourcePath = "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiManager.cs";

    [Theory]
    [InlineData("OnBlockTexturesLoaded")]
    [InlineData("OnLevelFinalize")]
    [InlineData("OnOwnPlayerDataReceived")]
    [InlineData("OnFinalizeFrame")]
    [InlineData("OnKeyDown")]
    [InlineData("OnKeyUp")]
    [InlineData("OnKeyPress")]
    [InlineData("OnMouseDown")]
    [InlineData("OnMouseUp")]
    [InlineData("OnMouseMove")]
    public void MethodIsRegisteredAsACecilTransplantTarget(string methodName)
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains($"\"Vintagestory.Client.NoObf.GuiManager\", \"{methodName}\"", programSource);
    }

    [Fact]
    public void ScratchFieldsUseLazyInitNotEagerConstruction()
    {
        // Cecil-injected fields copy the field definition only, not
        // constructor initializer IL: an eager `= new()` would stay null
        // after injection. Every scratch field must be declared bare and
        // assigned with ??= at its point of use instead.
        string source = File.ReadAllText(FindRepositoryFile(SourcePath));

        string[] fields =
        {
            "_scratchBlockTexturesLoaded", "_scratchLevelFinalize", "_scratchOwnPlayerData",
            "_scratchFinalizeFrame", "_scratchKeyDownOpened", "_scratchKeyUp", "_scratchKeyPress",
            "_scratchMouseDown", "_scratchMouseUp", "_scratchMouseMove",
        };

        foreach (var field in fields)
        {
            Assert.DoesNotContain($"{field} = new", source);
            Assert.Contains($"{field} ??= new List<GuiDialog>()", source);
        }
    }

    [Fact]
    public void RequestFocusKeepsItsOriginalWhereToListUnchanged()
    {
        // RequestFocus is deliberately not transplanted (see Program.cs's
        // comment on the GuiManager targets): its other two FindIndex
        // lambdas persist regardless of this fix, so the LINQ removal here
        // wouldn't unlock transplant capability. Confirms nobody "cleaned
        // this one up too" without also handling those lambdas.
        string source = File.ReadAllText(FindRepositoryFile(SourcePath));
        Assert.Contains("game.LoadedGuis.Where((GuiDialog d) => d != dialog).ToList()", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
