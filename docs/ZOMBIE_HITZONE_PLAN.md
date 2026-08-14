# Hit zones — two damage models, one zone geometry

**Status: notes only.** Nothing here is implemented.

strawberry, 2026-08-14:

> *"so i think almost all guns should 1 shot headshot zombies. and take considerably more shots on
> the body, even more on limbs. just take notes for now"*

> *"okay. two damage models. player and zombie. forget the damage models we have rn. identical
> hitbox zones."*

> *"hitboxes are parented to their physical body part owners? if that makes sense. a crawler IS a
> normal zombie but hitboxes are scaled/moved/rotated to fit"*

## The shape

**Hitboxes are parented to bones, not to heights.** A head hit is a hit on the collider riding the
skull bone, wherever the skeleton has put it. A crawler is a normal zombie whose hitboxes are
scaled / moved / rotated to fit its pose — not a different zone scheme, the same one on a different
skeleton.

This deletes the absolute-vs-fraction question rather than answering it: with no height thresholds
there is nothing to disagree about. It also deletes the height bands for players
(`PlayerHeadMinY` / `PlayerTorsoMinY`), which are the same trick with the same weakness — they are
wrong for anyone prone, crouched, or on a slope, for exactly the reason they are wrong for a crawler.

**One zone geometry. Two multiplier tables.** A hit resolves to head / torso / limb by the same
means whoever was hit; what that zone is *worth* is looked up in a per-target-kind table.

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

## The blocker: the authoritative path has no bones

Bone-parented hitboxes are a scene-tree concept — they need a skeleton, posed, in a tree. **The
server's authoritative path for sim zombies has none of that, by design.** A sim zombie has no node,
no skeleton and no collider; that is the entire point of the sim rewrite, and it is why
`ServerCombat` resolves hits analytically against a capsule (`ZombieZoneRadius 0.4`,
`ZombieHeadFrac 0.82`) and why `PlayerController.StepBullets` tests bullet segments against the sim
rather than raycasting.

So "hitboxes parented to body parts" is implementable on the node-backed paths (SP, and MP zombies
that have been promoted to nodes) and **not** on the path that decides MP damage. Options, none free:

- **Sim carries a limb model.** The sim already knows each zombie's pose enough to animate it; a
  small set of per-limb capsules derived from that, evaluated analytically, mirrors the bone layout
  without a scene tree. Keeps the sim engine-free. Most work, best fidelity-per-cost.
- **Promote to nodes within combat range.** Bones become real, hit resolution is exact, and the cost
  is precisely the one the sim rewrite existed to remove — at the range where the most zombies are.
- **Server keeps an approximation.** Node paths get true bone hitboxes, the server keeps a coarser
  analytic model. Cheapest, and it reintroduces the SP/MP divergence this plan exists to remove:
  the same shot would resolve to different zones in the two modes.

Whatever is picked, the answer has to be the same in all three paths or the modes drift again.

## Material that already exists

- `RiggedCharacter` builds a `PhysicalBone3D` per bone for the ragdoll (`RiggedCharacter.cs:641`),
  so per-bone shapes and their fitting already exist — the ragdoll set is a plausible seed for the
  hitbox set rather than authoring a second one.
- `BoneAttachment3D` is already used for gear (hat / mask / glasses on `Skull`, vest / backpack on
  `Spine`), which is the same parenting mechanism a hitbox would use.
- A live zombie's collider today is a **single capsule** (`ZombieController.cs:110`, height 0.8 or
  1.8, radius 0.4). That is the thing being replaced by a set.

## Open, and blocking

1. **How the server resolves zones** — see the blocker above. Everything else is tuning; this one is
   architecture, and it decides whether MP damage can agree with SP at all.
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
a test covering one of them would pass against exactly the bug that exists. Include a crawler case: it is the pose that
separates real bone-parented hitboxes from any height-band approximation, so it is the test that
catches a server quietly falling back to the old model.
