using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;
#if !NO_DONOR
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
#endif

namespace Optimum.Tests;

[Collection("AudioPcmLifecycle")]
public sealed class AudioPcmLifecycleTests
{
    private static byte[] BuildSyntheticOggTailStream(
        int prefixBytes,
        int serial,
        long granulePos,
        byte headerTypeFlags,
        byte version,
        byte[] payload,
        bool injectFalseMagicInPrefix = false,
        bool truncateLastPayloadByte = false)
    {
        int segmentCount = payload.Length == 0 ? 1 : (payload.Length + 254) / 255 + (payload.Length % 255 == 0 ? 1 : 0);
        int pageSize = 27 + segmentCount + payload.Length - (truncateLastPayloadByte ? 1 : 0);
        byte[] stream = new byte[prefixBytes + pageSize];

        if (injectFalseMagicInPrefix && prefixBytes >= 32)
        {
            // Plant a bogus "OggS" sequence inside payload prefix with EOS flag set
            // whose lacing length does NOT reach EOF, so the tail scanner must skip it.
            int falsePos = 4;
            stream[falsePos] = (byte)'O';
            stream[falsePos + 1] = (byte)'g';
            stream[falsePos + 2] = (byte)'g';
            stream[falsePos + 3] = (byte)'S';
            stream[falsePos + 4] = 0;
            stream[falsePos + 5] = 0x04;
            BinaryPrimitives.WriteInt64LittleEndian(stream.AsSpan(falsePos + 6, 8), 999_999);
            BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(falsePos + 14, 4), serial);
            stream[falsePos + 26] = 1;
            stream[falsePos + 27] = 1;
        }

        int pos = prefixBytes;
        stream[pos] = (byte)'O';
        stream[pos + 1] = (byte)'g';
        stream[pos + 2] = (byte)'g';
        stream[pos + 3] = (byte)'S';
        stream[pos + 4] = version;
        stream[pos + 5] = headerTypeFlags;
        BinaryPrimitives.WriteInt64LittleEndian(stream.AsSpan(pos + 6, 8), granulePos);
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(pos + 14, 4), serial);
        stream[pos + 26] = (byte)segmentCount;

        int remaining = payload.Length;
        for (int i = 0; i < segmentCount; i++)
        {
            int segLen = Math.Min(255, remaining);
            stream[pos + 27 + i] = (byte)segLen;
            remaining -= segLen;
        }

        int bodyCopy = payload.Length - (truncateLastPayloadByte ? 1 : 0);
        Array.Copy(payload, 0, stream, pos + 27 + segmentCount, bodyCopy);
        return stream;
    }

    private sealed class UnseekableStream : Stream
    {
        private readonly Stream inner;

        public UnseekableStream(Stream inner) => this.inner = inner;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void TryEstimateOggPcmByteLength_ParsesValidEosPage_SkipsFalseMagic_AndRestoresPosition()
    {
        byte[] bytes = BuildSyntheticOggTailStream(
            prefixBytes: 4096,
            serial: 0x12345678,
            granulePos: 480_000,
            headerTypeFlags: 0x04,
            version: 0,
            payload: new byte[64],
            injectFalseMagicInPrefix: true);

        using var ms = new MemoryStream(bytes);
        ms.Position = 1234;

        bool ok = OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, 0x12345678, channels: 2, out int estimated);

        Assert.True(ok);
        Assert.Equal(480_000 * 2 * 2, estimated);
        Assert.Equal(1234, ms.Position);
    }

    [Fact]
    public void TryEstimateOggPcmByteLength_RejectsUnseekableWrongSerialMissingEosAndBogusGranule()
    {
        byte[] valid = BuildSyntheticOggTailStream(
            prefixBytes: 256,
            serial: 42,
            granulePos: 1000,
            headerTypeFlags: 0x04,
            version: 0,
            payload: new byte[16]);

        using (var unseekable = new UnseekableStream(new MemoryStream(valid)))
        {
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(unseekable, 42, 2, out int est));
            Assert.Equal(0, est);
        }

        using (var ms = new MemoryStream(valid))
        {
            ms.Position = 77;
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, firstSerial: 99, channels: 2, out _));
            Assert.Equal(77, ms.Position);
        }

        byte[] noEos = BuildSyntheticOggTailStream(128, 42, 1000, headerTypeFlags: 0x00, version: 0, payload: new byte[16]);
        using (var ms = new MemoryStream(noEos))
        {
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, 42, 2, out _));
        }

        byte[] badVersion = BuildSyntheticOggTailStream(128, 42, 1000, headerTypeFlags: 0x04, version: 1, payload: new byte[16]);
        using (var ms = new MemoryStream(badVersion))
        {
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, 42, 2, out _));
        }

        byte[] hugeGranule = BuildSyntheticOggTailStream(128, 42, granulePos: 100_000_000L, headerTypeFlags: 0x04, version: 0, payload: new byte[16]);
        using (var ms = new MemoryStream(hugeGranule))
        {
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, 42, 2, out _));
        }

        byte[] truncatedTail = BuildSyntheticOggTailStream(128, 42, 1000, 0x04, 0, new byte[16], truncateLastPayloadByte: true);
        using (var ms = new MemoryStream(truncatedTail))
        {
            Assert.False(OptimumAudioBufferHelper.TryEstimateOggPcmByteLength(ms, 42, 2, out _));
        }
    }

    private static byte[] BuildSyntheticVorbisHeaderAndTailOgg(int serial, byte channels, int sampleRate, long totalSamples)
    {
        byte[] headPage = new byte[28 + 30];
        headPage[0] = (byte)'O';
        headPage[1] = (byte)'g';
        headPage[2] = (byte)'g';
        headPage[3] = (byte)'S';
        headPage[4] = 0;
        headPage[5] = 0x02; // BOS
        BinaryPrimitives.WriteInt32LittleEndian(headPage.AsSpan(14, 4), serial);
        headPage[26] = 1;
        headPage[27] = 30;
        int pkt = 28;
        headPage[pkt] = 0x01;
        headPage[pkt + 1] = (byte)'v';
        headPage[pkt + 2] = (byte)'o';
        headPage[pkt + 3] = (byte)'r';
        headPage[pkt + 4] = (byte)'b';
        headPage[pkt + 5] = (byte)'i';
        headPage[pkt + 6] = (byte)'s';
        BinaryPrimitives.WriteUInt32LittleEndian(headPage.AsSpan(pkt + 7, 4), 0);
        headPage[pkt + 11] = channels;
        BinaryPrimitives.WriteInt32LittleEndian(headPage.AsSpan(pkt + 12, 4), sampleRate);

        byte[] fullWithTail = BuildSyntheticOggTailStream(
            prefixBytes: headPage.Length + 1024,
            serial: serial,
            granulePos: totalSamples,
            headerTypeFlags: 0x04,
            version: 0,
            payload: new byte[32]);
        Array.Copy(headPage, 0, fullWithTail, 0, headPage.Length);
        return fullWithTail;
    }

    [Fact]
    public void TryEstimateOggDurationAndFormat_AndShouldLazyDecodeSfx_ClassifyDurationsAccurately()
    {
        byte[] longOgg = BuildSyntheticVorbisHeaderAndTailOgg(serial: 777, channels: 2, sampleRate: 44100, totalSamples: 44100 * 12);
        using (var ms = new MemoryStream(longOgg))
        {
            ms.Position = 17;
            Assert.True(OptimumAudioBufferHelper.TryEstimateOggDurationAndFormat(ms, out int ch, out int rate, out float dur));
            Assert.Equal(2, ch);
            Assert.Equal(44100, rate);
            Assert.InRange(dur, 11.99f, 12.01f);
            Assert.Equal(17, ms.Position);
            Assert.True(OptimumAudioBufferHelper.ShouldLazyDecodeSfx(ms, longOgg.Length));
        }

        byte[] shortOgg = BuildSyntheticVorbisHeaderAndTailOgg(serial: 778, channels: 1, sampleRate: 48000, totalSamples: 48000 * 2);
        using (var ms = new MemoryStream(shortOgg))
        {
            Assert.True(OptimumAudioBufferHelper.TryEstimateOggDurationAndFormat(ms, out int ch, out int rate, out float dur));
            Assert.Equal(1, ch);
            Assert.Equal(48000, rate);
            Assert.InRange(dur, 1.99f, 2.01f);
            Assert.False(OptimumAudioBufferHelper.ShouldLazyDecodeSfx(ms, shortOgg.Length));
        }

        // Unseekable fallback classifies by compressed threshold cleanly.
        using (var unseekableSmall = new UnseekableStream(new MemoryStream(shortOgg)))
        {
            Assert.False(OptimumAudioBufferHelper.ShouldLazyDecodeSfx(unseekableSmall, compressedByteLength: 64 * 1024));
            Assert.True(OptimumAudioBufferHelper.ShouldLazyDecodeSfx(unseekableSmall, compressedByteLength: 300 * 1024));
        }
    }

    [Fact]
    public void Issue604_SharedOpenAlBuffersAndLazySfx_AreRegisteredInProgramAndOwnedList()
    {
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));

        string[] expectedPatches =
        {
            "patches/VintagestoryLib/Vintagestory.Client/AudioOpenAl.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client/LoadedSoundNative.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/AudioMetaData.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs.patch",
        };

        foreach (string patch in expectedPatches)
        {
            Assert.Contains(patch, cecilList);
            Assert.True(File.Exists(PatchReader.FindRepositoryFile(patch)), $"Missing patch file: {patch}");
        }

        Assert.Contains("\"AcquireSharedBuffer\"", programSource);
        Assert.Contains("\"EvictIdleSharedBuffers\"", programSource);
        Assert.Contains("\"DisposeAllSharedBuffers\"", programSource);
        Assert.Contains("new(\"Vintagestory.Client.AudioOpenAl\", \"initContext\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.AudioOpenAl\", \"Dispose\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.ScreenManager\", \"LoadSoundsSlow\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemSoundEngine\", \"OnGameTick500ms\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemSoundEngine\", \"Dispose\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"CreateAudio\", 3)", programSource);

        string loadedSoundPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client/LoadedSoundNative.cs.patch");
        Assert.Contains("AcquireSharedBuffer", loadedSoundPatch);
        Assert.Contains("ReleaseSharedBuffer", loadedSoundPatch);

        string screenMgrPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch");
        Assert.Contains("ShouldSkipSlowPredecode", screenMgrPatch);
    }

    [Fact]
    public void OptimumPcmBuffer_PreSizedExactTrimsAndGrowsProperly()
    {
        var empty = new OptimumPcmBuffer(0);
        Assert.Empty(empty.ToExactArray());

        byte[] chunkA = new byte[] { 1, 2, 3, 4 };
        byte[] chunkB = new byte[] { 5, 6, 7, 8 };

        var exact = new OptimumPcmBuffer(8);
        exact.Append(chunkA, 4);
        exact.Append(chunkB, 4);
        byte[] exactOut = exact.ToExactArray();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, exactOut);

        var overEstimated = new OptimumPcmBuffer(16);
        overEstimated.Append(chunkA, 4);
        overEstimated.Append(chunkB, 2);
        byte[] trimmedOut = overEstimated.ToExactArray();
        Assert.Equal(6, trimmedOut.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, trimmedOut);

        var grown = new OptimumPcmBuffer(2);
        byte[] big = Enumerable.Range(0, 20000).Select(i => (byte)(i & 0xFF)).ToArray();
        grown.Append(big, big.Length);
        Assert.Equal(big, grown.ToExactArray());
    }

    [Fact]
    public void Issue603_AudioPatchesAndCecilTargets_AreRegisteredInProgramAndOwnedList()
    {
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));

        string[] expectedPatches =
        {
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/AudioMetaData.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/OggDecoder.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemStartup.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemDebugCommands.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client/LoadedSoundNative.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch",
            "patches/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs.patch",
        };

        foreach (string patch in expectedPatches)
        {
            Assert.Contains(patch, cecilList);
            Assert.True(File.Exists(PatchReader.FindRepositoryFile(patch)), $"Missing patch file: {patch}");
        }

        Assert.Contains("\"Vintagestory.Client.NoObf.OptimumPendingPlaySound\"", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.OggDecoder\", \"OggToWav\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.AudioMetaData\", \"DoLoad\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.AudioMetaData\", \"AddOnLoaded\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.LoadedSoundNative\", \"createSoundSource\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.LoadedSoundNative\", \"Dispose\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.LoadedSoundNative\", \"Finalize\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.ScreenManager\", \"LoadMusicTrack\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.GuiScreenRunningGame\", \"handOverRenderingToRunningGame\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientSystemDebugCommands\", \"OnCmdSoundsummary\", 1)", programSource);

        string audioMetaPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/AudioMetaData.cs.patch");
        Assert.Contains("ReleaseManagedPcm();", audioMetaPatch);

        string loadedSoundPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client/LoadedSoundNative.cs.patch");
        Assert.DoesNotContain("+			if (ClientSettings.OptimizeRamMode == 2", loadedSoundPatch);
        Assert.Contains("AcquireSharedBuffer", loadedSoundPatch);

        string screenManagerPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch");
        Assert.Contains("StopAndDisposeIntroMusic", screenManagerPatch);
        Assert.Contains("audioMetaData2.AutoUnload = true;", screenManagerPatch);
    }

#if !NO_DONOR
    private sealed class ReloadableOggAsset : IAsset, IAssetOrigin
    {
        private readonly byte[] compressedBytes;
        public int LoadCalls;

        public ReloadableOggAsset(AssetLocation location, byte[] compressedBytes, bool startLoaded = true)
        {
            Location = location;
            Name = location.GetName();
            this.compressedBytes = compressedBytes;
            Origin = this;
            if (startLoaded)
            {
                Data = (byte[])compressedBytes.Clone();
            }
        }

        public string Name { get; }
        public AssetLocation Location { get; }
        public IAssetOrigin Origin { get; set; }
        public byte[] Data { get; set; }
        public bool IsPatched { get; set; }
        public string OriginPath => "memory";

        public bool IsLoaded() => Data != null;
        public BitmapRef ToBitmap(ICoreClientAPI api) => null;
        public T ToObject<T>(Newtonsoft.Json.JsonSerializerSettings settings = null) => default;
        public string ToText() => string.Empty;

        public void LoadAsset(IAsset asset)
        {
            Interlocked.Increment(ref LoadCalls);
            asset.Data = (byte[])compressedBytes.Clone();
        }

        public bool TryLoadAsset(IAsset asset)
        {
            LoadAsset(asset);
            return true;
        }

        public List<IAsset> GetAssets(AssetCategory Category, bool shouldLoad = true) => new();
        public List<IAsset> GetAssets(AssetLocation baseLocation, bool shouldLoad = true) => new();
        public bool IsAllowedToAffectGameplay() => true;
    }

    private static string FindVanillaOggAsset(params string[] relativeCandidates)
    {
        foreach (string rel in relativeCandidates)
        {
            string full = Path.GetFullPath(Path.Combine(
                PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"),
                "..", "..", rel));
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    [Fact]
    public void OggDecoder_RealVanillaMusicAndSoundAssets_ProduceBitExactPcmWithLowAllocationOverhead()
    {
        string[] candidateOggs =
        {
            ".vanilla/linux-x64/vintagestory/assets/game/music/roots.ogg",
            ".vanilla/linux-x64/vintagestory/assets/survival/music/the-lazaret.ogg",
            ".vanilla/linux-x64/vintagestory/assets/game/sounds/menubutton.ogg",
        };

        var decoder = new OggDecoder();
        int decodedCount = 0;

        foreach (string rel in candidateOggs)
        {
            string path = FindVanillaOggAsset(rel);
            if (path == null)
            {
                continue;
            }

            decodedCount++;
            byte[] fileBytes = File.ReadAllBytes(path);
            var asset = new ReloadableOggAsset(new AssetLocation("game", Path.GetFileName(path)), fileBytes);

            AudioMetaData unseekableResult = decoder.OggToWav(new UnseekableStream(new MemoryStream(fileBytes)), asset);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            AudioMetaData seekableResult = decoder.OggToWav(new MemoryStream(fileBytes), asset);
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long allocated = allocAfter - allocBefore;

            Assert.Equal(unseekableResult.Channels, seekableResult.Channels);
            Assert.Equal(unseekableResult.Rate, seekableResult.Rate);
            Assert.Equal(unseekableResult.Pcm.Length, seekableResult.Pcm.Length);
            Assert.True(unseekableResult.Pcm.AsSpan().SequenceEqual(seekableResult.Pcm),
                $"PCM mismatch for {rel}");

            // Pre-sized decode avoids MemoryStream growth + final ToArray copy,
            // keeping managed allocation well below 1.5x the output PCM array size
            // even on small files, and ~1.0x on multi-megabyte music files.
            double overheadRatio = (double)allocated / seekableResult.Pcm.Length;
            if (seekableResult.Pcm.Length >= 1024 * 1024)
            {
                Assert.True(overheadRatio <= 1.15,
                    $"Expected <= 1.15x PCM allocation on {rel} ({seekableResult.Pcm.Length} B PCM), measured {allocated} B ({overheadRatio:F2}x)");
            }
        }

        Assert.True(decodedCount >= 2, "Expected vanilla music .ogg files under .vanilla/linux-x64/vintagestory/assets/");
    }

    [Fact]
    public void AudioMetaData_AddOnLoaded_FiresWhenLoadedIs2_AndRefCountsPreventPrematureUnload()
    {
        var asset = new ReloadableOggAsset(new AssetLocation("game", "music/test.ogg"), new byte[16]);
        var meta = new AudioMetaData(asset)
        {
            Loaded = 2,
            Pcm = new byte[4096],
            Channels = 2,
            Rate = 44100,
            BitsPerSample = 16,
            AutoUnload = true,
        };

        int invoked = 0;
        var action = new MainThreadAction(null, () => ++invoked, "test");
        // Verify AddOnLoaded sees Loaded == 2 (not only == 3). With game == null it calls game.EnqueueMainThreadTask,
        // so use the reflection check + direct waitingToPlay check via Loaded == 1 vs Loaded == 2 behavior.
        meta.Loaded = 1;
        meta.AddOnLoaded(action);
        FieldInfo waitingField = typeof(AudioMetaData).GetField("waitingToPlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waitingList = (List<MainThreadAction>)waitingField.GetValue(meta)!;
        Assert.Single(waitingList);

        // Now verify Unload clears state cleanly regardless of previous values.
        MethodInfo acquireOwner = typeof(AudioMetaData).GetMethod("AcquireOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo releaseOwner = typeof(AudioMetaData).GetMethod("ReleaseOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        acquireOwner.Invoke(meta, null);
        acquireOwner.Invoke(meta, null);
        Assert.Equal(1, (int)releaseOwner.Invoke(meta, null)!);
        Assert.Equal(0, (int)releaseOwner.Invoke(meta, null)!);
        Assert.Equal(0, (int)releaseOwner.Invoke(meta, null)!);

        meta.Unload();
        Assert.Null(meta.Pcm);
        Assert.Equal(0, meta.Loaded);
        Assert.Equal(0, meta.Channels);
        Assert.Equal(0, meta.Rate);
        Assert.Equal(0, meta.BitsPerSample);
        Assert.Null(asset.Data);
    }

    [Fact]
    public void LoadedSoundNative_OpenAlLifecycle_DropsPcmAfterUpload_ReloadsOnDeviceSwitch_AndUnloadsInRamMode1()
    {
        string rootsPath = FindVanillaOggAsset(".vanilla/linux-x64/vintagestory/assets/game/sounds/menubutton.ogg",
            ".vanilla/linux-x64/vintagestory/assets/game/music/roots.ogg");
        Assert.NotNull(rootsPath);
        byte[] compressed = File.ReadAllBytes(rootsPath);

        string tempDataPath = Path.Combine(Path.GetTempPath(), "optimum-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDataPath);
        string prevDataPath = GamePaths.DataPath;
        ClientPlatformAbstract prevPlatform = ScreenManager.Platform;
        int prevRamMode = ClientSettings.OptimizeRamMode;
        bool prevDebugSoundDispose = RuntimeEnv.DebugSoundDispose;

        try
        {
            GamePaths.DataPath = tempDataPath;
            RuntimeEnv.DebugSoundDispose = false;
            var platform = new ClientPlatformWindows(null);
            ScreenManager.Platform = platform;
            ClientSettings.OptimizeRamMode = 1; // Default "Optimize somewhat", previously ignored by Dispose()!

            var asset = new ReloadableOggAsset(new AssetLocation("game", "music/test-track.ogg"), compressed);
            AudioMetaData meta = (AudioMetaData)ScreenManager.LoadMusicTrack(asset);
            Assert.NotNull(meta);
            Assert.True(meta.AutoUnload);
            Assert.Equal(2, meta.Loaded);
            Assert.NotNull(meta.Pcm);
            Assert.Null(asset.Data);

            int initialAssetLoads = asset.LoadCalls;

            var sound1 = (LoadedSoundNative)platform.CreateAudio(new SoundParams
            {
                Location = asset.Location,
                SoundType = EnumSoundType.Music,
            }, meta);

            Assert.NotNull(sound1);
            Assert.False(sound1.IsDisposed);
            Assert.Equal(3, meta.Loaded);
            Assert.True(sound1.IsReady);
            Assert.Null(meta.Pcm); // Managed PCM dropped immediately after OpenAL upload (no double buffering!)
            Assert.True(meta.Channels > 0);

            // Second concurrent owner reloads PCM cleanly and preserves owner count >= 1 on first Dispose.
            var sound2 = (LoadedSoundNative)platform.CreateAudio(new SoundParams
            {
                Location = asset.Location,
                SoundType = EnumSoundType.Music,
            }, meta);

            Assert.False(sound2.IsDisposed);
            Assert.True(asset.LoadCalls > initialAssetLoads);
            Assert.Null(meta.Pcm);

            // Changing output device while Pcm == null must reload from Asset and keep both sounds alive.
            int beforeDeviceSwitchLoads = asset.LoadCalls;
            LoadedSoundNative.ChangeOutputDevice(() => { });
            Assert.False(sound1.IsDisposed);
            Assert.False(sound2.IsDisposed);
            Assert.True(asset.LoadCalls > beforeDeviceSwitchLoads);
            Assert.Null(meta.Pcm);

            // Disposing owner 1 leaves owner 2 active (no premature Unload()).
            sound1.Dispose();
            Assert.True(sound1.IsDisposed);
            Assert.Equal(3, meta.Loaded);
            Assert.True(meta.Channels > 0);

            // Disposing last owner in OptimizeRamMode == 1 MUST unload cleanly!
            sound2.Dispose();
            Assert.True(sound2.IsDisposed);
            Assert.Equal(0, meta.Loaded);
            Assert.Equal(0, meta.Channels);
            Assert.Null(meta.Pcm);

            platform.StopAudio();
        }
        finally
        {
            ScreenManager.soundAudioData.Clear();
            ClientSettings.OptimizeRamMode = prevRamMode;
            RuntimeEnv.DebugSoundDispose = prevDebugSoundDispose;
            ScreenManager.Platform = prevPlatform;
            GamePaths.DataPath = prevDataPath;
            try { Directory.Delete(tempDataPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void VintagestoryLibPatched_ContainsIssue603BodyChangesWithoutLambdas()
    {
        string patchedDll = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"),
            "..", "..", "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"));
        Assert.True(File.Exists(patchedDll), $"Missing {patchedDll}; run make patch-il first.");

        using var asm = AssemblyDefinition.ReadAssembly(patchedDll);
        ModuleDefinition mod = asm.MainModule;

        Assert.NotNull(mod.GetType("Vintagestory.Client.NoObf.OptimumPendingPlaySound"));

        TypeDefinition oggDecoder = mod.GetType("Vintagestory.Client.NoObf.OggDecoder");
        MethodDefinition oggToWav = oggDecoder.Methods.Single(m => m.Name == "OggToWav");
        Assert.Contains(oggToWav.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "TryEstimateOggPcmByteLength");
        Assert.DoesNotContain(oggToWav.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.DeclaringType.Name == "StringWriter");

        TypeDefinition loadedSound = mod.GetType("Vintagestory.Client.LoadedSoundNative");
        MethodDefinition createSource = loadedSound.Methods.Single(m => m.Name == "createSoundSource");
        Assert.Contains(createSource.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "AcquirePcmSnapshot");
        Assert.Contains(createSource.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "ReleaseManagedPcm");

        MethodDefinition dispose = loadedSound.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        Assert.DoesNotContain(dispose.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "get_OptimizeRamMode");
        Assert.Contains(dispose.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "ReleaseSampleOwner");

        TypeDefinition guiRunning = mod.GetType("Vintagestory.Client.GuiScreenRunningGame");
        MethodDefinition handover = guiRunning.Methods.Single(m => m.Name == "handOverRenderingToRunningGame");
        Assert.Contains(handover.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "StopAndDisposeIntroMusic");

        TypeDefinition screenMgr = mod.GetType("Vintagestory.Client.ScreenManager");
        MethodDefinition loadSoundsSlow = screenMgr.Methods.Single(m => m.Name == "LoadSoundsSlow");
        Assert.Contains(loadSoundsSlow.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "get_OptimizeRamMode");
    }

    [Fact]
    public void Issue643_AggressiveOptimizeSkipsPriorityAndSecondarySoundPredecoding()
    {
        string dripPath = FindVanillaOggAsset(
            ".vanilla/linux-x64/vintagestory/assets/survival/sounds/environment/drip1.ogg",
            ".vanilla/win-x64/vintagestory/assets/survival/sounds/environment/drip1.ogg");
        string windPath = FindVanillaOggAsset(
            ".vanilla/linux-x64/vintagestory/assets/game/sounds/environment/wind.ogg",
            ".vanilla/win-x64/vintagestory/assets/game/sounds/environment/wind.ogg");
        Assert.NotNull(dripPath);
        Assert.NotNull(windPath);

        byte[] dripBytes = File.ReadAllBytes(dripPath);
        byte[] windBytes = File.ReadAllBytes(windPath);

        string tempDataPath = Path.Combine(Path.GetTempPath(), "optimum-audio-643-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDataPath);
        string prevDataPath = GamePaths.DataPath;
        ClientPlatformAbstract prevPlatform = ScreenManager.Platform;
        int prevRamMode = ClientSettings.OptimizeRamMode;
        bool prevAsyncComplete = ScreenManager.AsyncSoundLoadComplete;

        try
        {
            GamePaths.DataPath = tempDataPath;
            ScreenManager.Platform = new ClientPlatformWindows(null);
            ClientMain fakeGame = (ClientMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));

            var prioShortAsset = new ReloadableOggAsset(new AssetLocation("game", "sounds/environment/drip1.ogg"), dripBytes, startLoaded: false);
            var prioLongAsset = new ReloadableOggAsset(new AssetLocation("game", "sounds/environment/wind.ogg"), windBytes, startLoaded: false);
            var regularShortAsset = new ReloadableOggAsset(new AssetLocation("game", "sounds/blocks/dirt.ogg"), dripBytes, startLoaded: false);

            var prioShortMeta = new AudioMetaData(prioShortAsset) { Loaded = 0 };
            var prioLongMeta = new AudioMetaData(prioLongAsset) { Loaded = 0 };
            var regularShortMeta = new AudioMetaData(regularShortAsset) { Loaded = 0 };

            // Mode 2 (AggressiveOptimize): skip BOTH pass 1 (<5s priority) and pass 2, plus header scans.
            ScreenManager.soundAudioDataAsyncLoadTemp.Clear();
            ScreenManager.soundAudioDataAsyncLoadTemp[prioShortAsset.Location] = prioShortMeta;
            ScreenManager.soundAudioDataAsyncLoadTemp[prioLongAsset.Location] = prioLongMeta;
            ScreenManager.soundAudioDataAsyncLoadTemp[regularShortAsset.Location] = regularShortMeta;
            ScreenManager.AsyncSoundLoadComplete = false;
            ClientSettings.OptimizeRamMode = (int)EnumOptimizeRamMode.AggressiveOptimize;

            ScreenManager.LoadSoundsSlow(fakeGame);

            Assert.True(ScreenManager.AsyncSoundLoadComplete);
            Assert.Empty(ScreenManager.soundAudioDataAsyncLoadTemp);
            Assert.Equal(0, prioShortMeta.Loaded);
            Assert.Null(prioShortMeta.Pcm);
            Assert.Equal(0, prioShortAsset.LoadCalls);
            Assert.Equal(0, prioLongMeta.Loaded);
            Assert.Null(prioLongMeta.Pcm);
            Assert.Equal(0, prioLongAsset.LoadCalls);
            Assert.Equal(0, regularShortMeta.Loaded);
            Assert.Null(regularShortMeta.Pcm);
            Assert.Equal(0, regularShortAsset.LoadCalls);

            // On-demand playback still decodes clean PCM and releases managed PCM on upload.
            Assert.True(prioShortMeta.Load());
            Assert.Equal(2, prioShortMeta.Loaded);
            Assert.NotNull(prioShortMeta.Pcm);
            Assert.Equal(1, prioShortAsset.LoadCalls);

            // Mode 1 (Optimize): still predecodes <5s priority + regular sounds while skipping >=5s priority sound.
            var prioShortMode1Asset = new ReloadableOggAsset(new AssetLocation("game", "sounds/environment/drip1.ogg"), dripBytes, startLoaded: false);
            var prioLongMode1Asset = new ReloadableOggAsset(new AssetLocation("game", "sounds/environment/wind.ogg"), windBytes, startLoaded: false);
            var regularShortMode1Asset = new ReloadableOggAsset(new AssetLocation("game", "sounds/blocks/dirt.ogg"), dripBytes, startLoaded: false);

            var prioShortMode1Meta = new AudioMetaData(prioShortMode1Asset) { Loaded = 0 };
            var prioLongMode1Meta = new AudioMetaData(prioLongMode1Asset) { Loaded = 0 };
            var regularShortMode1Meta = new AudioMetaData(regularShortMode1Asset) { Loaded = 0 };

            ScreenManager.soundAudioDataAsyncLoadTemp.Clear();
            ScreenManager.soundAudioDataAsyncLoadTemp[prioShortMode1Asset.Location] = prioShortMode1Meta;
            ScreenManager.soundAudioDataAsyncLoadTemp[prioLongMode1Asset.Location] = prioLongMode1Meta;
            ScreenManager.soundAudioDataAsyncLoadTemp[regularShortMode1Asset.Location] = regularShortMode1Meta;
            ScreenManager.AsyncSoundLoadComplete = false;
            ClientSettings.OptimizeRamMode = (int)EnumOptimizeRamMode.Optimize;

            ScreenManager.LoadSoundsSlow(fakeGame);

            Assert.True(ScreenManager.AsyncSoundLoadComplete);
            Assert.Empty(ScreenManager.soundAudioDataAsyncLoadTemp);
            Assert.Equal(2, prioShortMode1Meta.Loaded);
            Assert.NotNull(prioShortMode1Meta.Pcm);
            Assert.Equal(0, prioLongMode1Meta.Loaded);
            Assert.Null(prioLongMode1Meta.Pcm);
            Assert.Equal(2, regularShortMode1Meta.Loaded);
            Assert.NotNull(regularShortMode1Meta.Pcm);
        }
        finally
        {
            ScreenManager.soundAudioDataAsyncLoadTemp.Clear();
            ScreenManager.AsyncSoundLoadComplete = prevAsyncComplete;
            ClientSettings.OptimizeRamMode = prevRamMode;
            ScreenManager.Platform = prevPlatform;
            GamePaths.DataPath = prevDataPath;
            try { Directory.Delete(tempDataPath, recursive: true); } catch { }
        }
    }
#endif
}
