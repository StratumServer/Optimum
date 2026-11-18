#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class CompactServerAssetsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void CompactServerAssets_DefaultsTrue_AndConfigRoundTrips()
    {
        bool orig = OptimumConfig.CompactServerAssetsEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-compact-assets-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.CompactServerAssetsEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.CompactServerAssets));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.CompactServerAssetsEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.CompactServerAssetsEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.CompactServerAssetsEnabled);

            OptimumConfig.CompactServerAssetsEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.CompactServerAssetsEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.CompactServerAssetsEnabled);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.CompactServerAssetsEnabled = orig;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CompactServerAssets_PatchesAndCecilRegistrationsExist()
    {
        string serverMainPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Server/ServerMain.cs.patch"));
        string boxedPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Server/BoxedPacket_ServerAssets.cs.patch"));
        string tcpPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Server.Network/TcpNetConnection.cs.patch"));
        string dummyPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/DummyNetConnection.cs.patch"));
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "Optimum.Patcher/Program.cs"));

        Assert.Contains("CacheCompressedAndRelease", serverMainPatch);
        Assert.Contains("ReleaseUncompressedSource", serverMainPatch);
        Assert.Contains("HasPayload()", serverMainPatch);
        Assert.Contains("DecompressForUncompressedSend", boxedPatch);
        Assert.Contains("DecompressForUncompressedSend", tcpPatch);
        Assert.Contains("DecompressForUncompressedSend", dummyPatch);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"SendServerAssets\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.ServerMain\", \"Dispose\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Common.DummyNetConnection\", \"PreparePacketForSending\", 3)", programSource);
    }

    [Fact]
    public void BoxedPacketServerAssets_CompressesReleasesAndDecompressesOnDemand()
    {
        string repoRoot = RepoRoot();
        string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib.dll"))
            ? "Release"
            : "Debug";
        string libPath = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib.dll");
        if (!File.Exists(libPath))
        {
            return;
        }

        var alc = new System.Runtime.Loader.AssemblyLoadContext("CompactAssetsTest", isCollectible: true);
        string libDir = Path.GetDirectoryName(libPath)!;
        string vanillaLibDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib");
        alc.Resolving += (ctx, name) =>
        {
            foreach (string dir in new[] { libDir, vanillaLibDir, AppContext.BaseDirectory })
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
            Assembly libAsm = alc.LoadFromAssemblyPath(libPath);
            Type boxedType = libAsm.GetType("Vintagestory.Server.BoxedPacket_ServerAssets", throwOnError: true)!;
            Type packetServerType = libAsm.GetType("Packet_Server", throwOnError: true)!;
            Type packetAssetsType = libAsm.GetType("Packet_ServerAssets", throwOnError: true)!;
            Type packetBlockType = libAsm.GetType("Packet_BlockType", throwOnError: true)!;
            Type compressionType = libAsm.GetType("Vintagestory.Common.Compression", throwOnError: true)!;
            Type tcpConnType = libAsm.GetType("Vintagestory.Server.Network.TcpNetConnection", throwOnError: true)!;
            Type dummyConnType = libAsm.GetType("Vintagestory.Common.DummyNetConnection", throwOnError: true)!;

            // Build a synthetic >1460-byte Packet_ServerAssets payload
            Array blockPackets = Array.CreateInstance(packetBlockType, 400);
            MethodInfo setCodeMethod = packetBlockType.GetMethod("SetCode", [typeof(string)])!;
            for (int i = 0; i < blockPackets.Length; i++)
            {
                object blk = Activator.CreateInstance(packetBlockType)!;
                setCodeMethod.Invoke(blk, [$"game:testblock-{i}-payload-padding-for-compression"]);
                blockPackets.SetValue(blk, i);
            }
            object assets = Activator.CreateInstance(packetAssetsType)!;
            packetAssetsType.GetMethod("SetBlocks", [blockPackets.GetType()])!.Invoke(assets, [blockPackets]);
            object packetServer = Activator.CreateInstance(packetServerType)!;
            packetServerType.GetMethod("SetId", [typeof(int)])!.Invoke(packetServer, [19]);
            packetServerType.GetMethod("SetAssets", [packetAssetsType])!.Invoke(packetServer, [assets]);

            object boxed = Activator.CreateInstance(boxedType)!;
            MethodInfo serializeMethod = boxedType.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            int rawLength = (int)serializeMethod.Invoke(boxed, [packetServer])!;
            byte[] expectedRawBytes = new byte[rawLength];
            byte[] serializedBuf = (byte[])boxedType.GetField("buffer")!.GetValue(boxed)!;
            Array.Copy(serializedBuf, expectedRawBytes, rawLength);

            // Fresh BoxedPacket_ServerAssets tests CacheCompressedAndRelease end-to-end
            object boxedClean = Activator.CreateInstance(boxedType)!;
            boxedType.GetMethod("CacheCompressedAndRelease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(boxedClean, [packetServer, true]);

            Assert.True((bool)boxedType.GetMethod("HasPayload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boxedClean, null)!);
            Assert.Null(boxedType.GetField("packet", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxedClean));
            Assert.Null(boxedType.GetField("buffer")!.GetValue(boxedClean));
            Assert.Equal(0, (int)boxedType.GetField("Length")!.GetValue(boxedClean)!);

            byte[] compressedBuf = (byte[])boxedType.GetField("compressedBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxedClean)!;
            Assert.NotNull(compressedBuf);
            Assert.True(compressedBuf.Length < expectedRawBytes.Length);

            // TcpNetConnection.PreparePacketForSending with compressionAllowed=true returns compressedBuf
            object tcpConn = RuntimeHelpersGetUninitialized(tcpConnType);
            MethodInfo tcpPrepare = tcpConnType.GetMethod("PreparePacketForSending")!;
            object?[] tcpArgsComp = [boxedClean, true, false];
            byte[] tcpWireComp = (byte[])tcpPrepare.Invoke(tcpConn, tcpArgsComp)!;
            Assert.Same(compressedBuf, tcpWireComp);
            Assert.True((bool)tcpArgsComp[2]!);

            // TcpNetConnection.PreparePacketForSending with compressionAllowed=false decompresses exact bytes
            object?[] tcpArgsRaw = [boxedClean, false, true];
            byte[] tcpWireRaw = (byte[])tcpPrepare.Invoke(tcpConn, tcpArgsRaw)!;
            Assert.False((bool)tcpArgsRaw[2]!);
            Assert.Equal(expectedRawBytes.Length + 4, tcpWireRaw.Length);
            Assert.Equal(expectedRawBytes, tcpWireRaw.AsSpan(4).ToArray());

            // DummyNetConnection.PreparePacketForSending decompresses cleanly without 4-byte framing prefix
            object dummyConn = RuntimeHelpersGetUninitialized(dummyConnType);
            object?[] dummyArgs = [boxedClean, false, true];
            byte[] dummyWire = (byte[])dummyConnType.GetMethod("PreparePacketForSending")!.Invoke(dummyConn, dummyArgs)!;
            Assert.False((bool)dummyArgs[2]!);
            Assert.Equal(expectedRawBytes, dummyWire);

            // Decompressed payload from compressedBuf matches original serialized bytes
            byte[] roundtrip = (byte[])compressionType.GetMethod("Decompress", [typeof(byte[]), typeof(int), typeof(int)])!
                .Invoke(null, [compressedBuf, 4, compressedBuf.Length - 4])!;
            Assert.Equal(expectedRawBytes, roundtrip);

            // Invalidate resets everything cleanly
            boxedType.GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boxedClean, null);
            Assert.False((bool)boxedType.GetMethod("HasPayload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boxedClean, null)!);
            Assert.Null(boxedType.GetField("compressedBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxedClean));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static object RuntimeHelpersGetUninitialized(Type t)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
}
