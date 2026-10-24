# KitsuneTrapXP

🦊 **Part of the [Kitsune Systems Collection](https://github.com/Kitsune-Den)** ~
[KitsunePvPExtended](https://github.com/Kitsune-Den/KitsunePvPExtended) ·
[KitsuneZombieReach](https://github.com/Kitsune-Den/KitsuneZombieReach) ·
[KitsuneKitchen7D](https://github.com/Kitsune-Den/KitsuneKitchen7D) ·
[KitsuneFuelSaver](https://github.com/Kitsune-Den/KitsuneFuelSaver) ·
[KitsuneFoxacary](https://github.com/Kitsune-Den/KitsuneFoxacary)

![KitsuneTrapXP](kitsune-trap-xp.jpg)

**Trap kills give 100% XP to the trap owner. No perk required. Party-shared. For 7 Days to Die 3.0 – 3.3, plus a separate build for V2.6.**

Here's the thing: you spend all week gathering materials, building your horde base, laying spike trap corridors and blade trap kill rooms. Horde night comes. The traps work beautifully. Zombies die by the dozen. And you get... zero XP from most of them.

Vanilla locks trap kill XP behind `perkAdvancedEngineering` (Intellect tree). Even maxed out, you only get 75% of the XP, and only from *electrical* traps like blade traps and turrets. Spike traps? Zero XP. Barbed fence? Zero. The whole system is gated behind a specific perk path that most players don't take.

This mod makes trap kills give the owner 100% of the zombie's XP by default, for every trap type in the game. Advanced Engineering still exists, but it's been repurposed as a bonus multiplier: each rank adds more XP on top of the baseline, so rank 5 gets you double XP per trap kill. And party members within range get their share automatically via vanilla's `PartySharedKillRange` + 10% party penalty.

No more feeling like you're getting robbed on your own horde night.

## Requirements

- 7 Days to Die V3.0 – V3.3 (verified against V3.2.0 experimental; on V3.3.0 every patch target and game reference checks out statically)
- Still on **V2.6**? Use the V2.6 build instead (see [V2.6 build](#v26-build) below). The regular build does not work on 2.6.
- **Server-side only.** Install on the server (or in single player). Clients don't need anything.
- **EAC must be off** on the server. This is a Harmony DLL mod and EAC blocks those.

## Installation

1. Drop the `KitsuneTrapXP` folder from the release zip into your `Mods/` folder on the server.
2. Restart the server.

Done. Clients can connect normally, they don't need the mod.

## Configuring the XP amount

By default a trap kill awards the owner **100%** of the zombie's XP. If that feels too generous, edit `Settings.xml` in the mod folder and restart the server:

```xml
<KitsuneTrapXP baseline="1.0" />
```

`baseline` is the share awarded, as a fraction: `1.0` = 100%, `0.5` = 50%, `0.25` = 25%, `0` = none. Advanced Engineering's bonus still stacks on top (see `Config/progression.xml`), so for example `baseline="0.25"` with AE rank 5 (+100%) gives 125% total. If the file is missing or the value can't be read, it falls back to 100%, so existing setups are unchanged.

## V2.6 build

V2.6 has the same features (configurable baseline, landmines, turrets, blade and dart traps, saved trap owners), built against the 2.6 game files. Install it the same way, from the V2.6 zip. One difference: on 2.6, a turret kill made while another player has that turret's menu open isn't credited to the owner, because 2.6 doesn't mark that damage as coming from a trap. Every other trap kill works as on 3.x.

To build it, put the 2.6 game DLLs in `7dtd-binaries-2.6/` (same files as `7dtd-binaries/`, with `0Harmony.dll` from `Mods/0_TFP_Harmony`) and run `dotnet build -c Release -p:GameVersion=2.6`. The complete mod folder lands in `dist/v2.6/KitsuneTrapXP/`.

## What it does

Under the hood it's a handful of Harmony patches and one XML tweak:

- **Block placement tracking** ~ `GameManager.ChangeBlocks` postfix + `NetPackageSetBlock.ProcessPackage` prefix (belt-and-suspenders). When you place a spike trap, barbed fence, landmine, or any other trap block, the mod records you (your persistent player ID) as the owner at that block position. Hitting a trap with a hammer to upgrade it makes you its owner; a zombie wearing it down a stage doesn't change the owner.
- **Owners survive restarts** ~ the owner list is saved to `KitsuneTrapXP-owners.txt` in the world's save folder every time the world saves (autosave, pause menu, shutdown) and read back when the world starts. Traps that have since been destroyed are dropped from the list when it saves. Landmines carry their own `Mine` block tag (they aren't tagged `trapsSkill` like the rest), so the tracker watches both tags. Blade traps, dart traps and turrets don't need the tracker: vanilla already passes the trap owner as the attacker on their damage (flagged `bTrapKillXP`), so the mod reads the owner straight off the hit.
- **Damage attribution** ~ `EntityAlive.DamageEntity` prefix, plus `EntityAlive.ProcessDamageResponse` for damage that arrives from a client (a turret fires from the client of whoever has its menu open). When a zombie takes damage from a trap, the mod stamps it with the trap owner's entity ID. Last trap to hit wins if multiple people's traps damaged the same zombie.
- **XP on death** ~ `EntityAlive.OnEntityDeath` prefix. When a stamped zombie dies, the mod sends a `NetPackageSharedPartyKill` to the owner's client with `ExperienceValue × (1.0 + AE_bonus)`. That's the package vanilla uses for party-shared XP, and it's the only packet wired to animate the XP bar + pop the "+N XP" tooltip client-side. Party members within range get their share too via vanilla's `SharedKillServer`. Vanilla's own `AwardKillXPServer` is skipped for these kills so the AE bonus isn't paid twice.
- **XML patch** ~ Advanced Engineering's `ElectricalTrapXP` passive effect values change from `.15/.3/.45/.6/.75` to `.2/.4/.6/.8/1.0`. Rank 0 = +0% (100% total), rank 5 = +100% (200% total, aka double XP).

## What it does NOT do

- Traps placed before installing this mod (or before 0.8.0, which is when owners started being saved) don't have a recorded owner. They won't give XP until replaced, or upgraded with a hammer. Anything placed from then on keeps its owner across restarts.
- A trap only pays its owner while they're online. Kills while the owner is logged off give no trap XP.
- It doesn't change Advanced Engineering's *other* effects (crafting time reduction, loot probability for electrical/trap/workstation items). Those are untouched.

## Credits

There's another mod on Nexus that partially addresses this, but it kept the perk as a gate and just added a scale factor on top. This mod takes the different philosophical stance: **trap kills should give XP, period.** The perk makes it give *more*.

Made by AdaInTheLab.

## License

[CC BY-NC-SA 4.0](LICENSE), with an extra permission for servers that make money: see [NOTICE.md](NOTICE.md). In short: play it, change it and share it, with credit, not for sale, and with changed versions under the same license.
