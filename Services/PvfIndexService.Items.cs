using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DfoGmTool.ServerCore.Game.Inventory;
using GmPvfLib;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class PvfIndexService
    {
        public sealed class ItemEntry
        {
            public int Id;
            public string Name;
            public string Kind;      // equipment / stackable
            public string TypeTag;   // [weapon]/[coat]/[material]/... 的首个标签(去壳小写)
            public string Segment;   // 堆叠物的背包入格分类(与服务端 GetSlotRange 同语义), 装备为 null
            public string Special;   // 品质细分: legacy(传承)/boss(领主神器)/sealed(魔法封印), 无则 null
            public int Rarity;
            public int MinLevel;
            public int Grade;
            public string UsableJob;
            public int AbsoluteExpirationUnixTime;
            public int UsablePeriodDays;
            public bool DailyDeleteItem;
            public bool HasInvalidExpirationDefinition;
            public bool RequiresManualGrantType;
            public bool RequiresConfiguration;
            public bool SupportsQuality;
            /// <summary>Full archive path (equipment/... or stackable/...) for direct PVF open.</summary>
            public string FilePath;
            // v4 grant fields: enough for TryGrant / grant-options without reopening PVF scripts.
            public string TypeFull;       // raw equipment type / stackable type string
            public string ItemCategory;
            public string AttachType;
            public int StackLimit;
            public int Durability;
            public string ImpossibleJson; // JSON string array
            // v6 avatar grant fields (null/empty for non-avatar).
            public int AbilityCaseIndex = -1;
            public string AvatarSelectJson;
            public string AvatarDurationsJson;
        }

        public readonly struct ItemExpirationDefinition
        {
            internal ItemExpirationDefinition(
                bool isKnown,
                int absoluteExpirationUnixTime,
                int usablePeriodDays,
                bool dailyDeleteItem,
                bool hasInvalidDefinition)
            {
                IsKnown = isKnown;
                AbsoluteExpirationUnixTime = absoluteExpirationUnixTime;
                UsablePeriodDays = usablePeriodDays;
                DailyDeleteItem = dailyDeleteItem;
                HasInvalidDefinition = hasInvalidDefinition;
            }

            public bool IsKnown { get; }

            public int AbsoluteExpirationUnixTime { get; }

            public int UsablePeriodDays { get; }

            public bool DailyDeleteItem { get; }

            public bool HasInvalidDefinition { get; }
        }

        private static readonly Regex ItemCategoryPattern = new Regex(
            @"\[item category\]\s*`?([^`\r\n\[]+)", RegexOptions.Compiled);

        // 品质细分识别(均经实物验证):
        //   [item category] legacy    → 传承(紫, 10104 传承:智慧女神的纱棉长袍)
        //   [item category] boss drop → 领主神器(100300063 凝视者之眸)
        //   [random option]           → 魔法封印(2224104 密制镇魂安曲剑, "(魔法封印)"前缀是客户端运行时加的)
        private static string EquipSpecial(string text)
        {
            var category = ItemCategoryPattern.Match(text);
            if (category.Success)
            {
                var value = category.Groups[1].Value.Trim();
                if (value == "legacy")
                    return "legacy";
                if (value == "boss drop")
                    return "boss";
            }
            if (text.Contains("[random option]"))
                return "sealed";
            return null;
        }

        // 与服务端 ItemMetadataResolver.GetSlotRange 同语义的背包分类
        private static string StackSegment(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return "消耗品";
            var st = stackableType.Replace("`", "").Trim().ToLowerInvariant();
            if (st.StartsWith("[material]"))
                return "材料";
            if (st.StartsWith("[quest]"))
                return "任务品";
            if (st.StartsWith("[material expert job]"))
                return "副职业材料";
            if (st.StartsWith("[avatar emblem]"))
                return "徽章";
            return "消耗品";
        }

        /// <summary>
        /// Loads every item from the disk index. Prefer <see cref="FindItem"/> / Resolve* for runtime paths.
        /// </summary>
        public IReadOnlyList<ItemEntry> AllItems =>
            _diskIndex.IsReady ? _diskIndex.LoadAllItems() : Array.Empty<ItemEntry>();

        public ItemEntry FindItem(Func<ItemEntry, bool> predicate)
        {
            if (!_diskIndex.IsReady || predicate == null)
                return null;
            var hits = _diskIndex.FindItems(predicate, 1);
            return hits.Count > 0 ? hits[0] : null;
        }

        public List<ItemEntry> FindItems(Func<ItemEntry, bool> predicate, int limit)
        {
            if (!_diskIndex.IsReady || predicate == null)
                return new List<ItemEntry>();
            return _diskIndex.FindItems(predicate, limit);
        }

        public ItemEntry GetItem(int itemId)
        {
            return _diskIndex.IsReady ? _diskIndex.GetItem(itemId) : null;
        }

        public string ResolveItemName(int itemId)
        {
            return _diskIndex.GetItemName(itemId);
        }

        public string ResolveItemKind(int itemId)
        {
            return _diskIndex.GetItemKind(itemId);
        }

        // 品级(0-6), 索引未就绪或未知物品返回 -1(前端按 -1 不着色)
        public int ResolveItemRarity(int itemId)
        {
            return _diskIndex.GetItemRarity(itemId);
        }

        public ItemExpirationDefinition ResolveItemExpiration(int itemId)
        {
            return _diskIndex.TryGetItemExpiration(itemId, out var expiration)
                ? expiration
                : default;
        }

        /// <summary>equipment/xxx.equ or stackable/xxx.stk full archive path when known.</summary>
        public string ResolveItemArchivePath(int itemId)
        {
            var path = _diskIndex.GetItemFilePath(itemId);
            return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/').TrimStart('/');
        }

        internal int FindArchiveFileIndex(string relativePath) =>
            _diskIndex.FindArchiveFileIndex(relativePath);

        // 发放界面的分类清单: 装备按部位标签, 堆叠物按背包入格分类(与背包页同款)
        public object GetItemCategories()
        {
            if (!_diskIndex.IsReady)
                return new { ready = false, equipment = new object[0], stackable = new object[0], jobs = GetAllJobOptions() };

            _diskIndex.GetItemCategories(out var equipment, out var stackable);
            return new
            {
                ready = true,
                equipment,
                stackable,
                jobs = GetAllJobOptions(),
            };
        }

        public object SearchItems(string query, string kind, string tag, string segment, string special, int minLevel, int maxLevel, int rarity, int limit, int offset, string expiration, int usableJobFilter = -1)
        {
            if (!_diskIndex.IsReady)
                return new { success = false, error = BuildError != null ? "索引构建失败: " + BuildError : "物品索引还在构建中, 稍等几秒再搜" };

            if (limit <= 0 || limit > 200)
                limit = 100;
            if (offset < 0)
                offset = 0;

            var tagSet = SplitFilterValues(tag);
            var segmentSet = SplitFilterValues(segment);
            List<ItemEntry> filtered;
            int total;
            if (usableJobFilter == -1)
            {
                filtered = _diskIndex.SearchItems(
                    query, kind, tagSet, segmentSet, special, minLevel, maxLevel, rarity, expiration,
                    limit, offset, out total);
            }
            else
            {
                filtered = _diskIndex.SearchItemsStreaming(
                    query, kind, tagSet, segmentSet, special, minLevel, maxLevel, rarity, expiration,
                    entry =>
                    {
                        if (entry.Kind != "equipment")
                            return true;
                        if (usableJobFilter == -2)
                            return IsUnrestrictedUsableJob(entry.UsableJob);
                        if (usableJobFilter >= 0)
                            return AvatarGrantPolicy.IsUsableByJob(entry.UsableJob, usableJobFilter);
                        return true;
                    },
                    limit, offset, out total);
            }

            var page = filtered
                .Select(e => (object)new
                {
                    itemId = e.Id,
                    name = e.Name,
                    kind = e.Kind,
                    tag = e.TypeTag,
                    segment = e.Segment,
                    special = e.Special,
                    rarity = e.Rarity,
                    minLevel = e.MinLevel,
                    grade = e.Grade,
                    usableJob = e.UsableJob,
                    usableJobLabel = UsableJobLabel(e.UsableJob),
                    usableJobLabels = UsableJobLabels(e.UsableJob),
                    requiresManualGrantType = e.RequiresManualGrantType,
                    requiresConfiguration = e.RequiresConfiguration,
                    supportsQuality = e.SupportsQuality,
                    templateExpiration = new
                    {
                        known = true,
                        absoluteExpireTime = e.AbsoluteExpirationUnixTime,
                        usablePeriodDays = e.UsablePeriodDays,
                        dailyDeleteItem = e.DailyDeleteItem,
                        invalid = e.HasInvalidExpirationDefinition,
                    },
                })
                .ToArray();

            return new { success = true, total, offset, count = page.Length, results = page };
        }

        private static HashSet<string> SplitFilterValues(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return new HashSet<string>(
                value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim()),
                StringComparer.Ordinal);
        }

        private static bool MatchesExpirationFilter(ItemEntry entry, string filter, long now)
        {
            var hasAbsoluteExpiration = entry.AbsoluteExpirationUnixTime > 0;
            var hasRelativeExpiration = entry.UsablePeriodDays > 0;
            var hasDailyDeletion = entry.DailyDeleteItem;

            switch (filter)
            {
                case "limited":
                    return hasAbsoluteExpiration || hasRelativeExpiration || hasDailyDeletion;
                case "none":
                    return !entry.HasInvalidExpirationDefinition
                        && !hasAbsoluteExpiration
                        && !hasRelativeExpiration
                        && !hasDailyDeletion;
                case "relative":
                    return hasRelativeExpiration;
                case "absolute":
                    return hasAbsoluteExpiration;
                case "daily":
                    return hasDailyDeletion;
                case "expired":
                    return hasAbsoluteExpiration && entry.AbsoluteExpirationUnixTime <= now;
                default:
                    return true;
            }
        }

        private static bool IsUnrestrictedUsableJob(string usableJob)
        {
            var normalized = NormalizeUsableJob(usableJob);
            return string.IsNullOrEmpty(normalized) || normalized.Contains("[all]", StringComparison.Ordinal);
        }

        private static string NormalizeUsableJob(string usableJob)
        {
            return (usableJob ?? string.Empty)
                .Trim()
                .Trim('`')
                .ToLowerInvariant()
                .Replace("`", string.Empty)
                .Replace("_", " ")
                .Replace("\t", " ")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private static string UsableJobLabel(string usableJob)
        {
            var normalized = NormalizeUsableJob(usableJob);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("[all]", StringComparison.Ordinal))
                return "无限制";

            var labels = new List<string>();
            foreach (Match match in Regex.Matches(normalized, @"\[([^\]]+)\]"))
            {
                var token = match.Groups[1].Value.Trim();
                if (token.Length == 0 || token == "all")
                    continue;
                var label = UsableJobTokenLabel(token);
                if (!labels.Contains(label))
                    labels.Add(label);
            }
            return labels.Count == 0 ? "无限制" : string.Join("、", labels);
        }

        private static string[] UsableJobLabels(string usableJob)
        {
            var normalized = NormalizeUsableJob(usableJob);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("[all]", StringComparison.Ordinal))
                return new[] { UsableJobLabel(usableJob) };

            var labels = new List<string>();
            foreach (Match match in Regex.Matches(normalized, @"\[([^\]]+)\]"))
            {
                var token = match.Groups[1].Value.Trim();
                if (token.Length == 0 || token == "all")
                    continue;
                var label = UsableJobTokenLabel(token);
                if (!labels.Contains(label))
                    labels.Add(label);
            }
            return labels.Count == 0 ? new[] { UsableJobLabel(usableJob) } : labels.ToArray();
        }

        private static string UsableJobTokenLabel(string token)
        {
            switch ((token ?? string.Empty).Replace("_", " ").Trim().ToLowerInvariant())
            {
                case "swordman": return "鬼剑士";
                case "fighter": return "格斗家";
                case "gunner": return "神枪手";
                case "mage": return "魔法师";
                case "priest": return "圣职者";
                case "thief": return "暗夜使者";
                case "knight": return "守护者";
                case "at gunner": return "女神枪手";
                case "at fighter": return "男格斗家";
                case "at mage": return "男魔法师";
                case "at swordman": return "女鬼剑士";
                case "atswordman": return "女鬼剑士";
                case "demonic swordman": return "黑暗武士";
                case "demonicswordman": return "黑暗武士";
                case "creatormage": return "缔造者";
                case "creator mage": return "缔造者";
                default: return token;
            }
        }

        public object Search(string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new { success = false, error = "query 不能为空" };
            if (limit <= 0 || limit > 100)
                limit = 30;

            if (!_diskIndex.IsReady)
                return new { success = false, error = BuildError != null ? "索引构建失败: " + BuildError : "物品索引还在构建中, 稍等几秒再搜" };

            query = query.Trim();
            int numericId;
            var isNumeric = int.TryParse(query, out numericId);

            var list = _diskIndex.SearchItems(
                query, null, null, null, null, 0, 0, -1, null, limit, 0, out _);
            var results = new List<object>();
            foreach (var entry in list)
            {
                if (results.Count >= limit)
                    break;
                if ((isNumeric && entry.Id == numericId) ||
                    (entry.Name != null && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    results.Add(new { itemId = entry.Id, name = entry.Name, kind = entry.Kind });
                }
            }

            return new { query, count = results.Count, results };
        }

        private static readonly Regex TagPattern = new Regex(@"\[([a-z ]+)\]", RegexOptions.Compiled);

        private static string FirstTag(string typeString)
        {
            return ItemMetadataResolver.FirstPvfTypeTag(typeString);
        }

        private static string SerializeImpossible(IReadOnlyList<string> items)
        {
            if (items == null || items.Count == 0)
                return null;
            return JsonSerializer.Serialize(items);
        }

        /// <summary>
        /// Build grant-ready ItemMetadata from disk index without opening Script.pvf.
        /// Returns null when the item is not indexed.
        /// </summary>
        public ItemMetadata TryBuildGrantMetadata(int itemId)
        {
            var entry = GetItem(itemId);
            return entry == null ? null : ToGrantMetadata(entry);
        }

        internal static ItemMetadata ToGrantMetadata(ItemEntry entry)
        {
            if (entry == null)
                return null;

            var isEquipment = string.Equals(entry.Kind, "equipment", StringComparison.OrdinalIgnoreCase);
            var typeFull = entry.TypeFull;
            if (string.IsNullOrWhiteSpace(typeFull) && !string.IsNullOrWhiteSpace(entry.TypeTag))
                typeFull = "[" + entry.TypeTag + "]";

            var relativePath = entry.FilePath;
            if (!string.IsNullOrEmpty(relativePath))
            {
                relativePath = relativePath.Replace('\\', '/').TrimStart('/');
                if (relativePath.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring("equipment/".Length);
                else if (relativePath.StartsWith("stackable/", StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring("stackable/".Length);
            }

            return new ItemMetadata
            {
                ItemKind = isEquipment ? "equipment" : "stackable",
                StackableType = isEquipment ? null : typeFull,
                EquipmentType = isEquipment ? typeFull : null,
                ItemCategory = entry.ItemCategory,
                AttachType = entry.AttachType,
                PvfFilePath = relativePath,
                Grade = entry.Grade,
                MinimumLevel = entry.MinLevel,
                Rarity = entry.Rarity,
                StackLimit = entry.StackLimit > 0 ? entry.StackLimit : (isEquipment ? 1 : 1),
                Durability = (ushort)Math.Max(0, entry.Durability),
                SupportsPetEquipmentQuality = entry.SupportsQuality,
                ImpossibleContents = DeserializeImpossible(entry.ImpossibleJson),
                DailyDeleteItem = entry.DailyDeleteItem,
            };
        }

        private static IReadOnlyList<string> DeserializeImpossible(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<string>();
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                return list != null && list.Count > 0 ? list : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        internal static string SerializeAvatarSelect(IReadOnlyList<AvatarSelectAbilityEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return null;
            var rows = new List<object>(entries.Count);
            foreach (var e in entries)
            {
                if (e == null)
                    continue;
                rows.Add(new
                {
                    v = e.OptionValue,
                    a = e.Ability,
                    o = e.Operator,
                    n = e.Amount,
                    j = e.Job,
                    si = e.SkillIndex,
                    sl = e.SkillLevel,
                });
            }
            return rows.Count == 0 ? null : JsonSerializer.Serialize(rows);
        }

        private static string SerializeAvatarDurations(IReadOnlyList<AvatarDurationOption> options)
        {
            if (options == null || options.Count == 0)
                return null;
            var rows = new List<object>(options.Count);
            foreach (var o in options)
            {
                if (o == null)
                    continue;
                rows.Add(new { d = o.DurationDays, c = o.CeraPrice });
            }
            return rows.Count == 0 ? null : JsonSerializer.Serialize(rows);
        }

        internal static List<AvatarSelectAbilityEntry> DeserializeAvatarSelect(string json)
        {
            var result = new List<AvatarSelectAbilityEntry>();
            if (string.IsNullOrWhiteSpace(json))
                return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    result.Add(new AvatarSelectAbilityEntry
                    {
                        OptionValue = el.TryGetProperty("v", out var v) ? v.GetInt32() : 0,
                        Ability = el.TryGetProperty("a", out var a) ? a.GetString() : null,
                        Operator = el.TryGetProperty("o", out var o) ? o.GetString() : null,
                        Amount = el.TryGetProperty("n", out var n) ? n.GetInt32() : 0,
                        Job = el.TryGetProperty("j", out var j) ? j.GetString() : null,
                        SkillIndex = el.TryGetProperty("si", out var si) ? si.GetInt32() : 0,
                        SkillLevel = el.TryGetProperty("sl", out var sl) ? sl.GetInt32() : 0,
                    });
                }
            }
            catch
            {
                // ignore corrupt index rows
            }
            return result;
        }

        internal static List<AvatarDurationOption> DeserializeAvatarDurations(string json)
        {
            var result = new List<AvatarDurationOption>();
            if (string.IsNullOrWhiteSpace(json))
                return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    result.Add(new AvatarDurationOption
                    {
                        DurationDays = el.TryGetProperty("d", out var d) ? d.GetInt32() : 0,
                        CeraPrice = el.TryGetProperty("c", out var c) ? c.GetInt32() : 0,
                    });
                }
            }
            catch
            {
                // ignore
            }
            return result;
        }

        private static ItemExpirationDefinition ResolveEquipmentExpiration(EquipmentFile equipment)
        {
            var typeTag = FirstTag(equipment?.EquipmentType);
            if (string.Equals(typeTag, "name tag", StringComparison.OrdinalIgnoreCase))
                return new ItemExpirationDefinition(true, 0, 30, false, false);

            var rawExpiration = equipment.GetStringValue("expiration date");
            if (string.IsNullOrWhiteSpace(rawExpiration) || rawExpiration.Trim() == "0")
                return new ItemExpirationDefinition(true, 0, 0, false, false);

            return ItemGrantExpirationResolver.TryParsePvfExpirationUnixTime(
                rawExpiration,
                -1,
                out var absoluteExpiration)
                ? new ItemExpirationDefinition(true, absoluteExpiration, 0, false, false)
                : new ItemExpirationDefinition(true, 0, 0, false, true);
        }

        private static ItemExpirationDefinition ResolveStackableExpiration(StackableItemFile stackable)
        {
            if (!StackableExpirationPolicyResolver.TryResolve(stackable, out var policy))
                return new ItemExpirationDefinition(true, 0, 0, false, true);

            return new ItemExpirationDefinition(
                true,
                policy.AbsoluteExpirationUnixTime,
                policy.UsablePeriodDays,
                policy.DailyDeleteItem,
                false);
        }

        private int BuildKindToDisk(
            PvfArchive archive,
            SqliteConnection conn,
            SqliteTransaction tx,
            string lstPath,
            string kind)
        {
            if (lstPath == null)
                return 0;

            var lstText = archive.GetFileContent(lstPath);
            if (string.IsNullOrEmpty(lstText))
                return 0;

            var rootFolder = lstPath.Contains("/") ? lstPath.Substring(0, lstPath.LastIndexOf('/')) : string.Empty;
            var entries = new List<KeyValuePair<int, string>>();
            foreach (Match match in LstPattern.Matches(lstText))
            {
                int id;
                if (int.TryParse(match.Groups[1].Value, out id))
                    entries.Add(new KeyValuePair<int, string>(id, match.Groups[2].Value));
            }

            var results = new ItemEntry[entries.Count];
            Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = IndexBuildParallelism }, i =>
            {
                var relative = entries[i].Value.Replace('\\', '/');
                var fullPath = string.IsNullOrEmpty(rootFolder) ? relative : rootFolder + "/" + relative;
                try
                {
                    var text = archive.GetFileContent(fullPath);
                    // 串行构建时周期性清 chunk, 限制解压缓存峰值。
                    if (IndexBuildParallelism == 1 && (i & 0x7F) == 0x7F)
                        archive.ClearChunkCache();
                    if (string.IsNullOrEmpty(text))
                        return;

                    if (kind == "equipment")
                    {
                        var model = EquipmentFile.Parse(text);
                        if (string.IsNullOrEmpty(model.Name))
                            return;
                        var expiration = ResolveEquipmentExpiration(model);
                        var eqType = ItemMetadataResolver.NormalizeEquipmentTypePublic(model.EquipmentType);
                        var metadata = new ItemMetadata
                        {
                            ItemKind = "equipment",
                            EquipmentType = eqType,
                            ItemCategory = model.ItemCategory,
                            MinimumLevel = model.MinimumLevel,
                            Rarity = model.Rarity,
                            SupportsPetEquipmentQuality = ItemMetadataResolver.HasPetEquipmentQuality(model),
                            ImpossibleContents = model.ImpossibleContentItems,
                            PvfFilePath = fullPath,
                        };
                        var isAvatar = ItemMetadataResolver.IsAvatarMetadata(metadata);
                        var isPetCreature = ItemMetadataResolver.IsPetCreatureMetadata(metadata);
                        var isPetArtifact = ItemMetadataResolver.IsPetArtifactMetadata(metadata);
                        var capability = EquipmentGrantPolicy.Describe(metadata);
                        var isCoatAvatar = string.Equals(
                            ItemMetadataResolver.ResolvePvfTypeTag(metadata),
                            "coat avatar",
                            StringComparison.OrdinalIgnoreCase);
                        var hasAvatarOption = model.Grade > 0
                            && ((isCoatAvatar && model.AbilityCaseIndex >= 0)
                                || (model.AvatarSelectAbilities != null && model.AvatarSelectAbilities.Count > 1));
                        var avatarDurations = isAvatar
                            ? AvatarDurationResolver.Parse(text)
                            : Array.Empty<AvatarDurationOption>();
                        var hasAvatarDuration = avatarDurations.Count > 0;
                        var requiresManual = ItemMetadataResolver.RequiresManualGrantType(metadata);
                        var supportsQuality = isPetArtifact && metadata.SupportsPetEquipmentQuality;
                        var configurableExpiration = expiration.AbsoluteExpirationUnixTime > 0
                            || expiration.UsablePeriodDays > 0;
                        var hasDurability = model.Durability > 0
                            && ItemMetadataResolver.HasDurabilityByTypePublic(eqType);
                        results[i] = new ItemEntry
                        {
                            Id = entries[i].Key,
                            Name = model.Name,
                            Kind = kind,
                            TypeTag = FirstTag(model.EquipmentType),
                            Special = EquipSpecial(text),
                            Rarity = model.Rarity,
                            MinLevel = model.MinimumLevel,
                            Grade = model.Grade,
                            UsableJob = model.UsableJob,
                            AbsoluteExpirationUnixTime = expiration.AbsoluteExpirationUnixTime,
                            UsablePeriodDays = expiration.UsablePeriodDays,
                            DailyDeleteItem = expiration.DailyDeleteItem,
                            HasInvalidExpirationDefinition = expiration.HasInvalidDefinition,
                            RequiresManualGrantType = requiresManual,
                            SupportsQuality = supportsQuality,
                            RequiresConfiguration = !isPetCreature
                                && (requiresManual
                                    || (isAvatar && (hasAvatarOption || hasAvatarDuration || configurableExpiration))
                                    || (isPetArtifact && supportsQuality)
                                    || (!isAvatar && !isPetArtifact
                                        && (configurableExpiration || capability.CanUpgrade || capability.CanAmplify || capability.CanForge))),
                            FilePath = fullPath,
                            TypeFull = eqType,
                            ItemCategory = model.ItemCategory,
                            AttachType = model.AttachType,
                            StackLimit = 1,
                            Durability = hasDurability ? model.Durability : 0,
                            ImpossibleJson = SerializeImpossible(model.ImpossibleContentItems),
                            AbilityCaseIndex = isAvatar ? model.AbilityCaseIndex : -1,
                            AvatarSelectJson = isAvatar ? SerializeAvatarSelect(model.AvatarSelectAbilities) : null,
                            AvatarDurationsJson = isAvatar ? SerializeAvatarDurations(avatarDurations) : null,
                        };
                    }
                    else
                    {
                        var model = StackableItemFile.Parse(text);
                        if (string.IsNullOrEmpty(model.Name))
                            return;
                        var expiration = ResolveStackableExpiration(model);
                        var requiresManual = ItemMetadataResolver.RequiresManualGrantType(new ItemMetadata
                        {
                            ItemKind = "stackable",
                            StackableType = model.StackableType,
                        });
                        var stackLimit = model.StackLimit > 0 ? model.StackLimit : 1;
                        results[i] = new ItemEntry
                        {
                            Id = entries[i].Key,
                            Name = model.Name,
                            Kind = kind,
                            TypeTag = FirstTag(model.StackableType),
                            Segment = StackSegment(model.StackableType),
                            Rarity = model.Rarity,
                            MinLevel = model.MinimumLevel,
                            Grade = model.Grade,
                            UsableJob = model.UsableJob,
                            AbsoluteExpirationUnixTime = expiration.AbsoluteExpirationUnixTime,
                            UsablePeriodDays = expiration.UsablePeriodDays,
                            DailyDeleteItem = expiration.DailyDeleteItem,
                            HasInvalidExpirationDefinition = expiration.HasInvalidDefinition,
                            RequiresManualGrantType = requiresManual,
                            RequiresConfiguration = requiresManual
                                || expiration.AbsoluteExpirationUnixTime > 0
                                || expiration.UsablePeriodDays > 0,
                            FilePath = fullPath,
                            TypeFull = model.StackableType,
                            ItemCategory = model.ItemCategory,
                            AttachType = model.AttachType,
                            StackLimit = stackLimit,
                            Durability = 0,
                            ImpossibleJson = SerializeImpossible(model.ImpossibleContentItems),
                        };
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _parseFailures);
                }
            });

            var written = 0;
            var seen = new HashSet<int>();
            foreach (var entry in results)
            {
                if (entry == null || !seen.Add(entry.Id))
                    continue;
                PvfDiskIndexStore.InsertItem(conn, tx, entry);
                written++;
            }
            return written;
        }
    }
}
