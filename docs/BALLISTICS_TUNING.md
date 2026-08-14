# Ballistics tuning — notes

**Status: notes only.** Nothing here is implemented. Sequencing is strawberry's: *"lets normalize the
top end first"*, per-gun, not a sweep.

## Decisions so far

strawberry, 2026-08-14:

> *"gravity 1x. we're doing per-gun tuning."*

> *"railguns shoots almost completely flat. its intended as a super rare late game weapon. its not
> intended to replace snipers, but acts as a counter to armored vehicles. high damage, and a small
> aoe explosion at impact point."*

Gravity goes from ×4 to ×1. Velocity is then tuned **per gun**, starting at the top end (the 300 m
rifles and the railguns) rather than all 54 at once.

## Railgun — the spec

| | |
| --- | --- |
| trajectory | almost completely flat |
| role | counter to armoured vehicles |
| **not** | a sniper replacement |
| rarity | super rare, late game |
| damage | high |
| on impact | small AOE explosion |

The important line is *not a sniper replacement*. Flat trajectory is the railgun's identity, which
means the snipers must **keep** a visible arc — if a sniper is also flat, the railgun's defining
property stops distinguishing it and it becomes a strictly-better sniper. So "very little drop" is a
railgun property, not a top-end property.

Drop at 300 m, gravity ×1, through the port's own integrator:

| velocity | drop at 300 m |
| --- | --- |
| 900 m/s (real .338/.408/.50) | 51 cm |
| 1500 m/s | 18 cm |
| 2000 m/s | 10 cm |
| 3000 m/s | 4 cm |

2000 m/s reads as "almost completely flat" at 300 m — a tenth of a metre over the longest shot the
map allows — while leaving snipers a 51 cm arc to learn. Not yet agreed.

## What the railgun already has

`shadowstalker.dat` — `Action Rail`, `Range 300`, `Ballistic_Travel 12.5` (625 m/s), `Firerate 50`
(0.98 s between shots), `Player_Damage 99`, **`Vehicle_Damage 250`**, `Resource_Damage 250`,
`Caliber_Name "Railgun Slug"`.

So the anti-vehicle role is **already half built**: 250 vehicle damage against an eaglefire's 35.
What is missing against the spec is the flat trajectory (it is 625 m/s today, slower than a real
.338) and the impact AOE.

## What an impact AOE would take

There is working explosion machinery — `ExplosionMath.Linear` (`core/UnturnedSim/CombatMath.cs:12`)
for falloff and `ExplosionBlocked` for line-of-sight — and one gun already uses it. But the wiring is
a hardcoded special case:

```
PlayerController.cs:4529
if (Gun?.Action == "Rocket") Explode(point, 9f, 250f, 200f, 300f);
```

Radius and damages are literals in the fire path, keyed off `Action`. The rocket's own
`Explosion 45` key in the `.dat` is **not parsed by `GunDef` at all**.

So a railgun AOE is either a second hardcoded branch (`Action == "Rail"`, cheap, and the second copy
of a pattern that should not be copied twice) or the point at which blast radius/damage become real
`.dat` fields both guns read. The second is the right shape and is barely more work — one parse, one
lookup, and the rocket stops carrying magic numbers.

## Blocker: per-gun stats do not reach multiplayer

`ServerCombat.SetGunProfile` (`core/UnturnedNet/ServerCombat.cs:209`) has **zero callers** — nothing
in the game or the tests ever writes `_gunByPlayer`. `GunFor(playerId)` therefore always returns
`DefaultGun`, a hardcoded eaglefire-shaped profile: 500 m/s, gravity ×4, 40 player damage, firerate
4, mag 30, 20 ballistic steps.

**In multiplayer every gun is an eaglefire on the server.** Whatever gets tuned per gun will apply in
singleplayer and be silently ignored in MP — including the railgun, which would fire an eaglefire's
bullet at an eaglefire's velocity for an eaglefire's damage.

This has to be wired before per-gun tuning means anything in MP. It is the same shape as the
`IsHeadshot` finding in [ZOMBIE_HITZONE_PLAN.md](ZOMBIE_HITZONE_PLAN.md): a seam that exists, reads
as wired, and is never driven.

## Bows and the crossbow need `Range` cut, not just velocity

Real archery velocities cannot be dropped into the current ranges. A compound bow is ~90 m/s; over
its declared `Range 200` at gravity ×1 that is **~24 m of drop**. The 200 m range only works today
because 250 m/s is equally fictional. Either they keep fantasy velocities or their ranges come down
to something archery-shaped (40–70 m). Not a velocity edit on its own.

## Also worth knowing

- **Nothing needs to be flat past 300 m.** Props region-cull at 447 m (`LodBaker.cs`), and no gun's
  bullet outlives its own `Range` — snipers 300, rifles 200, pistols 100–125.
- **Gravity ×1 is global by nature.** Even with no velocity edits it flattens every gun immediately:
  a 500 m/s gun goes from 298 cm of drop at 200 m to 71 cm. Sequencing the velocity work per gun does
  not sequence the gravity change.
- **`Ballistic_Steps` must move with `Ballistic_Travel`.** `GunDef.ComputeBallistics` uses both as
  given when both are declared, so raising travel without recomputing steps extends a gun's real
  reach past its `Range`. Deleting the `Ballistic_Steps` key instead lets it derive as
  `ceil(Range / travel)`, which preserves the declared range exactly.
