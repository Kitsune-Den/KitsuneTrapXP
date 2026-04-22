using System.Collections.Generic;
using HarmonyLib;

/// <summary>
/// Tracks who placed each non-tile-entity trap block (spike trap, barbed fence, etc.)
/// so we can attribute kills to them on zombie death.
///
/// Tile-entity traps (blade trap, dart trap, turrets) already have their own ownerID
/// on the tile entity itself, so we don't track those here - we read them directly
/// via TileEntityPoweredMeleeTrap.GetOwner() at award time.
/// </summary>
public static class TrapOwnership
{
    // Block tag applied to all trap-like blocks in vanilla (blocks.xml uses "trapsSkill").
    private const string TrapsTag = "trapsSkill";

    private static readonly Dictionary<Vector3i, int> _ownerByPos = new Dictionary<Vector3i, int>();
    private static readonly object _lock = new object();

    public static void RegisterPatches(Harmony harmony)
    {
        var m1 = AccessTools.Method(typeof(World), "SetBlockRPC",
            new[] { typeof(int), typeof(Vector3i), typeof(BlockValue), typeof(int) });
        var m2 = AccessTools.Method(typeof(World), "SetBlockRPC",
            new[] { typeof(int), typeof(Vector3i), typeof(BlockValue), typeof(sbyte), typeof(int) });

        var postfix = new HarmonyMethod(AccessTools.Method(typeof(TrapOwnership), nameof(SetBlockRPCPostfix)));
        if (m1 != null) harmony.Patch(m1, postfix: postfix);
        if (m2 != null) harmony.Patch(m2, postfix: postfix);

        if (m1 == null && m2 == null)
            Log.Warning("[KitsuneTrapXP] World.SetBlockRPC not found - spike trap ownership tracking disabled.");
    }

    public static void SetBlockRPCPostfix(Vector3i _blockPos, BlockValue _blockValue, int _changingEntityId)
    {
        try
        {
            if (_changingEntityId <= 0) return;
            var block = _blockValue.Block;
            if (block == null) return;

            if (IsTrapBlock(block))
            {
                lock (_lock) { _ownerByPos[_blockPos] = _changingEntityId; }
            }
            else
            {
                lock (_lock) { _ownerByPos.Remove(_blockPos); }
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] SetBlockRPCPostfix failed at {_blockPos}: {ex.Message}");
        }
    }

    public static int GetOwnerEntityId(Vector3i pos)
    {
        lock (_lock)
        {
            return _ownerByPos.TryGetValue(pos, out var id) ? id : -1;
        }
    }

    public static bool IsTrapBlock(Block block)
    {
        if (block == null) return false;
        return block.HasAnyFastTags(FastTags<TagGroup.Global>.Parse(TrapsTag));
    }
}
