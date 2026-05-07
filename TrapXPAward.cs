using HarmonyLib;

/// <summary>
/// On zombie death with a trap-attributed owner, send a NetPackageSharedPartyKill to the
/// owner's client. That package triggers SharedKillClient which:
///   - AddLevelExp(notifyUI: true) - animates the XP bar
///   - Shows the "+N XP" tooltip on the right side of the screen
///   - Fires QuestEventManager.EntityKilled for quest tracking
///
/// This is the only package vanilla has that's wired to update the client's UI with XP on
/// a kill. NetPackageEntityAwardKillServer sounds like it should but only does quest events.
///
/// We also set entityThatKilledMe so vanilla's own kill log + AwardKill flow sees correct
/// attribution (harmless if redundant with our package, catches any code paths we missed).
///
/// AE bonus: read from ElectricalTrapXP cvar on the owner's buffs. Baseline 1.0 + bonus.
/// progression.xml patch raises the cvar to 0.2/0.4/0.6/0.8/1.0 for the 5 AE ranks, so
/// rank 0 gets 100% XP, rank 5 gets 200% XP (double).
/// </summary>
public static class TrapXPAward
{
    private const string AECvar = "ElectricalTrapXP";

    public static void RegisterPatches(Harmony harmony)
    {
        var m = AccessTools.Method(typeof(EntityAlive), "OnEntityDeath");
        if (m != null)
        {
            // Prefix so we can set entityThatKilledMe before vanilla's kill flow reads it,
            // and so we send the XP package before the zombie's entityId becomes stale.
            harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapXPAward), nameof(OnEntityDeathPrefix))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] EntityAlive.OnEntityDeath not found - XP award disabled.");
        }
    }

    public static void OnEntityDeathPrefix(EntityAlive __instance)
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

            // Baseline 100% + AE bonus.
            float aeBonus = 0f;
            try { aeBonus = owner.Buffs.GetCustomVar(AECvar); } catch { aeBonus = 0f; }
            float xpMultiplier = 1.0f + aeBonus;

            int baseXp = EntityClass.list[__instance.entityClass].ExperienceValue;
            int awarded = (int)(baseXp * xpMultiplier + 0.5f);
            if (awarded < 1) awarded = 1;

            // Stamp the killer on the zombie so vanilla's own kill log + quest packet see it.
            __instance.entityThatKilledMe = owner;

            // Deliver XP to the trap owner.
            //
            // SP/host: NetPackageSharedPartyKill doesn't deliver to the host's own client
            // (SendPackage targets remote clients only, and ProcessPackage's UI path doesn't
            // fire reliably when invoked locally). Award XP directly through Progression.
            //
            // Dedicated server: owner is a remote client, send the packet over the wire so
            // the client-side handler (AddLevelExp + "+N XP" tooltip) runs there.
            try
            {
                if (owner is EntityPlayerLocal localOwner && localOwner.Progression != null)
                {
                    localOwner.Progression.AddLevelExp(
                        awarded,
                        "_xpFromKilling",
                        Progression.XPTypes.Kill,
                        true,    // useBonus
                        true,    // notifyUI
                        ownerId);
                }
                else
                {
                    var pkg = NetPackageManager.GetPackage<NetPackageSharedPartyKill>()
                        .Setup(__instance.entityClass, awarded, owner.entityId, __instance.entityId);
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        pkg,
                        _onlyClientsAttachedToAnEntity: true,
                        _attachedToEntityId: owner.entityId);
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[KitsuneTrapXP] Failed to award trap XP to owner: {ex.Message}");
            }

            // Party share - vanilla's SharedKillServer distributes to OTHER party members
            // within PartySharedKillRange, applying the 10% party penalty.
            if (owner.IsInParty())
            {
                GameManager.Instance.SharedKillServer(__instance.entityId, ownerId, xpMultiplier);
            }

            Log.Out($"[KitsuneTrapXP] Trap kill: {__instance.EntityName} → {owner.EntityName} " +
                    $"(base={baseXp} xMult={xpMultiplier:F2} awarded={awarded}).");
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] OnEntityDeathPrefix failed: {ex.Message}");
        }
    }
}
