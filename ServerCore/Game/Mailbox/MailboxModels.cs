using System;
using System.Collections.Generic;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    public enum MailboxSendError
    {
        None,
        InvalidRequest,
        EmptyContent,
        ReceiverNotFound,
        ReceiverDeleted,
        InsufficientGold,
        ReceiverGoldLimitExceeded,
        InvalidAttachment,
        TooManyAttachments,
        NotTradable,
        AccountBound,
        LimitedPeriodItem,
        ExpiredItem,
        ItemLocked,
        DailyGoldLimitExceeded,
        Blacklisted,
        IllegalText,
        ReceiverTradeRestricted,
        SenderLevelOrSendLimit,
        MailNotFound,
        InventoryFull,
        ItemCarryLimitExceeded,
        GoldCarryLimitExceeded,
        TradeRestricted,
        PersonalShopOpen,
        Trading,
        SelfSendNotAllowed,
        MailboxStorageFull,
        ServerBusy
    }

    public sealed class MailboxSendRequest
    {
        public int SenderCharacterId { get; set; }
        public int SenderAccountId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public int ReceiverCharacterId { get; set; }
        public int ReceiverAccountId { get; set; }
        public string ReceiverName { get; set; } = string.Empty;
        public int SenderLevel { get; set; }
        public int ReceiverLevel { get; set; }
        public int Gold { get; set; }
        public string Text { get; set; } = string.Empty;
        public int MailType { get; set; }
        public ushort SourceProtocol { get; set; }
        public bool? Unlimited { get; set; }
        public DateTimeOffset? ExpireAtUtc { get; set; }
        public string AuditActor { get; set; } = string.Empty;
        public string AuditReason { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public IReadOnlyList<MailboxSendAttachmentRequest> Attachments { get; set; } = Array.Empty<MailboxSendAttachmentRequest>();
    }

    public sealed class MailboxSendAttachmentRequest
    {
        public byte ItemType { get; set; }
        public ushort ItemSlot { get; set; }
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; } = -1;
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        /// <summary>可选预编码 ItemCore；与服务端字段对齐，系统发放通常由工厂生成。</summary>
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        /// <summary>装扮/宠物等明细 JSON；GM 系统发放通常为空，玩家邮件由服务端 Capture。</summary>
        public string DetailJson { get; set; } = string.Empty;
    }

    public sealed class MailboxSendResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }
        public int FeeGold { get; set; }
        public int UpdatedGold { get; set; }

        public static MailboxSendResult Fail(MailboxSendError error)
            => new MailboxSendResult { Success = false, Error = error };
    }

    public sealed class MailboxCampaignBatchResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public string CampaignId { get; set; } = string.Empty;
        public int DeliveredCount { get; set; }
        public int LastCharacterId { get; set; }
        public bool Completed { get; set; }

        public static MailboxCampaignBatchResult Fail(string campaignId, MailboxSendError error)
            => new MailboxCampaignBatchResult
            {
                Success = false,
                Error = error,
                CampaignId = campaignId ?? string.Empty
            };
    }

    public sealed class MailboxClaimResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }
        public int ClaimedGold { get; set; }
        public int ClaimedAttachmentCount { get; set; }
        public bool RemovedFromInbox { get; set; }
        public IReadOnlyList<short> UpdatedMainSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<short> UpdatedAvatarSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<short> UpdatedPetSlots { get; set; } = Array.Empty<short>();

        public static MailboxClaimResult Fail(MailboxSendError error)
            => new MailboxClaimResult { Success = false, Error = error };
    }

    public sealed class MailboxDeleteResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }

        public static MailboxDeleteResult Fail(MailboxSendError error)
            => new MailboxDeleteResult { Success = false, Error = error };
    }

    public sealed class MailboxListEntry
    {
        public long MessageId { get; set; }
        public int SenderCharacterId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public int Gold { get; set; }
        public int MailType { get; set; }
        public int SourceProtocol { get; set; }
        public int CreatedAtUnixSeconds { get; set; }
        public int LetterStat { get; set; }
        public int RemainSeconds { get; set; }
        public int FirstAttachmentExpireTime { get; set; }
        public IReadOnlyList<MailboxAttachmentEntry> Attachments { get; set; } = Array.Empty<MailboxAttachmentEntry>();
    }

    public sealed class MailboxInboxPage
    {
        public IReadOnlyList<MailboxListEntry> Entries { get; set; } = Array.Empty<MailboxListEntry>();
        public int TotalCount { get; set; }
        public int LoadedInboxCount { get; set; }
        public int RemainingCount => Math.Max(0, TotalCount - LoadedInboxCount);
    }

    public sealed class MailboxAttachmentEntry
    {
        public long AttachmentId { get; set; }
        public int Ordinal { get; set; }
        public byte ItemType { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public long SourceItemUid { get; set; }
        public int ItemTemplateId { get; set; }
        public string ItemKind { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }

    public sealed class MailboxExpirationRecipient
    {
        public int CharacterId { get; set; }
        public IReadOnlyList<long> MessageIds { get; set; } = Array.Empty<long>();
    }

    public sealed class MailboxExpirationBatchResult
    {
        public int ExpiredRecipientCount { get; set; }
        public int PurgedMessageCount { get; set; }
        public IReadOnlyList<MailboxExpirationRecipient> Recipients { get; set; } = Array.Empty<MailboxExpirationRecipient>();
    }

    internal sealed class MailboxAttachmentSnapshot
    {
        public int Ordinal { get; set; }
        public byte ItemType { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public long SourceItemUid { get; set; }
        public int ItemTemplateId { get; set; }
        public string ItemKind { get; set; } = "unknown";
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int EquipmentLockId { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }
}
