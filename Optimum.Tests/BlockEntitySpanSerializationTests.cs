#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class BlockEntitySpanSerializationTests
{
    private sealed class IsolatedPatchedAlc : AssemblyLoadContext
    {
        private readonly string[] searchDirs;
        private readonly Dictionary<string, Assembly> preloaded = new(StringComparer.OrdinalIgnoreCase);

        public IsolatedPatchedAlc(string name, params string[] searchDirs)
            : base(name, isCollectible: true)
        {
            this.searchDirs = searchDirs;
        }

        public Assembly LoadEntryAssembly(string path, string logicalName)
        {
            Assembly asm = LoadFromAssemblyPath(path);
            preloaded[logicalName] = asm;
            return asm;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name != null && preloaded.TryGetValue(name.Name, out Assembly? pinned))
            {
                return pinned;
            }

            if (name.Name != null && !name.Name.StartsWith("System.", StringComparison.Ordinal) &&
                name.Name != "netstandard" && name.Name != "mscorlib")
            {
                foreach (string dir in searchDirs)
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return LoadFromAssemblyPath(candidate);
                    }
                }
            }

            return null;
        }
    }

    private static TreeAttribute BuildComprehensiveTree()
    {
        var nested = new TreeAttribute();
        nested.SetInt("innerInt", -99);
        nested.SetFloat("innerFloat", 1.25f);
        nested.SetString("innerText", "chest-slot");

        var itemStackAttrs = new TreeAttribute();
        itemStackAttrs.SetInt("durability", 180);
        itemStackAttrs.SetString("material", "bronze");
        var itemStack = new ItemStack
        {
            Class = EnumItemClass.Item,
            Id = 42,
            StackSize = 16,
            Attributes = itemStackAttrs
        };

        var arrTree0 = new TreeAttribute();
        arrTree0.SetInt("slot", 0);
        var arrTree1 = new TreeAttribute();
        arrTree1.SetInt("slot", 1);

        var tree = new TreeAttribute();
        tree.SetInt("posx", 512005);
        tree.SetInt("posy", 112);
        tree.SetInt("posz", 511960);
        tree.SetLong("longVal", 12345678901234L);
        tree.SetDouble("doubleVal", 987.654321);
        tree.SetFloat("meshAngle", 1.5707964f);
        tree.SetString("blockCode", "game:chest-east");
        tree["subTree"] = nested;
        tree["stack"] = new ItemstackAttribute(itemStack);
        tree["nullStack"] = new ItemstackAttribute();
        tree.SetBytes("rawBytes", new byte[] { 1, 2, 3, 255 });
        tree.SetBool("burning", true);
        tree["strArr"] = new StringArrayAttribute(new[] { "a", "bb", "" });
        tree["intArr"] = new IntArrayAttribute(new[] { 10, -20, 30 });
        tree["floatArr"] = new FloatArrayAttribute(new[] { 0.5f, -1.5f });
        tree["doubleArr"] = new DoubleArrayAttribute(new[] { 2.5, -3.5 });
        tree["treeArr"] = new TreeArrayAttribute(new[] { arrTree0, arrTree1 });
        tree["longArr"] = new LongArrayAttribute(new[] { 100L, -200L });
        tree["boolArr"] = new BoolArrayAttribute(new[] { true, false, true });
        return tree;
    }

    [Fact]
    public void BlockEntitySpanSerialization_PatchesAndRegistrationsExist()
    {
        string treePatch = PatchReader.ReadPatch("patches/VintagestoryApi/Datastructures/AttributeTree/TreeAttribute.cs.patch");
        string serverPacketsPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerPackets.cs.patch");
        string serverChunkPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerChunk.cs.patch");
        string serverMainPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerMain.cs.patch");
        string serverBlockSimPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemBlockSimulation.cs.patch");
        string clientChunkPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientChunk.cs.patch");
        string clientEntitiesPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemEntities.cs.patch");
        string packetBePatch = PatchReader.ReadPatch("patches/VintagestoryLib/Packet_BlockEntity.cs.patch");
        string chunkTesselatorPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch");
        string chunkTesselatorManagerPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");
        string packetSerPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Packet_BlockEntitySerializer.cs.patch");
        string apiPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        string allowlist = File.ReadAllText(PatchReader.FindRepositoryFile("scripts/vanilla-compat-allowlist.txt"));

        Assert.Contains("OptimumApiBridge.CreateTreeFromBytes", treePatch);
        Assert.Contains("OptimumTreeAttributeCodec.WriteBlockEntityToStream", serverPacketsPatch);
        Assert.Contains("GetBlockEntitiesPacketsPooled", serverChunkPatch);
        Assert.Contains("OptimumPooledSlab.Rent", serverMainPatch);
        Assert.Contains("OptimumPooledSlab.Rent", serverBlockSimPatch);
        Assert.Contains("OptimumTreeAttributeCodec.ReadTreeFromBytes", clientChunkPatch);
        Assert.Contains("ReleaseAndClearOptimumSlice", clientChunkPatch);
        Assert.Contains("ReleaseAndClearOptimumSlice", clientEntitiesPatch);
        Assert.Contains("SetOptimumSlice", packetBePatch);
        Assert.Contains("ReleaseAndClearOptimumSlice", packetBePatch);
        Assert.Contains("ReleaseAndClearOptimumSlices", packetBePatch);
        Assert.Contains("TryGetOptimumDataSpan", packetSerPatch);
        Assert.Contains("ContainsRetryTesselationException", chunkTesselatorManagerPatch);
        Assert.Contains("new object[] { chunkX, chunkY, chunkZ, ex2 }), ex2);", chunkTesselatorPatch);

        Assert.Contains("PatchTreeAttributeSerialization(", apiPatcherSource);
        Assert.Contains("\"Packet_BlockEntity\"", programSource);
        Assert.Contains("\"ReleaseAndClearOptimumSlice\"", programSource);
        Assert.Contains("\"ReleaseAndClearOptimumSlices\"", programSource);
        Assert.Contains("\"ContainsRetryTesselationException\", 1", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"SendPacketFast\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"Serialize_\", 1)", programSource);
        Assert.Contains("new(\"Packet_BlockEntitySerializer\", \"Deserialize\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerChunk\", \"AfterDeserialization\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientChunk\", \"PreLoadBlockEntitiesFromPacket\", 3)", programSource);
        Assert.Contains("patches/VintagestoryLib/Packet_BlockEntity.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Packet_BlockEntitySerializer.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Packet_BlockEntitySerializer.cs.patch", allowlist);
    }

    [Fact]
    public void TreeAttribute_SpanRoundTrip_MatchesLegacyBytesAndPreservesInternedKeys()
    {
        bool previousToggle = OptimumConfig.SpanBlockEntitySerializationEnabled;
        try
        {
            TreeAttribute source = BuildComprehensiveTree();

            OptimumConfig.SpanBlockEntitySerializationEnabled = false;
            byte[] legacyBytes = source.ToBytes();

            OptimumConfig.SpanBlockEntitySerializationEnabled = true;
            byte[] spanBytes = source.ToBytes();

            Assert.Equal(legacyBytes, spanBytes);

            TreeAttribute decoded = TreeAttribute.CreateFromBytes(spanBytes);
            Assert.Equal(512005, decoded.GetInt("posx"));
            Assert.Equal(112, decoded.GetInt("posy"));
            Assert.Equal(511960, decoded.GetInt("posz"));
            Assert.Equal(12345678901234L, decoded.GetLong("longVal"));
            Assert.Equal(987.654321, decoded.GetDouble("doubleVal"));
            Assert.Equal(1.5707964f, decoded.GetFloat("meshAngle"));
            Assert.Equal("game:chest-east", decoded.GetString("blockCode"));
            Assert.Equal(-99, decoded.GetTreeAttribute("subTree").GetInt("innerInt"));
            Assert.Equal(1.25f, decoded.GetTreeAttribute("subTree").GetFloat("innerFloat"));
            Assert.Equal("chest-slot", decoded.GetTreeAttribute("subTree").GetString("innerText"));

            ItemStack? decodedStack = decoded.GetItemstack("stack");
            Assert.NotNull(decodedStack);
            Assert.Equal(EnumItemClass.Item, decodedStack!.Class);
            Assert.Equal(42, decodedStack.Id);
            Assert.Equal(16, decodedStack.StackSize);
            Assert.Equal(180, decodedStack.Attributes.GetInt("durability"));
            Assert.Equal("bronze", decodedStack.Attributes.GetString("material"));
            Assert.Null(decoded.GetItemstack("nullStack"));

            Assert.Equal(new byte[] { 1, 2, 3, 255 }, decoded.GetBytes("rawBytes"));
            Assert.True(decoded.GetBool("burning"));
            Assert.Equal(new[] { "a", "bb", "" }, (decoded["strArr"] as StringArrayAttribute)?.value);
            Assert.Equal(new[] { 10, -20, 30 }, (decoded["intArr"] as IntArrayAttribute)?.value);
            Assert.Equal(new[] { 0.5f, -1.5f }, (decoded["floatArr"] as FloatArrayAttribute)?.value);
            Assert.Equal(new[] { 2.5, -3.5 }, (decoded["doubleArr"] as DoubleArrayAttribute)?.value);
            Assert.Equal(new[] { 100L, -200L }, (decoded["longArr"] as LongArrayAttribute)?.value);
            Assert.Equal(new[] { true, false, true }, (decoded["boolArr"] as BoolArrayAttribute)?.value);
            TreeAttribute[]? arrTrees = (decoded["treeArr"] as TreeArrayAttribute)?.value;
            Assert.NotNull(arrTrees);
            Assert.Equal(2, arrTrees!.Length);
            Assert.Equal(0, arrTrees[0].GetInt("slot"));
            Assert.Equal(1, arrTrees[1].GetInt("slot"));

            foreach (string key in decoded.Keys)
            {
                Assert.Same(string.Intern(key), key);
            }

            var reuseTarget = new TreeAttribute();
            reuseTarget.SetInt("stale", 1);
            reuseTarget.FromBytes(spanBytes);
            Assert.False(reuseTarget.HasAttribute("stale"));
            Assert.Equal(512005, reuseTarget.GetInt("posx"));
        }
        finally
        {
            OptimumConfig.SpanBlockEntitySerializationEnabled = previousToggle;
        }
    }

    [Fact]
    public void TreeAttribute_BlockEntityHeaderDecodeAndScalarReuse_EliminateStreamAndByte4Churn()
    {
        var tree = new TreeAttribute();
        tree.SetInt("posx", 10);
        tree.SetInt("posy", 64);
        tree.SetInt("posz", 20);
        tree.SetFloat("meshAngle", 0.7853982f);
        tree.SetBool("burning", false);
        tree.SetDouble("burnUntil", 48.25);

        byte[] payload;
        using (var ms = new FastMemoryStream(256))
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write("GenericContainer");
            tree.ToBytes(bw);
            bw.Flush();
            payload = ms.ToArray();
        }

        // Warm up caches and thread-static state on this thread.
        var target = new TreeAttribute();
        OptimumTreeAttributeCodec.ReadBlockEntityFromBytes(payload, 0, payload.Length, out string warmClass, target);
        Assert.Equal("GenericContainer", warmClass);
        Assert.Equal(0.7853982f, target.GetFloat("meshAngle"));

        var legacyTarget = new TreeAttribute();
        using (var ms = new MemoryStream(payload))
        using (var br = new BinaryReader(ms))
        {
            _ = br.ReadString();
            legacyTarget.FromBytes(br);
        }

        // Measure legacy BinaryReader path vs warmed span decode path.
        long legacyStart = GC.GetAllocatedBytesForCurrentThread();
        using (var ms = new MemoryStream(payload))
        using (var br = new BinaryReader(ms))
        {
            _ = br.ReadString();
            legacyTarget.FromBytes(br);
        }
        long legacyAlloc = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

        // Measure only the classname + key cache lookups and primitive reads on second pass:
        // classname is cached, all 6 keys are cached, and FloatAttribute no longer allocates byte[4].
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        OptimumTreeAttributeCodec.ReadBlockEntityFromBytes(payload, 0, payload.Length, out string secondClass, target);
        long spanAlloc = GC.GetAllocatedBytesForCurrentThread() - startAlloc;

        Assert.Same(warmClass, secondClass);
        Assert.True(spanAlloc <= 512 && (legacyAlloc - spanAlloc) >= 400,
            $"Expected span decode ({spanAlloc} B) to save >=400 B of MemoryStream/BinaryReader/string/byte[4] churn vs legacy ({legacyAlloc} B).");
    }

    [Fact]
    public void TreeAttribute_MalformedCountsAndTruncatedBuffers_FailFastBeforeOom()
    {
        // IntArray (attrId=11), key="k", quantity=int.MaxValue on an 8-byte payload.
        byte[] maliciousArrayCount =
        {
            11, 1, (byte)'k',
            0xFF, 0xFF, 0xFF, 0x7F,
            0
        };
        Assert.Throws<EndOfStreamException>(() => OptimumTreeAttributeCodec.CreateTreeFromBytes(maliciousArrayCount));

        // Negative ByteArray length (attrId=8), key="b", length=-1.
        byte[] negativeByteLen =
        {
            8, 1, (byte)'b',
            0xFF, 0xFF,
            0
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => OptimumTreeAttributeCodec.CreateTreeFromBytes(negativeByteLen));
    }

    [Fact]
    public void SyncedTreeAttribute_FromBytesByteArray_StillFiresOnModifiedListeners()
    {
        var source = new TreeAttribute();
        source.SetInt("count", 7);
        byte[] bytes = source.ToBytes();

        var synced = new SyncedTreeAttribute();
        int fires = 0;
        synced.RegisterModifiedListener(null, () => fires++);
        synced.FromBytes(bytes);

        Assert.Equal(1, fires);
        Assert.Equal(7, synced.GetInt("count"));
    }

    [Fact]
    public void OptimumPooledSlab_RetainAndRelease_RespectsRefCountAndZeroesBufferOnFinalRelease()
    {
        var slab = OptimumPooledSlab.Rent(128);
        Assert.NotNull(slab.Buffer);

        slab.Retain();
        slab.Release();
        Assert.NotNull(slab.Buffer);

        slab.Release();
        Assert.Null(slab.Buffer);

        // Idempotent after final release.
        slab.Release();
        slab.Retain();
        Assert.Null(slab.Buffer);
    }

    [Fact]
    public void ApiPatcher_ShipsSpanTreeAttributeIntoVanillaApiAssembly()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!));
        string vanillaApi = Path.Combine(repoRoot, ".vanilla", "win-x64", "runtime-donors", "VintagestoryAPI.dll");
        string contractsDll = typeof(OptimumTreeAttributeCodec).Assembly.Location;
        if (!File.Exists(vanillaApi) || !File.Exists(contractsDll))
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-642-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string patchedApi = Path.Combine(tempDir, "VintagestoryAPI.dll");
            Assert.True(ApiPatcher.Patch(vanillaApi, contractsDll, patchedApi));

            var alc = new IsolatedPatchedAlc(
                "Optimum642ApiVerify",
                tempDir,
                Path.GetDirectoryName(contractsDll)!,
                Path.GetDirectoryName(vanillaApi)!,
                Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib"));
            try
            {
                Assembly apiAsm = alc.LoadEntryAssembly(patchedApi, "VintagestoryAPI");
                Type treeType = apiAsm.GetType("Vintagestory.API.Datastructures.TreeAttribute", throwOnError: true)!;
                object tree = Activator.CreateInstance(treeType)!;
                treeType.GetMethod("SetInt", new[] { typeof(string), typeof(int) })!.Invoke(tree, new object[] { "k", 123 });
                treeType.GetMethod("SetFloat", new[] { typeof(string), typeof(float) })!.Invoke(tree, new object[] { "f", 3.5f });

                byte[] bytes = (byte[])treeType.GetMethod("ToBytes", Type.EmptyTypes)!.Invoke(tree, null)!;
                object roundtrip = treeType.GetMethod("CreateFromBytes", new[] { typeof(byte[]) })!.Invoke(null, new object[] { bytes })!;
                int k = (int)treeType.GetMethod("GetInt", new[] { typeof(string), typeof(int) })!.Invoke(roundtrip, new object[] { "k", 0 })!;
                float f = (float)treeType.GetMethod("GetFloat", new[] { typeof(string), typeof(float) })!.Invoke(roundtrip, new object[] { "f", 0f })!;
                Assert.Equal(123, k);
                Assert.Equal(3.5f, f);
            }
            finally
            {
                alc.Unload();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PacketBlockEntitySerializer_SliceSerializationAndDeserialization_MatchesVanillaBytes()
    {
        string repoRoot = Path.GetFullPath(Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!);
        string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"))
            ? "Release"
            : "Debug";
        string patchedLib = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib-patched.dll");
        string outDir = Path.Combine(repoRoot, "bin", cfgDir, "net10.0");
        string patchedApi = Path.Combine(outDir, "VintagestoryAPI-patched.dll");
        if (!File.Exists(patchedLib) || !File.Exists(patchedApi))
        {
            return;
        }

        var tree = BuildComprehensiveTree();
        byte[] payload = tree.ToBytes();

        var alc = new IsolatedPatchedAlc(
            "Optimum642PatchedLibVerify",
            Path.GetDirectoryName(patchedLib)!,
            outDir,
            Path.Combine(repoRoot, ".vanilla", "linux-x64", "vintagestory", "Lib"),
            Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib"));
        try
        {
            Assembly apiAsm = alc.LoadEntryAssembly(patchedApi, "VintagestoryAPI");
            Assembly libAsm = alc.LoadEntryAssembly(patchedLib, "VintagestoryLib");
            Assembly contractsAsm = alc.LoadFromAssemblyName(new AssemblyName("Optimum.Api.Contracts"));

            Type packetType = libAsm.GetType("Packet_BlockEntity", throwOnError: true)!;
            Type serializerType = libAsm.GetType("Packet_BlockEntitySerializer", throwOnError: true)!;
            Type citoStreamType = libAsm.GetType("CitoMemoryStream", throwOnError: true)!;
            Type citoBaseType = libAsm.GetType("CitoStream", throwOnError: true)!;
            Type slabType = contractsAsm.GetType("Vintagestory.API.Config.OptimumPooledSlab", throwOnError: true)!;
            Type codecType = contractsAsm.GetType("Vintagestory.API.Config.OptimumTreeAttributeCodec", throwOnError: true)!;
            Type treeType = apiAsm.GetType("Vintagestory.API.Datastructures.TreeAttribute", throwOnError: true)!;

            object CreatePacket(byte[]? data)
            {
                object pkt = Activator.CreateInstance(packetType)!;
                packetType.GetField("Classname")!.SetValue(pkt, "GenericContainer");
                packetType.GetField("PosX")!.SetValue(pkt, 512005);
                packetType.GetField("PosY")!.SetValue(pkt, 112);
                packetType.GetField("PosZ")!.SetValue(pkt, 511960);
                if (data != null)
                {
                    packetType.GetField("Data")!.SetValue(pkt, data);
                }
                return pkt;
            }

            object legacyPacket = CreatePacket(payload);
            object slab = slabType.GetMethod("Rent", new[] { typeof(int) })!.Invoke(null, new object[] { payload.Length + 32 })!;
            byte[] slabBuf = (byte[])slabType.GetProperty("Buffer")!.GetValue(slab)!;
            const int sliceOffset = 17;
            Buffer.BlockCopy(payload, 0, slabBuf, sliceOffset, payload.Length);

            object slicePacket = CreatePacket(null);
            MethodInfo setSlice = packetType.GetMethod("SetOptimumSlice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            MethodInfo hasData = packetType.GetMethod("HasOptimumData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            MethodInfo tryGetSpan = packetType.GetMethod("TryGetOptimumDataSpan", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

            setSlice.Invoke(slicePacket, new object[] { slabBuf, sliceOffset, payload.Length });
            Assert.True((bool)hasData.Invoke(slicePacket, null)!);

            MethodInfo getSize = serializerType.GetMethod("GetSize", new[] { packetType })!;
            int legacySize = (int)getSize.Invoke(null, new object[] { legacyPacket })!;
            int sliceSize = (int)getSize.Invoke(null, new object[] { slicePacket })!;
            Assert.Equal(legacySize, sliceSize);

            MethodInfo serialize = serializerType.GetMethod("Serialize", new[] { citoBaseType, packetType })!;
            MethodInfo toArray = citoStreamType.GetMethod("ToArray", Type.EmptyTypes)!;
            MethodInfo position = citoStreamType.GetMethod("Position", Type.EmptyTypes)!;

            byte[] SerializePacket(object pkt)
            {
                object stream = Activator.CreateInstance(citoStreamType)!;
                serialize.Invoke(null, new object[] { stream, pkt });
                byte[] full = (byte[])toArray.Invoke(stream, null)!;
                int len = (int)position.Invoke(stream, null)!;
                return full.AsSpan(0, len).ToArray();
            }

            byte[] legacyWire = SerializePacket(legacyPacket);
            byte[] sliceWire = SerializePacket(slicePacket);
            Assert.Equal(legacyWire, sliceWire);

            // Deserializing into a Packet_BlockEntity slices incoming buffer without allocating Data[].
            object decodedPacket = Activator.CreateInstance(packetType)!;
            MethodInfo deserializeBuffer = serializerType.GetMethod("DeserializeBuffer", new[] { typeof(byte[]), typeof(int), packetType })!;
            deserializeBuffer.Invoke(null, new object[] { sliceWire, sliceWire.Length, decodedPacket });

            Assert.Null(packetType.GetField("Data")!.GetValue(decodedPacket));
            Assert.True((bool)hasData.Invoke(decodedPacket, null)!);
            object?[] spanArgs = new object?[] { null, 0, 0 };
            Assert.True((bool)tryGetSpan.Invoke(decodedPacket, spanArgs)!);
            byte[] decBuf = (byte[])spanArgs[0]!;
            int decOff = (int)spanArgs[1]!;
            int decLen = (int)spanArgs[2]!;
            Assert.Same(sliceWire, decBuf);
            Assert.Equal(payload.Length, decLen);
            Assert.Equal(payload, decBuf.AsSpan(decOff, decLen).ToArray());

            object decodedTree = Activator.CreateInstance(treeType)!;
            codecType.GetMethod("ReadTreeFromBytes", new[] { treeType, typeof(byte[]), typeof(int), typeof(int) })!
                .Invoke(null, new object[] { decodedTree, decBuf, decOff, decLen });
            Assert.Equal(512005, (int)treeType.GetMethod("GetInt", new[] { typeof(string), typeof(int) })!.Invoke(decodedTree, new object[] { "posx", 0 })!);
            Assert.Equal("game:chest-east", (string)treeType.GetMethod("GetString", new[] { typeof(string), typeof(string) })!.Invoke(decodedTree, new object[] { "blockCode", string.Empty })!);

            // Removal packet (null Classname and no data) preserves !HasOptimumData().
            object removalPacket = Activator.CreateInstance(packetType)!;
            Assert.False((bool)hasData.Invoke(removalPacket, null)!);

            slabType.GetMethod("Release", Type.EmptyTypes)!.Invoke(slab, null);
            Assert.Null(slabType.GetProperty("Buffer")!.GetValue(slab));

            // Verify ReleaseAndClearOptimumSlice releases attached slab and clears slice fields.
            object directHandoffSlab = slabType.GetMethod("Rent", new[] { typeof(int) })!.Invoke(null, new object[] { payload.Length })!;
            byte[] directHandoffBuf = (byte[])slabType.GetProperty("Buffer")!.GetValue(directHandoffSlab)!;
            Buffer.BlockCopy(payload, 0, directHandoffBuf, 0, payload.Length);
            object directHandoffPacket = CreatePacket(null);
            setSlice.Invoke(directHandoffPacket, new object[] { directHandoffBuf, 0, payload.Length });
            packetType.GetField("optimumSlab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(directHandoffPacket, directHandoffSlab);
            MethodInfo releaseAndClear = packetType.GetMethod("ReleaseAndClearOptimumSlice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            releaseAndClear.Invoke(directHandoffPacket, null);
            Assert.False((bool)hasData.Invoke(directHandoffPacket, null)!);
            Assert.Null(slabType.GetProperty("Buffer")!.GetValue(directHandoffSlab));

            // Repeated chunk handoffs retain the shared slices until the final consumer releases the slab.
            object repeatedHandoffSlab = slabType.GetMethod("Rent", new[] { typeof(int) })!.Invoke(null, new object[] { payload.Length * 3 })!;
            byte[] repeatedHandoffBuffer = (byte[])slabType.GetProperty("Buffer")!.GetValue(repeatedHandoffSlab)!;
            Array repeatedHandoffPackets = Array.CreateInstance(packetType, 3);
            for (int i = 0; i < repeatedHandoffPackets.Length; i++)
            {
                int offset = i * payload.Length;
                Buffer.BlockCopy(payload, 0, repeatedHandoffBuffer, offset, payload.Length);
                object repeatedPacket = CreatePacket(null);
                repeatedHandoffPackets.SetValue(repeatedPacket, i);
                setSlice.Invoke(repeatedPacket, new object[] { repeatedHandoffBuffer, offset, payload.Length });
            }
            packetType.GetField("optimumSlab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(repeatedHandoffPackets.GetValue(0), repeatedHandoffSlab);
            slabType.GetMethod("Retain", Type.EmptyTypes)!.Invoke(repeatedHandoffSlab, null);
            MethodInfo releaseAndClearMany = packetType.GetMethod("ReleaseAndClearOptimumSlices", BindingFlags.Static | BindingFlags.NonPublic)!;
            releaseAndClearMany.Invoke(null, new object[] { repeatedHandoffPackets, repeatedHandoffPackets.Length });
            Assert.NotNull(slabType.GetProperty("Buffer")!.GetValue(repeatedHandoffSlab));
            Assert.All(Enumerable.Range(0, repeatedHandoffPackets.Length), i =>
                Assert.True((bool)hasData.Invoke(repeatedHandoffPackets.GetValue(i), null)!));

            releaseAndClearMany.Invoke(null, new object[] { repeatedHandoffPackets, repeatedHandoffPackets.Length });
            Assert.Null(slabType.GetProperty("Buffer")!.GetValue(repeatedHandoffSlab));
            Assert.All(Enumerable.Range(0, repeatedHandoffPackets.Length), i =>
                Assert.False((bool)hasData.Invoke(repeatedHandoffPackets.GetValue(i), null)!));

            Type? retryExceptionType = new[] { libAsm, apiAsm }
                .SelectMany(assembly => assembly.GetTypes())
                .FirstOrDefault(type => type.Name == "RetryTesselationException");
            Assert.NotNull(retryExceptionType);
            object retryException = Activator.CreateInstance(
                retryExceptionType!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { "retry" },
                culture: null)!;
            Type managerType = libAsm.GetType("Vintagestory.Client.NoObf.ChunkTesselatorManager", throwOnError: true)!;
            MethodInfo containsRetry = managerType.GetMethod("ContainsRetryTesselationException", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.True((bool)containsRetry.Invoke(null, new object[] { new Exception("outer", new Exception("inner", (Exception)retryException)) })!);
            Assert.False((bool)containsRetry.Invoke(null, new object[] { new Exception("outer", new Exception("inner")) })!);

            // Verify ServerMain.SendPacketFast and Serialize_ manage Retain/Release slab lifecycle
            // while leaving ServerMain.SendPacket(int, Packet_Server) at its vanilla IL body for Komet's fingerprint check.
            using (var module = Mono.Cecil.ModuleDefinition.ReadModule(patchedLib))
            {
                Mono.Cecil.TypeDefinition serverMainDef = module.Types.First(t => t.FullName == "Vintagestory.Server.ServerMain");
                Mono.Cecil.MethodDefinition sendPacketFastDef = serverMainDef.Methods.First(m =>
                    m.Name == "SendPacketFast" && m.Parameters.Count == 2);
                Mono.Cecil.MethodDefinition serializeDef = serverMainDef.Methods.First(m =>
                    m.Name == "Serialize_" && m.Parameters.Count == 1);
                var fastCalls = sendPacketFastDef.Body.Instructions
                    .Where(i => i.Operand is Mono.Cecil.MethodReference)
                    .Select(i => ((Mono.Cecil.MethodReference)i.Operand).Name)
                    .ToList();
                var serializeCalls = serializeDef.Body.Instructions
                    .Where(i => i.Operand is Mono.Cecil.MethodReference)
                    .Select(i => ((Mono.Cecil.MethodReference)i.Operand).Name)
                    .ToList();
                Assert.Contains("OptimumRetainBlockEntityPacketSlabs", fastCalls);
                Assert.Contains("OptimumReleaseBlockEntityPacketSlabs", serializeCalls);
            }
        }
        finally
        {
            alc.Unload();
        }
    }
}
