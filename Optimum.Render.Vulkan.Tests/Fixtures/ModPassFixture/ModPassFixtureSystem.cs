// Source: Optimum.Render.Vulkan.Tests/Fixtures/ModPassFixture/ModPassFixtureSystem.cs
namespace Optimum.Render.Vulkan.Tests.Fixtures.ModPassFixture
{
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>
/// A fixture mod that follows docs/vulkan.md step by step: one declared pass and one
/// motion writer, registered in StartClientSide through the client-API extensions and removed in
/// Dispose. The pass tints Primary's colour from its glow after the AfterOIT renderers and writes
/// motion for its draw, so it reads one attachment of its own target (which therefore leaves the
/// rendering scope), writes colour and depth, and opens the motion window. The writer is what a
/// RegisterRenderer renderer of the same mod would open around its own draws.
///
/// What the draw does is supplied by the host (<see cref="Drawer" />): in a shipped mod it would draw
/// through capi.Render with its own shader; the GPU test draws the same shape through the platform.
/// </summary>
public sealed class ModPassFixtureSystem : ModSystem
{
    public const string PassName = "glow-tint";
    public const string WriterName = "fixture-renderer";

    public OptimumPassDecl? Pass { get; private set; }

    public OptimumMotionWriterDecl? Writer { get; private set; }

    /// <summary>The draw the pass performs; null draws nothing.</summary>
    public Action<OptimumPassDecl>? Drawer;

    /// <summary>The id registrations are stored under (the mod id, or this assembly's name when loaded outside the mod loader).</summary>
    public string ModId => OptimumModRenderExtensions.OptimumModId(this);

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        Writer = new OptimumMotionWriterDecl { Name = WriterName, Mode = EnumOptimumMotionWrite.WithColor };
        if (!api.RegisterOptimumMotionWriter(this, Writer, out string reason))
            throw new InvalidOperationException(reason);

        Pass = new OptimumPassDecl
        {
            Name = PassName,
            Slot = EnumOptimumPass.AfterOIT,
            Reads = new[] { EnumOptimumAttachment.PrimaryGlow },
            Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryDepth },
            Draw = OnDraw,
            MotionWriter = new OptimumMotionWriterDecl { Name = PassName, Mode = EnumOptimumMotionWrite.WithColor },
        };
        if (!api.RegisterOptimumPass(this, Pass, out reason))
            throw new InvalidOperationException(reason);
    }

    private void OnDraw(OptimumPassDecl pass) => Drawer?.Invoke(pass);

    public override void Dispose()
    {
        OptimumModPasses.UnregisterMod(ModId);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/Fixtures/ClientApiStub.cs
namespace Optimum.Render.Vulkan.Tests.Fixtures
{
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;

/// <summary>
/// A client API with only the members mod registration touches: <c>Event</c> and its
/// <c>LeaveWorld</c> event. Everything else returns its default.
/// </summary>
public class ClientApiStub : DispatchProxy
{
    public readonly List<Action> LeaveWorldHandlers = new();

    private IClientEventAPI? events;

    public static (ICoreClientAPI Api, ClientApiStub Stub) Create()
    {
        ICoreClientAPI api = Create<ICoreClientAPI, ClientApiStub>();
        var stub = (ClientApiStub)(object)api;
        IClientEventAPI events = Create<IClientEventAPI, ClientApiStub>();
        var eventStub = (ClientApiStub)(object)events;
        stub.events = events;
        eventStub.owner = stub;
        return (api, stub);
    }

    private ClientApiStub? owner;

    /// <summary>What leaving the world does to the subscribers.</summary>
    public void LeaveWorld()
    {
        foreach (Action handler in LeaveWorldHandlers.ToArray()) handler();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
        case "get_Event":
            return events;
        case "add_LeaveWorld":
            (owner ?? this).LeaveWorldHandlers.Add((Action)args![0]!);
            return null;
        case "remove_LeaveWorld":
            (owner ?? this).LeaveWorldHandlers.Remove((Action)args![0]!);
            return null;
        }
        Type type = targetMethod.ReturnType;
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
}
