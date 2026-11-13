using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

// Issue #582: GuiElementItemSlotGridBase subscribed to inventory.SlotNotified in its constructor and never
// unsubscribed, so the inventory kept every closed dialog alive through the delegate. A grid built by a skipped
// AddIf is never composed or disposed, so the subscription now happens on compose.
public sealed class GuiSlotNotifiedTests
{
    private const string GridPatchPath = "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/Inventory/GuiElementItemSlotGridBase.cs.patch";

    [Fact]
    public void Patch_UnsubscribesInDispose_AndSubscribesOnCompose_NotInTheConstructor()
    {
        string grid = PatchReader.ReadPatch(GridPatchPath);
        Assert.Contains("inventory.SlotNotified -= OnSlotNotified;", grid);
        Assert.Contains("inventory.SlotNotified += OnSlotNotified;", grid);
        Assert.DoesNotContain("SendPacketHandler = sendPacket;\n\n            inventory.SlotNotified += OnSlotNotified;", grid);

        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        Assert.Contains("PatchGuiSlotNotifiedSubscription(", patcher);
        Assert.Contains("if (guiSlotNotifiedPatches != 3)", patcher);
    }

    [Fact]
    public void PatchGuiSlotNotifiedSubscription_MovesTheSubscriptionFromTheConstructorToCompose()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        Assert.Equal(3, ApiPatcher.PatchGuiSlotNotifiedSubscription(vanilla.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        var grid = vanilla.MainModule.GetType("Vintagestory.API.Client.GuiElementItemSlotGridBase")!;

        var dispose = grid.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
        var disposeInstructions = dispose.Body.Instructions.ToList();
        int baseCall = disposeInstructions.FindIndex(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Dispose"
            && ((MethodReference)i.Operand).DeclaringType.Name == "GuiElement");
        int remove = disposeInstructions.FindIndex(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "remove_SlotNotified");
        int firstReturn = disposeInstructions.FindIndex(i => i.OpCode == OpCodes.Ret);
        Assert.True(baseCall >= 0 && remove > baseCall, "the unsubscription follows base.Dispose()");
        // The crossedOutTexture null check returns early, so no ret may come before the unsubscription.
        Assert.True(remove < firstReturn, "a ret before the unsubscription would skip it");
        Assert.DoesNotContain(disposeInstructions, i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "add_SlotNotified");

        // A grid that is never composed must never subscribe, so the constructor no longer does.
        foreach (var constructor in grid.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.HasBody))
        {
            Assert.DoesNotContain(constructor.Body.Instructions,
                i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "add_SlotNotified");
        }

        var compose = grid.Methods.Single(m => m.Name == "ComposeInteractiveElements" && m.Parameters.Count == 0);
        var composeInstructions = compose.Body.Instructions.ToList();
        int reuseDispose = composeInstructions.FindIndex(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && ((MethodReference)i.Operand).Name == "Dispose" && ((MethodReference)i.Operand).Parameters.Count == 0);
        int add = composeInstructions.FindIndex(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "add_SlotNotified");
        Assert.True(reuseDispose >= 0 && add > reuseDispose, "the subscription follows the reuse Dispose()");
        Assert.Equal(1, composeInstructions.Count(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "add_SlotNotified"));
        // The "slotQuantityTextures != null" branch must land on the subscription, so the first compose subscribes too.
        Instruction subscriptionStart = composeInstructions[add - 5];
        Assert.Equal(OpCodes.Ldarg_0, subscriptionStart.OpCode);
        Assert.Contains(composeInstructions, i => ReferenceEquals(i.Operand, subscriptionStart));
    }

    private static string? FindVanillaApi()
    {
        foreach (string relative in new[]
        {
            ".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll",
        })
        {
            try
            {
                return PatchReader.FindRepositoryFile(relative);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return null;
    }
}
