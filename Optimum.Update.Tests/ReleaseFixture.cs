using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimum.Update;

namespace Optimum.Update.Tests;

/// <summary>A fake GitHub release: a signed manifest, its signature and a real tar.gz overlay, served from memory.</summary>
public sealed class ReleaseFixture : IDisposable
{
    public const string Base = "https://releases.test/";

    public string Version { get; init; } = "0.3.22";
    public string Rid { get; init; } = "win-x64";
    public string[] GameVersions { get; init; } = { "1.22.7" };

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    /// <summary>Relative path inside the overlay folder to the file bytes. Built by the constructor, edited by tests before <see cref="Build"/>.</summary>
    public Dictionary<string, byte[]> Files { get; } = new()
    {
        [".optimum/donors/VintagestoryLib.Donor.dll"] = Encoding.ASCII.GetBytes("donor-lib"),
        [".optimum/version"] = Encoding.ASCII.GetBytes("0.3.22"),
        ["optimum.exe"] = Encoding.ASCII.GetBytes("cli"),
        ["patcher/Optimum.Patcher.dll"] = Encoding.ASCII.GetBytes("patcher"),
        ["assets/game/lang/en.json"] = Encoding.UTF8.GetBytes("{\"optimum-x\":\"y\"}"),
    };

    /// <summary>Extra tar entries written verbatim, by full entry name, for hostile-archive tests.</summary>
    public List<(string Name, TarEntryType Type, byte[] Data, string? LinkName)> ExtraEntries { get; } = new();

    /// <summary>When set, the archive carries these files while the manifest still lists <see cref="Files"/>.</summary>
    public Dictionary<string, byte[]>? ArchiveFiles { get; set; }

    public Func<string, string, string>? ManifestEdit { get; set; }
    public bool SignManifest { get; set; } = true;
    public byte[]? ArchiveOverride { get; set; }
    public Dictionary<string, byte[]> Served { get; } = new();

    public string TopFolder => $"Optimum-v{Version}-{Rid}-overlay";
    public string ArchiveName => $"{TopFolder}.tar.gz";

    public byte[] ManifestBytes { get; private set; } = Array.Empty<byte>();
    public byte[] ArchiveBytes { get; private set; } = Array.Empty<byte>();

    public ReleaseFixture Build()
    {
        ArchiveBytes = ArchiveOverride ?? BuildArchive();
        var files = Files.Select(f => new { path = f.Key, size = f.Value.Length, sha256 = "sha256:" + Hex(f.Value) }).ToArray();
        var manifest = new
        {
            manifestVersion = 1,
            optimumVersion = Version,
            supportedGameVersions = GameVersions,
            rid = Rid,
            archive = new { filename = ArchiveName, size = ArchiveBytes.Length, sha256 = "sha256:" + Hex(ArchiveBytes) },
            targets = Array.Empty<object>(),
            files,
        };
        string json = JsonSerializer.Serialize(manifest);
        if (ManifestEdit is not null) json = ManifestEdit(json, Version);
        ManifestBytes = Encoding.UTF8.GetBytes(json);

        string tag = "v" + Version;
        Served[$"{tag}/optimum-manifest-{Rid}.json"] = ManifestBytes;
        if (SignManifest)
            Served[$"{tag}/optimum-manifest-{Rid}.json.sig"] = Key.SignData(ManifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Served[$"{tag}/{ArchiveName}"] = ArchiveBytes;
        return this;
    }

    private byte[] BuildArchive()
    {
        using var raw = new MemoryStream();
        using (var gzip = new GZipStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, TopFolder + "/"));
            foreach ((string path, byte[] data) in ArchiveFiles ?? Files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{TopFolder}/{path}") { DataStream = new MemoryStream(data) };
                writer.WriteEntry(entry);
            }
            foreach ((string name, TarEntryType type, byte[] data, string? link) in ExtraEntries)
            {
                TarEntry entry = type switch
                {
                    TarEntryType.SymbolicLink => new PaxTarEntry(type, name) { LinkName = link! },
                    TarEntryType.Directory => new PaxTarEntry(type, name),
                    _ => new PaxTarEntry(type, name) { DataStream = new MemoryStream(data) },
                };
                writer.WriteEntry(entry);
            }
        }
        return raw.ToArray();
    }

    public HttpClient Client(Func<string, HttpResponseMessage?>? intercept = null)
        => new(new FakeHandler(Served, intercept));

    public static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose() => Key.Dispose();

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> served;
        private readonly Func<string, HttpResponseMessage?>? intercept;

        public FakeHandler(Dictionary<string, byte[]> served, Func<string, HttpResponseMessage?>? intercept)
        {
            this.served = served;
            this.intercept = intercept;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => Respond(request, cancellationToken);

        private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsoluteUri.Substring(Base.Length);
            HttpResponseMessage? custom = intercept?.Invoke(path);
            if (custom is not null) return custom;
            if (!served.TryGetValue(path, out byte[]? bytes))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}

/// <summary>A scratch directory removed after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optu-" + Guid.NewGuid().ToString("N").Substring(0, 10));

    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>A game folder laid out the way `optimum patch` leaves it.</summary>
    public string PatchedInstall()
    {
        string state = System.IO.Path.Combine(Path, ".optimum");
        Directory.CreateDirectory(System.IO.Path.Combine(state, "vanilla"));
        File.WriteAllText(System.IO.Path.Combine(state, "manifest.json"), "{}");
        File.WriteAllText(System.IO.Path.Combine(state, "vanilla", "VintagestoryLib.vanilla.dll"), "vanilla");
        return Path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
    }
}
