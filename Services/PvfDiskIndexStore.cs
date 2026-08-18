using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    /// <summary>
    /// SQLite-backed PVF search index. Reused when Script.pvf content hash matches;
    /// otherwise rebuilt and the new hash is persisted in meta.
    /// </summary>
    internal sealed class PvfDiskIndexStore : IDisposable
    {
        // v3: archive_paths + items.file_path (lite open).
        // v4: grant fields on items (stack/durability/type/category) so TryGrant is index-first.
        // v5: premium_items so GiveItem never opens premiumlist_new.etc / Script.pvf.
        // v6: avatar grant fields + amplify + ability tables + skill names + job stat tables.
        private const int SchemaVersion = 6;
        private readonly object _gate = new object();
        private SqliteConnection _conn;
        private string _dbPath;
        private bool _ready;
        private string _buildError;
        private int _itemCount;
        private int _questCount;

        public bool IsReady
        {
            get { lock (_gate) return _ready; }
        }

        public string BuildError
        {
            get { lock (_gate) return _buildError; }
        }

        public int ItemCount
        {
            get { lock (_gate) return _itemCount; }
        }

        public int QuestCount
        {
            get { lock (_gate) return _questCount; }
        }

        public string DatabasePath
        {
            get { lock (_gate) return _dbPath; }
        }

        public static string ResolveCachePath(string pvfPath)
        {
            var full = Path.GetFullPath(pvfPath);
            var dir = Path.Combine(Path.GetTempPath(), "DfoGmTool", "pvf-index");
            Directory.CreateDirectory(dir);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))
                .Substring(0, 16)
                .ToLowerInvariant();
            return Path.Combine(dir, "pvf-index-" + key + ".db");
        }

        /// <summary>
        /// SHA256 hex of the PVF file contents (lowercase). Used as cache invalidation key.
        /// </summary>
        public static string ComputePvfHash(string pvfPath)
        {
            using var stream = File.OpenRead(pvfPath);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Open an existing on-disk index if schema + pvf_hash match. Returns false to force rebuild.
        /// </summary>
        public bool TryOpenExisting(string dbPath, string expectedPvfHash)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || string.IsNullOrWhiteSpace(expectedPvfHash))
                return false;
            if (!File.Exists(dbPath))
                return false;

            lock (_gate)
            {
                CloseUnlocked();
                _dbPath = dbPath;
                _buildError = null;
                _ready = false;
                _itemCount = 0;
                _questCount = 0;

                try
                {
                    _conn = OpenConnection(dbPath);

                    var schema = ReadMeta("schema_version");
                    if (schema != SchemaVersion.ToString(CultureInfo.InvariantCulture))
                    {
                        CloseUnlocked();
                        return false;
                    }

                    var storedHash = ReadMeta("pvf_hash");
                    if (!string.Equals(storedHash, expectedPvfHash, StringComparison.OrdinalIgnoreCase))
                    {
                        CloseUnlocked();
                        return false;
                    }

                    if (!int.TryParse(ReadMeta("item_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _itemCount)
                        || !int.TryParse(ReadMeta("quest_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _questCount)
                        || _itemCount < 0
                        || _questCount < 0)
                    {
                        CloseUnlocked();
                        return false;
                    }

                    // Sanity: every table required by the current schema must be queryable.
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM items),
  (SELECT COUNT(*) FROM quests),
  (SELECT COUNT(*) FROM jobs),
  (SELECT COUNT(*) FROM region_names),
  (SELECT COUNT(*) FROM archive_paths),
  (SELECT COUNT(*) FROM premium_items),
  (SELECT COUNT(*) FROM amplify_config),
  (SELECT COUNT(*) FROM avatar_ability_names),
  (SELECT COUNT(*) FROM avatar_ability_cases),
  (SELECT COUNT(*) FROM skill_names),
  (SELECT COUNT(*) FROM job_stat_tables);";
                        using var reader = cmd.ExecuteReader();
                        if (!reader.Read() || reader.GetInt32(4) <= 0)
                        {
                            CloseUnlocked();
                            return false;
                        }
                    }

                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT ability_case_index, avatar_select_json, avatar_durations_json FROM items LIMIT 1;";
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA query_only=ON;";
                        cmd.ExecuteNonQuery();
                    }

                    _ready = true;
                    return true;
                }
                catch
                {
                    CloseUnlocked();
                    return false;
                }
            }
        }

        public void OpenEmpty(string dbPath)
        {
            lock (_gate)
            {
                CloseUnlocked();
                _dbPath = dbPath;
                _buildError = null;
                _ready = false;
                _itemCount = 0;
                _questCount = 0;

                if (File.Exists(dbPath))
                    File.Delete(dbPath);
                TryDelete(dbPath + "-wal");
                TryDelete(dbPath + "-shm");

                var tmp = dbPath + ".building";
                if (File.Exists(tmp))
                    File.Delete(tmp);

                _conn = OpenConnection(tmp);
                ApplySchema(_conn);
            }
        }

        public void FinalizeBuild(int itemCount, int questCount, int parseFailures, string pvfHash)
        {
            lock (_gate)
            {
                if (_conn == null)
                    throw new InvalidOperationException("索引连接未打开。");
                if (string.IsNullOrWhiteSpace(pvfHash))
                    throw new ArgumentException("pvfHash 不能为空。", nameof(pvfHash));

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_items_kind_tag ON items(kind, type_tag);
CREATE INDEX IF NOT EXISTS ix_items_kind_seg ON items(kind, segment);
CREATE INDEX IF NOT EXISTS ix_items_level ON items(min_level);
CREATE INDEX IF NOT EXISTS ix_items_rarity ON items(rarity);
CREATE INDEX IF NOT EXISTS ix_quests_grade ON quests(grade);
CREATE INDEX IF NOT EXISTS ix_quests_region ON quests(region);
CREATE INDEX IF NOT EXISTS ix_items_file_path ON items(file_path);";
                    cmd.ExecuteNonQuery();
                }

                UpsertMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                UpsertMeta("pvf_hash", pvfHash.ToLowerInvariant());
                UpsertMeta("item_count", itemCount.ToString(CultureInfo.InvariantCulture));
                UpsertMeta("quest_count", questCount.ToString(CultureInfo.InvariantCulture));
                UpsertMeta("parse_failures", parseFailures.ToString(CultureInfo.InvariantCulture));
                UpsertMeta("built_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }

                // Always use the known building path — DataSource can be absolute/relative
                // and must not be trusted for the atomic promote step.
                var buildingPath = _dbPath + ".building";
                CloseUnlocked();
                SqliteConnection.ClearAllPools();

                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
                TryDelete(_dbPath + "-wal");
                TryDelete(_dbPath + "-shm");
                // Promote building → final. Leftover -wal/-shm from a crashed build are removed.
                TryDelete(buildingPath + "-wal");
                TryDelete(buildingPath + "-shm");
                if (!File.Exists(buildingPath))
                    throw new InvalidOperationException("索引构建产物缺失: " + buildingPath);
                File.Move(buildingPath, _dbPath);

                _conn = OpenConnection(_dbPath);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA query_only=ON;";
                    cmd.ExecuteNonQuery();
                }

                _itemCount = itemCount;
                _questCount = questCount;
                _ready = true;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }

        public void MarkFailed(string error)
        {
            lock (_gate)
            {
                _buildError = error ?? "索引构建失败";
                _ready = false;
                try
                {
                    var building = _dbPath + ".building";
                    CloseUnlocked();
                    if (File.Exists(building))
                        File.Delete(building);
                }
                catch
                {
                    // best effort cleanup
                }
            }
        }

        public void BeginWriteBatch(out SqliteConnection conn, out SqliteTransaction tx)
        {
            lock (_gate)
            {
                if (_conn == null)
                    throw new InvalidOperationException("索引连接未打开。");
                conn = _conn;
                tx = _conn.BeginTransaction();
            }
        }

        public static void InsertItem(SqliteConnection conn, SqliteTransaction tx, PvfIndexService.ItemEntry entry)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR REPLACE INTO items(
  id, name, kind, type_tag, segment, special, rarity, min_level, grade, usable_job,
  abs_expire, usable_days, daily_delete, invalid_expire, requires_manual, requires_config, supports_quality,
  file_path, type_full, item_category, attach_type, stack_limit, durability, impossible_json,
  ability_case_index, avatar_select_json, avatar_durations_json)
VALUES(
  @id, @name, @kind, @tag, @seg, @special, @rarity, @minlv, @grade, @job,
  @abs, @days, @daily, @invalid, @manual, @config, @quality,
  @path, @typefull, @cat, @attach, @stack, @dura, @imposs,
  @caseidx, @avsel, @avdur);";
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@name", entry.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("@kind", entry.Kind ?? string.Empty);
            cmd.Parameters.AddWithValue("@tag", (object)entry.TypeTag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@seg", (object)entry.Segment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@special", (object)entry.Special ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rarity", entry.Rarity);
            cmd.Parameters.AddWithValue("@minlv", entry.MinLevel);
            cmd.Parameters.AddWithValue("@grade", entry.Grade);
            cmd.Parameters.AddWithValue("@job", (object)entry.UsableJob ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@abs", entry.AbsoluteExpirationUnixTime);
            cmd.Parameters.AddWithValue("@days", entry.UsablePeriodDays);
            cmd.Parameters.AddWithValue("@daily", entry.DailyDeleteItem ? 1 : 0);
            cmd.Parameters.AddWithValue("@invalid", entry.HasInvalidExpirationDefinition ? 1 : 0);
            cmd.Parameters.AddWithValue("@manual", entry.RequiresManualGrantType ? 1 : 0);
            cmd.Parameters.AddWithValue("@config", entry.RequiresConfiguration ? 1 : 0);
            cmd.Parameters.AddWithValue("@quality", entry.SupportsQuality ? 1 : 0);
            cmd.Parameters.AddWithValue("@path", (object)entry.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@typefull", (object)entry.TypeFull ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cat", (object)entry.ItemCategory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@attach", (object)entry.AttachType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stack", entry.StackLimit);
            cmd.Parameters.AddWithValue("@dura", entry.Durability);
            cmd.Parameters.AddWithValue("@imposs", (object)entry.ImpossibleJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@caseidx", entry.AbilityCaseIndex);
            cmd.Parameters.AddWithValue("@avsel", (object)entry.AvatarSelectJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avdur", (object)entry.AvatarDurationsJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public static void InsertArchivePath(SqliteConnection conn, SqliteTransaction tx, string path, int fileIndex)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO archive_paths(path, file_index) VALUES(@p, @i);";
            cmd.Parameters.AddWithValue("@p", path ?? string.Empty);
            cmd.Parameters.AddWithValue("@i", fileIndex);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Look up PVF file index by normalized relative path. Returns -1 if missing.</summary>
        public int FindArchiveFileIndex(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return -1;
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return -1;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT file_index FROM archive_paths WHERE path=@p LIMIT 1;";
                cmd.Parameters.AddWithValue("@p", relativePath.Replace('\\', '/').Trim().TrimStart('/'));
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return -1;
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        public string GetItemFilePath(int itemId)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return null;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT file_path FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public static void InsertRegionName(SqliteConnection conn, SqliteTransaction tx, string key, string name)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO region_names(key, name) VALUES(@k, @n);";
            cmd.Parameters.AddWithValue("@k", key ?? string.Empty);
            cmd.Parameters.AddWithValue("@n", name ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public static void InsertDungeonRegion(SqliteConnection conn, SqliteTransaction tx, int dungeonId, string region)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO dungeon_regions(dungeon_id, region) VALUES(@id, @r);";
            cmd.Parameters.AddWithValue("@id", dungeonId);
            cmd.Parameters.AddWithValue("@r", region ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public static void InsertMapDungeon(SqliteConnection conn, SqliteTransaction tx, int mapId, int dungeonId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO map_dungeons(map_id, dungeon_id) VALUES(@m, @d);";
            cmd.Parameters.AddWithValue("@m", mapId);
            cmd.Parameters.AddWithValue("@d", dungeonId);
            cmd.ExecuteNonQuery();
        }

        public static void InsertDungeonPermission(SqliteConnection conn, SqliteTransaction tx, int dungeonId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO dungeon_permissions(dungeon_id) VALUES(@id);";
            cmd.Parameters.AddWithValue("@id", dungeonId);
            cmd.ExecuteNonQuery();
        }

        public static void InsertOpenHub(SqliteConnection conn, SqliteTransaction tx, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO open_hubs(key) VALUES(@k);";
            cmd.Parameters.AddWithValue("@k", key ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public static void InsertJob(
            SqliteConnection conn,
            SqliteTransaction tx,
            int jobId,
            string baseName,
            string growNamesJson,
            string awakeningJson)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR REPLACE INTO jobs(job_id, base_name, grow_names_json, awakening_json)
VALUES(@id, @base, @grow, @awake);";
            cmd.Parameters.AddWithValue("@id", jobId);
            cmd.Parameters.AddWithValue("@base", baseName ?? string.Empty);
            cmd.Parameters.AddWithValue("@grow", growNamesJson ?? "[]");
            cmd.Parameters.AddWithValue("@awake", awakeningJson ?? "{}");
            cmd.ExecuteNonQuery();
        }

        public static void InsertPremiumItem(
            SqliteConnection conn,
            SqliteTransaction tx,
            int itemCode,
            int premiumType,
            int durationDays)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR REPLACE INTO premium_items(item_code, premium_type, duration_days)
VALUES(@id, @type, @days);";
            cmd.Parameters.AddWithValue("@id", itemCode);
            cmd.Parameters.AddWithValue("@type", premiumType);
            cmd.Parameters.AddWithValue("@days", durationDays);
            cmd.ExecuteNonQuery();
        }

        public bool TryGetPremium(int itemCode, out int premiumType, out int durationDays)
        {
            premiumType = 0;
            durationDays = 0;
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return false;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT premium_type, duration_days FROM premium_items WHERE item_code=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemCode);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return false;
                premiumType = reader.GetInt32(0);
                durationDays = reader.GetInt32(1);
                return premiumType > 0 && durationDays > 0;
            }
        }

        public List<(int ItemCode, int PremiumType, int DurationDays)> LoadPremiumItems()
        {
            lock (_gate)
            {
                EnsureReady();
                var list = new List<(int, int, int)>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT item_code, premium_type, duration_days FROM premium_items;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
                return list;
            }
        }

        public static void UpsertAmplifyConfig(SqliteConnection conn, SqliteTransaction tx, string key, string value)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO amplify_config(key, value) VALUES(@k, @v);";
            cmd.Parameters.AddWithValue("@k", key ?? string.Empty);
            cmd.Parameters.AddWithValue("@v", value ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public Dictionary<string, string> LoadAmplifyConfig()
        {
            lock (_gate)
            {
                EnsureReady();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM amplify_config;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(key))
                        continue;
                    map[key] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }
                return map;
            }
        }

        public static void InsertAvatarAbilityName(SqliteConnection conn, SqliteTransaction tx, string token, string name)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO avatar_ability_names(token, name) VALUES(@t, @n);";
            cmd.Parameters.AddWithValue("@t", token ?? string.Empty);
            cmd.Parameters.AddWithValue("@n", name ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public static void InsertAvatarAbilityCase(SqliteConnection conn, SqliteTransaction tx, int caseIndex, string entriesJson)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO avatar_ability_cases(case_index, entries_json) VALUES(@i, @j);";
            cmd.Parameters.AddWithValue("@i", caseIndex);
            cmd.Parameters.AddWithValue("@j", entriesJson ?? "[]");
            cmd.ExecuteNonQuery();
        }

        public Dictionary<string, string> LoadAvatarAbilityNames()
        {
            lock (_gate)
            {
                EnsureReady();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT token, name FROM avatar_ability_names;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var token = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(token))
                        continue;
                    map[token] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }
                return map;
            }
        }

        public Dictionary<int, string> LoadAvatarAbilityCasesJson()
        {
            lock (_gate)
            {
                EnsureReady();
                var map = new Dictionary<int, string>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT case_index, entries_json FROM avatar_ability_cases;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    map[reader.GetInt32(0)] = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                return map;
            }
        }

        public static void InsertSkillName(SqliteConnection conn, SqliteTransaction tx, int job, int skillIndex, string name)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO skill_names(job, skill_index, name) VALUES(@j, @i, @n);";
            cmd.Parameters.AddWithValue("@j", job);
            cmd.Parameters.AddWithValue("@i", skillIndex);
            cmd.Parameters.AddWithValue("@n", name ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public string GetSkillName(int job, int skillIndex)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return null;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM skill_names WHERE job=@j AND skill_index=@i LIMIT 1;";
                cmd.Parameters.AddWithValue("@j", job);
                cmd.Parameters.AddWithValue("@i", skillIndex);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public static void InsertJobStatTables(SqliteConnection conn, SqliteTransaction tx, int jobId, string tablesJson)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO job_stat_tables(job_id, tables_json) VALUES(@id, @j);";
            cmd.Parameters.AddWithValue("@id", jobId);
            cmd.Parameters.AddWithValue("@j", tablesJson ?? "{}");
            cmd.ExecuteNonQuery();
        }

        public string GetJobStatTablesJson(int jobId)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return null;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT tables_json FROM job_stat_tables WHERE job_id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", jobId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public Dictionary<string, string> LoadRegionNames()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT key, name FROM region_names;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(key))
                        continue;
                    result[key] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }
                return result;
            }
        }

        public Dictionary<int, string> LoadDungeonRegions()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new Dictionary<int, string>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT dungeon_id, region FROM dungeon_regions;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result[reader.GetInt32(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                return result;
            }
        }

        public Dictionary<int, int> LoadMapDungeons()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new Dictionary<int, int>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT map_id, dungeon_id FROM map_dungeons;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result[reader.GetInt32(0)] = reader.GetInt32(1);
                return result;
            }
        }

        public List<int> LoadDungeonPermissions()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new List<int>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT dungeon_id FROM dungeon_permissions ORDER BY dungeon_id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetInt32(0));
                return result;
            }
        }

        public HashSet<string> LoadOpenHubs()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT key FROM open_hubs;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        result.Add(reader.GetString(0));
                }
                return result;
            }
        }

        public Dictionary<int, JobRecord> LoadJobs()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new Dictionary<int, JobRecord>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT job_id, base_name, grow_names_json, awakening_json FROM jobs;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetInt32(0)] = new JobRecord(
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? "[]" : reader.GetString(2),
                        reader.IsDBNull(3) ? "{}" : reader.GetString(3));
                }
                return result;
            }
        }

        internal readonly struct JobRecord
        {
            public readonly string BaseName;
            public readonly string GrowNamesJson;
            public readonly string AwakeningJson;

            public JobRecord(string baseName, string growNamesJson, string awakeningJson)
            {
                BaseName = baseName ?? string.Empty;
                GrowNamesJson = growNamesJson ?? "[]";
                AwakeningJson = awakeningJson ?? "{}";
            }
        }

        public static void InsertQuest(SqliteConnection conn, SqliteTransaction tx, PvfIndexService.QuestMeta meta)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR REPLACE INTO quests(
  id, name, grade, min_level, max_level, pre_required_json, pre_groups_json,
  pre_answer_json, collision_json, region, job, grow_type, job_change_quest,
  grow_number, reward_chain_type, target_character, exposed_by_npc, is_event,
  creature_kind, expert_job_type, expert_job_level, target_dungeon_id, target_map_id,
  target_quest_id, target_level, linked_dungeon_id, reward_title_item_id,
  reward_item_ids_json, reward_selection_item_ids_json, exception_quest)
VALUES(
  @id, @name, @grade, @minlv, @maxlv, @pre, @pregroups,
  @answer, @collision, @region, @job, @grow, @jcq,
  @grown, @rct, @targetchar, @exposed, @event,
  @creature, @ejt, @ejl, @tdungeon, @tmap,
  @tquest, @tlevel, @ldungeon, @rtitle,
  @ritems, @rsel, @exception);";
            cmd.Parameters.AddWithValue("@id", meta.Id);
            cmd.Parameters.AddWithValue("@name", meta.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("@grade", meta.Grade ?? string.Empty);
            cmd.Parameters.AddWithValue("@minlv", meta.MinLevel);
            cmd.Parameters.AddWithValue("@maxlv", meta.MaxLevel);
            cmd.Parameters.AddWithValue("@pre", SerializeIntArray(meta.PreRequired));
            cmd.Parameters.AddWithValue("@pregroups", SerializeIntArrays(meta.PreGroups));
            cmd.Parameters.AddWithValue("@answer", SerializeIntArray(meta.PreRequiredQuestAnswer));
            cmd.Parameters.AddWithValue("@collision", SerializeIntArray(meta.CollisionQuest));
            cmd.Parameters.AddWithValue("@region", meta.Region ?? string.Empty);
            cmd.Parameters.AddWithValue("@job", meta.Job ?? string.Empty);
            cmd.Parameters.AddWithValue("@grow", meta.GrowType);
            cmd.Parameters.AddWithValue("@jcq", meta.JobChangeQuestValue);
            cmd.Parameters.AddWithValue("@grown", meta.GrowNumber);
            cmd.Parameters.AddWithValue("@rct", meta.RewardChainType);
            cmd.Parameters.AddWithValue("@targetchar", meta.TargetCharacter ?? string.Empty);
            cmd.Parameters.AddWithValue("@exposed", meta.ExposedByNpc);
            cmd.Parameters.AddWithValue("@event", meta.IsEvent ? 1 : 0);
            cmd.Parameters.AddWithValue("@creature", meta.CreatureKind);
            cmd.Parameters.AddWithValue("@ejt", meta.ExpertJobType);
            cmd.Parameters.AddWithValue("@ejl", meta.ExpertJobLevel);
            cmd.Parameters.AddWithValue("@tdungeon", meta.TargetDungeonId);
            cmd.Parameters.AddWithValue("@tmap", meta.TargetMapId);
            cmd.Parameters.AddWithValue("@tquest", meta.TargetQuestId);
            cmd.Parameters.AddWithValue("@tlevel", meta.TargetLevel);
            cmd.Parameters.AddWithValue("@ldungeon", meta.LinkedDungeonId);
            cmd.Parameters.AddWithValue("@rtitle", meta.RewardTitleItemId);
            cmd.Parameters.AddWithValue("@ritems", SerializeIntArray(meta.RewardItemIds));
            cmd.Parameters.AddWithValue("@rsel", SerializeIntArray(meta.RewardSelectionItemIds));
            cmd.Parameters.AddWithValue("@exception", meta.ExceptionQuest ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public PvfIndexService.ItemEntry GetItem(int itemId)
        {
            lock (_gate)
            {
                EnsureReady();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT " + ItemSelectColumns + " FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadItem(reader) : null;
            }
        }

        public string GetItemName(int itemId)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return null;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public HashSet<int> CopyItemIds()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new HashSet<int>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM items;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetInt32(0));
                return result;
            }
        }

        public string GetItemKind(int itemId)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return null;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT kind FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public int GetItemRarity(int itemId)
        {
            lock (_gate)
            {
                if (!_ready || _conn == null)
                    return -1;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT rarity FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        public bool TryGetItemExpiration(int itemId, out PvfIndexService.ItemExpirationDefinition expiration)
        {
            lock (_gate)
            {
                expiration = default;
                if (!_ready || _conn == null)
                    return false;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT abs_expire, usable_days, daily_delete, invalid_expire FROM items WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return false;
                expiration = new PvfIndexService.ItemExpirationDefinition(
                    true,
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3) != 0);
                return true;
            }
        }

        public void BuildItemFilter(
            string query,
            string kind,
            HashSet<string> tagSet,
            HashSet<string> segmentSet,
            string special,
            int minLevel,
            int maxLevel,
            int rarity,
            string expiration,
            out string whereSql,
            out List<SqliteParameter> args)
        {
            var where = new StringBuilder("WHERE 1=1");
            args = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(kind))
            {
                where.Append(" AND kind=@kind");
                args.Add(new SqliteParameter("@kind", kind));
            }

            if (tagSet != null && tagSet.Count > 0)
                AppendInClause(where, args, "type_tag", "tag", tagSet);

            if (segmentSet != null && segmentSet.Count > 0)
                AppendInClause(where, args, "segment", "seg", segmentSet);

            if (special != null)
            {
                where.Append(" AND special=@special");
                args.Add(new SqliteParameter("@special", special));
            }

            if (minLevel > 0)
            {
                where.Append(" AND min_level>=@minlv");
                args.Add(new SqliteParameter("@minlv", minLevel));
            }

            if (maxLevel > 0)
            {
                where.Append(" AND min_level<=@maxlv");
                args.Add(new SqliteParameter("@maxlv", maxLevel));
            }

            if (rarity >= 0)
            {
                where.Append(" AND rarity=@rarity");
                args.Add(new SqliteParameter("@rarity", rarity));
            }

            expiration = (expiration ?? string.Empty).Trim().ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            switch (expiration)
            {
                case "limited":
                    where.Append(" AND (abs_expire>0 OR usable_days>0 OR daily_delete=1)");
                    break;
                case "none":
                    where.Append(" AND invalid_expire=0 AND abs_expire=0 AND usable_days=0 AND daily_delete=0");
                    break;
                case "relative":
                    where.Append(" AND usable_days>0");
                    break;
                case "absolute":
                    where.Append(" AND abs_expire>0");
                    break;
                case "daily":
                    where.Append(" AND daily_delete=1");
                    break;
                case "expired":
                    where.Append(" AND abs_expire>0 AND abs_expire<=@now");
                    args.Add(new SqliteParameter("@now", now));
                    break;
            }

            query = (query ?? string.Empty).Trim();
            if (query.Length > 0)
            {
                if (int.TryParse(query, out var numericId) && numericId > 0)
                {
                    where.Append(" AND (id=@qid OR instr(lower(name), lower(@qname))>0)");
                    args.Add(new SqliteParameter("@qid", numericId));
                    args.Add(new SqliteParameter("@qname", query));
                }
                else
                {
                    where.Append(" AND instr(lower(name), lower(@qname))>0");
                    args.Add(new SqliteParameter("@qname", query));
                }
            }

            whereSql = where.ToString();
        }

        public List<PvfIndexService.ItemEntry> SearchItems(
            string query,
            string kind,
            HashSet<string> tagSet,
            HashSet<string> segmentSet,
            string special,
            int minLevel,
            int maxLevel,
            int rarity,
            string expiration,
            int limit,
            int offset,
            out int total)
        {
            lock (_gate)
            {
                EnsureReady();
                BuildItemFilter(query, kind, tagSet, segmentSet, special, minLevel, maxLevel, rarity, expiration,
                    out var where, out var args);

                using (var countCmd = _conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM items " + where + ";";
                    foreach (var p in args)
                        countCmd.Parameters.Add(CloneParameter(p));
                    total = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var results = new List<PvfIndexService.ItemEntry>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + ItemSelectColumns + " FROM items " + where
                        + " ORDER BY id LIMIT @lim OFFSET @off;";
                    foreach (var p in args)
                        cmd.Parameters.Add(CloneParameter(p));
                    cmd.Parameters.AddWithValue("@lim", limit);
                    cmd.Parameters.AddWithValue("@off", offset);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }

                return results;
            }
        }

        /// <summary>
        /// Stream filtered rows for post-filters (e.g. usable job tokens) without materializing the full set.
        /// </summary>
        public List<PvfIndexService.ItemEntry> SearchItemsStreaming(
            string query,
            string kind,
            HashSet<string> tagSet,
            HashSet<string> segmentSet,
            string special,
            int minLevel,
            int maxLevel,
            int rarity,
            string expiration,
            Func<PvfIndexService.ItemEntry, bool> postFilter,
            int limit,
            int offset,
            out int total)
        {
            if (postFilter == null)
                return SearchItems(query, kind, tagSet, segmentSet, special, minLevel, maxLevel, rarity, expiration, limit, offset, out total);

            lock (_gate)
            {
                EnsureReady();
                BuildItemFilter(query, kind, tagSet, segmentSet, special, minLevel, maxLevel, rarity, expiration,
                    out var where, out var args);

                total = 0;
                var results = new List<PvfIndexService.ItemEntry>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + ItemSelectColumns + " FROM items " + where + " ORDER BY id;";
                    foreach (var p in args)
                        cmd.Parameters.Add(CloneParameter(p));
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var entry = ReadItem(reader);
                        if (!postFilter(entry))
                            continue;
                        if (total >= offset && results.Count < limit)
                            results.Add(entry);
                        total++;
                    }
                }

                return results;
            }
        }

        public void GetItemCategories(out object[] equipment, out object[] stackable)
        {
            lock (_gate)
            {
                equipment = Array.Empty<object>();
                stackable = Array.Empty<object>();
                if (!_ready)
                    return;

                var equipList = new List<object>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COALESCE(type_tag, '(无标签)') AS tag, COUNT(*) AS cnt
FROM items WHERE kind='equipment'
GROUP BY COALESCE(type_tag, '(无标签)')
ORDER BY tag;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        equipList.Add(new { tag = reader.GetString(0), count = reader.GetInt32(1) });
                }

                var stackList = new List<object>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COALESCE(segment, '消耗品') AS segment, COUNT(*) AS cnt
FROM items WHERE kind='stackable'
GROUP BY COALESCE(segment, '消耗品')
ORDER BY segment;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        stackList.Add(new { segment = reader.GetString(0), count = reader.GetInt32(1) });
                }

                equipment = equipList.ToArray();
                stackable = stackList.ToArray();
            }
        }

        public PvfIndexService.QuestMeta GetQuest(int questId)
        {
            lock (_gate)
            {
                EnsureReady();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT " + QuestSelectColumns + " FROM quests WHERE id=@id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", questId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadQuest(reader) : null;
            }
        }

        public Dictionary<int, PvfIndexService.QuestMeta> LoadAllQuests()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new Dictionary<int, PvfIndexService.QuestMeta>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT " + QuestSelectColumns + " FROM quests ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var meta = ReadQuest(reader);
                    result[meta.Id] = meta;
                }
                return result;
            }
        }

        public List<PvfIndexService.ItemEntry> LoadAllItems()
        {
            lock (_gate)
            {
                EnsureReady();
                var result = new List<PvfIndexService.ItemEntry>(_itemCount);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT " + ItemSelectColumns + " FROM items ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(ReadItem(reader));
                return result;
            }
        }

        public List<PvfIndexService.ItemEntry> FindItems(Func<PvfIndexService.ItemEntry, bool> predicate, int limit = 1)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));
            if (limit <= 0)
                limit = 1;

            lock (_gate)
            {
                EnsureReady();
                var result = new List<PvfIndexService.ItemEntry>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT " + ItemSelectColumns + " FROM items ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var entry = ReadItem(reader);
                    if (!predicate(entry))
                        continue;
                    result.Add(entry);
                    if (result.Count >= limit)
                        break;
                }
                return result;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                CloseUnlocked();
            }
        }

        private void EnsureReady()
        {
            if (!_ready || _conn == null)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(_buildError) ? "PVF 索引尚未就绪。" : "PVF 索引失败: " + _buildError);
        }

        private void CloseUnlocked()
        {
            if (_conn != null)
            {
                try { _conn.Close(); } catch { /* ignore */ }
                try { _conn.Dispose(); } catch { /* ignore */ }
                _conn = null;
            }
            _ready = false;
        }

        private void UpsertMeta(string key, string value)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES(@k, @v);";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        private string ReadMeta(string key)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=@k LIMIT 1;";
            cmd.Parameters.AddWithValue("@k", key);
            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? null
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static SqliteConnection OpenConnection(string path)
        {
            // Pooling off: rebuild deletes/renames the db file; pooled handles would pin
            // the old inode and surface "no such column" after schema upgrades.
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA locking_mode=NORMAL;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        private static void ApplySchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta(
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS items(
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  type_tag TEXT,
  segment TEXT,
  special TEXT,
  rarity INTEGER NOT NULL DEFAULT 0,
  min_level INTEGER NOT NULL DEFAULT 0,
  grade INTEGER NOT NULL DEFAULT 0,
  usable_job TEXT,
  abs_expire INTEGER NOT NULL DEFAULT 0,
  usable_days INTEGER NOT NULL DEFAULT 0,
  daily_delete INTEGER NOT NULL DEFAULT 0,
  invalid_expire INTEGER NOT NULL DEFAULT 0,
  requires_manual INTEGER NOT NULL DEFAULT 0,
  requires_config INTEGER NOT NULL DEFAULT 0,
  supports_quality INTEGER NOT NULL DEFAULT 0,
  file_path TEXT,
  type_full TEXT,
  item_category TEXT,
  attach_type TEXT,
  stack_limit INTEGER NOT NULL DEFAULT 1,
  durability INTEGER NOT NULL DEFAULT 0,
  impossible_json TEXT,
  ability_case_index INTEGER NOT NULL DEFAULT -1,
  avatar_select_json TEXT,
  avatar_durations_json TEXT
);
CREATE TABLE IF NOT EXISTS archive_paths(
  path TEXT PRIMARY KEY COLLATE NOCASE,
  file_index INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS quests(
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  grade TEXT NOT NULL DEFAULT '',
  min_level INTEGER NOT NULL DEFAULT 0,
  max_level INTEGER NOT NULL DEFAULT 99,
  pre_required_json TEXT NOT NULL DEFAULT '[]',
  pre_groups_json TEXT NOT NULL DEFAULT '[]',
  pre_answer_json TEXT NOT NULL DEFAULT '[]',
  collision_json TEXT NOT NULL DEFAULT '[]',
  region TEXT NOT NULL DEFAULT '',
  job TEXT NOT NULL DEFAULT '',
  grow_type INTEGER NOT NULL DEFAULT 0,
  job_change_quest INTEGER NOT NULL DEFAULT 0,
  grow_number INTEGER NOT NULL DEFAULT 0,
  reward_chain_type INTEGER NOT NULL DEFAULT 0,
  target_character TEXT NOT NULL DEFAULT '',
  exposed_by_npc INTEGER NOT NULL DEFAULT -1,
  is_event INTEGER NOT NULL DEFAULT 0,
  creature_kind INTEGER NOT NULL DEFAULT 0,
  expert_job_type INTEGER NOT NULL DEFAULT 0,
  expert_job_level INTEGER NOT NULL DEFAULT 0,
  target_dungeon_id INTEGER NOT NULL DEFAULT -1,
  target_map_id INTEGER NOT NULL DEFAULT -1,
  target_quest_id INTEGER NOT NULL DEFAULT -1,
  target_level INTEGER NOT NULL DEFAULT -1,
  linked_dungeon_id INTEGER NOT NULL DEFAULT -1,
  reward_title_item_id INTEGER NOT NULL DEFAULT -1,
  reward_item_ids_json TEXT NOT NULL DEFAULT '[]',
  reward_selection_item_ids_json TEXT NOT NULL DEFAULT '[]',
  exception_quest TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS region_names(
  key TEXT PRIMARY KEY,
  name TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS dungeon_regions(
  dungeon_id INTEGER PRIMARY KEY,
  region TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS map_dungeons(
  map_id INTEGER PRIMARY KEY,
  dungeon_id INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS dungeon_permissions(
  dungeon_id INTEGER PRIMARY KEY
);
CREATE TABLE IF NOT EXISTS open_hubs(
  key TEXT PRIMARY KEY
);
CREATE TABLE IF NOT EXISTS jobs(
  job_id INTEGER PRIMARY KEY,
  base_name TEXT NOT NULL DEFAULT '',
  grow_names_json TEXT NOT NULL DEFAULT '[]',
  awakening_json TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE IF NOT EXISTS premium_items(
  item_code INTEGER PRIMARY KEY,
  premium_type INTEGER NOT NULL,
  duration_days INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS amplify_config(
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS avatar_ability_names(
  token TEXT PRIMARY KEY COLLATE NOCASE,
  name TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS avatar_ability_cases(
  case_index INTEGER PRIMARY KEY,
  entries_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS skill_names(
  job INTEGER NOT NULL,
  skill_index INTEGER NOT NULL,
  name TEXT NOT NULL,
  PRIMARY KEY(job, skill_index)
);
CREATE TABLE IF NOT EXISTS job_stat_tables(
  job_id INTEGER PRIMARY KEY,
  tables_json TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }

        private const string ItemSelectColumns =
            "id, name, kind, type_tag, segment, special, rarity, min_level, grade, usable_job, "
            + "abs_expire, usable_days, daily_delete, invalid_expire, requires_manual, requires_config, supports_quality, file_path, "
            + "type_full, item_category, attach_type, stack_limit, durability, impossible_json, "
            + "ability_case_index, avatar_select_json, avatar_durations_json";

        private const string QuestSelectColumns =
            "id, name, grade, min_level, max_level, pre_required_json, pre_groups_json, pre_answer_json, collision_json, "
            + "region, job, grow_type, job_change_quest, grow_number, reward_chain_type, target_character, exposed_by_npc, "
            + "is_event, creature_kind, expert_job_type, expert_job_level, target_dungeon_id, target_map_id, target_quest_id, "
            + "target_level, linked_dungeon_id, reward_title_item_id, reward_item_ids_json, reward_selection_item_ids_json, exception_quest";

        private static PvfIndexService.ItemEntry ReadItem(SqliteDataReader reader)
        {
            return new PvfIndexService.ItemEntry
            {
                Id = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Kind = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                TypeTag = reader.IsDBNull(3) ? null : reader.GetString(3),
                Segment = reader.IsDBNull(4) ? null : reader.GetString(4),
                Special = reader.IsDBNull(5) ? null : reader.GetString(5),
                Rarity = reader.GetInt32(6),
                MinLevel = reader.GetInt32(7),
                Grade = reader.GetInt32(8),
                UsableJob = reader.IsDBNull(9) ? null : reader.GetString(9),
                AbsoluteExpirationUnixTime = reader.GetInt32(10),
                UsablePeriodDays = reader.GetInt32(11),
                DailyDeleteItem = reader.GetInt32(12) != 0,
                HasInvalidExpirationDefinition = reader.GetInt32(13) != 0,
                RequiresManualGrantType = reader.GetInt32(14) != 0,
                RequiresConfiguration = reader.GetInt32(15) != 0,
                SupportsQuality = reader.GetInt32(16) != 0,
                FilePath = reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetString(17) : null,
                TypeFull = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : null,
                ItemCategory = reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
                AttachType = reader.FieldCount > 20 && !reader.IsDBNull(20) ? reader.GetString(20) : null,
                StackLimit = reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetInt32(21) : 1,
                Durability = reader.FieldCount > 22 && !reader.IsDBNull(22) ? reader.GetInt32(22) : 0,
                ImpossibleJson = reader.FieldCount > 23 && !reader.IsDBNull(23) ? reader.GetString(23) : null,
                AbilityCaseIndex = reader.FieldCount > 24 && !reader.IsDBNull(24) ? reader.GetInt32(24) : -1,
                AvatarSelectJson = reader.FieldCount > 25 && !reader.IsDBNull(25) ? reader.GetString(25) : null,
                AvatarDurationsJson = reader.FieldCount > 26 && !reader.IsDBNull(26) ? reader.GetString(26) : null,
            };
        }

        private static PvfIndexService.QuestMeta ReadQuest(SqliteDataReader reader)
        {
            return new PvfIndexService.QuestMeta
            {
                Id = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Grade = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                MinLevel = reader.GetInt32(3),
                MaxLevel = reader.GetInt32(4),
                PreRequired = DeserializeIntArray(reader.GetString(5)),
                PreGroups = DeserializeIntArrays(reader.GetString(6)),
                PreRequiredQuestAnswer = DeserializeIntArray(reader.GetString(7)),
                CollisionQuest = DeserializeIntArray(reader.GetString(8)),
                Region = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                Job = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                GrowType = reader.GetInt32(11),
                JobChangeQuestValue = reader.GetInt32(12),
                GrowNumber = reader.GetInt32(13),
                RewardChainType = reader.GetInt32(14),
                TargetCharacter = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                ExposedByNpc = reader.GetInt32(16),
                IsEvent = reader.GetInt32(17) != 0,
                CreatureKind = reader.GetInt32(18),
                ExpertJobType = reader.GetInt32(19),
                ExpertJobLevel = reader.GetInt32(20),
                TargetDungeonId = reader.GetInt32(21),
                TargetMapId = reader.GetInt32(22),
                TargetQuestId = reader.GetInt32(23),
                TargetLevel = reader.GetInt32(24),
                LinkedDungeonId = reader.GetInt32(25),
                RewardTitleItemId = reader.GetInt32(26),
                RewardItemIds = DeserializeIntArray(reader.GetString(27)),
                RewardSelectionItemIds = DeserializeIntArray(reader.GetString(28)),
                ExceptionQuest = reader.IsDBNull(29) ? string.Empty : reader.GetString(29),
            };
        }

        private static string SerializeIntArray(int[] values)
        {
            return JsonSerializer.Serialize(values ?? Array.Empty<int>());
        }

        private static string SerializeIntArrays(int[][] values)
        {
            return JsonSerializer.Serialize(values ?? Array.Empty<int[]>());
        }

        private static int[] DeserializeIntArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<int>();
            try
            {
                return JsonSerializer.Deserialize<int[]>(json) ?? Array.Empty<int>();
            }
            catch
            {
                return Array.Empty<int>();
            }
        }

        private static int[][] DeserializeIntArrays(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<int[]>();
            try
            {
                return JsonSerializer.Deserialize<int[][]>(json) ?? Array.Empty<int[]>();
            }
            catch
            {
                return Array.Empty<int[]>();
            }
        }

        private static void AppendInClause(
            StringBuilder where,
            List<SqliteParameter> args,
            string column,
            string prefix,
            HashSet<string> values)
        {
            where.Append(" AND ").Append(column).Append(" IN (");
            var i = 0;
            foreach (var value in values)
            {
                if (i > 0)
                    where.Append(',');
                var name = "@" + prefix + i;
                where.Append(name);
                args.Add(new SqliteParameter(name, value));
                i++;
            }
            where.Append(')');
        }

        private static SqliteParameter CloneParameter(SqliteParameter source)
        {
            return new SqliteParameter(source.ParameterName, source.Value ?? DBNull.Value);
        }
    }
}
