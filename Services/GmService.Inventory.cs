using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DfoGmTool.ServerCore.Game.TitleBook;
using DfoGmTool.ServerCore.Game.Characters;
using DfoGmTool.ServerCore.Game.Currency;
using DfoGmTool.ServerCore.Game.Dungeon;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.Mailbox;
using DfoGmTool.ServerCore.Game.Premium;
using DfoGmTool.ServerCore.Game.Quests;
using DfoGmTool.ServerCore.Game.ReviveCoin;
using GmPvfLib;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        private const short NameTagEquippedSlot = 28;
        private const int DefaultNameTagGrantDays = 30;
        private const long SecondsPerDay = 86400L;

        // 读侧从新版 ItemCore 投影页面模型，不读取旧 character_items。
        public object ListItems(int characterId, PvfIndexService pvfIndex)
        {
            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var snapshot = _inventory.LoadCharacterItems(characterId, accountId);
            var rentalExpireTimes = _supplementalItemExpiration.LoadRentalExpireTimes(characterId);
            TryLoadGrantCharacter(characterId, out var job, out _, out _);

            var items = new List<object>();

            // Source 1:1: 主背包虚拟槽(金币/复活币/胜点)单独列出, 始终展示且不可删除
            var wallet = _inventory.LoadWallet(characterId);
            var virtualCounts = new[]
            {
                (Slot: 0, TemplateId: 0, Count: wallet.Gold),
                (Slot: 1, TemplateId: 1, Count: wallet.ReviveCoin),
                (Slot: 2, TemplateId: 2, Count: wallet.Sp),
            };
            foreach (var virtualItem in virtualCounts)
            {
                items.Add(new
                {
                    container = "主背包",
                    category = "货币",
                    listType = (int)InventoryListType.Main,
                    slot = virtualItem.Slot,
                    templateId = virtualItem.TemplateId,
                    name = pvfIndex.ResolveItemName(virtualItem.TemplateId),
                    kind = "special",
                    rarity = 0,
                    count = virtualItem.Count,
                    instanceValue = virtualItem.Count,
                    durability = 0,
                    serial = 0,
                    expireTime = 0,
                    supplementalExpiration = (object)null,
                    templateExpiration = CreateTemplateExpiration(pvfIndex, virtualItem.TemplateId),
                    seal = 0,
                    deletable = false,
                    countEditable = false,
                    configurable = false,
                    expirationConfigurable = false,
                    configKind = (string)null,
                });
            }

            foreach (var item in snapshot)
            {
                // 主背包 0-2 虚拟槽由上方通道单独展示
                if (item.ListType == InventoryListType.Main && item.SlotIndex <= 2)
                    continue;

                var kind = item.ItemKind;
                // Prefer disk index flags so listing inventory does not open the full PVF archive.
                var indexed = pvfIndex.GetItem(item.ItemTemplateId);
                string configKind = null;
                bool expirationConfigurable;
                if (indexed != null)
                {
                    configKind = ResolveInventoryConfigKindFromIndex(indexed, kind, item.ListType);
                    expirationConfigurable = CanConfigureInventoryExpirationFromIndex(
                        indexed, kind, item.ExpireTime);
                }
                else
                {
                    configKind = ResolveInventoryConfigKind(item.ItemTemplateId, kind, item.ListType, job, pvfIndex);
                    expirationConfigurable = CanConfigureInventoryExpiration(item.ItemTemplateId, kind, item.ExpireTime);
                }
                // Source container labels: 穿戴栏 / 时装（非「主背包」合并）
                var container = item.ListType switch
                {
                    InventoryListType.PersonalCargo => "个人仓库",
                    InventoryListType.AccountCargo => "账号金库",
                    InventoryListType.Equipment => "穿戴栏",
                    InventoryListType.Avatar => "时装",
                    InventoryListType.Pet => "宠物",
                    _ => "主背包",
                };
                var category = item.ListType switch
                {
                    InventoryListType.Main => ResolveMainSegment(item.SlotIndex),
                    InventoryListType.Equipment => "穿戴装备",
                    InventoryListType.Avatar => "时装",
                    InventoryListType.Pet => ResolvePetSegment(item.SlotIndex),
                    _ => container,
                };
                items.Add(new
                {
                    container,
                    category,
                    listType = (int)item.ListType,
                    slot = (int)item.SlotIndex,
                    templateId = item.ItemTemplateId,
                    name = indexed?.Name ?? pvfIndex.ResolveItemName(item.ItemTemplateId),
                    kind,
                    rarity = indexed?.Rarity ?? pvfIndex.ResolveItemRarity(item.ItemTemplateId),
                    count = item.Count,
                    instanceValue = item.InstanceValue,
                    durability = (int)item.Core.Durability,
                    serial = item.Core.ItemKind == ItemCore.KindCreature ? item.Core.CreatureUid : 0,
                    expireTime = item.ExpireTime,
                    supplementalExpiration = CreateSupplementalExpiration(rentalExpireTimes, item.ItemTemplateId, item.ExpireTime),
                    templateExpiration = indexed != null
                        ? CreateTemplateExpirationFromIndex(indexed)
                        : CreateTemplateExpiration(pvfIndex, item.ItemTemplateId),
                    seal = (int)item.Core.SealFlag,
                    deletable = IsDeletable(item.ListType, item.SlotIndex),
                    countEditable = CanEditItemCount(item.ListType, item.SlotIndex, item.Core.ItemKind),
                    configurable = configKind != null || expirationConfigurable,
                    expirationConfigurable,
                    configKind,
                });
            }

            // Source 1:1: 晶块是账号级货币(accounts.cube_*), 列表展示且不可删除
            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                foreach (var cube in CurrencyService.LoadCubeFragments(conn, null, accountId))
                {
                    items.Add(new
                    {
                        container = "主背包",
                        category = "账号晶块",
                        listType = (int)InventoryListType.Main,
                        slot = cube.Slot,
                        templateId = cube.ItemId,
                        name = pvfIndex.ResolveItemName(cube.ItemId),
                        kind = "special",
                        rarity = pvfIndex.ResolveItemRarity(cube.ItemId),
                        count = cube.Count,
                        instanceValue = cube.Count,
                        durability = 0,
                        serial = 0,
                        expireTime = 0,
                        supplementalExpiration = (object)null,
                        templateExpiration = CreateTemplateExpiration(pvfIndex, cube.ItemId),
                        seal = 0,
                        deletable = false,
                        countEditable = false,
                        configurable = false,
                        expirationConfigurable = false,
                        configKind = (string)null,
                    });
                }
            }

            return new { characterId, count = items.Count, items };
        }

        // 货币行(主背包 slot 0-2)删行会打坏钱包; 晶块(354-359)和账号金库是账号共享, 在账号面板管理
        private static object CreateTemplateExpiration(PvfIndexService pvfIndex, int itemTemplateId)
        {
            var expiration = pvfIndex.ResolveItemExpiration(itemTemplateId);
            return new
            {
                known = expiration.IsKnown,
                absoluteExpireTime = expiration.AbsoluteExpirationUnixTime,
                usablePeriodDays = expiration.UsablePeriodDays,
                dailyDeleteItem = expiration.DailyDeleteItem,
                invalid = expiration.HasInvalidDefinition,
            };
        }

        private static object CreateTemplateExpirationFromIndex(PvfIndexService.ItemEntry indexed)
        {
            return new
            {
                known = true,
                absoluteExpireTime = indexed.AbsoluteExpirationUnixTime,
                usablePeriodDays = indexed.UsablePeriodDays,
                dailyDeleteItem = indexed.DailyDeleteItem,
                invalid = indexed.HasInvalidExpirationDefinition,
            };
        }

        // Disk-index fast path: list UI only needs coarse flags, not full EquipmentFile parse.
        private static string ResolveInventoryConfigKindFromIndex(
            PvfIndexService.ItemEntry indexed,
            string itemKind,
            InventoryListType listType)
        {
            if (indexed == null)
                return null;
            if (string.Equals(itemKind, "avatar", StringComparison.Ordinal)
                || string.Equals(indexed.TypeTag, "avatar", StringComparison.OrdinalIgnoreCase)
                || (indexed.TypeTag != null && indexed.TypeTag.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return indexed.RequiresConfiguration ? "avatar" : null;
            }

            if (listType == InventoryListType.Pet
                && string.Equals(itemKind, "pet", StringComparison.Ordinal)
                && indexed.SupportsQuality)
                return "equipment";

            if (!string.Equals(itemKind, "equipment", StringComparison.Ordinal)
                && !string.Equals(indexed.Kind, "equipment", StringComparison.Ordinal))
                return null;
            if (listType != InventoryListType.Main && listType != InventoryListType.Equipment)
                return null;
            if (indexed.RequiresManualGrantType)
                return null;
            return indexed.RequiresConfiguration || indexed.SupportsQuality ? "equipment" : null;
        }

        private static bool CanConfigureInventoryExpirationFromIndex(
            PvfIndexService.ItemEntry indexed,
            string itemKind,
            int currentExpireTime)
        {
            if (indexed == null)
                return false;
            if (indexed.DailyDeleteItem)
                return false;
            if (currentExpireTime > 0)
                return true;

            var isAvatar = string.Equals(itemKind, "avatar", StringComparison.Ordinal)
                || (indexed.TypeTag != null
                    && indexed.TypeTag.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isAvatar)
                return false;

            return indexed.UsablePeriodDays > 0 || indexed.AbsoluteExpirationUnixTime > 0;
        }

        private static object CreateSupplementalExpiration(
            IReadOnlyDictionary<int, int> rentalExpireTimes,
            int itemTemplateId,
            int instanceExpireTime)
        {
            if (instanceExpireTime <= 0
                && rentalExpireTimes != null
                && rentalExpireTimes.TryGetValue(itemTemplateId, out var expireTime)
                && expireTime > 0)
            {
                return new
                {
                    expireTime,
                    source = "rental",
                };
            }

            return null;
        }

        private static bool IsDeletable(InventoryListType listType, int slot)
        {
            if (listType == InventoryListType.AccountCargo)
                return false;
            if (listType == InventoryListType.Main && slot <= 2)
                return false;
            if (listType == InventoryListType.Main && CurrencyService.IsCubeFragmentSlot(slot))
                return false;
            return true;
        }

        private static bool CanEditItemCount(InventoryListType listType, short slot, byte kind)
        {
            if (!NewInventoryStore.IsStackableKind(kind))
                return false;
            if (listType == InventoryListType.Main && (slot <= 2 || CurrencyService.IsCubeFragmentSlot(slot)))
                return false;
            return true;
        }

        public object SetItemCount(int characterId, int listType, int slot, int count)
        {
            if (count < 1)
                return Error("数量必须大于 0，删除请用删除按钮");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var list = (InventoryListType)listType;
            if (list == InventoryListType.Main && (slot <= 2 || CurrencyService.IsCubeFragmentSlot(slot)))
                return Error("货币行和晶块不能在这里改数量");

            if (!_inventory.TryLoadItem(characterId, accountId, list, (short)slot, out var record))
                return Error("目标槽位没有物品");
            if (!NewInventoryStore.IsStackableKind(record.Core.ItemKind))
                return Error("该物品不能改数量（装备、时装、宠物请用配置或删除）");

            var stackLimit = 0;
            var metadata = ItemMetadataResolver.Resolve(record.ItemTemplateId);
            if (metadata != null && metadata.IsStackable && metadata.StackLimit > 0)
                stackLimit = metadata.StackLimit;

            if (!_inventory.TrySetStackCount(characterId, accountId, list, (short)slot, count, stackLimit, out var newCount, out var error))
                return Error(error);

            return new
            {
                success = true,
                characterId,
                listType,
                slot,
                count = newCount,
                stackLimit,
            };
        }

        // 走服务端 DELETE_ITEM 同款入口(TryDeleteItem): 按列表+槽位精确删除,
        // 排列锁清理/魔方碎片/整删部分删的语义都由服务端代码处理
        public object DeleteItemAt(int characterId, int listType, int slot, int count)
        {
            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var list = (InventoryListType)listType;
            if (!IsDeletable(list, slot))
                return Error("该槽位不允许删除(货币行或账号金库)");

            if (!_inventory.TryDelete(characterId, accountId, list, (short)slot, count, out var remaining))
                return Error("删除失败(槽位为空或该列表不支持删除)");

            return new
            {
                success = true,
                characterId,
                listType,
                slot,
                remaining,
                sorted = false,
            };
        }

        public object BatchDeleteItems(int characterId, List<BatchDeleteEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return Error("没有要删除的条目");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var deleted = 0;
            var failed = new List<object>();
            foreach (var entry in entries)
            {
                var list = (InventoryListType)entry.ListType;
                if (!IsDeletable(list, entry.Slot))
                {
                    failed.Add(new { entry.ListType, entry.Slot, reason = "受保护槽位" });
                    continue;
                }

                if (_inventory.TryDelete(characterId, accountId, list, (short)entry.Slot, 0, out _))
                {
                    deleted++;
                }
                else
                    failed.Add(new { entry.ListType, entry.Slot, reason = "删除失败" });
            }

            return new { success = true, characterId, deleted, sortedSegments = 0, failedCount = failed.Count, failed };
        }

        // 主背包 slot 分段, 与服务端 ItemMetadataResolver.GetSlotRange / 各 Slot 常量一致
        private static string ResolveMainSegment(int slot)
        {
            if (slot <= 2) return "货币";        // 0金币 1复活币 2技能点
            if (slot <= 8) return "快捷栏";      // QuickSlot 3-8
            if (slot <= 64) return "装备";       // 9-64 (含租赁)
            if (slot <= 120) return "消耗品";    // 65-120
            if (slot <= 176) return "材料";      // 121-176
            if (slot <= 232) return "任务品";    // 177-232
            if (slot <= 288) return "副职业材料"; // 233-288
            if (slot <= 351) return "徽章";      // 289-351
            if (slot <= 353) return "保留槽";     // 352-353 不存放普通物品
            if (slot <= 359) return "账号晶块";   // 354-359 账号共享(accounts表列), 在账号面板调整
            return "其他";
        }

        private static string ResolvePetSegment(int slot)
        {
            if (slot <= 139) return "宠物";       // 0-139
            if (slot <= 188) return "宠物装备";    // 140-188
            return "宠物用品";                    // 189-237
        }

        public object GiveItem(
            int characterId,
            int itemTemplateId,
            int count,
            ItemGrantOptions options,
            PvfIndexService pvfIndex,
            bool direct = false,
            string requestId = null,
            string deliveryMode = null)
        {
            if (itemTemplateId <= 0)
                return Error("itemTemplateId 无效");
            if (count <= 0)
                return Error("数量必须大于 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            // 名字解析不到通常意味着 ID 不存在, 直接发下去客户端会异常, 先拦住
            var name = pvfIndex.ResolveItemName(itemTemplateId);
            if (name == null && pvfIndex.IsReady)
                return Error("物品 ID " + itemTemplateId + " 在 PVF 中不存在(装备/堆叠表都没有)");

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            var forceInventory = direct || NormalizeDeliveryMode(deliveryMode) == "inventory";
            if (ItemMetadataResolver.IsNameTagMetadata(metadata))
            {
                if (!forceInventory)
                    return Error("名称装饰卡无法通过邮件发放，请改用背包发放");
                var days = options?.ExpirationDays ?? DefaultNameTagGrantDays;
                if (days <= 0 || days > ItemGrantExpirationOverride.MaximumDays)
                    return Error("名称装饰卡期限必须在 1-3650 天之间");
                var previous = _inventory.LoadNameTag(characterId);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var baseTime = previous.ItemId == itemTemplateId && previous.ExpireTime > now ? previous.ExpireTime : now;
                var expire = Math.Min(now + ItemGrantExpirationOverride.MaximumDays * SecondsPerDay, baseTime + days * count * SecondsPerDay);
                if (expire <= now || expire > int.MaxValue)
                    return Error("名称装饰卡期限超出服务端可存储范围");
                _inventory.UpsertNameTag(characterId, itemTemplateId, (int)expire);
                return new { success = true, characterId, itemTemplateId, name, count, slot = (int)NameTagEquippedSlot, expireTime = (int)expire, slots = new[] { NameTagEquippedSlot }, nameTagEquipped = true };
            }

            if (PremiumCatalog.Load().TryGetValue(itemTemplateId, out var premiumType, out var durationDays)
                && premiumType > 0
                && durationDays > 0)
            {
                using (var connection = new SqliteConnection(_config.ConnectionString))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    var premiumGrant = GrantAccountPremium(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        itemTemplateId,
                        count,
                        premiumType,
                        durationDays);
                    if (!premiumGrant.Success)
                        return Error(premiumGrant.Error ?? "账号契约发放失败");
                    transaction.Commit();
                    return new
                    {
                        success = true,
                        characterId,
                        accountId,
                        itemTemplateId,
                        name,
                        count = premiumGrant.GrantedCount,
                        premiumActivated = true,
                        premiumType,
                        durationDays,
                        expireTime = premiumGrant.ExpireTime,
                    };
                }
            }

            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                using (var connection = new SqliteConnection(_config.ConnectionString))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, count);
                    transaction.Commit();
                }
                return new { success = true, characterId, itemTemplateId, name, count, slot = CurrencyService.GetCubeFragmentSlot(itemTemplateId) };
            }

            if (ReviveCoinService.IsReviveCoinReward(itemTemplateId))
            {
                if (!_inventory.TryAdjustVirtualCount(characterId, accountId, 1, count, int.MaxValue, out _))
                    return Error("复活币发放失败");
                return new { success = true, characterId, itemTemplateId, name, count, slot = 1 };
            }

            // 普通装备（非装扮/宠物/名牌）：Source 自定义属性仅走邮件附件 ItemCore。
            var isOrdinaryEquipment = metadata != null
                && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && !ItemMetadataResolver.IsAvatarMetadata(metadata)
                && !ItemMetadataResolver.IsPetCreatureMetadata(metadata)
                && !ItemMetadataResolver.IsPetArtifactMetadata(metadata)
                && !ItemMetadataResolver.IsNameTagMetadata(metadata);

            // Target-only：装扮属性/期限/手动分类仍需直写。
            var needsTargetDirectOptions = NeedsTargetDirectGrantOptions(options);
            if (!isOrdinaryEquipment && HasExclusiveEquipmentMailState(options))
                return Error("只有装备可以设置净化、强化、增幅或锻造属性");

            EquipmentMailConfiguration equipment = null;
            if (isOrdinaryEquipment && !needsTargetDirectOptions)
            {
                if (!TryResolveEquipmentMailConfiguration(itemTemplateId, metadata, options, out equipment, out var equipmentError))
                    return Error(equipmentError);
                if (!forceInventory && count > MaximumMailAttachments)
                    return Error("装备发送数量不能超过邮件附件上限 " + MaximumMailAttachments);

                if (forceInventory)
                {
                    if (equipment != null && equipment.IsCustomized)
                        return Error("装备属性配置仅支持通过邮件发放");
                }
                else
                {
                    return GiveItemViaMail(characterId, accountId, itemTemplateId, count, name, equipment, requestId);
                }
            }

            // 未指定或 deliveryMode=mail：默认系统邮件。
            // 装扮属性/期限/手动分类仍走背包直写（Target 既有能力，邮件附件无法表达）。
            if (!forceInventory && !needsTargetDirectOptions)
                return GiveItemViaMail(characterId, accountId, itemTemplateId, count, name, null, requestId);

            if (!TryLoadGrantCharacter(characterId, out var job, out _, out _))
                return Error("角色不存在: " + characterId);
            var grant = _inventory.TryGrant(characterId, accountId, job, itemTemplateId, count, options);
            if (!grant.Success)
                return Error(grant.Error ?? "发放失败(背包可能已满)");
            return new
            {
                success = true,
                characterId,
                itemTemplateId,
                name,
                count = grant.GrantedCount,
                grantedCount = grant.GrantedCount,
                delivery = "inventory",
                slot = (int)grant.AssignedSlot,
                slots = grant.AffectedSlots,
                expireTime = grant.ExpireTime,
                requiresReselect = true,
                deliveryHint = "物品已直接写入新版角色背包；请返回角色选择界面后重新进入以刷新显示。",
            };
        }

        // GM 系统邮件发件人固定 ID(正数即可, sender 无 FK; 收件箱显示发件人名 "GM")
        private const int GmMailSenderCharacterId = 1999999999;
        private const int MailAttachmentLimit = 10;
        private const int MaximumMailMessages = 10;
        private const int MaximumMailAttachments = MailAttachmentLimit * MaximumMailMessages;
        private const byte UnidentifiedAmplifyFlag = 0x80;

        private static string NormalizeDeliveryMode(string deliveryMode)
        {
            return string.Equals(
                    (deliveryMode ?? string.Empty).Trim(),
                    "inventory",
                    StringComparison.OrdinalIgnoreCase)
                ? "inventory"
                : "mail";
        }

        private static bool NeedsTargetDirectGrantOptions(ItemGrantOptions options)
        {
            if (options == null)
                return false;
            return options.AvatarOptionValue.HasValue
                || options.ExpirationDays.HasValue
                || !string.IsNullOrWhiteSpace(options.ManualGrantType);
        }

        /// <summary>Source equipmentOptions 专属字段；QualityMode 可被宠物装备共用，不算专属。</summary>
        private static bool HasExclusiveEquipmentMailState(ItemGrantOptions options)
        {
            if (options == null)
                return false;
            return !string.IsNullOrWhiteSpace(options.State);
        }

        private object GiveItemViaMail(
            int characterId,
            int accountId,
            int itemTemplateId,
            int count,
            string name,
            EquipmentMailConfiguration equipment,
            string requestId)
        {
            if (!_mailboxRepository.TryLoadActiveCharacterMailIdentity(characterId, out var receiverName, out var receiverLevel, out var identityError))
                return Error(identityError);

            if (!TryCreateMailAttachments(itemTemplateId, count, equipment, out var attachments, out var attachmentError))
                return Error(attachmentError);
            if (attachments.Count > MaximumMailAttachments)
                return Error("发放数量过大：每封邮件最多 " + MailAttachmentLimit + " 个附件、最多 " + MaximumMailMessages + " 封邮件");

            var rootKey = BuildMailIdempotencyKey(requestId);
            var requests = new List<MailboxSendRequest>();
            var shardCount = (attachments.Count + MailAttachmentLimit - 1) / MailAttachmentLimit;
            if (shardCount <= 0)
                shardCount = 1;
            for (var shard = 0; shard < shardCount; shard++)
            {
                var offset = shard * MailAttachmentLimit;
                var shardAttachments = attachments
                    .Skip(offset)
                    .Take(Math.Min(MailAttachmentLimit, attachments.Count - offset))
                    .ToList();
                requests.Add(new MailboxSendRequest
                {
                    SenderCharacterId = GmMailSenderCharacterId,
                    SenderAccountId = 0,
                    SenderName = "GM",
                    SenderLevel = 86,
                    ReceiverCharacterId = characterId,
                    ReceiverAccountId = accountId,
                    ReceiverName = receiverName ?? string.Empty,
                    ReceiverLevel = receiverLevel,
                    Gold = 0,
                    Text = "GM 发放",
                    MailType = 1,
                    SourceProtocol = 0,
                    Unlimited = true,
                    IdempotencyKey = shard == 0
                        ? rootKey
                        : rootKey + ":part:" + shard.ToString(CultureInfo.InvariantCulture),
                    AuditActor = "DfoGmTool",
                    AuditReason = "GM 发放",
                    Attachments = shardAttachments,
                });
            }

            var result = requests.Count == 1
                ? _mailboxRepository.SendSystemMail(requests[0])
                : _mailboxRepository.SendSystemMails(requests);
            if (!result.Success)
                return Error("邮件发放失败: " + MailErrorText(result.Error));

            var messageIds = result.MessageIds != null && result.MessageIds.Count > 0
                ? result.MessageIds
                : new[] { result.MessageId };
            return new
            {
                success = true,
                characterId,
                itemTemplateId,
                name,
                count,
                viaMail = true,
                delivery = "mail",
                messageId = result.MessageId,
                messageIds,
                messageCount = messageIds.Count,
                attachmentCount = attachments.Count,
                replayed = result.Replayed,
                notification = "mailbox_reopen_required",
                requiresReselect = false,
                deliveryHint = "在线角色请打开邮箱；如果邮箱已经打开，请关闭后重新打开，无需重新选择角色。",
            };
        }

        private static string BuildMailIdempotencyKey(string requestId)
        {
            var trimmed = (requestId ?? string.Empty).Trim();
            if (trimmed.Length >= 8 && trimmed.Length <= 128)
                return "gm:" + trimmed;
            return "gm:" + Guid.NewGuid().ToString("N");
        }

        private static bool TryResolveEquipmentMailConfiguration(
            int itemTemplateId,
            ItemMetadata metadata,
            ItemGrantOptions options,
            out EquipmentMailConfiguration configuration,
            out string error)
        {
            configuration = null;
            error = null;
            if (metadata == null || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
            {
                error = "物品不是装备: " + itemTemplateId;
                return false;
            }

            var capabilities = EquipmentGrantPolicy.Describe(metadata);
            var state = (options?.State ?? "normal").Trim().ToLowerInvariant();
            if (state.Length == 0)
                state = "normal";

            var upgradeLevel = options?.UpgradeLevel ?? 0;
            var amplifyType = options?.AmplifyType ?? 0;
            var forgingLevel = options?.ForgingLevel ?? 0;
            var qualityMode = options?.QualityMode ?? ItemQualityMode.Top;

            if (upgradeLevel < 0 || upgradeLevel > EquipmentGrantPolicy.MaximumUpgradeLevel)
            {
                error = "强化/增幅等级必须在 0-" + EquipmentGrantPolicy.MaximumUpgradeLevel + " 之间";
                return false;
            }
            if (forgingLevel < 0 || forgingLevel > EquipmentGrantPolicy.MaximumForgingLevel)
            {
                error = "锻造等级必须在 0-" + EquipmentGrantPolicy.MaximumForgingLevel + " 之间";
                return false;
            }
            if (!capabilities.CanForge && forgingLevel != 0)
            {
                error = "只有武器可以设置锻造等级";
                return false;
            }

            if (!Enum.IsDefined(typeof(ItemQualityMode), qualityMode))
            {
                error = "装备品级选项无效";
                return false;
            }

            int? qualitySeed = qualityMode == ItemQualityMode.Top
                ? unchecked((int)ItemQuality.TopQualitySeed)
                : null;

            byte resolvedAmplifyType;
            ushort resolvedAmplifyValue;
            switch (state)
            {
                case "normal":
                    if (amplifyType != 0)
                    {
                        error = "普通强化装备不能设置增幅属性";
                        return false;
                    }
                    if (!capabilities.CanUpgrade && upgradeLevel != 0)
                    {
                        error = "该装备禁止强化";
                        return false;
                    }
                    resolvedAmplifyType = 0;
                    resolvedAmplifyValue = 0;
                    break;

                case "unpurified":
                    if (!capabilities.CanHaveAmplifyState)
                    {
                        error = "该装备不支持异界气息";
                        return false;
                    }
                    if (upgradeLevel != 0 || amplifyType != 0)
                    {
                        error = "未净化装备不能设置强化、增幅等级或增幅属性";
                        return false;
                    }
                    resolvedAmplifyType = UnidentifiedAmplifyFlag;
                    resolvedAmplifyValue = 0;
                    break;

                case "purified":
                case "amplified":
                    if (!capabilities.CanHaveAmplifyState)
                    {
                        error = "该装备不支持净化或增幅";
                        return false;
                    }
                    if (amplifyType < 1 || amplifyType > 4)
                    {
                        error = "增幅属性必须是体力、精神、力量或智力";
                        return false;
                    }
                    if (!capabilities.CanAmplify && upgradeLevel != 0)
                    {
                        error = "该装备禁止增幅";
                        return false;
                    }
                    resolvedAmplifyType = (byte)amplifyType;
                    resolvedAmplifyValue = AmplifyInitialValueResolver.ResolveForAttribute(metadata.Rarity, amplifyType);
                    if (resolvedAmplifyValue == 0)
                    {
                        error = "无法从当前 PVF 计算增幅属性初始值";
                        return false;
                    }
                    state = "amplified";
                    break;

                default:
                    error = "未知装备状态: " + state;
                    return false;
            }

            configuration = new EquipmentMailConfiguration
            {
                UpgradeLevel = (byte)upgradeLevel,
                AmplifyType = resolvedAmplifyType,
                AmplifyValue = resolvedAmplifyValue,
                ForgingLevel = (byte)forgingLevel,
                QualitySeed = qualitySeed,
                IsCustomized = state != "normal"
                    || upgradeLevel != 0
                    || forgingLevel != 0
                    || (options != null && qualitySeed.HasValue),
            };
            return true;
        }

        private static bool TryCreateMailAttachments(
            int itemTemplateId,
            int count,
            EquipmentMailConfiguration equipment,
            out IReadOnlyList<MailboxSendAttachmentRequest> attachments,
            out string error)
        {
            error = null;
            if (equipment == null)
            {
                attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = itemTemplateId,
                        ItemCount = count,
                    },
                };
                return true;
            }

            var equipmentAttachments = new List<MailboxSendAttachmentRequest>(count);
            for (var i = 0; i < count; i++)
            {
                if (!SystemMailboxAttachmentFactory.TryCreate(
                        new MailboxSendAttachmentRequest
                        {
                            ItemId = itemTemplateId,
                            ItemCount = 1,
                        },
                        out var core,
                        out _,
                        out _))
                {
                    attachments = Array.Empty<MailboxSendAttachmentRequest>();
                    error = "装备附件创建失败: " + itemTemplateId;
                    return false;
                }

                core.Upgrade = equipment.UpgradeLevel;
                core.AmplifyType = equipment.AmplifyType;
                core.AmplifyValue = equipment.AmplifyValue;
                core.GenuineUpgrade = equipment.ForgingLevel;
                core.InstanceValue = equipment.QualitySeed
                    ?? unchecked((int)ItemQuality.ResolveSeed(ItemQualityMode.Random));
                equipmentAttachments.Add(new MailboxSendAttachmentRequest
                {
                    ItemId = itemTemplateId,
                    ItemCount = 1,
                    ItemCoreData = core.ToBytes(),
                });
            }

            attachments = equipmentAttachments;
            return true;
        }

        private sealed class EquipmentMailConfiguration
        {
            public byte UpgradeLevel { get; set; }
            public byte AmplifyType { get; set; }
            public ushort AmplifyValue { get; set; }
            public byte ForgingLevel { get; set; }
            public int? QualitySeed { get; set; }
            public bool IsCustomized { get; set; }
        }

        private static string MailErrorText(MailboxSendError error)
        {
            switch (error)
            {
                case MailboxSendError.None: return "未知错误";
                case MailboxSendError.InvalidRequest: return "请求无效";
                case MailboxSendError.ReceiverNotFound: return "收件角色不存在";
                case MailboxSendError.ReceiverDeleted: return "收件角色已删除";
                case MailboxSendError.InvalidAttachment: return "附件无效(物品不可邮或创建失败)";
                case MailboxSendError.TooManyAttachments: return "附件数量超限";
                case MailboxSendError.NotTradable: return "该物品不可交易";
                case MailboxSendError.AccountBound: return "该物品为账号绑定";
                case MailboxSendError.InventoryFull: return "背包已满";
                case MailboxSendError.ItemCarryLimitExceeded: return "超过物品携带上限";
                case MailboxSendError.GoldCarryLimitExceeded: return "超过金币携带上限";
                case MailboxSendError.MailboxStorageFull: return "邮件收藏已满";
                case MailboxSendError.ExpiredItem: return "附件已过期";
                default: return error.ToString();
            }
        }

        private sealed class AccountPremiumGrantResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public int ItemTemplateId { get; set; }
            public int RequestedCount { get; set; }
            public int GrantedCount { get; set; }
            public long ExpireTime { get; set; }
            public long PreviousExpireTime { get; set; }
        }

        private static AccountPremiumGrantResult GrantAccountPremium(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            int itemTemplateId,
            int count,
            int premiumType,
            int durationDays)
        {
            var result = new AccountPremiumGrantResult
            {
                Success = false,
                ItemTemplateId = itemTemplateId,
                RequestedCount = count,
            };

            if (accountId <= 0)
                return FailAccountPremiumGrant(result, "账号不存在");
            if (count <= 0)
                return FailAccountPremiumGrant(result, "数量必须大于 0");
            if (premiumType <= 0 || durationDays <= 0)
                return FailAccountPremiumGrant(result, "账号契约配置无效");

            var effectiveCount = Math.Max(1, count);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var duration = (long)durationDays * SecondsPerDay * effectiveCount;
            var oldExpire = LoadAccountPremiumExpire(connection, transaction, accountId, premiumType);
            var newExpire = Math.Max(now, oldExpire) + duration;
            if (newExpire <= now)
                return FailAccountPremiumGrant(result, "账号契约期限超出服务端可存储范围");

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO account_premiums (account_id, premium_type, end_time, updated_at)
VALUES (@aid, @type, @expire, CURRENT_TIMESTAMP)
ON CONFLICT(account_id, premium_type)
DO UPDATE SET end_time = @expire, updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@type", premiumType);
                command.Parameters.AddWithValue("@expire", newExpire);
                command.ExecuteNonQuery();
            }

            result.Success = true;
            result.GrantedCount = effectiveCount;
            result.ExpireTime = newExpire;
            result.PreviousExpireTime = oldExpire;
            WriteAccountPremiumGrantAudit(
                connection,
                transaction,
                accountId,
                characterId,
                result,
                premiumType,
                durationDays);
            return result;
        }

        private static long LoadAccountPremiumExpire(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int premiumType)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT end_time FROM account_premiums WHERE account_id=@aid AND premium_type=@type;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@type", premiumType);
                var value = command.ExecuteScalar();
                return value != null && value != DBNull.Value ? Convert.ToInt64(value) : 0L;
            }
        }

        private static void WriteAccountPremiumGrantAudit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            AccountPremiumGrantResult grant,
            int premiumType,
            int durationDays)
        {
            if (grant == null || !grant.Success)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'account', @ownerId, @characterId, 'gm_grant', NULL, NULL,
    @itemTemplateId, @deltaStackCount, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@itemTemplateId", grant.ItemTemplateId);
                command.Parameters.AddWithValue("@deltaStackCount", grant.GrantedCount);
                command.Parameters.AddWithValue("@payloadJson",
                    "{\"source\":\"gm_tool\",\"premiumActivated\":true"
                    + ",\"premiumType\":" + premiumType.ToString(CultureInfo.InvariantCulture)
                    + ",\"requestedCount\":" + grant.RequestedCount.ToString(CultureInfo.InvariantCulture)
                    + ",\"grantedCount\":" + grant.GrantedCount.ToString(CultureInfo.InvariantCulture)
                    + ",\"durationDays\":" + durationDays.ToString(CultureInfo.InvariantCulture)
                    + ",\"expireTime\":" + grant.ExpireTime.ToString(CultureInfo.InvariantCulture)
                    + ",\"previousExpireTime\":" + grant.PreviousExpireTime.ToString(CultureInfo.InvariantCulture)
                    + "}");
                command.ExecuteNonQuery();
            }
        }

        private static AccountPremiumGrantResult FailAccountPremiumGrant(AccountPremiumGrantResult result, string error)
        {
            result.Error = error;
            return result;
        }

        public object GetItemGrantOptions(int characterId, int itemTemplateId, PvfIndexService pvfIndex)
        {
            if (itemTemplateId <= 0)
                return Error("itemTemplateId 无效");
            if (!TryLoadGrantCharacter(characterId, out var job, out var growType, out var level))
                return Error("角色不存在: " + characterId);

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata == null || metadata.ItemKind == "special")
                return Error("物品 ID " + itemTemplateId + " 在当前 PVF 中不存在");

            var name = pvfIndex.ResolveItemName(itemTemplateId);
            var equipmentCapability = EquipmentGrantPolicy.Describe(metadata);
            var isAvatar = ItemMetadataResolver.IsAvatarMetadata(metadata);
            var requiresManualGrantType = ItemMetadataResolver.RequiresManualGrantType(metadata);
            var expiration = BuildGrantExpirationCapability(itemTemplateId, metadata, isAvatar, out var expirationError);
            if (expirationError != null)
                return Error(expirationError);

            object avatar = null;
            List<AvatarGrantOption> avatarOptionValues = null;
            IReadOnlyList<AvatarDurationOption> avatarDurationValues = Array.Empty<AvatarDurationOption>();
            if (isAvatar)
            {
                string usableJob = null;
                int abilityCaseIndex = -1;
                IReadOnlyList<AvatarSelectAbilityEntry> selectAbilities = null;
                string equipmentType = null;
                int grade = 0;
                var avatarLoader = AvatarGrantIndex.Loader;
                var fromIndex = avatarLoader != null
                    && avatarLoader(
                        itemTemplateId,
                        out usableJob,
                        out abilityCaseIndex,
                        out selectAbilities,
                        out equipmentType,
                        out grade);
                if (!fromIndex)
                {
                    if (avatarLoader != null)
                        return Error("装扮模板索引不可用");
                    if (!ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment))
                        return Error("装扮模板无法从 PVF 读取");
                    usableJob = equipment.UsableJob;
                    abilityCaseIndex = equipment.AbilityCaseIndex;
                    selectAbilities = equipment.AvatarSelectAbilities;
                    equipmentType = equipment.EquipmentType;
                    grade = equipment.Grade;
                }

                var compatible = AvatarGrantPolicy.IsUsableByJob(usableJob, job);
                avatarOptionValues = compatible
                    ? AvatarGrantPolicy.ResolveOptions(
                        equipmentType,
                        grade,
                        selectAbilities,
                        job,
                        abilityCaseIndex)
                    : new List<AvatarGrantOption>();
                avatarDurationValues = AvatarDurationResolver.Resolve(itemTemplateId);
                avatar = new
                {
                    compatible,
                    part = metadata.EquipmentType ?? equipmentType,
                    grade = metadata.Grade > 0 ? metadata.Grade : grade,
                    usableJob,
                    options = avatarOptionValues.Select(value => new
                    {
                        value = value.Value,
                        label = value.Label,
                        isSkill = value.IsSkill,
                    }).ToArray(),
                    durations = avatarDurationValues.Select(value => new
                    {
                        days = value.DurationDays,
                        label = value.DurationDays == 0 ? "永久" : value.DurationDays + " 天",
                    }).ToArray(),
                };
            }

            var isPetArtifact = ItemMetadataResolver.IsPetArtifactMetadata(metadata);
            var isPetCreature = ItemMetadataResolver.IsPetCreatureMetadata(metadata);
            var isConfigurablePetEquipment = isPetArtifact && metadata.SupportsPetEquipmentQuality;
            var isConfigurableEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && !requiresManualGrantType
                && !isAvatar
                && !ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId)
                && (equipmentCapability.CanUpgrade || equipmentCapability.CanAmplify || equipmentCapability.CanForge);
            var requiresAvatarConfiguration = isAvatar
                && ((avatarOptionValues?.Count ?? 0) > 1 || avatarDurationValues.Count > 0);
            var requiresConfiguration = !isPetCreature
                && (isConfigurableEquipment
                    || isConfigurablePetEquipment
                    || requiresAvatarConfiguration
                    || (!isPetArtifact && expiration.CanOverride)
                    || requiresManualGrantType);
            return new
            {
                success = true,
                characterId,
                itemTemplateId,
                name,
                kind = metadata.ItemKind,
                requiresConfiguration,
                pvfTypeTag = ItemMetadataResolver.ResolvePvfTypeTag(metadata),
                equipment = isConfigurableEquipment || isConfigurablePetEquipment ? new
                {
                    type = metadata.EquipmentType,
                    rarity = metadata.Rarity,
                    minimumLevel = metadata.MinimumLevel,
                    canUpgrade = equipmentCapability.CanUpgrade,
                    canHaveAmplifyState = equipmentCapability.CanHaveAmplifyState,
                    canAmplify = equipmentCapability.CanAmplify,
                    canForge = equipmentCapability.CanForge,
                    mailAttachmentLimit = MaximumMailAttachments,
                    supportsQuality = isConfigurableEquipment || isConfigurablePetEquipment,
                    maxUpgradeLevel = equipmentCapability.MaxUpgradeLevel,
                    maxForgingLevel = equipmentCapability.MaxForgingLevel,
                    qualityOptions = new[]
                    {
                        new { value = (int)ItemQualityMode.Random, label = "随机品级" },
                        new { value = (int)ItemQualityMode.Top, label = "100% 最上级" },
                    },
                    amplifyTypes = new[]
                    {
                        new { value = 0, label = "无红字（强化）" },
                        new { value = 1, label = EquipmentGrantPolicy.GetAmplifyTypeLabel(1) },
                        new { value = 2, label = EquipmentGrantPolicy.GetAmplifyTypeLabel(2) },
                        new { value = 3, label = EquipmentGrantPolicy.GetAmplifyTypeLabel(3) },
                        new { value = 4, label = EquipmentGrantPolicy.GetAmplifyTypeLabel(4) },
                    },
                } : null,
                avatar,
                manual = requiresManualGrantType ? new
                {
                    required = true,
                    choices = BuildManualGrantTypeChoices(metadata),
                } : null,
                expiration = new
                {
                    limited = expiration.IsLimited,
                    canOverride = expiration.CanOverride,
                    expired = expiration.IsExpired,
                    defaultExpireTime = expiration.DefaultExpireTime,
                    maxDays = ItemGrantExpirationOverride.MaximumDays,
                },
            };
        }

        private static object[] BuildManualGrantTypeChoices(ItemMetadata metadata)
        {
            if (metadata == null)
                return Array.Empty<object>();

            if (string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
            {
                return new object[]
                {
                    new { value = "equipment", label = "普通装备栏" },
                    new { value = "avatar", label = "装扮栏" },
                    new { value = "pet", label = "宠物栏" },
                    new { value = "pet-equipment", label = "宠物装备栏" },
                };
            }

            if (metadata.IsStackable)
            {
                return new object[]
                {
                    new { value = "consumable", label = "消耗品" },
                    new { value = "material", label = "材料" },
                    new { value = "quest", label = "任务品" },
                    new { value = "expert-material", label = "副职业材料" },
                    new { value = "avatar-emblem", label = "徽章" },
                    new { value = "pet-consumable", label = "宠物消耗品" },
                };
            }

            return Array.Empty<object>();
        }

        private bool TryLoadGrantCharacter(int characterId, out int job, out int growType, out int level)
        {
            job = 0;
            growType = 0;
            level = 0;
            using (var connection = new SqliteConnection(_config.ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT job, grow_type, level FROM characters WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                            return false;
                        job = reader.GetInt32(0);
                        growType = reader.GetInt32(1);
                        level = reader.GetInt32(2);
                        return true;
                    }
                }
            }
        }

        private static ItemGrantExpirationCapability BuildGrantExpirationCapability(
            int itemTemplateId,
            ItemMetadata metadata,
            bool isAvatar,
            out string error)
        {
            error = null;
            if (isAvatar)
            {
                var durations = AvatarDurationResolver.Resolve(itemTemplateId);
                return new ItemGrantExpirationCapability
                {
                    IsLimited = durations.Any(value => value.DurationDays > 0),
                    CanOverride = durations.Count > 0,
                    DefaultExpireTime = 0,
                };
            }

            if (!ItemGrantExpirationResolver.TryResolve(itemTemplateId, metadata, out var expireTime, out error))
            {
                if (!IsExpiredGrantExpirationError(error))
                    return new ItemGrantExpirationCapability();
                error = null;
                return new ItemGrantExpirationCapability
                {
                    IsLimited = true,
                    CanOverride = true,
                    DefaultExpireTime = 0,
                    IsExpired = true,
                };
            }
            var capability = new ItemGrantExpirationCapability
            {
                IsLimited = expireTime > 0,
                CanOverride = expireTime > 0,
                DefaultExpireTime = expireTime,
            };
            // Only consult StackableFile when present (PVF path). Index-first grants leave it null;
            // expireTime already comes from DiskExpirationResolver.
            if (metadata.IsStackable
                && metadata.StackableFile != null
                && StackableExpirationPolicyResolver.TryResolve(metadata.StackableFile, out var policy))
            {
                capability.IsLimited = policy.RequiresInstanceExpiration
                    || policy.AbsoluteExpirationUnixTime > 0
                    || policy.DailyDeleteItem;
                capability.CanOverride = policy.RequiresInstanceExpiration
                    || policy.AbsoluteExpirationUnixTime > 0;
            }
            else if (metadata.IsStackable && metadata.DailyDeleteItem)
            {
                capability.IsLimited = true;
                capability.CanOverride = false;
            }
            return capability;
        }

        private static bool IsExpiredGrantExpirationError(string error)
            => !string.IsNullOrWhiteSpace(error) && error.Contains("已过期");

        public object RemoveItem(int characterId, int itemTemplateId, int count)
        {
            if (count <= 0)
                count = 1;

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            if (!_inventory.TryRemoveByTemplateId(characterId, accountId, itemTemplateId, count, out var slot, out var remaining))
                return Error("移除失败(角色没有该物品或数量不足)");
            return new { success = true, characterId, itemTemplateId, count, slot = (int)slot, remaining };
        }

        public object AdjustGold(int characterId, int amount)
        {
            if (amount == 0)
                return Error("amount 不能为 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            GoldLimitSnapshot goldLimit;
            try
            {
                goldLimit = LoadGoldLimitSnapshot(characterId);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }

            var requestedAmount = amount;
            var wallet = _inventory.LoadWallet(characterId);
            if (amount > 0)
                amount = Math.Min(amount, Math.Max(0, goldLimit.GoldCarryLimit - wallet.Gold));
            if (!_inventory.TryAdjustVirtualCount(characterId, accountId, 0, amount, goldLimit.GoldCarryLimit, out var gold))
                return Error("扣款失败(金币不足)");
            return new { success = true, characterId, requestedAmount, amount, gold, goldCarryLimit = goldLimit.GoldCarryLimit };
        }

        // 三种角色货币都写入新版 ItemCore 虚拟钱包槽：金币 slot0、复活币 slot1、技能点 slot2。
        public object SetWalletValue(int characterId, string type, int value)
        {
            if (value < 0)
                return Error("数值不能为负");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            type = (type ?? string.Empty).Trim().ToLowerInvariant();

            if (type == "gold")
            {
                GoldLimitSnapshot goldLimit;
                try
                {
                    goldLimit = LoadGoldLimitSnapshot(characterId);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(ex.Message);
                }
                if (value > goldLimit.GoldCarryLimit)
                    return Error("金币不能超过当前上限 " + goldLimit.GoldCarryLimit.ToString("N0"));

                if (!_inventory.TrySetVirtualCount(characterId, accountId, 0, value))
                    return Error("设置失败");
                return new { success = true, characterId, type, value, goldCarryLimit = goldLimit.GoldCarryLimit };
            }

            int slot;
            switch (type)
            {
                case "revive": slot = 1; break;
                case "sp": slot = 2; break;
                default: return Error("不支持的类型: " + type + " (可用: gold/revive/sp)");
            }

            if (!_inventory.TrySetVirtualCount(characterId, accountId, (short)slot, value))
                return Error("货币设置失败(slot " + slot + ")");
            return new { success = true, characterId, type, value };
        }

        // 点券是账号级余额, 服务端接口按角色定位账号
        public object AdjustCera(int characterId, int amount, string type)
        {
            if (amount == 0)
                return Error("amount 不能为 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var useToken = string.Equals(type, "token", StringComparison.OrdinalIgnoreCase);
            using (var connection = new SqliteConnection(_config.ConnectionString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                if (amount > 0)
                {
                    if (useToken)
                        CurrencyService.GrantTokenCera(connection, transaction, characterId, amount);
                    else
                        CurrencyService.GrantCera(connection, transaction, characterId, amount);
                }
                else
                {
                    var ok = useToken
                        ? CurrencyService.TrySpendTokenCera(connection, transaction, characterId, -amount)
                        : CurrencyService.TrySpendCera(connection, transaction, characterId, -amount);
                    if (!ok)
                        return Error("扣减失败(余额不足)");
                }
                transaction.Commit();
            }

            using (var connection = new SqliteConnection(_config.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT cera, token_cera FROM accounts WHERE account_id=@aid;";
                command.Parameters.AddWithValue("@aid", accountId);
                using var reader = command.ExecuteReader();
                reader.Read();
                return new { success = true, characterId, accountId, amount, cera = reader.GetInt32(0), tokenCera = reader.GetInt32(1) };
            }
        }
    }

    public sealed class BatchDeleteEntry
    {
        public int ListType { get; set; }
        public int Slot { get; set; }
    }
}
