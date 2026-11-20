using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class ClientArchiveAcquisitionTests
{
    private const string Version = "1.22.7";

    [Fact]
    public async Task InterruptedTransferResumesFromTheSavedOffsetWithTheSameEntityTag()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        using var cancellation = new CancellationTokenSource();
        using var interruptedHttp = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\"", cancellation));
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), interruptedHttp, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan firstPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.EnsureAsync(
                firstPlan, null, cancellation.Token));

            string partial = firstPlan.ArchivePath + ".partial";
            long savedBytes = new FileInfo(partial).Length;
            Assert.InRange(savedBytes, 1, archive.Length - 1);
            Assert.Equal("\"archive-v1\"", File.ReadAllText(partial + ".etag"));

            using var resumedHttp = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));
            var resumedAcquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), resumedHttp, new FixedStorage(), VanillaArchivePins.None);
            ClientArchivePlan retryPlan = await resumedAcquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.True(retryPlan.PartialCanResume);
            Assert.Contains("retry resumes", retryPlan.Summary);
            string resolved = await resumedAcquisition.EnsureAsync(retryPlan, null, CancellationToken.None);

            Assert.Equal(retryPlan.ArchivePath, resolved);
            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
            Assert.False(File.Exists(partial));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedEntityTagDiscardsTheOldPartialAndStartsFromZero()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v2\"");
        using var http = new HttpClient(handler);
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath + ".partial", [1, 2, 3]);
            await File.WriteAllTextAsync(plan.ArchivePath + ".partial.etag", "\"archive-v1\"");

            ClientArchivePlan retryPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Assert.False(retryPlan.PartialCanResume);

            string resolved = await acquisition.EnsureAsync(retryPlan, null, CancellationToken.None);

            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
            Assert.Equal(0, handler.RangeRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesOnlyAnArchiveWithTheExpectedSizeAndValidClientContents()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"");
        using var http = new HttpClient(handler);
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, archive);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(archive, "\"archive-v1\""));

            ClientArchivePlan cachedPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.True(cachedPlan.CachedArchiveIsValid);
            Assert.Contains("client archive cached", cachedPlan.Summary);
            Assert.Equal(plan.ExpectedBytes, cachedPlan.ExpectedBytes);
            Assert.Equal(plan.ArchivePath, await acquisition.EnsureAsync(cachedPlan, null, CancellationToken.None));
            Assert.Equal(0, handler.GetRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesValidatedCacheWithoutRequiringTheCdn()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"");
        using var http = new HttpClient(handler);
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, archive);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(archive, "\"archive-v1\""));
            int headRequestsBeforeRetry = handler.HeadRequests;
            handler.FailHead = true;

            ClientArchivePlan cachedPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.True(cachedPlan.CachedArchiveIsValid);
            Assert.Equal(headRequestsBeforeRetry, handler.HeadRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsSameSizeWindowsCacheWhenItsRecordedDigestDiffers()
    {
        string repo = NewRepo();
        byte[] installer = CreateWindowsInstaller();
        var probe = new FakeSystemProbe { Os = OsKind.Windows, Arch = Architecture.X64 };
        using var http = new HttpClient(new ArchiveHandler(installer, "\"archive-v1\""));
        var acquisition = new ClientArchiveAcquisition(probe, http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, installer);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(installer, "\"archive-v1\""));
            installer[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(plan.ArchivePath, installer);

            ClientArchivePlan retryPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.False(retryPlan.CachedArchiveIsValid);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ValidatedCacheInspectionObservesCancellation()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        using var http = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, archive);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(archive, "\"archive-v1\""));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                acquisition.InspectAsync(repo, Version, cancellation.Token));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task DiscardsAnInvalidCachedArchiveAndDownloadsItAgain()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"");
        using var http = new HttpClient(handler);
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan initialPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(initialPlan.ArchivePath)!);
            await File.WriteAllBytesAsync(initialPlan.ArchivePath, new byte[archive.Length]);

            ClientArchivePlan retryPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Assert.False(retryPlan.CachedArchiveIsValid);

            string resolved = await acquisition.EnsureAsync(retryPlan, null, CancellationToken.None);

            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
            Assert.Equal(1, handler.GetRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RestartsPartialWhenEndpointDoesNotAdvertiseRangeSupport()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"", advertiseRanges: false);
        using var http = new HttpClient(handler);
        var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath + ".partial", [1, 2, 3]);
            await File.WriteAllTextAsync(plan.ArchivePath + ".partial.etag", "\"archive-v1\"");

            ClientArchivePlan retryPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Assert.False(retryPlan.PartialCanResume);
            await acquisition.EnsureAsync(retryPlan, null, CancellationToken.None);

            Assert.Equal(0, handler.RangeRequests);
            Assert.Equal(archive, await File.ReadAllBytesAsync(retryPlan.ArchivePath));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Theory]
    [InlineData(OsKind.Linux, Architecture.X64, "vs_client_linux-x64_1.22.7.tar.gz")]
    [InlineData(OsKind.Linux, Architecture.Arm64, "vs_client_linux-arm64_1.22.7.tar.gz")]
    [InlineData(OsKind.MacOs, Architecture.X64, "vs_client_osx-x64_1.22.7.tar.gz")]
    [InlineData(OsKind.MacOs, Architecture.Arm64, "vs_client_osx-arm64_1.22.7.tar.gz")]
    [InlineData(OsKind.Windows, Architecture.X64, "vs_install_win-x64_1.22.7.exe")]
    public async Task SelectsTheOfficialArchiveForEachSupportedPlatform(
        OsKind os,
        Architecture architecture,
        string fileName)
    {
        string repo = NewRepo();
        using var http = new HttpClient(new ArchiveHandler(CreateClientArchive(Version), "\"archive-v1\""));
        var probe = new FakeSystemProbe { Os = os, Arch = architecture };
        var acquisition = new ClientArchiveAcquisition(probe, http, new FixedStorage(), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Assert.EndsWith(fileName, plan.Url, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsLowSpaceAndThePerPlatformDownloadEstimate()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        using var http = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));
        var acquisition = new ClientArchiveAcquisition(
            new FakeSystemProbe(), http, new FixedStorage(availableBytes: 512L * 1024 * 1024), VanillaArchivePins.None);

        try
        {
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.Equal(archive.LongLength, plan.ExpectedBytes);
            Assert.NotNull(plan.SpaceWarning);
            Assert.Equal(3L * 1024 * 1024 * 1024 + archive.LongLength, plan.SourceRequiredBytes);
            Assert.Equal(1024L * 1024 * 1024, plan.TemporaryRequiredBytes);
            Assert.Contains("Estimated space needed", plan.Summary);
            Assert.Contains("cancel", plan.SpaceWarning, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsADownloadThatMatchesThePinnedOfficialArchive()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        using var http = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));

        try
        {
            ClientArchiveAcquisition acquisition = await PinnedAsync(repo, http, archive, Sha256Of(archive));
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            string resolved = await acquisition.EnsureAsync(plan, null, CancellationToken.None);

            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsADownloadThatIsNotTheOfficialArchiveAndKeepsNothing()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        byte[] officialSameSize = (byte[])archive.Clone();
        officialSameSize[^1] ^= 0xFF;
        using var http = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));

        try
        {
            ClientArchiveAcquisition acquisition = await PinnedAsync(repo, http, archive, Sha256Of(officialSameSize));
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                acquisition.EnsureAsync(plan, null, CancellationToken.None));

            Assert.Contains("not the official Vintage Story", error.Message);
            Assert.Contains(Sha256Of(archive).ToLowerInvariant(), error.Message);
            Assert.False(File.Exists(plan.ArchivePath));
            Assert.False(File.Exists(plan.ArchivePath + ".partial"));
            Assert.False(File.Exists(plan.ArchivePath + ".meta"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesAServerFileOfAnotherSizeBeforeDownloadingAnything()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"");
        using var http = new HttpClient(handler);

        try
        {
            var pins = new VanillaArchivePins(
                new Dictionary<string, VanillaArchivePin> { [LinuxArchiveName] = new(archive.Length + 1, Sha256Of(archive)) },
                new Dictionary<string, IReadOnlyDictionary<string, string>>());
            var acquisition = new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), pins);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                acquisition.InspectAsync(repo, Version, CancellationToken.None));

            Assert.Contains("Nothing was downloaded", error.Message);
            Assert.Equal(0, handler.GetRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadsAgainWhenTheCachedArchiveIsNotThePinnedOne()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        byte[] tampered = (byte[])archive.Clone();
        tampered[^1] ^= 0xFF;
        using var http = new HttpClient(new ArchiveHandler(archive, "\"archive-v1\""));

        try
        {
            ClientArchiveAcquisition acquisition = await PinnedAsync(repo, http, archive, Sha256Of(archive));
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, tampered);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(tampered, "\"archive-v1\""));

            ClientArchivePlan retryPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Assert.False(retryPlan.CachedArchiveIsValid);

            string resolved = await acquisition.EnsureAsync(retryPlan, null, CancellationToken.None);

            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsADownloadThatDoesNotMatchTheMd5TheOfficialManifestPublishes()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"") { ManifestMd5 = new string('0', 32) };
        using var http = new HttpClient(handler);

        try
        {
            var acquisition = new ClientArchiveAcquisition(
                new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                acquisition.EnsureAsync(plan, null, CancellationToken.None));

            Assert.Equal(1, handler.ManifestRequests);
            Assert.Contains("vintagestory.at publishes", error.Message);
            Assert.False(File.Exists(plan.ArchivePath));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsADownloadThatMatchesTheMd5TheOfficialManifestPublishes()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"")
        {
            ManifestMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(archive)).ToLowerInvariant(),
        };
        using var http = new HttpClient(handler);

        try
        {
            var acquisition = new ClientArchiveAcquisition(
                new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            string resolved = await acquisition.EnsureAsync(plan, null, CancellationToken.None);

            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task APinnedInstallNeverAsksTheOfficialManifest()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"") { ManifestMd5 = new string('0', 32) };
        using var http = new HttpClient(handler);

        try
        {
            ClientArchiveAcquisition acquisition = await PinnedAsync(repo, http, archive, Sha256Of(archive));
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            string resolved = await acquisition.EnsureAsync(plan, null, CancellationToken.None);

            Assert.Equal(0, handler.ManifestRequests);
            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnavailableOfficialManifestNeverBlocksAnUnpinnedInstall()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"") { ManifestStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);

        try
        {
            var acquisition = new ClientArchiveAcquisition(
                new FakeSystemProbe(), http, new FixedStorage(), VanillaArchivePins.None);
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            string resolved = await acquisition.EnsureAsync(plan, null, CancellationToken.None);

            Assert.Equal(1, handler.ManifestRequests);
            Assert.Null(plan.OfficialMd5);
            Assert.Equal(archive, await File.ReadAllBytesAsync(resolved));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesACachedArchiveThatMatchesThePinWithoutAskingTheManifest()
    {
        string repo = NewRepo();
        byte[] archive = CreateClientArchive(Version);
        var handler = new ArchiveHandler(archive, "\"archive-v1\"");
        using var http = new HttpClient(handler);

        try
        {
            ClientArchiveAcquisition acquisition = await PinnedAsync(repo, http, archive, Sha256Of(archive));
            ClientArchivePlan plan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
            await File.WriteAllBytesAsync(plan.ArchivePath, archive);
            await File.WriteAllTextAsync(plan.ArchivePath + ".meta", ArchiveMetadata(archive, "\"archive-v1\""));

            ClientArchivePlan cachedPlan = await acquisition.InspectAsync(repo, Version, CancellationToken.None);

            Assert.True(cachedPlan.CachedArchiveIsValid);
            Assert.Equal(0, handler.GetRequests);
            Assert.Equal(0, handler.ManifestRequests);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static string LinuxArchiveName =>
        ClientArchiveAcquisition.ArchiveFileName(Version, OsKind.Linux, Architecture.X64);

    private static Task<ClientArchiveAcquisition> PinnedAsync(
        string repo, HttpClient http, byte[] archive, string officialSha256)
    {
        var pins = new VanillaArchivePins(
            new Dictionary<string, VanillaArchivePin> { [LinuxArchiveName] = new(archive.Length, officialSha256) },
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
        return Task.FromResult(new ClientArchiveAcquisition(new FakeSystemProbe(), http, new FixedStorage(), pins));
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static string NewRepo()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-client-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ArchiveMetadata(byte[] archive, string entityTag) =>
        $"{archive.Length}\n{entityTag}\n{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive))}";

    private static byte[] CreateClientArchive(string version)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(CreateEntry("vintagestory/VintagestoryLib.dll", [1, 2, 3]));
            writer.WriteEntry(CreateEntry($"vintagestory/assets/version-{version}.txt", []));
        }
        return output.ToArray();
    }

    private static byte[] CreateWindowsInstaller()
    {
        byte[] installer = new byte[128];
        installer[0] = (byte)'M';
        installer[1] = (byte)'Z';
        installer[0x3C] = 0x40;
        installer[0x40] = (byte)'P';
        installer[0x41] = (byte)'E';
        return installer;
    }

    private static UstarTarEntry CreateEntry(string name, byte[] content) => new(TarEntryType.RegularFile, name)
    {
        DataStream = new MemoryStream(content),
    };

    private sealed class FixedStorage(long availableBytes = 20L * 1024 * 1024 * 1024) : IStorageSpaceProbe
    {
        public StorageSpace? GetAvailable(string path) => new("test-volume", availableBytes);
    }

    private sealed class ArchiveHandler(
        byte[] archive,
        string entityTag,
        CancellationTokenSource? cancelAfterRead = null,
        bool advertiseRanges = true)
        : HttpMessageHandler
    {
        public bool FailHead { get; set; }
        public int HeadRequests { get; private set; }
        public int GetRequests { get; private set; }
        public int RangeRequests { get; private set; }

        public string? ManifestMd5 { get; set; }
        public HttpStatusCode ManifestStatus { get; set; } = HttpStatusCode.OK;
        public int ManifestRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "api.vintagestory.at")
            {
                ManifestRequests++;
                string manifest = ManifestMd5 is null
                    ? "{}"
                    : "{\"1.22.7\":{\"linux\":{\"filename\":\"" + ClientArchiveAcquisition.ArchiveFileName("1.22.7", OsKind.Linux, Architecture.X64)
                      + "\",\"md5\":\"" + ManifestMd5 + "\"}}}";
                return Task.FromResult(new HttpResponseMessage(ManifestStatus)
                {
                    Content = new StringContent(manifest),
                });
            }

            if (request.Method == HttpMethod.Head)
            {
                HeadRequests++;
                if (FailHead)
                    throw new HttpRequestException("CDN is offline");
                return Task.FromResult(Response(HttpStatusCode.OK, contentLength: archive.LongLength));
            }

            GetRequests++;
            if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is long offset)
            {
                RangeRequests++;
                Assert.Equal(entityTag, request.Headers.IfRange?.EntityTag?.ToString());
                var ranged = Response(HttpStatusCode.PartialContent, archive.LongLength - offset);
                ranged.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, archive.Length - 1, archive.Length);
                ranged.Content = new StreamContent(cancelAfterRead is null
                    ? new MemoryStream(archive[(int)offset..], writable: false)
                    : new CancelAfterFirstReadStream(archive[(int)offset..], cancelAfterRead));
                ranged.Content.Headers.ContentLength = archive.LongLength - offset;
                ranged.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, archive.Length - 1, archive.Length);
                return Task.FromResult(ranged);
            }

            var response = Response(HttpStatusCode.OK, archive.LongLength);
            response.Content = new StreamContent(cancelAfterRead is null
                ? new MemoryStream(archive, writable: false)
                : new CancelAfterFirstReadStream(archive, cancelAfterRead));
            response.Content.Headers.ContentLength = archive.LongLength;
            response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
            return Task.FromResult(response);
        }

        private HttpResponseMessage Response(HttpStatusCode status, long contentLength) => Response(status, (long?)contentLength);

        private HttpResponseMessage Response(HttpStatusCode status, long? contentLength)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
            if (advertiseRanges)
                response.Headers.AcceptRanges.Add("bytes");
            if (contentLength is long length)
                response.Content.Headers.ContentLength = length;
            return response;
        }
    }

    private sealed class CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation)
        : MemoryStream(bytes, writable: false)
    {
        private bool _cancelled;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_cancelled)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(cancellation.Token);
            }

            int read = base.Read(buffer.Span[..Math.Min(buffer.Length, 64)]);
            _cancelled = read > 0;
            return ValueTask.FromResult(read);
        }
    }
}
