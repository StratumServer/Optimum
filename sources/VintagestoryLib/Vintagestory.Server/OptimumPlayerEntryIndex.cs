using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Vintagestory.Server;

/// <summary>
/// Secondary hash index over a <see cref="List{T}"/> of <see cref="PlayerEntry"/> items so
/// ban and whitelist lookups run in O(1) while preserving the underlying list as the source of truth
/// and matching vanilla first-hit order across UID, null-UID, and name-only queries.
/// </summary>
internal sealed class OptimumPlayerEntryIndex
{
	private readonly struct IndexedEntry
	{
		public readonly PlayerEntry Entry;

		public readonly int Order;

		public IndexedEntry(PlayerEntry entry, int order)
		{
			Entry = entry;
			Order = order;
		}
	}

	private static readonly Func<List<PlayerEntry>, int> ReadListVersion = CreateListVersionReader();

	private readonly Dictionary<string, IndexedEntry> byUid = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);

	private readonly Dictionary<string, IndexedEntry> byName = new Dictionary<string, IndexedEntry>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, IndexedEntry> nullUidByName = new Dictionary<string, IndexedEntry>(StringComparer.OrdinalIgnoreCase);

	private List<PlayerEntry> syncedList;

	private int syncedCount = -1;

	private int syncedVersion = -1;

	private int nextOrder;

	private IndexedEntry firstNullName;

	private IndexedEntry firstNullUidAndNullName;

	private bool hasDuplicateKeys;

	public PlayerEntry FindEntry(List<PlayerEntry> list, string playeruid, string playername)
	{
		if (list == null || list.Count == 0)
		{
			return null;
		}

		EnsureSynced(list);

		if (playeruid == null)
		{
			return FindByName(playername);
		}

		bool hasUidMatch = byUid.TryGetValue(playeruid, out IndexedEntry uidMatch);
		bool hasNullUidMatch = TryFindNullUidByName(playername, out IndexedEntry nullUidMatch);

		if (hasUidMatch && hasNullUidMatch)
		{
			return uidMatch.Order <= nullUidMatch.Order ? uidMatch.Entry : nullUidMatch.Entry;
		}

		if (hasUidMatch)
		{
			return uidMatch.Entry;
		}

		return hasNullUidMatch ? nullUidMatch.Entry : null;
	}

	public void Rebuild(List<PlayerEntry> list)
	{
		ClearDictionaries();
		syncedList = list;
		if (list == null)
		{
			syncedCount = -1;
			syncedVersion = -1;
			return;
		}

		int count = list.Count;
		for (int i = 0; i < count; i++)
		{
			IndexEntry(list[i], nextOrder++);
		}

		syncedCount = count;
		syncedVersion = GetVersion(list);
	}

	public void NotifyAdded(List<PlayerEntry> list, PlayerEntry entry)
	{
		if (list == null || entry == null)
		{
			Invalidate();
			return;
		}

		if (!object.ReferenceEquals(list, syncedList) || list.Count != syncedCount + 1)
		{
			Rebuild(list);
			return;
		}

		IndexEntry(entry, nextOrder++);
		syncedCount = list.Count;
		syncedVersion = GetVersion(list);
	}

	public void NotifyRemoved(List<PlayerEntry> list, PlayerEntry entry)
	{
		if (list == null || entry == null || hasDuplicateKeys || !object.ReferenceEquals(list, syncedList) || list.Count != syncedCount - 1)
		{
			Invalidate();
			return;
		}

		RemoveIfSame(byUid, entry.PlayerUID, entry);

		if (entry.PlayerName != null)
		{
			RemoveIfSame(byName, entry.PlayerName, entry);
			if (entry.PlayerUID == null)
			{
				RemoveIfSame(nullUidByName, entry.PlayerName, entry);
			}
		}
		else
		{
			if (object.ReferenceEquals(firstNullName.Entry, entry))
			{
				firstNullName = default;
			}
			if (entry.PlayerUID == null && object.ReferenceEquals(firstNullUidAndNullName.Entry, entry))
			{
				firstNullUidAndNullName = default;
			}
		}

		syncedCount = list.Count;
		syncedVersion = GetVersion(list);
	}

	public void NotifyUidUpdated(List<PlayerEntry> list, PlayerEntry entry, string previousUid)
	{
		if (list == null || entry == null || hasDuplicateKeys || !object.ReferenceEquals(list, syncedList) || list.Count != syncedCount)
		{
			Invalidate();
			return;
		}

		int preservedOrder = ResolveExistingOrder(entry, previousUid);
		if (preservedOrder < 0)
		{
			Rebuild(list);
			return;
		}

		if (previousUid != null)
		{
			RemoveIfSame(byUid, previousUid, entry);
		}
		else if (entry.PlayerName != null)
		{
			RemoveIfSame(nullUidByName, entry.PlayerName, entry);
		}
		else if (object.ReferenceEquals(firstNullUidAndNullName.Entry, entry))
		{
			firstNullUidAndNullName = default;
		}

		if (entry.PlayerUID != null)
		{
			if (!byUid.TryAdd(entry.PlayerUID, new IndexedEntry(entry, preservedOrder)))
			{
				hasDuplicateKeys = true;
			}
		}
		else
		{
			IndexNullUidName(entry, preservedOrder);
		}
	}

	private void EnsureSynced(List<PlayerEntry> list)
	{
		if (!object.ReferenceEquals(list, syncedList) || list.Count != syncedCount || GetVersion(list) != syncedVersion)
		{
			Rebuild(list);
		}
	}

	private static int GetVersion(List<PlayerEntry> list)
	{
		return ReadListVersion != null ? ReadListVersion(list) : list.Count;
	}

	private static Func<List<PlayerEntry>, int> CreateListVersionReader()
	{
		FieldInfo versionField = typeof(List<PlayerEntry>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);
		if (versionField == null)
		{
			return null;
		}

		ParameterExpression listParam = Expression.Parameter(typeof(List<PlayerEntry>), "list");
		MemberExpression fieldAccess = Expression.Field(listParam, versionField);
		return Expression.Lambda<Func<List<PlayerEntry>, int>>(fieldAccess, listParam).Compile();
	}

	private void Invalidate()
	{
		syncedCount = -1;
		syncedVersion = -1;
	}

	private void ClearDictionaries()
	{
		byUid.Clear();
		byName.Clear();
		nullUidByName.Clear();
		nextOrder = 0;
		firstNullName = default;
		firstNullUidAndNullName = default;
		hasDuplicateKeys = false;
	}

	private PlayerEntry FindByName(string playername)
	{
		if (playername == null)
		{
			return firstNullName.Entry;
		}

		return byName.TryGetValue(playername, out IndexedEntry match) ? match.Entry : null;
	}

	private bool TryFindNullUidByName(string playername, out IndexedEntry match)
	{
		if (playername == null)
		{
			match = firstNullUidAndNullName;
			return match.Entry != null;
		}

		return nullUidByName.TryGetValue(playername, out match);
	}

	private void IndexEntry(PlayerEntry entry, int order)
	{
		if (entry == null)
		{
			return;
		}

		if (entry.PlayerUID != null)
		{
			TryRecord(byUid, entry.PlayerUID, entry, order);
		}
		else
		{
			IndexNullUidName(entry, order);
		}

		if (entry.PlayerName != null)
		{
			TryRecord(byName, entry.PlayerName, entry, order);
		}
		else if (firstNullName.Entry == null)
		{
			firstNullName = new IndexedEntry(entry, order);
		}
		else
		{
			hasDuplicateKeys = true;
		}
	}

	private void IndexNullUidName(PlayerEntry entry, int order)
	{
		if (entry.PlayerName != null)
		{
			TryRecord(nullUidByName, entry.PlayerName, entry, order);
		}
		else if (firstNullUidAndNullName.Entry == null)
		{
			firstNullUidAndNullName = new IndexedEntry(entry, order);
		}
		else
		{
			hasDuplicateKeys = true;
		}
	}

	private void TryRecord(Dictionary<string, IndexedEntry> index, string key, PlayerEntry entry, int order)
	{
		if (!index.TryAdd(key, new IndexedEntry(entry, order)))
		{
			hasDuplicateKeys = true;
		}
	}

	private static void RemoveIfSame(Dictionary<string, IndexedEntry> index, string key, PlayerEntry entry)
	{
		if (key != null && index.TryGetValue(key, out IndexedEntry existing) && object.ReferenceEquals(existing.Entry, entry))
		{
			index.Remove(key);
		}
	}

	private int ResolveExistingOrder(PlayerEntry entry, string previousUid)
	{
		if (previousUid != null && byUid.TryGetValue(previousUid, out IndexedEntry uidMatch) && object.ReferenceEquals(uidMatch.Entry, entry))
		{
			return uidMatch.Order;
		}

		if (entry.PlayerName != null && byName.TryGetValue(entry.PlayerName, out IndexedEntry nameMatch) && object.ReferenceEquals(nameMatch.Entry, entry))
		{
			return nameMatch.Order;
		}

		if (entry.PlayerName == null && object.ReferenceEquals(firstNullName.Entry, entry))
		{
			return firstNullName.Order;
		}

		return -1;
	}
}
