#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace Optimum.Tests;

public sealed class CraftingGridDragBatchTests
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

    private static string? FindPatchedLibPath()
    {
        string repoRoot = RepoRoot();
        string release = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll");
        if (File.Exists(release)) return release;
        string debug = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Debug", "net10.0", "VintagestoryLib-patched.dll");
        return File.Exists(debug) ? debug : null;
    }

    [Fact]
    public void GuiManager_PatchWrapsOnMouseMoveInCraftingGridBatch()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiManager.cs.patch");
        Assert.True(File.Exists(patchPath), "GuiManager.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("Vintagestory.Common.InventoryCraftingGrid.BeginMouseMoveBatch();", patched);
        Assert.Contains("Vintagestory.Common.InventoryCraftingGrid.EndMouseMoveBatch();", patched);
        Assert.Contains("finally", patched);
    }

    [Fact]
    public void InventoryCraftingGrid_PatchDefersClientSearchAndFlushesBeforeCraft()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/InventoryCraftingGrid.cs.patch");
        Assert.True(File.Exists(patchPath), "InventoryCraftingGrid.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("[ThreadStatic]", patched);
        Assert.Contains("t_mouseMoveBatchDepth", patched);
        Assert.Contains("t_pendingGrid", patched);
        Assert.Contains("t_pendingOverflow", patched);
        Assert.Contains("Api?.Side == EnumAppSide.Client", patched);
        Assert.Contains("ScheduleOrFindMatchingRecipe();", patched);
        Assert.Contains("FlushPendingRecipeSearch();", patched);
        Assert.Contains("ClearPendingRecipeSearch();", patched);
    }

    [Fact]
    public void PatcherManifest_RegistersCraftingGridBatchMembersAndMethods()
    {
        string programPath = PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs");
        string source = File.ReadAllText(programPath);

        string[] requiredTokens =
        {
            "[\"Vintagestory.Common.InventoryCraftingGrid\"]",
            "\"t_mouseMoveBatchDepth\"",
            "\"t_pendingGrid\"",
            "\"t_pendingOverflow\"",
            "\"BeginMouseMoveBatch\"",
            "\"EndMouseMoveBatch\"",
            "\"FlushAllPendingRecipeSearches\"",
            "\"ScheduleOrFindMatchingRecipe\"",
            "\"EnqueuePendingRecipeSearch\"",
            "\"FlushPendingRecipeSearch\"",
            "\"ClearPendingRecipeSearch\"",
            "new(\"Vintagestory.Common.InventoryCraftingGrid\", \"ActivateSlot\", 3)",
            "new(\"Vintagestory.Common.InventoryCraftingGrid\", \"OnItemSlotModified\", 1)",
            "new(\"Vintagestory.Common.InventoryCraftingGrid\", \"TryMoveItemStack\", 4)",
            "new(\"Vintagestory.Common.InventoryCraftingGrid\", \"FindMatchingRecipe\", 0)",
            "new(\"Vintagestory.Common.InventoryCraftingGrid\", \"ConsumeIngredients\", 1)",
            "new(\"Vintagestory.Client.NoObf.GuiManager\", \"OnMouseMove\", 1)",
        };

        foreach (string token in requiredTokens)
        {
            Assert.Contains(token, source);
        }
    }

    [Fact]
    public void PatchedAssembly_PreservesThreadStaticOnAllCraftingGridBatchFields()
    {
        string? patchedPath = FindPatchedLibPath();
        if (patchedPath == null) return;

        using var assembly = AssemblyDefinition.ReadAssembly(patchedPath);
        var gridType = assembly.MainModule.GetType("Vintagestory.Common.InventoryCraftingGrid");
        Assert.NotNull(gridType);

        string[] threadStaticFields = { "t_mouseMoveBatchDepth", "t_pendingGrid", "t_pendingOverflow" };
        foreach (string fieldName in threadStaticFields)
        {
            var field = gridType!.Fields.FirstOrDefault(f => f.Name == fieldName);
            Assert.NotNull(field);
            Assert.True(field!.IsStatic, $"{fieldName} must be static");
            Assert.Contains(
                field.CustomAttributes,
                attr => attr.AttributeType.FullName == "System.ThreadStaticAttribute");
        }
    }

    [Fact]
    public void PatchedAssembly_WiresGuiManagerFinallyAndInventoryCraftingGridHooks()
    {
        string? patchedPath = FindPatchedLibPath();
        if (patchedPath == null) return;

        using var assembly = AssemblyDefinition.ReadAssembly(patchedPath);

        var guiManager = assembly.MainModule.GetType("Vintagestory.Client.NoObf.GuiManager");
        Assert.NotNull(guiManager);
        var onMouseMove = guiManager!.Methods.Single(m => m.Name == "OnMouseMove" && m.Parameters.Count == 1);
        Assert.Contains(
            onMouseMove.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "BeginMouseMoveBatch");
        Assert.Contains(
            onMouseMove.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "EndMouseMoveBatch");
        Assert.Contains(
            onMouseMove.Body.ExceptionHandlers,
            eh => eh.HandlerType == ExceptionHandlerType.Finally);

        var gridType = assembly.MainModule.GetType("Vintagestory.Common.InventoryCraftingGrid");
        Assert.NotNull(gridType);

        var onModified = gridType!.Methods.Single(m => m.Name == "OnItemSlotModified" && m.Parameters.Count == 1);
        Assert.Contains(
            onModified.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "ScheduleOrFindMatchingRecipe");
        Assert.DoesNotContain(
            onModified.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "FindMatchingRecipe");

        var tryMove = gridType.Methods.Single(m => m.Name == "TryMoveItemStack" && m.Parameters.Count == 4);
        Assert.Contains(
            tryMove.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "ScheduleOrFindMatchingRecipe");

        var activateSlot = gridType.Methods.Single(m => m.Name == "ActivateSlot" && m.Parameters.Count == 3);
        Assert.Contains(
            activateSlot.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "FlushPendingRecipeSearch");

        var consume = gridType.Methods.Single(m => m.Name == "ConsumeIngredients" && m.Parameters.Count == 1);
        Assert.Contains(
            consume.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "FlushPendingRecipeSearch");
    }

    private sealed class PatchedCraftingHarness : IDisposable
    {
        private readonly AssemblyLoadContext? _alc;
        private readonly Type? _gridType;
        private readonly MethodInfo? _beginBatch;
        private readonly MethodInfo? _endBatch;
        private readonly MethodInfo? _consumeIngredients;
        private readonly FieldInfo? _matchingRecipeField;

        public bool IsAvailable => _gridType != null;
        public Item InputItem { get; }
        public Item OutputItem { get; }
        public GridRecipe FullGridRecipe { get; }
        public OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>> FastSearchMap { get; }
        public int SearchCount => _searchCount;
        private int _searchCount;

        public ICoreAPI ClientApi { get; }
        public ICoreAPI ServerApi { get; }
        public IPlayer Player { get; private set; } = null!;
        public IWorldAccessor World { get; }
        private readonly Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase> _inventories = new();

        public PatchedCraftingHarness(int extraIngredientKeys = 0)
        {
            var entityPlayer = new EntityPlayer();
            var eventApi = CreateProxy<IEventAPI>(method => method.Name switch
            {
                "TriggerMatchesRecipe" => true,
                _ => GetDefault(method.ReturnType),
            });
            var classRegistry = CreateProxy<IClassRegistryAPI>(method => GetDefault(method.ReturnType));
            var clientWorldApi = CreateProxy<ICoreAPI>(method => method.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_Event" => eventApi,
                "get_ClassRegistry" => classRegistry,
                _ => GetDefault(method.ReturnType),
            });

            World = CreateProxy<IWorldAccessor>(method => method.Name switch
            {
                "get_FastSearchRecipesByIngredient" => IncrementAndReturnMap(),
                "PlayerByUid" => Player!,
                "get_Api" => clientWorldApi,
                _ => GetDefault(method.ReturnType),
            });
            entityPlayer.World = World;
            entityPlayer.Api = clientWorldApi;

            ClientApi = CreateProxy<ICoreAPI>(method => method.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_World" => World,
                "get_Event" => eventApi,
                "get_ClassRegistry" => classRegistry,
                _ => GetDefault(method.ReturnType),
            });

            ServerApi = CreateProxy<ICoreAPI>(method => method.Name switch
            {
                "get_Side" => EnumAppSide.Server,
                "get_World" => World,
                "get_Event" => eventApi,
                "get_ClassRegistry" => classRegistry,
                _ => GetDefault(method.ReturnType),
            });

            InputItem = new Item(101) { Code = new AssetLocation("game", "teststick"), MaxStackSize = 64 };
            InputItem.OnLoadedNative(ClientApi);
            OutputItem = new Item(102) { Code = new AssetLocation("game", "testbundle"), MaxStackSize = 64 };
            OutputItem.OnLoadedNative(ClientApi);

            var recipeIngredient = new CraftingRecipeIngredient
            {
                Type = EnumItemClass.Item,
                Code = InputItem.Code,
                Quantity = 1,
                ResolvedItemStack = new ItemStack(InputItem, 1),
            };

            FullGridRecipe = new GridRecipe
            {
                Enabled = true,
                Width = 3,
                Height = 3,
                Shapeless = false,
                IngredientPattern = "AAA,AAA,AAA",
                Ingredients = new Dictionary<string, CraftingRecipeIngredient> { ["A"] = recipeIngredient },
                ResolvedIngredients = Enumerable.Range(0, 9).Select(_ => recipeIngredient.Clone()).ToArray(),
                Output = new CraftingRecipeIngredient
                {
                    Type = EnumItemClass.Item,
                    Code = OutputItem.Code,
                    Quantity = 2,
                    ResolvedItemStack = new ItemStack(OutputItem, 2),
                },
            };

            FastSearchMap = new OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>();
            for (int i = 0; i < extraIngredientKeys; i++)
            {
                var dummyItem = new Item(1000 + i) { Code = new AssetLocation("mod", $"filler-{i}"), MaxStackSize = 64 };
                dummyItem.OnLoadedNative(ClientApi);
                var filler = new CraftingRecipeIngredient
                {
                    Type = EnumItemClass.Item,
                    Code = dummyItem.Code,
                    Quantity = 1,
                    ResolvedItemStack = new ItemStack(dummyItem, 1),
                };
                FastSearchMap[filler] = new List<IRecipeBase> { FullGridRecipe };
            }
            FastSearchMap[recipeIngredient] = new List<IRecipeBase> { FullGridRecipe };

            string? patchedPath = FindPatchedLibPath();
            if (patchedPath == null)
            {
                return;
            }

            string repoRoot = RepoRoot();
            string libDir = Path.GetDirectoryName(patchedPath)!;
            string vanillaLibDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory", "Lib");
            _alc = new AssemblyLoadContext($"CraftingGridBatchTest-{Guid.NewGuid():N}", isCollectible: true);
            _alc.Resolving += (ctx, name) =>
            {
                // Share VintagestoryAPI with Default ALC so InventoryBase/ItemSlot/GridRecipe share CLR identity.
                if (string.Equals(name.Name, "VintagestoryAPI", StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(InventoryBase).Assembly;
                }
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

            Assembly libAsm = _alc.LoadFromAssemblyPath(patchedPath);
            _gridType = libAsm.GetType("Vintagestory.Common.InventoryCraftingGrid", throwOnError: true)!;
            _beginBatch = _gridType.GetMethod("BeginMouseMoveBatch", BindingFlags.Static | BindingFlags.NonPublic)!;
            _endBatch = _gridType.GetMethod("EndMouseMoveBatch", BindingFlags.Static | BindingFlags.NonPublic)!;
            _consumeIngredients = _gridType.GetMethod("ConsumeIngredients", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _matchingRecipeField = _gridType.GetField("MatchingRecipe", BindingFlags.Instance | BindingFlags.Public)!;

            // Construct a real ClientPlayer backed by ClientWorldPlayerData + ClientPlayerInventoryManager.
            Type clientPlayerType = libAsm.GetType("Vintagestory.Client.NoObf.ClientPlayer", throwOnError: true)!;
            Type worldDataType = libAsm.GetType("Vintagestory.Client.NoObf.ClientWorldPlayerData", throwOnError: true)!;
            Type invMgrType = libAsm.GetType("Vintagestory.Client.NoObf.ClientPlayerInventoryManager", throwOnError: true)!;

            object worldData = RuntimeHelpers.GetUninitializedObject(worldDataType);
            worldDataType.GetField("PlayerUID", BindingFlags.Public | BindingFlags.Instance)!.SetValue(worldData, "test-player");
            worldDataType.GetField("gameMode", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(worldData, EnumGameMode.Creative);
            worldDataType.GetField("entityplayer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(worldData, entityPlayer);

            object clientPlayer = RuntimeHelpers.GetUninitializedObject(clientPlayerType);
            clientPlayerType.GetField("worlddata", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(clientPlayer, worldData);

            object invMgr = RuntimeHelpers.GetUninitializedObject(invMgrType);
            invMgrType.BaseType!.GetField("player", BindingFlags.Public | BindingFlags.Instance)!.SetValue(invMgr, clientPlayer);
            invMgrType.BaseType!.GetField("Inventories", BindingFlags.Public | BindingFlags.Instance)!.SetValue(invMgr, _inventories);
            clientPlayerType.GetField("inventoryMgr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(clientPlayer, invMgr);

            Player = (IPlayer)clientPlayer;
        }

        public void RegisterInventory(InventoryBase inv)
        {
            _inventories[inv.InventoryID] = inv;
            inv.Open(Player);
        }

        public ItemSlot CreateSinkSlot()
        {
            var inv = new DummyInventory(ClientApi, 1);
            return inv[0];
        }

        public void BeginBatch() => _beginBatch!.Invoke(null, null);
        public void EndBatch() => _endBatch!.Invoke(null, null);

        public GridRecipe? GetMatchingRecipe(InventoryBasePlayer grid) =>
            (GridRecipe?)_matchingRecipeField!.GetValue(grid);

        public GridRecipe? InvokeConsumeIngredients(InventoryBasePlayer grid, ItemSlot outputSlot) =>
            (GridRecipe?)_consumeIngredients!.Invoke(grid, new object[] { outputSlot });

        public void ResetSearchCount() => Interlocked.Exchange(ref _searchCount, 0);

        public InventoryBasePlayer CreateGrid(ICoreAPI api, string id = "craftinggrid-test-player")
        {
            var grid = (InventoryBasePlayer)Activator.CreateInstance(_gridType!, new object?[] { id, null })!;
            grid.LateInitialize(id, api);
            return grid;
        }

        private OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>> IncrementAndReturnMap()
        {
            Interlocked.Increment(ref _searchCount);
            return FastSearchMap;
        }

        public void Dispose()
        {
            _alc?.Unload();
        }
    }

    public class SimpleProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?> Handler { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod == null ? null : Handler(targetMethod);
        }
    }

    private static T CreateProxy<T>(System.Func<MethodInfo, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, SimpleProxy>()!;
        ((SimpleProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? GetDefault(Type type)
    {
        if (type == typeof(void) || !type.IsValueType) return null;
        return Activator.CreateInstance(type);
    }

    private static void SimulateNineSlotLeftDrag(
        PatchedCraftingHarness harness,
        InventoryBasePlayer grid,
        int initialStackSize,
        bool batched,
        out int totalSlotModifiedCallbacks,
        out int[] searchesPerMouseMove)
    {
        int callbacks = 0;
        grid.SlotModified += _ => callbacks++;

        // Mouse down on slot 0 places the held stack into slot 0.
        var mouseSlot = new DummySlot(new ItemStack(harness.InputItem, initialStackSize));
        var initialOp = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, initialStackSize)
        {
            ActingPlayer = harness.Player,
        };
        mouseSlot.TryPutInto(grid[0], ref initialOp);

        var addedBySlot = new Dictionary<int, int> { [0] = initialStackSize };
        var slotOrder = new List<int> { 0 };
        searchesPerMouseMove = new int[8];

        harness.ResetSearchCount();
        callbacks = 0;

        for (int nextSlot = 1; nextSlot < 9; nextSlot++)
        {
            int searchesBeforeStep = harness.SearchCount;
            slotOrder.Add(nextSlot);
            addedBySlot[nextSlot] = 0;

            if (batched) harness.BeginBatch();
            try
            {
                int stacksPerSlot = initialStackSize / slotOrder.Count;
                for (int i = 0; i < slotOrder.Count - 1; i++)
                {
                    int srcId = slotOrder[i];
                    int addedSrc = addedBySlot[srcId];
                    if (addedSrc > stacksPerSlot)
                    {
                        var op = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge)
                        {
                            ActingPlayer = harness.Player,
                            RequestedQuantity = addedSrc - stacksPerSlot,
                        };
                        grid[srcId].TryPutInto(grid[nextSlot], ref op);
                        addedBySlot[nextSlot] += op.MovedQuantity;
                        addedBySlot[srcId] -= op.MovedQuantity;
                    }
                }
            }
            finally
            {
                if (batched) harness.EndBatch();
            }

            searchesPerMouseMove[nextSlot - 1] = harness.SearchCount - searchesBeforeStep;
        }

        totalSlotModifiedCallbacks = callbacks;
    }

    [Fact]
    public void DragRedistribution_RunsRecipeSearchOncePerMouseMoveAndPreservesSlotCallbacks()
    {
        using var harness = new PatchedCraftingHarness(extraIngredientKeys: 25);
        if (!harness.IsAvailable) return;

        var unbatchedGrid = harness.CreateGrid(harness.ClientApi);
        SimulateNineSlotLeftDrag(harness, unbatchedGrid, 64, batched: false, out int unbatchedCallbacks, out int[] unbatchedSearches);

        var batchedGrid = harness.CreateGrid(harness.ClientApi);
        SimulateNineSlotLeftDrag(harness, batchedGrid, 64, batched: true, out int batchedCallbacks, out int[] batchedSearches);

        // Unbatched drag runs 2*k searches on move k (2+4+6+8+10+12+14+16 = 72).
        Assert.Equal(new[] { 2, 4, 6, 8, 10, 12, 14, 16 }, unbatchedSearches);
        Assert.Equal(72, unbatchedSearches.Sum());

        // Batched drag runs exactly 1 search per mouse-move event (8 total).
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 1 }, batchedSearches);
        Assert.Equal(8, batchedSearches.Sum());

        // Every SlotModified callback still fires identically.
        Assert.Equal(72, unbatchedCallbacks);
        Assert.Equal(unbatchedCallbacks, batchedCallbacks);
    }

    [Fact]
    public void DragRedistribution_MatchesOneSlotAtATimePlacementForPreviewOutputAndConsumedIngredients()
    {
        using var harness = new PatchedCraftingHarness(extraIngredientKeys: 10);
        if (!harness.IsAvailable) return;

        // Grid A: place 2 items into each of the 9 slots one slot at a time (no drag batch).
        var manualGrid = harness.CreateGrid(harness.ClientApi);
        for (int slotId = 0; slotId < 9; slotId++)
        {
            var src = new DummySlot(new ItemStack(harness.InputItem, 2));
            var op = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 2)
            {
                ActingPlayer = harness.Player,
            };
            src.TryPutInto(manualGrid[slotId], ref op);
        }

        // Grid B: drag-distribute a stack of 18 items across slots 0..8 inside mouse-move batches.
        var draggedGrid = harness.CreateGrid(harness.ClientApi);
        SimulateNineSlotLeftDrag(harness, draggedGrid, 18, batched: true, out _, out _);

        // Preview and recipe match must be identical before clicking output.
        Assert.Same(harness.GetMatchingRecipe(manualGrid), harness.GetMatchingRecipe(draggedGrid));
        Assert.NotNull(harness.GetMatchingRecipe(draggedGrid));
        Assert.Equal(manualGrid[9].StackSize, draggedGrid[9].StackSize);
        Assert.Equal(harness.OutputItem.Code, draggedGrid[9].Itemstack!.Collectible.Code);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(manualGrid[i].StackSize, draggedGrid[i].StackSize);
        }

        // Craft once via output slot activation and compare crafted output + remaining ingredients.
        ItemSlot manualSink = harness.CreateSinkSlot();
        ItemSlot draggedSink = harness.CreateSinkSlot();
        var manualCraftOp = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 64)
        {
            ActingPlayer = harness.Player,
        };
        var draggedCraftOp = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 64)
        {
            ActingPlayer = harness.Player,
        };
        manualGrid.ActivateSlot(9, manualSink, ref manualCraftOp);
        draggedGrid.ActivateSlot(9, draggedSink, ref draggedCraftOp);

        Assert.Equal(manualSink.StackSize, draggedSink.StackSize);
        Assert.Equal(2, draggedSink.StackSize);
        Assert.Equal(manualSink.Itemstack!.Collectible.Code, draggedSink.Itemstack!.Collectible.Code);
        Assert.Same(harness.GetMatchingRecipe(manualGrid), harness.GetMatchingRecipe(draggedGrid));
        Assert.Equal(manualGrid[9].StackSize, draggedGrid[9].StackSize);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(manualGrid[i].StackSize, draggedGrid[i].StackSize);
            Assert.Equal(1, draggedGrid[i].StackSize);
        }

        // Craft the second remaining set and verify grid empties identically.
        var manualCraftOp2 = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 64)
        {
            ActingPlayer = harness.Player,
        };
        var draggedCraftOp2 = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 64)
        {
            ActingPlayer = harness.Player,
        };
        manualGrid.ActivateSlot(9, manualSink, ref manualCraftOp2);
        draggedGrid.ActivateSlot(9, draggedSink, ref draggedCraftOp2);

        Assert.Equal(4, manualSink.StackSize);
        Assert.Equal(4, draggedSink.StackSize);
        Assert.Null(harness.GetMatchingRecipe(manualGrid));
        Assert.Null(harness.GetMatchingRecipe(draggedGrid));
        Assert.True(draggedGrid[9].Empty);
        for (int i = 0; i < 9; i++)
        {
            Assert.True(manualGrid[i].Empty);
            Assert.True(draggedGrid[i].Empty);
        }
    }

    [Fact]
    public void ThreadIsolation_ServerThreadAndServerGridSearchImmediatelyDuringClientMouseMoveBatch()
    {
        using var harness = new PatchedCraftingHarness();
        if (!harness.IsAvailable) return;

        var clientGrid = harness.CreateGrid(harness.ClientApi, "craftinggrid-client");
        var serverGrid = harness.CreateGrid(harness.ServerApi, "craftinggrid-server");

        harness.ResetSearchCount();

        harness.BeginBatch();
        try
        {
            // Client grid on the dispatching thread defers.
            clientGrid[0].Itemstack = new ItemStack(harness.InputItem, 1);
            clientGrid.OnItemSlotModified(clientGrid[0]);
            Assert.Equal(0, harness.SearchCount);

            // Server-side grid never defers, even on the same thread.
            serverGrid[0].Itemstack = new ItemStack(harness.InputItem, 1);
            serverGrid.OnItemSlotModified(serverGrid[0]);
            Assert.Equal(1, harness.SearchCount);

            // Integrated server (or any other) thread is unaffected by this thread's batch depth.
            int otherThreadSearches = -1;
            var worker = new Thread(() =>
            {
                var otherGrid = harness.CreateGrid(harness.ClientApi, "craftinggrid-other-thread");
                int before = harness.SearchCount;
                otherGrid[0].Itemstack = new ItemStack(harness.InputItem, 1);
                otherGrid.OnItemSlotModified(otherGrid[0]);
                otherThreadSearches = harness.SearchCount - before;
            });
            worker.Start();
            worker.Join();
            Assert.Equal(1, otherThreadSearches);
        }
        finally
        {
            harness.EndBatch();
        }

        // Exiting the client mouse-move batch flushes the deferred client grid search once.
        Assert.Equal(3, harness.SearchCount);
    }

    [Fact]
    public void OutputActivationAndConsumeIngredients_FlushPendingSearchBeforeCrafting()
    {
        using var harness = new PatchedCraftingHarness();
        if (!harness.IsAvailable) return;

        var grid = harness.CreateGrid(harness.ClientApi);
        harness.ResetSearchCount();

        // Scenario 1: ActivateSlot(9) inside an active batch flushes before crafting.
        harness.BeginBatch();
        try
        {
            for (int i = 0; i < 9; i++)
            {
                grid[i].Itemstack = new ItemStack(harness.InputItem, 1);
                grid.OnItemSlotModified(grid[i]);
            }
            Assert.Equal(0, harness.SearchCount);
            Assert.Null(harness.GetMatchingRecipe(grid));

            ItemSlot sink = harness.CreateSinkSlot();
            var op = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 64)
            {
                ActingPlayer = harness.Player,
            };
            grid.ActivateSlot(9, sink, ref op);

            Assert.Equal(2, sink.StackSize);
            Assert.Equal(harness.OutputItem.Code, sink.Itemstack!.Collectible.Code);
            for (int i = 0; i < 9; i++)
            {
                Assert.True(grid[i].Empty);
            }
        }
        finally
        {
            harness.EndBatch();
        }

        // ActivateSlot flushed once before craft, ConsumeIngredients + EndCraft searched after craft,
        // and EndBatch must not trigger an extra redundant search.
        Assert.InRange(harness.SearchCount, 2, 3);

        // Scenario 2: ConsumeIngredients inside an active batch flushes pending search first.
        harness.ResetSearchCount();
        harness.BeginBatch();
        try
        {
            for (int i = 0; i < 9; i++)
            {
                grid[i].Itemstack = new ItemStack(harness.InputItem, 1);
                grid.OnItemSlotModified(grid[i]);
            }
            Assert.Equal(0, harness.SearchCount);

            GridRecipe? consumed = harness.InvokeConsumeIngredients(grid, grid[9]);
            Assert.Same(harness.FullGridRecipe, consumed);
            for (int i = 0; i < 9; i++)
            {
                Assert.True(grid[i].Empty);
            }
        }
        finally
        {
            harness.EndBatch();
        }

        Assert.Equal(2, harness.SearchCount);
    }

    [Fact]
    public void NestedBatchAndExceptionInMouseMove_StillFlushAndResetThreadDepth()
    {
        using var harness = new PatchedCraftingHarness();
        if (!harness.IsAvailable) return;

        var grid = harness.CreateGrid(harness.ClientApi);
        harness.ResetSearchCount();

        // Nested batch: inner EndBatch does not flush; outer EndBatch flushes once.
        harness.BeginBatch();
        try
        {
            harness.BeginBatch();
            try
            {
                grid[0].Itemstack = new ItemStack(harness.InputItem, 1);
                grid.OnItemSlotModified(grid[0]);
            }
            finally
            {
                harness.EndBatch();
            }

            Assert.Equal(0, harness.SearchCount);
        }
        finally
        {
            harness.EndBatch();
        }
        Assert.Equal(1, harness.SearchCount);

        // Exception mid-dispatch: finally still flushes and restores depth to 0.
        harness.ResetSearchCount();
        Action throwingDispatch = () =>
        {
            harness.BeginBatch();
            try
            {
                grid[1].Itemstack = new ItemStack(harness.InputItem, 1);
                grid.OnItemSlotModified(grid[1]);
                throw new InvalidOperationException("simulated gui handler failure");
            }
            finally
            {
                harness.EndBatch();
            }
        };
        Assert.Throws<InvalidOperationException>(throwingDispatch);
        Assert.Equal(1, harness.SearchCount);

        // Subsequent unbatched modification must run immediately (depth == 0).
        grid[2].Itemstack = new ItemStack(harness.InputItem, 1);
        grid.OnItemSlotModified(grid[2]);
        Assert.Equal(2, harness.SearchCount);
    }

    [Fact]
    public void TryMoveItemStack_InsideMouseMoveBatch_CoalescesWithSlotModifiedIntoSingleSearch()
    {
        using var harness = new PatchedCraftingHarness(extraIngredientKeys: 10);
        if (!harness.IsAvailable) return;

        var grid = harness.CreateGrid(harness.ClientApi, "craftinggrid-test-player");
        harness.RegisterInventory(grid);
        grid[0].Itemstack = new ItemStack(harness.InputItem, 4);

        // Unbatched TryMoveItemStack triggers OnItemSlotModified (source + target) AND its own FindMatchingRecipe call.
        harness.ResetSearchCount();
        var opUnbatched = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 2)
        {
            ActingPlayer = harness.Player,
        };
        Assert.True(grid.TryMoveItemStack(harness.Player, new[] { grid.InventoryID, grid.InventoryID }, new[] { 0, 1 }, ref opUnbatched));
        Assert.Equal(3, harness.SearchCount);

        // Batched TryMoveItemStack coalesces both OnItemSlotModified calls and the TryMoveItemStack call into 1 search.
        harness.ResetSearchCount();
        harness.BeginBatch();
        try
        {
            var opBatched = new ItemStackMoveOperation(harness.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 1)
            {
                ActingPlayer = harness.Player,
            };
            Assert.True(grid.TryMoveItemStack(harness.Player, new[] { grid.InventoryID, grid.InventoryID }, new[] { 0, 2 }, ref opBatched));
            Assert.Equal(0, harness.SearchCount);
        }
        finally
        {
            harness.EndBatch();
        }
        Assert.Equal(1, harness.SearchCount);
    }

    [Fact]
    public void MultiGridOverflow_FlushesEveryModifiedClientGridOnce()
    {
        using var harness = new PatchedCraftingHarness();
        if (!harness.IsAvailable) return;

        var gridA = harness.CreateGrid(harness.ClientApi, "craftinggrid-a");
        var gridB = harness.CreateGrid(harness.ClientApi, "craftinggrid-b");
        var gridC = harness.CreateGrid(harness.ClientApi, "craftinggrid-c");
        harness.ResetSearchCount();

        harness.BeginBatch();
        try
        {
            for (int repeat = 0; repeat < 3; repeat++)
            {
                gridA[repeat].Itemstack = new ItemStack(harness.InputItem, 1);
                gridA.OnItemSlotModified(gridA[repeat]);
                gridB[repeat].Itemstack = new ItemStack(harness.InputItem, 1);
                gridB.OnItemSlotModified(gridB[repeat]);
                gridC[repeat].Itemstack = new ItemStack(harness.InputItem, 1);
                gridC.OnItemSlotModified(gridC[repeat]);
            }
            Assert.Equal(0, harness.SearchCount);
        }
        finally
        {
            harness.EndBatch();
        }

        Assert.Equal(3, harness.SearchCount);

        // A second empty batch must not re-search any previously flushed grid.
        harness.BeginBatch();
        harness.EndBatch();
        Assert.Equal(3, harness.SearchCount);
    }
}
