using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Tests.Fixtures;

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
