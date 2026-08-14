# Zombie hit zones — damage by limb

**Status: notes only.** strawberry, 2026-08-14: *"so i think almost all guns should 1 shot headshot
zombies. and take considerably more shots on the body, even more on limbs. just take notes for now"*

Nothing here is implemented. This records the target, what is actually in the code today, and the
decisions that have to be made before any of it can be written.

## The target

| zone | intent |
| --- | --- |
| head | almost every gun kills in one |
| body | considerably more shots than today |
| limbs | more still |

## What exists today

There **is** a limb table, and it applies to **players in multiplayer only** —
`core/UnturnedNet/ServerCombat.cs:38-40`:

```
HeadMult  = 3.0f
TorsoMult = 1.0f
LegMult   = 0.6f
```

Hardcoded, not per-gun, not read from any `.dat`. Zones are resolved by hit height against the
avatar: head ≥ 1.45 m, torso ≥ 0.78 m, legs below (`PlayerHeadMinY` / `PlayerTorsoMinY`, applied at
`ServerCombat.cs:389`).

**Against zombies there is no multiplier on any path.** Three paths, three different ways of not
applying one:

| path | site | what happens |
| --- | --- | --- |
| singleplayer | `game/PlayerController.cs:4448` | `z.DamageHit(b.Damage, …)` — flat. `IsHeadshot` feeds only the hitmarker colour |
| MP, node-backed zombie | `game/ZombieNetSync.cs:199` | `t.Brain.DamageHit(damage, …)` — the `headshot` bool is dropped entirely |
| MP, sim zombie | `core/UnturnedSim/ZombieSim.cs:834` | limb IS passed in, recorded to `_killedBy[row]`, never multiplies |

The sim path is the one to be careful about. It threads `ZombieLimb.Skull` vs `Spine` all the way
through the call, so the code reads as wired — the value is used for kill attribution and nothing
else. Reading the call site is not enough to tell; only the implementation says.

A zombie headshot is therefore a red hitmarker and nothing else, in every mode.

## Why nobody noticed

`Zombie_Damage` is ≥ 99 on **37 of 54 guns**. Most weapons already one-shot a normal zombie wherever
they hit, so a missing head multiplier is invisible: the head result is already correct, by accident.

This also means **the body number is the actual work**, not the head one. Hitting the target is
mostly a matter of bringing `Zombie_Damage` down far enough that a body shot takes several rounds,
then letting a head multiplier restore the one-shot. Adding a head multiplier alone changes nothing
observable.

## Decisions needed before implementing

1. **Crawlers.** `ZombieController.IsHeadshot` takes the top 18% of the collider, and a crawler's is
   0.8 m rather than 1.8 m — so its head zone is ~14 cm tall and sits near the floor. A single
   fraction cannot serve both. Needs either a per-speciality zone or an explicit "crawlers have no
   head zone" call.
2. **Which guns are "almost all".** A one-shot rule expressed as a multiplier depends on the body
   number, so the two have to be retuned together, per gun or per tier. The `.22` (`sportshot`,
   `Zombie_Damage 32`) and the LMGs (`nykorev` 33, `dragonfang` 38, `fury` 25) are the ones that will
   not reach a kill on any sane multiplier — they are presumably the "almost".
3. **Per-gun or global.** The player table is a global constant. Zombies could follow that, or read
   a `.dat` field. Retail carries per-gun damage multipliers; this port does not parse any.
4. **Specials.** Mega/boss zombies may want different zone behaviour than a normal one; not covered
   by a single table.
5. **Where the multiplier is applied.** It has to land in one place all three paths reach, or the
   modes drift. `ZombieSim.Damage` already receives the limb and is engine-free — the natural home,
   but the two node paths (`ZombieController.DamageHit`, SP and MP) do not route through it.

## Test shape

Whatever lands needs a check that **fails when the multiplier is removed** — the current
`IsHeadshot` is a live demonstration of how a wired-looking limb value passes review while doing
nothing. Assert on damage dealt, per zone, per path (SP node / MP node / MP sim), because those are
three separate code paths that today disagree silently. A test that only covers one of them would
pass against exactly the bug that exists now.
