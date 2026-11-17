using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class GlFinalizerReclaimTests
{
    public class InterceptorProxy : DispatchProxy
    {
        internal System.Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod == null ? null : Handler(targetMethod, args);
        }
    }

    private sealed class TestClientHarness
    {
        internal int DebugCount;
        internal int DeleteCallCount;
        internal int LastDeletedTextureId;
        internal bool ShuttingDown;
        internal Action<int>? OnDeleteTexture;
        internal readonly ICoreClientAPI Api;

        internal TestClientHarness()
        {
            var logger = DispatchProxy.Create<ILogger, InterceptorProxy>();
            ((InterceptorProxy)(object)logger).Handler = (method, args) =>
            {
                if (method.Name == "Debug" ||
                    (method.Name == "Log" && args?.Length > 0 && Equals(args[0], EnumLogType.Debug)))
                {
                    DebugCount++;
                }
                return null;
            };

            var gui = DispatchProxy.Create<IGuiAPI, InterceptorProxy>();
            ((InterceptorProxy)(object)gui).Handler = (method, args) =>
            {
                if (method.Name == "DeleteTexture" && args?.Length == 1 && args[0] is int texId)
                {
                    DeleteCallCount++;
                    LastDeletedTextureId = texId;
                    OnDeleteTexture?.Invoke(texId);
                }
                return null;
            };

            Api = DispatchProxy.Create<ICoreClientAPI, InterceptorProxy>();
            ((InterceptorProxy)(object)Api).Handler = (method, _) => method.Name switch
            {
                "get_IsShuttingDown" => ShuttingDown,
                "get_Gui" => gui,
                "get_Logger" => logger,
                _ => null,
            };
        }
    }

    [Fact]
    public void DisposeLoadedTexture_SuppressesFinalizer_ZerosId_AndGuardsNullCapi()
    {
        OptimumGlReclaimQueue.ResetForTests();
        var harness = new TestClientHarness();
        var tex = new LoadedTexture(harness.Api, 77, 16, 16);

        tex.Dispose();

        Assert.True(tex.Disposed);
        Assert.Equal(0, tex.TextureId);
        Assert.Equal(1, harness.DeleteCallCount);
        Assert.Equal(77, harness.LastDeletedTextureId);
        Assert.Equal(0, OptimumGlReclaimQueue.PendingTotalCount);

        var nullApiTex = new LoadedTexture(null, 88, 16, 16);
        nullApiTex.Dispose();
        Assert.True(nullApiTex.Disposed);
        Assert.Equal(0, nullApiTex.TextureId);
    }

    [Fact]
    public void FinalizeLoadedTexture_ReclaimsAcrossWorldExit_ButSkipsSharedNullCapiAndShutdown()
    {
        bool origEnabled = OptimumConfig.GlFinalizerReclaimEnabled;
        OptimumGlReclaimQueue.ResetForTests();
        try
        {
            var harness = new TestClientHarness { ShuttingDown = true };
            bool disposed = false;
            int textureId = 314;

            // World exit sets capi.IsShuttingDown = true while GL context stays alive: must still reclaim.
            OptimumApiBridge.FinalizeLoadedTexture(ref disposed, ref textureId, ignoreUndisposed: false, trace: null, harness.Api);
            Assert.True(disposed);
            Assert.Equal(0, textureId);
            Assert.Equal(1L, OptimumGlReclaimQueue.PendingTextureCount);

            // Duplicate call must be a no-op.
            OptimumApiBridge.FinalizeLoadedTexture(ref disposed, ref textureId, ignoreUndisposed: false, trace: null, harness.Api);
            Assert.Equal(1L, OptimumGlReclaimQueue.PendingTextureCount);

            // Null capi (non-owning atlas/view wrapper like EntityBehaviorTexturedClothing) must never delete live textures.
            bool nullCapiDisposed = false;
            int nullCapiId = 555;
            OptimumApiBridge.FinalizeLoadedTexture(ref nullCapiDisposed, ref nullCapiId, ignoreUndisposed: false, trace: null, null);
            Assert.False(nullCapiDisposed);
            Assert.Equal(555, nullCapiId);
            Assert.Equal(1L, OptimumGlReclaimQueue.PendingTextureCount);

            // IgnoreUndisposed = true (GetOrLoadCachedTexture wrapper) must never delete cached texture.
            bool sharedDisposed = false;
            int sharedId = 666;
            OptimumApiBridge.FinalizeLoadedTexture(ref sharedDisposed, ref sharedId, ignoreUndisposed: true, trace: null, harness.Api);
            Assert.False(sharedDisposed);
            Assert.Equal(1L, OptimumGlReclaimQueue.PendingTextureCount);

            // GL platform shutdown stops further queueing.
            OptimumGlReclaimQueue.SetGlShuttingDown(true);
            bool shutdownDisposed = false;
            int shutdownId = 777;
            OptimumApiBridge.FinalizeLoadedTexture(ref shutdownDisposed, ref shutdownId, ignoreUndisposed: false, trace: null, harness.Api);
            Assert.False(shutdownDisposed);
            Assert.Equal(1L, OptimumGlReclaimQueue.PendingTextureCount);

            Span<int> drained = stackalloc int[4];
            Assert.Equal(1, OptimumGlReclaimQueue.DequeueTextures(drained));
            Assert.Equal(314, drained[0]);
            Assert.Equal(0L, OptimumGlReclaimQueue.PendingTextureCount);
        }
        finally
        {
            OptimumConfig.GlFinalizerReclaimEnabled = origEnabled;
            OptimumGlReclaimQueue.ResetForTests();
        }
    }

    [Fact]
    public void FinalizeLoadedTexture_RateLimitsLogs_AndRespectsModFinalizerOverride()
    {
        OptimumGlReclaimQueue.ResetForTests();
        try
        {
            var harness = new TestClientHarness();
            for (int i = 1; i <= 50; i++)
            {
                bool disposed = false;
                int id = i;
                OptimumApiBridge.FinalizeLoadedTexture(ref disposed, ref id, ignoreUndisposed: false, trace: null, harness.Api);
            }

            Assert.Equal(50L, OptimumGlReclaimQueue.PendingTextureCount);
            Assert.Equal(1, harness.DebugCount);

            OptimumGlReclaimQueue.SetExternalFinalizerPatchOverridesForTest(loadedTexturePatched: true, vaoPatched: true);
            bool modDisposed = false;
            int modId = 999;
            OptimumApiBridge.FinalizeLoadedTexture(ref modDisposed, ref modId, ignoreUndisposed: false, trace: null, harness.Api);
            Assert.True(modDisposed);
            Assert.Equal(0, modId);
            Assert.Equal(50L, OptimumGlReclaimQueue.PendingTextureCount);
        }
        finally
        {
            OptimumGlReclaimQueue.ResetForTests();
        }
    }

    [Fact]
    public void OptimumGlReclaimQueue_SeparatesQueues_ExcludesSharedEboAndStaleGenerations_AndScalesBudget()
    {
        OptimumGlReclaimQueue.ResetForTests();
        try
        {
            const int sharedEboId = 42;
            OptimumGlReclaimQueue.EnqueueVaoWithBuffers(
                vaoId: 10,
                xyzVboId: 11,
                normalsVboId: 12,
                uvVboId: 0,
                rgbaVboId: 13,
                flagsVboId: 0,
                customFloatVboId: 0,
                customIntVboId: 0,
                customShortVboId: 0,
                customByteVboId: 0,
                indexVboId: sharedEboId);

            Assert.Equal(1L, OptimumGlReclaimQueue.PendingVaoCount);
            Assert.Equal(4L, OptimumGlReclaimQueue.PendingBufferCount);
            Assert.Equal(OptimumGlReclaimQueue.MinBatchPerFrame, OptimumGlReclaimQueue.ComputeDynamicBatchBudget());

            Span<int> vaos = stackalloc int[8];
            Span<int> buffers = stackalloc int[8];
            Assert.Equal(1, OptimumGlReclaimQueue.DequeueVaos(vaos));
            Assert.Equal(10, vaos[0]);

            int bufferCount = OptimumGlReclaimQueue.DequeueBuffers(buffers, sharedIndexBufferId: sharedEboId);
            Assert.Equal(3, bufferCount);
            Assert.DoesNotContain(sharedEboId, buffers.Slice(0, bufferCount).ToArray());

            // Dynamic batch growth on large drops and stale generation filtering across context restart.
            for (int i = 1; i <= 2400; i++)
            {
                OptimumGlReclaimQueue.EnqueueTexture(i);
            }
            int budget = OptimumGlReclaimQueue.ComputeDynamicBatchBudget();
            Assert.InRange(budget, 200, OptimumGlReclaimQueue.MaxBatchPerFrame);

            OptimumGlReclaimQueue.EndContextAndAdvanceGeneration();
            Assert.Equal(0L, OptimumGlReclaimQueue.PendingTotalCount);

            // Stale entry pushed before EndContext cannot survive generation bump.
            OptimumGlReclaimQueue.BeginNewContext();
            OptimumGlReclaimQueue.EnqueueTexture(8001);
            OptimumGlReclaimQueue.BeginNewContext();
            Assert.Equal(0L, OptimumGlReclaimQueue.PendingTotalCount);
            Span<int> textures = stackalloc int[8];
            Assert.Equal(0, OptimumGlReclaimQueue.DequeueTextures(textures));
        }
        finally
        {
            OptimumGlReclaimQueue.ResetForTests();
        }
    }

    [Fact]
    public void PatchLoadedTextureDisposal_TransformsVanillaApi_WithValidIlStack()
    {
        string vanillaApiPath;
        try
        {
            vanillaApiPath = PatchReader.FindRepositoryFile(".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll");
        }
        catch (FileNotFoundException)
        {
            return;
        }

        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumApiBridge).Assembly.Location);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")!;
        var disposeBridge = bridge.Methods.Single(m => m.Name == "DisposeLoadedTexture" && m.Parameters.Count == 4);
        var finalizeBridge = bridge.Methods.Single(m => m.Name == "FinalizeLoadedTexture" && m.Parameters.Count == 5);

        int patched = ApiPatcher.PatchLoadedTextureDisposal(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(disposeBridge),
            vanilla.MainModule.ImportReference(finalizeBridge));

        Assert.Equal(2, patched);
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        var textureType = vanilla.MainModule.GetType("Vintagestory.API.Client.LoadedTexture")!;
        var disposeMethod = textureType.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        Assert.Contains(disposeMethod.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "DisposeLoadedTexture");

        var finalizeMethod = textureType.Methods.Single(m => m.Name == "Finalize" && m.Parameters.Count == 0);
        Assert.Single(finalizeMethod.Body.ExceptionHandlers);
        Assert.Equal(ExceptionHandlerType.Finally, finalizeMethod.Body.ExceptionHandlers[0].HandlerType);
        Assert.Contains(finalizeMethod.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "FinalizeLoadedTexture");
    }

    [Fact]
    public void Issue605_PatchesAndTargetsAreRegisteredConsistentAcrossPatchersAndOwnedList()
    {
        string programCs = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string apiPatcherCs = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        string modPatcherCs = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/mod-patcher.cs"));
        string cecilOwned = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        string statusCs = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
        string clothingPatch = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityBehaviorTexturedClothing.cs.patch");

        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/VAO.cs.patch", cecilOwned);
        Assert.Contains("\"sharedIndexBuffer\"", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.VAO\", \"Dispose\", 0)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.VAO\", \"Finalize\", 0)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.RenderAPIBase\", \"LoadTexture\", 5)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"GetOrLoadCachedTexture\", 2)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"GetOrLoadCachedTexture\", 3)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"AllocateEmptySSBOMesh\", 12)", programCs);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"WindowExit\", 2)", programCs);
        Assert.Contains("DrainFinalizerReclaimQueues", programCs);

        Assert.Contains("PatchLoadedTextureDisposal(", apiPatcherCs);
        Assert.Contains("EntityBehaviorTexturedClothing", modPatcherCs);
        Assert.Contains("IgnoreUndisposed = true", clothingPatch);
        Assert.Contains("GetGlReclaimSummary()", statusCs);
    }

    [GlContextFact]
    public void RealGlContext_ReclaimsLeakedVaoPersistentVboAndTexture_PreservesSharedEbo_AndSkipsDisposed()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string libPath = PatchReader.FindRepositoryFile($"build/VintagestoryLib/bin/{configuration}/net10.0/VintagestoryLib.dll");
        if (!File.Exists(libPath))
        {
            return;
        }

        Assembly lib = Assembly.LoadFrom(libPath);
        Type vaoType = lib.GetType("Vintagestory.Client.NoObf.VAO", throwOnError: true)!;
        Type platformType = lib.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows", throwOnError: true)!;
        Type platAbstractType = lib.GetType("Vintagestory.Client.NoObf.ClientPlatformAbstract", throwOnError: true)!;

        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(4, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new OpenTK.Mathematics.Vector2i(32, 32),
        });
        window.MakeCurrent();
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

        bool origEnabled = OptimumConfig.GlFinalizerReclaimEnabled;
        OptimumGlReclaimQueue.ResetForTests();
        try
        {
            OptimumConfig.GlFinalizerReclaimEnabled = true;
            object platform = Activator.CreateInstance(platformType, new object?[] { null })!;
            MethodInfo drainMethod = platformType.GetMethod("DrainFinalizerReclaimQueues", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo sharedEboField = platAbstractType.GetField("singleIndexBufferId", BindingFlags.Public | BindingFlags.Static)!;

            // 1. Cleanly disposed VAO & LoadedTexture must NOT enqueue into finalizer queues.
            int cleanVao = GL.GenVertexArray();
            int cleanVbo = GL.GenBuffer();
            GL.BindVertexArray(cleanVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cleanVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

            int cleanTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, cleanTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            var cleanHarness = new TestClientHarness { OnDeleteTexture = GL.DeleteTexture };
            AllocateAndDispose(vaoType, cleanHarness.Api, cleanVao, cleanVbo, cleanTex);
            RunGcAndWaitForFinalizers();

            Assert.Equal(0L, OptimumGlReclaimQueue.PendingTotalCount);
            Assert.Equal(0L, OptimumGlReclaimQueue.TotalReclaimedCount);
            Assert.False(GL.IsVertexArray(cleanVao));
            Assert.False(GL.IsBuffer(cleanVbo));
            Assert.False(GL.IsTexture(cleanTex));

            // 2. Leaked VAO (including optional ARB_buffer_storage persistent VBO), leaked shared-EBO VAO,
            // and leaked LoadedTexture across capi.IsShuttingDown = true must reclaim cleanly while keeping live EBO.
            int leakedVao = GL.GenVertexArray();
            int leakedVbo = GL.GenBuffer();
            GL.BindVertexArray(leakedVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, leakedVbo);
            bool persistentAllocated = TryAllocatePersistentBuffer(64, out nint persistentPtr);
            if (!persistentAllocated)
            {
                GL.BufferData(BufferTarget.ArrayBuffer, 64, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

            int staleEbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, staleEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 32, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            GL.DeleteBuffer(staleEbo);

            // Reallocate EBO (often reuses the deleted GL integer ID immediately).
            int liveSharedEbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, liveSharedEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            sharedEboField.SetValue(null, liveSharedEbo);

            int leakedSsboVao = GL.GenVertexArray();
            int leakedSsboVbo = GL.GenBuffer();
            GL.BindVertexArray(leakedSsboVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, leakedSsboVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

            int leakedTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, leakedTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            var exitedWorldHarness = new TestClientHarness { ShuttingDown = true };
            AllocateLeakedObjects(
                vaoType,
                exitedWorldHarness.Api,
                leakedVao,
                leakedVbo,
                persistentAllocated,
                persistentPtr,
                leakedSsboVao,
                leakedSsboVbo,
                staleEbo,
                leakedTex);

            RunGcAndWaitForFinalizers();

            Assert.True(OptimumGlReclaimQueue.PendingTotalCount >= 5);
            int drained = (int)drainMethod.Invoke(platform, null)!;
            Assert.True(drained >= 5);
            Assert.Equal(0L, OptimumGlReclaimQueue.PendingTotalCount);

            Assert.False(GL.IsVertexArray(leakedVao));
            Assert.False(GL.IsBuffer(leakedVbo));
            Assert.False(GL.IsVertexArray(leakedSsboVao));
            Assert.False(GL.IsBuffer(leakedSsboVbo));
            Assert.False(GL.IsTexture(leakedTex));
            Assert.True(GL.IsBuffer(liveSharedEbo));
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());

            sharedEboField.SetValue(null, 0);
            GL.DeleteBuffer(liveSharedEbo);
        }
        finally
        {
            OptimumConfig.GlFinalizerReclaimEnabled = origEnabled;
            OptimumGlReclaimQueue.ResetForTests();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndDispose(Type vaoType, ICoreClientAPI api, int vaoId, int vboId, int texId)
    {
        object vao = Activator.CreateInstance(vaoType)!;
        vaoType.GetField("VaoId")!.SetValue(vao, vaoId);
        vaoType.GetField("xyzVboId")!.SetValue(vao, vboId);
        ((IDisposable)vao).Dispose();

        var tex = new LoadedTexture(api, texId, 4, 4);
        tex.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateLeakedObjects(
        Type vaoType,
        ICoreClientAPI api,
        int leakedVao,
        int leakedVbo,
        bool persistentAllocated,
        nint persistentPtr,
        int leakedSsboVao,
        int leakedSsboVbo,
        int staleEboId,
        int leakedTex)
    {
        object vao = Activator.CreateInstance(vaoType)!;
        vaoType.GetField("VaoId")!.SetValue(vao, leakedVao);
        vaoType.GetField("xyzVboId")!.SetValue(vao, leakedVbo);
        vaoType.GetField("Persistent")!.SetValue(vao, persistentAllocated);
        vaoType.GetField("xyzPtr")!.SetValue(vao, persistentPtr);

        object ssboVao = Activator.CreateInstance(vaoType)!;
        vaoType.GetField("VaoId")!.SetValue(ssboVao, leakedSsboVao);
        vaoType.GetField("xyzVboId")!.SetValue(ssboVao, leakedSsboVbo);
        vaoType.GetField("vboIdIndex")!.SetValue(ssboVao, staleEboId);
        vaoType.GetField("sharedIndexBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ssboVao, true);

        _ = new LoadedTexture(api, leakedTex, 4, 4);
    }

    private static bool TryAllocatePersistentBuffer(int sizeBytes, out nint ptr)
    {
        ptr = 0;
        try
        {
            const BufferStorageFlags storageFlags = (BufferStorageFlags)450;
            const MapBufferAccessMask mapFlags = (MapBufferAccessMask)194;
            GL.BufferStorage(BufferTarget.ArrayBuffer, sizeBytes, IntPtr.Zero, storageFlags);
            if (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError)
            {
                while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
                return false;
            }
            ptr = GL.MapBufferRange(BufferTarget.ArrayBuffer, IntPtr.Zero, sizeBytes, mapFlags);
            if (ptr == 0 || GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError)
            {
                while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
                return false;
            }
            return true;
        }
        catch
        {
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
            return false;
        }
    }

    private static void RunGcAndWaitForFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
