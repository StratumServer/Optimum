using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Prerequisites;
using Optimum.Bootstrap.Core.Tests;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class GitInstallerTests
{
    [Fact]
    public async Task TriesBusyBoxPortableVariantWhenRegularMinGitFailsDigestCheck()
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData);
            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(archive, digest, corruptRegularAsset: true);
            using var http = new HttpClient(handler);
            var activatedPaths = new List<string>();
            string gitPath = GitInstaller.PortableExecutable(probe);
            probe.OnCommand(gitPath, "--version", "git version 2.56.0.windows.3\n");
            var installer = new GitInstaller(probe, http,
                directory => activatedPaths.Add(directory));

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(gitPath, result.InstalledPath);
            Assert.True(File.Exists(gitPath));
            Assert.Equal(Path.GetDirectoryName(gitPath), activatedPaths[0]);
            Assert.Equal(
                ["MinGit-2.56.0.3-64-bit.zip", "MinGit-2.56.0.3-busybox-64-bit.zip"],
                handler.DownloadedAssets);
            probe.Path.Insert(0, activatedPaths[0]);
            probe.AddFile(gitPath);
            PrerequisiteResult detected = new PrerequisiteScanner(probe, "").ScanGit();
            Assert.Equal(PrerequisiteState.Ok, detected.State);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public async Task FallsBackToOfficialReleasePageWhenGitHubApiIsRateLimited()
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData);
            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(archive, digest,
                corruptRegularAsset: true, rateLimitApi: true);
            using var http = new HttpClient(handler);
            string gitPath = GitInstaller.PortableExecutable(probe);
            probe.OnCommand(gitPath, "--version", "git version 2.56.0.windows.3\n");
            var installer = new GitInstaller(probe, http, _ => { });

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(gitPath, result.InstalledPath);
            Assert.Equal(1, handler.ReleasePageRequests);
            Assert.Equal(
                ["MinGit-2.56.0.3-64-bit.zip", "MinGit-2.56.0.3-busybox-64-bit.zip"],
                handler.DownloadedAssets);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsPortableArchiveWhenPublishedDigestDoesNotMatch()
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData);
            byte[] archive = PortableArchive();
            var handler = new ReleaseHandler(archive, new string('0', 64));
            using var http = new HttpClient(handler);
            var installer = new GitInstaller(probe, http,
                _ => { });

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("SHA-256", result.Message);
            Assert.False(Directory.Exists(GitInstaller.CacheDirectory(probe)));
            Assert.Equal(2, handler.DownloadedAssets.Count);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public async Task RestoresExistingCacheWhenEveryPortableGitFailsVersionVerification()
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData);
            string cache = GitInstaller.CacheDirectory(probe);
            string gitPath = GitInstaller.PortableExecutable(probe);
            Directory.CreateDirectory(Path.GetDirectoryName(gitPath)!);
            File.WriteAllText(gitPath, "previous cache marker");
            probe.AddFile(gitPath);
            probe.OnCommand(gitPath, "--version", "not Git\n", exitCode: 1);

            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(archive, digest);
            using var http = new HttpClient(handler);
            var installer = new GitInstaller(probe, http, _ => { });

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("git --version did not succeed", result.Message);
            Assert.Equal("previous cache marker", File.ReadAllText(gitPath));
            Assert.True(Directory.Exists(cache));
            Assert.Equal(2, handler.DownloadedAssets.Count);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Theory]
    [InlineData(Architecture.X64, "MinGit-2.56.1-64-bit.zip")]
    [InlineData(Architecture.Arm64, "MinGit-2.56.1-arm64.zip")]
    [InlineData(Architecture.X86, "MinGit-2.56.1-32-bit.zip")]
    public async Task InstallsPublishedMinGitAssetForWindowsDotOneTagAcrossArchitectures(
        Architecture arch, string expectedAsset)
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData, arch);
            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(
                archive,
                digest,
                tag: "v2.56.1.windows.1",
                publishedAssets:
                [
                    "MinGit-2.56.1-32-bit.zip",
                    "MinGit-2.56.1-64-bit.zip",
                    "MinGit-2.56.1-arm64.zip",
                    "MinGit-2.56.1-busybox-32-bit.zip",
                    "MinGit-2.56.1-busybox-64-bit.zip",
                    "MinGit-2.56.1-busybox-arm64.zip",
                    "MinGit-2.56.1-mingw64.zip",
                ]);
            using var http = new HttpClient(handler);
            var activatedPaths = new List<string>();
            string gitPath = GitInstaller.PortableExecutable(probe);
            probe.OnCommand(gitPath, "--version", "git version 2.56.1.windows.1\n");
            var installer = new GitInstaller(probe, http, directory => activatedPaths.Add(directory));

            Assert.Equal(PrerequisiteState.Missing, new PrerequisiteScanner(probe, "").ScanGit().State);

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(gitPath, result.InstalledPath);
            Assert.Equal([expectedAsset], handler.DownloadedAssets);
            Assert.Single(activatedPaths);
            Assert.Equal(Path.GetDirectoryName(gitPath), activatedPaths[0]);

            probe.Path.Insert(0, activatedPaths[0]);
            probe.AddFile(gitPath);
            PrerequisiteResult detected = new PrerequisiteScanner(probe, "").ScanGit();
            Assert.Equal(PrerequisiteState.Ok, detected.State);
            Assert.False(detected.BlocksBuild);
            Assert.Equal(gitPath.Replace('\\', '/'), detected.DetectedPath!.Replace('\\', '/'));
            Assert.Equal("2.56.1.windows.1", detected.DetectedVersion);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Theory]
    [InlineData("v2.56.1.windows.1", Architecture.X64, "MinGit-2.56.1-64-bit.zip", new[] { "MinGit-2.56.1-64-bit.zip", "MinGit-2.56.1-busybox-64-bit.zip" })]
    [InlineData("v2.56.1.windows.1", Architecture.Arm64, "MinGit-2.56.1-arm64.zip", new[] { "MinGit-2.56.1-arm64.zip", "MinGit-2.56.1-busybox-arm64.zip" })]
    [InlineData("v2.56.1.windows.1", Architecture.X86, "MinGit-2.56.1-32-bit.zip", new[] { "MinGit-2.56.1-32-bit.zip", "MinGit-2.56.1-busybox-32-bit.zip" })]
    [InlineData("v2.56.0.windows.2", Architecture.X64, "MinGit-2.56.0-64-bit.zip", new[] { "MinGit-2.56.0-64-bit.zip", "MinGit-2.56.0-busybox-64-bit.zip" })]
    [InlineData("v2.56.1", Architecture.X64, "MinGit-2.56.1-64-bit.zip", new[] { "MinGit-2.56.1-64-bit.zip" })]
    [InlineData("v2.56.1.windows.1", Architecture.X64, "MinGit-2.56.1.1-64-bit.zip", new[] { "MinGit-2.56.1.1-64-bit.zip" })]
    public async Task ResolvesPublishedMinGitAssetAcrossTagAndFilenameShapesWhenRateLimited(
        string tag, Architecture arch, string expectedAsset, string[] publishedAssets)
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData, arch);
            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(
                archive,
                digest,
                rateLimitApi: true,
                tag: tag,
                publishedAssets: publishedAssets);
            using var http = new HttpClient(handler);
            string gitPath = GitInstaller.PortableExecutable(probe);
            probe.OnCommand(gitPath, "--version", "git version 2.56.1.windows.1\n");
            var installer = new GitInstaller(probe, http, _ => { });

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(1, handler.ReleasePageRequests);
            Assert.Equal([expectedAsset], handler.DownloadedAssets);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public async Task FallsBackToBusyBoxWhenOnlyBusyBoxIsPublishedAndRejectsTamperedDigest()
    {
        string localAppData = NewTempDirectory();
        try
        {
            FakeSystemProbe probe = WindowsProbe(localAppData);
            byte[] archive = PortableArchive();
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var busyboxHandler = new ReleaseHandler(
                archive,
                digest,
                tag: "v2.56.1.windows.1",
                publishedAssets: ["MinGit-2.56.1-busybox-64-bit.zip"]);
            using var httpOk = new HttpClient(busyboxHandler);
            string gitPath = GitInstaller.PortableExecutable(probe);
            probe.OnCommand(gitPath, "--version", "git version 2.56.1.windows.1\n");
            var installer = new GitInstaller(probe, httpOk, _ => { });

            ToolAcquisitionResult result = await installer.InstallAsync(NullObserver.Instance, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(["MinGit-2.56.1-busybox-64-bit.zip"], busyboxHandler.DownloadedAssets);

            // Fresh cache check: tampered digest on MinGit-2.56.1-64-bit.zip must still reject install.
            string badLocalAppData = NewTempDirectory();
            FakeSystemProbe badProbe = WindowsProbe(badLocalAppData);
            try
            {
                var badHandler = new ReleaseHandler(
                    archive,
                    new string('0', 64),
                    tag: "v2.56.1.windows.1",
                    publishedAssets: ["MinGit-2.56.1-64-bit.zip", "MinGit-2.56.1-busybox-64-bit.zip"]);
                using var httpBad = new HttpClient(badHandler);
                var badInstaller = new GitInstaller(badProbe, httpBad, _ => { });

                ToolAcquisitionResult failed = await badInstaller.InstallAsync(NullObserver.Instance, CancellationToken.None);

                Assert.False(failed.Ok);
                Assert.Contains("SHA-256", failed.Message);
                Assert.Equal(
                    ["MinGit-2.56.1-64-bit.zip", "MinGit-2.56.1-busybox-64-bit.zip"],
                    badHandler.DownloadedAssets);
            }
            finally
            {
                if (Directory.Exists(badLocalAppData))
                    Directory.Delete(badLocalAppData, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    private static FakeSystemProbe WindowsProbe(string localAppData, Architecture arch = Architecture.X64) => new()
    {
        Os = Optimum.Bootstrap.Core.Platform.OsKind.Windows,
        Arch = arch,
        HomeDirectory = "C:/Users/tester",
        Environment = { ["LOCALAPPDATA"] = localAppData },
    };

    private static byte[] PortableArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (Stream entry = archive.CreateEntry("cmd/git.exe").Open())
            entry.Write("test git executable"u8);
        return stream.ToArray();
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ReleaseHandler(
        byte[] archive,
        string digest,
        bool corruptRegularAsset = false,
        bool rateLimitApi = false,
        string tag = "v2.56.0.windows.3",
        string[]? publishedAssets = null) : HttpMessageHandler
    {
        private readonly string[] _publishedAssets = publishedAssets ??
        [
            "MinGit-2.56.0.3-64-bit.zip",
            "MinGit-2.56.0.3-busybox-64-bit.zip",
        ];

        public List<string> DownloadedAssets { get; } = [];
        public int ReleasePageRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("api.github.com", StringComparison.Ordinal))
            {
                if (rateLimitApi)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        RequestMessage = request,
                        Content = new StringContent("{\"message\":\"API rate limit exceeded\"}",
                            Encoding.UTF8, "application/json"),
                    });
                }

                string assetJson = string.Join(",\n", _publishedAssets.Select(name =>
                {
                    string assetDigest = corruptRegularAsset && !name.Contains("busybox", StringComparison.Ordinal)
                        ? new string('0', 64)
                        : digest;
                    return $$"""
                    {
                      "name": "{{name}}",
                      "browser_download_url": "https://github.com/git-for-windows/git/releases/download/{{tag}}/{{name}}",
                      "digest": "sha256:{{assetDigest}}"
                    }
                    """;
                }));
                string metadata = $$"""
                {
                  "tag_name": "{{tag}}",
                  "assets": [
                {{assetJson}}
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsoluteUri == "https://github.com/git-for-windows/git/releases/latest")
            {
                ReleasePageRequests++;
                string rows = string.Join("\n", _publishedAssets.Select(name =>
                {
                    string assetDigest = corruptRegularAsset && !name.Contains("busybox", StringComparison.Ordinal)
                        ? new string('0', 64)
                        : digest;
                    return $"        <tr><td>{name}</td><td>{assetDigest}</td></tr>";
                }));
                string html = $$"""
                    <html>
                      <table><tbody>
                    {{rows}}
                      </tbody></table>
                    </html>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        $"https://github.com/git-for-windows/git/releases/tag/{tag}"),
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                });
            }

            DownloadedAssets.Add(Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
            });
        }
    }

    private sealed class NullObserver : IBuildObserver
    {
        public static readonly NullObserver Instance = new();
        public void Phase(ProgressPhase phase, int percent, string detail) { }
        public void Log(LogLevel level, string message) { }
        public void RawOutput(bool isError, string line) { }
    }
}
