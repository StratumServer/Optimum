using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// bootstrap.sh and worktree-bootstrap.sh copy Optimum-only files from sources/ into the
/// working tree, where the fork projects are built. Asset overlays and the native shader
/// tree are not project source: deploy, the packagers and the shader compiler read them
/// from sources/ directly. A root copy of one of them is stale the moment the real file
/// is edited, and an edit made to the copy is silently lost.
/// </summary>
public class BootstrapSourceSyncCoverageTests
{
    private static readonly string[] SourceOnlyTrees = { "lang", "shaders", "shaderincludes", "shaders-vk" };

    [Theory]
    [InlineData("scripts/bootstrap.sh")]
    [InlineData("scripts/dev/worktree-bootstrap.sh")]
    public void TheSourceSyncSkipsEveryTreeThatIsReadFromSourcesDirectly(string script)
    {
        string text = File.ReadAllText(Path.Combine(Root(), script));
        Match skip = Regex.Match(text, @"case ""\$top(?:_proj)?"" in\s+([a-z|\-]+)\)\s+continue");
        Assert.True(skip.Success, script + " no longer has the sources/ skip list");

        string[] skipped = skip.Groups[1].Value.Split('|');
        foreach (string tree in SourceOnlyTrees)
        {
            Assert.Contains(tree, skipped);
        }
    }

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }
}
