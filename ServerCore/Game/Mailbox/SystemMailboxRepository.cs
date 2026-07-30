using System;
using DfoGmTool.ServerCore.Game.Inventory;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    /// <summary>
    /// Shared system-mail attachment factory for the Target ItemCore model.
    /// GM give path goes through <see cref="MailboxRepository.SendSystemMail"/>;
    /// this type only builds attachment cores and remains for tests/compat.
    /// </summary>
    public static class SystemMailboxAttachmentFactory
    {
        internal static bool TryCreate(
            MailboxSendAttachmentRequest request,
            out ItemCore core,
            out string itemKind,
            out string error)
        {
            core = null;
            itemKind = "unknown";
            error = null;

            if (request == null || request.ItemId <= 0 || request.ItemCount <= 0)
            {
                error = "附件无效";
                return false;
            }

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(request.ItemId);
            }
            catch
            {
                metadata = null;
            }

            if (metadata == null)
            {
                // Allow offline/self-test templates that are not in PVF.
                core = ItemCore.Create(ItemCore.KindConsumable, request.ItemId);
                core.Count = Math.Max(1, request.ItemCount);
                itemKind = "stackable";
                return true;
            }

            var kind = ResolveKind(metadata);
            core = ItemCore.Create(kind, request.ItemId);
            itemKind = MailboxItemCoreCodec.GetLegacyKindName(core);

            var itemCount = Math.Max(1, request.ItemCount);
            var tradeLimitMax = metadata.StackableFile != null && metadata.StackableFile.TradeLimit > 0
                ? metadata.StackableFile.TradeLimit
                : 0;
            if (NewInventoryStore.IsStackableKind(kind))
            {
                core.Count = itemCount;
                if (tradeLimitMax > 0)
                    core.StackTradeCount = (byte)Math.Min(7, tradeLimitMax);
            }
            else if (kind == ItemCore.KindEquipment || kind == ItemCore.KindCreatureEquipment)
            {
                core.InstanceValue = request.InstanceValue != 0
                    ? request.InstanceValue
                    : checked((int)ItemQuality.TopQualitySeed);
                core.Durability = request.Durability != 0
                    ? (ushort)Math.Clamp(request.Durability, 0, ushort.MaxValue)
                    : (ushort)Math.Clamp((int)metadata.Durability, 0, ushort.MaxValue);
                core.SealFlag = request.SealFlag != 0
                    ? (byte)Math.Clamp(request.SealFlag, 0, byte.MaxValue)
                    : (metadata.IsSealed ? (byte)1 : (byte)0);
            }
            else
            {
                if (request.InstanceValue != 0)
                    core.Value = request.InstanceValue;
                if (request.Durability != 0)
                    core.Durability = (ushort)Math.Clamp(request.Durability, 0, ushort.MaxValue);
                core.SealFlag = (byte)Math.Clamp(request.SealFlag, 0, byte.MaxValue);
            }

            if (request.OptionValue != 0 && kind == ItemCore.KindAvatar)
                core.AbilityNo = (ushort)Math.Clamp(request.OptionValue, 0, ushort.MaxValue);
            if (request.PetSerialOrHandle != 0 && kind == ItemCore.KindCreature)
                core.CreatureUid = request.PetSerialOrHandle;
            core.Marker16 = request.Marker16;

            if (request.ExpireTime > 0)
            {
                core.ExpireTime = request.ExpireTime;
            }
            else if (ItemGrantExpirationResolver.TryResolve(request.ItemId, metadata, out var expireTime, out _)
                     && expireTime > 0)
            {
                // NameTag/avatar relative expire is stored on detail at claim time;
                // equipment/stackable expire lives on the core (Source CreateCore).
                if (kind != ItemCore.KindAvatar && kind != ItemCore.KindCreature)
                    core.ExpireTime = expireTime;
            }

            if (MailboxSendPolicy.IsTradeLimitItem(metadata) && core.StackTradeCount == 0 && tradeLimitMax > 0)
                core = MailboxSendPolicy.SetRemainingTradeCount(core, tradeLimitMax);

            return true;
        }

        internal static ItemCore CreateAttachmentCore(ItemMetadata metadata, int itemTemplateId, int count)
        {
            var request = new MailboxSendAttachmentRequest
            {
                ItemId = itemTemplateId,
                ItemCount = count,
            };
            if (!TryCreate(request, out var core, out _, out _))
                return ItemCore.Create(ItemCore.KindConsumable, itemTemplateId);
            return core;
        }

        private static byte ResolveKind(ItemMetadata metadata)
        {
            if (metadata != null && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
            {
                if (ItemMetadataResolver.IsAvatarMetadata(metadata))
                    return ItemCore.KindAvatar;
                if (ItemMetadataResolver.IsPetCreatureMetadata(metadata))
                    return ItemCore.KindCreature;
                if (ItemMetadataResolver.IsPetArtifactMetadata(metadata))
                    return ItemCore.KindCreatureEquipment;
                return ItemCore.KindEquipment;
            }

            if (metadata?.IsStackable == true)
            {
                return ItemMetadataResolver.ResolvePvfTypeTag(metadata) switch
                {
                    "material" => ItemCore.KindMaterial,
                    "quest" => ItemCore.KindQuest,
                    "material expert job" => ItemCore.KindExpertJobMaterial,
                    "avatar emblem" => ItemCore.KindAvatarEmblem,
                    _ => ItemCore.KindConsumable,
                };
            }

            return ItemCore.KindConsumable;
        }
    }

    /// <summary>
    /// Compatibility wrapper used by existing SelfTests. New code should call
    /// <see cref="MailboxRepository.SendSystemMail"/>.
    /// </summary>
    public sealed class SystemMailboxRepository
    {
        private readonly MailboxRepository _mailbox;

        public SystemMailboxRepository(string databasePath, string schemaPath)
        {
            _mailbox = new MailboxRepository(databasePath, schemaPath);
        }

        public SystemMailboxSendResult Send(int characterId, int accountId, string receiverName, int itemTemplateId, int count)
        {
            if (characterId <= 0 || accountId <= 0 || itemTemplateId <= 0 || count <= 0)
                return SystemMailboxSendResult.Fail("邮件发放参数无效");

            var result = _mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = 1999999999,
                SenderAccountId = 0,
                SenderName = "GM",
                SenderLevel = 86,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = receiverName ?? string.Empty,
                Gold = 0,
                Text = "GM 发放",
                MailType = 1,
                SourceProtocol = 0,
                Unlimited = true,
                IdempotencyKey = "gm:" + Guid.NewGuid().ToString("N"),
                AuditActor = "DfoGmTool",
                AuditReason = "GM 发放",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = itemTemplateId,
                        ItemCount = count,
                    },
                },
            });

            if (!result.Success)
                return SystemMailboxSendResult.Fail("邮件发放失败: " + result.Error);
            return new SystemMailboxSendResult { Success = true, MessageId = result.MessageId };
        }

        internal static ItemCore CreateAttachmentCore(ItemMetadata metadata, int itemTemplateId, int count)
            => SystemMailboxAttachmentFactory.CreateAttachmentCore(metadata, itemTemplateId, count);
    }

    public sealed class SystemMailboxSendResult
    {
        public bool Success { get; set; }
        public long MessageId { get; set; }
        public string Error { get; set; }

        public static SystemMailboxSendResult Fail(string error)
            => new SystemMailboxSendResult { Error = error };
    }
}
