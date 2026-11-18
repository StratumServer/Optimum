using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace Vintagestory.Client.NoObf;

internal sealed class OptimumPreparedInventoryMesh
{
	public readonly int Generation;

	public readonly MeshData[]? Parts;

	public readonly int[]? TextureIds;

	public readonly bool IsUnknown;

	private OptimumPreparedInventoryMesh(int generation, MeshData[]? parts, int[]? textureIds, bool isUnknown)
	{
		Generation = generation;
		Parts = parts;
		TextureIds = textureIds;
		IsUnknown = isUnknown;
	}

	public static OptimumPreparedInventoryMesh CreateUnknown(int generation)
	{
		return new OptimumPreparedInventoryMesh(generation, null, null, true);
	}

	public static OptimumPreparedInventoryMesh CreateFromMeshData(int generation, MeshData data)
	{
		MeshData[] parts = data.SplitByTextureId();
		int[] textureIds = data.TextureIds ?? Array.Empty<int>();
		return new OptimumPreparedInventoryMesh(generation, parts, textureIds, false);
	}
}

internal struct OptimumInventoryPrepTarget
{
	public bool IsBlock;

	public int Id;

	public int RenderVariant;

	public Block? Block;

	public Item? Item;
}

internal sealed class OptimumInventoryParallelBatchState
{
	public readonly OptimumInventoryMeshPreparer Preparer;

	public readonly List<OptimumInventoryPrepTarget> Targets;

	public readonly OptimumPreparedInventoryMesh?[] Results;

	public readonly int Generation;

	public readonly ManualResetEventSlim DoneEvent;

	public int NextIndex;

	public int RemainingWorkers;

	public OptimumInventoryParallelBatchState(
		OptimumInventoryMeshPreparer preparer,
		List<OptimumInventoryPrepTarget> targets,
		int generation,
		int workerCount)
	{
		Preparer = preparer;
		Targets = targets;
		Results = new OptimumPreparedInventoryMesh?[targets.Count];
		Generation = generation;
		DoneEvent = new ManualResetEventSlim(false);
		NextIndex = -1;
		RemainingWorkers = workerCount;
	}

	public void ProcessLoop(bool isWorkerThread)
	{
		try
		{
			while (true)
			{
				int index = Interlocked.Increment(ref NextIndex);
				if ((uint)index >= (uint)Targets.Count)
				{
					break;
				}
				OptimumInventoryPrepTarget target = Targets[index];
				Results[index] = Preparer.PrepareTargetCpuOnly(target, Generation);
			}
		}
		finally
		{
			if (isWorkerThread)
			{
				Preparer.DisposeWorkerTesselator();
				if (Interlocked.Decrement(ref RemainingWorkers) == 0)
				{
					DoneEvent.Set();
				}
			}
		}
	}
}

internal sealed class OptimumInventoryParallelWorkerTask
{
	private readonly OptimumInventoryParallelBatchState state;

	public OptimumInventoryParallelWorkerTask(OptimumInventoryParallelBatchState state)
	{
		this.state = state;
	}

	public void Run()
	{
		state.ProcessLoop(true);
	}
}

internal sealed class OptimumInventoryLookaheadTask
{
	private readonly OptimumInventoryMeshPreparer preparer;

	private readonly List<OptimumInventoryPrepTarget> targets;

	private readonly int generation;

	private readonly int sequence;

	public OptimumInventoryLookaheadTask(
		OptimumInventoryMeshPreparer preparer,
		List<OptimumInventoryPrepTarget> targets,
		int generation,
		int sequence)
	{
		this.preparer = preparer;
		this.targets = targets;
		this.generation = generation;
		this.sequence = sequence;
	}

	public void Run()
	{
		preparer.RunBackgroundLookahead(targets, generation, sequence);
	}
}

internal sealed class OptimumInventoryMeshPreparer
{
	private const int MaxPreparedUnuploaded = 300;

	private const int MaxInitialCreativePrefetchSlots = 270;

	private const int MaxParallelVisibleWorkers = 4;

	private readonly ShapeTesselatorManager manager;

	private readonly ClientMain game;

	private readonly object stateLock = new object();

	private readonly object shapePinLock = new object();

	private readonly object voxelizeAssetLock = new object();

	private readonly Dictionary<UnloadableShape, int> pinnedShapes = new Dictionary<UnloadableShape, int>();

	private readonly List<OptimumInventoryPrepTarget> mainThreadVisibleMissing = new List<OptimumInventoryPrepTarget>(140);

	private readonly List<OptimumInventoryPrepTarget> mainThreadVisibleParallel = new List<OptimumInventoryPrepTarget>(140);

	private readonly List<OptimumInventoryPrepTarget> mainThreadVisibleSequential = new List<OptimumInventoryPrepTarget>(16);

	private readonly HashSet<long> mainThreadDedupKeys = new HashSet<long>(140);

	private readonly List<UnloadableShape> mainThreadPinnedShapes = new List<UnloadableShape>(64);

	private OptimumPreparedInventoryMesh?[]? preparedBlockMeshes;

	private OptimumPreparedInventoryMesh?[]? preparedItemMeshes;

	private OptimumPreparedInventoryMesh?[]?[]? preparedAltItemMeshes;

	private int unuploadedCount;

	private int generation;

	private int lookaheadSequence;

	private int lookaheadInFlight;

	private int disposed;

	private int bridgeRegistered;

	internal int ShapePinningModeOverride;

	private bool IsTemporaryShapePinningEnabled()
	{
		return ShapePinningModeOverride == 2 || (ShapePinningModeOverride == 0 && ClientSettings.OptimizeRamMode == 2);
	}

	public OptimumInventoryMeshPreparer(ShapeTesselatorManager manager, ClientMain game)
	{
		this.manager = manager;
		this.game = game;
	}

	internal void EnsureBridgeRegistered()
	{
		if (IsDisposed || game.api == null)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref bridgeRegistered, 1, 0) == 0)
		{
			OptimumApiBridge.RegisterInventoryMeshPreparer(game.api, new OptimumApiBridge.InventoryMeshWindowPreparerDelegate(manager.PrepareSlotGridWindow));
		}
	}

	internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

	internal void DisposeWorkerTesselator()
	{
		manager.ThreadDispose();
	}

	public void InvalidateAndClear()
	{
		Interlocked.Increment(ref generation);
		Interlocked.Increment(ref lookaheadSequence);
		lock (stateLock)
		{
			preparedBlockMeshes = null;
			preparedItemMeshes = null;
			preparedAltItemMeshes = null;
			Interlocked.Exchange(ref unuploadedCount, 0);
		}
	}

	public void ResetBlocks(int count)
	{
		Interlocked.Increment(ref generation);
		Interlocked.Increment(ref lookaheadSequence);
		lock (stateLock)
		{
			preparedBlockMeshes = count > 0 ? new OptimumPreparedInventoryMesh?[count] : null;
			RecountUnuploadedLocked();
		}
	}

	public void ResetItems(int count)
	{
		Interlocked.Increment(ref generation);
		Interlocked.Increment(ref lookaheadSequence);
		lock (stateLock)
		{
			preparedItemMeshes = count > 0 ? new OptimumPreparedInventoryMesh?[count] : null;
			preparedAltItemMeshes = count > 0 ? new OptimumPreparedInventoryMesh?[count][] : null;
			RecountUnuploadedLocked();
		}
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref disposed, 1);
		InvalidateAndClear();
	}

	private void RecountUnuploadedLocked()
	{
		int count = 0;
		OptimumPreparedInventoryMesh?[]? blocks = preparedBlockMeshes;
		if (blocks != null)
		{
			for (int i = 0; i < blocks.Length; i++)
			{
				if (blocks[i] != null)
				{
					count++;
				}
			}
		}
		OptimumPreparedInventoryMesh?[]? items = preparedItemMeshes;
		if (items != null)
		{
			for (int i = 0; i < items.Length; i++)
			{
				if (items[i] != null)
				{
					count++;
				}
			}
		}
		OptimumPreparedInventoryMesh?[]?[]? alts = preparedAltItemMeshes;
		if (alts != null)
		{
			for (int i = 0; i < alts.Length; i++)
			{
				OptimumPreparedInventoryMesh?[]? arr = alts[i];
				if (arr == null)
				{
					continue;
				}
				for (int j = 0; j < arr.Length; j++)
				{
					if (arr[j] != null)
					{
						count++;
					}
				}
			}
		}
		Interlocked.Exchange(ref unuploadedCount, count);
	}

	private void EnsureArraysAllocatedLocked()
	{
		MultiTextureMeshRef[]? blocks = manager.blockModelRefsInventory;
		if (blocks != null && (preparedBlockMeshes == null || preparedBlockMeshes.Length != blocks.Length))
		{
			preparedBlockMeshes = new OptimumPreparedInventoryMesh?[blocks.Length];
		}
		MultiTextureMeshRef[]? items = manager.itemModelRefsInventory;
		if (items != null && (preparedItemMeshes == null || preparedItemMeshes.Length != items.Length))
		{
			preparedItemMeshes = new OptimumPreparedInventoryMesh?[items.Length];
		}
		MultiTextureMeshRef[][]? alts = manager.altItemModelRefsInventory;
		if (alts != null && (preparedAltItemMeshes == null || preparedAltItemMeshes.Length != alts.Length))
		{
			preparedAltItemMeshes = new OptimumPreparedInventoryMesh?[alts.Length][];
		}
	}

	public void UnloadAllUnpinnedTemporaryShapes()
	{
		if (!IsTemporaryShapePinningEnabled() || manager.shapes == null)
		{
			return;
		}
		lock (shapePinLock)
		{
			foreach (KeyValuePair<AssetLocation, UnloadableShape> entry in manager.shapes)
			{
				UnloadableShape shape = entry.Value;
				if (shape != null && !shape.keepLoaded && !pinnedShapes.ContainsKey(shape))
				{
					shape.Unload();
				}
			}
		}
	}

	public void PinCompositeShape(CompositeShape? cshape, List<UnloadableShape> pinnedOut)
	{
		if (cshape == null || cshape.Format != EnumShapeFormat.VintageStory || !IsTemporaryShapePinningEnabled())
		{
			return;
		}
		lock (shapePinLock)
		{
			if (!cshape.VoxelizeTexture && cshape.Base != null)
			{
				PinShapeLocked(cshape.Base, pinnedOut);
			}
			if (cshape.Overlays != null)
			{
				for (int i = 0; i < cshape.Overlays.Length; i++)
				{
					CompositeShape overlay = cshape.Overlays[i];
					if (overlay != null && overlay.Format == EnumShapeFormat.VintageStory && !overlay.VoxelizeTexture && overlay.Base != null)
					{
						PinShapeLocked(overlay.Base, pinnedOut);
					}
				}
			}
		}
	}

	private void PinBatchShapes(List<OptimumInventoryPrepTarget> targets, List<UnloadableShape> pinnedOut)
	{
		if (!IsTemporaryShapePinningEnabled() || targets.Count == 0)
		{
			return;
		}
		lock (shapePinLock)
		{
			for (int i = 0; i < targets.Count; i++)
			{
				OptimumInventoryPrepTarget target = targets[i];
				CompositeShape? cshape = GetTargetCompositeShape(target);
				if (cshape == null || cshape.Format != EnumShapeFormat.VintageStory)
				{
					continue;
				}
				if (!cshape.VoxelizeTexture && cshape.Base != null)
				{
					PinShapeLocked(cshape.Base, pinnedOut);
				}
				if (cshape.Overlays != null)
				{
					for (int j = 0; j < cshape.Overlays.Length; j++)
					{
						CompositeShape overlay = cshape.Overlays[j];
						if (overlay != null && overlay.Format == EnumShapeFormat.VintageStory && !overlay.VoxelizeTexture && overlay.Base != null)
						{
							PinShapeLocked(overlay.Base, pinnedOut);
						}
					}
				}
			}
		}
	}

	private void PinShapeLocked(AssetLocation shapeLoc, List<UnloadableShape> pinnedOut)
	{
		Vintagestory.API.Datastructures.OrderedDictionary<AssetLocation, UnloadableShape>? shapes = manager.shapes;
		if (shapes == null)
		{
			return;
		}
		if (!shapes.TryGetValue(shapeLoc, out UnloadableShape? shape) || shape == null)
		{
			return;
		}
		if (!shape.Loaded)
		{
			shape.Load(game, new AssetLocationAndSource(shapeLoc));
		}
		if (!shape.keepLoaded && IsTemporaryShapePinningEnabled())
		{
			pinnedShapes.TryGetValue(shape, out int count);
			pinnedShapes[shape] = count + 1;
			pinnedOut.Add(shape);
		}
	}

	public void UnpinShapes(List<UnloadableShape>? pinned)
	{
		if (pinned == null || pinned.Count == 0)
		{
			return;
		}
		lock (shapePinLock)
		{
			for (int i = 0; i < pinned.Count; i++)
			{
				UnloadableShape shape = pinned[i];
				if (pinnedShapes.TryGetValue(shape, out int count))
				{
					if (count <= 1)
					{
						pinnedShapes.Remove(shape);
						if (!shape.keepLoaded && IsTemporaryShapePinningEnabled())
						{
							shape.Unload();
						}
					}
					else
					{
						pinnedShapes[shape] = count - 1;
					}
				}
			}
			pinned.Clear();
		}
	}

	internal OptimumPreparedInventoryMesh? TryConsumePreparedBlock(int blockId)
	{
		OptimumPreparedInventoryMesh?[]? blocks = Volatile.Read(ref preparedBlockMeshes);
		if (blocks == null || (uint)blockId >= (uint)blocks.Length)
		{
			return null;
		}
		OptimumPreparedInventoryMesh? prepared = Interlocked.Exchange(ref blocks[blockId], null);
		if (prepared == null)
		{
			return null;
		}
		Interlocked.Decrement(ref unuploadedCount);
		return prepared.Generation == Volatile.Read(ref generation) ? prepared : null;
	}

	internal OptimumPreparedInventoryMesh? TryConsumePreparedItem(int itemId, int renderVariant)
	{
		if (renderVariant == 0)
		{
			OptimumPreparedInventoryMesh?[]? items = Volatile.Read(ref preparedItemMeshes);
			if (items == null || (uint)itemId >= (uint)items.Length)
			{
				return null;
			}
			OptimumPreparedInventoryMesh? prepared = Interlocked.Exchange(ref items[itemId], null);
			if (prepared == null)
			{
				return null;
			}
			Interlocked.Decrement(ref unuploadedCount);
			return prepared.Generation == Volatile.Read(ref generation) ? prepared : null;
		}

		OptimumPreparedInventoryMesh?[]?[]? alts = Volatile.Read(ref preparedAltItemMeshes);
		if (alts == null || (uint)itemId >= (uint)alts.Length)
		{
			return null;
		}
		OptimumPreparedInventoryMesh?[]? altArr = Volatile.Read(ref alts[itemId]);
		int altIdx = renderVariant - 1;
		if (altArr == null || (uint)altIdx >= (uint)altArr.Length)
		{
			return null;
		}
		OptimumPreparedInventoryMesh? altPrepared = Interlocked.Exchange(ref altArr[altIdx], null);
		if (altPrepared == null)
		{
			return null;
		}
		Interlocked.Decrement(ref unuploadedCount);
		return altPrepared.Generation == Volatile.Read(ref generation) ? altPrepared : null;
	}

	private bool HasPreparedEntry(in OptimumInventoryPrepTarget target)
	{
		if (target.IsBlock)
		{
			OptimumPreparedInventoryMesh?[]? blocks = Volatile.Read(ref preparedBlockMeshes);
			return blocks != null && (uint)target.Id < (uint)blocks.Length && Volatile.Read(ref blocks[target.Id]) != null;
		}
		if (target.RenderVariant == 0)
		{
			OptimumPreparedInventoryMesh?[]? items = Volatile.Read(ref preparedItemMeshes);
			return items != null && (uint)target.Id < (uint)items.Length && Volatile.Read(ref items[target.Id]) != null;
		}
		OptimumPreparedInventoryMesh?[]?[]? alts = Volatile.Read(ref preparedAltItemMeshes);
		int altIdx = target.RenderVariant - 1;
		if (alts == null || (uint)target.Id >= (uint)alts.Length)
		{
			return false;
		}
		OptimumPreparedInventoryMesh?[]? arr = Volatile.Read(ref alts[target.Id]);
		return arr != null && (uint)altIdx < (uint)arr.Length && Volatile.Read(ref arr[altIdx]) != null;
	}

	private void PublishPrepared(in OptimumInventoryPrepTarget target, OptimumPreparedInventoryMesh prepared)
	{
		if (IsDisposed || Volatile.Read(ref generation) != prepared.Generation)
		{
			return;
		}
		if (target.IsBlock)
		{
			MultiTextureMeshRef[]? refs = manager.blockModelRefsInventory;
			if (refs != null && (uint)target.Id < (uint)refs.Length && Volatile.Read(ref refs[target.Id]) != null)
			{
				return;
			}
			OptimumPreparedInventoryMesh?[]? blocks = Volatile.Read(ref preparedBlockMeshes);
			if (blocks != null && (uint)target.Id < (uint)blocks.Length)
			{
				if (Interlocked.CompareExchange(ref blocks[target.Id], prepared, null) == null)
				{
					Interlocked.Increment(ref unuploadedCount);
				}
			}
			return;
		}

		if (target.RenderVariant == 0)
		{
			MultiTextureMeshRef[]? refs = manager.itemModelRefsInventory;
			if (refs != null && (uint)target.Id < (uint)refs.Length && Volatile.Read(ref refs[target.Id]) != null)
			{
				return;
			}
			OptimumPreparedInventoryMesh?[]? items = Volatile.Read(ref preparedItemMeshes);
			if (items != null && (uint)target.Id < (uint)items.Length)
			{
				if (Interlocked.CompareExchange(ref items[target.Id], prepared, null) == null)
				{
					Interlocked.Increment(ref unuploadedCount);
				}
			}
			return;
		}

		MultiTextureMeshRef[][]? altRefs = manager.altItemModelRefsInventory;
		int altIdx = target.RenderVariant - 1;
		if (altRefs != null && (uint)target.Id < (uint)altRefs.Length)
		{
			MultiTextureMeshRef[]? existingAlt = Volatile.Read(ref altRefs[target.Id]);
			if (existingAlt != null && (uint)altIdx < (uint)existingAlt.Length && Volatile.Read(ref existingAlt[altIdx]) != null)
			{
				return;
			}
		}

		OptimumPreparedInventoryMesh?[]?[]? alts = Volatile.Read(ref preparedAltItemMeshes);
		if (alts == null || (uint)target.Id >= (uint)alts.Length)
		{
			return;
		}
		CompositeShape[]? alternates = target.Item?.Shape?.BakedAlternates;
		if (alternates == null || (uint)altIdx >= (uint)alternates.Length)
		{
			return;
		}
		OptimumPreparedInventoryMesh?[]? altArr = Volatile.Read(ref alts[target.Id]);
		if (altArr == null)
		{
			OptimumPreparedInventoryMesh?[] created = new OptimumPreparedInventoryMesh?[alternates.Length];
			altArr = Interlocked.CompareExchange(ref alts[target.Id], created, null) ?? created;
		}
		if ((uint)altIdx < (uint)altArr.Length)
		{
			if (Interlocked.CompareExchange(ref altArr[altIdx], prepared, null) == null)
			{
				Interlocked.Increment(ref unuploadedCount);
			}
		}
	}

	public MultiTextureMeshRef UploadPreparedOnMainThread(OptimumPreparedInventoryMesh prepared, MultiTextureMeshRef fallbackUnknownRef)
	{
		if (prepared.IsUnknown)
		{
			return fallbackUnknownRef;
		}
		MeshData[]? parts = prepared.Parts;
		int[] textureIds = prepared.TextureIds ?? Array.Empty<int>();
		int partCount = parts != null ? parts.Length : 0;
		MeshRef[] meshRefs = partCount == 0 ? Array.Empty<MeshRef>() : new MeshRef[partCount];
		IRenderAPI renderApi = game.api.renderapi;
		for (int i = 0; i < partCount; i++)
		{
			meshRefs[i] = renderApi.UploadMesh(parts![i]);
		}
		return new MultiTextureMeshRef(meshRefs, textureIds);
	}

	private static int GetRenderVariantSafe(ItemStack stack, Item item)
	{
		int renderVariant = 0;
		if (stack.TempAttributes != null && stack.TempAttributes.HasAttribute("renderVariant"))
		{
			renderVariant = stack.TempAttributes.GetInt("renderVariant");
		}
		else if (stack.Attributes != null && stack.Attributes.HasAttribute("renderVariant"))
		{
			renderVariant = stack.Attributes.GetInt("renderVariant");
		}
		if (renderVariant != 0)
		{
			CompositeShape[]? alternates = item.Shape?.BakedAlternates;
			if (alternates == null || (uint)(renderVariant - 1) >= (uint)alternates.Length)
			{
				renderVariant = 0;
			}
		}
		return renderVariant;
	}

	private bool TryBuildMissingTarget(ItemStack? stack, out OptimumInventoryPrepTarget target)
	{
		target = default;
		if (stack == null)
		{
			return false;
		}
		if (stack.Class == EnumItemClass.Block)
		{
			Block? block = stack.Block;
			MultiTextureMeshRef[]? blockRefs = manager.blockModelRefsInventory;
			if (block == null || blockRefs == null)
			{
				return false;
			}
			int blockId = block.BlockId;
			if ((uint)blockId >= (uint)blockRefs.Length || Volatile.Read(ref blockRefs[blockId]) != null)
			{
				return false;
			}
			target = new OptimumInventoryPrepTarget
			{
				IsBlock = true,
				Id = blockId,
				RenderVariant = 0,
				Block = block,
				Item = null,
			};
			return true;
		}

		Item? item = stack.Item;
		MultiTextureMeshRef[]? itemRefs = manager.itemModelRefsInventory;
		if (item == null || itemRefs == null)
		{
			return false;
		}
		int itemId = item.ItemId;
		if ((uint)itemId >= (uint)itemRefs.Length)
		{
			return false;
		}
		int renderVariant = GetRenderVariantSafe(stack, item);
		if (renderVariant == 0)
		{
			if (Volatile.Read(ref itemRefs[itemId]) != null)
			{
				return false;
			}
		}
		else
		{
			MultiTextureMeshRef[][]? altRefs = manager.altItemModelRefsInventory;
			if (altRefs == null || (uint)itemId >= (uint)altRefs.Length)
			{
				return false;
			}
			MultiTextureMeshRef[]? arr = Volatile.Read(ref altRefs[itemId]);
			int altIdx = renderVariant - 1;
			if (arr != null && (uint)altIdx < (uint)arr.Length && Volatile.Read(ref arr[altIdx]) != null)
			{
				return false;
			}
		}

		target = new OptimumInventoryPrepTarget
		{
			IsBlock = false,
			Id = itemId,
			RenderVariant = renderVariant,
			Block = null,
			Item = item,
		};
		return true;
	}

	private static long GetTargetDedupKey(in OptimumInventoryPrepTarget target)
	{
		return ((long)(target.IsBlock ? 1 : 0) << 48) | ((long)(uint)target.RenderVariant << 32) | (uint)target.Id;
	}

	private static CompositeShape? GetTargetCompositeShape(in OptimumInventoryPrepTarget target)
	{
		if (target.IsBlock)
		{
			Block? block = target.Block;
			return block != null ? (block.ShapeInventory ?? block.Shape) : null;
		}
		Item? item = target.Item;
		if (item == null)
		{
			return null;
		}
		if (target.RenderVariant == 0)
		{
			return item.Shape;
		}
		CompositeShape[]? alternates = item.Shape?.BakedAlternates;
		int altIdx = target.RenderVariant - 1;
		return (alternates != null && (uint)altIdx < (uint)alternates.Length) ? alternates[altIdx] : null;
	}

	private static bool CanPrepareOffThread(in OptimumInventoryPrepTarget target)
	{
		CompositeShape? cshape = GetTargetCompositeShape(target);
		if (cshape != null && cshape.Format == EnumShapeFormat.GltfEmbedded && cshape.InsertBakedTextures)
		{
			return false;
		}
		return true;
	}

	private void PreloadVoxelizeTextureAssetsIfNeeded(in OptimumInventoryPrepTarget target)
	{
		if (target.IsBlock)
		{
			Block? block = target.Block;
			if (block != null && !block.IsMissing && block.Shape != null && block.Shape.VoxelizeTexture)
			{
				AssetLocation? bakedName = block.FirstTextureInventory?.Baked?.BakedName;
				if (bakedName != null)
				{
					PreloadCompositeTextureAssets(bakedName);
				}
			}
			return;
		}

		Item? item = target.Item;
		if (item == null || item.Code == null)
		{
			return;
		}
		CompositeShape? forShape = GetTargetCompositeShape(target);
		if (forShape == null || forShape.VoxelizeTexture)
		{
			CompositeTexture? tex = item.FirstTexture;
			if (forShape?.Base != null && item.Textures != null && item.Textures.TryGetValue(forShape.Base.ToShortString(), out CompositeTexture? overrideTex))
			{
				tex = overrideTex;
			}
			AssetLocation? bakedName = tex?.Baked?.BakedName;
			if (bakedName != null)
			{
				PreloadCompositeTextureAssets(bakedName);
			}
		}
	}

	private void PreloadCompositeTextureAssets(AssetLocation bakedName)
	{
		IAssetManager? assets = game.Platform?.AssetManager;
		if (assets == null)
		{
			return;
		}
		string path = bakedName.Path;
		lock (voxelizeAssetLock)
		{
			if (!path.Contains("++", StringComparison.Ordinal))
			{
				AssetLocation texLoc = TextureAtlasManager.ToTextureAssetLocation(new AssetLocationAndSource(bakedName));
				assets.TryGet(texLoc);
				return;
			}
			string full = bakedName.ToString();
			string[] parts = full.Split(new string[] { "++" }, StringSplitOptions.None);
			int rotation = TextureAtlasManager.getRotation(new AssetLocationAndSource(bakedName));
			for (int i = 0; i < parts.Length; i++)
			{
				string[] segments = parts[i].Split('~');
				string rawLoc = segments.Length > 1 ? segments[1] : segments[0];
				AssetLocation partLoc = AssetLocation.Create(rawLoc, bakedName.Domain);
				if (rotation != 0)
				{
					partLoc.WithPathAppendixOnce("@" + rotation);
				}
				AssetLocation texLoc = TextureAtlasManager.ToTextureAssetLocation(new AssetLocationAndSource(partLoc));
				assets.TryGet(texLoc);
			}
		}
	}

	internal OptimumPreparedInventoryMesh? PrepareTargetCpuOnly(in OptimumInventoryPrepTarget target, int batchGeneration)
	{
		if (IsDisposed || Volatile.Read(ref generation) != batchGeneration)
		{
			return null;
		}
		if (target.IsBlock)
		{
			Block? block = target.Block;
			if (block == null || block.Code == null || target.Id == 0)
			{
				return null;
			}
			if (block.IsMissing)
			{
				return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
			}
			try
			{
				PreloadVoxelizeTextureAssetsIfNeeded(target);
				MeshData data = manager.OptimumTesselateBlockForInventory(block);
				if (data == null || data == manager.unknownBlockModelData)
				{
					return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
				}
				return OptimumPreparedInventoryMesh.CreateFromMeshData(batchGeneration, data);
			}
			catch (Exception ex)
			{
				game.Platform.Logger.Error("Exception thrown during prepared inventory tesselation for block {0}:", block.Code);
				game.Platform.Logger.Error(ex);
				return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
			}
		}

		Item? item = target.Item;
		if (item == null)
		{
			return null;
		}
		if (target.RenderVariant == 0)
		{
			if (item.Code == null || ((item.FirstTexture == null || item.FirstTexture.Base.Path == "unknown") && item.Shape == null))
			{
				return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
			}
			try
			{
				PreloadVoxelizeTextureAssetsIfNeeded(target);
				manager.Tesselator.TesselateItem(item, item.Shape, out MeshData modeldata);
				if (modeldata == null || modeldata == manager.unknownItemModelData)
				{
					return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
				}
				return OptimumPreparedInventoryMesh.CreateFromMeshData(batchGeneration, modeldata);
			}
			catch (Exception ex)
			{
				game.Platform.Logger.Error("Exception thrown during prepared inventory tesselation for item {0}:", item.Code);
				game.Platform.Logger.Error(ex);
				return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
			}
		}

		CompositeShape[]? alternates = item.Shape?.BakedAlternates;
		int altIdx = target.RenderVariant - 1;
		if (alternates == null || (uint)altIdx >= (uint)alternates.Length)
		{
			return null;
		}
		try
		{
			PreloadVoxelizeTextureAssetsIfNeeded(target);
			manager.Tesselator.TesselateItem(item, alternates[altIdx], out MeshData altModeldata);
			if (altModeldata == null || altModeldata == manager.unknownItemModelData)
			{
				return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
			}
			return OptimumPreparedInventoryMesh.CreateFromMeshData(batchGeneration, altModeldata);
		}
		catch (Exception ex)
		{
			game.Platform.Logger.Error("Exception thrown during prepared inventory tesselation for item {0} variant {1}:", item.Code, target.RenderVariant);
			game.Platform.Logger.Error(ex);
			return OptimumPreparedInventoryMesh.CreateUnknown(batchGeneration);
		}
	}

	private void UploadPreparedResultForTarget(in OptimumInventoryPrepTarget target, OptimumPreparedInventoryMesh? prepared)
	{
		if (prepared == null)
		{
			return;
		}
		if (target.IsBlock)
		{
			MultiTextureMeshRef[]? refs = manager.blockModelRefsInventory;
			if (refs == null || (uint)target.Id >= (uint)refs.Length || Volatile.Read(ref refs[target.Id]) != null)
			{
				return;
			}
			manager.EnsureUnknownBlockModelRef();
			MultiTextureMeshRef uploaded = UploadPreparedOnMainThread(prepared, manager.unknownBlockModelRef);
			Volatile.Write(ref refs[target.Id], uploaded);
			return;
		}

		manager.EnsureUnknownItemModelRef();
		if (target.RenderVariant == 0)
		{
			MultiTextureMeshRef[]? refs = manager.itemModelRefsInventory;
			if (refs == null || (uint)target.Id >= (uint)refs.Length || Volatile.Read(ref refs[target.Id]) != null)
			{
				return;
			}
			MultiTextureMeshRef uploaded = UploadPreparedOnMainThread(prepared, manager.unknownItemModelRef);
			Volatile.Write(ref refs[target.Id], uploaded);
			return;
		}

		MultiTextureMeshRef[][]? altRefs = manager.altItemModelRefsInventory;
		CompositeShape[]? alternates = target.Item?.Shape?.BakedAlternates;
		int altIdx = target.RenderVariant - 1;
		if (altRefs == null || alternates == null || (uint)target.Id >= (uint)altRefs.Length || (uint)altIdx >= (uint)alternates.Length)
		{
			return;
		}
		MultiTextureMeshRef[]? arr = Volatile.Read(ref altRefs[target.Id]);
		if (arr == null)
		{
			arr = new MultiTextureMeshRef[alternates.Length];
			Volatile.Write(ref altRefs[target.Id], arr);
		}
		if (Volatile.Read(ref arr[altIdx]) == null)
		{
			MultiTextureMeshRef uploaded = UploadPreparedOnMainThread(prepared, manager.unknownItemModelRef);
			Volatile.Write(ref arr[altIdx], uploaded);
		}
	}

	public void PrepareSlotGridWindow(
		Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot> renderedSlots,
		int visibleStartIndex,
		int visibleCount,
		int lookaheadStartIndex,
		int lookaheadCount)
	{
		if (IsDisposed || !OptimumConfig.LazyInventoryMeshesEnabled || !OptimumConfig.LazyInventoryMeshPrepEnabled)
		{
			return;
		}
		if (manager.blockModelRefsInventory == null || manager.itemModelRefsInventory == null)
		{
			return;
		}
		int totalCount = renderedSlots.Count;
		int visibleEnd = Math.Min(totalCount, visibleStartIndex + visibleCount);
		mainThreadVisibleMissing.Clear();
		mainThreadDedupKeys.Clear();

		for (int i = visibleStartIndex; i < visibleEnd; i++)
		{
			ItemSlot? slot = renderedSlots.GetValueAtIndex(i);
			if (slot == null || !TryBuildMissingTarget(slot.Itemstack, out OptimumInventoryPrepTarget target))
			{
				continue;
			}
			OptimumPreparedInventoryMesh? prepared = target.IsBlock
				? TryConsumePreparedBlock(target.Id)
				: TryConsumePreparedItem(target.Id, target.RenderVariant);
			if (prepared != null)
			{
				UploadPreparedResultForTarget(target, prepared);
				OptimumDiagnostics.RecordInventoryMeshPreparedHit();
				continue;
			}
			long key = GetTargetDedupKey(target);
			if (mainThreadDedupKeys.Add(key))
			{
				mainThreadVisibleMissing.Add(target);
			}
		}

		if (mainThreadVisibleMissing.Count > 0)
		{
			PrepareVisibleMissingBatchOnMainThread(mainThreadVisibleMissing);
			mainThreadVisibleMissing.Clear();
			mainThreadDedupKeys.Clear();
		}

		if (lookaheadCount > 0 && Volatile.Read(ref lookaheadInFlight) == 0 && Volatile.Read(ref unuploadedCount) < MaxPreparedUnuploaded)
		{
			ScheduleLookaheadFromSlotGrid(renderedSlots, lookaheadStartIndex, lookaheadCount);
		}
	}

	private void PrepareVisibleMissingBatchOnMainThread(List<OptimumInventoryPrepTarget> missing)
	{
		Interlocked.Increment(ref lookaheadSequence);
		mainThreadVisibleParallel.Clear();
		mainThreadVisibleSequential.Clear();
		for (int i = 0; i < missing.Count; i++)
		{
			OptimumInventoryPrepTarget target = missing[i];
			if (CanPrepareOffThread(target))
			{
				mainThreadVisibleParallel.Add(target);
			}
			else
			{
				mainThreadVisibleSequential.Add(target);
			}
		}

		int currentGeneration = Volatile.Read(ref generation);
		mainThreadPinnedShapes.Clear();
		try
		{
			PinBatchShapes(missing, mainThreadPinnedShapes);
			for (int i = 0; i < mainThreadVisibleSequential.Count; i++)
			{
				OptimumInventoryPrepTarget target = mainThreadVisibleSequential[i];
				if (target.IsBlock)
				{
					manager.EnsureBlockInventoryMeshRefOnMainThread(target.Block!);
				}
				else
				{
					manager.EnsureItemInventoryMeshRefOnMainThread(target.Item!, target.RenderVariant);
				}
			}

			int parallelCount = mainThreadVisibleParallel.Count;
			if (parallelCount > 0)
			{
				for (int i = 0; i < parallelCount; i++)
				{
					PreloadVoxelizeTextureAssetsIfNeeded(mainThreadVisibleParallel[i]);
				}
				int workerCount = 0;
				if (parallelCount >= 4 && Environment.ProcessorCount > 1)
				{
					workerCount = Math.Min(MaxParallelVisibleWorkers, Math.Min(parallelCount / 2, Environment.ProcessorCount - 1));
				}
				if (workerCount == 0)
				{
					for (int i = 0; i < parallelCount; i++)
					{
						OptimumInventoryPrepTarget target = mainThreadVisibleParallel[i];
						OptimumPreparedInventoryMesh? prepared = PrepareTargetCpuOnly(target, currentGeneration);
						UploadPreparedResultForTarget(target, prepared);
					}
				}
				else
				{
					OptimumInventoryParallelBatchState state = new OptimumInventoryParallelBatchState(this, mainThreadVisibleParallel, currentGeneration, workerCount);
					try
					{
						for (int w = 0; w < workerCount; w++)
						{
							OptimumInventoryParallelWorkerTask workerTask = new OptimumInventoryParallelWorkerTask(state);
							TyronThreadPool.QueueTask(new Action(workerTask.Run), "OptimumInventoryMeshBatch");
						}
						state.ProcessLoop(false);
						state.DoneEvent.Wait();
					}
					finally
					{
						state.DoneEvent.Dispose();
					}
					for (int i = 0; i < parallelCount; i++)
					{
						UploadPreparedResultForTarget(mainThreadVisibleParallel[i], state.Results[i]);
					}
				}
			}
			OptimumDiagnostics.RecordInventoryMeshBatchPrep();
		}
		finally
		{
			UnpinShapes(mainThreadPinnedShapes);
			mainThreadVisibleParallel.Clear();
			mainThreadVisibleSequential.Clear();
		}
	}

	private void ScheduleLookaheadFromSlotGrid(
		Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot> renderedSlots,
		int lookaheadStartIndex,
		int lookaheadCount)
	{
		int end = Math.Min(renderedSlots.Count, lookaheadStartIndex + lookaheadCount);
		if (lookaheadStartIndex < 0 || lookaheadStartIndex >= end)
		{
			return;
		}
		lock (stateLock)
		{
			EnsureArraysAllocatedLocked();
		}
		List<OptimumInventoryPrepTarget>? targets = null;
		HashSet<long>? seen = null;
		for (int i = lookaheadStartIndex; i < end; i++)
		{
			ItemSlot? slot = renderedSlots.GetValueAtIndex(i);
			if (slot == null || !TryBuildMissingTarget(slot.Itemstack, out OptimumInventoryPrepTarget target))
			{
				continue;
			}
			if (!CanPrepareOffThread(target) || HasPreparedEntry(target))
			{
				continue;
			}
			seen ??= new HashSet<long>(Math.Min(lookaheadCount, 135));
			if (!seen.Add(GetTargetDedupKey(target)))
			{
				continue;
			}
			targets ??= new List<OptimumInventoryPrepTarget>(Math.Min(lookaheadCount, 135));
			targets.Add(target);
		}
		if (targets == null || targets.Count == 0)
		{
			return;
		}
		QueueBackgroundLookahead(targets);
	}

	public void BeginInitialPrefetch()
	{
		if (IsDisposed || !OptimumConfig.LazyInventoryMeshesEnabled || !OptimumConfig.LazyInventoryMeshPrepEnabled)
		{
			return;
		}
		if (manager.blockModelRefsInventory == null || manager.itemModelRefsInventory == null)
		{
			return;
		}
		IPlayerInventoryManager? invMgr = game.player?.inventoryMgr;
		if (invMgr == null)
		{
			return;
		}
		lock (stateLock)
		{
			EnsureArraysAllocatedLocked();
		}

		List<OptimumInventoryPrepTarget> targets = new List<OptimumInventoryPrepTarget>(280);
		HashSet<long> seen = new HashSet<long>(280);
		CollectInventoryTargets(invMgr.GetOwnInventory("hotbar"), 64, targets, seen);
		CollectInventoryTargets(invMgr.GetOwnInventory("backpack"), 64, targets, seen);
		CollectInventoryTargets(invMgr.GetOwnInventory("craftinggrid"), 16, targets, seen);
		CollectInventoryTargets(invMgr.GetOwnInventory("character"), 32, targets, seen);
		if (game.player?.worlddata?.CurrentGameMode == EnumGameMode.Creative)
		{
			CollectInventoryTargets(invMgr.GetOwnInventory("creative"), MaxInitialCreativePrefetchSlots, targets, seen);
		}

		if (targets.Count > 0)
		{
			QueueBackgroundLookahead(targets);
		}
	}

	private void CollectInventoryTargets(
		IInventory? inventory,
		int maxNonEmptySlots,
		List<OptimumInventoryPrepTarget> targets,
		HashSet<long> seen)
	{
		if (inventory == null || maxNonEmptySlots <= 0)
		{
			return;
		}
		int remainingBudget = MaxPreparedUnuploaded - Volatile.Read(ref unuploadedCount) - targets.Count;
		if (remainingBudget <= 0)
		{
			return;
		}
		int limit = Math.Min(maxNonEmptySlots, remainingBudget);
		int added = 0;
		int slotCount = inventory.Count;
		for (int i = 0; i < slotCount && added < limit; i++)
		{
			ItemSlot? slot = inventory[i];
			if (slot == null || !TryBuildMissingTarget(slot.Itemstack, out OptimumInventoryPrepTarget target))
			{
				continue;
			}
			if (!CanPrepareOffThread(target) || HasPreparedEntry(target))
			{
				continue;
			}
			if (seen.Add(GetTargetDedupKey(target)))
			{
				targets.Add(target);
				added++;
			}
		}
	}

	private void QueueBackgroundLookahead(List<OptimumInventoryPrepTarget> targets)
	{
		if (Interlocked.CompareExchange(ref lookaheadInFlight, 1, 0) != 0)
		{
			return;
		}
		int currentGen = Volatile.Read(ref generation);
		int seq = Interlocked.Increment(ref lookaheadSequence);
		OptimumInventoryLookaheadTask task = new OptimumInventoryLookaheadTask(this, targets, currentGen, seq);
		TyronThreadPool.QueueTask(new Action(task.Run), "OptimumInventoryMeshLookahead");
	}

	internal void RunBackgroundLookahead(List<OptimumInventoryPrepTarget> targets, int batchGeneration, int sequence)
	{
		List<UnloadableShape>? pinned = null;
		try
		{
			if (IsDisposed || Volatile.Read(ref generation) != batchGeneration || Volatile.Read(ref lookaheadSequence) != sequence)
			{
				return;
			}
			pinned = new List<UnloadableShape>(64);
			PinBatchShapes(targets, pinned);
			for (int i = 0; i < targets.Count; i++)
			{
				if (IsDisposed || Volatile.Read(ref generation) != batchGeneration || Volatile.Read(ref lookaheadSequence) != sequence)
				{
					break;
				}
				if (Volatile.Read(ref unuploadedCount) >= MaxPreparedUnuploaded)
				{
					break;
				}
				OptimumInventoryPrepTarget target = targets[i];
				OptimumPreparedInventoryMesh? prepared = PrepareTargetCpuOnly(target, batchGeneration);
				if (prepared != null)
				{
					PublishPrepared(target, prepared);
					OptimumDiagnostics.RecordInventoryMeshLookaheadPrep();
				}
			}
		}
		finally
		{
			UnpinShapes(pinned);
			DisposeWorkerTesselator();
			Interlocked.Exchange(ref lookaheadInFlight, 0);
		}
	}
}
