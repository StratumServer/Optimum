using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class NetworkAndAudioBufferReuseTests
{
    private static string ResolvePatchedVintagestoryLibPath()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"), "..", ".."));
        string prebuilt = Path.Combine(
            repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll");
        if (File.Exists(prebuilt))
        {
            return prebuilt;
        }

        string donor = Path.Combine(
            repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib.dll");
        if (File.Exists(donor))
        {
            return donor;
        }

        return null;
    }

    private static Assembly LoadPatchedVintagestoryLibAssembly()
    {
        Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "VintagestoryLib", StringComparison.Ordinal));
        if (loaded != null)
        {
            return loaded;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"), "..", ".."));
        string donorPath = Path.Combine(
            repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib.dll");
        if (!File.Exists(donorPath))
        {
            return null;
        }

        string vanillaDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory");
        string vanillaLibDir = Path.Combine(vanillaDir, "Lib");

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string candidate1 = Path.Combine(vanillaDir, simpleName + ".dll");
            if (File.Exists(candidate1))
            {
                return Assembly.LoadFrom(candidate1);
            }
            string candidate2 = Path.Combine(vanillaLibDir, simpleName + ".dll");
            if (File.Exists(candidate2))
            {
                return Assembly.LoadFrom(candidate2);
            }
            return null;
        };

        return Assembly.LoadFrom(donorPath);
    }

    [Fact]
    public void NetworkAndAudioBufferReuseEnabled_DefaultsToTrue()
    {
        Assert.True(OptimumConfig.NetworkAndAudioBufferReuseEnabled);
    }

    [Fact]
    public void PatchedVintagestoryLib_ContainsIssue649NetworkAndAudioTransplantsAndThreadStaticAttributes()
    {
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));

        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/TCPNetworkConnection.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Server.Network/TcpNetConnection.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Server.Network/UdpNetServer.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/OggDecoder.cs.patch", cecilList);

        Assert.Contains("new(\"Vintagestory.Common.TCPNetworkConnection\", \"OnBytesReceived\", 1)", programSource);
        Assert.Contains("new(\"Vintagestory.Common.TCPNetworkConnection\", \"Receive\", 3)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.Network.TcpNetConnection\", \"ReceiveData\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.Network.TcpNetConnection\", \"Send\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.Network.TcpNetConnection\", \"HiPerformanceSend\", 3)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.Network.UdpNetServer\", \"ReadMessage\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Server.Network.UdpNetServer\", \"SendToClient\", 2)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.ScreenManager\", \"LoadSoundsInitial\", 0)", programSource);

        string patchedPath = ResolvePatchedVintagestoryLibPath();
        if (patchedPath == null)
        {
            return;
        }

        using var asm = AssemblyDefinition.ReadAssembly(patchedPath);
        ModuleDefinition mod = asm.MainModule;

        TypeDefinition oggDecoder = mod.GetType("Vintagestory.Client.NoObf.OggDecoder");
        Assert.NotNull(oggDecoder);
        string[] threadStaticOggFields =
        {
            "convbuffer",
            "threadSyncState",
            "threadStreamState",
            "threadOggPage",
            "threadPacket",
            "threadComment",
            "threadBlock",
            "threadPcmOut",
            "threadPcmIndex",
        };
        foreach (string fieldName in threadStaticOggFields)
        {
            FieldDefinition field = oggDecoder.Fields.SingleOrDefault(f => f.Name == fieldName);
            Assert.NotNull(field);
            Assert.Contains(field.CustomAttributes, a => a.AttributeType.FullName == "System.ThreadStaticAttribute");
        }

        TypeDefinition udpNetServer = mod.GetType("Vintagestory.Server.Network.UdpNetServer");
        Assert.NotNull(udpNetServer);
        FieldDefinition udpSendBuffer = udpNetServer.Fields.SingleOrDefault(f => f.Name == "udpSendBuffer");
        Assert.NotNull(udpSendBuffer);
        Assert.Contains(udpSendBuffer.CustomAttributes, a => a.AttributeType.FullName == "System.ThreadStaticAttribute");

        MethodDefinition udpReadMessage = udpNetServer.Methods.Single(m => m.Name == "ReadMessage" && m.Parameters.Count == 0);
        Assert.Contains(udpReadMessage.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "TryDequeue");

        TypeDefinition tcpConnClient = mod.GetType("Vintagestory.Common.TCPNetworkConnection");
        Assert.NotNull(tcpConnClient);
        MethodDefinition clientReceive = tcpConnClient.Methods.Single(m => m.Name == "Receive" && m.Parameters.Count == 3);
        Assert.Contains(clientReceive.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "DequeueReceivedBytes");

        TypeDefinition tcpConnServer = mod.GetType("Vintagestory.Server.Network.TcpNetConnection");
        Assert.NotNull(tcpConnServer);
        MethodDefinition serverSend = tcpConnServer.Methods.Single(m => m.Name == "Send" && m.Parameters.Count == 2);
        Assert.Contains(serverSend.Body.Instructions,
            ins => ins.Operand is MethodReference mr && mr.Name == "SendPooledPayload");

        TypeDefinition screenMgr = mod.GetType("Vintagestory.Client.ScreenManager");
        Assert.NotNull(screenMgr);
        MethodDefinition loadSoundsInitial = screenMgr.Methods.Single(m => m.Name == "LoadSoundsInitial" && m.Parameters.Count == 0);
        Assert.Contains(loadSoundsInitial.Body.Instructions,
            ins => ins.Operand is FieldReference fr && fr.Name == "NetworkAndAudioBufferReuseEnabled");
    }

    [Fact]
    public void TCPNetworkConnection_RingBufferWrapAroundGrowthAndChunkedReceive_MatchesVanillaFifoOrdering()
    {
        Assembly libAsm = LoadPatchedVintagestoryLibAssembly();
        if (libAsm == null)
        {
            return;
        }

        Type tcpType = libAsm.GetType("Vintagestory.Common.TCPNetworkConnection", throwOnError: true);
        object conn = RuntimeHelpers.GetUninitializedObject(tcpType);
        FieldInfo receivedQueueField = tcpType.GetField("received", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo dataRcvBufField = tcpType.GetField("dataRcvBuf", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo processChunk = tcpType.GetMethod("ProcessReceivedChunk", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo receiveMethod = tcpType.GetMethod("Receive", BindingFlags.Instance | BindingFlags.Public)!;

        var queue = new Queue<byte>();
        byte[] rcvBacking = new byte[8192];
        receivedQueueField.SetValue(conn, queue);
        dataRcvBufField.SetValue(conn, new Memory<byte>(rcvBacking));

        bool prevFlag = OptimumConfig.NetworkAndAudioBufferReuseEnabled;
        try
        {
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = true;

            // Feed 40,000 deterministic bytes in mixed chunk sizes while interleaving partial reads
            // to force ring wrap-around and capacity growth (16,384 -> 32,768 -> 65,536).
            int totalWritten = 0;
            int totalRead = 0;
            int[] chunkSizes = { 5000, 7000, 6000, 8000, 8192, 5808 };
            byte[] readScratch = new byte[6144];

            for (int step = 0; step < chunkSizes.Length; step++)
            {
                int chunkLen = chunkSizes[step];
                for (int i = 0; i < chunkLen; i++)
                {
                    rcvBacking[i] = (byte)((totalWritten + i) * 31 + 7);
                }
                totalWritten += chunkLen;

                bool keepGoing = (bool)processChunk.Invoke(conn, new object[] { chunkLen })!;
                Assert.True(keepGoing);

                if (step % 2 == 0)
                {
                    object[] args = { readScratch, 3333, 0 };
                    receiveMethod.Invoke(conn, args);
                    int got = (int)args[2];
                    Assert.Equal(3333, got);
                    for (int i = 0; i < got; i++)
                    {
                        byte expected = (byte)((totalRead + i) * 31 + 7);
                        Assert.Equal(expected, readScratch[i]);
                    }
                    totalRead += got;
                }
            }

            // Drain all remaining bytes and verify exact FIFO sequence.
            while (totalRead < totalWritten)
            {
                object[] args = { readScratch, readScratch.Length, 0 };
                receiveMethod.Invoke(conn, args);
                int got = (int)args[2];
                Assert.True(got > 0);
                for (int i = 0; i < got; i++)
                {
                    byte expected = (byte)((totalRead + i) * 31 + 7);
                    Assert.Equal(expected, readScratch[i]);
                }
                totalRead += got;
            }

            Assert.Equal(totalWritten, totalRead);
            Assert.Empty(queue);
        }
        finally
        {
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = prevFlag;
        }
    }

    [Fact]
    public void TcpNetConnection_And_UdpNetServer_LoopbackFraming_And_ReadMessageBatchDrain_MatchVanilla()
    {
        Assembly libAsm = LoadPatchedVintagestoryLibAssembly();
        if (libAsm == null)
        {
            return;
        }

        bool prevFlag = OptimumConfig.NetworkAndAudioBufferReuseEnabled;
        try
        {
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = true;

            // 1. Verify UdpNetServer.ReadMessage batch draining, caching, and reference clearing,
            // plus SendToClient over localhost UDP.
            Type udpServerType = libAsm.GetType("Vintagestory.Server.Network.UdpNetServer", throwOnError: true);
            Type udpPacketStructType = libAsm.GetType("Vintagestory.Common.Network.Packets.UdpPacket", throwOnError: true);
            Type packetUdpType = libAsm.GetType("Packet_UdpPacket", throwOnError: true);
            Type packetEntityPosType = libAsm.GetType("Packet_EntityPosition", throwOnError: true);
            Type serializerType = libAsm.GetType("Packet_UdpPacketSerializer", throwOnError: true);

            object udpNetServer = Activator.CreateInstance(udpServerType, new object[] { null })!;
            MethodInfo enqueuePacket = udpServerType.GetMethod("EnqueuePacket", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo readMessage = udpServerType.GetMethod("ReadMessage", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo addEndpoint = udpServerType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo sendToClient = udpServerType.GetMethod("SendToClient", BindingFlags.Instance | BindingFlags.Public)!;
            FieldInfo packetField = udpPacketStructType.GetField("Packet")!;
            FieldInfo idField = packetUdpType.GetField("Id")!;
            FieldInfo entityPosField = packetUdpType.GetField("EntityPosition")!;
            FieldInfo xField = packetEntityPosType.GetField("X")!;
            FieldInfo yField = packetEntityPosType.GetField("Y")!;
            FieldInfo zField = packetEntityPosType.GetField("Z")!;

            Assert.Null(readMessage.Invoke(udpNetServer, null));

            for (int i = 0; i < 5; i++)
            {
                object pkt = Activator.CreateInstance(packetUdpType)!;
                idField.SetValue(pkt, 2);
                object udpItem = Activator.CreateInstance(udpPacketStructType)!;
                packetField.SetValue(udpItem, pkt);
                enqueuePacket.Invoke(udpNetServer, new[] { udpItem });
            }

            Array batch1 = (Array)readMessage.Invoke(udpNetServer, null)!;
            Assert.NotNull(batch1);
            Assert.Equal(5, batch1.Length);
            for (int i = 0; i < 5; i++)
            {
                object item = batch1.GetValue(i)!;
                object pkt = packetField.GetValue(item)!;
                Assert.Equal(2, (int)idField.GetValue(pkt)!);
            }

            // Next empty ReadMessage must return null and clear references in the cached array.
            Assert.Null(readMessage.Invoke(udpNetServer, null));
            for (int i = 0; i < 5; i++)
            {
                object clearedItem = batch1.GetValue(i)!;
                Assert.Null(packetField.GetValue(clearedItem));
            }

            // Test SendToClient over localhost UDP to a receiver UdpClient.
            using var receiverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var senderUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            FieldInfo udpClientField = udpServerType.GetField("udpServer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            udpClientField.SetValue(udpNetServer, senderUdp);

            var targetEp = (IPEndPoint)receiverUdp.Client.LocalEndPoint!;
            addEndpoint.Invoke(udpNetServer, new object[] { targetEp, 42 });

            object outPkt = Activator.CreateInstance(packetUdpType)!;
            idField.SetValue(outPkt, 5);
            object posPkt = Activator.CreateInstance(packetEntityPosType)!;
            xField.SetValue(posPkt, 123456L);
            yField.SetValue(posPkt, 654321L);
            zField.SetValue(posPkt, 789012L);
            entityPosField.SetValue(outPkt, posPkt);

            int sentBytes = (int)sendToClient.Invoke(udpNetServer, new object[] { 42, outPkt })!;
            Assert.True(sentBytes > 0);

            IPEndPoint remoteEp = null;
            byte[] receivedDatagram = receiverUdp.Receive(ref remoteEp);
            Assert.Equal(sentBytes, receivedDatagram.Length);

            object decodedPkt = Activator.CreateInstance(packetUdpType)!;
            MethodInfo deserializeBuf = serializerType.GetMethod("DeserializeBuffer", BindingFlags.Static | BindingFlags.Public)!;
            deserializeBuf.Invoke(null, new object[] { receivedDatagram, receivedDatagram.Length, decodedPkt });
            Assert.Equal(5, (int)idField.GetValue(decodedPkt)!);
            object decodedPos = entityPosField.GetValue(decodedPkt)!;
            Assert.NotNull(decodedPos);
            Assert.Equal(123456L, (long)xField.GetValue(decodedPos)!);
            Assert.Equal(654321L, (long)yField.GetValue(decodedPos)!);
            Assert.Equal(789012L, (long)zField.GetValue(decodedPos)!);

            // 2. Verify TcpNetConnection.Send and HiPerformanceSend framing over a loopback TCP socket pair.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var clientSide = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSide.Connect(IPAddress.Loopback, port);
            using Socket serverSide = listener.AcceptSocket();

            Type tcpNetConnType = libAsm.GetType("Vintagestory.Server.Network.TcpNetConnection", throwOnError: true);
            Type boxedPacketType = libAsm.GetType("Vintagestory.Common.BoxedPacket", throwOnError: true);
            object tcpNetConn = Activator.CreateInstance(tcpNetConnType, new object[] { serverSide })!;
            FieldInfo ctsField = tcpNetConnType.GetField("cts", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var cts = new CancellationTokenSource();
            ctsField.SetValue(tcpNetConn, cts);

            MethodInfo sendMethod = tcpNetConnType.GetMethod("Send", BindingFlags.Instance | BindingFlags.Public)!;
            MethodInfo hiPerfSend = tcpNetConnType.GetMethod("HiPerformanceSend", BindingFlags.Instance | BindingFlags.Public)!;

            byte[] payload1 = { 10, 20, 30, 40, 50 };
            sendMethod.Invoke(tcpNetConn, new object[] { payload1, false });

            object box = Activator.CreateInstance(boxedPacketType)!;
            byte[] payload2 = { 1, 2, 3, 4, 5, 6, 7, 8 };
            boxedPacketType.GetField("buffer")!.SetValue(box, payload2);
            boxedPacketType.GetField("Length")!.SetValue(box, payload2.Length);
            hiPerfSend.Invoke(tcpNetConn, new object[] { box, null, true });
            Assert.Equal(payload2.Length, (int)boxedPacketType.GetField("LengthSent")!.GetValue(box)!);

            byte[] wireBuf = new byte[(4 + payload1.Length) + (4 + payload2.Length)];
            int readTotal = 0;
            while (readTotal < wireBuf.Length)
            {
                int r = clientSide.Receive(wireBuf, readTotal, wireBuf.Length - readTotal, SocketFlags.None);
                Assert.True(r > 0);
                readTotal += r;
            }

            int frame1Len = BinaryPrimitives.ReadInt32BigEndian(wireBuf.AsSpan(0, 4));
            Assert.Equal(payload1.Length, frame1Len);
            Assert.True(wireBuf.AsSpan(4, payload1.Length).SequenceEqual(payload1));

            int frame2Len = BinaryPrimitives.ReadInt32BigEndian(wireBuf.AsSpan(4 + payload1.Length, 4));
            Assert.Equal(payload2.Length, frame2Len);
            Assert.True(wireBuf.AsSpan(8 + payload1.Length, payload2.Length).SequenceEqual(payload2));
        }
        finally
        {
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = prevFlag;
        }
    }

    private sealed class MinimalOggAsset : IAsset
    {
        public MinimalOggAsset(AssetLocation location)
        {
            Location = location;
            Name = location.GetName();
        }

        public string Name { get; }
        public AssetLocation Location { get; }
        public IAssetOrigin Origin { get; set; }
        public byte[] Data { get; set; }
        public bool IsPatched { get; set; }
        public bool IsLoaded() => Data != null;
        public BitmapRef ToBitmap(Vintagestory.API.Client.ICoreClientAPI api) => null;
        public T ToObject<T>(Newtonsoft.Json.JsonSerializerSettings settings = null) => default;
        public string ToText() => string.Empty;
    }

    [Fact]
    public void OggDecoder_ReusesThreadStaticDecoderBuffers_And_ReducesSmallFileAllocations()
    {
        Assembly libAsm = LoadPatchedVintagestoryLibAssembly();
        if (libAsm == null)
        {
            return;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(
            PatchReader.FindRepositoryFile("Optimum.Tests/Optimum.Tests.csproj"), "..", ".."));
        string menuButtonOgg = Path.Combine(
            repoRoot, ".vanilla", "linux-x64", "vintagestory", "assets", "game", "sounds", "menubutton.ogg");
        if (!File.Exists(menuButtonOgg))
        {
            menuButtonOgg = Path.Combine(
                repoRoot, ".vanilla", "win-x64", "vintagestory", "assets", "game", "sounds", "menubutton.ogg");
        }
        if (!File.Exists(menuButtonOgg))
        {
            return;
        }

        byte[] oggBytes = File.ReadAllBytes(menuButtonOgg);
        var dummyAsset = new MinimalOggAsset(new AssetLocation("game", "sounds/menubutton.ogg"));
        Type oggDecoderType = libAsm.GetType("Vintagestory.Client.NoObf.OggDecoder", throwOnError: true);
        object decoder = Activator.CreateInstance(oggDecoderType)!;
        MethodInfo oggToWav = oggDecoderType.GetMethod("OggToWav", BindingFlags.Instance | BindingFlags.Public)!;
        Type audioMetaType = libAsm.GetType("Vintagestory.Client.NoObf.AudioMetaData", throwOnError: true);
        FieldInfo pcmField = audioMetaType.GetField("Pcm", BindingFlags.Instance | BindingFlags.Public)!;
        FieldInfo channelsField = audioMetaType.GetField("Channels", BindingFlags.Instance | BindingFlags.Public)!;
        FieldInfo rateField = audioMetaType.GetField("Rate", BindingFlags.Instance | BindingFlags.Public)!;

        bool prevFlag = OptimumConfig.NetworkAndAudioBufferReuseEnabled;
        try
        {
            // Warm up both paths once
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = false;
            object baselineMeta = oggToWav.Invoke(decoder, new object[] { new MemoryStream(oggBytes), dummyAsset })!;
            byte[] baselinePcm = (byte[])pcmField.GetValue(baselineMeta)!;
            int baselineChannels = (int)channelsField.GetValue(baselineMeta)!;
            int baselineRate = (int)rateField.GetValue(baselineMeta)!;

            long baseBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                _ = oggToWav.Invoke(decoder, new object[] { new MemoryStream(oggBytes), dummyAsset })!;
            }
            long baseAllocPerFile = (GC.GetAllocatedBytesForCurrentThread() - baseBefore) / 10;

            OptimumConfig.NetworkAndAudioBufferReuseEnabled = true;
            object optMeta = oggToWav.Invoke(decoder, new object[] { new MemoryStream(oggBytes), dummyAsset })!;
            byte[] optPcm = (byte[])pcmField.GetValue(optMeta)!;
            int optChannels = (int)channelsField.GetValue(optMeta)!;
            int optRate = (int)rateField.GetValue(optMeta)!;

            Assert.Equal(baselineChannels, optChannels);
            Assert.Equal(baselineRate, optRate);
            Assert.Equal(baselinePcm.Length, optPcm.Length);

            // Verify sample values match within <= 1 LSB (float vs double 32767 scaling).
            for (int offset = 0; offset + 1 < baselinePcm.Length; offset += 2)
            {
                short sBase = BinaryPrimitives.ReadInt16LittleEndian(baselinePcm.AsSpan(offset, 2));
                short sOpt = BinaryPrimitives.ReadInt16LittleEndian(optPcm.AsSpan(offset, 2));
                Assert.True(Math.Abs(sBase - sOpt) <= 1, $"Sample delta > 1 LSB at byte {offset}: {sBase} vs {sOpt}");
            }

            long optBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                _ = oggToWav.Invoke(decoder, new object[] { new MemoryStream(oggBytes), dummyAsset })!;
            }
            long optAllocPerFile = (GC.GetAllocatedBytesForCurrentThread() - optBefore) / 10;

            // Reusing SyncState (8 KB), StreamState (28 KB), and Block PCM buffers saves > 35 KB per file.
            Assert.True(baseAllocPerFile - optAllocPerFile >= 30_000,
                $"Expected >= 30 KB allocation savings per Ogg decode, got baseline={baseAllocPerFile} B vs opt={optAllocPerFile} B");
        }
        finally
        {
            OptimumConfig.NetworkAndAudioBufferReuseEnabled = prevFlag;
        }
    }
}
