using System;
using System.Collections.Generic;
using System.Globalization;
using DfoGmTool.ServerCore.GameWorld;

namespace DfoGmTool.ServerCore.Game.Premium
{
    public sealed class PremiumCatalog
    {
        private static PremiumCatalog _cached;
        private readonly Dictionary<int, PremiumEntry> _byItemCode;

        private PremiumCatalog(Dictionary<int, PremiumEntry> byItemCode)
        {
            _byItemCode = byItemCode;
        }

        /// <summary>
        /// When set (by PvfIndexService), Load prefers the disk index so GiveItem
        /// never opens etc/premiumlist_new.etc / Script.pvf.
        /// </summary>
        public static Func<PremiumCatalog> DiskCatalogLoader { get; set; }

        public static PremiumCatalog Load()
        {
            if (_cached != null)
                return _cached;

            var disk = DiskCatalogLoader;
            if (disk != null)
            {
                try
                {
                    return _cached = disk() ?? FromEntries(null);
                }
                catch (Exception ex)
                {
                    DfoGmTool.ServerCore.FileLogger.Log("[PremiumCatalog] 磁盘索引读取失败: " + ex.Message);
                    return _cached = FromEntries(null);
                }
            }

            return _cached = Parse(PvfArchiveAccessor.ReadText("etc/premiumlist_new.etc"));
        }

        public static PremiumCatalog FromEntries(IEnumerable<PremiumEntry> entries)
        {
            var map = new Dictionary<int, PremiumEntry>();
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry == null || entry.ItemCode <= 0 || entry.PremiumType <= 0 || entry.DurationDays <= 0)
                        continue;
                    map[entry.ItemCode] = entry;
                }
            }
            return new PremiumCatalog(map);
        }

        internal static void Reset()
        {
            _cached = null;
            DiskCatalogLoader = null;
        }

        /// <summary>Drop in-memory cache only; keep DiskCatalogLoader wiring.</summary>
        internal static void ResetCacheOnly()
        {
            _cached = null;
        }

        public IReadOnlyCollection<PremiumEntry> Entries => _byItemCode.Values;

        public bool TryGetValue(int itemCode, out int premiumType, out int durationDays)
        {
            premiumType = 0;
            durationDays = 0;
            if (!_byItemCode.TryGetValue(itemCode, out var entry))
                return false;

            premiumType = entry.PremiumType;
            durationDays = entry.DurationDays;
            return true;
        }

        internal static PremiumCatalog Parse(string text)
        {
            var tokens = Tokenize(text ?? string.Empty);
            var map = new Dictionary<int, PremiumEntry>();
            var premiumType = 0;
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "[type]" && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
                {
                    premiumType = type;
                    continue;
                }

                if (premiumType <= 0)
                    continue;

                if (tokens[i] != "[item]" || i + 4 >= tokens.Count)
                    continue;

                if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemCode)
                    || tokens[i + 2] != "[term]"
                    || !int.TryParse(tokens[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                    continue;

                map[itemCode] = new PremiumEntry(itemCode, premiumType, days);
            }

            return new PremiumCatalog(map);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                var end = i + 1;
                if (text[i] == '[')
                {
                    end = text.IndexOf(']', i + 1) + 1;
                }
                else if (text[i] == '`')
                {
                    end = text.IndexOf('`', i + 1) + 1;
                    if (end <= 0)
                        end = text.Length;
                    i = end;
                    continue;
                }
                else
                {
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '[')
                        end++;
                }

                if (end <= i)
                    break;

                tokens.Add(text.Substring(i, end - i));
                i = end;
            }

            return tokens;
        }
    }

    public sealed class PremiumEntry
    {
        public PremiumEntry(int itemCode, int premiumType, int durationDays)
        {
            ItemCode = itemCode;
            PremiumType = premiumType;
            DurationDays = durationDays;
        }

        public int ItemCode { get; }

        public int PremiumType { get; }

        public int DurationDays { get; }
    }
}
