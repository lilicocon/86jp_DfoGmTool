using System;
using System.Collections.Generic;
using System.IO;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.Mailbox;
using DfoGmTool.Services;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.SelfTests
{
    internal static class MailboxGmSelfTest
    {
        private static int _failures;

        internal static int Run()
        {
            Console.WriteLine("=== MAILBOX_GM selftest ===");
            var root = Path.Combine(Path.GetTempPath(), "dfo-gm-mailbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Check("ItemCore.Size is 82", ItemCore.Size == 82);
                TestListUnclaimedAttachments(root);
                TestDiscardUnclaimedAttachment(root);
                TestGmDeleteBeatsPlayerDelete(root);
                TestClearInbox(root);
                TestClaimedAttachmentReject(root);
                TestClaimInProgressReject(root);
                TestSharedMailOnlyRemovesCurrentRecipient(root);
                TestRootDeleteClearsAudit(root);
                TestTransactionRollback(root);
                TestCampaignSetNullOnRootDelete(root);
                TestInactiveRecipientDoesNotKeepRoot(root);
                TestExpiredPeerDoesNotKeepRoot(root);
                TestSentFolderDoesNotKeepRoot(root);
                TestGmDeleteCountsOnlyUnclaimedAttachments(root);
                TestDropOrphanBackupMailboxAudits();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.Error.WriteLine("UNHANDLED: " + ex);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            Console.WriteLine(_failures == 0
                ? "MailboxGmSelfTest OK"
                : $"MailboxGmSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestListUnclaimedAttachments(string root)
        {
            var db = CreateDatabase(root, "list.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "list-mail", 101001, 101002);
            Check("list send succeeds", sent.Success && sent.MessageId > 0);
            Check("attachment ItemCore length is 82",
                Scalar(db, "SELECT length(item_core) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + " LIMIT 1;") == ItemCore.Size);

            var inbox = mailbox.LoadGmInbox(1);
            Check("list inbox succeeds", inbox.Success && inbox.MessageCount == 1);
            Check("list reports unclaimed attachments", inbox.UnclaimedAttachmentCount == 2 && inbox.ClaimedAttachmentCount == 0);
            Check("list message carries both attachments",
                inbox.Messages.Count == 1 && inbox.Messages[0].Attachments.Count == 2);
            Check("unclaimed attachments are deletable for exclusive mail",
                inbox.Messages[0].Attachments[0].CanDelete && inbox.Messages[0].Attachments[1].CanDelete);
        }

        private static void TestDiscardUnclaimedAttachment(string root)
        {
            var db = CreateDatabase(root, "discard.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "discard-mail", 102001, 102002);
            var firstId = Scalar(db, "SELECT MIN(attachment_id) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";");
            var discarded = mailbox.DiscardUnclaimedAttachment(1, firstId);
            Check("discard unclaimed attachment succeeds", discarded.Success && discarded.AttachmentCount == 1);
            Check("discard does not remove mail while another attachment remains", !discarded.MailRemoved);
            Check("one unclaimed attachment remains",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + " AND claimed_flag=0;") == 1);
            Check("discarded item did not enter bag",
                Scalar(db, "SELECT COUNT(*) FROM character_new_items WHERE character_id=1;") == 0);

            var lastId = Scalar(db, "SELECT attachment_id FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";");
            var emptied = mailbox.DiscardUnclaimedAttachment(1, lastId);
            Check("discard last attachment removes empty mail", emptied.Success && emptied.MailRemoved);
            Check("empty mail left current inbox", mailbox.LoadGmInbox(1).MessageCount == 0);
        }

        private static void TestGmDeleteBeatsPlayerDelete(string root)
        {
            var db = CreateDatabase(root, "gm-delete.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "stuck-mail", 103001);
            var player = mailbox.DeleteMail(1, sent.MessageId);
            Check("player DeleteMail fails while unclaimed attachments exist",
                !player.Success && player.Error == MailboxSendError.InvalidRequest);
            Check("player delete left the mail in inbox",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_recipients WHERE character_id=1 AND message_id=" + sent.MessageId + " AND deleted_flag=0;") == 1);

            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("GM delete succeeds with unclaimed attachments", gm.Success && gm.MessageCount == 1);
            Check("GM delete dropped attachments without granting",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM character_new_items WHERE character_id=1;") == 0);
            Check("GM delete removed inbox row", mailbox.LoadGmInbox(1).MessageCount == 0);
        }

        private static void TestClearInbox(string root)
        {
            var db = CreateDatabase(root, "clear.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            SendStuckMail(mailbox, 1, "clear-a", 104001);
            SendStuckMail(mailbox, 1, "clear-b", 104002);
            var cleared = mailbox.GmClearInbox(1);
            Check("clear inbox succeeds", cleared.Success && cleared.MessageCount == 2 && cleared.AttachmentCount == 2);
            Check("clear emptied current inbox", mailbox.LoadGmInbox(1).MessageCount == 0);
            Check("clear dropped unclaimed attachments",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments;") == 0
                && Scalar(db, "SELECT COUNT(*) FROM character_new_items WHERE character_id=1;") == 0);
        }

        private static void TestClaimedAttachmentReject(string root)
        {
            var db = CreateDatabase(root, "claimed.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "claimed-mail", 105001);
            var attachmentId = Scalar(db, "SELECT attachment_id FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";");
            ExecSql(db, "UPDATE mailbox_attachments SET claimed_flag=1 WHERE attachment_id=" + attachmentId + ";");
            var discarded = mailbox.DiscardUnclaimedAttachment(1, attachmentId);
            Check("claimed attachment delete is rejected", !discarded.Success && (discarded.Error ?? string.Empty).Contains("已领取"));
            Check("claimed attachment row remains",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE attachment_id=" + attachmentId + " AND claimed_flag=1;") == 1);

            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("GM can still delete mail after attachment was claimed", gm.Success);
        }

        private static void TestClaimInProgressReject(string root)
        {
            var db = CreateDatabase(root, "claiming.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "claiming-mail", 106001);
            var attachmentId = Scalar(db, "SELECT attachment_id FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";");
            ExecSql(db, "UPDATE mailbox_attachments SET claimed_flag=2 WHERE attachment_id=" + attachmentId + ";");
            var discarded = mailbox.DiscardUnclaimedAttachment(1, attachmentId);
            Check("claim-in-progress attachment delete is rejected",
                !discarded.Success && (discarded.Error ?? string.Empty).Contains("领取事务"));
            Check("claim-in-progress row remains",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE attachment_id=" + attachmentId + " AND claimed_flag=2;") == 1);
        }

        private static void TestSharedMailOnlyRemovesCurrentRecipient(string root)
        {
            var db = CreateDatabase(root, "shared.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "shared-mail", 107001);
            ExecSql(db, "INSERT INTO mailbox_recipients(message_id, character_id, folder, deleted_flag) VALUES(" + sent.MessageId + ",2,0,0);");

            var inbox1 = mailbox.LoadGmInbox(1);
            Check("shared mail is marked not exclusively deletable",
                inbox1.Success && inbox1.Messages.Count == 1 && !inbox1.Messages[0].Attachments[0].CanDelete);

            var discard = mailbox.DiscardUnclaimedAttachment(1, inbox1.Messages[0].Attachments[0].AttachmentId);
            Check("shared mail refuses single attachment delete", !discard.Success);

            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("shared GM delete only removes current recipient", gm.Success && gm.SharedMailRetained && gm.MessageCount == 0);
            Check("current character no longer sees shared mail", mailbox.LoadGmInbox(1).MessageCount == 0);
            Check("other recipient still holds the mail", mailbox.LoadGmInbox(2).MessageCount == 1);
            Check("shared root message and attachments remain",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=" + sent.MessageId + ";") == 1
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";") == 1);
        }

        private static void TestRootDeleteClearsAudit(string root)
        {
            var db = CreateDatabase(root, "audit.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "audit-mail", 108001);
            Check("system mail audit exists before GM delete",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=" + sent.MessageId + ";") == 1
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_system_mail_audit_attachments;") == 1);

            Check("GM delete exclusive mail", mailbox.GmDeleteMail(1, sent.MessageId).Success);
            Check("root delete left no orphan audit rows",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_system_mail_audit_attachments;") == 0);
        }

        private static void TestTransactionRollback(string root)
        {
            var db = CreateDatabase(root, "rollback.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "rollback-mail", 109001, 109002);
            ExecSql(db, @"
CREATE TRIGGER mailbox_attachments_force_abort
AFTER DELETE ON mailbox_attachments
BEGIN
  SELECT RAISE(ABORT, 'forced rollback');
END;");
            var threw = false;
            try
            {
                mailbox.DiscardUnclaimedAttachment(1, Scalar(db, "SELECT MIN(attachment_id) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";"));
            }
            catch (SqliteException)
            {
                threw = true;
            }
            Check("forced abort surfaces as failure", threw);
            Check("failed discard rolled back both attachments",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";") == 2);
            Check("failed discard left the mail in inbox",
                mailbox.LoadGmInbox(1).MessageCount == 1);
        }

        private static void TestCampaignSetNullOnRootDelete(string root)
        {
            var db = CreateDatabase(root, "campaign.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "campaign-mail", 110001);
            ExecSql(db, "INSERT INTO mailbox_campaigns(campaign_id, payload_hash, status) VALUES('gm-selftest','hash',0);");
            ExecSql(db, "INSERT INTO mailbox_campaign_deliveries(campaign_id, character_id, message_id) VALUES('gm-selftest',1," + sent.MessageId + ");");
            Check("GM delete exclusive campaign mail", mailbox.GmDeleteMail(1, sent.MessageId).Success);
            Check("campaign row is kept", Scalar(db, "SELECT COUNT(*) FROM mailbox_campaigns WHERE campaign_id='gm-selftest';") == 1);
            Check("campaign delivery message_id is SET NULL",
                Scalar(db, "SELECT CASE WHEN message_id IS NULL THEN 1 ELSE 0 END FROM mailbox_campaign_deliveries WHERE campaign_id='gm-selftest';") == 1);
        }

        private static void TestInactiveRecipientDoesNotKeepRoot(string root)
        {
            var db = CreateDatabase(root, "inactive-peer.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "inactive-peer-mail", 111001);
            ExecSql(db, "INSERT INTO mailbox_recipients(message_id, character_id, folder, deleted_flag) VALUES(" + sent.MessageId + ",2,0,0);");
            ExecSql(db, "UPDATE mailbox_recipients SET deleted_flag=2 WHERE character_id=2 AND message_id=" + sent.MessageId + ";");

            var inbox = mailbox.LoadGmInbox(1);
            Check("expired peer is not treated as an active shared holder",
                inbox.Success && inbox.Messages.Count == 1 && inbox.Messages[0].Attachments[0].CanDelete);

            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("GM delete last active inbox removes root after peers expired",
                gm.Success && gm.MessageCount == 1 && !gm.SharedMailRetained);
            Check("expired-peer leftover attachments and audit are gone",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=" + sent.MessageId + ";") == 0);
        }

        private static void TestExpiredPeerDoesNotKeepRoot(string root)
        {
            var db = CreateDatabase(root, "expired-peer.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "expired-peer-mail", 114001);
            ExecSql(db, "INSERT INTO mailbox_recipients(message_id, character_id, folder, deleted_flag) VALUES(" + sent.MessageId + ",2,0,0);");
            ExecSql(db, "UPDATE mailbox_messages SET unlimited_flag=0, expire_at=datetime('now','-1 day') WHERE message_id=" + sent.MessageId + ";");

            var inbox = mailbox.LoadGmInbox(1);
            Check("expired peer still counts for exclusive attachment delete",
                inbox.Success && inbox.Messages.Count == 1 && inbox.Messages[0].InboxRecipientCount == 2
                && !inbox.Messages[0].Attachments[0].CanDelete);

            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("expired peer recipient does not retain shared root",
                gm.Success && gm.MessageCount == 1 && !gm.SharedMailRetained);
            Check("expired peer leftover root is gone",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";") == 0);
        }

        private static void TestSentFolderDoesNotKeepRoot(string root)
        {
            var db = CreateDatabase(root, "sent-folder.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "sent-folder-mail", 113001);
            ExecSql(db, "INSERT INTO mailbox_recipients(message_id, character_id, folder, deleted_flag) VALUES(" + sent.MessageId + ",1,1,0);");
            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("GM delete inbox is not blocked by the same character sent folder",
                gm.Success && gm.MessageCount == 1 && !gm.SharedMailRetained);
            Check("sent-folder leftover root and attachments are gone",
                Scalar(db, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=" + sent.MessageId + ";") == 0
                && Scalar(db, "SELECT COUNT(*) FROM mailbox_attachments;") == 0);
        }

        private static void TestGmDeleteCountsOnlyUnclaimedAttachments(string root)
        {
            var db = CreateDatabase(root, "claimed-count.db");
            var mailbox = OpenMailbox(db);
            using (var connection = Open(db))
                SeedCharacters(connection);

            var sent = SendStuckMail(mailbox, 1, "claimed-count-mail", 112001, 112002);
            var claimedId = Scalar(db, "SELECT MIN(attachment_id) FROM mailbox_attachments WHERE message_id=" + sent.MessageId + ";");
            ExecSql(db, "UPDATE mailbox_attachments SET claimed_flag=1 WHERE attachment_id=" + claimedId + ";");
            var gm = mailbox.GmDeleteMail(1, sent.MessageId);
            Check("GM delete attachment count excludes already claimed items",
                gm.Success && gm.AttachmentCount == 1);
        }

        private static void TestDropOrphanBackupMailboxAudits()
        {
            var messages = new AccountBackupTableDump
            {
                Name = "mailbox_messages",
                Columns = new List<string> { "message_id" },
                Rows = new List<List<AccountBackupValue>>
                {
                    new List<AccountBackupValue> { new AccountBackupValue { Type = "integer", Integer = 10 } },
                },
            };
            var audits = new AccountBackupTableDump
            {
                Name = "mailbox_system_mail_audit",
                Columns = new List<string> { "audit_id", "message_id" },
                Rows = new List<List<AccountBackupValue>>
                {
                    new List<AccountBackupValue>
                    {
                        new AccountBackupValue { Type = "integer", Integer = 1 },
                        new AccountBackupValue { Type = "integer", Integer = 10 },
                    },
                    new List<AccountBackupValue>
                    {
                        new AccountBackupValue { Type = "integer", Integer = 2 },
                        new AccountBackupValue { Type = "integer", Integer = 99 },
                    },
                },
            };
            var attachments = new AccountBackupTableDump
            {
                Name = "mailbox_system_mail_audit_attachments",
                Columns = new List<string> { "audit_attachment_id", "audit_id" },
                Rows = new List<List<AccountBackupValue>>
                {
                    new List<AccountBackupValue>
                    {
                        new AccountBackupValue { Type = "integer", Integer = 1 },
                        new AccountBackupValue { Type = "integer", Integer = 1 },
                    },
                    new List<AccountBackupValue>
                    {
                        new AccountBackupValue { Type = "integer", Integer = 2 },
                        new AccountBackupValue { Type = "integer", Integer = 2 },
                    },
                },
            };
            var tableMap = new Dictionary<string, AccountBackupTableDump>(StringComparer.OrdinalIgnoreCase)
            {
                ["mailbox_messages"] = messages,
                ["mailbox_system_mail_audit"] = audits,
                ["mailbox_system_mail_audit_attachments"] = attachments,
            };

            var dropped = GmService.DropOrphanBackupMailboxAudits(tableMap);
            Check("orphan mailbox audit rows are dropped from backup restore", dropped == 1 && audits.Rows.Count == 1);
            Check("orphan mailbox audit attachments are dropped with the audit",
                attachments.Rows.Count == 1 && attachments.Rows[0][1].ToInt64() == 1);
        }

        private static MailboxSendResult SendStuckMail(MailboxRepository mailbox, int characterId, string text, params int[] itemIds)
        {
            var attachments = new MailboxSendAttachmentRequest[itemIds.Length];
            for (var i = 0; i < itemIds.Length; i++)
            {
                attachments[i] = new MailboxSendAttachmentRequest
                {
                    ItemId = itemIds[i],
                    ItemCount = 1,
                };
            }

            return mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = 1999999999,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = 1,
                ReceiverName = characterId == 1 ? "mailbox-role" : "mailbox-role-2",
                Text = text,
                MailType = 1,
                Unlimited = true,
                AuditActor = "DfoGmTool",
                AuditReason = "GM 发放",
                IdempotencyKey = "selftest-" + text,
                Attachments = attachments,
            });
        }

        private static MailboxRepository OpenMailbox(string db)
        {
            var schema = Path.Combine(Directory.GetCurrentDirectory(), "ServerCore", "Sqlite", "item_schema.sql");
            return new MailboxRepository(db, schema);
        }

        private static string CreateDatabase(string root, string name)
        {
            var db = Path.Combine(root, name);
            var schema = Path.Combine(Directory.GetCurrentDirectory(), "ServerCore", "Sqlite", "item_schema.sql");
            _ = new NewInventoryStore(db, schema);
            return db;
        }

        private static SqliteConnection Open(string db)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
            connection.Open();
            return connection;
        }

        private static void SeedCharacters(SqliteConnection connection)
        {
            Exec(connection, "UPDATE accounts SET m_id='mailbox-test',password_hash='' WHERE account_id=1;");
            Exec(connection, "INSERT INTO characters(character_id,account_id,name) VALUES(1,1,'mailbox-role');");
            Exec(connection, "INSERT INTO characters(character_id,account_id,name) VALUES(2,1,'mailbox-role-2');");
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void ExecSql(string db, string sql)
        {
            using var connection = Open(db);
            Exec(connection, sql);
        }

        private static int Scalar(string db, string sql)
        {
            using var connection = Open(db);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void Check(string name, bool condition)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition)
                _failures++;
        }
    }
}
