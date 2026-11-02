using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class SystemProbeTests
{
    [Fact]
    public void CanWriteToDirectoryChecksTheNearestExistingParent()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-write-check-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(SystemProbe.Default.CanWriteToDirectory(Path.Combine(root, "not-created", "yet")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanWriteToDirectoryRejectsAFilePath()
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-write-check-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "test");
        try
        {
            Assert.False(SystemProbe.Default.CanWriteToDirectory(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CanWriteToDirectoryRejectsAFileInTheDestinationPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-write-check-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "file");
        File.WriteAllText(file, "test");
        try
        {
            Assert.False(SystemProbe.Default.CanWriteToDirectory(Path.Combine(file, "nested")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
