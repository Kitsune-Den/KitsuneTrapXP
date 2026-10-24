#if V26
using System;
using System.Collections.Generic;
using HarmonyLib;

/// <summary>
/// V2.6 only. On 3.x, turret / blade / dart damage carries DamageSource.bTrapKillXP; 2.6 has
/// no such flag. Instead 2.6 passes <c>_flags &amp; 2</c> to ItemActionAttack.Hit (turrets,
/// blade traps) and fires darts through BlockProjectileMoveScript, with the trap owner as the
/// attacker in both cases. We mark "inside a trap hit" for the duration of those calls, so
/// TrapAttribution can tell a turret's bullet from the owner's own gun.
///
/// The zombie dies (and OnEntityDeath runs) inside the call, and only afterwards does vanilla
/// pay its own ExperienceValue * ElectricalTrapXP via EntityPlayer.AddKillXP. We remember the
/// kills we paid out during the call and skip vanilla's payout for them, so the AE bonus
/// isn't paid twice. The list is dropped when the call ends.
/// </summary>
public static class TrapHitContext
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static HashSet<int> _awardedKills;

    /// <summary>True while a turret, blade trap or dart trap is dealing its damage.</summary>
    public static bool Active => _depth > 0;

    public static void RegisterPatches(Harmony harmony)
    {
        var hit = AccessTools.Method(typeof(ItemActionAttack), "Hit");
        if (hit != null)
        {
            harmony.Patch(hit,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TrapHitContext), nameof(HitPrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(TrapHitContext), nameof(HitFinalizer))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] ItemActionAttack.Hit not found - turret and blade trap kills won't be attributed.");
        }

        var dart = AccessTools.Method(typeof(BlockProjectileMoveScript), "checkCollision");
        if (dart != null)
        {
            harmony.Patch(dart,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TrapHitContext), nameof(Enter))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(TrapHitContext), nameof(Exit))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] BlockProjectileMoveScript.checkCollision not found - dart trap kills won't be attributed.");
        }

        var addKillXP = AccessTools.Method(typeof(EntityPlayer), "AddKillXP",
            new[] { typeof(EntityAlive), typeof(float) });
        if (addKillXP != null)
        {
            harmony.Patch(addKillXP, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapHitContext), nameof(AddKillXPPrefix))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] EntityPlayer.AddKillXP not found - electrical trap kills may also get vanilla's AE XP on top.");
        }
    }

    // _flags bit 2 is vanilla's "this hit is a trap" marker (it gates the ElectricalTrapXP payout).
    public static void HitPrefix(int _flags)
    {
        if ((_flags & 2) != 0) Enter();
    }

    public static void HitFinalizer(int _flags)
    {
        if ((_flags & 2) != 0) Exit();
    }

    public static void Enter()
    {
        _depth++;
    }

    public static void Exit()
    {
        if (_depth > 0) _depth--;
        if (_depth == 0) _awardedKills?.Clear();
    }

    /// <summary>Record that we paid out for this kill during the current trap hit.</summary>
    public static void MarkAwarded(int entityId)
    {
        if (!Active) return;
        if (_awardedKills == null) _awardedKills = new HashSet<int>();
        _awardedKills.Add(entityId);
    }

    /// <summary>Skip vanilla's trap kill XP for zombies we already paid for in this trap hit.</summary>
    public static bool AddKillXPPrefix(EntityAlive killedEntity)
    {
        if (!Active || killedEntity == null || _awardedKills == null) return true;
        return !_awardedKills.Remove(killedEntity.entityId);
    }
}
#endif
