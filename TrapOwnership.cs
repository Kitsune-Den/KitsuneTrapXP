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
        // Primary hook: GameManager.ChangeBlocks — deepest funnel for all block changes on
        // the server side. Player-placed blocks arrive as NetPackageSetBlock → ChangeBlocks.
        // BlockChangeInfo carries changedByEntityId + pos + blockValue natively.
        var m = AccessTools.Method(typeof(GameManager), "ChangeBlocks",
            new[] { typeof(PlatformUserIdentifierAbs), typeof(List<BlockChangeInfo>) });
        if (m != null)
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapOwnership), nameof(ChangeBlocksPostfix))));
            Log.Out("[KitsuneTrapXP] Patched GameManager.ChangeBlocks");
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] GameManager.ChangeBlocks not found - spike trap ownership tracking disabled.");
        }

        // Fallback hook: NetPackageSetBlock.ProcessPackage — catches player block placements
        // at the network-receive layer. Redundant when ChangeBlocks works, but if Harmony
        // has trouble binding to the generic-arg signature of ChangeBlocks, this still fires.
        var np = AccessTools.Method(typeof(NetPackageSetBlock), "ProcessPackage",
            new[] { typeof(World), typeof(GameManager) });
        if (np != null)
        {
            harmony.Patch(np, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapOwnership), nameof(NetPackageSetBlockPrefix))));
            Log.Out("[KitsuneTrapXP] Patched NetPackageSetBlock.ProcessPackage");
        }
    }

    /// <summary>
    /// Prefix on NetPackageSetBlock.ProcessPackage. Reads persistentPlayerId + blockChanges
    /// fields via reflection, then calls into the same logic as ChangeBlocksPostfix.
    /// Doesn't skip the original (returns void).
    /// </summary>
    private static readonly System.Reflection.FieldInfo _npPersistentPlayerId =
        typeof(NetPackageSetBlock).GetField("persistentPlayerId",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.FieldInfo _npBlockChanges =
        typeof(NetPackageSetBlock).GetField("blockChanges",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static void NetPackageSetBlockPrefix(NetPackageSetBlock __instance)
    {
        try
        {
            if (TrapAttribution.Debug)
                Log.Out("[KitsuneTrapXP.debug] NetPackageSetBlock.ProcessPackage fired");

            if (_npPersistentPlayerId == null || _npBlockChanges == null) return;

            var pid = _npPersistentPlayerId.GetValue(__instance) as PlatformUserIdentifierAbs;
            var changes = _npBlockChanges.GetValue(__instance) as List<BlockChangeInfo>;
            if (changes == null) return;

            RecordPlacements(pid, changes);
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] NetPackageSetBlockPrefix failed: {ex.Message}");
        }
    }

    public static void ChangeBlocksPostfix(PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> _blocksToChange)
    {
        try
        {
            if (TrapAttribution.Debug)
                Log.Out($"[KitsuneTrapXP.debug] ChangeBlocks fired: changes={_blocksToChange?.Count ?? -1}, persistentId={persistentPlayerId?.ToString() ?? "null"}");
            RecordPlacements(persistentPlayerId, _blocksToChange);
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] ChangeBlocksPostfix failed: {ex.Message}");
        }
    }

    /// <summary>Shared placement-tracking logic called from either ChangeBlocks postfix or NetPackageSetBlock prefix.</summary>
    private static void RecordPlacements(PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> changes)
    {
        if (changes == null) return;

        int fallbackEntityId = -1;
        var gm = GameManager.Instance;
        if (gm != null && persistentPlayerId != null)
        {
            var ppd = gm.GetPersistentPlayerList()?.GetPlayerData(persistentPlayerId);
            if (ppd != null) fallbackEntityId = ppd.EntityId;
        }

        foreach (var change in changes)
        {
            if (TrapAttribution.Debug)
            {
                var bName = change.blockValue.Block?.GetBlockName() ?? "null";
                Log.Out($"[KitsuneTrapXP.debug]   change: pos={change.pos} block={bName} bChangeBlockValue={change.bChangeBlockValue} changedByEntityId={change.changedByEntityId} fallback={fallbackEntityId}");
            }

            if (!change.bChangeBlockValue) continue;

            var block = change.blockValue.Block;
            if (block == null) continue;

            var entityId = change.changedByEntityId > 0 ? change.changedByEntityId : fallbackEntityId;
            if (entityId <= 0) continue;

            var isTrap = IsTrapBlock(block);
            if (TrapAttribution.Debug)
                Log.Out($"[KitsuneTrapXP.debug]   → block={block.GetBlockName()} isTrap={isTrap}");

            if (isTrap)
            {
                lock (_lock) { _ownerByPos[change.pos] = entityId; }
                if (TrapAttribution.Debug)
                    Log.Out($"[KitsuneTrapXP.debug] Tracked trap placement: {block.GetBlockName()} at {change.pos} by entity {entityId}");
            }
            else
            {
                lock (_lock) { _ownerByPos.Remove(change.pos); }
            }
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

        // Primary: name-prefix match. Covers trapSpikesIronDmg0-3, trapSpikesWoodDmg0-3,
        // trapSpikesScrapIronMaster, barbedFence, barbedWire, etc. In 2.x the non-tile-entity
        // trap blocks all use these prefixes, and TFP doesn't tag them with trapsSkill.
        var name = block.GetBlockName();
        if (!string.IsNullOrEmpty(name))
        {
            if (name.StartsWith("trap", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("barbed", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Fallback: trapsSkill tag. Covers electrical traps (blade, dart, turrets) and any
        // future trap blocks TFP decides to tag. Tile-entity traps are read directly via
        // TileEntityPoweredMeleeTrap/RangedTrap at attribution time so they don't strictly
        // need to hit this tracker, but catching them here is harmless defense-in-depth.
        return block.HasAnyFastTags(FastTags<TagGroup.Global>.Parse(TrapsTag));
    }
}
