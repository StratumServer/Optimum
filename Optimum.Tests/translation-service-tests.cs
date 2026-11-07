using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class TranslationServiceTests
{
    private sealed class SilentLogger : LoggerBase
    {
        protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
    }

    private sealed class DummyAsset : IAsset
    {
        public string Name { get; set; } = "";
        public AssetLocation Location { get; set; } = new AssetLocation("game", "");
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public IAssetOrigin Origin { get; set; } = null!;
        public bool IsPatched { get; set; }
        public bool IsLoaded() => true;
        public string ToText() => Encoding.UTF8.GetString(Data);
        public BitmapRef ToBitmap(ICoreClientAPI capi) => null!;
        public T ToObject<T>(JsonSerializerSettings? settings = null) => JsonConvert.DeserializeObject<T>(ToText(), settings)!;
    }

    private sealed class DummyAssetOrigin : IAssetOrigin
    {
        public string OriginPath => "";
        public List<IAsset> LangAssets { get; } = new();

        public List<IAsset> GetAssets(AssetCategory category, bool shouldLoad = true)
        {
            if (category == AssetCategory.lang) return LangAssets;
            return new List<IAsset>();
        }

        public List<IAsset> GetAssets(AssetLocation baseLocation, bool shouldLoad = true) => new();
        public bool IsAllowedToAffectGameplay() => true;
        public void LoadAsset(IAsset asset) { }
        public bool TryLoadAsset(IAsset asset) => true;
    }

    private sealed class DummyAssetManager : IAssetManager
    {
        public List<IAssetOrigin> Origins { get; } = new();
        public Dictionary<AssetLocation, IAsset> AllAssets => new();
        public bool Exists(AssetLocation location) => false;
        public void Add(AssetLocation path, IAsset asset) { }
        public IAsset Get(AssetLocation Location) => null!;
        public IAsset TryGet(AssetLocation Location, bool loadAsset = true) => null!;
        public List<IAsset> GetMany(string pathBegins, string domain = null, bool loadAsset = true) => new();
        public List<IAsset> GetManyInCategory(string categoryCode, string pathBegins, string domain = null, bool loadAsset = true) => new();
        public Dictionary<AssetLocation, T> GetMany<T>(ILogger logger, string pathBegins, string domain = null) => new();
        public T Get<T>(AssetLocation Location) => default!;
        public int Reload(AssetCategory category) => 0;
        public int Reload(AssetLocation location) => 0;
        public void AddModOrigin(string modDomain, string fullPath) { }
        public void AddModOrigin(string modDomain, string fullPath, string originPath) { }
        public void RemoveModOrigin(string modDomain, string originPath) { }
        public void AddPathOrigin(string domain, string fullPath) { }
        public List<AssetLocation> GetLocations(string pathBegins, string domain = null) => new();
    }

    [Fact]
    public void Load_LoadsOnlyActiveLanguageAndWorldConfig_PreservingParity()
    {
        var assetManager = new DummyAssetManager();
        var origin = new DummyAssetOrigin();

        // Active language: "pt-br"
        origin.LangAssets.Add(new DummyAsset
        {
            Name = "pt-br.json",
            Location = new AssetLocation("game", "lang/pt-br.json"),
            Data = Encoding.UTF8.GetBytes("{\"game:greeting\": \"Olá mundo\", \"game:farewell\": \"Até logo\"}")
        });

        // Worldconfig for active language
        origin.LangAssets.Add(new DummyAsset
        {
            Name = "worldconfig-pt-br.json",
            Location = new AssetLocation("game", "lang/worldconfig-pt-br.json"),
            Data = Encoding.UTF8.GetBytes("{\"worldconfig-lore\": \"Verdadeiro\"}")
        });

        // Inactive language files that must NOT overwrite or contaminate
        origin.LangAssets.Add(new DummyAsset
        {
            Name = "en.json",
            Location = new AssetLocation("game", "lang/en.json"),
            Data = Encoding.UTF8.GetBytes("{\"game:greeting\": \"Hello world\", \"game:english_only\": \"Only in English\"}")
        });
        origin.LangAssets.Add(new DummyAsset
        {
            Name = "de.json",
            Location = new AssetLocation("game", "lang/de.json"),
            Data = Encoding.UTF8.GetBytes("{\"game:greeting\": \"Hallo Welt\"}")
        });

        assetManager.Origins.Add(origin);

        var service = new TranslationService("pt-br", new SilentLogger(), assetManager);
        service.Load();

        Assert.True(service.HasTranslation("game:greeting"));
        Assert.Equal("Olá mundo", service.Get("game:greeting"));
        Assert.True(service.HasTranslation("game:farewell"));
        Assert.Equal("Até logo", service.Get("game:farewell"));
        Assert.True(service.HasTranslation("worldconfig-lore"));
        Assert.Equal("Verdadeiro", service.Get("worldconfig-lore"));

        // Non-active language keys should not have been loaded into this service's cache
        Assert.False(service.HasTranslation("game:english_only"));
    }

    [Fact]
    public void TranslationServicePatch_VerifiesNonAllocatingOriginLoop()
    {
        string patchPath = PatchReader.FindRepositoryFile("patches/VintagestoryApi/Localization/TranslationService.cs.patch");
        Assert.True(File.Exists(patchPath), "TranslationService.cs.patch must exist");
        string patchedContent = PatchReader.ReadPatchedContent(patchPath);
        Assert.Contains("string langFileName = LanguageCode + \".json\";", patchedContent);
        Assert.Contains("string worldconfigLangFileName = \"worldconfig-\" + LanguageCode + \".json\";", patchedContent);
        Assert.DoesNotContain("origins.SelectMany", patchedContent);
    }
}
