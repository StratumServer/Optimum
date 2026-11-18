#nullable enable
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class JsonLineInfoOptimizationTests
{
    private sealed class Holder
    {
        [JsonConverter(typeof(JsonAttributesConverter))]
        public JsonObject? Attributes { get; set; }
    }

    [Fact]
    public void IgnoreJsonLineInfo_DefaultsTrue_AndConfigRoundTrips()
    {
        bool origEnabled = OptimumConfig.IgnoreJsonLineInfoEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-jsonline-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.IgnoreJsonLineInfoEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.IgnoreJsonLineInfo));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.IgnoreJsonLineInfoEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.IgnoreJsonLineInfoEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.IgnoreJsonLineInfoEnabled);

            OptimumConfig.IgnoreJsonLineInfoEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.IgnoreJsonLineInfoEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.IgnoreJsonLineInfoEnabled);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.IgnoreJsonLineInfoEnabled = origEnabled;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(0, false, true, false)]
    [InlineData(1, false, true, false)]
    [InlineData(2, false, true, true)]
    [InlineData(2, true, true, false)]
    [InlineData(2, false, false, false)]
    public void EffectiveIgnoreJsonLineInfo_MatchesModeDevAndToggleMatrix(
        int ramMode,
        bool developerMode,
        bool toggleEnabled,
        bool expected)
    {
        bool origToggle = OptimumConfig.IgnoreJsonLineInfoEnabled;
        int origRam = OptimumConfig.ClientOptimizeRamMode;
        bool origDev = OptimumConfig.ClientDeveloperMode;
        try
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = toggleEnabled;
            OptimumConfig.SetClientJsonLineInfoState(ramMode, developerMode);
            Assert.Equal(expected, OptimumConfig.EffectiveIgnoreJsonLineInfo);
        }
        finally
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = origToggle;
            OptimumConfig.SetClientJsonLineInfoState(origRam, origDev);
        }
    }

    [Fact]
    public void JsonParsingAndConverters_StripLineInfoOnlyWhenAggressiveAndDevOff_AndPreserveSemantics()
    {
        const string sampleJson = "{\n  \"name\": \"stone\",\n  \"nested\": { \"hardness\": 4.5, \"tags\": [\"rock\", \"solid\"] }\n}";

        bool origToggle = OptimumConfig.IgnoreJsonLineInfoEnabled;
        int origRam = OptimumConfig.ClientOptimizeRamMode;
        bool origDev = OptimumConfig.ClientDeveloperMode;
        try
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = true;

            // DeveloperMode off + Aggressive RAM (2): omits LineInfoAnnotation while preserving values.
            OptimumConfig.SetClientJsonLineInfoState(2, false);
            JToken tokAggressive = OptimumApiBridge.ParseJsonToken(sampleJson);
            JObject objAggressive = OptimumApiBridge.ParseJsonObject(sampleJson);
            JsonObject jsonObjAggressive = JsonObject.FromJson(sampleJson);
            Holder? holderAggressive = JsonConvert.DeserializeObject<Holder>("{ \"Attributes\": " + sampleJson + " }");
            JObject? fastUtilObj = JsonUtil.ToObject<JObject>(sampleJson, "game");

            Assert.False(((IJsonLineInfo)tokAggressive).HasLineInfo());
            Assert.False(((IJsonLineInfo)tokAggressive["nested"]!).HasLineInfo());
            Assert.False(((IJsonLineInfo)objAggressive).HasLineInfo());
            Assert.False(((IJsonLineInfo)jsonObjAggressive.Token!).HasLineInfo());
            Assert.False(((IJsonLineInfo)holderAggressive!.Attributes!.Token!).HasLineInfo());
            Assert.False(((IJsonLineInfo)fastUtilObj!).HasLineInfo());
            Assert.False(((IJsonLineInfo)tokAggressive.DeepClone()).HasLineInfo());

            Assert.Equal("stone", jsonObjAggressive["name"].AsString());
            Assert.Equal(4.5f, jsonObjAggressive["nested"]["hardness"].AsFloat());
            Assert.Equal(new[] { "rock", "solid" }, jsonObjAggressive["nested"]["tags"].AsArray<string>());
            Assert.Equal("stone", holderAggressive.Attributes["name"].AsString());
            Assert.Equal("stone", (string?)fastUtilObj["name"]);

            // DeveloperMode on + Aggressive RAM (2): preserves LineInfoAnnotation on JToken.Parse/ReadFrom paths.
            OptimumConfig.SetClientJsonLineInfoState(2, true);
            JToken tokDev = OptimumApiBridge.ParseJsonToken(sampleJson);
            JsonObject jsonObjDev = JsonObject.FromJson(sampleJson);
            Holder? holderDev = JsonConvert.DeserializeObject<Holder>("{ \"Attributes\": " + sampleJson + " }");

            Assert.True(((IJsonLineInfo)tokDev).HasLineInfo());
            Assert.Equal(2, ((IJsonLineInfo)tokDev["name"]!).LineNumber);
            Assert.True(((IJsonLineInfo)jsonObjDev.Token!).HasLineInfo());
            Assert.True(((IJsonLineInfo)holderDev!.Attributes!.Token!).HasLineInfo());
        }
        finally
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = origToggle;
            OptimumConfig.SetClientJsonLineInfoState(origRam, origDev);
        }
    }

    [Fact]
    public void ParseJsonToken_StillReportsSyntaxErrorLineNumbersWhenLineInfoIgnored()
    {
        const string invalidJson = "{\n  \"ok\": 1,\n  \"broken\": }\n}";

        bool origToggle = OptimumConfig.IgnoreJsonLineInfoEnabled;
        int origRam = OptimumConfig.ClientOptimizeRamMode;
        bool origDev = OptimumConfig.ClientDeveloperMode;
        try
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = true;
            OptimumConfig.SetClientJsonLineInfoState(2, false);

            var ex = Assert.Throws<JsonReaderException>(() => OptimumApiBridge.ParseJsonToken(invalidJson));
            Assert.Equal(3, ex.LineNumber);
        }
        finally
        {
            OptimumConfig.IgnoreJsonLineInfoEnabled = origToggle;
            OptimumConfig.SetClientJsonLineInfoState(origRam, origDev);
        }
    }

    [Fact]
    public void PatchContract_CecilOwnedAndApiPatchesExist()
    {
        string entityPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/EntityTypeNet.cs.patch"));
        string settingsPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSettings.cs.patch"));

        Assert.Contains("OptimumApiBridge.ParseJsonToken", entityPatch);
        Assert.Contains("OptimumConfig.SetClientJsonLineInfoState", settingsPatch);
    }
}
