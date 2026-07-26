using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.Premium;
using DfoGmTool.ServerCore.GameWorld;
using GmPvfLib;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    // 从 Script.pvf 建物品/任务/辅助表索引, 落盘到 SQLite。
    // 启动时计算 pvf_hash: 与磁盘缓存一致则复用(不打开 PVF), 否则全量重建并写入新 hash。
    // 运行时查询走磁盘索引, 进程内不常驻 13 万+ ItemEntry。
    // partial 按域拆分: Items Jobs Quests World Dungeons
    public sealed partial class PvfIndexService
    {
        private static readonly Regex NamePattern = new Regex(@"\[name\]\s*`([^`]*)`", RegexOptions.Compiled);
        private static readonly Regex LstPattern = new Regex(@"(\d+)\s+`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex BacktickPattern = new Regex("`([^`]+)`", RegexOptions.Compiled);

        // 小内存机: 索引构建强制串行, 避免多线程同时解压 chunk 抬峰值。
        private static readonly int IndexBuildParallelism = 1;

        private readonly string _pvfPath;
        private readonly PvfDiskIndexStore _diskIndex = new PvfDiskIndexStore();
        private volatile Dictionary<string, string> _regionNames;
        private volatile Dictionary<int, string> _dungeonRegion;
        private volatile Dictionary<int, int> _mapDungeon;
        private volatile List<int> _dungeonPermissionIds;
        private volatile HashSet<string> _openHubKeys;
        private volatile Dictionary<int, JobNameInfo> _jobNames;
        private volatile Dictionary<int, QuestMeta> _questMetaCache;
        private volatile string _buildError;
        private int _parseFailures;
        private int _builtItemCount;
        private int _builtQuestCount;

        public PvfIndexService(string pvfPath)
        {
            _pvfPath = pvfPath;
        }

        public bool IsReady => _diskIndex.IsReady;
        public string BuildError => _buildError ?? _diskIndex.BuildError;

        public void WarmInBackground()
        {
            Task.Run(() =>
            {
                try
                {
                    Build();
                }
                catch (Exception ex)
                {
                    _buildError = ex.Message;
                    _diskIndex.MarkFailed(ex.Message);
                    Console.WriteLine("[PvfIndex] 索引构建失败: " + ex);
                }
            });
        }

        private void Build()
        {
            Interlocked.Exchange(ref _parseFailures, 0);
            _questMetaCache = null;
            _buildError = null;

            var dbPath = PvfDiskIndexStore.ResolveCachePath(_pvfPath);
            Console.WriteLine("[PvfIndex] 计算 PVF 内容 hash: " + _pvfPath);
            var pvfHash = PvfDiskIndexStore.ComputePvfHash(_pvfPath);
            Console.WriteLine("[PvfIndex] pvf_hash=" + pvfHash);

            // 缓存命中: 只读 SQLite, 全程不打开 Script.pvf → 启动峰值最低。
            if (_diskIndex.TryOpenExisting(dbPath, pvfHash))
            {
                Console.WriteLine("[PvfIndex] 磁盘索引命中(pvf_hash 未变), 跳过重建: " + dbPath);
                LoadAuxiliaryMapsFromDisk();
                _builtItemCount = _diskIndex.ItemCount;
                _builtQuestCount = _diskIndex.QuestCount;
                _questMetaCache = _diskIndex.LoadAllQuests();
                WireExternalPathResolver();
                // VerifyPvf / WarmUp 可能已打开过 archive, 复用路径也必须卸掉。
                ReleaseArchiveMemory();
                Console.WriteLine($"[PvfIndex] 索引就绪(复用): 物品 {_builtItemCount}, 任务 {_builtQuestCount}, db=" + dbPath);
                return;
            }

            Console.WriteLine("[PvfIndex] 缓存缺失或 pvf_hash 变化, 开始全量重建: " + dbPath);
            PvfArchiveAccessor.Configure(_pvfPath);
            _diskIndex.OpenEmpty(dbPath);

            try
            {
                // Rebuild opens non-lite once so path→index can be persisted to archive_paths.
                // Runtime afterwards uses lite OpenMapped + ExternalPathResolver.
                using (var archive = PvfArchive.OpenMapped(_pvfPath, lite: false))
                {
                    LoadAuxiliaryMaps(archive);
                    archive.ClearChunkCache();
                    CompactHeap();

                    _diskIndex.BeginWriteBatch(out var conn, out var tx);
                    try
                    {
                        PersistAuxiliaryMaps(conn, tx);

                        // Path index first: every subsequent GetFileContent stays O(1) via sticky map.
                        var pathCount = 0;
                        foreach (var pair in archive.EnumerateRuntimePaths())
                        {
                            PvfDiskIndexStore.InsertArchivePath(conn, tx, pair.Key, pair.Value);
                            pathCount++;
                        }
                        Console.WriteLine("[PvfIndex] archive_paths 写入 " + pathCount + " 条");

                        var itemCount = 0;
                        itemCount += BuildKindToDisk(archive, conn, tx, "equipment/equipment.lst", "equipment");
                        archive.ClearChunkCache();
                        CompactHeap();
                        itemCount += BuildKindToDisk(archive, conn, tx, "stackable/stackable.lst", "stackable");
                        archive.ClearChunkCache();
                        CompactHeap();
                        var premiumCount = BuildPremiumItemsToDisk(archive, conn, tx);
                        Console.WriteLine("[PvfIndex] premium_items 写入 " + premiumCount + " 条");
                        var questCount = BuildQuestMetaToDisk(archive, conn, tx);
                        archive.ClearChunkCache();

                        tx.Commit();
                        _builtItemCount = itemCount;
                        _builtQuestCount = questCount;
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { /* ignore */ }
                        throw;
                    }
                }

                _diskIndex.FinalizeBuild(_builtItemCount, _builtQuestCount, _parseFailures, pvfHash);
                WireExternalPathResolver();
            }
            finally
            {
                // 重建结束后立刻卸掉 PVF, 避免稳态挂着整包。
                ReleaseArchiveMemory();
            }

            // 任务量小, 构建后一次性载入缓存, 避免任务页反复扫盘。
            // 辅助表已在 LoadAuxiliaryMaps 阶段填好, 无需再读盘。
            _questMetaCache = _diskIndex.LoadAllQuests();
            CompactHeap();

            var failures = _parseFailures;
            Console.WriteLine($"[PvfIndex] 索引就绪(磁盘): 物品 {_builtItemCount}, 任务 {_builtQuestCount}"
                + (failures > 0 ? $", 解析失败被跳过 {failures} 条" : "")
                + ", pvf_hash=" + pvfHash
                + ", db=" + dbPath);
        }

        private void WireExternalPathResolver()
        {
            // Lite OpenMapped resolves paths via SQLite instead of a 300MB in-process map.
            var store = _diskIndex;
            PvfArchive.ExternalPathResolver = path => store.FindArchiveFileIndex(path);
            // Path-only SQL (no full ItemEntry materialization) on hot item lookups.
            ItemMetadataResolver.ItemFilePathResolver = id =>
            {
                var filePath = store.GetItemFilePath(id);
                return string.IsNullOrEmpty(filePath)
                    ? null
                    : filePath.Replace('\\', '/').TrimStart('/');
            };
            // Grant path: full ItemMetadata from index (schema v4+), no PVF open.
            ItemMetadataResolver.DiskGrantMetadataResolver = id =>
            {
                var entry = store.GetItem(id);
                return entry == null ? null : ToGrantMetadata(entry);
            };
            // Expiration from index abs/usable/invalid columns.
            ItemGrantExpirationResolver.DiskExpirationResolver = (id, metadata, _) =>
            {
                var entry = store.GetItem(id);
                if (entry == null)
                    return (false, 0, null);
                if (entry.HasInvalidExpirationDefinition)
                    return (true, 0, "物品期限定义无法从 PVF 解析");

                var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                if (entry.UsablePeriodDays > 0)
                    return (true, ItemGrantExpirationResolver.AddDaysFromNowPublic(entry.UsablePeriodDays), null);

                if (entry.AbsoluteExpirationUnixTime > 0)
                {
                    if (entry.AbsoluteExpirationUnixTime <= now)
                        return (true, 0, string.Equals(entry.Kind, "equipment", StringComparison.OrdinalIgnoreCase)
                            ? "装备的固定期限已过期"
                            : "物品的固定期限已过期");
                    return (true, entry.AbsoluteExpirationUnixTime, null);
                }

                // Equipment permanent: match previous PVF path (expireTime = -1).
                if (string.Equals(entry.Kind, "equipment", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(entry.TypeTag, "name tag", StringComparison.OrdinalIgnoreCase))
                        return (true, ItemGrantExpirationResolver.AddDaysFromNowPublic(30), null);
                    return (true, -1, null);
                }

                // Stackable with no period/absolute → permanent (0).
                return (true, 0, null);
            };
            // Premium contracts from index (schema v5+): GiveItem must not open premiumlist.
            // Empty catalog is still valid — never fall back to Script.pvf once index is wired.
            PremiumCatalog.DiskCatalogLoader = () =>
            {
                try
                {
                    var rows = store.LoadPremiumItems();
                    return PremiumCatalog.FromEntries(
                        (rows ?? new List<(int, int, int)>())
                            .Select(r => new PremiumEntry(r.ItemCode, r.PremiumType, r.DurationDays)));
                }
                catch
                {
                    return null;
                }
            };
            PremiumCatalog.ResetCacheOnly();
        }

        private static int BuildPremiumItemsToDisk(PvfArchive archive, SqliteConnection conn, SqliteTransaction tx)
        {
            string text;
            try
            {
                text = archive.GetFileContent("etc/premiumlist_new.etc");
            }
            catch
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var catalog = PremiumCatalog.Parse(text);
            var count = 0;
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || entry.ItemCode <= 0 || entry.PremiumType <= 0 || entry.DurationDays <= 0)
                    continue;
                PvfDiskIndexStore.InsertPremiumItem(conn, tx, entry.ItemCode, entry.PremiumType, entry.DurationDays);
                count++;
            }
            return count;
        }

        private static void ReleaseArchiveMemory()
        {
            PvfArchiveAccessor.Unload();
            CompactHeap();
        }

        private static void CompactHeap()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        private void LoadAuxiliaryMapsFromDisk()
        {
            _regionNames = _diskIndex.LoadRegionNames();
            _dungeonRegion = _diskIndex.LoadDungeonRegions();
            _mapDungeon = _diskIndex.LoadMapDungeons();
            _dungeonPermissionIds = _diskIndex.LoadDungeonPermissions();
            _openHubKeys = _diskIndex.LoadOpenHubs();
            _jobNames = LoadJobNamesFromDisk();
        }

        private Dictionary<int, JobNameInfo> LoadJobNamesFromDisk()
        {
            var result = new Dictionary<int, JobNameInfo>();
            foreach (var pair in _diskIndex.LoadJobs())
            {
                var info = new JobNameInfo
                {
                    BaseName = pair.Value.BaseName ?? string.Empty,
                };
                try
                {
                    var grows = JsonSerializer.Deserialize<List<string>>(pair.Value.GrowNamesJson);
                    if (grows != null)
                        info.GrowTypeNames = grows;
                }
                catch { /* ignore corrupt row */ }

                try
                {
                    var awake = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(pair.Value.AwakeningJson);
                    if (awake != null)
                    {
                        foreach (var kv in awake)
                        {
                            if (int.TryParse(kv.Key, out var growType) && kv.Value != null)
                                info.AwakeningNames[growType] = kv.Value;
                        }
                    }
                }
                catch { /* ignore corrupt row */ }

                result[pair.Key] = info;
            }
            return result;
        }

        private void PersistAuxiliaryMaps(SqliteConnection conn, SqliteTransaction tx)
        {
            var regions = _regionNames;
            if (regions != null)
            {
                foreach (var pair in regions)
                    PvfDiskIndexStore.InsertRegionName(conn, tx, pair.Key, pair.Value);
            }

            var dungeonRegion = _dungeonRegion;
            if (dungeonRegion != null)
            {
                foreach (var pair in dungeonRegion)
                    PvfDiskIndexStore.InsertDungeonRegion(conn, tx, pair.Key, pair.Value);
            }

            var mapDungeon = _mapDungeon;
            if (mapDungeon != null)
            {
                foreach (var pair in mapDungeon)
                    PvfDiskIndexStore.InsertMapDungeon(conn, tx, pair.Key, pair.Value);
            }

            var perms = _dungeonPermissionIds;
            if (perms != null)
            {
                foreach (var id in perms)
                    PvfDiskIndexStore.InsertDungeonPermission(conn, tx, id);
            }

            var hubs = _openHubKeys;
            if (hubs != null)
            {
                foreach (var key in hubs)
                    PvfDiskIndexStore.InsertOpenHub(conn, tx, key);
            }

            var jobs = _jobNames;
            if (jobs != null)
            {
                foreach (var pair in jobs)
                {
                    var growJson = JsonSerializer.Serialize(pair.Value.GrowTypeNames ?? new List<string>());
                    var awakeDict = new Dictionary<string, List<string>>();
                    if (pair.Value.AwakeningNames != null)
                    {
                        foreach (var aw in pair.Value.AwakeningNames)
                            awakeDict[aw.Key.ToString()] = aw.Value ?? new List<string>();
                    }
                    var awakeJson = JsonSerializer.Serialize(awakeDict);
                    PvfDiskIndexStore.InsertJob(conn, tx, pair.Key, pair.Value.BaseName, growJson, awakeJson);
                }
            }
        }

        private void LoadAuxiliaryMaps(PvfArchive archive)
        {
            _jobNames = BuildJobNames(archive);
            archive.ClearChunkCache();
            _regionNames = BuildRegionNames(archive);
            archive.ClearChunkCache();
            _dungeonRegion = BuildDungeonRegionMap(archive);
            archive.ClearChunkCache();
            _mapDungeon = BuildMapDungeonMap(archive);
            archive.ClearChunkCache();
            _dungeonPermissionIds = BuildDungeonPermissionIds(archive);
            _openHubKeys = BuildOpenHubKeys(archive);
        }

        private static string FindLstPath(PvfArchive archive, string fileName)
        {
            foreach (var file in archive.Files)
            {
                var name = file.Name ?? string.Empty;
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(file.Path) ? name : file.Path.Replace('\\', '/') + "/" + name;
            }
            return null;
        }
    }
}
