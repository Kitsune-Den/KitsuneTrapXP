using HarmonyLib;

/// <summary>
/// On zombie death, if a trap owner is on record, award them 100% of the zombie's XP
/// plus an Advanced Engineering bonus, and trigger vanilla party share.
///
/// Baseline is 1.0x (100% XP). AE bonus is read from the ElectricalTrapXP cvar on the
/// owner's buffs, which is bumped to 0.2/0.4/0.6/0.8/1.0 by perkAdvancedEngineering
/// (see Config/progression.xml). So rank 0 = 1.0x, rank 5 = 2.0x.
/// </summary>
public static class TrapXPAward
{
    private const string AECvar = "ElectricalTrapXP";

    public static void RegisterPatches(Harmony harmony)
    {
        var m = AccessTools.Method(typeof(EntityAlive), "OnEntityDeath");
        if (m != null)
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapXPAward), nameof(OnEntityDeathPostfix))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] EntityAlive.OnEntityDeath not found - XP award disabled.");
        }
    }

    public static void OnEntityDeathPostfix(EntityAlive __instance)
    {
        try
        {
            if (__instance == null || __instance.isEntityRemote) return;
            if (__instance.entityType != EntityType.Zombie) return;

            var ownerId = TrapAttribution.PopTrapOwnerFor(__instance.entityId);
            if (ownerId <= 0) return;

            var world = GameManager.Instance?.World;
            if (world == null) return;

            var owner = world.GetEntity(ownerId) as EntityPlayer;
            if (owner == null) return;

            // Baseline 1.0 (100%) + AE bonus cvar (0.0 at rank 0, up to 1.0 at rank 5).
            float aeBonus = 0f;
            try { aeBonus = owner.Buffs.GetCustomVar(AECvar); } catch { aeBonus = 0f; }
            float xpMultiplier = 1.0f + aeBonus;

            int baseXp = EntityClass.list[__instance.entityClass].ExperienceValue;
            int awarded = (int)(baseXp * xpMultiplier + 0.5f);
            if (awarded < 1) awarded = 1;

            owner.Progression.AddLevelExp(awarded, "_xpFromKill", Progression.XPTypes.Kill, useBonus: true);
            owner.bPlayerStatsChanged = true;

            Log.Out($"[KitsuneTrapXP] Trap kill: {__instance.EntityName} killed by {owner.EntityName}'s trap, " +
                    $"base={baseXp} xMult={xpMultiplier:F2} awarded={awarded}.");

            // Vanilla party share: recomputes ExperienceValue * xpModifier and distributes
            // to party members within PartySharedKillRange, applying the 10% party penalty.
            if (owner.IsInParty())
            {
                GameManager.Instance.SharedKillServer(__instance.entityId, ownerId, xpMultiplier);
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] OnEntityDeathPostfix failed: {ex.Message}");
        }
    }
}
