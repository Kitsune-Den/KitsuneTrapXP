# KitsuneTrapXP

**Trap kills give 100% XP to the trap owner. No perk required. Party-shared. For 7 Days to Die 2.x.**

Here's the thing: you spend all week gathering materials, building your horde base, laying spike trap corridors and blade trap kill rooms. Horde night comes. The traps work beautifully. Zombies die by the dozen. And you get... zero XP from most of them.

Vanilla locks trap kill XP behind `perkAdvancedEngineering` (Intellect tree). Even maxed out, you only get 75% of the XP, and only from *electrical* traps like blade traps and turrets. Spike traps? Zero XP. Barbed fence? Zero. The whole system is gated behind a specific perk path that most players don't take.

This mod makes trap kills give the owner 100% of the zombie's XP by default, for every trap type in the game. Advanced Engineering still exists, but it's been repurposed as a bonus multiplier: each rank adds more XP on top of the baseline, so rank 5 gets you double XP per trap kill. And party members within range get their share automatically via vanilla's `PartySharedKillRange` + 10% party penalty. No more feeling like you're getting robbed on your own horde night.

## Requirements

- 7 Days to Die V2.0+
- **Server-side only.** Install on the server (or in single player). Clients don't need anything.
- **EAC must be off** on the server. This is a Harmony DLL mod and EAC blocks those.

## Installation

1. Drop the `KitsuneTrapXP` folder from the release zip into your `Mods/` folder on the server.
2. Restart the server.

Done. Clients can connect normally, they don't need the mod.

## What it does

Three Harmony patches and one small XML tweak:

- **Block placement tracking**: when you place a spike trap, barbed fence, or any block tagged `trapsSkill`, the mod records your entity ID as the owner at that position. Tile-entity traps (blade, dart, turrets) already track their own owner in vanilla, so those read directly.
- **Damage attribution**: when a zombie takes damage from a trap block, the mod stamps it with the trap owner's ID. Last trap to hit wins if multiple people's traps damaged the same zombie.
- **XP on death**: when a stamped zombie dies, the owner gets `ExperienceValue × (1.0 + AE_bonus)` XP. Party members within `PartySharedKillRange` get their share via vanilla's `SharedKillServer`.
- **XML patch**: Advanced Engineering's `ElectricalTrapXP` values change from `.15/.3/.45/.6/.75` to `.2/.4/.6/.8/1.0`. Rank 0 = +0% (100% total), rank 5 = +100% (200% total).

## What it does NOT do

- Pre-existing spike traps (placed before installing this mod) don't have a recorded owner. They won't give XP until someone places a new trap and re-enters attribution. First fresh horde night after install will work correctly for any new traps placed.
- It doesn't change the perk's *other* effects (crafting time reduction, loot probability for electrical/trap/workstation items). Those are untouched.

## Credits

There's another mod on Nexus that partially addresses this, but it kept the perk as a gate and just added a scale factor on top. This mod takes the different philosophical stance: trap kills should give XP, period. The perk makes it give *more*.

Made by AdaInTheLab.
