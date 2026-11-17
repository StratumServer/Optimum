using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;
using VsOrderedDictionary = Vintagestory.API.Datastructures.OrderedDictionary<int, Vintagestory.API.Common.ItemSlot>;

namespace Optimum.Tests;

// Issue #580: Deliver the #512 inventory search and stable slot sorting optimizations into the
// shipped VintagestoryAPI.dll via Optimum.Patcher/api-patcher.cs and verify both the patched IL
// and runtime search/sort semantics.
public sealed class InventorySearchPatchTests
{
    [Fact]
    public void Bridge_InventorySearchHelpers_AreRegisteredAndCountedByThePatcher()
    {
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        Assert.Contains("PatchInventorySearch(", patcher);
        Assert.Contains("if (inventorySearchPatches != 2)", patcher);
    }

    [Fact]
    public void PatchInventorySearch_RewritesVanillaMethodsToBridgeAndRejectsReapply()
    {
        string? vanillaApiPath = FindVanillaApi();
        if (vanillaApiPath == null) return;

        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumApiBridge).Assembly.Location);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
        var bridge = contracts.MainModule.GetType("Vintagestory.API.Config.OptimumApiBridge")!;
        var matchesBridge = bridge.Methods.Single(m => m.Name == nameof(OptimumApiBridge.ItemStackMatchesSearchText) && m.Parameters.Count == 3);
        var filterBridge = bridge.Methods.Single(m => m.Name == nameof(OptimumApiBridge.FilterSlotGridItems) && m.Parameters.Count == 10);

        int patched = ApiPatcher.PatchInventorySearch(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(matchesBridge),
            vanilla.MainModule.ImportReference(filterBridge));
        Assert.Equal(2, patched);
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(vanilla.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        AssertPatchedInventorySearchIl(vanilla.MainModule);

        Assert.Throws<InvalidOperationException>(() => ApiPatcher.PatchInventorySearch(
            vanilla.MainModule,
            vanilla.MainModule.ImportReference(matchesBridge),
            vanilla.MainModule.ImportReference(filterBridge)));
    }

    [Fact]
    public void ShippedPatchedApi_ContainsInventorySearchAndSortBridgeCalls()
    {
        string? shippedPatchedApi = FindOptionalRepositoryFile("bin/Release/net10.0/VintagestoryAPI-patched.dll")
            ?? FindOptionalRepositoryFile("bin/Debug/net10.0/VintagestoryAPI-patched.dll");
        if (shippedPatchedApi == null) return;

        using var patched = AssemblyDefinition.ReadAssembly(shippedPatchedApi);
        AssertPatchedInventorySearchIl(patched.MainModule);
    }

    [Fact]
    public void ItemStackMatchesSearchText_ShortCircuitsOnNameAndReusesCleanedDummySlot()
    {
        var nameOnly = new SearchProbeCollectible("Oak Planks", "Used for building houses");
        Assert.True(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(nameOnly), null, "planks"));
        Assert.Equal(0, nameOnly.DescriptionCalls);

        Assert.True(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(nameOnly), null, ""));
        Assert.True(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(nameOnly), null, null));
        Assert.Equal(0, nameOnly.DescriptionCalls);

        ItemSlot? firstSlot = null;
        ItemSlot? secondSlot = null;
        var descItemA = new SearchProbeCollectible("Granite Cobble", "Hard volcanic stone", slot =>
        {
            firstSlot = slot;
            slot.BackgroundIcon = "dirty-icon";
            slot.HexBackgroundColor = "#123456";
            slot.DrawUnavailable = true;
        });
        var descItemB = new SearchProbeCollectible("Basalt Brick", "Smooth dark masonry", slot => secondSlot = slot);

        Assert.True(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(descItemA), null, "volcanic"));
        Assert.NotNull(firstSlot);
        Assert.Null(firstSlot!.Itemstack);
        Assert.Null(firstSlot.BackgroundIcon);
        Assert.Null(firstSlot.HexBackgroundColor);
        Assert.False(firstSlot.DrawUnavailable);

        Assert.False(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(descItemB), null, "obsidian"));
        Assert.Same(firstSlot, secondSlot);
        Assert.Null(secondSlot!.Itemstack);
    }

    [Fact]
    public void ItemStackMatchesSearchText_HandlesReentrantCallSafely()
    {
        ItemSlot? innerSlot = null;
        ItemSlot? outerSlot = null;
        var inner = new SearchProbeCollectible("Flint", "Sharp stone chip", slot => innerSlot = slot);
        var innerStack = new ItemStack(inner);

        var outer = new SearchProbeCollectible("Tool Kit", "Contains useful tools", slot =>
        {
            outerSlot = slot;
            Assert.NotNull(slot.Itemstack);
            bool innerMatched = OptimumApiBridge.ItemStackMatchesSearchText(innerStack, null, "chip");
            Assert.True(innerMatched);
            // Outer slot must still hold the outer ItemStack after the nested call returns.
            Assert.NotNull(slot.Itemstack);
            Assert.Same("Tool Kit", slot.Itemstack!.GetName());
        });

        Assert.True(OptimumApiBridge.ItemStackMatchesSearchText(new ItemStack(outer), null, "useful"));
        Assert.NotNull(outerSlot);
        Assert.NotNull(innerSlot);
        Assert.NotSame(outerSlot, innerSlot);
        Assert.Null(outerSlot!.Itemstack);
        Assert.Null(innerSlot!.Itemstack);
    }

    [Fact]
    public void FilterSlotGridItems_RanksAllWeightTiersAndPreservesTieOrder()
    {
        var items = new[]
        {
            (Key: 10, Name: "iron sword handle", Desc: "metal part"),             // tier 0.125f (starts with word)
            (Key: 20, Name: "ironwoodplank", Desc: "hard wood"),                  // tier 0.75f  (starts with prefix)
            (Key: 30, Name: "cast iron", Desc: "smelted metal"),                  // tier 0.25f  (ends with word)
            (Key: 40, Name: "gridiron", Desc: "cooking grate"),                   // tier 1f     (substring)
            (Key: 50, Name: "pig iron ingot", Desc: "crude metal"),               // tier 0.5f   (exact inner word)
            (Key: 60, Name: "anvil", Desc: "shaped from heavy metal"),            // no match
            (Key: 70, Name: "iron", Desc: "pure element"),                        // tier 0f     (exact match)
            (Key: 80, Name: "chisel", Desc: "iron-forged carpenter tool"),        // tier 2f     (desc starts with)
            (Key: 90, Name: "ironaxe", Desc: "chopper"),                          // tier 0.75f  (starts with prefix, tie #2)
            (Key: 100, Name: "meteorite", Desc: "contains iron ore"),             // tier 3f     (desc contains)
            (Key: 110, Name: "iron block", Desc: "solid cube"),                   // tier 0.125f (starts with word, tie #2)
            (Key: 120, Name: "raw iron bar", Desc: "bar stock"),                  // tier 0.5f   (exact inner word, tie #2)
        };

        var inventory = new TestSearchInventory(items.Length);
        var available = new VsOrderedDictionary();
        var rendered = new VsOrderedDictionary();
        var cache = new Dictionary<int, string>();
        var names = new Dictionary<int, string>();

        for (int i = 0; i < items.Length; i++)
        {
            var stack = new ItemStack(new SearchProbeCollectible(items[i].Name, items[i].Desc));
            inventory[i].Itemstack = stack;
            available.Add(i, inventory[i]);
            names[i] = items[i].Name;
            cache[i] = items[i].Desc;
        }

        string? searchText = null;
        int rows = 0;

        OptimumApiBridge.FilterSlotGridItems(
            ref searchText, available, rendered, inventory, null, cols: 4, ref rows,
            text: "Iron", searchCache: cache, searchCacheNames: names);

        int[] expectedKeys = [6, 0, 10, 2, 4, 11, 1, 8, 3, 7, 9];
        int[] actualKeys = Enumerable.Range(0, rendered.Count).Select(rendered.GetKeyAtIndex).ToArray();
        Assert.Equal("iron", searchText);
        Assert.Equal(expectedKeys, actualKeys);
        Assert.Equal((int)Math.Ceiling(1f * expectedKeys.Length / 4), rows);

        // Clearing search returns all non-empty slots in original availableSlots order.
        OptimumApiBridge.FilterSlotGridItems(
            ref searchText, available, rendered, inventory, null, cols: 4, ref rows,
            text: "", searchCache: cache, searchCacheNames: names);
        Assert.Equal(Enumerable.Range(0, items.Length).ToArray(),
            Enumerable.Range(0, rendered.Count).Select(rendered.GetKeyAtIndex).ToArray());

        // Null searchCache uses ItemStackMatchesSearchText fallback.
        OptimumApiBridge.FilterSlotGridItems(
            ref searchText, available, rendered, inventory, null, cols: 4, ref rows,
            text: "smelted", searchCache: null, searchCacheNames: names);
        Assert.Equal([2], Enumerable.Range(0, rendered.Count).Select(rendered.GetKeyAtIndex).ToArray());

        // Null searchCacheNames leaves renderedSlots empty on non-empty search, matching vanilla.
        OptimumApiBridge.FilterSlotGridItems(
            ref searchText, available, rendered, inventory, null, cols: 4, ref rows,
            text: "iron", searchCache: cache, searchCacheNames: null);
        Assert.Empty(rendered);
    }

    [Fact]
    public void FilterSlotGridItems_SteadyStateCachedSearchAvoidsHeapAllocations()
    {
        const int total = 200;
        var inventory = new TestSearchInventory(total);
        var available = new VsOrderedDictionary();
        var rendered = new VsOrderedDictionary();
        var cache = new Dictionary<int, string>(total);
        var names = new Dictionary<int, string>(total);

        for (int i = 0; i < total; i++)
        {
            string name = i % 2 == 0 ? $"granite stone {i}" : $"oak plank {i}";
            string desc = "decorative building block";
            inventory[i].Itemstack = new ItemStack(new SearchProbeCollectible(name, desc));
            available.Add(i, inventory[i]);
            names[i] = name;
            cache[i] = desc;
        }

        string? searchText = null;
        int rows = 0;

        // Warmup populates the thread-local scratch buffer and renderedSlots internal capacity.
        OptimumApiBridge.FilterSlotGridItems(ref searchText, available, rendered, inventory, null, 8, ref rows, "granite", cache, names);
        rendered.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        OptimumApiBridge.FilterSlotGridItems(ref searchText, available, rendered, inventory, null, 8, ref rows, "granite", cache, names);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(total / 2, rendered.Count);
        // Only the normalized search string from ToSearchFriendly().ToLowerInvariant() may allocate (~128 bytes max).
        Assert.True(after - before <= 256, $"Unexpected steady-state allocations: {after - before} bytes");
    }

    private static void AssertPatchedInventorySearchIl(ModuleDefinition module)
    {
        var itemStack = module.GetType("Vintagestory.API.Common.ItemStack")!;
        var matches = itemStack.Methods.Single(m => m.Name == "MatchesSearchText" && m.Parameters.Count == 2);
        var matchesInstructions = matches.Body.Instructions;
        Assert.DoesNotContain(matchesInstructions, i => i.OpCode == OpCodes.Newobj);
        Assert.Contains(matchesInstructions, i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference called &&
            called.DeclaringType.FullName == "Vintagestory.API.Config.OptimumApiBridge" &&
            called.Name == nameof(OptimumApiBridge.ItemStackMatchesSearchText));

        var grid = module.GetType("Vintagestory.API.Client.GuiElementItemSlotGridBase")!;
        var filter = grid.Methods.Single(m => m.Name == "FilterItemsBySearchText" && m.Parameters.Count == 3);
        var filterInstructions = filter.Body.Instructions;
        Assert.DoesNotContain(filterInstructions, i =>
            i.Operand is MethodReference called &&
            called.Name == "OrderBy");
        Assert.Contains(filterInstructions, i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference called &&
            called.DeclaringType.FullName == "Vintagestory.API.Config.OptimumApiBridge" &&
            called.Name == nameof(OptimumApiBridge.FilterSlotGridItems));
        Assert.Contains(filterInstructions, i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference called &&
            called.Name == "ComposeInteractiveElements");
    }

    private static string? FindVanillaApi() =>
        FindOptionalRepositoryFile(".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll")
        ?? FindOptionalRepositoryFile(".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");

    private static string? FindOptionalRepositoryFile(string relative)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relative);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private sealed class SearchProbeCollectible : Item
    {
        private readonly string _name;
        private readonly string _description;
        private readonly Action<ItemSlot>? _onDescription;

        public int DescriptionCalls { get; private set; }

        public SearchProbeCollectible(string name, string description, Action<ItemSlot>? onDescription = null)
        {
            _name = name;
            _description = description;
            _onDescription = onDescription;
        }

        public override string GetHeldItemName(ItemStack itemStack) => _name;

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            DescriptionCalls++;
            _onDescription?.Invoke(inSlot);
            dsc.Append(_description);
        }
    }

    private sealed class TestSearchInventory : InventoryBase
    {
        private readonly ItemSlot[] _slots;

        public TestSearchInventory(int slotCount) : base("test-search", null)
        {
            _slots = new ItemSlot[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                _slots[i] = new DummySlot();
            }
        }

        public override int Count => _slots.Length;

        public override ItemSlot this[int slotId]
        {
            get => _slots[slotId];
            set => _slots[slotId] = value;
        }

        public override void FromTreeAttributes(Vintagestory.API.Datastructures.ITreeAttribute tree) { }

        public override void ToTreeAttributes(Vintagestory.API.Datastructures.ITreeAttribute tree) { }
    }
}
