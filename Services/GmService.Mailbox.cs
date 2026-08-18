using System;
using System.Linq;
using DfoGmTool.ServerCore.Game.Mailbox;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        public object GetCharacterMailbox(int characterId, PvfIndexService pvfIndex)
        {
            var inbox = _mailboxRepository.LoadGmInbox(characterId);
            if (!inbox.Success)
                return Error(inbox.Error ?? "读取邮箱失败");

            var messages = inbox.Messages.Select(message => new
            {
                messageId = message.MessageId,
                title = message.Title,
                body = message.BodyPreview,
                gold = message.Gold,
                receivedGold = message.ReceivedGold,
                mailType = message.MailType,
                read = message.Read,
                saved = message.Saved,
                createdAt = message.CreatedAt,
                expireAt = message.ExpireAt,
                unlimitedFlag = message.UnlimitedFlag,
                shared = message.InboxRecipientCount > 1,
                attachments = message.Attachments.Select(attachment => new
                {
                    attachmentId = attachment.AttachmentId,
                    ordinal = attachment.Ordinal,
                    itemTemplateId = attachment.ItemTemplateId,
                    name = ResolveMailboxItemName(pvfIndex, attachment.ItemTemplateId),
                    count = attachment.ItemCount,
                    claimedFlag = attachment.ClaimedFlag,
                    canDelete = attachment.CanDelete,
                }).ToArray(),
            }).ToArray();

            return new
            {
                success = true,
                characterId = inbox.CharacterId,
                folder = 0,
                messageCount = inbox.MessageCount,
                unclaimedAttachmentCount = inbox.UnclaimedAttachmentCount,
                claimedAttachmentCount = inbox.ClaimedAttachmentCount,
                unclaimedGold = inbox.UnclaimedGold,
                messages,
                notification = "mailbox_reopen_required",
            };
        }

        public object DeleteCharacterMail(int characterId, long messageId)
        {
            var result = _mailboxRepository.GmDeleteMail(characterId, messageId);
            return MapMailboxMutation(result);
        }

        public object DeleteCharacterMailAttachment(int characterId, long attachmentId)
        {
            var result = _mailboxRepository.DiscardUnclaimedAttachment(characterId, attachmentId);
            return MapMailboxMutation(result);
        }

        public object ClearCharacterMailbox(int characterId)
        {
            var result = _mailboxRepository.GmClearInbox(characterId);
            return MapMailboxMutation(result);
        }

        private static object MapMailboxMutation(GmMailboxMutationResult result)
        {
            if (result == null || !result.Success)
                return Error(result?.Error ?? "邮箱操作失败");
            return new
            {
                success = true,
                characterId = result.CharacterId,
                messageId = result.MessageId,
                attachmentId = result.AttachmentId,
                recipientCount = result.RecipientCount,
                messageCount = result.MessageCount,
                attachmentCount = result.AttachmentCount,
                auditCount = result.AuditCount,
                mailRemoved = result.MailRemoved,
                sharedMailRetained = result.SharedMailRetained,
                notification = result.Notification,
            };
        }

        private static string ResolveMailboxItemName(PvfIndexService pvfIndex, int templateId)
        {
            var name = pvfIndex?.ResolveItemName(templateId);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}
