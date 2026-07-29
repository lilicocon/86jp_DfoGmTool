using System;
using System.Collections.Generic;
using GmPvfLib;

namespace DfoGmTool.ServerCore.Game.Inventory
{
    /// <summary>
    /// Disk-index hook for avatar grant fields (usable job, ability case, select abilities).
    /// Wired by PvfIndexService so grant/options never reopen .equ scripts.
    /// </summary>
    internal static class AvatarGrantIndex
    {
        internal delegate bool LoaderDelegate(
            int itemId,
            out string usableJob,
            out int abilityCaseIndex,
            out IReadOnlyList<AvatarSelectAbilityEntry> selectAbilities,
            out string equipmentType,
            out int grade);

        public static LoaderDelegate Loader { get; set; }

        internal static void Reset()
        {
            Loader = null;
        }
    }
}
