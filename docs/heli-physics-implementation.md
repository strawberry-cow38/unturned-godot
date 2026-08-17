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

## Phases 2 and 3: translational lift and ground effect

Both are multipliers on rotor thrust, both are applied **above the dead-tail clamp**, and both are capped
as a *product* by a per-airframe ceiling derived from that airframe's own `HeliClimbMax`. Capping each
factor separately would let the product through, and the product is what out-climbs the envelope: ETL 1.05
× ground effect 1.333 = 1.40 against the Hind's ceiling of 1.26.

**The ordering is the load-bearing part and it was teeth-checked.** The dead-tail clamp is an absolute
ceiling encoding a signed-off rule; a multiplier applied after it lifts the machine straight back through.
Moving the multipliers below the clamp makes a dead-tail helicopter climb +2.3 m and still be rising at
+0.24 m/s at the end of the window — and exactly the two ordering checks go red while the rest stay green.

**ETL is 0.05, not the 0.087 the algebra allows, and the gap was measured.** The bound comes from the
hands-off sink: idle lift is `9.8 × 0.92 = 9.016`, so a gain of `9.8/9.016 − 1 = 0.087` cancels it. At
0.08 — comfortably "under the bound" — a Huey hands-off at 20 m/s **climbed at +0.14 m/s**. The bound
assumes the collective sits exactly on its spring target; it settles about a percent above, which is more
than the 0.06 m/s² of margin 0.08 leaves. A limit derived from an idealised state needs headroom for the
state actually reached.

**Ground effect broke a signed-off behaviour, and the fix was not to shrink it.** Cheeseman-Bennett at full
strength gives 1.333 near the deck, which takes the idling collective's 9.016 up to 12.0 against a g of
9.8 — so a parked helicopter with the engine running slowly flies away. It surfaced as a failure in the
*turbulence* test, whose grounded subject stopped being grounded.

The obvious fix is to clamp ground effect to ~1.06, the largest value leaving the 8.7 % idle margin intact
— which preserves the behaviour by deleting the feature. Instead `HoverCollective` now accounts for ground
effect, so the hands-off trim targets the thrust needed *right here*. Hands-off lift is then `0.92 × g`
exactly at any height, while collective the pilot actually pulls still gets the full cushion. That is also
what a trimmed control does in reality.

The probe is its own raycast reaching two rotor radii. `GroundedByRay` could not serve: it reaches ~1.4 m
against an 11 m rotor. **Correcting the review on one detail** — it called that cast's `1<<5` the vehicle
bit, which is right, but bit 0 does not exclude vehicles either: vehicle bodies sit on `bit0|bit5`. So the
mask is bit 0 and it *does* see vehicles. That is left deliberately — a surface under the disc is a
surface, and a helicopter hovering low over a truck really is in ground effect. Only a *landing* test needs
that distinction. Props (bit 6) are excluded, since a bush is not something downwash builds a cushion on.

## What the three-agent implementation review changed

Three reviewers (physics correctness, integration risk, adversarial test quality) read the two commits
independently. Their convergence was the useful signal: **three findings were reported by all three**, and
those three were all real.

| Finding | Reported by | Outcome |
|---|---|---|
| Ground effect measured from the fuselage origin, not the rotor disc | all 3 | fixed — casts from `_mainHubCentre` |
| Drag derived from unboosted thrust while ETL is saturated at every cruise speed | all 3 | fixed — derives against `thrust × (1 + EtlGain)` |
| Stale numbers in comments (1.44, 0.08, "~28 m/s") | all 3 | fixed |
| `HoverCollective` divides by *raw* ground effect while lift uses the *capped* product | 2 of 3 | fixed — new `_geApplied` |
| No level-flight test; the central derivation was unmeasured | 2 of 3 | fixed — new check, teeth-confirmed |
| Convexity check tolerates a residual linear term up to ~0.25 s⁻¹ | 1 | fixed — now solves for it directly |
| `HeliLateralDragRatio` had zero coverage | 1 | fixed — new ratio check |

Three of these deserve recording in more detail, because of *why* they were invisible.

**The ground-effect probe measured from the wrong point.** Cheeseman-Bennett's `z` is the height of the
disc; the hub sits 1.12–4.18 m above the body origin depending on airframe. Measuring from the origin
overstated the cushion by 11–22 % on the deck and shifted the whole decay curve upward, so a Hind kept a
meaningful boost until its fuselage was at two rotor radii — disc nearly three. It also made every airframe
pin to the `R/2` clamp while parked, so the clamp was silently standing in for the geometry. No test could
see it because every comparison was *within* one airframe, where a constant offset cancels.

**The trim cancelled the wrong quantity.** `HoverCollective` divided by the raw ground-effect factor while
the lift path multiplied by the *capped* product. When the cap binds those are different numbers, and the
error runs the wrong way: on a Hind parked in ground effect the hands-off sink came out **63 % harder**
near the deck than at altitude — ground effect inverted, in the flare. The parked-machine test could not
catch it because it uses a minicopter, whose cap is 1.586 and can never bind. A check whose failure state
is unreachable for the mechanism it guards is not a check.

**The drag calibration was never measured.** Every window in the speed suite was a 45° dive, which settles
against the 1.15 backstop rather than against drag — so halving the drag coefficient left the entire suite
green. The fix is a level-flight check flown with an integral trim controller, since "level flight" is a
constraint rather than an attitude: driving vertical speed to zero at full collective converges on exactly
the attitude the coefficient is derived against. Teeth confirmed — with the coefficient halved both new
checks go red (1.179x, 1.176x) while every dive check still passes.

### A correction against myself

The commit message for phase 2 and the `EtlGain` docstring both claimed a measurement: that at gain 0.08 a
Huey hands-off at 20 m/s **climbed** at +0.14 m/s, blamed on the collective settling above its spring
target. The adversarial reviewer showed that reading was a rig artefact — the test zeroed vertical velocity
while the collective was still at full, so the window opened with ~1.4 m/s of climb decaying on a 2.2 s
time constant, and 4 s was not long enough to settle. The offered mechanism was wrong too: `DriveHeli`
converges with `MoveToward`, which cannot overshoot.

The number was real and it answered a different question — "what is this machine doing one time constant
in", not "where does it settle". Steady state at 0.08 is a *sink* of 0.139 m/s.

**0.05 is still the right value, for a different reason.** The 0.087 bound is where the sink inverts, and
the behaviour dies well before that: at 0.08 the settled sink is 1.4 m over ten seconds, a hover with a
rounding error rather than the "gentle sink" that was asked for. At 0.05 it is 0.74 m/s. The window is now
10 s (4.5 time constants) and the check asserts a *meaningful* sink.

## What is not done

**Phase 4, torque coupling**, is deliberately not implemented. It would make collective input yaw the
airframe, requiring constant pedal correction — a change to how the machine is *flown*. VoX's brief was
explicit that the control feel is settled ("remember everything I said about the controls"), so this is a
sign-off, not an implementation detail. The review also found the plan's own equation contradicted its
prose and flipped the sign against the existing dead-tail term, so it should not be taken from the plan as
written either.

**Feel change to flag before this is called done:** removing `ForeAftBoost` costs about 2.6 m/s² of
low-speed forward acceleration, and removing the linear horizontal damping gives back about 1.3 m/s² — a
net ~1.3 m/s² less punch off the mark at 5 m/s, in exchange for an honest asymptotic approach to a top
speed that is now correct on every airframe. That is a trade strawberry should get to look at, not one to
assume.
