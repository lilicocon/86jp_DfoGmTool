using System;
using DfoGmTool.ServerCore.Game.Inventory;

namespace DfoGmTool.ServerCore.Game.Mailbox
{
    internal static class MailboxSendPolicy
    {
        private const int MinExpirationUnixTime = 1000000000;

        public static MailboxSendError ValidateAttachment(MailboxSendRequest request, ItemCore core)
        {
            if (core == null || core.ItemId <= 0)
                return MailboxSendError.InvalidAttachment;

            if (core.ExpireTime >= MinExpirationUnixTime)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return core.ExpireTime <= now
                    ? MailboxSendError.ExpiredItem
                    : MailboxSendError.LimitedPeriodItem;
            }

            if (core.TradeRestriction != 0)
                return MailboxSendError.NotTradable;

            var metadata = ItemMetadataResolver.Resolve(core.ItemId);
            var attachType = NormalizePvfToken(metadata?.AttachType);
            if (attachType == "trade limit")
            {
                return core.StackTradeCount > 0
                    ? MailboxSendError.None
                    : MailboxSendError.NotTradable;
            }

            return ValidateAttachType(request, attachType, core.SealFlag);
        }

        internal static ItemCore SetRemainingTradeCount(ItemCore core, int remainingCount)
        {
            if (core == null)
                return null;

            var updated = core.Copy();
            updated.StackTradeCount = (byte)Math.Max(0, Math.Min(7, remainingCount));
            return updated;
        }

        internal static ItemCore DecrementTradeCount(ItemCore core)
        {
            if (core == null)
                return null;

            var result = core.Copy();
            result.StackTradeCount = (byte)Math.Max(0, core.StackTradeCount - 1);
            return result;
        }

        internal static bool IsTradeLimitItem(ItemMetadata metadata)
            => metadata != null && NormalizePvfToken(metadata.AttachType) == "trade limit";

        internal static MailboxSendError ValidateAttachType(
            MailboxSendRequest request,
            string attachType,
            int sealFlag)
        {
            attachType = NormalizePvfToken(attachType);
            if (attachType == "free")
                return MailboxSendError.None;

            if (attachType.Contains("account"))
            {
                return request.SenderAccountId == request.ReceiverAccountId
                    ? MailboxSendError.None
                    : MailboxSendError.AccountBound;
            }

            if (attachType == "sealing" || attachType == "seal")
                return sealFlag != 0 ? MailboxSendError.None : MailboxSendError.NotTradable;

            if (attachType.Length == 0
                || attachType == "trade"
                || attachType == "trade delete"
                || attachType == "sealing trade"
                || attachType.Contains("no trade")
                || attachType.Contains("not trade")
                || attachType.Contains("untrade")
                || attachType.Contains("character")
                || attachType == "bind"
                || attachType == "bound")
            {
                return MailboxSendError.NotTradable;
            }

            return MailboxSendError.NotTradable;
        }

        public static MailboxSendError ValidateDeferredPolicies(MailboxSendRequest request)
            => MailboxSendError.None;

        private static string NormalizePvfToken(string value)
            => (value ?? string.Empty)
                .Replace("`", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim()
                .ToLowerInvariant();
    }
}
