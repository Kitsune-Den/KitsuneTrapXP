using HarmonyLib;
using System.Reflection;

/// <summary>
/// Entry point for KitsuneTrapXP.
///
/// Makes trap kills give the trap owner 100% of the zombie's XP by default, no perk required,
/// for every trap type (spike, barbed fence, blade trap, dart trap, turrets).
/// Advanced Engineering becomes a bonus multiplier on top of baseline.
/// Party members within PartySharedKillRange get their share via vanilla's SharedKillServer.
/// </summary>
public class KitsuneTrapXPMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("com.adainthelab.kitsunetrapxp");

        // Track block placements so we know who owns each spike/barbed-fence block.
        // Tile entity traps (blade, dart) already track their own owner, so those
        // don't need the tracker - we read them directly via TileEntityPoweredMeleeTrap.
        TrapOwnership.RegisterPatches(harmony);

        // Attribute trap damage: when a zombie takes damage from a trap block, remember
        // who placed the trap so we can award XP on death.
        TrapAttribution.RegisterPatches(harmony);

        // Award XP on zombie death for trap kills: owner gets ExperienceValue * (1 + AE bonus),
        // party members get their share via vanilla SharedKillServer.
        TrapXPAward.RegisterPatches(harmony);

        Log.Out("[KitsuneTrapXP] Loaded - trap kills give 100% XP baseline, Advanced Engineering adds bonus.");
    }
}
