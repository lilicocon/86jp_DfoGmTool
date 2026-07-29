using DfoGmTool.ServerCore.Game.Characters;
using DfoGmTool.ServerCore.Game.Dungeon;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.SelectCharacter;
using DfoGmTool.ServerCore.Game.Skills;
using GmPvfLib;

namespace DfoGmTool.ServerCore.GameWorld
{
    // Every static value here is parsed from PVF and must not outlive a source switch.
    internal static class PvfRuntimeCache
    {
        internal static void ResetForPvfChange()
        {
            PvfArchive.ExternalPathResolver = null;
            CharacterStatComputer.ResetForPvfChange();
            ExpTableProvider.ResetForPvfChange();
            InitialCharacterSkills.ResetForPvfChange();
            ItemMetadataResolver.ResetForPvfChange();
            ItemGrantExpirationResolver.ResetForPvfChange();
            AmplifyInitialValueResolver.ResetForPvfChange();
            AvatarAbilityDataProvider.ResetForPvfChange();
            AvatarDurationResolver.ResetForPvfChange();
            AvatarGrantIndex.Reset();
            CreatureExtraResolver.ResetForPvfChange();
            RentalWeaponInventoryMapper.ResetForPvfChange();
            SkillDataProvider.ResetForPvfChange();
            SpTableProvider.ResetForPvfChange();
            SqliteInventoryStore.ResetForPvfChange();
        }

        internal static void WarmForPvfChange()
        {
            SkillDataProvider.WarmUp();
            AvatarAbilityDataProvider.WarmUp();
        }
    }
}
