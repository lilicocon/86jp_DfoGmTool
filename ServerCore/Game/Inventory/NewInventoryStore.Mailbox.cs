using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.ServerCore.Game.Inventory
{
    internal sealed class MailboxInventorySource
    {
        internal InventoryListType ListType { get; set; }
        internal short SlotIndex { get; set; }
        internal long ItemUid { get; set; }
        internal ItemCore Core { get; set; }
    }

    public sealed partial class NewInventoryStore
    {
        internal static bool TryReadMailboxSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            byte itemType,
            ushort slotIndex,
            int itemId,
            int count,
            out MailboxInventorySource source)
        {
            source = null;
            if (itemId <= 0 || count <= 0 || !TryMapMailboxItemType(itemType, out var listType))
                return false;
            if (!TryLoadItem(connection, transaction, characterId, accountId, listType, checked((short)slotIndex), out var record)
                || record.ItemUid <= 0 || record.Core.ItemId != itemId || record.Core.EquipmentLockId != 0)
                return false;
            if (IsStackableKind(record.Core.ItemKind))
            {
                if (record.Core.Count < count)
                    return false;
            }
            else if (count != 1)
            {
                return false;
            }

            source = new MailboxInventorySource
            {
                ListType = listType,
                SlotIndex = checked((short)slotIndex),
                ItemUid = record.ItemUid,
                Core = record.Core.Copy(),
            };
            if (IsStackableKind(source.Core.ItemKind))
                source.Core.Count = count;
            return true;
        }

        internal static bool TryConsumeMailboxSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            MailboxInventorySource source,
            int count)
        {
            if (source == null || count <= 0
                || !TryLoadItem(connection, transaction, characterId, accountId, source.ListType, source.SlotIndex, out var record)
                || record.ItemUid != source.ItemUid)
                return false;

            var before = record.Core.Copy();
            if (IsStackableKind(record.Core.ItemKind))
            {
                if (record.Core.Count < count)
                    return false;
                if (record.Core.Count > count)
                {
                    record.Core.Count -= count;
                    UpdateCore(connection, transaction, record, before, "mail_send_partial");
                    return true;
                }
            }
            else if (count != 1)
            {
                return false;
            }

            DeleteCoreRow(connection, transaction, record);
            DeleteAssociatedState(connection, transaction, characterId, record.Core);
            WriteAudit(connection, transaction, "mail_send", characterId, accountId, record.ListType, record.SlotIndex, before, null, record.ItemUid);
            return true;
        }

        internal static bool TryGrantMailboxCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            ItemCore sourceCore,
            int count,
            IList<short> affectedSlots,
            out string error)
        {
            error = null;
            if (sourceCore == null || sourceCore.ItemId <= 0 || count <= 0)
            {
                error = "邮件附件无效";
                return false;
            }
            if (!TryGetCharacterOpenRange(connection, transaction, characterId, sourceCore.ItemKind,
                    out var listType, out var start, out var end, out error))
                return false;

            var core = sourceCore.Copy();
            var stackable = IsStackableKind(core.ItemKind);
            // 邮件附件 ItemCore 已在发送时编码；领取时 PVF 不可用（自测/离线）不应抛异常。
            ItemMetadata metadata = null;
            try
            {
                metadata = ItemMetadataResolver.Resolve(core.ItemId);
            }
            catch
            {
                metadata = null;
            }
            var remaining = stackable ? count : 1;
            var stackLimit = stackable
                ? (metadata != null && metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue)
                : 1;
            if (stackable)
            {
                foreach (var existing in LoadList(connection, transaction, characterId, listType, start, end))
                {
                    if (remaining <= 0 || !CanStackWith(existing.Core, core))
                        continue;
                    var added = (int)Math.Min(Math.Max(0L, (long)stackLimit - existing.Core.Count), remaining);
                    if (added <= 0)
                        continue;
                    var before = existing.Core.Copy();
                    existing.Core.Count += added;
                    UpdateCore(connection, transaction, existing, before, "mail_claim_stack");
                    AddSlot(affectedSlots, existing.SlotIndex);
                    remaining -= added;
                }
            }

            var occupied = LoadOccupiedSlots(connection, transaction, characterId, listType, start, end);
            while (remaining > 0)
            {
                var slot = FirstFree(occupied, start, end);
                if (slot < 0)
                {
                    error = "背包空间不足";
                    return false;
                }
                var perSlot = stackable
                    ? Math.Min(remaining, stackLimit == int.MaxValue ? remaining : stackLimit)
                    : 1;
                var granted = core.Copy();
                if (stackable)
                    granted.Count = perSlot;
                if (granted.ItemKind == ItemCore.KindAvatar)
                {
                    var uid = AllocateSequence(connection, transaction, "character_avatar_uid_sequence", "avatar_uid");
                    granted.AvatarUid = checked((int)uid);
                    InsertAvatarDetail(connection, transaction, uid, accountId, characterId, granted.ItemId, 0);
                }
                else if (granted.ItemKind == ItemCore.KindCreature)
                {
                    var uid = AllocateSequence(connection, transaction, "character_creature_uid_sequence", "creature_uid");
                    granted.CreatureUid = checked((int)uid);
                    InsertCreatureDetail(connection, transaction, characterId, uid);
                }
                granted.EquipmentLockId = 0;
                var itemUid = InsertCharacterCore(connection, transaction, characterId, listType, checked((short)slot), granted);
                WriteAudit(connection, transaction, "mail_claim", characterId, accountId, listType, checked((short)slot), null, granted, itemUid);
                occupied.Add(checked((short)slot));
                AddSlot(affectedSlots, checked((short)slot));
                remaining -= perSlot;
            }
            return true;
        }

        internal static bool TryChangeMailboxGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int delta,
            out int value)
        {
            value = 0;
            if (!TryLoadItem(connection, transaction, characterId, accountId, InventoryListType.Main, 0, out var record))
                return false;
            var next = (long)record.Core.Count + delta;
            if (next < 0 || next > int.MaxValue)
                return false;
            var before = record.Core.Copy();
            record.Core.Count = (int)next;
            UpdateCore(connection, transaction, record, before, delta < 0 ? "mail_send_gold" : "mail_claim_gold");
            value = record.Core.Count;
            return true;
        }

        internal static bool TryGetMailboxGold(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, out int gold)
        {
            gold = 0;
            return TryLoadItem(connection, transaction, characterId, accountId, InventoryListType.Main, 0, out var record)
                && (gold = record.Core.Count) >= 0;
        }

        private static bool TryMapMailboxItemType(byte itemType, out InventoryListType listType)
        {
            listType = itemType switch
            {
                1 => InventoryListType.Avatar,
                3 or 7 => InventoryListType.Pet,
                _ => InventoryListType.Main,
            };
            return true;
        }

        internal static bool TryMapMailboxItemKindForCount(byte itemKind, out InventoryListType listType)
        {
            listType = itemKind switch
            {
                ItemCore.KindAvatar => InventoryListType.Avatar,
                ItemCore.KindCreature or ItemCore.KindCreatureEquipment or ItemCore.KindCreatureConsumable
                    => InventoryListType.Pet,
                _ => InventoryListType.Main,
            };
            return true;
        }

        internal static int CountItemsByTemplate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            InventoryListType listType,
            int itemTemplateId,
            int expireTime)
        {
            if (itemTemplateId <= 0)
                return 0;

            if (!TryGetCharacterOpenRange(connection, transaction, characterId,
                    listType == InventoryListType.Avatar ? ItemCore.KindAvatar
                    : listType == InventoryListType.Pet ? ItemCore.KindCreature
                    : ItemCore.KindConsumable,
                    out var resolvedList, out var start, out var end, out _))
            {
                // Fall back to scanning the requested list with a wide range when open range is unavailable.
                resolvedList = listType;
                start = 0;
                end = short.MaxValue;
            }

            var total = 0;
            foreach (var existing in LoadList(connection, transaction, characterId, resolvedList, start, end))
            {
                if (existing.Core.ItemId != itemTemplateId)
                    continue;
                if (IsStackableKind(existing.Core.ItemKind) && existing.Core.ExpireTime != expireTime && expireTime > 0)
                    continue;
                total += IsStackableKind(existing.Core.ItemKind) ? Math.Max(0, existing.Core.Count) : 1;
            }

            return total;
        }

        private static bool CanStackWith(ItemCore existing, ItemCore incoming)
        {
            if (!IsStackableKind(existing.ItemKind) || existing.ItemKind != incoming.ItemKind || existing.ItemId != incoming.ItemId)
                return false;
            var left = existing.Copy();
            var right = incoming.Copy();
            left.Count = right.Count = 0;
            return left.ToBytes().SequenceEqual(right.ToBytes());
        }

        private static void AddSlot(IList<short> slots, short slot)
        {
            if (slots != null && !slots.Contains(slot))
                slots.Add(slot);
        }
    }
}
