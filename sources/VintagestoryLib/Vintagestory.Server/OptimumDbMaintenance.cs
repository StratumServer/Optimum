using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;

namespace Vintagestory.Server;

internal sealed class OptimumDbMaintenance
{
	private const long ChunkCoordMask = 4194303L;

	private const long DimLowBitsMask = 130023424L;

	private static readonly FieldInfo s_gameDbConnField = typeof(GameDatabase).GetField("conn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

	private static readonly FieldInfo s_sqliteConnField = typeof(SQLiteDBConnection).GetField("sqliteConn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

	private readonly ServerMain server;

	private readonly ChunkServerThread chunkthread;

	private readonly IServerPlayer logToPlayer;

	private readonly int threshold;

	private readonly bool dryRun;

	private readonly bool keepClaims;

	private readonly string aboveVersion;

	private readonly int chunksize;

	private readonly List<LandClaim> claims;

	private readonly HashSet<FastVec2i> toDelete;

	private readonly HashSet<FastVec2i> toKeep;

	private readonly HorRectanglei rect;

	private byte[] lastVersionBytes;

	private int lastVersionLen;

	private string lastVersionStr;

	private bool lastVersionIsLower;

	public OptimumDbMaintenance(
		ServerMain server,
		ChunkServerThread chunkthread,
		IServerPlayer logToPlayer,
		int threshold,
		bool dryRun,
		bool keepClaims,
		string aboveVersion,
		int chunksize,
		List<LandClaim> claims,
		HashSet<FastVec2i> toDelete,
		HashSet<FastVec2i> toKeep)
	{
		this.server = server;
		this.chunkthread = chunkthread;
		this.logToPlayer = logToPlayer;
		this.threshold = threshold;
		this.dryRun = dryRun;
		this.keepClaims = keepClaims;
		this.aboveVersion = aboveVersion;
		this.chunksize = chunksize;
		this.claims = claims;
		this.toDelete = toDelete;
		this.toKeep = toKeep;
		rect = new HorRectanglei();
	}

	public static TextCommandResult StartPrune(
		ServerMain server,
		ChunkServerThread chunkthread,
		IServerPlayer logToPlayer,
		int threshold,
		bool dryRun,
		bool keepClaims,
		string aboveVersion)
	{
		HashSet<FastVec2i> toDelete = new HashSet<FastVec2i>();
		HashSet<FastVec2i> toKeep = new HashSet<FastVec2i>();
		List<LandClaim> claims = server.WorldMap.All;
		int chunksize = server.api.worldapi.ChunkSize;
		OptimumDbMaintenance job = new OptimumDbMaintenance(
			server,
			chunkthread,
			logToPlayer,
			threshold,
			dryRun,
			keepClaims,
			aboveVersion,
			chunksize,
			claims,
			toDelete,
			toKeep);
		TyronThreadPool.QueueTask(new Action(job.RunPruneBackground), "supplychunkcommand");
		return TextCommandResult.Success(dryRun ? Lang.Get("Dry run prune started, this may take some time.") : Lang.Get("Prune started, this may take some time."));
	}

	public void RunPruneBackground()
	{
		if (OptimumConfig.BatchedDbMaintenanceEnabled)
		{
			ScanGameDatabaseForPrune(chunkthread.gameDatabase, chunksize, threshold, keepClaims, aboveVersion, claims, toDelete, toKeep);
		}
		else
		{
			chunkthread.gameDatabase.ForAllChunks(new Action<DbChunk>(OnLegacyChunk));
			foreach (FastVec2i item in toKeep)
			{
				toDelete.Remove(item);
			}
		}
		server.EnqueueMainThreadTask(new Action(RunPruneMainThread));
	}

	public void OnLegacyChunk(DbChunk schunk)
	{
		if (keepClaims)
		{
			bool hasClaim = false;
			rect.X1 = schunk.Position.X * chunksize;
			rect.Z1 = schunk.Position.Z * chunksize;
			rect.X2 = schunk.Position.X * chunksize + chunksize;
			rect.Z2 = schunk.Position.Z * chunksize + chunksize;
			if (claims != null)
			{
				foreach (LandClaim claim in claims)
				{
					if (claim != null && claim.Intersects2d(rect))
					{
						hasClaim = true;
						break;
					}
				}
			}
			if (hasClaim)
			{
				toKeep.Add(new FastVec2i(schunk.Position.X, schunk.Position.Z));
				return;
			}
		}
		bool keep = false;
		if (threshold > 0 || aboveVersion != null)
		{
			ServerChunk serverChunk;
			using (MemoryStream memoryStream = new MemoryStream(schunk.Data))
			{
				serverChunk = Serializer.Deserialize<ServerChunk>((Stream)memoryStream);
			}
			keep = serverChunk.BlocksRemoved + serverChunk.BlocksPlaced >= threshold && threshold > 0;
			if (aboveVersion != null && GameVersion.IsLowerVersionThan(serverChunk.GameVersionCreated, aboveVersion))
			{
				keep = true;
			}
		}
		if (keep)
		{
			toKeep.Add(new FastVec2i(schunk.Position.X, schunk.Position.Z));
		}
		else
		{
			toDelete.Add(new FastVec2i(schunk.Position.X, schunk.Position.Z));
		}
	}

	public void OnOptimizedFallbackChunk(DbChunk schunk)
	{
		FastVec2i col = new FastVec2i(schunk.Position.X, schunk.Position.Z);
		if (toKeep.Contains(col))
		{
			return;
		}
		if (keepClaims && !toDelete.Contains(col) && claims != null && claims.Count > 0)
		{
			if (ColumnHasClaim(schunk.Position.X, schunk.Position.Z, chunksize, claims, rect))
			{
				toKeep.Add(col);
				return;
			}
		}
		bool needPayload = threshold > 0 || aboveVersion != null;
		if (!needPayload)
		{
			toDelete.Add(col);
			return;
		}
		if (ShouldKeepChunk(schunk.Data, threshold, aboveVersion, ref lastVersionBytes, ref lastVersionLen, ref lastVersionStr, ref lastVersionIsLower))
		{
			toKeep.Add(col);
		}
		else
		{
			toDelete.Add(col);
		}
	}

	public void RunPruneMainThread()
	{
		if (dryRun)
		{
			NotifyIndirect(server, logToPlayer, Lang.Get("Dry run prune complete. With a {0} block edits threshold, {1} chunk columns can be removed, {2} chunk columns would be kept.", threshold, toDelete.Count, toKeep.Count));
			return;
		}

		server.api.Server.PauseThread("chunkdbthread");
		int regionChunkSize = server.api.worldapi.RegionSize / chunksize;
		Cuboidi cuboidi = new Cuboidi();
		FastMemoryStream ms = new FastMemoryStream();

		if (OptimumConfig.BatchedDbMaintenanceEnabled)
		{
			int deleteCount = toDelete.Count;
			if (deleteCount > 0)
			{
				FastVec2i[] cols = new FastVec2i[deleteCount];
				long[] regionKeys = new long[deleteCount];
				int idx = 0;
				foreach (FastVec2i col in toDelete)
				{
					cols[idx] = col;
					regionKeys[idx] = server.WorldMap.MapRegionIndex2D(col.X / regionChunkSize, col.Y / regionChunkSize);
					idx++;
				}
				Array.Sort(regionKeys, cols);

				List<DbChunk> dirtyRegions = new List<DbChunk>();
				long currentRegIndex2d = long.MinValue;
				ServerMapRegion currentRegion = null;
				ServerMapRegion currentUnloadedRegion = null;

				for (int i = 0; i < deleteCount; i++)
				{
					FastVec2i item4 = cols[i];
					long regIndex2d = regionKeys[i];
					if (regIndex2d != currentRegIndex2d)
					{
						if (currentUnloadedRegion != null)
						{
							dirtyRegions.Add(new DbChunk
							{
								Position = server.WorldMap.MapRegionPosFromIndex2D(currentRegIndex2d),
								Data = currentUnloadedRegion.ToBytes(ms)
							});
							currentUnloadedRegion = null;
						}
						currentRegIndex2d = regIndex2d;
						int regX = item4.X / regionChunkSize;
						int regZ = item4.Y / regionChunkSize;
						currentRegion = server.WorldMap.GetMapRegion(regX, regZ) as ServerMapRegion;
						if (currentRegion == null)
						{
							byte[] mapRegionBytes = chunkthread.gameDatabase.GetMapRegion(regX, regZ);
							if (mapRegionBytes != null)
							{
								currentUnloadedRegion = ServerMapRegion.FromBytes(mapRegionBytes);
								currentRegion = currentUnloadedRegion;
							}
						}
					}

					List<GeneratedStructure> structures = currentRegion?.GeneratedStructures;
					if (structures != null && structures.Count > 0)
					{
						cuboidi.X1 = item4.X * chunksize;
						cuboidi.Z1 = item4.Y * chunksize;
						cuboidi.X2 = cuboidi.X1 + chunksize;
						cuboidi.Z2 = cuboidi.Z1 + chunksize;
						for (int s = structures.Count - 1; s >= 0; s--)
						{
							GeneratedStructure gs = structures[s];
							if (cuboidi.Contains(gs.Location.Start.X, gs.Location.Start.Z))
							{
								structures.RemoveAt(s);
							}
						}
					}

					server.api.WorldManager.DeleteChunkColumn(item4.X, item4.Y);
				}

				if (currentUnloadedRegion != null)
				{
					dirtyRegions.Add(new DbChunk
					{
						Position = server.WorldMap.MapRegionPosFromIndex2D(currentRegIndex2d),
						Data = currentUnloadedRegion.ToBytes(ms)
					});
				}
				chunkthread.gameDatabase.SetMapRegions(dirtyRegions);
			}
			else
			{
				chunkthread.gameDatabase.SetMapRegions(new List<DbChunk>(0));
			}
		}
		else
		{
			Dictionary<long, ServerMapRegion> dictionary = new Dictionary<long, ServerMapRegion>(10);
			Queue<long> queue = new Queue<long>(10);
			foreach (FastVec2i item4 in toDelete)
			{
				int num2 = item4.X / regionChunkSize;
				int num3 = item4.Y / regionChunkSize;
				ServerMapRegion value = server.WorldMap.GetMapRegion(num2, num3) as ServerMapRegion;
				long num4 = server.WorldMap.MapRegionIndex2D(num2, num3);
				if (value == null && !dictionary.TryGetValue(num4, out value))
				{
					byte[] mapRegion = chunkthread.gameDatabase.GetMapRegion(num2, num3);
					if (mapRegion != null)
					{
						if (queue.Count >= 9)
						{
							long num5 = queue.Dequeue();
							ServerMapRegion serverMapRegion = dictionary[num5];
							DbChunk item = new DbChunk
							{
								Position = server.WorldMap.MapRegionPosFromIndex2D(num5),
								Data = serverMapRegion.ToBytes(ms)
							};
							chunkthread.gameDatabase.SetMapRegions(new List<DbChunk> { item });
							dictionary.Remove(num5);
						}
						value = (dictionary[num4] = ServerMapRegion.FromBytes(mapRegion));
						queue.Enqueue(num4);
					}
				}
				List<GeneratedStructure> list = new List<GeneratedStructure>();
				cuboidi.X1 = item4.X * chunksize;
				cuboidi.Z1 = item4.Y * chunksize;
				cuboidi.X2 = item4.X * chunksize + chunksize;
				cuboidi.Z2 = item4.Y * chunksize + chunksize;
				if (value?.GeneratedStructures != null)
				{
					foreach (GeneratedStructure generatedStructure in value.GeneratedStructures)
					{
						if (cuboidi.Contains(generatedStructure.Location.Start.X, generatedStructure.Location.Start.Z))
						{
							list.Add(generatedStructure);
						}
					}
					foreach (GeneratedStructure item5 in list)
					{
						value.GeneratedStructures.Remove(item5);
					}
				}
				server.api.WorldManager.DeleteChunkColumn(item4.X, item4.Y);
			}
			List<DbChunk> remainingRegions = new List<DbChunk>(dictionary.Count);
			foreach (KeyValuePair<long, ServerMapRegion> r in dictionary)
			{
				remainingRegions.Add(new DbChunk
				{
					Position = server.WorldMap.MapRegionPosFromIndex2D(r.Key),
					Data = r.Value.ToBytes(ms)
				});
			}
			chunkthread.gameDatabase.SetMapRegions(remainingRegions);
		}

		server.api.Server.ResumeThread("chunkdbthread");
		NotifyIndirect(server, logToPlayer, Lang.Get("Prune complete, {1} chunk columns were removed, {2} chunk columns were kept.", threshold, toDelete.Count, toKeep.Count));
	}

	private static void NotifyIndirect(ServerMain server, IServerPlayer logToPlayer, string msg)
	{
		if (logToPlayer != null)
		{
			logToPlayer.SendMessage(server.IsDedicatedServer ? GlobalConstants.ServerInfoChatGroup : GlobalConstants.GeneralChatGroup, msg, EnumChatType.CommandSuccess, "backupdone");
		}
		else
		{
			ServerMain.Logger.Notification(msg);
		}
	}

	public static void ScanGameDatabaseForPrune(
		GameDatabase gameDatabase,
		int chunksize,
		int threshold,
		bool keepClaims,
		string aboveVersion,
		List<LandClaim> claims,
		HashSet<FastVec2i> toDelete,
		HashSet<FastVec2i> toKeep)
	{
		if (s_gameDbConnField?.GetValue(gameDatabase) is SQLiteDbConnectionv2 sqliteDb)
		{
			ScanSqliteDatabaseForPrune(sqliteDb, chunksize, threshold, keepClaims, aboveVersion, claims, toDelete, toKeep);
			return;
		}
		OptimumDbMaintenance fallback = new OptimumDbMaintenance(null, null, null, threshold, dryRun: true, keepClaims, aboveVersion, chunksize, claims, toDelete, toKeep);
		gameDatabase.ForAllChunks(new Action<DbChunk>(fallback.OnOptimizedFallbackChunk));
		foreach (FastVec2i item in toKeep)
		{
			toDelete.Remove(item);
		}
	}

	public static void ScanSqliteDatabaseForPrune(
		SQLiteDbConnectionv2 sqliteDb,
		int chunksize,
		int threshold,
		bool keepClaims,
		string aboveVersion,
		List<LandClaim> claims,
		HashSet<FastVec2i> toDelete,
		HashSet<FastVec2i> toKeep)
	{
		SqliteConnection conn = s_sqliteConnField?.GetValue(sqliteDb) as SqliteConnection;
		if (conn == null)
		{
			OptimumDbMaintenance fallback = new OptimumDbMaintenance(null, null, null, threshold, dryRun: true, keepClaims, aboveVersion, chunksize, claims, toDelete, toKeep);
			sqliteDb.ForAllChunks(new Action<DbChunk>(fallback.OnOptimizedFallbackChunk));
			foreach (FastVec2i item in toKeep)
			{
				toDelete.Remove(item);
			}
			return;
		}

		bool needPayload = threshold > 0 || aboveVersion != null;
		bool hasClaims = keepClaims && claims != null && claims.Count > 0;
		HorRectanglei rect = new HorRectanglei();
		byte[] lastVerBytes = null;
		int lastVerLen = 0;
		string lastVerStr = null;
		bool lastVerLower = false;

		SqliteCommand cmd = conn.CreateCommand();
		try
		{
			((DbCommand)(object)cmd).CommandText = needPayload ? "SELECT position, data FROM chunk" : "SELECT position FROM chunk";
			SqliteDataReader reader = cmd.ExecuteReader();
			try
			{
				DbDataReader dbReader = (DbDataReader)(object)reader;
				while (dbReader.Read())
				{
					ChunkPos pos = ChunkPos.FromChunkIndex_saveGamev2((ulong)dbReader.GetInt64(0));
					FastVec2i col = new FastVec2i(pos.X, pos.Z);
					if (toKeep.Contains(col))
					{
						continue;
					}
					if (!needPayload && toDelete.Contains(col))
					{
						continue;
					}
					if (keepClaims && !toDelete.Contains(col) && hasClaims)
					{
						if (ColumnHasClaim(pos.X, pos.Z, chunksize, claims, rect))
						{
							toKeep.Add(col);
							continue;
						}
					}
					if (!needPayload)
					{
						toDelete.Add(col);
						continue;
					}
					byte[] rawData = dbReader.IsDBNull(1) ? null : (dbReader.GetValue(1) as byte[]);
					if (ShouldKeepChunk(rawData, threshold, aboveVersion, ref lastVerBytes, ref lastVerLen, ref lastVerStr, ref lastVerLower))
					{
						toKeep.Add(col);
					}
					else
					{
						toDelete.Add(col);
					}
				}
			}
			finally
			{
				((IDisposable)reader)?.Dispose();
			}
		}
		finally
		{
			((IDisposable)cmd)?.Dispose();
		}

		foreach (FastVec2i kept in toKeep)
		{
			toDelete.Remove(kept);
		}
	}

	private static bool ColumnHasClaim(int chunkX, int chunkZ, int chunksize, List<LandClaim> claims, HorRectanglei rect)
	{
		rect.X1 = chunkX * chunksize;
		rect.Z1 = chunkZ * chunksize;
		rect.X2 = rect.X1 + chunksize;
		rect.Z2 = rect.Z1 + chunksize;
		for (int i = 0; i < claims.Count; i++)
		{
			LandClaim claim = claims[i];
			if (claim != null && claim.Intersects2d(rect))
			{
				return true;
			}
		}
		return false;
	}

	public static bool ShouldKeepChunk(
		byte[] rawData,
		int threshold,
		string aboveVersion,
		ref byte[] lastVersionBytes,
		ref int lastVersionLen,
		ref string lastVersionStr,
		ref bool lastVersionIsLower)
	{
		bool needVersion = aboveVersion != null;
		if (rawData != null && TryScanChunkPruneHeader(
			new ReadOnlySpan<byte>(rawData),
			needVersion,
			aboveVersion,
			ref lastVersionBytes,
			ref lastVersionLen,
			ref lastVersionStr,
			ref lastVersionIsLower,
			out int blocksPlaced,
			out int blocksRemoved,
			out bool versionIsLower))
		{
			bool keep = threshold > 0 && blocksRemoved + blocksPlaced >= threshold;
			if (needVersion && versionIsLower)
			{
				keep = true;
			}
			return keep;
		}

		ServerChunk serverChunk;
		using (MemoryStream memoryStream = new MemoryStream(rawData))
		{
			serverChunk = Serializer.Deserialize<ServerChunk>((Stream)memoryStream);
		}
		bool fallbackKeep = serverChunk.BlocksRemoved + serverChunk.BlocksPlaced >= threshold && threshold > 0;
		if (aboveVersion != null && GameVersion.IsLowerVersionThan(serverChunk.GameVersionCreated, aboveVersion))
		{
			fallbackKeep = true;
		}
		return fallbackKeep;
	}

	public static bool TryScanChunkPruneHeader(
		ReadOnlySpan<byte> data,
		bool readVersion,
		string aboveVersion,
		ref byte[] lastVersionBytes,
		ref int lastVersionLen,
		ref string lastVersionStr,
		ref bool lastVersionIsLower,
		out int blocksPlaced,
		out int blocksRemoved,
		out bool versionIsLower)
	{
		blocksPlaced = 0;
		blocksRemoved = 0;
		versionIsLower = false;
		bool sawVersionField = false;
		int offset = 0;
		int len = data.Length;
		while (offset < len)
		{
			if (!TryReadVarint64(data, ref offset, out ulong tag))
			{
				return false;
			}
			int wireType = (int)(tag & 7uL);
			int fieldNumber = (int)(tag >> 3);
			if (fieldNumber <= 0)
			{
				return false;
			}
			switch (wireType)
			{
			case 0:
			{
				if (!TryReadVarint64(data, ref offset, out ulong val))
				{
					return false;
				}
				if (fieldNumber == 17)
				{
					blocksPlaced = (int)val;
				}
				else if (fieldNumber == 18)
				{
					blocksRemoved = (int)val;
				}
				break;
			}
			case 1:
				if (len - offset < 8)
				{
					return false;
				}
				offset += 8;
				break;
			case 2:
			{
				if (!TryReadVarint64(data, ref offset, out ulong payloadLen) || payloadLen > (ulong)(len - offset))
				{
					return false;
				}
				int plen = (int)payloadLen;
				if (fieldNumber == 12 && readVersion)
				{
					sawVersionField = true;
					ReadOnlySpan<byte> verSpan = data.Slice(offset, plen);
					if (lastVersionStr != null && lastVersionBytes != null && plen == lastVersionLen && verSpan.SequenceEqual(new ReadOnlySpan<byte>(lastVersionBytes, 0, lastVersionLen)))
					{
						versionIsLower = lastVersionIsLower;
					}
					else
					{
						string verStr = plen == 0 ? string.Empty : Encoding.UTF8.GetString(verSpan);
						bool isLower = GameVersion.IsLowerVersionThan(verStr, aboveVersion);
						if (plen <= 64)
						{
							if (lastVersionBytes == null || lastVersionBytes.Length < 64)
							{
								lastVersionBytes = new byte[64];
							}
							verSpan.CopyTo(lastVersionBytes);
							lastVersionLen = plen;
							lastVersionStr = verStr;
							lastVersionIsLower = isLower;
						}
						versionIsLower = isLower;
					}
				}
				offset += plen;
				break;
			}
			case 5:
				if (len - offset < 4)
				{
					return false;
				}
				offset += 4;
				break;
			default:
				return false;
			}
		}
		if (readVersion && !sawVersionField)
		{
			versionIsLower = GameVersion.IsLowerVersionThan(null, aboveVersion);
		}
		return true;
	}

	private static bool TryReadVarint64(ReadOnlySpan<byte> data, ref int offset, out ulong value)
	{
		value = 0uL;
		int shift = 0;
		while (offset < data.Length)
		{
			byte b = data[offset++];
			value |= (ulong)(b & 0x7Fu) << shift;
			if ((b & 0x80u) == 0)
			{
				return true;
			}
			shift += 7;
			if (shift >= 64)
			{
				return false;
			}
		}
		return false;
	}

	public static bool CanUseBatchedBackupPartial(Vec2i start, Vec2i end, int mapSizeYinChunks)
	{
		return start != null
			&& end != null
			&& start.X >= 0
			&& start.Y >= 0
			&& end.X <= ChunkCoordMask
			&& end.Y <= ChunkCoordMask
			&& mapSizeYinChunks <= 512;
	}

	public static void ExecuteBatchedBackupPartial(
		SqliteConnection destConn,
		string sourceFileName,
		object sourceTransactionLock,
		Vec2i start,
		Vec2i end,
		int mapSizeYinChunks)
	{
		object syncRoot = sourceTransactionLock ?? new object();
		lock (syncRoot)
		{
			SqliteCommand attachCmd = destConn.CreateCommand();
			try
			{
				((DbCommand)(object)attachCmd).CommandText = "ATTACH DATABASE $source AS src;";
				attachCmd.Parameters.AddWithValue("$source", (object)sourceFileName);
				((DbCommand)(object)attachCmd).ExecuteNonQuery();
			}
			finally
			{
				((IDisposable)attachCmd)?.Dispose();
			}

			try
			{
				SqliteTransaction tx = destConn.BeginTransaction();
				try
				{
					SqliteCommand metaCmd = destConn.CreateCommand();
					try
					{
						metaCmd.Transaction = tx;
						((DbCommand)(object)metaCmd).CommandText =
							"INSERT INTO main.gamedata SELECT * FROM src.gamedata;" +
							"INSERT INTO main.playerdata SELECT * FROM src.playerdata;";
						((DbCommand)(object)metaCmd).ExecuteNonQuery();
					}
					finally
					{
						((IDisposable)metaCmd)?.Dispose();
					}

					if (start.X <= end.X && start.Y <= end.Y)
					{
						if (mapSizeYinChunks > 0)
						{
							SqliteCommand chunkCmd = destConn.CreateCommand();
							try
							{
								chunkCmd.Transaction = tx;
								((DbCommand)(object)chunkCmd).CommandText =
									"INSERT OR REPLACE INTO main.chunk (position, data) " +
									"SELECT position, data FROM src.chunk " +
									"WHERE position BETWEEN $minPos AND $maxPos " +
									"AND (position & 130023424) = 0 " +
									"AND (position & 4194303) BETWEEN $minX AND $maxX;";
								SqliteParameter pMinPos = chunkCmd.Parameters.Add("$minPos", SqliteType.Integer);
								SqliteParameter pMaxPos = chunkCmd.Parameters.Add("$maxPos", SqliteType.Integer);
								chunkCmd.Parameters.AddWithValue("$minX", (object)(long)start.X);
								chunkCmd.Parameters.AddWithValue("$maxX", (object)(long)end.X);
								((DbCommand)(object)chunkCmd).Prepare();
								for (int y = 0; y < mapSizeYinChunks; y++)
								{
									((DbParameter)(object)pMinPos).Value = (long)ChunkPos.ToChunkIndex(start.X, y, start.Y);
									((DbParameter)(object)pMaxPos).Value = (long)ChunkPos.ToChunkIndex(end.X, y, end.Y);
									((DbCommand)(object)chunkCmd).ExecuteNonQuery();
								}
							}
							finally
							{
								((IDisposable)chunkCmd)?.Dispose();
							}
						}

						SqliteCommand mapChunkCmd = destConn.CreateCommand();
						try
						{
							mapChunkCmd.Transaction = tx;
							((DbCommand)(object)mapChunkCmd).CommandText =
								"INSERT OR REPLACE INTO main.mapchunk (position, data) " +
								"SELECT position, data FROM src.mapchunk " +
								"WHERE position BETWEEN $minPos AND $maxPos " +
								"AND (position & 130023424) = 0 " +
								"AND (position & 4194303) BETWEEN $minX AND $maxX;";
							mapChunkCmd.Parameters.AddWithValue("$minPos", (object)(long)ChunkPos.ToChunkIndex(start.X, 0, start.Y));
							mapChunkCmd.Parameters.AddWithValue("$maxPos", (object)(long)ChunkPos.ToChunkIndex(end.X, 0, end.Y));
							mapChunkCmd.Parameters.AddWithValue("$minX", (object)(long)start.X);
							mapChunkCmd.Parameters.AddWithValue("$maxX", (object)(long)end.X);
							((DbCommand)(object)mapChunkCmd).ExecuteNonQuery();
						}
						finally
						{
							((IDisposable)mapChunkCmd)?.Dispose();
						}

						int regSize = MagicNum.ChunkRegionSizeInChunks;
						int minRegX = start.X / regSize;
						int maxRegX = end.X / regSize;
						int minRegZ = start.Y / regSize;
						int maxRegZ = end.Y / regSize;
						SqliteCommand mapRegionCmd = destConn.CreateCommand();
						try
						{
							mapRegionCmd.Transaction = tx;
							((DbCommand)(object)mapRegionCmd).CommandText =
								"INSERT OR REPLACE INTO main.mapregion (position, data) " +
								"SELECT position, data FROM src.mapregion " +
								"WHERE position BETWEEN $minPos AND $maxPos " +
								"AND (position & 130023424) = 0 " +
								"AND (position & 4194303) BETWEEN $minX AND $maxX;";
							mapRegionCmd.Parameters.AddWithValue("$minPos", (object)(long)ChunkPos.ToChunkIndex(minRegX, 0, minRegZ));
							mapRegionCmd.Parameters.AddWithValue("$maxPos", (object)(long)ChunkPos.ToChunkIndex(maxRegX, 0, maxRegZ));
							mapRegionCmd.Parameters.AddWithValue("$minX", (object)(long)minRegX);
							mapRegionCmd.Parameters.AddWithValue("$maxX", (object)(long)maxRegX);
							((DbCommand)(object)mapRegionCmd).ExecuteNonQuery();
						}
						finally
						{
							((IDisposable)mapRegionCmd)?.Dispose();
						}
					}

					((DbTransaction)(object)tx).Commit();
				}
				finally
				{
					((IDisposable)tx)?.Dispose();
				}
			}
			finally
			{
				SqliteCommand detachCmd = destConn.CreateCommand();
				try
				{
					((DbCommand)(object)detachCmd).CommandText = "DETACH DATABASE src;";
					((DbCommand)(object)detachCmd).ExecuteNonQuery();
				}
				finally
				{
					((IDisposable)detachCmd)?.Dispose();
				}
			}
		}
	}
}
