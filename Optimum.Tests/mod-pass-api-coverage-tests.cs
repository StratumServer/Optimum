using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 5: the mod pass and motion-writer API. The contract types are data
/// holders in Optimum.Api.Contracts (no lib types); registration validates against the contract,
/// copies the declaration, stores it per mod and drops it when the client leaves the world; only the
/// Vulkan platform reads it, from the end of the stage bracket, with the mod-hosted pass flags. The
/// GPU side (slot, attachment states, validation) is ModPassHostingTests in Optimum.Render.Vulkan.Tests.
/// </summary>
[Collection("OptimumModPasses")]
public class ModPassApiCoverageTests
{
    private const string ModId = "coverage-mod";

    public class ClientApiStub : DispatchProxy
    {
        public readonly List<Action> Handlers = new();
        private IClientEventAPI? events;
        private ClientApiStub? owner;

        public static (ICoreClientAPI Api, ClientApiStub Stub) Create()
        {
            ICoreClientAPI api = Create<ICoreClientAPI, ClientApiStub>();
            var stub = (ClientApiStub)(object)api;
            IClientEventAPI events = Create<IClientEventAPI, ClientApiStub>();
            ((ClientApiStub)(object)events).owner = stub;
            stub.events = events;
            return (api, stub);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
            case "get_Event": return events;
            case "add_LeaveWorld": (owner ?? this).Handlers.Add((Action)args![0]!); return null;
            case "remove_LeaveWorld": (owner ?? this).Handlers.Remove((Action)args![0]!); return null;
            }
            Type type = targetMethod.ReturnType;
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static OptimumPassDecl ValidPass(string name = "tint") => new()
    {
        Name = name,
        Slot = EnumOptimumPass.AfterOIT,
        Reads = new[] { EnumOptimumAttachment.PrimaryGlow },
        Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryDepth },
        Draw = static _ => { },
        MotionWriter = new OptimumMotionWriterDecl { Name = name, Mode = EnumOptimumMotionWrite.WithColor },
    };

    private static string Read(string path) => File.ReadAllText(PatchReader.FindRepositoryFile(path));

    [Fact]
    public void TheSlotsMirrorTheRenderStages()
    {
        foreach (EnumRenderStage stage in Enum.GetValues<EnumRenderStage>())
            Assert.Equal(stage.ToString(), ((EnumOptimumPass)(int)stage).ToString());
        Assert.Equal(Enum.GetValues<EnumRenderStage>().Length, Enum.GetValues<EnumOptimumPass>().Length);
    }

    [Fact]
    public void TheContractIsDataHoldersWithNoLibTypes()
    {
        Assembly contracts = typeof(OptimumPassDecl).Assembly;
        Assert.Equal("Optimum.Api.Contracts", contracts.GetName().Name);
        foreach (AssemblyName reference in contracts.GetReferencedAssemblies())
            Assert.NotEqual("VintagestoryLib", reference.Name);

        foreach (FieldInfo field in typeof(OptimumPassDecl).GetFields(BindingFlags.Public | BindingFlags.Instance))
            Assert.False(field.IsInitOnly, field.Name + " is a plain settable field");
        Assert.Empty(typeof(OptimumPassDecl).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(OptimumMotionWriterDecl).GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ContractFileIsWiredIntoContractsAndRemovedFromTheFork()
    {
        Assert.Contains(@"..\sources\VintagestoryApi\Client\Render\OptimumModPasses.cs",
            Read("optimum-api-contracts/optimum-api-contracts.csproj"));
        Assert.Contains(@"<Compile Remove=""Client\Render\OptimumModPasses.cs"" />", Read("VintagestoryApi/VintagestoryAPI.csproj"));
        Assert.Contains(@"<Compile Remove=""Client\Render\OptimumModPasses.cs"" />", Read("sources/VintagestoryApi/VintagestoryAPI.csproj"));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Client/Render/OptimumModPasses.cs")));
    }

    [Theory]
    [InlineData("motion-write", "PrimaryMotion directly")]
    [InlineData("feedback", "feedback loop")]
    [InlineData("two-targets", "more than one target")]
    [InlineData("default-in-world", "only AfterBlit, Ortho and Done")]
    [InlineData("primary-after-blit", "after the scene was blitted")]
    [InlineData("motion-in-ortho", "motion window exists only in Opaque and AfterOIT")]
    [InlineData("shadow", "shadow stages")]
    [InlineData("read-only-write", "which is read only")]
    [InlineData("no-draw", "no draw callback")]
    [InlineData("nothing", "writes nothing")]
    public void RegistrationEnforcesTheContract(string breach, string expected)
    {
        OptimumPassDecl decl = ValidPass();
        switch (breach)
        {
        case "motion-write": decl.Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryMotion }; break;
        case "feedback": decl.Reads = new[] { EnumOptimumAttachment.PrimaryColor }; break;
        case "two-targets": decl.Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.TransparentAccumulation }; decl.MotionWriter = null; break;
        case "default-in-world": decl.Writes = new[] { EnumOptimumAttachment.DefaultColor }; decl.Reads = Array.Empty<EnumOptimumAttachment>(); decl.MotionWriter = null; break;
        case "primary-after-blit": decl.Slot = EnumOptimumPass.AfterBlit; decl.MotionWriter = null; break;
        case "motion-in-ortho": decl.Slot = EnumOptimumPass.AfterPostProcessing; break;
        case "shadow": decl.Slot = EnumOptimumPass.ShadowNear; break;
        case "read-only-write": decl.Writes = new[] { EnumOptimumAttachment.GodRays }; decl.MotionWriter = null; break;
        case "no-draw": decl.Draw = null; break;
        case "nothing": decl.Writes = Array.Empty<EnumOptimumAttachment>(); decl.MotionWriter = null; break;
        }

        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        Assert.False(OptimumModPasses.Register(api, ModId, decl, out string reason));
        Assert.Contains(expected, reason);
        Assert.Empty(OptimumModPasses.ForSlot(decl.Slot));
    }

    [Fact]
    public void AValidDeclarationIsCopiedStoredPerModAndReplacedByName()
    {
        (ICoreClientAPI api, ClientApiStub stub) = ClientApiStub.Create();
        try
        {
            OptimumPassDecl decl = ValidPass();
            Assert.True(OptimumPassContract.Validate(decl, out string? reason), reason);
            Assert.Equal(EnumOptimumTarget.Primary, OptimumPassContract.TargetOf(decl));
            Assert.True(OptimumModPasses.Register(api, ModId, decl, out reason), reason);
            Assert.True(OptimumModPasses.Register(api, "other-mod", ValidPass("other"), out reason), reason);

            OptimumPassRegistration[] slot = OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT);
            Assert.Equal(2, slot.Length);
            Assert.Equal(ModId, slot[0].ModId);
            Assert.NotSame(decl, slot[0].Decl);
            decl.Writes[0] = EnumOptimumAttachment.PrimaryGlow;
            Assert.Equal(EnumOptimumAttachment.PrimaryColor, OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT)[0].Decl.Writes[0]);
            Assert.Same(slot, OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));

            long version = OptimumModPasses.Version;
            Assert.True(OptimumModPasses.Register(api, ModId, ValidPass(), out reason), reason);
            Assert.Equal(2, OptimumModPasses.PassCount);
            Assert.True(OptimumModPasses.Version > version);
            // One LeaveWorld subscription per mod, not per registration.
            Assert.Equal(2, stub.Handlers.Count);
        }
        finally
        {
            OptimumModPasses.UnregisterMod(ModId);
            OptimumModPasses.UnregisterMod("other-mod");
        }
        Assert.Equal(0, OptimumModPasses.PassCount);
    }

    [Fact]
    public void LeavingTheWorldClearsWhatTheModRegistered()
    {
        (ICoreClientAPI api, ClientApiStub stub) = ClientApiStub.Create();
        var writer = new OptimumMotionWriterDecl { Name = "renderer" };
        Assert.True(OptimumModPasses.Register(api, ModId, ValidPass(), out _));
        Assert.True(OptimumModPasses.RegisterMotionWriter(api, ModId, writer, out _));
        Assert.True(OptimumModPasses.IsRegisteredWriter(writer));
        Assert.Single(stub.Handlers);

        foreach (Action handler in stub.Handlers.ToArray()) handler();

        Assert.Empty(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));
        Assert.False(OptimumModPasses.IsRegisteredWriter(writer));
        Assert.Empty(stub.Handlers);
        Assert.DoesNotContain(ModId, OptimumModPasses.RegisteredMods());
    }

    [Fact]
    public void MotionWritersOpenOnlyThroughAnInstalledHookAndOnlyWhenRegistered()
    {
        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        var writer = new OptimumMotionWriterDecl { Name = "renderer", Mode = EnumOptimumMotionWrite.MotionOnly };
        var calls = new List<string>();
        try
        {
            Assert.True(OptimumModPasses.RegisterMotionWriter(api, ModId, writer, out _));
            Assert.False(OptimumModPasses.BeginMotionWriter(writer), "no hook: OpenGL ignores writers");
            OptimumModPasses.EndMotionWriter();

            OptimumModPasses.MotionBeginHook = w => { calls.Add("begin " + w.Name); return true; };
            OptimumModPasses.MotionEndHook = () => calls.Add("end");
            Assert.False(OptimumModPasses.BeginMotionWriter(new OptimumMotionWriterDecl { Name = "stranger" }));
            Assert.True(OptimumModPasses.BeginMotionWriter(writer));
            OptimumModPasses.EndMotionWriter();
            Assert.Equal(new[] { "begin renderer", "end" }, calls);
        }
        finally
        {
            OptimumModPasses.MotionBeginHook = null;
            OptimumModPasses.MotionEndHook = null;
            OptimumModPasses.UnregisterMod(ModId);
        }
    }

    [Fact]
    public void TheModSystemEntryPointsFallBackToTheAssemblyName()
    {
        var system = new CoverageModSystem();
        Assert.Equal(typeof(CoverageModSystem).Assembly.GetName().Name, OptimumModRenderExtensions.OptimumModId(system));
    }

    private sealed class CoverageModSystem : Vintagestory.API.Common.ModSystem
    {
    }

    [Fact]
    public void OnlyTheVulkanPlatformHostsModPassesFromTheStageBracket()
    {
        string stages = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs");
        int run = stages.IndexOf("RunModPasses(stage);", StringComparison.Ordinal);
        Assert.True(run > 0, "EndRenderStage runs the slot's mod passes");
        Assert.True(run < stages.IndexOf("InRenderStage = false;", StringComparison.Ordinal), "mod passes run inside the stage");
        Assert.True(run < stages.IndexOf("RenderStageListener?.OnEndRenderStage(stage);", StringComparison.Ordinal),
            "mod passes run before the stage's pass ends");

        string host = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.ModPasses.cs");
        Assert.Contains("internal const PassFlags ModPassFlags = PassFlags.OpenSampling | PassFlags.AllowSplit;", host);
        Assert.Contains("Flags = ModPassFlags,", host);
        Assert.Contains("BeginMotionOnlyWrite() : BeginMotionWrite()", host);
        Assert.Contains("if (motion) EndMotionWrite();", host);
        Assert.Contains("statedPass = plan.Declaration;", host);
        Assert.Contains("statedPass = null;", host);
        Assert.Contains("out string? refusal, declared);",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs"));

        string platform = VulkanPlatformSource.Read();
        Assert.Contains("InstallModPassHooks();", platform);
        Assert.Contains("RemoveModPassHooks();", platform);

        string gl = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("OptimumModPasses", gl);
        Assert.DoesNotContain("OptimumPassDecl", gl);
    }

    [Fact]
    public void TheModderDocumentationAndTheFixtureShip()
    {
        string doc = Read("docs/vulkan-mod-support.md");
        foreach (string term in new[] { "OptimumPassDecl", "EnumOptimumPass", "OptimumMotionWriterDecl", "TAAMOTIONLOCATION",
                     "BeginMotionWriter", "routes to OpenGL", "rewriter", "shaderincludes", "LeaveWorld" })
            Assert.Contains(term, doc);
        Assert.Contains("!docs/vulkan-mod-support.md", Read(".gitignore"));
        Assert.Contains("RegisterOptimumPass", Read("Optimum.Render.Vulkan.Tests/Fixtures/ModPassFixture/ModPassFixtureSystem.cs"));
    }
}
