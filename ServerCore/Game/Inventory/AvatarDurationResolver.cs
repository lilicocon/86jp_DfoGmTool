using System;
using System.Collections.Generic;
using System.IO;

namespace DfoGmTool.ServerCore.Game.Inventory
{
    public sealed class AvatarDurationOption
    {
        public int DurationDays { get; set; }

        public int CeraPrice { get; set; }
    }

    internal static class AvatarDurationResolver
    {
        private const string Tag = "[avatar type select]";
        private const string EndTag = "[/avatar type select]";
        // Bound like skill cache: grant/search loops must not pin every avatar parse forever.
        private const int MaxCacheEntries = 256;
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, LinkedListNode<CacheEntry>> Cache =
            new Dictionary<int, LinkedListNode<CacheEntry>>();
        private static readonly LinkedList<CacheEntry> CacheOrder = new LinkedList<CacheEntry>();

        private sealed class CacheEntry
        {
            public int ItemTemplateId;
            public IReadOnlyList<AvatarDurationOption> Options;
        }

        internal static void ResetForPvfChange()
        {
            lock (Sync)
            {
                Cache.Clear();
                CacheOrder.Clear();
            }
        }

        internal static IReadOnlyList<AvatarDurationOption> Resolve(int itemTemplateId)
        {
            lock (Sync)
            {
                if (Cache.TryGetValue(itemTemplateId, out var node))
                {
                    CacheOrder.Remove(node);
                    CacheOrder.AddFirst(node);
                    return node.Value.Options;
                }
            }

            IReadOnlyList<AvatarDurationOption> resolved = Array.Empty<AvatarDurationOption>();
            var entry = ItemMetadataResolver.GetEquipmentEntry(itemTemplateId);
            if (entry != null)
            {
                var text = GameWorld.PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath));
                resolved = Parse(text);
            }

            lock (Sync)
            {
                if (Cache.TryGetValue(itemTemplateId, out var existing))
                {
                    existing.Value.Options = resolved;
                    CacheOrder.Remove(existing);
                    CacheOrder.AddFirst(existing);
                    return resolved;
                }

                var cacheEntry = new CacheEntry { ItemTemplateId = itemTemplateId, Options = resolved };
                var newNode = CacheOrder.AddFirst(cacheEntry);
                Cache[itemTemplateId] = newNode;
                while (Cache.Count > MaxCacheEntries)
                {
                    var last = CacheOrder.Last;
                    if (last == null)
                        break;
                    CacheOrder.RemoveLast();
                    Cache.Remove(last.Value.ItemTemplateId);
                }
            }
            return resolved;
        }

        internal static bool ContainsDuration(IReadOnlyList<AvatarDurationOption> options, int days)
        {
            if (options == null)
                return false;
            foreach (var option in options)
            {
                if (option.DurationDays == days)
                    return true;
            }
            return false;
        }

        internal static IReadOnlyList<AvatarDurationOption> Parse(string text)
        {
            var result = new List<AvatarDurationOption>();
            var seenDays = new HashSet<int>();
            if (string.IsNullOrEmpty(text))
                return result;

            var start = text.IndexOf(Tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return result;
            start += Tag.Length;
            var end = text.IndexOf(EndTag, start, StringComparison.OrdinalIgnoreCase);
            var section = end > start ? text.Substring(start, end - start) : text.Substring(start);

            var values = new List<int>();
            foreach (var token in section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out var value))
                    break;
                values.Add(value);
            }

            for (var index = 0; index + 6 < values.Count; index += 7)
            {
                if (!seenDays.Add(values[index]))
                    continue;
                result.Add(new AvatarDurationOption
                {
                    DurationDays = values[index],
                    CeraPrice = values[index + 3],
                });
            }
            return result;
        }
    }
}
