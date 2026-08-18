using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    public sealed partial class MailboxRepository
    {
        /// <summary>
        /// 与服务端 DfoServer 一致：邮件列表附件行的领取对象 ID 用该标志位编码 attachment_id。
        /// 纯金币/整信领取仍使用原始 messageId。
        /// </summary>
        internal const long AttachmentClaimFlag = 0x40000000L;

        /// <summary>玩家收件箱可见：未删、在收件箱，且已保存或无限期或未过期。与 DfoServer 同谓词。</summary>
        private const string PlayerInboxVisiblePredicate =
            "r.folder = 0 AND r.deleted_flag = 0 AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))";

        private readonly string _connectionString;

        public MailboxRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public MailboxSendResult SendMail(MailboxSendRequest request) => Send(request, system: false);
        public MailboxSendResult SendSystemMail(MailboxSendRequest request) => Send(request, system: true);

        public MailboxSendResult SendSystemMails(IReadOnlyList<MailboxSendRequest> requests)
        {
            if (requests == null || requests.Count == 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var ids = new List<long>(requests.Count);
            var replayed = 0;
            foreach (var request in requests)
            {
                var result = SendSystemInTransaction(connection, transaction, request);
                if (!result.Success)
                    return result;
                ids.Add(result.MessageId);
                if (result.Replayed)
                    replayed++;
            }

            if (replayed != 0 && replayed != requests.Count)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            transaction.Commit();
            return new MailboxSendResult
            {
                Success = true,
                MessageId = ids[0],
                MessageIds = ids,
                Replayed = replayed == requests.Count,
            };
        }
        public IReadOnlyList<MailboxListEntry> LoadInbox(int characterId, int limit)
            => LoadInboxPage(characterId, limit).Entries;

        public MailboxInboxPage LoadInboxPage(int characterId, int limit)
        {
            if (characterId <= 0 || limit <= 0)
                return new MailboxInboxPage();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            ExpireNormalInbox(connection, transaction, characterId);

            var totalCount = ScalarInt(connection, transaction, @"
SELECT COUNT(*)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.saved_flag = 0
  AND r.deleted_flag = 0
  AND (m.unlimited_flag != 0 OR m.expire_at > CURRENT_TIMESTAMP);",
                ("@cid", characterId));

            var entries = new List<MailboxListEntry>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
WITH inbox AS (
    SELECT r.recipient_id, 0 AS mailbox_group
    FROM mailbox_recipients r
    JOIN mailbox_messages m ON m.message_id = r.message_id
    WHERE r.character_id = @cid
      AND r.folder = 0
      AND r.saved_flag = 0
      AND r.deleted_flag = 0
      AND (m.unlimited_flag != 0 OR m.expire_at > CURRENT_TIMESTAMP)
    ORDER BY datetime(m.created_at) ASC, m.message_id ASC
    LIMIT @limit
),
stored AS (
    SELECT r.recipient_id, 1 AS mailbox_group
    FROM mailbox_recipients r
    JOIN mailbox_messages m ON m.message_id = r.message_id
    WHERE r.character_id = @cid
      AND r.folder = 0
      AND r.saved_flag = 1
      AND r.deleted_flag = 0
    ORDER BY datetime(COALESCE(r.saved_at, r.created_at)) ASC, m.message_id ASC
    LIMIT 10
),
selected AS (
    SELECT recipient_id, mailbox_group FROM inbox
    UNION ALL
    SELECT recipient_id, mailbox_group FROM stored
)
SELECT
    m.message_id,
    m.sender_character_id,
    m.sender_name,
    m.body,
    CASE WHEN r.received_gold_flag = 0 THEN m.gold ELSE 0 END AS gold,
    CASE
        WHEN m.unlimited_flag != 0 OR m.expire_at >= '9999-01-01 00:00:00' THEN 0
        ELSE MIN(
            2147483647,
            MAX(0, CAST(strftime('%s', m.expire_at) AS INTEGER) - CAST(strftime('%s', 'now') AS INTEGER)))
    END AS remain_seconds,
    CAST(strftime('%s', m.created_at) AS INTEGER) AS created_at_unix_seconds,
    r.read_flag,
    r.saved_flag,
    m.mail_type,
    m.source_protocol,
    s.mailbox_group
FROM selected s
JOIN mailbox_recipients r ON r.recipient_id = s.recipient_id
JOIN mailbox_messages m ON m.message_id = r.message_id
ORDER BY s.mailbox_group ASC, datetime(m.created_at) DESC, m.message_id DESC;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@limit", limit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var read = reader.GetInt32(7) != 0;
                    var saved = reader.GetInt32(8) != 0;
                    entries.Add(new MailboxListEntry
                    {
                        MessageId = reader.GetInt64(0),
                        SenderCharacterId = reader.GetInt32(1),
                        SenderName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Body = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        Gold = reader.GetInt32(4),
                        RemainSeconds = reader.GetInt32(5),
                        CreatedAtUnixSeconds = reader.GetInt32(6),
                        LetterStat = saved ? 3 : (read ? 2 : 1),
                        MailType = reader.GetInt32(9),
                        SourceProtocol = reader.GetInt32(10),
                    });
                }
            }

            foreach (var entry in entries)
            {
                entry.Attachments = LoadAttachments(connection, transaction, entry.MessageId, claimedOnly: false);
                if (entry.Attachments.Count > 0)
                    entry.FirstAttachmentExpireTime = entry.Attachments[0].ExpireTime;
            }

            transaction.Commit();
            return new MailboxInboxPage
            {
                Entries = entries,
                TotalCount = totalCount,
                LoadedInboxCount = entries.Count(x => x.LetterStat != 3),
            };
        }

        public MailboxClaimResult ClaimMail(int characterId, long claimObjectId)
        {
            if (characterId <= 0 || claimObjectId <= 0)
                return MailboxClaimResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            // 服务端协议：附件行 = AttachmentClaimFlag | attachment_id；整信/金币 = messageId。
            ClaimAttachmentTarget target = null;
            if (claimObjectId >= AttachmentClaimFlag)
            {
                target = LoadClaimAttachmentTarget(
                    connection, transaction, characterId, claimObjectId - AttachmentClaimFlag);
                if (target?.Attachment == null)
                    return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);
            }

            var messageId = target?.MessageId ?? claimObjectId;
            if (messageId <= 0)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var mailState = LoadClaimMailState(connection, transaction, characterId, messageId);
            if (mailState == null)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var attachments = target?.Attachment != null
                ? new List<MailboxAttachmentEntry> { target.Attachment }
                : LoadAttachments(connection, transaction, messageId, claimedOnly: true);

            var claimsGold = mailState.Gold > 0 && !mailState.ReceivedGold;
            if (attachments.Count == 0 && !claimsGold)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var main = new List<short>();
            var avatar = new List<short>();
            var pet = new List<short>();
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var attachment in attachments)
            {
                var core = MailboxItemCoreCodec.Decode(attachment);
                if (core == null || core.ItemId <= 0 || attachment.ItemCount <= 0)
                    return MailboxClaimResult.Fail(MailboxSendError.InvalidAttachment);
                if (core.ExpireTime > 0 && core.ExpireTime <= nowUnix)
                    return MailboxClaimResult.Fail(MailboxSendError.ExpiredItem);

                var count = Math.Max(1, attachment.ItemCount);
                if (NewInventoryStore.IsStackableKind(core.ItemKind))
                {
                    var metadata = SafeResolve(core.ItemId);
                    if (metadata?.StackLimit > 0
                        && WouldExceedCarryLimit(
                            CountOwned(connection, transaction, characterId, mailState.AccountId, core),
                            count,
                            metadata.StackLimit))
                    {
                        return MailboxClaimResult.Fail(MailboxSendError.ItemCarryLimitExceeded);
                    }
                    core.Count = count;
                }
                else if (core.ItemKind == ItemCore.KindAvatar)
                {
                    core.AvatarUid = 0;
                }
                else if (core.ItemKind == ItemCore.KindCreature)
                {
                    core.CreatureUid = 0;
                }

                var slots = core.ItemKind == ItemCore.KindAvatar
                    ? avatar
                    : core.ItemKind is ItemCore.KindCreature or ItemCore.KindCreatureEquipment or ItemCore.KindCreatureConsumable
                        ? pet
                        : main;

                if (!NewInventoryStore.TryGrantMailboxCore(
                        connection, transaction, characterId, mailState.AccountId, core, count, slots, out _))
                    return MailboxClaimResult.Fail(MailboxSendError.InventoryFull);
            }

            var claimedGold = 0;
            if (claimsGold)
            {
                if (!NewInventoryStore.TryGetMailboxGold(connection, transaction, characterId, mailState.AccountId, out var currentGold))
                    return MailboxClaimResult.Fail(MailboxSendError.ServerBusy);
                var goldLimit = LoadEffectiveGoldCarryLimit(connection, transaction, characterId);
                if (mailState.Gold > Math.Max(0, goldLimit) - currentGold)
                    return MailboxClaimResult.Fail(MailboxSendError.GoldCarryLimitExceeded);
                if (!NewInventoryStore.TryChangeMailboxGold(
                        connection, transaction, characterId, mailState.AccountId, mailState.Gold, out _))
                    return MailboxClaimResult.Fail(MailboxSendError.GoldCarryLimitExceeded);
                claimedGold = mailState.Gold;
            }

            if (!ReserveClaimState(connection, transaction, characterId, messageId, claimsGold, attachments))
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);
            MarkMailClaimed(connection, transaction, characterId, messageId, claimsGold, attachments);

            transaction.Commit();
            return new MailboxClaimResult
            {
                Success = true,
                MessageId = messageId,
                ClaimedGold = claimedGold,
                ClaimedAttachmentCount = attachments.Count,
                RemovedFromInbox = false,
                UpdatedMainSlots = main,
                UpdatedAvatarSlots = avatar,
                UpdatedPetSlots = pet,
            };
        }

        public MailboxDeleteResult DeleteMail(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var pending = ScalarInt(connection, transaction, @"
SELECT COUNT(*)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.message_id = @mid
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'));",
                ("@cid", characterId), ("@mid", messageId));
            if (pending != 1)
                return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);

            pending = ScalarInt(connection, transaction, @"
SELECT COUNT(*)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.message_id = @mid
  AND " + PlayerInboxVisiblePredicate + @"
  AND (
        (m.gold > 0 AND r.received_gold_flag = 0)
     OR EXISTS (
            SELECT 1 FROM mailbox_attachments a
            WHERE a.message_id = m.message_id AND a.claimed_flag = 0));",
                ("@cid", characterId), ("@mid", messageId));
            if (pending != 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            var changed = Execute(connection, transaction, @"
UPDATE mailbox_recipients
SET deleted_flag = 1,
    deleted_at = CURRENT_TIMESTAMP,
    read_flag = 1,
    read_at = COALESCE(read_at, CURRENT_TIMESTAMP)
WHERE character_id = @cid AND message_id = @mid AND folder = 0 AND deleted_flag = 0
  AND EXISTS (
      SELECT 1 FROM mailbox_messages m
      WHERE m.message_id = mailbox_recipients.message_id
        AND (mailbox_recipients.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now')));",
                ("@cid", characterId), ("@mid", messageId));
            if (changed != 1)
                return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);

            transaction.Commit();
            return new MailboxDeleteResult { Success = true, MessageId = messageId };
        }

        public MailboxDeleteResult MarkMailRead(int characterId, long messageId)
            => SetRecipientFlag(characterId, messageId, "read_flag=1,read_at=COALESCE(read_at,CURRENT_TIMESTAMP)");

        public MailboxDeleteResult SaveMail(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var count = ScalarInt(connection, transaction,
                "SELECT COUNT(*) FROM mailbox_recipients WHERE character_id=@cid AND folder=0 AND saved_flag=1 AND deleted_flag=0;",
                ("@cid", characterId));
            var already = ScalarInt(connection, transaction,
                "SELECT COUNT(*) FROM mailbox_recipients WHERE character_id=@cid AND message_id=@mid AND saved_flag=1 AND deleted_flag=0;",
                ("@cid", characterId), ("@mid", messageId));
            if (already == 0 && count >= 10)
                return MailboxDeleteResult.Fail(MailboxSendError.MailboxStorageFull);

            var changed = Execute(connection, transaction, @"
UPDATE mailbox_recipients
SET saved_flag=1, read_flag=1,
    saved_at=COALESCE(saved_at,CURRENT_TIMESTAMP),
    read_at=COALESCE(read_at,CURRENT_TIMESTAMP)
WHERE character_id=@cid AND message_id=@mid AND folder=0 AND deleted_flag=0;",
                ("@cid", characterId), ("@mid", messageId));
            if (changed != 1)
                return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);

            transaction.Commit();
            return new MailboxDeleteResult { Success = true, MessageId = messageId };
        }

        public MailboxExpirationBatchResult MaintainExpiredMail(int expireBatchSize = 200, int purgeBatchSize = 100)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            expireBatchSize = Math.Max(1, expireBatchSize);
            purgeBatchSize = Math.Max(1, purgeBatchSize);
            var expiredByCharacter = new Dictionary<int, List<long>>();

            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
WITH expired AS (
    SELECT m.message_id
    FROM mailbox_messages m
    WHERE m.unlimited_flag = 0
      AND m.expire_at <= CURRENT_TIMESTAMP
      AND EXISTS (
          SELECT 1 FROM mailbox_recipients active
          WHERE active.message_id = m.message_id
            AND active.folder = 0
            AND active.saved_flag = 0
            AND active.deleted_flag = 0)
    ORDER BY m.expire_at, m.message_id
    LIMIT @batch
)
SELECT r.character_id, r.message_id
FROM mailbox_recipients r
JOIN expired e ON e.message_id = r.message_id
WHERE r.folder = 0 AND r.saved_flag = 0 AND r.deleted_flag = 0
ORDER BY r.character_id, r.message_id;";
                select.Parameters.AddWithValue("@batch", expireBatchSize);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var cid = reader.GetInt32(0);
                    if (!expiredByCharacter.TryGetValue(cid, out var ids))
                    {
                        ids = new List<long>();
                        expiredByCharacter[cid] = ids;
                    }
                    ids.Add(reader.GetInt64(1));
                }
            }

            var expiredRecipientCount = Execute(connection, transaction, @"
WITH expired AS (
    SELECT m.message_id
    FROM mailbox_messages m
    WHERE m.unlimited_flag = 0
      AND m.expire_at <= CURRENT_TIMESTAMP
      AND EXISTS (
          SELECT 1 FROM mailbox_recipients active
          WHERE active.message_id = m.message_id
            AND active.folder = 0
            AND active.saved_flag = 0
            AND active.deleted_flag = 0)
    ORDER BY m.expire_at, m.message_id
    LIMIT @batch
)
UPDATE mailbox_recipients
SET deleted_flag = 2, deleted_at = COALESCE(deleted_at, CURRENT_TIMESTAMP)
WHERE folder = 0 AND saved_flag = 0 AND deleted_flag = 0
  AND message_id IN (SELECT message_id FROM expired);",
                ("@batch", expireBatchSize));

            var purgedMessageCount = Execute(connection, transaction, @"
DELETE FROM mailbox_messages
WHERE message_id IN (
    SELECT message_id FROM mailbox_messages
    WHERE unlimited_flag = 0
      AND expire_at <= datetime('now', '-30 days')
      AND NOT EXISTS (
          SELECT 1 FROM mailbox_recipients r
          WHERE r.message_id = mailbox_messages.message_id
            AND r.folder = 0 AND r.saved_flag = 1 AND r.deleted_flag = 0)
    ORDER BY expire_at, message_id
    LIMIT @batch);",
                ("@batch", purgeBatchSize));

            transaction.Commit();
            return new MailboxExpirationBatchResult
            {
                ExpiredRecipientCount = expiredRecipientCount,
                PurgedMessageCount = purgedMessageCount,
                Recipients = expiredByCharacter
                    .Select(pair => new MailboxExpirationRecipient
                    {
                        CharacterId = pair.Key,
                        MessageIds = pair.Value,
                    })
                    .ToList(),
            };
        }

        public MailboxCampaignBatchResult ProcessSystemMailCampaignBatch(
            string campaignId,
            MailboxSendRequest template,
            int batchSize = 500)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || template == null || template.SenderCharacterId <= 0)
                return MailboxCampaignBatchResult.Fail(campaignId, MailboxSendError.InvalidRequest);

            campaignId = campaignId.Trim();
            batchSize = Math.Clamp(batchSize, 1, 1000);
            var hash = HashRequest(template);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            var max = ScalarInt(connection, transaction,
                "SELECT COALESCE(MAX(character_id),0) FROM characters WHERE delete_flag=0;");
            Execute(connection, transaction,
                "INSERT OR IGNORE INTO mailbox_campaigns(campaign_id,payload_hash,max_character_id) VALUES(@id,@hash,@max);",
                ("@id", campaignId), ("@hash", hash), ("@max", max));

            using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = "SELECT payload_hash,last_character_id,status,max_character_id FROM mailbox_campaigns WHERE campaign_id=@id;";
            state.Parameters.AddWithValue("@id", campaignId);
            using var reader = state.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != hash)
                return MailboxCampaignBatchResult.Fail(campaignId, MailboxSendError.InvalidRequest);

            var last = reader.GetInt32(1);
            var complete = reader.GetInt32(2) != 0;
            max = reader.GetInt32(3);
            reader.Close();

            if (complete)
            {
                transaction.Commit();
                return new MailboxCampaignBatchResult
                {
                    Success = true,
                    CampaignId = campaignId,
                    LastCharacterId = last,
                    Completed = true,
                };
            }

            var recipients = new List<(int Id, int Account, string Name)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
SELECT character_id,account_id,name
FROM characters
WHERE delete_flag=0 AND character_id>@last AND character_id<=@max
ORDER BY character_id LIMIT @limit;";
                select.Parameters.AddWithValue("@last", last);
                select.Parameters.AddWithValue("@max", max);
                select.Parameters.AddWithValue("@limit", batchSize);
                using var rows = select.ExecuteReader();
                while (rows.Read())
                    recipients.Add((rows.GetInt32(0), rows.GetInt32(1), rows.GetString(2)));
            }

            foreach (var recipient in recipients)
            {
                var mail = CloneCampaign(template, campaignId, recipient);
                var send = SendSystemInTransaction(connection, transaction, mail);
                if (!send.Success)
                    return MailboxCampaignBatchResult.Fail(campaignId, send.Error);
                Execute(connection, transaction,
                    "INSERT OR IGNORE INTO mailbox_campaign_deliveries(campaign_id,character_id,message_id) VALUES(@id,@cid,@mid);",
                    ("@id", campaignId), ("@cid", recipient.Id), ("@mid", send.MessageId));
                last = recipient.Id;
            }

            var more = ScalarInt(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM characters WHERE delete_flag=0 AND character_id>@last AND character_id<=@max);",
                ("@last", last), ("@max", max)) != 0;
            Execute(connection, transaction, @"
UPDATE mailbox_campaigns
SET last_character_id=@last, status=@status, updated_at=CURRENT_TIMESTAMP,
    completed_at=CASE WHEN @status=1 THEN CURRENT_TIMESTAMP ELSE NULL END
WHERE campaign_id=@id;",
                ("@last", last), ("@status", more ? 0 : 1), ("@id", campaignId));
            transaction.Commit();
            return new MailboxCampaignBatchResult
            {
                Success = true,
                CampaignId = campaignId,
                DeliveredCount = recipients.Count,
                LastCharacterId = last,
                Completed = !more,
            };
        }

        public static int CalculateFeeGold(int sendGold, int attachmentCount)
            => checked((attachmentCount > 0 ? attachmentCount * 1000 : 100)
                + (int)Math.Min(10000L, Math.Max(0L, (long)sendGold * 5 / 100)));

        internal static bool WouldExceedCarryLimit(int currentCount, int incomingCount, int carryLimit)
            => carryLimit > 0 && incomingCount > carryLimit - Math.Max(0, currentCount);

        private MailboxSendResult Send(MailboxSendRequest request, bool system)
        {
            if (request == null)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = system
                ? SendSystemInTransaction(connection, transaction, request)
                : SendPlayerInTransaction(connection, transaction, request);
            if (result.Success)
                transaction.Commit();
            return result;
        }

        private static MailboxSendResult SendPlayerInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request)
        {
            if (!ValidateRequest(request, system: false, out var error))
                return MailboxSendResult.Fail(error);
            if (request.SenderCharacterId == request.ReceiverCharacterId)
                return MailboxSendResult.Fail(MailboxSendError.SelfSendNotAllowed);
            if (!CharacterIsActive(connection, transaction, request.SenderCharacterId)
                || !CharacterIsActive(connection, transaction, request.ReceiverCharacterId))
                return MailboxSendResult.Fail(MailboxSendError.ReceiverNotFound);

            var hash = HashRequest(request);
            if (TryIdempotent(connection, transaction, request, hash, out var replay))
                return replay;

            var sources = new List<MailboxInventorySource>();
            foreach (var attachment in request.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>())
            {
                if (!NewInventoryStore.TryReadMailboxSource(
                        connection, transaction,
                        request.SenderCharacterId, request.SenderAccountId,
                        attachment.ItemType, attachment.ItemSlot,
                        attachment.ItemId, attachment.ItemCount,
                        out var source))
                    return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);

                var policy = MailboxSendPolicy.ValidateAttachment(request, source.Core);
                if (policy != MailboxSendError.None)
                    return MailboxSendResult.Fail(policy);

                if (MailboxSendPolicy.IsTradeLimitItem(ItemMetadataResolver.Resolve(source.Core.ItemId)))
                    source.Core = MailboxSendPolicy.DecrementTradeCount(source.Core);
                sources.Add(source);
            }

            var fee = CalculateFeeGold(request.Gold, sources.Count);
            var cost = (long)request.Gold + fee;
            if (cost > int.MaxValue
                || !NewInventoryStore.TryChangeMailboxGold(
                    connection, transaction, request.SenderCharacterId, request.SenderAccountId, -(int)cost, out var gold))
                return MailboxSendResult.Fail(MailboxSendError.InsufficientGold);

            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                var consume = NewInventoryStore.IsStackableKind(source.Core.ItemKind)
                    ? source.Core.Count
                    : 1;
                if (!NewInventoryStore.TryConsumeMailboxSource(
                        connection, transaction, request.SenderCharacterId, request.SenderAccountId, source, consume))
                    return MailboxSendResult.Fail(MailboxSendError.ServerBusy);
            }

            var message = InsertMessage(connection, transaction, request, fee, hash, unlimited: false, DateTimeOffset.UtcNow.AddDays(15));
            InsertRecipient(connection, transaction, message, request.ReceiverCharacterId, folder: 0);
            InsertRecipient(connection, transaction, message, request.SenderCharacterId, folder: 1);
            for (var i = 0; i < sources.Count; i++)
                InsertAttachment(connection, transaction, message, i, request.Attachments[i], sources[i].Core, sources[i]);

            return new MailboxSendResult
            {
                Success = true,
                MessageId = message,
                FeeGold = fee,
                UpdatedGold = gold,
            };
        }

        private static MailboxSendResult SendSystemInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request)
        {
            if (!ValidateRequest(request, system: true, out var error))
                return MailboxSendResult.Fail(error);
            if (!CharacterIsActive(connection, transaction, request.ReceiverCharacterId))
                return MailboxSendResult.Fail(MailboxSendError.ReceiverNotFound);

            var hash = HashRequest(request);
            if (TryIdempotent(connection, transaction, request, hash, out var replay))
                return replay;

            var unlimited = request.Unlimited ?? true;
            var expires = request.ExpireAtUtc ?? DateTimeOffset.UtcNow.AddDays(15);
            if (!unlimited && expires <= DateTimeOffset.UtcNow)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            var snapshots = new List<MailboxAttachmentSnapshot>();
            var attachments = request.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>();
            for (var i = 0; i < attachments.Count; i++)
            {
                if (!TryCreateSystemAttachmentSnapshot(i, attachments[i], out var snapshot))
                    return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                snapshots.Add(snapshot);
            }

            var message = InsertMessage(connection, transaction, request, 0, hash, unlimited, expires);
            InsertRecipient(connection, transaction, message, request.ReceiverCharacterId, folder: 0);
            foreach (var snapshot in snapshots)
                InsertAttachmentSnapshot(connection, transaction, message, snapshot);
            InsertSystemMailAudit(connection, transaction, message, request, hash, unlimited, expires, snapshots);
            return new MailboxSendResult
            {
                Success = true,
                MessageId = message,
                MessageIds = new[] { message },
            };
        }

        private static bool TryCreateSystemAttachmentSnapshot(
            int ordinal,
            MailboxSendAttachmentRequest request,
            out MailboxAttachmentSnapshot snapshot)
        {
            snapshot = null;
            var metadata = SafeResolve(request.ItemId);
            if (request?.ItemCoreData != null && request.ItemCoreData.Length > 0)
                return TryCreateExplicitSystemAttachmentSnapshot(ordinal, request, metadata, out snapshot);

            if (!SystemMailboxAttachmentFactory.TryCreate(request, out var core, out var itemKind, out _))
                return false;

            snapshot = new MailboxAttachmentSnapshot
            {
                Ordinal = ordinal,
                ItemType = request.ItemType,
                SourceListType = ResolveMailboxAttachmentListType(request.ItemType, request.ItemId, metadata),
                SourceSlotIndex = request.ItemSlot,
                SourceItemUid = 0,
                ItemTemplateId = request.ItemId,
                ItemKind = itemKind,
                ItemCount = Math.Max(1, request.ItemCount),
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = request.OptionValue,
                EquipmentLockId = 0,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = string.IsNullOrWhiteSpace(request.ExtraJson) ? "{}" : request.ExtraJson,
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = request.DetailJson ?? string.Empty,
            };
            return true;
        }

        /// <summary>
        /// Source 1:1: 自定义装备邮件附件可直接携带 82 字节 ItemCore，
        /// 避免工厂重建时丢失强化/增幅/锻造/品级。
        /// </summary>
        private static bool TryCreateExplicitSystemAttachmentSnapshot(
            int ordinal,
            MailboxSendAttachmentRequest request,
            ItemMetadata metadata,
            out MailboxAttachmentSnapshot snapshot)
        {
            snapshot = null;
            if (request?.ItemCoreData == null || request.ItemCoreData.Length != ItemCore.Size)
                return false;

            var core = ItemCore.FromBytes(request.ItemCoreData);
            if (core == null || core.ItemId <= 0 || core.ItemId != request.ItemId)
                return false;

            core = core.Copy();
            var itemCount = Math.Max(1, request.ItemCount);
            if (NewInventoryStore.IsStackableKind(core.ItemKind))
                core.Count = itemCount;
            else
                itemCount = 1;

            core.SortLockFlag = 0;
            core.EquipmentLockId = 0;
            if (core.ItemKind == ItemCore.KindAvatar)
                core.AvatarUid = 0;
            else if (core.ItemKind == ItemCore.KindCreature)
                core.CreatureUid = 0;

            snapshot = new MailboxAttachmentSnapshot
            {
                Ordinal = ordinal,
                ItemType = request.ItemType,
                SourceListType = ResolveMailboxAttachmentListType(request.ItemType, request.ItemId, metadata),
                SourceSlotIndex = request.ItemSlot,
                SourceItemUid = 0,
                ItemTemplateId = core.ItemId,
                ItemKind = MailboxItemCoreCodec.GetLegacyKindName(core),
                ItemCount = itemCount,
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = core.AbilityNo,
                EquipmentLockId = 0,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = string.IsNullOrWhiteSpace(request.ExtraJson) ? "{}" : request.ExtraJson,
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = request.DetailJson ?? string.Empty,
            };
            return true;
        }

        private static int ResolveMailboxAttachmentListType(byte itemType, int itemTemplateId, ItemMetadata metadata)
        {
            var requestedList = MapMailboxItemType(itemType);
            if (requestedList == (int)InventoryListType.Pet || metadata == null)
                return requestedList;

            var isPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            return isPetConsumable || isPetEquipment
                ? (int)InventoryListType.Pet
                : requestedList;
        }

        private static int MapMailboxItemType(byte itemType)
            => itemType switch
            {
                1 => (int)InventoryListType.Avatar,
                3 or 7 => (int)InventoryListType.Pet,
                _ => (int)InventoryListType.Main,
            };

        private static bool ValidateRequest(MailboxSendRequest r, bool system, out MailboxSendError error)
        {
            error = MailboxSendError.None;
            if (r.SenderCharacterId <= 0 || r.ReceiverCharacterId <= 0 || r.Gold < 0)
            {
                error = MailboxSendError.InvalidRequest;
                return false;
            }

            var attachments = r.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>();
            if (attachments.Count > 10)
            {
                error = MailboxSendError.TooManyAttachments;
                return false;
            }

            if (attachments.Any(a => a == null || a.ItemId <= 0 || a.ItemCount <= 0))
            {
                error = MailboxSendError.InvalidAttachment;
                return false;
            }

            if (r.Gold == 0 && attachments.Count == 0 && string.IsNullOrWhiteSpace(r.Text))
            {
                error = system ? MailboxSendError.InvalidRequest : MailboxSendError.EmptyContent;
                return false;
            }

            return true;
        }

        private static bool CharacterIsActive(SqliteConnection c, SqliteTransaction t, int id)
            => ScalarInt(c, t, "SELECT COUNT(*) FROM characters WHERE character_id=@id AND delete_flag=0;", ("@id", id)) == 1;

        private static void ExpireNormalInbox(SqliteConnection c, SqliteTransaction t, int characterId)
        {
            Execute(c, t, @"
UPDATE mailbox_recipients
SET deleted_flag = 2, deleted_at = COALESCE(deleted_at, CURRENT_TIMESTAMP)
WHERE character_id = @id
  AND folder = 0
  AND saved_flag = 0
  AND deleted_flag = 0
  AND message_id IN (
      SELECT message_id FROM mailbox_messages
      WHERE unlimited_flag = 0 AND expire_at <= CURRENT_TIMESTAMP);",
                ("@id", characterId));
        }

        private MailboxDeleteResult SetRecipientFlag(int characterId, long messageId, string set)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var changed = Execute(connection, null, @"
UPDATE mailbox_recipients SET " + set + @"
WHERE character_id=@cid AND message_id=@mid AND folder=0 AND deleted_flag=0
  AND EXISTS (
      SELECT 1 FROM mailbox_messages m
      WHERE m.message_id = mailbox_recipients.message_id
        AND (m.unlimited_flag <> 0 OR datetime(m.expire_at) > datetime('now')));",
                ("@cid", characterId), ("@mid", messageId));
            return changed == 1
                ? new MailboxDeleteResult { Success = true, MessageId = messageId }
                : MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);
        }

        private sealed class ClaimMailState
        {
            public int AccountId { get; set; }
            public int Gold { get; set; }
            public bool ReceivedGold { get; set; }
        }

        private sealed class ClaimAttachmentTarget
        {
            public long MessageId { get; set; }
            public MailboxAttachmentEntry Attachment { get; set; }
        }

        private static ClaimMailState LoadClaimMailState(
            SqliteConnection c, SqliteTransaction t, int characterId, long messageId)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"
SELECT ch.account_id, m.gold, r.received_gold_flag
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
JOIN characters ch ON ch.character_id = r.character_id
WHERE r.character_id = @cid
  AND r.message_id = @mid
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@mid", messageId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new ClaimMailState
            {
                AccountId = reader.GetInt32(0),
                Gold = reader.GetInt32(1),
                ReceivedGold = reader.GetInt32(2) != 0,
            };
        }

        private static ClaimAttachmentTarget LoadClaimAttachmentTarget(
            SqliteConnection c, SqliteTransaction t, int characterId, long objectId)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"
SELECT m.message_id,
       a.attachment_id, a.ordinal, a.item_type, a.source_list_type, a.source_slot_index, a.source_item_uid,
       a.item_template_id, a.item_kind, a.item_count, a.instance_value, a.durability, a.seal_flag,
       a.option_value, a.expire_time, a.marker_16, a.pet_serial_or_handle, a.extra_json, a.item_core, a.detail_json
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
JOIN mailbox_attachments a ON a.attachment_id = @id AND a.message_id = m.message_id AND a.claimed_flag = 0
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
            command.Parameters.AddWithValue("@id", objectId);
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new ClaimAttachmentTarget
            {
                MessageId = reader.GetInt64(0),
                Attachment = ReadAttachment(reader, start: 1),
            };
        }

        private static List<MailboxAttachmentEntry> LoadAttachments(
            SqliteConnection c,
            SqliteTransaction t,
            long messageId,
            bool claimedOnly,
            long? attachmentId = null)
        {
            var result = new List<MailboxAttachmentEntry>();
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"
SELECT attachment_id, ordinal, item_type, source_list_type, source_slot_index, source_item_uid,
       item_template_id, item_kind, item_count, instance_value, durability, seal_flag,
       option_value, expire_time, marker_16, pet_serial_or_handle, extra_json, item_core, detail_json
FROM mailbox_attachments
WHERE message_id = @mid
  AND (@claimedOnly = 0 OR claimed_flag = 0)
  AND (@aid IS NULL OR attachment_id = @aid)
ORDER BY ordinal;";
            command.Parameters.AddWithValue("@mid", messageId);
            command.Parameters.AddWithValue("@claimedOnly", claimedOnly ? 1 : 0);
            command.Parameters.AddWithValue("@aid", attachmentId.HasValue ? attachmentId.Value : DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadAttachment(reader, start: 0));
            return result;
        }

        private static MailboxAttachmentEntry ReadAttachment(SqliteDataReader reader, int start)
            => new MailboxAttachmentEntry
            {
                AttachmentId = reader.GetInt64(start + 0),
                Ordinal = reader.GetInt32(start + 1),
                ItemType = (byte)reader.GetInt32(start + 2),
                SourceListType = reader.GetInt32(start + 3),
                SourceSlotIndex = reader.GetInt32(start + 4),
                SourceItemUid = reader.GetInt64(start + 5),
                ItemTemplateId = reader.GetInt32(start + 6),
                ItemKind = reader.IsDBNull(start + 7) ? string.Empty : reader.GetString(start + 7),
                ItemCount = reader.GetInt32(start + 8),
                InstanceValue = reader.GetInt32(start + 9),
                Durability = reader.GetInt32(start + 10),
                SealFlag = reader.GetInt32(start + 11),
                OptionValue = reader.GetInt32(start + 12),
                ExpireTime = reader.GetInt32(start + 13),
                Marker16 = reader.GetInt32(start + 14),
                PetSerialOrHandle = reader.GetInt32(start + 15),
                ExtraJson = reader.IsDBNull(start + 16) ? "{}" : reader.GetString(start + 16),
                ItemCoreData = reader.IsDBNull(start + 17) ? Array.Empty<byte>() : (byte[])reader[start + 17],
                DetailJson = reader.IsDBNull(start + 18) ? string.Empty : reader.GetString(start + 18),
            };

        private static bool ReserveClaimState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long messageId,
            bool claimsGold,
            IReadOnlyList<MailboxAttachmentEntry> attachments)
        {
            foreach (var attachment in attachments)
            {
                var changed = Execute(connection, transaction, @"
UPDATE mailbox_attachments
SET claimed_flag = 2
WHERE attachment_id = @id AND message_id = @mid AND claimed_flag = 0;",
                    ("@id", attachment.AttachmentId), ("@mid", messageId));
                if (changed != 1)
                    return false;
            }

            if (claimsGold)
            {
                var changed = Execute(connection, transaction, @"
UPDATE mailbox_recipients
SET received_gold_flag = 2
WHERE character_id = @cid AND message_id = @mid AND received_gold_flag = 0;",
                    ("@cid", characterId), ("@mid", messageId));
                if (changed != 1)
                    return false;
            }

            return true;
        }

        private static void MarkMailClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long messageId,
            bool claimsGold,
            IReadOnlyList<MailboxAttachmentEntry> attachments)
        {
            foreach (var attachment in attachments)
            {
                Execute(connection, transaction, @"
UPDATE mailbox_attachments
SET claimed_flag = 1, claimed_at = CURRENT_TIMESTAMP
WHERE attachment_id = @id AND message_id = @mid AND claimed_flag = 2;",
                    ("@id", attachment.AttachmentId), ("@mid", messageId));
            }

            if (claimsGold)
            {
                Execute(connection, transaction, @"
UPDATE mailbox_recipients
SET received_gold_flag = 1
WHERE character_id = @cid AND message_id = @mid AND received_gold_flag = 2;",
                    ("@cid", characterId), ("@mid", messageId));
            }
        }

        private static long InsertMessage(
            SqliteConnection c,
            SqliteTransaction t,
            MailboxSendRequest r,
            int fee,
            string hash,
            bool unlimited,
            DateTimeOffset expires)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"
INSERT INTO mailbox_messages(
    sender_character_id, sender_account_id, sender_name,
    receiver_character_id, receiver_account_id, receiver_name,
    title, body, gold, fee_gold, mail_type, source_protocol,
    idempotency_key, request_hash, unlimited_flag, expire_at)
VALUES(
    @sid, @said, @sname, @rid, @raid, @rname,
    @title, @body, @gold, @fee, @type, @protocol,
    @key, @hash, @unlimited, @expire);
SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@sid", r.SenderCharacterId);
            command.Parameters.AddWithValue("@said", r.SenderAccountId);
            command.Parameters.AddWithValue("@sname", r.SenderName ?? string.Empty);
            command.Parameters.AddWithValue("@rid", r.ReceiverCharacterId);
            command.Parameters.AddWithValue("@raid", r.ReceiverAccountId);
            command.Parameters.AddWithValue("@rname", r.ReceiverName ?? string.Empty);
            command.Parameters.AddWithValue("@title", r.Text ?? string.Empty);
            command.Parameters.AddWithValue("@body", r.Text ?? string.Empty);
            command.Parameters.AddWithValue("@gold", r.Gold);
            command.Parameters.AddWithValue("@fee", fee);
            command.Parameters.AddWithValue("@type", r.MailType);
            command.Parameters.AddWithValue("@protocol", r.SourceProtocol);
            command.Parameters.AddWithValue("@key",
                string.IsNullOrWhiteSpace(r.IdempotencyKey) ? DBNull.Value : r.IdempotencyKey.Trim());
            command.Parameters.AddWithValue("@hash", hash);
            command.Parameters.AddWithValue("@unlimited", unlimited ? 1 : 0);
            command.Parameters.AddWithValue("@expire",
                unlimited ? "9999-12-31 23:59:59" : expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static void InsertRecipient(SqliteConnection c, SqliteTransaction t, long mid, int cid, int folder)
            => Execute(c, t,
                "INSERT OR IGNORE INTO mailbox_recipients(message_id,character_id,folder) VALUES(@mid,@cid,@folder);",
                ("@mid", mid), ("@cid", cid), ("@folder", folder));

        private static void InsertAttachment(
            SqliteConnection c,
            SqliteTransaction t,
            long mid,
            int ordinal,
            MailboxSendAttachmentRequest request,
            ItemCore core,
            MailboxInventorySource source)
        {
            Execute(c, t, @"
INSERT INTO mailbox_attachments(
    message_id, ordinal, item_type, source_list_type, source_slot_index, source_item_uid,
    item_template_id, item_kind, item_count, instance_value, durability, seal_flag,
    option_value, equipment_lock_id, expire_time, marker_16, pet_serial_or_handle,
    extra_json, item_core, detail_json)
VALUES(
    @mid, @ordinal, @type, @list, @slot, @uid,
    @item, @kind, @count, @value, @durability, @seal,
    @option, @lock, @expire, @marker, @pet,
    @extra, @core, @detail);",
                ("@mid", mid),
                ("@ordinal", ordinal),
                ("@type", request.ItemType),
                ("@list", source == null ? 0 : (int)source.ListType),
                ("@slot", source == null ? request.ItemSlot : source.SlotIndex),
                ("@uid", source?.ItemUid ?? 0),
                ("@item", core.ItemId),
                ("@kind", MailboxItemCoreCodec.GetLegacyKindName(core)),
                ("@count", NewInventoryStore.IsStackableKind(core.ItemKind) ? core.Count : 1),
                ("@value", core.Value),
                ("@durability", core.Durability),
                ("@seal", core.SealFlag),
                ("@option", request.OptionValue),
                ("@lock", core.EquipmentLockId),
                ("@expire", core.ExpireTime),
                ("@marker", core.Marker16),
                ("@pet", core.CreatureUid),
                ("@extra", request.ExtraJson ?? "{}"),
                ("@core", core.ToBytes()),
                ("@detail", request.DetailJson ?? string.Empty));
        }

        private static void InsertAttachmentSnapshot(
            SqliteConnection c,
            SqliteTransaction t,
            long mid,
            MailboxAttachmentSnapshot snapshot)
        {
            Execute(c, t, @"
INSERT INTO mailbox_attachments(
    message_id, ordinal, item_type, source_list_type, source_slot_index, source_item_uid,
    item_template_id, item_kind, item_count, instance_value, durability, seal_flag,
    option_value, equipment_lock_id, expire_time, marker_16, pet_serial_or_handle,
    extra_json, item_core, detail_json)
VALUES(
    @mid, @ordinal, @type, @list, @slot, @uid,
    @item, @kind, @count, @value, @durability, @seal,
    @option, @lock, @expire, @marker, @pet,
    @extra, @core, @detail);",
                ("@mid", mid),
                ("@ordinal", snapshot.Ordinal),
                ("@type", snapshot.ItemType),
                ("@list", snapshot.SourceListType),
                ("@slot", snapshot.SourceSlotIndex),
                ("@uid", snapshot.SourceItemUid),
                ("@item", snapshot.ItemTemplateId),
                ("@kind", snapshot.ItemKind),
                ("@count", snapshot.ItemCount),
                ("@value", snapshot.InstanceValue),
                ("@durability", snapshot.Durability),
                ("@seal", snapshot.SealFlag),
                ("@option", snapshot.OptionValue),
                ("@lock", snapshot.EquipmentLockId),
                ("@expire", snapshot.ExpireTime),
                ("@marker", snapshot.Marker16),
                ("@pet", snapshot.PetSerialOrHandle),
                ("@extra", snapshot.ExtraJson ?? "{}"),
                ("@core", snapshot.ItemCoreData ?? Array.Empty<byte>()),
                ("@detail", snapshot.DetailJson ?? string.Empty));
        }

        private static void InsertSystemMailAudit(
            SqliteConnection c,
            SqliteTransaction t,
            long mid,
            MailboxSendRequest r,
            string hash,
            bool unlimited,
            DateTimeOffset expires,
            IReadOnlyList<MailboxAttachmentSnapshot> snapshots)
        {
            long auditId;
            using (var command = c.CreateCommand())
            {
                command.Transaction = t;
                command.CommandText = @"
INSERT INTO mailbox_system_mail_audit(
    message_id, actor_account_id, actor_character_id, actor_name, audit_reason,
    receiver_account_id, receiver_character_id, receiver_name, gold, attachment_count,
    mail_type, source_protocol, idempotency_key, request_hash, unlimited_flag, expire_at)
VALUES(
    @mid, @aid, @cid, @name, @reason,
    @raid, @rid, @rname, @gold, @count,
    @type, @protocol, @key, @hash, @unlimited, @expire);
SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@mid", mid);
                command.Parameters.AddWithValue("@aid", r.SenderAccountId);
                command.Parameters.AddWithValue("@cid", r.SenderCharacterId);
                command.Parameters.AddWithValue("@name",
                    string.IsNullOrWhiteSpace(r.AuditActor) ? r.SenderName ?? string.Empty : r.AuditActor);
                command.Parameters.AddWithValue("@reason",
                    string.IsNullOrWhiteSpace(r.AuditReason) ? "system-mail" : r.AuditReason);
                command.Parameters.AddWithValue("@raid", r.ReceiverAccountId);
                command.Parameters.AddWithValue("@rid", r.ReceiverCharacterId);
                command.Parameters.AddWithValue("@rname", r.ReceiverName ?? string.Empty);
                command.Parameters.AddWithValue("@gold", r.Gold);
                command.Parameters.AddWithValue("@count", snapshots.Count);
                command.Parameters.AddWithValue("@type", r.MailType);
                command.Parameters.AddWithValue("@protocol", r.SourceProtocol);
                command.Parameters.AddWithValue("@key",
                    string.IsNullOrWhiteSpace(r.IdempotencyKey) ? DBNull.Value : r.IdempotencyKey);
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@unlimited", unlimited ? 1 : 0);
                command.Parameters.AddWithValue("@expire",
                    unlimited ? "9999-12-31 23:59:59" : expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                auditId = Convert.ToInt64(command.ExecuteScalar());
            }

            foreach (var snapshot in snapshots)
            {
                Execute(c, t, @"
INSERT INTO mailbox_system_mail_audit_attachments(
    audit_id, ordinal, item_template_id, item_kind, item_count,
    instance_value, seal_flag, expire_time)
VALUES(@auditId, @ordinal, @item, @kind, @count, @value, @seal, @expire);",
                    ("@auditId", auditId),
                    ("@ordinal", snapshot.Ordinal),
                    ("@item", snapshot.ItemTemplateId),
                    ("@kind", snapshot.ItemKind),
                    ("@count", snapshot.ItemCount),
                    ("@value", snapshot.InstanceValue),
                    ("@seal", snapshot.SealFlag),
                    ("@expire", snapshot.ExpireTime));
            }
        }

        private static bool TryIdempotent(
            SqliteConnection c,
            SqliteTransaction t,
            MailboxSendRequest r,
            string hash,
            out MailboxSendResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(r.IdempotencyKey))
                return false;

            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"
SELECT message_id, fee_gold, request_hash
FROM mailbox_messages
WHERE sender_character_id = @cid AND idempotency_key = @key;";
            command.Parameters.AddWithValue("@cid", r.SenderCharacterId);
            command.Parameters.AddWithValue("@key", r.IdempotencyKey.Trim());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return false;

            result = reader.GetString(2) == hash
                ? new MailboxSendResult
                {
                    Success = true,
                    MessageId = reader.GetInt64(0),
                    FeeGold = reader.GetInt32(1),
                    MessageIds = new[] { reader.GetInt64(0) },
                    Replayed = true,
                }
                : MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
            return true;
        }

        private static string HashRequest(MailboxSendRequest r)
        {
            var text = string.Join("|",
                r.SenderCharacterId,
                r.SenderAccountId,
                r.ReceiverCharacterId,
                r.ReceiverAccountId,
                r.Gold,
                r.MailType,
                r.SourceProtocol,
                r.Text,
                r.Unlimited,
                r.ExpireAtUtc?.ToString("O"),
                string.Join(";", (r.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>()).Select(a =>
                    a == null
                        ? "null"
                        : string.Join("|",
                            a.ItemType, a.ItemSlot, a.ItemId, a.ItemCount, a.InstanceValue,
                            a.Durability, a.SealFlag, a.OptionValue, a.ExpireTime,
                            a.Marker16, a.PetSerialOrHandle, a.ExtraJson,
                            a.ItemCoreData != null && a.ItemCoreData.Length > 0
                                ? Convert.ToHexString(a.ItemCoreData)
                                : string.Empty,
                            a.DetailJson ?? string.Empty))));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private static MailboxSendRequest CloneCampaign(
            MailboxSendRequest t,
            string campaign,
            (int Id, int Account, string Name) r)
            => new MailboxSendRequest
            {
                SenderCharacterId = t.SenderCharacterId,
                SenderAccountId = t.SenderAccountId,
                SenderName = t.SenderName,
                ReceiverCharacterId = r.Id,
                ReceiverAccountId = r.Account,
                ReceiverName = r.Name,
                Gold = t.Gold,
                Text = t.Text,
                MailType = t.MailType,
                SourceProtocol = t.SourceProtocol,
                Unlimited = t.Unlimited,
                ExpireAtUtc = t.ExpireAtUtc,
                AuditActor = t.AuditActor,
                AuditReason = string.IsNullOrWhiteSpace(t.AuditReason) ? "campaign:" + campaign : t.AuditReason,
                IdempotencyKey = "campaign:" + campaign + ":" + r.Id,
                Attachments = t.Attachments,
            };

        private static int CountOwned(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            ItemCore core)
        {
            if (!NewInventoryStore.TryMapMailboxItemKindForCount(core.ItemKind, out var listType))
                return 0;
            return NewInventoryStore.CountItemsByTemplate(
                connection, transaction, characterId, accountId, listType, core.ItemId, core.ExpireTime);
        }

        private static int LoadEffectiveGoldCarryLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT gold_carry_limit FROM character_gold_limits WHERE character_id=@cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            var raw = command.ExecuteScalar();
            if (raw == null || raw == DBNull.Value)
                return int.MaxValue;
            var saved = Convert.ToInt32(raw);
            return saved > 0 ? saved : int.MaxValue;
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

        private static int ScalarInt(SqliteConnection c, SqliteTransaction t, string sql, params (string, object)[] values)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = sql;
            foreach (var value in values)
                command.Parameters.AddWithValue(value.Item1, value.Item2 ?? DBNull.Value);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int Execute(SqliteConnection c, SqliteTransaction t, string sql, params (string, object)[] values)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = sql;
            foreach (var value in values)
                command.Parameters.AddWithValue(value.Item1, value.Item2 ?? DBNull.Value);
            return command.ExecuteNonQuery();
        }
    }
}
