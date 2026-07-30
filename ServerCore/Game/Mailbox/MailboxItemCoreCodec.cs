using System;
using DfoGmTool.ServerCore.Game.Inventory;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    internal static class MailboxItemCoreCodec
    {
        internal static ItemCore Decode(MailboxAttachmentEntry attachment)
        {
            if (attachment == null)
                return null;

            return Decode(
                attachment.ItemCoreData,
                attachment.ItemTemplateId,
                attachment.ItemKind,
                attachment.ItemCount,
                attachment.InstanceValue,
                attachment.Durability,
                attachment.SealFlag,
                attachment.OptionValue,
                attachment.ExpireTime,
                attachment.Marker16,
                attachment.PetSerialOrHandle);
        }

        internal static ItemCore Decode(MailboxAttachmentSnapshot attachment)
        {
            if (attachment == null)
                return null;

            return Decode(
                attachment.ItemCoreData,
                attachment.ItemTemplateId,
                attachment.ItemKind,
                attachment.ItemCount,
                attachment.InstanceValue,
                attachment.Durability,
                attachment.SealFlag,
                attachment.OptionValue,
                attachment.ExpireTime,
                attachment.Marker16,
                attachment.PetSerialOrHandle);
        }

        internal static ItemCore Decode(
            byte[] itemCoreData,
            int itemId,
            string itemKind,
            int itemCount,
            int instanceValue,
            int durability,
            int sealFlag,
            int optionValue,
            int expireTime,
            int marker16,
            int petSerialOrHandle)
        {
            if (itemCoreData != null && itemCoreData.Length >= ItemCore.Size)
            {
                var core = ItemCore.FromBytes(itemCoreData);
                if (core != null && core.ItemId > 0)
                {
                    RestoreLegacyExpireTime(core, itemId, expireTime);
                    return core;
                }
            }

            if (itemId <= 0)
                return null;

            var metadata = SafeResolve(itemId);
            var resolvedKind = ResolveKind(metadata, itemKind, itemId);
            var legacy = ItemCore.Create(resolvedKind, itemId);
            legacy.Value = instanceValue;
            legacy.Durability = ClampUInt16(durability);
            legacy.SealFlag = ClampByte(sealFlag);
            legacy.ExpireTime = expireTime;
            legacy.Marker16 = marker16;

            if (legacy.ItemKind == ItemCore.KindCreature && petSerialOrHandle > 0)
                legacy.CreatureUid = petSerialOrHandle;
            if (legacy.ItemKind == ItemCore.KindAvatar)
                legacy.AbilityNo = ClampUInt16(optionValue);
            if (NewInventoryStore.IsStackableKind(legacy.ItemKind))
                legacy.Count = Math.Max(1, itemCount);

            return legacy;
        }

        internal static byte[] Encode(ItemCore core) => core?.ToBytes() ?? Array.Empty<byte>();

        internal static string GetLegacyKindName(ItemCore core)
        {
            if (core == null)
                return "unknown";

            return core.ItemKind switch
            {
                ItemCore.KindEquipment or ItemCore.KindCreatureEquipment => "equipment",
                ItemCore.KindAvatar => "avatar",
                ItemCore.KindCreature => "creature",
                _ => "stackable",
            };
        }

        private static ItemMetadata SafeResolve(int itemId)
        {
            try
            {
                return ItemMetadataResolver.Resolve(itemId);
            }
            catch
            {
                return null;
            }
        }

        private static byte ResolveKind(ItemMetadata metadata, string itemKind, int itemId)
        {
            if (metadata != null)
            {
                if (string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                {
                    if (ItemMetadataResolver.IsAvatarMetadata(metadata))
                        return ItemCore.KindAvatar;
                    if (ItemMetadataResolver.IsPetCreatureMetadata(metadata))
                        return ItemCore.KindCreature;
                    if (ItemMetadataResolver.IsPetArtifactMetadata(metadata))
                        return ItemCore.KindCreatureEquipment;
                    return ItemCore.KindEquipment;
                }

                if (metadata.IsStackable)
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
            }

            return (itemKind ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "equipment" => ItemCore.KindEquipment,
                "avatar" => ItemCore.KindAvatar,
                "creature" or "pet" => ItemCore.KindCreature,
                _ => ItemCore.KindConsumable,
            };
        }

        private static void RestoreLegacyExpireTime(ItemCore core, int itemId, int expireTime)
        {
            if (core == null || expireTime <= 0 || core.ExpireTime > 0)
                return;
            if (itemId > 0 && core.ItemId != itemId)
                return;
            core.ExpireTime = expireTime;
        }

        private static byte ClampByte(int value)
            => (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, value));

        private static ushort ClampUInt16(int value)
            => (ushort)Math.Max(ushort.MinValue, Math.Min(ushort.MaxValue, value));
    }
}
