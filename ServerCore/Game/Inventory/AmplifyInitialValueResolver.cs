using System;
using System.Collections.Generic;
using DfoGmTool.ServerCore.GameWorld;
using GmPvfLib;

namespace DfoGmTool.ServerCore.Game.Inventory
{
    internal static class AmplifyInitialValueResolver
    {
        private static readonly object Sync = new object();
        private static AmplifyItemFile _config;
        private static Dictionary<string, double> _diskWeights;
        private static double? _diskBase;
        private static bool _diskLoadAttempted;

        /// <summary>
        /// When set, Resolve prefers disk-index amplify_config rows (schema v6+)
        /// and never opens etc/amplifyitem.etc.
        /// </summary>
        public static Func<Dictionary<string, string>> DiskConfigLoader { get; set; }

        internal static void ResetForPvfChange()
        {
            lock (Sync)
            {
                _config = null;
                _diskWeights = null;
                _diskBase = null;
                _diskLoadAttempted = false;
                DiskConfigLoader = null;
            }
        }

        internal static void ResetCacheOnly()
        {
            lock (Sync)
            {
                _config = null;
                _diskWeights = null;
                _diskBase = null;
                _diskLoadAttempted = false;
            }
        }

        internal static ushort Resolve(int rarity)
        {
            if (TryResolveFromDisk(rarity, out var diskValue))
                return diskValue;

            if (DiskConfigLoader != null)
                return 0;

            var config = GetConfig();
            var baseValue = config.GetBaseValue(AmplifyOptionType.PhysicalAttack);
            var weight = config.RarityWeights.TryGetValue(GetRarityName(rarity), out var value)
                ? value
                : 1d;
            var result = Math.Max(0, (int)(baseValue * weight));
            return (ushort)Math.Min(ushort.MaxValue, result);
        }

        private static bool TryResolveFromDisk(int rarity, out ushort result)
        {
            result = 0;
            lock (Sync)
            {
                if (_diskBase == null)
                {
                    var loader = DiskConfigLoader;
                    if (loader == null || _diskLoadAttempted)
                        return false;
                    _diskLoadAttempted = true;
                    Dictionary<string, string> map;
                    try { map = loader(); }
                    catch (Exception ex)
                    {
                        DfoGmTool.ServerCore.FileLogger.Log("[AmplifyInitialValueResolver] 磁盘索引读取失败: " + ex.Message);
                        return false;
                    }
                    if (map == null || map.Count == 0)
                        return false;
                    if (!map.TryGetValue("physical_attack_base", out var baseText)
                        || !double.TryParse(baseText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var baseValue))
                        return false;
                    var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in map)
                    {
                        if (!pair.Key.StartsWith("weight:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!double.TryParse(pair.Value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var w))
                            continue;
                        weights[pair.Key.Substring("weight:".Length)] = w;
                    }
                    _diskBase = baseValue;
                    _diskWeights = weights;
                }

                var weight = 1d;
                if (_diskWeights != null
                    && _diskWeights.TryGetValue(GetRarityName(rarity), out var wVal))
                    weight = wVal;
                var raw = Math.Max(0, (int)(_diskBase.Value * weight));
                result = (ushort)Math.Min(ushort.MaxValue, raw);
                return true;
            }
        }

        private static AmplifyItemFile GetConfig()
        {
            lock (Sync)
            {
                return _config ??= AmplifyItemFile.Parse(PvfArchiveAccessor.ReadText("etc/amplifyitem.etc"));
            }
        }

        private static string GetRarityName(int rarity)
        {
            return rarity switch
            {
                0 => "common",
                1 => "uncommon",
                2 => "rare",
                3 => "unique",
                4 => "epic",
                5 => "chronicle",
                6 => "legendary",
                _ => string.Empty,
            };
        }
    }
}
