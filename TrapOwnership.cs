using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;

/// <summary>
/// Tracks who placed each non-tile-entity trap block (spike trap, barbed fence, etc.)
/// so we can attribute kills to them on zombie death.
///
/// Tile-entity traps (blade trap, dart trap, turrets) already have their own ownerID
/// on the tile entity itself, so we don't track those here - we read them directly
/// via TileEntityPoweredMeleeTrap.GetOwner() at award time.
///
/// Owners are stored as the player's persistent id (not the entity id, which changes
/// every session) and saved to KitsuneTrapXP-owners.txt in the save folder whenever the
/// world saves, so traps keep paying their owner after a server restart.
/// </summary>
public static class TrapOwnership
{
    // Block tag applied to all trap-like blocks in vanilla (blocks.xml uses "trapsSkill").
    private const string TrapsTag = "trapsSkill";

    // Landmines (mineCandyTin, mineHubcap, mineCookingPot, mineAirFilter + variants)
    // carry a landmine-exclusive "Mine" tag. They kill via explosion, and the
    // explosion stamps the victim's DamageSource.BlockPosition with the mine's own
    // block position, so once we track the placement here, ResolveTrapOwner maps
    // the kill back to whoever placed the mine.
    private const string MineTag = "Mine";

    private const string OwnersFileName = "KitsuneTrapXP-owners.txt";

    private static readonly Dictionary<Vector3i, PlatformUserIdentifierAbs> _ownerByPos = new Dictionary<Vector3i, PlatformUserIdentifierAbs>();
    private static readonly object _lock = new object();

    // Save folder of the world the map was loaded for. Pinned at load rather than looked
    // up at save time, so a save that lands while another world is starting up can never
    // write this world's owners into that world's folder.
    private static string _saveDir;
    private static bool _dirty;

    public static void RegisterPatches(Harmony harmony)
    {
        // Load the saved owners once the world is up, and write them whenever the world
        // saves. World.Save runs on the periodic autosave, the pause menu, and during
        // shutdown (after WorldShuttingDown fires, so the map must not be cleared there).
        ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
        var save = AccessTools.Method(typeof(World), "Save");
        if (save != null)
        {
            harmony.Patch(save, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(TrapOwnership), nameof(WorldSavePostfix))));
        }
        else
        {
            Log.Warning("[KitsuneTrapXP] World.Save not found - trap owners won't survive a restart.");
        }

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

        var players = GameManager.Instance?.GetPersistentPlayerList();
        if (players == null) return;

        PlatformUserIdentifierAbs fallbackOwner = null;
        if (persistentPlayerId != null)
            fallbackOwner = players.GetPlayerData(persistentPlayerId)?.PrimaryId;

        foreach (var change in changes)
        {
            if (TrapAttribution.Debug)
            {
                var bName = change.blockValue.Block?.GetBlockName() ?? "null";
                Log.Out($"[KitsuneTrapXP.debug]   change: pos={change.blockValueRef.BlockPosition} block={bName} bChangeBlockValue={change.bChangeBlockValue} changedByEntityId={change.changedByEntityId} fallback={fallbackOwner?.CombinedString ?? "null"}");
            }

            if (!change.bChangeBlockValue) continue;

            var block = change.blockValue.Block;
            if (block == null) continue;

            // Only players own traps. A change made by anything else (a zombie wearing a
            // spike down a stage, a block falling) leaves the recorded owner alone.
            PlatformUserIdentifierAbs owner = null;
            if (change.changedByEntityId > 0)
                owner = players.GetPlayerDataFromEntityID(change.changedByEntityId)?.PrimaryId;
            if (owner == null) owner = fallbackOwner;
            if (owner == null) continue;

            var isTrap = IsTrapBlock(block);
            if (TrapAttribution.Debug)
                Log.Out($"[KitsuneTrapXP.debug]   → block={block.GetBlockName()} isTrap={isTrap}");

            if (isTrap)
            {
                lock (_lock) { _ownerByPos[change.blockValueRef.BlockPosition] = owner; _dirty = true; }
                if (TrapAttribution.Debug)
                    Log.Out($"[KitsuneTrapXP.debug] Tracked trap placement: {block.GetBlockName()} at {change.blockValueRef.BlockPosition} by {owner.CombinedString}");
            }
            else
            {
                lock (_lock) { if (_ownerByPos.Remove(change.blockValueRef.BlockPosition)) _dirty = true; }
            }
        }
    }

    /// <summary>Entity id of the trap's owner, or -1 if nobody owns it or the owner is offline.</summary>
    public static int GetOwnerEntityId(Vector3i pos)
    {
        PlatformUserIdentifierAbs owner;
        lock (_lock)
        {
            if (!_ownerByPos.TryGetValue(pos, out owner)) return -1;
        }
        var ppd = GameManager.Instance?.GetPersistentPlayerList()?.GetPlayerData(owner);
        return ppd != null ? ppd.EntityId : -1;
    }

    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
    {
        lock (_lock)
        {
            _ownerByPos.Clear();
            _dirty = false;
            _saveDir = null;
        }

        // Only the server (dedicated, or the host in single player) tracks traps.
        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

        try
        {
            var dir = GameIO.GetSaveGameDir();
            var path = Path.Combine(dir, OwnersFileName);
            int loaded = 0, skipped = 0;
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    // x y z <tab> combined player id
                    var tab = line.IndexOf('\t');
                    if (tab <= 0) { skipped++; continue; }
                    var xyz = line.Substring(0, tab).Split(' ');
                    if (xyz.Length != 3
                        || !int.TryParse(xyz[0], out var x)
                        || !int.TryParse(xyz[1], out var y)
                        || !int.TryParse(xyz[2], out var z)
                        || !PlatformUserIdentifierAbs.TryFromCombinedString(line.Substring(tab + 1), out var owner)
                        || owner == null)
                    {
                        skipped++;
                        continue;
                    }
                    lock (_lock) { _ownerByPos[new Vector3i(x, y, z)] = owner; }
                    loaded++;
                }
            }
            lock (_lock) { _saveDir = dir; }
            Log.Out($"[KitsuneTrapXP] Loaded {loaded} trap owner(s) from {OwnersFileName}" +
                    (skipped > 0 ? $" ({skipped} unreadable line(s) skipped)." : "."));
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] Failed to load {OwnersFileName} ({ex.Message}); trap owners start empty.");
            lock (_lock) { _saveDir = GameIO.GetSaveGameDir(); }
        }
    }

    public static void WorldSavePostfix(World __instance)
    {
        try
        {
            string dir;
            lock (_lock) { dir = _saveDir; }
            if (dir == null) return;

            // Forget traps that are gone. A zombie that wrecks a spike changes the block with
            // no player attached, so RecordPlacements never sees it; check what is actually
            // there now, for the chunks that are loaded.
            var lines = new StringBuilder();
            lock (_lock)
            {
                var gone = new List<Vector3i>();
                foreach (var kv in _ownerByPos)
                {
                    if (__instance.GetChunkFromWorldPos(kv.Key) != null
                        && !IsTrapBlock(__instance.GetBlock(kv.Key).Block))
                        gone.Add(kv.Key);
                }
                foreach (var pos in gone) _ownerByPos.Remove(pos);
                if (gone.Count > 0) _dirty = true;
                if (!_dirty) return;

                foreach (var kv in _ownerByPos)
                    lines.Append(kv.Key.x).Append(' ').Append(kv.Key.y).Append(' ').Append(kv.Key.z)
                         .Append('\t').Append(kv.Value.CombinedString).Append('\n');
                _dirty = false;
            }

            // Write to a temp file and swap it in, so a crash mid-write can't leave half a file.
            var path = Path.Combine(dir, OwnersFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, lines.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (System.Exception ex)
        {
            lock (_lock) { _dirty = true; }
            Log.Warning($"[KitsuneTrapXP] Failed to save {OwnersFileName}: {ex.Message}");
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
        return block.HasAnyFastTags(FastTags<TagGroup.Global>.Parse(TrapsTag))
            || block.HasAnyFastTags(FastTags<TagGroup.Global>.Parse(MineTag));
    }
}
