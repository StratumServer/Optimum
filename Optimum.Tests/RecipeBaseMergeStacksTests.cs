using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Xunit;

namespace Optimum.Tests;

public class RecipeBaseMergeStacksTests
{
    private sealed class TestRecipeHarness : RecipeBase
    {
        public override IEnumerable<IRecipeIngredient> RecipeIngredients => Array.Empty<IRecipeIngredient>();
        public override IRecipeOutput RecipeOutput => null!;
        public override bool Resolve(IWorldAccessor world, string sourceForErrorLogging) => true;
        public override RecipeBase Clone() => new TestRecipeHarness();

        public void CallMergeStacks(ItemSlot[] slots, List<ItemStack> stacks)
        {
            MergeStacks(slots, stacks);
        }
    }

    private sealed class MockItem : Item
    {
        public override bool Satisfies(ItemStack thisStack, ItemStack otherStack)
        {
            return thisStack.Class == otherStack.Class && thisStack.Id == otherStack.Id;
        }
    }

    [Fact]
    public void MergeStacks_EmptyArray_LeavesStacksEmpty()
    {
        var recipe = new TestRecipeHarness();
        var stacks = new List<ItemStack>();

        recipe.CallMergeStacks(Array.Empty<ItemSlot>(), stacks);

        Assert.Empty(stacks);
    }

    [Fact]
    public void MergeStacks_AllEmptySlots_LeavesStacksEmpty()
    {
        var recipe = new TestRecipeHarness();
        var stacks = new List<ItemStack>();
        var slots = new ItemSlot[] { new DummySlot(), new DummySlot(), new DummySlot() };

        recipe.CallMergeStacks(slots, stacks);

        Assert.Empty(stacks);
    }

    [Fact]
    public void MergeStacks_NullSlotReferences_SafelySkipped()
    {
        var recipe = new TestRecipeHarness();
        var stacks = new List<ItemStack>();
        var item = new MockItem { ItemId = 1 };
        var slots = new ItemSlot[]
        {
            null!,
            new DummySlot(new ItemStack(item, 5)),
            null!,
            new DummySlot(),
            null!
        };

        recipe.CallMergeStacks(slots, stacks);

        Assert.Single(stacks);
        Assert.Equal(1, stacks[0].Id);
        Assert.Equal(5, stacks[0].StackSize);
    }

    [Fact]
    public void MergeStacks_MultipleIdenticalStacks_MergesStackSizes()
    {
        var recipe = new TestRecipeHarness();
        var stacks = new List<ItemStack>();
        var item = new MockItem { ItemId = 1 };
        var slots = new ItemSlot[]
        {
            new DummySlot(new ItemStack(item, 2)),
            new DummySlot(new ItemStack(item, 3)),
            new DummySlot(new ItemStack(item, 4))
        };

        recipe.CallMergeStacks(slots, stacks);

        Assert.Single(stacks);
        Assert.Equal(1, stacks[0].Id);
        Assert.Equal(9, stacks[0].StackSize);
    }

    [Fact]
    public void MergeStacks_DistinctStacks_AppendsIndividuallyInSlotOrder()
    {
        var recipe = new TestRecipeHarness();
        var stacks = new List<ItemStack>();
        var itemA = new MockItem { ItemId = 10 };
        var itemB = new MockItem { ItemId = 20 };
        var itemC = new MockItem { ItemId = 30 };

        var slots = new ItemSlot[]
        {
            new DummySlot(new ItemStack(itemA, 1)),
            new DummySlot(new ItemStack(itemB, 2)),
            new DummySlot(new ItemStack(itemC, 3))
        };

        recipe.CallMergeStacks(slots, stacks);

        Assert.Equal(3, stacks.Count);
        Assert.Equal(10, stacks[0].Id);
        Assert.Equal(1, stacks[0].StackSize);
        Assert.Equal(20, stacks[1].Id);
        Assert.Equal(2, stacks[1].StackSize);
        Assert.Equal(30, stacks[2].Id);
        Assert.Equal(3, stacks[2].StackSize);
    }

    [Fact]
    public void MergeStacks_MixedContents_MatchesVanillaLinqBehavior()
    {
        var recipe = new TestRecipeHarness();
        var itemA = new MockItem { ItemId = 1 };
        var itemB = new MockItem { ItemId = 2 };
        var itemC = new MockItem { ItemId = 3 };

        var slots = new ItemSlot[]
        {
            null!,
            new DummySlot(),
            new DummySlot(new ItemStack(itemA, 2)),
            new DummySlot(new ItemStack(itemB, 5)),
            new DummySlot(),
            new DummySlot(new ItemStack(itemA, 3)),
            new DummySlot(new ItemStack(itemC, 1)),
            null!,
            new DummySlot(new ItemStack(itemB, 4))
        };

        var expectedStacks = new List<ItemStack>();
        VanillaMergeStacks(slots, expectedStacks);

        var actualStacks = new List<ItemStack>();
        recipe.CallMergeStacks(slots, actualStacks);

        Assert.Equal(expectedStacks.Count, actualStacks.Count);
        for (int i = 0; i < expectedStacks.Count; i++)
        {
            Assert.Equal(expectedStacks[i].Id, actualStacks[i].Id);
            Assert.Equal(expectedStacks[i].StackSize, actualStacks[i].StackSize);
        }
    }

    [Fact]
    public void MergeStacks_EmptySlots_ProducesZeroAllocations()
    {
        var recipe = new TestRecipeHarness();
        var slots = new ItemSlot[]
        {
            new DummySlot(), new DummySlot(), new DummySlot(),
            new DummySlot(), new DummySlot(), new DummySlot(),
            new DummySlot(), new DummySlot(), new DummySlot()
        };
        var stacks = new List<ItemStack>(slots.Length);

        // Warmup
        for (int i = 0; i < 50; i++)
        {
            stacks.Clear();
            recipe.CallMergeStacks(slots, stacks);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            stacks.Clear();
            recipe.CallMergeStacks(slots, stacks);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    private static void VanillaMergeStacks(ItemSlot[] slots, List<ItemStack> stacks)
    {
        foreach (ItemStack suppliedStack in slots.Select(slot => slot?.Itemstack).OfType<ItemStack>())
        {
            ItemStack? similarStack = stacks.Find(stack => stack.Satisfies(suppliedStack));
            if (similarStack != null)
            {
                similarStack.StackSize += suppliedStack.StackSize;
            }
            else
            {
                stacks.Add(suppliedStack.Clone());
            }
        }
    }
}
