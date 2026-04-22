using System.Collections.Generic;
using HarmonyLib;

/// <summary>
/// Stamps each zombie with the owner of the last trap that damaged it.
/// Read at death time by TrapXPAward to decide who gets credit.
///
/// We don't try to split XP across multiple trap-owners if several people's traps
/// damaged the same zombie - last hit wins. That's a conscious simplification; in
/// practice during horde night the trap doing fatal damage is almost always the
/// owner you want to credit.
/// </summary>
public static class TrapAttribution
{
    // zombieEntityId -> ownerEntityId of last trap to hit them.
    private static readonly Dictionary<int, int> _trapDamageByZombie = new Dictionary<int, int>();
    private static readonly object _lock = new object();

    public static void RegisterPatches(Harmony harmony)
    {
        var m = AccessTools.Method(typeof(EntityAlive), "DamageEntity",
            new[] { typeof(DamageSource), typeof(int), typeof(bool), typeof(float) });
        if (m != null)
        {
            harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapAttribution), nameof(DamageEntityPrefix))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] EntityAlive.DamageEntity not found - trap attribution disabled.");
        }
    }

    // Diagnostic logging toggle. Flip to true to spam the log with per-damage-tick info
    // for debugging. Default false - the "Trap kill" summary per death is still logged
    // unconditionally in TrapXPAward so you can see the mod doing its thing without the noise.
    public const bool Debug = false;

    public static void DamageEntityPrefix(EntityAlive __instance, DamageSource _damageSource)
    {
        try
        {
            if (__instance == null || _damageSource == null) return;
            if (__instance.entityType != EntityType.Zombie) return;

            var blockPos = _damageSource.BlockPosition;
            var hasBlockPos = blockPos != Vector3i.zero;

            // Only log damage we think came from a block (BlockPosition set). Otherwise we'd
            // spam on every player melee hit to a zombie.
            if (Debug && hasBlockPos)
            {
                var world = GameManager.Instance?.World;
                var te = world?.GetTileEntity(0, blockPos);
                var tracked = TrapOwnership.GetOwnerEntityId(blockPos);
                Log.Out($"[KitsuneTrapXP.debug] zombie {__instance.entityId} hit from block {blockPos}: te={te?.GetType().Name ?? "none"}, trackedOwner={tracked}, srcEntityId={_damageSource.getEntityId()}");
            }

            var ownerId = ResolveTrapOwner(_damageSource);
            if (ownerId <= 0) return;

            lock (_lock)
            {
                _trapDamageByZombie[__instance.entityId] = ownerId;
            }

            if (Debug)
                Log.Out($"[KitsuneTrapXP.debug] Stamped zombie {__instance.entityId} with trap owner {ownerId}");
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] DamageEntityPrefix failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Given a damage source, try to find the trap-owning player's entity ID.
    /// Returns -1 if this damage wasn't from a trap we track.
    /// </summary>
    private static int ResolveTrapOwner(DamageSource src)
    {
        var world = GameManager.Instance?.World;
        if (world == null) return -1;

        var blockPos = src.BlockPosition;
        if (blockPos == Vector3i.zero) return -1;

        // 1. Tile-entity traps: ask the tile entity for its owner.
        var te = world.GetTileEntity(0, blockPos);
        if (te is TileEntityPoweredMeleeTrap meleeTrap)
            return meleeTrap.OwnerEntityID;
        if (te is TileEntityPoweredRangedTrap rangedTrap)
            return rangedTrap.OwnerEntityID;

        // 2. Simple trap blocks (spike, barbed fence): check our placement tracker.
        return TrapOwnership.GetOwnerEntityId(blockPos);
    }

    public static int PopTrapOwnerFor(int zombieEntityId)
    {
        lock (_lock)
        {
            if (_trapDamageByZombie.TryGetValue(zombieEntityId, out var ownerId))
            {
                _trapDamageByZombie.Remove(zombieEntityId);
                return ownerId;
            }
        }
        return -1;
    }
}
