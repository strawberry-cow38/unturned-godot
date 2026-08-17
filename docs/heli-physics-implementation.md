# Helicopter physics rework — implementation record

Phase 1 of `heli-physics-plan.md`, after folding the three review passes. Written as a record of what
changed and *why the plan changed shape*, because two of the plan's premises did not survive contact with
the code and the reasons are worth more than the diff.

## What shipped

| | before | after |
|---|---|---|
| Horizontal resistance | linear, `LinearDamp` (isotropic engine damping) + a spring at `Speed_Max` | **quadratic**, anisotropic, hand-rolled |
| Vertical resistance | the same isotropic `LinearDamp` | linear heave damping, world-aligned, hand-rolled |
| Fore/aft vs lateral asymmetry | `ForeAftBoost 1.65` / `LateralBoost 1.15` multiplying **thrust** | `HeliLateralDragRatio 2.5` multiplying **drag** |
| Top-speed limiter | the spring, at exactly `Speed_Max`, the only limiter | drag sets it; the spring survives as a **backstop** at `1.15 × Speed_Max` |
| Drag coefficient | — | **derived** at build time from `HeliThrust` and `Speed_Max`, not typed |

`TiltThrustLoss` is untouched. It encodes strawberry's verbatim instruction ("reduce the upward thrust
more when tilting forward"), and the review was right that removing it is a request for a sign-off, not a
physics argument.

## Two axes, two laws — and why that is not an inconsistency

The horizontal is quadratic because it is dominated by fuselage **parasite drag**. The shaft axis is not:
it is dominated by **rotor heave damping** — a rotor climbing sees reduced inflow through the disc, blade
angle of attack rises, and thrust rises with it. That restoring force is linear in axial velocity to first
order (the `Z_w` stability derivative). Modelling both axes with one law is what would be inconsistent.

**Departure from the physics review, stated because it was a real disagreement.** The review recommended
putting heave damping on the *body* shaft axis `b.Y`, since heave damping is a rotor property and follows
the disc. It is world-aligned here instead. The body-frame form scales vertical damping by `cos²(tilt)`,
which silently retunes every number derived from that constant — terminal climb rises ~22 % at an ordinary
25° cruise, straight into `HeliClimbMax` and the server's **zero-slack** vertical check. World-aligned keeps
the review's own stronger requirement ("change nothing on the vertical axis except who applies the force")
literally true. The cost is a coordinate artefact at extreme bank; the benefit is six calibrated numbers
staying valid.

## The drag coefficient is derived, not tuned

`LevelFlightAccel(thrust)` sweeps attitude to find the steepest lean whose remaining vertical thrust still
holds the machine up, and returns the horizontal acceleration it buys. Then `k = a / Speed_Max²`, which is
the equilibrium condition `a = k·v²` solved so that **level-flight terminal speed is exactly `Speed_Max`**.

This is deliberate, and the reason is that no real-world number could have supplied it. Every vehicle in
this game has `GlobalMass = 900` regardless of what it is, and ρ and mass are both folded into the
coefficient — so a hand-tuned table would be seven magic numbers whose rank is *inverted* against real
aircraft (the scrap minicopter ends up the draggiest, the Hind the least), and whose provenance dies with
the commit. Deriving from the two authorities that already exist means retuning either one carries the
drag along with it.

Not a closed form because `TiltThrustLoss` sits inside the tilt term; a tenth-of-a-degree sweep runs once
per spec at build time.

## The bug this uncovered: the fleet was never flying at 0.35

`project.godot` never overrides `physics/3d/default_linear_damp`, so Godot's default of **0.1** applies —
and `linear_damp_mode` defaults to `Combine`, which **adds** the body's value to it rather than replacing
it. The helicopters were set to `LinearDamp = 0.35` and were therefore actually running at **0.45**.

Measured, not inferred: with the body value at 0, the fleet still showed exactly `0.100 s⁻¹` of residual
horizontal damping, agreeing to three digits across hind, orca and hummingbird independently. That
residual was what left the three fastest airframes short of their own spec top speed, and it is why
`LinearDampMode` is now set to `Replace`.

**The consequence is left in place deliberately, and needs a decision.** The `HeliThrust` derivation table
at the Huey spec computes each airframe's thrust as `g + 0.35 × (that aircraft's real climb rate)`. Against
the 0.45 actually in force, **the whole fleet climbs about 22 % slower than the real machines it was
derived from**. So `HeliHeaveDamp` is set to 0.45 — preserving shipped behaviour exactly — rather than to
the 0.35 the comments claim. Correcting it is a change to the vertical axis and to numbers signed off by
feel; doing that quietly inside a horizontal-law rework is exactly the kind of change that compiles and
ships wrong.

> **Open question for VoX / strawberry:** climb rates are currently ~22 % below what the derivation table
> intends. Fix it (set the constant to 0.35, fleet climbs faster, matches the real aircraft) or keep
> today's feel and correct the table's comment instead?

## The instrument

`vehicle.heli_speed` (`HeliSpeedTests.cs`) — new, because **nothing in the repo measured a helicopter's
achieved horizontal speed**. The existing fleet check reads `SpeedMaxMps` off the spec field, which is the
calibration's *input*; it stays green with the achieved speed landing anywhere at all.

The load-bearing check is **convexity**, and the obvious version of it is a trap worth recording: *"acceleration
is lower at high speed than at low speed"* does **not** distinguish the old model from the new one, because
`LinearDamp` is itself a velocity-dependent drag. Acceleration already tapered. That check would have
passed before and after and proved nothing. What separates the laws is the *second* difference — sample
acceleration at three evenly spaced speeds, and under `a = A − c·v` the two decrements are equal, while
under `a = A − k·v²` the upper one is larger by a computable factor.

Run against the pre-rework model on purpose: **36 magnitude checks passed and convexity failed at 1.08**,
against a quadratic prediction of 1.51 and a linear prediction of 1.00. The green state is not one the old
model could have reached by accident.

Two rig defects found along the way, both of the same family — an instrument that returns a confident
number to the wrong question:

- The first rig pitched to attitude and **released the stick**. Attitude is state, but the model converges
  angular *velocity* toward the commanded rate and leans on `AngularDamp` to bleed the residual, so
  releasing at the commanded rate keeps rotating. It measured a tumbling helicopter falling 880 m: full
  health, no crash, and a plausible-looking speed that meant nothing. The rig now holds attitude on the
  stick with a PD controller, as a pilot does.
- The velocity-assignment windows were justified as "all three are speed increases, so the crash detector
  cannot fire" — reasoned about **flat** speed, while the detector uses **full 3-D** speed. In a 45° dive
  zeroing the vertical is a large 3-D drop; it bonked the rig and damaged the airframe whose acceleration
  was being measured. Only the horizontal component is assigned now.

## What is not done

Phases 2a, 2, 3 and 4 of the plan remain: multiplicative lift invariants, translational lift, ground
effect, and torque coupling. The reviews attached hard constraints to each — an ETL gain of ≤ 0.087, a cap
on the ETL × ground-effect product per airframe (the Hind busts its climb envelope at 1.18), ground effect
needing a new terrain-only raycast because `GroundedByRay` casts 1.4 m against a rotor diameter of 11 m and
uses the *vehicle* mask bit — and none of those should be taken on faith either.

**Feel change to flag before this is called done:** removing `ForeAftBoost` costs about 2.6 m/s² of
low-speed forward acceleration, and removing the linear horizontal damping gives back about 1.3 m/s² — a
net ~1.3 m/s² less punch off the mark at 5 m/s, in exchange for an honest asymptotic approach to a top
speed that is now correct on every airframe. That is a trade strawberry should get to look at, not one to
assume.
