using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    public sealed partial class MailboxRepository
    {
        public GmMailboxInbox LoadGmInbox(int characterId)
        {
            if (characterId <= 0)
                return GmMailboxInbox.Fail("角色编号无效");

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            if (!CharacterIsActive(connection, transaction, characterId))
                return GmMailboxInbox.Fail("角色不存在或已删除: " + characterId);

            var messages = new List<GmMailboxMessage>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT
    m.message_id,
    m.title,
    m.body,
    m.gold,
    r.received_gold_flag,
    m.mail_type,
    r.read_flag,
    r.saved_flag,
    m.created_at,
    m.expire_at,
    m.unlimited_flag,
    (SELECT COUNT(*) FROM mailbox_recipients other
     WHERE other.message_id = m.message_id AND other.folder = 0 AND other.deleted_flag = 0)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
ORDER BY datetime(m.created_at) DESC, m.message_id DESC;";
                command.Parameters.AddWithValue("@cid", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var body = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var gold = reader.GetInt32(3);
                    var receivedGoldFlag = reader.GetInt32(4);
                    messages.Add(new GmMailboxMessage
                    {
                        MessageId = reader.GetInt64(0),
                        Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        Body = body,
                        BodyPreview = PreviewBody(body),
                        Gold = gold,
                        ReceivedGold = receivedGoldFlag == 1 ? gold : 0,
                        MailType = reader.GetInt32(5),
                        Read = reader.GetInt32(6) != 0,
                        Saved = reader.GetInt32(7) != 0,
                        CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        ExpireAt = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        UnlimitedFlag = reader.GetInt32(10),
                        InboxRecipientCount = reader.GetInt32(11),
                    });
                }
            }

            var unclaimedAttachments = 0;
            var claimedAttachments = 0;
            var unclaimedGold = 0;
            foreach (var message in messages)
            {
                message.Attachments = LoadGmAttachments(
                    connection,
                    transaction,
                    message.MessageId,
                    exclusiveInbox: message.InboxRecipientCount == 1);
                foreach (var attachment in message.Attachments)
                {
                    if (attachment.ClaimedFlag == 0)
                        unclaimedAttachments++;
                    else if (attachment.ClaimedFlag == 1)
                        claimedAttachments++;
                }
                if (message.Gold > 0 && message.ReceivedGold == 0)
                    unclaimedGold += message.Gold;
            }

            transaction.Commit();
            return new GmMailboxInbox
            {
                Success = true,
                CharacterId = characterId,
                MessageCount = messages.Count,
                UnclaimedAttachmentCount = unclaimedAttachments,
                ClaimedAttachmentCount = claimedAttachments,
                UnclaimedGold = unclaimedGold,
                Messages = messages,
            };
        }

        public GmMailboxMutationResult GmDeleteMail(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return GmMailboxMutationResult.Fail("角色或邮件编号无效");

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!CharacterIsActive(connection, transaction, characterId))
                return GmMailboxMutationResult.Fail("角色不存在或已删除: " + characterId);

            var result = GmDeleteMailInTransaction(connection, transaction, characterId, messageId);
            if (!result.Success)
                return result;
            transaction.Commit();
            result.Notification = "mailbox_reopen_required";
            return result;
        }

        public GmMailboxMutationResult DiscardUnclaimedAttachment(int characterId, long attachmentId)
        {
            if (characterId <= 0 || attachmentId <= 0)
                return GmMailboxMutationResult.Fail("角色或附件编号无效");

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!CharacterIsActive(connection, transaction, characterId))
                return GmMailboxMutationResult.Fail("角色不存在或已删除: " + characterId);

            long messageId;
            int claimedFlag;
            int inboxRecipients;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT a.message_id, a.claimed_flag,
       (SELECT COUNT(*) FROM mailbox_recipients other
        WHERE other.message_id = a.message_id AND other.folder = 0 AND other.deleted_flag = 0)
FROM mailbox_attachments a
JOIN mailbox_recipients r ON r.message_id = a.message_id
WHERE a.attachment_id = @aid
  AND r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
LIMIT 1;";
                command.Parameters.AddWithValue("@aid", attachmentId);
                command.Parameters.AddWithValue("@cid", characterId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return GmMailboxMutationResult.Fail("附件不存在，或不属于当前角色收件箱");
                messageId = reader.GetInt64(0);
                claimedFlag = reader.GetInt32(1);
                inboxRecipients = reader.GetInt32(2);
            }

            if (claimedFlag == 1)
                return GmMailboxMutationResult.Fail("附件已领取，不能删除物品");
            if (claimedFlag == 2)
                return GmMailboxMutationResult.Fail("附件处于领取事务中，请稍后重试或检查是否有未完成领取");
            if (claimedFlag != 0)
                return GmMailboxMutationResult.Fail("附件状态无效，拒绝删除");
            if (inboxRecipients > 1)
                return GmMailboxMutationResult.Fail("共享邮件的附件不能单独删除，以免影响其他收件人；请改为删除当前角色的这封邮件");

            var removed = Execute(
                connection,
                transaction,
                "DELETE FROM mailbox_attachments WHERE attachment_id=@aid AND message_id=@mid AND claimed_flag=0;",
                ("@aid", attachmentId),
                ("@mid", messageId));
            if (removed != 1)
                return GmMailboxMutationResult.Fail("删除附件失败，事务已回滚");

            var remainingAttachments = ScalarInt(
                connection,
                transaction,
                "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=@mid;",
                ("@mid", messageId));
            var goldPending = ScalarInt(
                connection,
                transaction,
                @"
SELECT COUNT(*)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id=@cid AND r.message_id=@mid AND r.folder=0 AND r.deleted_flag=0
  AND m.gold > 0 AND r.received_gold_flag = 0;",
                ("@cid", characterId),
                ("@mid", messageId));

            var mailRemoved = false;
            if (remainingAttachments == 0 && goldPending == 0)
            {
                var emptyMail = GmDeleteMailInTransaction(connection, transaction, characterId, messageId);
                if (!emptyMail.Success)
                    return emptyMail;
                mailRemoved = true;
            }

            transaction.Commit();
            return new GmMailboxMutationResult
            {
                Success = true,
                CharacterId = characterId,
                MessageId = messageId,
                AttachmentId = attachmentId,
                AttachmentCount = 1,
                MessageCount = mailRemoved ? 1 : 0,
                MailRemoved = mailRemoved,
                Notification = "mailbox_reopen_required",
            };
        }

        public GmMailboxMutationResult GmClearInbox(int characterId)
        {
            if (characterId <= 0)
                return GmMailboxMutationResult.Fail("角色编号无效");

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!CharacterIsActive(connection, transaction, characterId))
                return GmMailboxMutationResult.Fail("角色不存在或已删除: " + characterId);

            var messageIds = new List<long>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT message_id
FROM mailbox_recipients
WHERE character_id=@cid AND folder=0 AND deleted_flag=0
ORDER BY message_id;";
                command.Parameters.AddWithValue("@cid", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    messageIds.Add(reader.GetInt64(0));
            }

            var recipients = 0;
            var attachments = 0;
            var audits = 0;
            foreach (var messageId in messageIds)
            {
                var deleted = GmDeleteMailInTransaction(connection, transaction, characterId, messageId);
                if (!deleted.Success)
                    return deleted;
                recipients += deleted.RecipientCount;
                attachments += deleted.AttachmentCount;
                audits += deleted.AuditCount;
            }

            transaction.Commit();
            return new GmMailboxMutationResult
            {
                Success = true,
                CharacterId = characterId,
                RecipientCount = recipients,
                MessageCount = messageIds.Count,
                AttachmentCount = attachments,
                AuditCount = audits,
                Notification = "mailbox_reopen_required",
            };
        }

        private GmMailboxMutationResult GmDeleteMailInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long messageId)
        {
            var inboxExists = ScalarInt(
                connection,
                transaction,
                @"
SELECT COUNT(*)
FROM mailbox_recipients
WHERE character_id=@cid AND message_id=@mid AND folder=0 AND deleted_flag=0;",
                ("@cid", characterId),
                ("@mid", messageId));
            if (inboxExists != 1)
                return GmMailboxMutationResult.Fail("邮件不存在于当前角色收件箱");

            var remainingAfter = ScalarInt(
                connection,
                transaction,
                @"
SELECT COUNT(*)
FROM mailbox_recipients r
INNER JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.message_id=@mid
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND r.character_id <> @cid
  AND (m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'));",
                ("@mid", messageId),
                ("@cid", characterId));

            var attachmentCount = 0;
            var auditCount = 0;
            var messageCount = 0;
            if (remainingAfter == 0)
            {
                attachmentCount = ScalarInt(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=@mid AND claimed_flag IN (0, 2);",
                    ("@mid", messageId));
                auditCount = ScalarInt(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=@mid;",
                    ("@mid", messageId));
                Execute(
                    connection,
                    transaction,
                    @"
DELETE FROM mailbox_system_mail_audit_attachments
WHERE audit_id IN (SELECT audit_id FROM mailbox_system_mail_audit WHERE message_id=@mid);",
                    ("@mid", messageId));
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM mailbox_system_mail_audit WHERE message_id=@mid;",
                    ("@mid", messageId));
            }

            var recipients = Execute(
                connection,
                transaction,
                @"
DELETE FROM mailbox_recipients
WHERE message_id=@mid AND character_id=@cid AND folder=0;",
                ("@mid", messageId),
                ("@cid", characterId));
            if (recipients != 1)
                return GmMailboxMutationResult.Fail("删除收件人失败，事务已回滚");

            if (remainingAfter == 0)
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM mailbox_messages WHERE message_id=@mid;",
                    ("@mid", messageId));
                messageCount = 1;
            }

            return new GmMailboxMutationResult
            {
                Success = true,
                CharacterId = characterId,
                MessageId = messageId,
                RecipientCount = recipients,
                MessageCount = messageCount,
                AttachmentCount = attachmentCount,
                AuditCount = auditCount,
                MailRemoved = true,
                SharedMailRetained = remainingAfter > 0,
            };
        }

        private static List<GmMailboxAttachment> LoadGmAttachments(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId,
            bool exclusiveInbox)
        {
            var result = new List<GmMailboxAttachment>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT attachment_id, ordinal, item_template_id, item_count, claimed_flag
FROM mailbox_attachments
WHERE message_id=@mid
ORDER BY ordinal, attachment_id;";
            command.Parameters.AddWithValue("@mid", messageId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var claimedFlag = reader.GetInt32(4);
                result.Add(new GmMailboxAttachment
                {
                    AttachmentId = reader.GetInt64(0),
                    Ordinal = reader.GetInt32(1),
                    ItemTemplateId = reader.GetInt32(2),
                    ItemCount = reader.GetInt32(3),
                    ClaimedFlag = claimedFlag,
                    CanDelete = exclusiveInbox && claimedFlag == 0,
                });
            }
            return result;
        }

        private static string PreviewBody(string body)
        {
            if (string.IsNullOrEmpty(body))
                return string.Empty;
            var compact = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length <= 80 ? compact : compact.Substring(0, 80);
        }

        public bool TryLoadActiveCharacterMailIdentity(int characterId, out string name, out int level, out string error)
        {
            name = string.Empty;
            level = 0;
            error = string.Empty;
            if (characterId <= 0)
            {
                error = "角色编号无效";
                return false;
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, level FROM characters WHERE character_id=@id AND delete_flag=0 LIMIT 1;";
            command.Parameters.AddWithValue("@id", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                error = "角色不存在或已删除: " + characterId;
                return false;
            }

            name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            level = reader.GetInt32(1);
            return true;
        }
    }
}
