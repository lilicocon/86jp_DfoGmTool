using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DfoGmTool.ServerCore.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        private static int _inventoryAnomalyRunning;

        public object GetInventoryAnomalyStatus(PvfIndexService pvfIndex)
        {
            if (!TryGetLegalItemIds(pvfIndex, out var legalIds, out var legalIdsError))
                return legalIdsError;

            if (Volatile.Read(ref _inventoryAnomalyRunning) != 0)
                return InventoryAnomalyResponse(
                    new InventoryAnomalySnapshot(),
                    running: true,
                    deletedCount: 0);

            try
            {
                using var connection = new SqliteConnection(_config.ConnectionString);
                connection.Open();
                var snapshot = ScanInventoryAnomalies(connection, null, legalIds);
                return InventoryAnomalyResponse(snapshot, running: false, deletedCount: 0);
            }
            catch (Exception ex) when (ex is SqliteException || ex is InvalidOperationException)
            {
                return InventoryAnomalyError("扫描异常库存失败: " + ex.Message);
            }
        }

        public object CleanInventoryAnomalies(PvfIndexService pvfIndex)
        {
            if (!TryGetLegalItemIds(pvfIndex, out var legalIds, out var legalIdsError))
                return legalIdsError;

            if (Interlocked.CompareExchange(ref _inventoryAnomalyRunning, 1, 0) != 0)
                return InventoryAnomalyError("异常库存清理正在运行，请稍后重试", running: true);

            var commitAttempted = false;
            var commitSucceeded = false;
            var deletedCount = 0;
            InventoryAnomalySnapshot remaining = null;
            try
            {
                using (var connection = new SqliteConnection(_config.ConnectionString))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction(deferred: false);
                    var before = ScanInventoryAnomalies(connection, transaction, legalIds);
                    foreach (var anomaly in before.Records)
                    {
                        if (anomaly.Source == InventoryAnomalySource.Character)
                        {
                            _inventory.DeleteCharacterAnomalyCore(
                                connection,
                                transaction,
                                anomaly.ItemUid,
                                anomaly.CharacterId,
                                anomaly.AccountId,
                                (InventoryListType)anomaly.ListType,
                                checked((short)anomaly.SlotIndex),
                                anomaly.ItemCoreBytes);
                        }
                        else
                        {
                            _inventory.DeleteAccountCargoAnomalyCore(
                                connection,
                                transaction,
                                anomaly.ItemUid,
                                anomaly.AccountId,
                                anomaly.CharacterId,
                                checked((short)anomaly.SlotIndex),
                                anomaly.ItemCoreBytes);
                        }
                    }

                    deletedCount = before.Records.Count;
                    remaining = ScanInventoryAnomalies(connection, transaction, legalIds);
                    commitAttempted = true;
                    transaction.Commit();
                    commitSucceeded = true;
                }
            }
            catch (Exception ex) when (ex is SqliteException
                                       || ex is InvalidOperationException
                                       || ex is OverflowException
                                       || ex is ArgumentException)
            {
                if (!commitAttempted)
                    return InventoryAnomalyError("清理异常库存失败，事务已回滚: " + ex.Message);
                if (!commitSucceeded)
                    return InventoryAnomalyError("清理异常库存失败，事务提交结果不确定，请核查: " + ex.Message);

                return InventoryAnomalyResponse(
                    remaining ?? new InventoryAnomalySnapshot(),
                    running: false,
                    deletedCount,
                    statusRefreshError: "清理已提交，但状态刷新阶段失败: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _inventoryAnomalyRunning, 0);
            }

            return InventoryAnomalyResponse(
                remaining ?? new InventoryAnomalySnapshot(),
                running: false,
                deletedCount);
        }

        internal static bool TryAcceptLegalItemIds(bool pvfReady, IReadOnlyCollection<int> legalIds, out string error)
        {
            error = null;
            if (!pvfReady)
            {
                error = "PVF 索引尚未就绪，无法判断合法物品 ID";
                return false;
            }

            if (legalIds == null || legalIds.Count == 0)
            {
                error = "当前 PVF 合法物品集合为空，已拒绝扫描/清理以免误删库存";
                return false;
            }

            return true;
        }

        private static bool TryGetLegalItemIds(PvfIndexService pvfIndex, out HashSet<int> legalIds, out object error)
        {
            legalIds = null;
            error = null;
            var ready = pvfIndex != null && pvfIndex.IsReady;
            var ids = ready ? pvfIndex.CopyValidItemIds() : null;
            if (!TryAcceptLegalItemIds(ready, ids, out var message))
            {
                error = InventoryAnomalyError(message);
                return false;
            }

            legalIds = ids as HashSet<int> ?? new HashSet<int>(ids);
            return true;
        }

        private static object InventoryAnomalyError(string error, bool running = false)
        {
            return new
            {
                success = false,
                running,
                hasAnomalies = false,
                totalCount = 0,
                characterCount = 0,
                accountCargoCount = 0,
                details = Array.Empty<object>(),
                deletedCount = 0,
                error,
            };
        }

        private static object InventoryAnomalyResponse(
            InventoryAnomalySnapshot snapshot,
            bool running,
            int deletedCount,
            string statusRefreshError = null)
        {
            snapshot ??= new InventoryAnomalySnapshot();
            var details = snapshot.Records
                .OrderBy(value => value.Source)
                .ThenBy(value => value.AccountId)
                .ThenBy(value => value.CharacterId)
                .ThenBy(value => value.ListType)
                .ThenBy(value => value.SlotIndex)
                .ThenBy(value => value.ItemUid)
                .Select(value => (object)new
                {
                    source = value.Source == InventoryAnomalySource.Character ? "character" : "accountCargo",
                    accountId = value.AccountId,
                    characterId = value.CharacterId,
                    characterName = value.CharacterName,
                    listType = value.ListType,
                    container = value.Container,
                    slot = value.SlotIndex,
                    itemId = value.ItemId,
                    itemUid = value.ItemUid,
                    reason = value.Reason,
                })
                .ToArray();
            return new
            {
                success = true,
                running,
                hasAnomalies = details.Length > 0,
                totalCount = details.Length,
                characterCount = snapshot.Records.Count(value => value.Source == InventoryAnomalySource.Character),
                accountCargoCount = snapshot.Records.Count(value => value.Source == InventoryAnomalySource.AccountCargo),
                details,
                deletedCount,
                statusRefreshError,
            };
        }

        private static InventoryAnomalySnapshot ScanInventoryAnomalies(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISet<int> legalIds)
        {
            var result = new InventoryAnomalySnapshot();
            ScanCharacterAnomalies(connection, transaction, legalIds, result.Records);
            ScanAccountCargoAnomalies(connection, transaction, legalIds, result.Records);
            return result;
        }

        private static void ScanCharacterAnomalies(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISet<int> legalIds,
            List<InventoryAnomalyRecord> records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT n.item_uid, n.owner_id, n.character_id, n.list_type, n.slot_index,
       n.item_core, c.account_id, c.name
FROM character_new_items n
LEFT JOIN characters c ON c.character_id=COALESCE(n.character_id, n.owner_id)
WHERE n.owner_scope='character'
ORDER BY n.owner_id, n.list_type, n.slot_index, n.item_uid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var characterId = reader.IsDBNull(2) ? reader.GetInt32(1) : reader.GetInt32(2);
                var listType = reader.GetInt32(3);
                var slotIndex = reader.GetInt32(4);
                if (listType == (int)InventoryListType.Main && slotIndex >= 0 && slotIndex <= 2)
                    continue;

                var bytes = ReadAnomalyBlob(reader, 5);
                var record = BuildAnomalyRecord(
                    InventoryAnomalySource.Character,
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    characterId,
                    reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    listType,
                    slotIndex,
                    reader.GetInt64(0),
                    bytes,
                    legalIds);
                if (record != null)
                    records.Add(record);
            }
        }

        private static void ScanAccountCargoAnomalies(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISet<int> legalIds,
            List<InventoryAnomalyRecord> records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT n.item_uid, n.account_id, n.character_id, n.list_type, n.slot_index,
       n.item_core, c.name
FROM account_cargo_new_items n
LEFT JOIN characters c ON c.character_id=n.character_id
ORDER BY n.account_id, n.list_type, n.slot_index, n.item_uid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bytes = ReadAnomalyBlob(reader, 5);
                var record = BuildAnomalyRecord(
                    InventoryAnomalySource.AccountCargo,
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt64(0),
                    bytes,
                    legalIds);
                if (record != null)
                    records.Add(record);
            }
        }

        private static byte[] ReadAnomalyBlob(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
                return null;
            return reader.GetValue(ordinal) as byte[];
        }

        private static InventoryAnomalyRecord BuildAnomalyRecord(
            InventoryAnomalySource source,
            int accountId,
            int characterId,
            string characterName,
            int listType,
            int slotIndex,
            long itemUid,
            byte[] itemCoreBytes,
            ISet<int> legalIds)
        {
            var container = ResolveAnomalyContainer(source, listType);
            if (itemCoreBytes == null || itemCoreBytes.Length != ItemCore.Size)
            {
                return NewAnomaly(
                    source, accountId, characterId, characterName, listType, container,
                    slotIndex, itemUid, 0, itemCoreBytes, "item_core_null_or_invalid_length");
            }

            ItemCore core;
            try
            {
                core = ItemCore.FromBytes(itemCoreBytes);
            }
            catch
            {
                return NewAnomaly(
                    source, accountId, characterId, characterName, listType, container,
                    slotIndex, itemUid, 0, itemCoreBytes, "item_core_decode_failed");
            }

            if (core.ItemId <= 0)
            {
                return NewAnomaly(
                    source, accountId, characterId, characterName, listType, container,
                    slotIndex, itemUid, core.ItemId, itemCoreBytes, "item_id_non_positive");
            }
            if (legalIds == null || !legalIds.Contains(core.ItemId))
            {
                return NewAnomaly(
                    source, accountId, characterId, characterName, listType, container,
                    slotIndex, itemUid, core.ItemId, itemCoreBytes, "item_id_not_in_pvf");
            }
            return null;
        }

        private static InventoryAnomalyRecord NewAnomaly(
            InventoryAnomalySource source,
            int accountId,
            int characterId,
            string characterName,
            int listType,
            string container,
            int slotIndex,
            long itemUid,
            int itemId,
            byte[] itemCoreBytes,
            string reason)
        {
            return new InventoryAnomalyRecord
            {
                Source = source,
                AccountId = accountId,
                CharacterId = characterId,
                CharacterName = characterName,
                ListType = listType,
                Container = container,
                SlotIndex = slotIndex,
                ItemUid = itemUid,
                ItemId = itemId,
                ItemCoreBytes = itemCoreBytes,
                Reason = reason,
            };
        }

        private static string ResolveAnomalyContainer(InventoryAnomalySource source, int listType)
        {
            if (source == InventoryAnomalySource.AccountCargo)
                return "账号金库";
            return (InventoryListType)listType switch
            {
                InventoryListType.Main => "主背包",
                InventoryListType.Equipment => "穿戴装备",
                InventoryListType.Avatar => "时装",
                InventoryListType.PersonalCargo => "个人仓库",
                InventoryListType.Pet => "宠物",
                _ => "角色列表" + listType,
            };
        }
    }

    internal enum InventoryAnomalySource
    {
        Character,
        AccountCargo,
    }

    internal sealed class InventoryAnomalyRecord
    {
        internal InventoryAnomalySource Source { get; set; }
        internal int AccountId { get; set; }
        internal int CharacterId { get; set; }
        internal string CharacterName { get; set; }
        internal int ListType { get; set; }
        internal string Container { get; set; }
        internal int SlotIndex { get; set; }
        internal int ItemId { get; set; }
        internal long ItemUid { get; set; }
        internal byte[] ItemCoreBytes { get; set; }
        internal string Reason { get; set; }
    }

    internal sealed class InventoryAnomalySnapshot
    {
        internal List<InventoryAnomalyRecord> Records { get; } = new List<InventoryAnomalyRecord>();
    }
}
