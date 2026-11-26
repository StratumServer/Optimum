#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Mono.Cecil;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;
using RefTypeAttributes = System.Reflection.TypeAttributes;
using RefFieldAttributes = System.Reflection.FieldAttributes;
using RefMethodAttributes = System.Reflection.MethodAttributes;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class CrucibleInFirepitRendererTests
{
    private static string FindDll(string relativePath)
    {
        string nearTests = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(relativePath));
        if (File.Exists(nearTests))
        {
            return nearTests;
        }
        return PatchReader.FindRepositoryFile(relativePath);
    }

    [Fact]
    public void BlockSmeltingContainer_RuntimePatchAndIl_InitializesTempAndAvoidsPerFrameAllocations()
    {
        string runtimePatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockSmeltingContainer.cs.patch"));
        Assert.Contains("renderer.OnUpdate(firepit.InputStackTemp);", runtimePatch);
        Assert.DoesNotMatch(new Regex(@"^\+.*=>", RegexOptions.Multiline), runtimePatch);

        string survivalDll = FindDll("bin/Release/net10.0/VSSurvivalMod.dll");
        Assert.True(File.Exists(survivalDll), $"Missing VSSurvivalMod.dll at {survivalDll}");

        string? vanillaDonor = null;
        string? compiledDonor = null;
        try
        {
            vanillaDonor = PatchReader.FindRepositoryFile(".vanilla/win-x64/runtime-donors/Mods/VSSurvivalMod.dll");
            compiledDonor = PatchReader.FindRepositoryFile(".build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll");
        }
        catch (FileNotFoundException)
        {
        }

        string tempOut = Path.Combine(Path.GetTempPath(), "optimum-crucible-donor-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            string targetDll = survivalDll;
            if (vanillaDonor != null && compiledDonor != null && File.Exists(vanillaDonor) && File.Exists(compiledDonor))
            {
                Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaDonor, compiledDonor, tempOut));
                Assert.True(File.Exists(tempOut));
                targetDll = tempOut;
            }

            using var asm = AssemblyDefinition.ReadAssembly(targetDll);
            TypeDefinition rendererType = asm.MainModule.GetType("Vintagestory.GameContent.CrucibleInFirepitRenderer");
            Assert.NotNull(rendererType);

            MethodDefinition renderMethod = rendererType.Methods.Single(m => m.Name == "OnRenderFrame");
            var instructions = renderMethod.Body.Instructions;

            Assert.DoesNotContain(instructions, i => i.OpCode == CecilOpCodes.Newobj || i.OpCode == CecilOpCodes.Newarr);
            Assert.Contains(instructions, i => i.Operand is MethodReference mr && mr.Name == "set_TempGlowMode");
            Assert.Contains(instructions, i => i.Operand is MethodReference mr && mr.Name == "set_AverageColor");

            if (targetDll == tempOut)
            {
                TypeDefinition smeltingType = asm.MainModule.GetType("Vintagestory.GameContent.BlockSmeltingContainer");
                Assert.NotNull(smeltingType);
                MethodDefinition getRenderer = smeltingType.Methods.Single(m => m.Name == "GetRendererWhenInFirepit");
                Assert.Contains(getRenderer.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "OnUpdate");
            }
        }
        finally
        {
            try { File.Delete(tempOut); } catch { }
        }
    }

    [Fact]
    public void CrucibleInFirepitRenderer_GlowCurveLeakIsolationAndCleanup_MatchVanilla()
    {
        WithLoadedRendererType(rendererType =>
        {
            int expectedAvgRgba = ColorUtil.ToRgba(255, 180, 140, 100);
            var expectedAvgVec = ColorUtil.ToRGBAVec4f(expectedAvgRgba);

            var harness = new CrucibleRenderHarness(averageColorRgba: expectedAvgRgba);
            var crucibleBlock = CreateCrucibleBlock(tempGlowMode: 1, textureSubId: 42);

            object renderer = Activator.CreateInstance(
                rendererType,
                harness.CreateClientApi(),
                crucibleBlock,
                new BlockPos(5, 64, 7, 0))!;
            MethodInfo onUpdate = rendererType.GetMethod("OnUpdate")!;
            MethodInfo onRenderFrame = rendererType.GetMethod("OnRenderFrame")!;
            MethodInfo dispose = rendererType.GetMethod("Dispose")!;

            try
            {
                // Cold and edge temperatures: ExtraGlow=0 and TempGlowMode=0 (prevents 0/0 in standard.fsh)
                foreach (float coldTemp in new[] { -10f, 0f, 300f, 500f, 502f, float.NaN })
                {
                    harness.PoisonLeakedShaderState();
                    onUpdate.Invoke(renderer, new object[] { coldTemp });
                    onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });

                    Assert.Equal(0, harness.DrawExtraGlow);
                    Assert.Equal(0, harness.DrawTempGlowMode);
                    AssertVec4Equal(expectedAvgVec, harness.DrawAverageColor);
                    if (float.IsNaN(coldTemp) || coldTemp <= 500f)
                    {
                        Assert.Equal(0f, harness.DrawRgbaGlowIn.R, 4);
                        Assert.Equal(0f, harness.DrawRgbaGlowIn.G, 4);
                        Assert.Equal(0f, harness.DrawRgbaGlowIn.B, 4);
                    }
                    Assert.Equal(0f, harness.DrawRgbaGlowIn.A, 4);
                    Assert.Equal(0, harness.StopTempGlowMode);
                    Assert.Equal(0, harness.StopExtraGlow);
                }

                // 950C heating: vanilla (temp - 500) / 3 = 150, TempGlowMode = 1, unaffected by leaked state
                harness.PoisonLeakedShaderState();
                onUpdate.Invoke(renderer, new object[] { 950f });
                onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });
                int drawGlowWithLeak = harness.DrawExtraGlow;
                int drawModeWithLeak = harness.DrawTempGlowMode;
                Vec4f drawAvgWithLeak = harness.DrawAverageColor.Clone();

                harness.ClearShaderState();
                onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });

                Assert.Equal(150, drawGlowWithLeak);
                Assert.Equal(1, drawModeWithLeak);
                AssertVec4Equal(expectedAvgVec, drawAvgWithLeak);
                Assert.Equal(drawGlowWithLeak, harness.DrawExtraGlow);
                Assert.Equal(drawModeWithLeak, harness.DrawTempGlowMode);
                AssertVec4Equal(drawAvgWithLeak, harness.DrawAverageColor);
                Assert.Equal(0, harness.StopTempGlowMode);
                Assert.Equal(0, harness.StopExtraGlow);

                // 1084C copper melting point: (1084 - 500) / 3 = 194 (not prematurely saturated at 255)
                harness.PoisonLeakedShaderState();
                onUpdate.Invoke(renderer, new object[] { 1084f });
                onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });
                Assert.Equal(194, harness.DrawExtraGlow);
                Assert.Equal(1, harness.DrawTempGlowMode);
                Assert.Equal(194f / 255f, harness.DrawRgbaGlowIn.A, 4);
                Assert.Equal(0, harness.StopTempGlowMode);
                Assert.Equal(0, harness.StopExtraGlow);

                // Very hot >= 1265C clamps cleanly at 255
                harness.PoisonLeakedShaderState();
                onUpdate.Invoke(renderer, new object[] { 1500f });
                onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });
                Assert.Equal(255, harness.DrawExtraGlow);
                Assert.Equal(1, harness.DrawTempGlowMode);
                Assert.Equal(1f, harness.DrawRgbaGlowIn.A, 4);
                Assert.Equal(0, harness.StopTempGlowMode);
                Assert.Equal(0, harness.StopExtraGlow);
            }
            finally
            {
                dispose.Invoke(renderer, null);
            }
        });
    }

    [Fact]
    public void CrucibleInFirepitRenderer_MissingAttributesOrTextures_UsesSafeFallbacksAndClearsLeakedMode()
    {
        WithLoadedRendererType(rendererType =>
        {
            var harness = new CrucibleRenderHarness(averageColorRgba: ColorUtil.ToRgba(255, 50, 60, 70));
            var minimalBlock = new Block { Code = new AssetLocation("mod", "custom-crucible") };

            object renderer = Activator.CreateInstance(
                rendererType,
                harness.CreateClientApi(),
                minimalBlock,
                new BlockPos(2, 64, 3, 0))!;
            MethodInfo onUpdate = rendererType.GetMethod("OnUpdate")!;
            MethodInfo onRenderFrame = rendererType.GetMethod("OnRenderFrame")!;
            MethodInfo dispose = rendererType.GetMethod("Dispose")!;

            try
            {
                harness.PoisonLeakedShaderState();
                onUpdate.Invoke(renderer, new object[] { 950f });
                onRenderFrame.Invoke(renderer, new object[] { 0.016f, EnumRenderStage.Opaque });

                Assert.Equal(150, harness.DrawExtraGlow);
                Assert.Equal(0, harness.DrawTempGlowMode);
                AssertVec4Equal(new Vec4f(1f, 1f, 1f, 1f), harness.DrawAverageColor);
                Assert.Equal(0, harness.StopTempGlowMode);
                Assert.Equal(0, harness.StopExtraGlow);
            }
            finally
            {
                dispose.Invoke(renderer, null);
            }
        });
    }

    private static Block CreateCrucibleBlock(int tempGlowMode, int textureSubId)
    {
        var tex = new CompositeTexture
        {
            Baked = new BakedCompositeTexture { TextureSubId = textureSubId }
        };
        return new Block
        {
            Code = new AssetLocation("game", "crucible-tan-fired"),
            Attributes = new JsonObject(JToken.Parse($"{{\"tempGlowMode\": {tempGlowMode}}}")),
            Textures = new Dictionary<string, CompositeTexture> { ["all"] = tex },
        };
    }

    private static void AssertVec4Equal(Vec4f expected, Vec4f actual)
    {
        Assert.Equal(expected.R, actual.R, 4);
        Assert.Equal(expected.G, actual.G, 4);
        Assert.Equal(expected.B, actual.B, 4);
        Assert.Equal(expected.A, actual.A, 4);
    }

    private static void WithLoadedRendererType(Action<Type> action)
    {
        string survivalDll = FindDll("bin/Release/net10.0/VSSurvivalMod.dll");
        Assert.True(File.Exists(survivalDll), $"Missing VSSurvivalMod.dll at {survivalDll}");
        string survivalDir = Path.GetDirectoryName(survivalDll)!;

        var alc = new AssemblyLoadContext("CrucibleRendererTest", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            if (name.Name == "VintagestoryAPI") return typeof(ICoreClientAPI).Assembly;
            if (name.Name == "Optimum.Api.Contracts") return typeof(Vintagestory.API.Config.OptimumConfig).Assembly;
            if (name.Name == "VSEssentials") return typeof(Vintagestory.GameContent.InWorldContainer).Assembly;
            string candidate = Path.Combine(survivalDir, name.Name + ".dll");
            if (File.Exists(candidate)) return alc.LoadFromAssemblyPath(candidate);
            string baseCandidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(baseCandidate)) return alc.LoadFromAssemblyPath(baseCandidate);
            return null;
        };

        try
        {
            Assembly modAsm = alc.LoadFromAssemblyPath(survivalDll);
            Type rendererType = modAsm.GetType("Vintagestory.GameContent.CrucibleInFirepitRenderer", throwOnError: true)!;
            action(rendererType);
        }
        finally
        {
            alc.Unload();
        }
    }

    private sealed class CrucibleRenderHarness
    {
        private readonly int averageColorRgba;
        private readonly MultiTextureMeshRef fakeMeshRef = new MultiTextureMeshRef(Array.Empty<MeshRef>(), Array.Empty<int>());
        private readonly float[] identityMat = Mat4f.Create();
        private readonly Vec3f ambient = new Vec3f(0.2f, 0.2f, 0.2f);
        private readonly Vec4f fog = new Vec4f(0.5f, 0.5f, 0.5f, 1f);
        private readonly Vec4f light = new Vec4f(0.8f, 0.7f, 0.6f, 1f);
        private readonly EntityPlayer fakePlayerEntity = new EntityPlayer();

        private int currentTempGlowMode;
        private int currentExtraGlow;
        private Vec4f currentAverageColor = new Vec4f();
        private Vec4f currentRgbaGlowIn = new Vec4f();

        public int DrawTempGlowMode { get; private set; }
        public int DrawExtraGlow { get; private set; }
        public Vec4f DrawAverageColor { get; private set; } = new Vec4f();
        public Vec4f DrawRgbaGlowIn { get; private set; } = new Vec4f();
        public int StopTempGlowMode { get; private set; } = -1;
        public int StopExtraGlow { get; private set; } = -1;

        public CrucibleRenderHarness(int averageColorRgba)
        {
            this.averageColorRgba = averageColorRgba;
        }

        public void PoisonLeakedShaderState()
        {
            currentTempGlowMode = 1;
            currentExtraGlow = 255;
            currentAverageColor = new Vec4f(0f, 0f, 0f, 0f);
            currentRgbaGlowIn = new Vec4f(1f, 0f, 0f, 1f);
        }

        public void ClearShaderState()
        {
            currentTempGlowMode = 0;
            currentExtraGlow = 0;
            currentAverageColor = new Vec4f(0f, 0f, 0f, 0f);
            currentRgbaGlowIn = new Vec4f(0f, 0f, 0f, 0f);
        }

        public ICoreClientAPI CreateClientApi()
        {
            var shader = CreateProxy<IStandardShaderProgram>((m, args) =>
            {
                switch (m.Name)
                {
                    case "set_TempGlowMode":
                        currentTempGlowMode = (int)args![0]!;
                        return null;
                    case "set_ExtraGlow":
                        currentExtraGlow = (int)args![0]!;
                        return null;
                    case "set_AverageColor":
                        currentAverageColor = ((Vec4f)args![0]!).Clone();
                        return null;
                    case "set_RgbaGlowIn":
                        currentRgbaGlowIn = ((Vec4f)args![0]!).Clone();
                        return null;
                    case "Stop":
                        StopTempGlowMode = currentTempGlowMode;
                        StopExtraGlow = currentExtraGlow;
                        return null;
                    default:
                        return DefaultValue(m.ReturnType);
                }
            });

            var renderApi = CreateProxy<IRenderAPI>((m, args) =>
            {
                switch (m.Name)
                {
                    case "UploadMultiTextureMesh":
                        return fakeMeshRef;
                    case "PreparedStandardShader":
                        // Match RenderAPIGame.PreparedStandardShader: resets ExtraGlow, does NOT reset TempGlowMode/AverageColor.
                        currentExtraGlow = 0;
                        return shader;
                    case "RenderMultiTextureMesh":
                        DrawTempGlowMode = currentTempGlowMode;
                        DrawExtraGlow = currentExtraGlow;
                        DrawAverageColor = currentAverageColor.Clone();
                        DrawRgbaGlowIn = currentRgbaGlowIn.Clone();
                        return null;
                    case "get_AmbientColor":
                        return ambient;
                    case "get_FogColor":
                        return fog;
                    case "get_CameraMatrixOriginf":
                    case "get_CurrentProjectionMatrix":
                        return identityMat;
                    default:
                        return DefaultValue(m.ReturnType);
                }
            });

            var tesselator = CreateProxy<ITesselatorAPI>((m, args) =>
            {
                if (m.Name == "TesselateBlock" && args != null && args.Length >= 2)
                {
                    args[1] = new MeshData(4, 6);
                }
                return DefaultValue(m.ReturnType);
            });

            var atlas = CreateProxy<IBlockTextureAtlasAPI>((m, _) => m.Name switch
            {
                "GetAverageColor" => averageColorRgba,
                _ => DefaultValue(m.ReturnType),
            });

            var blockAccessor = CreateProxy<IBlockAccessor>((m, _) => m.Name switch
            {
                "GetLightRGBs" => light,
                _ => DefaultValue(m.ReturnType),
            });

            var (player, entityField) = ClientPlayerStubFactory.Create();
            entityField.SetValue(player, fakePlayerEntity);

            var world = CreateProxy<IClientWorldAccessor>((m, _) => m.Name switch
            {
                "get_Player" => player,
                "get_BlockAccessor" => blockAccessor,
                _ => DefaultValue(m.ReturnType),
            });

            return CreateProxy<ICoreClientAPI>((m, _) => m.Name switch
            {
                "get_Render" => renderApi,
                "get_Tesselator" => tesselator,
                "get_BlockTextureAtlas" => atlas,
                "get_World" => world,
                _ => DefaultValue(m.ReturnType),
            });
        }

        private static class ClientPlayerStubFactory
        {
            private static readonly (Type StubType, FieldInfo EntityField) Compiled = BuildStubType();

            public static (IClientPlayer Instance, FieldInfo EntityField) Create()
            {
                var instance = (IClientPlayer)Activator.CreateInstance(Compiled.StubType)!;
                return (instance, Compiled.EntityField);
            }

            private static (Type StubType, FieldInfo EntityField) BuildStubType()
            {
                AssemblyBuilder asm = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName("CrucibleClientPlayerStub"),
                    AssemblyBuilderAccess.Run);
                ModuleBuilder mod = asm.DefineDynamicModule("Main");

                TypeBuilder attrTb = mod.DefineType(
                    "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
                    RefTypeAttributes.Public | RefTypeAttributes.Sealed | RefTypeAttributes.Class,
                    typeof(Attribute));
                FieldBuilder attrField = attrTb.DefineField("_assemblyName", typeof(string), RefFieldAttributes.Private);
                ConstructorBuilder attrCtor = attrTb.DefineConstructor(
                    RefMethodAttributes.Public,
                    CallingConventions.Standard,
                    new[] { typeof(string) });
                ILGenerator attrIl = attrCtor.GetILGenerator();
                attrIl.Emit(OpCodes.Ldarg_0);
                attrIl.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
                attrIl.Emit(OpCodes.Ldarg_0);
                attrIl.Emit(OpCodes.Ldarg_1);
                attrIl.Emit(OpCodes.Stfld, attrField);
                attrIl.Emit(OpCodes.Ret);
                Type attrType = attrTb.CreateType()!;

                asm.SetCustomAttribute(new CustomAttributeBuilder(
                    attrType.GetConstructor(new[] { typeof(string) })!,
                    new object[] { typeof(IClientPlayer).Assembly.GetName().Name! }));

                TypeBuilder tb = mod.DefineType(
                    "StubClientPlayer",
                    RefTypeAttributes.Public | RefTypeAttributes.Sealed | RefTypeAttributes.Class);
                tb.AddInterfaceImplementation(typeof(IClientPlayer));
                FieldBuilder entityField = tb.DefineField("_entity", typeof(EntityPlayer), RefFieldAttributes.Public);

                foreach (Type iface in new[] { typeof(IClientPlayer), typeof(IPlayer) })
                {
                    foreach (MethodInfo method in iface.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        Type[] paramTypes = Array.ConvertAll(method.GetParameters(), p => p.ParameterType);
                        MethodBuilder mb = tb.DefineMethod(
                            $"{iface.FullName}.{method.Name}",
                            RefMethodAttributes.Private | RefMethodAttributes.Final | RefMethodAttributes.HideBySig | RefMethodAttributes.NewSlot | RefMethodAttributes.Virtual,
                            method.ReturnType,
                            paramTypes);
                        ILGenerator il = mb.GetILGenerator();
                        if (method.Name == "get_Entity" && paramTypes.Length == 0)
                        {
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, entityField);
                        }
                        else if (method.ReturnType != typeof(void))
                        {
                            if (method.ReturnType.IsValueType)
                            {
                                LocalBuilder loc = il.DeclareLocal(method.ReturnType);
                                il.Emit(OpCodes.Ldloca_S, loc);
                                il.Emit(OpCodes.Initobj, method.ReturnType);
                                il.Emit(OpCodes.Ldloc_S, loc);
                            }
                            else
                            {
                                il.Emit(OpCodes.Ldnull);
                            }
                        }
                        il.Emit(OpCodes.Ret);
                        tb.DefineMethodOverride(mb, method);
                    }
                }

                Type stubType = tb.CreateType()!;
                return (stubType, stubType.GetField("_entity", BindingFlags.Public | BindingFlags.Instance)!);
            }
        }

        private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, LocalDispatchProxy>() as LocalDispatchProxy
                ?? throw new InvalidOperationException();
            proxy.Handler = handler;
            return (T)(object)proxy;
        }

        private static object? DefaultValue(Type t)
        {
            if (t == typeof(void)) return null;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }

    public class LocalDispatchProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler?.Invoke(targetMethod!, args);
        }
    }
}
