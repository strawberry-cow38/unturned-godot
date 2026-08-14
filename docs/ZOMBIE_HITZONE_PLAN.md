# Hit zones — two damage models, one zone geometry

**Status: notes only.** Nothing here is implemented.

strawberry, 2026-08-14:

> *"so i think almost all guns should 1 shot headshot zombies. and take considerably more shots on
> the body, even more on limbs. just take notes for now"*

> *"okay. two damage models. player and zombie. forget the damage models we have rn. identical
> hitbox zones."*

## The shape

**One zone resolver. Two multiplier tables.** A hit resolves to head / torso / limb by the same
geometry whoever was hit; what that zone is *worth* is looked up in a per-target-kind table.

| | zones | damage model |
| --- | --- | --- |
| player | shared | player table |
| zombie | shared | zombie table |

The existing numbers are discarded — both the player table (`3.0 / 1.0 / 0.6`) and the flat
`Zombie_Damage`. They are recorded below only as the starting state to be replaced.

Zombie target, from the first message: head kills in one for almost every gun, body takes
considerably more shots, limbs more still.

## What exists today (to be replaced)

There is a limb table, and it applies to **players in multiplayer only** —
`core/UnturnedNet/ServerCombat.cs:38-40`: `HeadMult 3.0`, `TorsoMult 1.0`, `LegMult 0.6`. Hardcoded,
not per-gun, not read from any `.dat`. Zones resolve by hit height against the avatar: head ≥ 1.45 m,
torso ≥ 0.78 m, legs below (`ServerCombat.cs:389`).

**Zombies get no multiplier on any path.** Three paths, three different ways of not applying one:

| path | site | what happens |
| --- | --- | --- |
| singleplayer | `game/PlayerController.cs:4448` | `z.DamageHit(b.Damage, …)` — flat. `IsHeadshot` feeds only the hitmarker colour |
| MP, node-backed zombie | `game/ZombieNetSync.cs:199` | `t.Brain.DamageHit(damage, …)` — the `headshot` bool is dropped entirely |
| MP, sim zombie | `core/UnturnedSim/ZombieSim.cs:834` | limb IS passed in, recorded to `_killedBy[row]`, never multiplies |

The sim path is the one to be careful about: it threads `ZombieLimb.Skull` vs `Spine` all the way
through the call, so the code reads as wired. The value is used for kill attribution and nothing
else. The call site cannot tell you that; only the implementation can.

A zombie headshot is therefore a red hitmarker and nothing else, in every mode.

## Why the body number is the work, not the head

`Zombie_Damage` is ≥ 99 on **37 of 54 guns**. Most weapons already one-shot a normal zombie wherever
they hit, so the missing head multiplier is invisible — the head result is already correct, by
accident. Adding a head multiplier alone would change nothing observable. Body damage has to come
down far enough that a torso hit takes several rounds before a head multiplier means anything.

## Open, and blocking

1. **"Identical hitbox zones" — by absolute height, or by fraction of collider?** They agree on a
   1.8 m zombie and diverge completely on a 0.8 m crawler. Absolute (the player's 1.45 m / 0.78 m)
   puts a crawler's entire body below the torso line: **every hit on it is a limb hit and it has no
   head at all.** By fraction it keeps a head zone, ~14 cm tall and near the floor. Neither is
   obviously right and the choice decides whether crawlers are trivial or nearly unkillable.
2. **The numbers themselves.** Both tables are being replaced, so head/torso/limb multipliers and the
   base damage they multiply need setting together — a one-shot-head rule is a statement about the
   product, not about the multiplier.
3. **Which guns are "almost all".** On any sane multiplier the `.22` (`sportshot`, 32) and the LMGs
   (`fury` 25, `nykorev` 33, `dragonfang` 38) will not reach a kill. Presumably the "almost".
4. **Per-gun or global.** The player table is a global constant today. Retail carries per-gun damage
   multipliers; this port parses none.
5. **Specials.** Mega/boss zombies may want their own table rather than sharing the zombie one.
6. **Where it lives.** The three zombie paths never meet, so a multiplier added to one silently does
   not apply in the others. `ZombieSim.Damage` already receives the limb and is engine-free, which
   makes it the natural home — but neither node path routes through it. Unifying that is most of the
   work.

## Test shape

Whatever lands needs a check that **fails when the multiplier is removed** — today's `IsHeadshot` is
a live demonstration of a limb value that reads as wired and does nothing. Assert damage dealt, per
zone, **per path** (SP node / MP node / MP sim), because those three disagree silently right now and
a test covering one of them would pass against exactly the bug that exists. Include a crawler case
once decision 1 is made; it is the input that separates the two readings of "identical zones".
