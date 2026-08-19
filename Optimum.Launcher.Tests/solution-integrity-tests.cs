using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Optimum.Launcher.Tests;

public class SolutionIntegrityTests
{
    [Fact]
    public void EverySolutionProjectPathExists()
    {
        var root = FindRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root.FullName, "VintageStory.slnx"));
        var projectPaths = solution
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(projectPaths);
        foreach (var projectPath in projectPaths)
        {
            var fullPath = Path.GetFullPath(projectPath, root.FullName);
            Assert.True(File.Exists(fullPath), $"Solution project not found: {projectPath}");
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
