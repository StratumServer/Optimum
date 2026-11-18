#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Newtonsoft.Json;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class SoundNatFloatReuseTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private sealed class SoundHolder
    {
        [JsonConverter(typeof(SoundAttributeConverter), true, 16f)]
        public SoundAttributes? BreakSound { get; set; }

        [JsonConverter(typeof(SoundAttributeConverter), false, 12f)]
        public SoundAttributes? IdleSound { get; set; }
    }

    [Fact]
    public void ReuseCommonNatFloat_DefaultsTrue_AndConfigRoundTrips()
    {
        bool orig = OptimumConfig.ReuseCommonNatFloatEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-natfloat-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.ReuseCommonNatFloatEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.ReuseCommonNatFloat));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.ReuseCommonNatFloatEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.ReuseCommonNatFloatEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.ReuseCommonNatFloatEnabled);

            OptimumConfig.ReuseCommonNatFloatEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.ReuseCommonNatFloatEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.ReuseCommonNatFloatEnabled);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.ReuseCommonNatFloatEnabled = orig;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SoundAttributes_FieldAbiTypesRemainPublicNatFloatFields()
    {
        FieldInfo pitch = typeof(SoundAttributes).GetField(nameof(SoundAttributes.Pitch), BindingFlags.Public | BindingFlags.Instance)!;
        FieldInfo volume = typeof(SoundAttributes).GetField(nameof(SoundAttributes.Volume), BindingFlags.Public | BindingFlags.Instance)!;

        Assert.NotNull(pitch);
        Assert.NotNull(volume);
        Assert.Equal(typeof(NatFloat), pitch.FieldType);
        Assert.Equal(typeof(NatFloat), volume.FieldType);
    }

    [Fact]
    public void SoundAttributes_Clone_ReusesCommonDefaults_AndIsolatesCustomAndOffsetInstances()
    {
        bool orig = OptimumConfig.ReuseCommonNatFloatEnabled;
        try
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = true;

            var common = new SoundAttributes
            {
                Location = new AssetLocation("sounds/block/dirt"),
                Pitch = new NatFloat(1f, 0.25f, EnumDistribution.UNIFORM),
                Volume = new NatFloat(1f, 0f, EnumDistribution.UNIFORM),
                Type = EnumSoundType.Sound,
                Range = 16f
            };
            SoundAttributes clonedCommon = common.Clone();

            Assert.Same(SoundAttributes.RandomPitch, clonedCommon.Pitch);
            Assert.Same(SoundAttributes.NatFloatOne, clonedCommon.Volume);
            Assert.Equal(common, clonedCommon);

            // Non-zero offset must NOT reuse shared constants.
            var withOffset = new SoundAttributes
            {
                Location = new AssetLocation("sounds/block/stone"),
                Pitch = new NatFloat(1f, 0f, EnumDistribution.UNIFORM) { offset = 0.5f },
                Volume = new NatFloat(1f, 0.25f, EnumDistribution.UNIFORM) { offset = -0.1f }
            };
            SoundAttributes clonedOffset = withOffset.Clone();

            Assert.NotSame(SoundAttributes.NatFloatOne, clonedOffset.Pitch);
            Assert.NotSame(withOffset.Pitch, clonedOffset.Pitch);
            Assert.Equal(0.5f, clonedOffset.Pitch.offset);
            Assert.NotSame(SoundAttributes.RandomPitch, clonedOffset.Volume);
            Assert.NotSame(withOffset.Volume, clonedOffset.Volume);
            Assert.Equal(-0.1f, clonedOffset.Volume.offset);

            // Uncommon distribution / variance must clone into independent instances.
            var custom = new SoundAttributes
            {
                Location = new AssetLocation("sounds/effect/toolbreak"),
                Pitch = new NatFloat(1f, 0.1f, EnumDistribution.UNIFORM),
                Volume = new NatFloat(0.8f, 0.2f, EnumDistribution.GAUSSIAN)
            };
            SoundAttributes clonedCustom = custom.Clone();

            Assert.NotSame(custom.Pitch, clonedCustom.Pitch);
            Assert.NotSame(custom.Volume, clonedCustom.Volume);
            clonedCustom.Pitch.var = 0.99f;
            Assert.Equal(0.1f, custom.Pitch.var);

            // Fallback when disabled allocates fresh instances.
            OptimumConfig.ReuseCommonNatFloatEnabled = false;
            SoundAttributes disabledClone = common.Clone();
            Assert.NotSame(SoundAttributes.RandomPitch, disabledClone.Pitch);
            Assert.NotSame(SoundAttributes.NatFloatOne, disabledClone.Volume);
            Assert.Equal(common, disabledClone);
        }
        finally
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = orig;
        }
    }

    [Fact]
    public void SoundAttributes_BinaryRoundTrip_ReusesCommonDefaults_AndPreservesRandomizationParity()
    {
        bool orig = OptimumConfig.ReuseCommonNatFloatEnabled;
        try
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = true;

            var originalCommon = new SoundAttributes(new AssetLocation("sounds/walk/grass"), withRandomPitch: true) { Range = 12f };
            var originalCustom = new SoundAttributes
            {
                Location = new AssetLocation("sounds/creature/chicken/chick1"),
                Pitch = new NatFloat(1.1f, 0.15f, EnumDistribution.TRIANGLE) { offset = 0.05f },
                Volume = new NatFloat(0.75f, 0.1f, EnumDistribution.UNIFORM),
                Type = EnumSoundType.Entity,
                Range = 24f
            };

            byte[] bytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                originalCommon.ToBytes(bw);
                originalCustom.ToBytes(bw);
                bytes = ms.ToArray();
            }

            SoundAttributes readCommon;
            SoundAttributes readCustom;
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                readCommon = SoundAttributes.FromBytes(br, "1.22.7");
                readCustom = SoundAttributes.FromBytes(br, "1.22.7");
            }

            Assert.Same(SoundAttributes.RandomPitch, readCommon.Pitch);
            Assert.Same(SoundAttributes.NatFloatOne, readCommon.Volume);
            Assert.Equal(originalCommon, readCommon);

            Assert.NotSame(originalCustom.Pitch, readCustom.Pitch);
            Assert.Equal(1.1f, readCustom.Pitch.avg);
            Assert.Equal(0.15f, readCustom.Pitch.var);
            Assert.Equal(0.05f, readCustom.Pitch.offset);
            Assert.Equal(EnumDistribution.TRIANGLE, readCustom.Pitch.dist);
            Assert.Equal(originalCustom, readCustom);

            // Randomization parity across identical RNG seeds.
            var rngA = new Random(123456);
            var rngB = new Random(123456);
            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(originalCommon.Pitch.nextFloat(1f, rngA), readCommon.Pitch.nextFloat(1f, rngB));
                Assert.Equal(originalCommon.Volume.nextFloat(1f, rngA), readCommon.Volume.nextFloat(1f, rngB));
                Assert.Equal(originalCustom.Pitch.nextFloat(1f, rngA), readCustom.Pitch.nextFloat(1f, rngB));
            }

            // Disabled fallback allocates distinct NatFloat instances.
            OptimumConfig.ReuseCommonNatFloatEnabled = false;
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                SoundAttributes disabledCommon = SoundAttributes.FromBytes(br, "1.22.7");
                Assert.NotSame(SoundAttributes.RandomPitch, disabledCommon.Pitch);
                Assert.NotSame(SoundAttributes.NatFloatOne, disabledCommon.Volume);
                Assert.Equal(originalCommon, disabledCommon);
            }
        }
        finally
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = orig;
        }
    }

    [Fact]
    public void SoundAttributeConverter_ReadJson_ReusesCommonDefaultsForStringAndObjectForms()
    {
        bool orig = OptimumConfig.ReuseCommonNatFloatEnabled;
        try
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = true;
            const string json = """
            {
              "BreakSound": {
                "path": "block/dirt",
                "pitch": { "avg": 1.0, "var": 0.25 },
                "volume": { "avg": 1.0, "var": 0.0 }
              },
              "IdleSound": "walk/grass"
            }
            """;

            SoundHolder? parsed = JsonConvert.DeserializeObject<SoundHolder>(json);
            Assert.NotNull(parsed);
            Assert.NotNull(parsed!.BreakSound);
            Assert.NotNull(parsed.IdleSound);

            Assert.Same(SoundAttributes.RandomPitch, parsed.BreakSound!.Value.Pitch);
            Assert.Same(SoundAttributes.NatFloatOne, parsed.BreakSound!.Value.Volume);
            Assert.Same(SoundAttributes.NatFloatOne, parsed.IdleSound!.Value.Pitch);
            Assert.Same(SoundAttributes.NatFloatOne, parsed.IdleSound!.Value.Volume);
            Assert.Equal("game:sounds/block/dirt", parsed.BreakSound.Value.Location?.ToString());
            Assert.Equal("game:sounds/walk/grass", parsed.IdleSound.Value.Location?.ToString());

            // Uncommon offset in JSON stays unshared.
            const string offsetJson = """
            {
              "BreakSound": {
                "path": "block/stone",
                "pitch": { "avg": 1.0, "var": 0.0, "offset": 0.25 }
              }
            }
            """;
            SoundHolder? parsedOffset = JsonConvert.DeserializeObject<SoundHolder>(offsetJson);
            Assert.NotSame(SoundAttributes.NatFloatOne, parsedOffset!.BreakSound!.Value.Pitch);
            Assert.Equal(0.25f, parsedOffset.BreakSound!.Value.Pitch.offset);
        }
        finally
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = orig;
        }
    }

    [Fact]
    public void DiagnosticsCounters_TrackAndResetCommonNatFloatReuse()
    {
        bool orig = OptimumConfig.ReuseCommonNatFloatEnabled;
        OptimumDiagnostics.ResetAllCounters();
        try
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = true;

            _ = OptimumApiBridge.ReuseCommonNatFloat(1f, 0f, EnumDistribution.UNIFORM);
            _ = OptimumApiBridge.ReuseCommonNatFloat(1.3f, 0.2f, EnumDistribution.GAUSSIAN);
            Assert.True(OptimumDiagnostics.CommonNatFloatReused >= 1);
            Assert.True(OptimumDiagnostics.CommonNatFloatAllocated >= 1);

            string summary = OptimumDiagnostics.GetCommonNatFloatSummary();
            Assert.Contains("Optimum common NatFloat reuse:", summary);
            Assert.Contains("natReused=", summary);
            Assert.Contains("packetReused=", summary);

            OptimumDiagnostics.ResetAllCounters();
            Assert.Equal(0, OptimumDiagnostics.CommonNatFloatReused);
            Assert.Equal(0, OptimumDiagnostics.CommonNatFloatAllocated);
            Assert.Equal(0, OptimumDiagnostics.CommonPacketNatFloatReused);
            Assert.Equal(0, OptimumDiagnostics.CommonPacketNatFloatAllocated);
        }
        finally
        {
            OptimumConfig.ReuseCommonNatFloatEnabled = orig;
            OptimumDiagnostics.ResetAllCounters();
        }
    }

    [Fact]
    public void PatchContracts_AndCecilOwnership_Exist()
    {
        string collectiblePatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/CollectibleNet.cs.patch"));
        string soundAttrPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryApi/Common/SoundAttributes.cs.patch"));
        string cecilOwned = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/cecil-owned.list"));
        string programCs = File.ReadAllText(PatchReader.FindRepositoryFile(
            "Optimum.Patcher/Program.cs"));

        Assert.Contains("OptimumNatFloatPacketCache.TryGetPacket", collectiblePatch);
        Assert.Contains("SoundAttributes.RandomPitch", collectiblePatch);
        Assert.Contains("OptimumApiBridge.CloneOrReuseNatFloat", soundAttrPatch);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/CollectibleNet.cs.patch", cecilOwned);
        Assert.Contains("Optimum.OptimumNatFloatPacketCache", programCs);
        Assert.Contains("new(\"Vintagestory.Common.CollectibleNet\", \"ToPacket\", 1", programCs);
        Assert.Contains("new(\"Vintagestory.Common.CollectibleNet\", \"FromPacket\", 1", programCs);
    }

    [Fact]
    public void LivePatchedAssemblies_ReuseCommonInstances_AndPreservePacketSerialization()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot == null)
        {
            return;
        }

        string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"))
            ? "Release"
            : "Debug";
        string patchedLibPath = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib-patched.dll");
        string patchedApiPath = Path.Combine(repoRoot, "bin", cfgDir, "net10.0", "VintagestoryAPI-patched.dll");
        string contractsPath = Path.Combine(repoRoot, "bin", cfgDir, "net10.0", "Optimum.Api.Contracts.dll");
        string libDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib");

        if (!File.Exists(patchedLibPath) || !File.Exists(patchedApiPath) || !File.Exists(contractsPath))
        {
            return;
        }

        // Verify ApiPatcher.PatchSoundAttributes is idempotent on an already-patched API assembly.
        using (var apiDef = AssemblyDefinition.ReadAssembly(patchedApiPath))
        using (var contractsDef = AssemblyDefinition.ReadAssembly(contractsPath))
        {
            int idempotentCount = ApiPatcher.PatchSoundAttributes(apiDef.MainModule, contractsDef.MainModule);
            Assert.Equal(3, idempotentCount);
        }

        var alc = new AssemblyLoadContext("SoundNatFloatPatchedTest", isCollectible: true);
        alc.Resolving += (ctx, name) =>
        {
            if (name.Name == "VintagestoryAPI")
            {
                return ctx.LoadFromAssemblyPath(patchedApiPath);
            }
            if (name.Name == "Optimum.Api.Contracts")
            {
                return ctx.LoadFromAssemblyPath(contractsPath);
            }
            if (name.Name == "VintagestoryLib")
            {
                return ctx.LoadFromAssemblyPath(patchedLibPath);
            }
            foreach (string dir in new[] { Path.GetDirectoryName(contractsPath)!, libDir, AppContext.BaseDirectory })
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return ctx.LoadFromAssemblyPath(candidate);
                }
            }
            return null;
        };

        try
        {
            Assembly apiAsm = alc.LoadFromAssemblyPath(patchedApiPath);
            Assembly contractsAsm = alc.LoadFromAssemblyPath(contractsPath);
            Assembly libAsm = alc.LoadFromAssemblyPath(patchedLibPath);

            Type configType = contractsAsm.GetType("Vintagestory.API.Config.OptimumConfig", throwOnError: true)!;
            FieldInfo enabledField = configType.GetField("ReuseCommonNatFloatEnabled", BindingFlags.Public | BindingFlags.Static)!;

            Type soundAttrType = apiAsm.GetType("Vintagestory.API.Common.SoundAttributes", throwOnError: true)!;
            Type natFloatType = apiAsm.GetType("Vintagestory.API.MathTools.NatFloat", throwOnError: true)!;
            Type enumDistType = apiAsm.GetType("Vintagestory.API.MathTools.EnumDistribution", throwOnError: true)!;
            Type assetLocType = apiAsm.GetType("Vintagestory.API.Common.AssetLocation", throwOnError: true)!;
            object uniformDist = Enum.ToObject(enumDistType, 0);

            object natOneStatic = soundAttrType.GetField("NatFloatOne", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            object randPitchStatic = soundAttrType.GetField("RandomPitch", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

            // Exercise Cecil-patched SoundAttributes.Clone()
            object sa = Activator.CreateInstance(soundAttrType)!;
            soundAttrType.GetField("Location")!.SetValue(sa, Activator.CreateInstance(assetLocType, "sounds/block/dirt"));
            soundAttrType.GetField("Pitch")!.SetValue(sa, Activator.CreateInstance(natFloatType, 1f, 0.25f, uniformDist));
            soundAttrType.GetField("Volume")!.SetValue(sa, Activator.CreateInstance(natFloatType, 1f, 0f, uniformDist));

            enabledField.SetValue(null, true);
            object saClone = soundAttrType.GetMethod("Clone", Type.EmptyTypes)!.Invoke(sa, null)!;
            Assert.Same(randPitchStatic, soundAttrType.GetField("Pitch")!.GetValue(saClone));
            Assert.Same(natOneStatic, soundAttrType.GetField("Volume")!.GetValue(saClone));

            // Exercise Cecil-patched SoundAttributes.FromBytes()
            byte[] serialized;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                soundAttrType.GetMethod("ToBytes", [typeof(BinaryWriter)])!.Invoke(saClone, [bw]);
                serialized = ms.ToArray();
            }
            using (var ms = new MemoryStream(serialized))
            using (var br = new BinaryReader(ms))
            {
                object saRead = soundAttrType.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.Static, [typeof(BinaryReader), typeof(string)])!.Invoke(null, [br, "1.22.7"])!;
                Assert.Same(randPitchStatic, soundAttrType.GetField("Pitch")!.GetValue(saRead));
                Assert.Same(natOneStatic, soundAttrType.GetField("Volume")!.GetValue(saRead));
            }

            // Exercise Cecil-patched CollectibleNet.ToPacket(NatFloat) and FromPacket(Packet_NatFloat)
            Type collectibleNetType = libAsm.GetType("Vintagestory.Common.CollectibleNet", throwOnError: true)!;
            Type packetNatFloatType = libAsm.GetType("Packet_NatFloat", throwOnError: true)!;
            Type packetSerializerType = libAsm.GetType("Packet_NatFloatSerializer", throwOnError: true)!;
            Type citoMemStreamType = libAsm.GetType("CitoMemoryStream", throwOnError: true)!;

            MethodInfo toPacket = collectibleNetType.GetMethod("ToPacket", BindingFlags.Public | BindingFlags.Static, [natFloatType])!;
            MethodInfo fromPacket = collectibleNetType.GetMethod("FromPacket", BindingFlags.Public | BindingFlags.Static, [packetNatFloatType])!;

            object freshOne = Activator.CreateInstance(natFloatType, 1f, 0f, uniformDist)!;
            object freshRand = Activator.CreateInstance(natFloatType, 1f, 0.25f, uniformDist)!;

            object pktOneA = toPacket.Invoke(null, [natOneStatic])!;
            object pktOneB = toPacket.Invoke(null, [freshOne])!;
            object pktRandA = toPacket.Invoke(null, [randPitchStatic])!;
            object pktRandB = toPacket.Invoke(null, [freshRand])!;

            Assert.Same(pktOneA, pktOneB);
            Assert.Same(pktRandA, pktRandB);
            Assert.NotSame(pktOneA, pktRandA);

            // Verify precomputed packet size matches Serializer.Serialize & SerializeWithSize wire bytes.
            Assert.Equal(3, (int)packetNatFloatType.GetField("size")!.GetValue(pktOneA)!);
            Assert.Equal(6, (int)packetNatFloatType.GetField("size")!.GetValue(pktRandA)!);
            object cmsOne = Activator.CreateInstance(citoMemStreamType)!;
            packetSerializerType.GetMethod("Serialize")!.Invoke(null, [cmsOne, pktOneA]);
            Assert.Equal(3, (int)citoMemStreamType.GetMethod("Position")!.Invoke(cmsOne, null)!);
            byte[] cachedWireOne = ((byte[])citoMemStreamType.GetMethod("GetBuffer")!.Invoke(cmsOne, null)!).Take(3).ToArray();

            object fromPktOne = fromPacket.Invoke(null, [pktOneA])!;
            object fromPktRand = fromPacket.Invoke(null, [pktRandA])!;
            Assert.Same(natOneStatic, fromPktOne);
            Assert.Same(randPitchStatic, fromPktRand);

            // Disabled toggle produces fresh Packet_NatFloat and fresh NatFloat instances with identical wire bytes.
            enabledField.SetValue(null, false);
            object disabledPkt = toPacket.Invoke(null, [freshOne])!;
            object disabledNat = fromPacket.Invoke(null, [disabledPkt])!;
            Assert.NotSame(pktOneA, disabledPkt);
            Assert.NotSame(natOneStatic, disabledNat);
            Assert.Equal(1024, (int)packetNatFloatType.GetField("Avg")!.GetValue(disabledPkt)!);
            Assert.Equal(1f, (float)natFloatType.GetField("avg")!.GetValue(disabledNat)!);

            object cmsDisabled = Activator.CreateInstance(citoMemStreamType)!;
            packetSerializerType.GetMethod("Serialize")!.Invoke(null, [cmsDisabled, disabledPkt]);
            Assert.Equal(3, (int)citoMemStreamType.GetMethod("Position")!.Invoke(cmsDisabled, null)!);
            byte[] uncachedWireOne = ((byte[])citoMemStreamType.GetMethod("GetBuffer")!.Invoke(cmsDisabled, null)!).Take(3).ToArray();
            Assert.Equal(uncachedWireOne, cachedWireOne);

            // SerializeWithSize succeeds using precomputed .size on cached packets without prior GetSize call.
            enabledField.SetValue(null, true);
            object cmsWithSize = Activator.CreateInstance(citoMemStreamType)!;
            packetSerializerType.GetMethod("SerializeWithSize")!.Invoke(null, [cmsWithSize, pktRandA]);
            Assert.Equal(7, (int)citoMemStreamType.GetMethod("Position")!.Invoke(cmsWithSize, null)!);
        }
        finally
        {
            alc.Unload();
        }
    }
}
