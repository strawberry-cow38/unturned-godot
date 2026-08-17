# Helicopter physics rework — implementation plan (draft 1, pre-review)

Companion to `docs/heli-physics-deepdive.md` (what the model does now) and
`docs/heli-control-requirements.md` (the control rules signed off in play, which win any conflict).

## The thesis

**This is a substitution, not an addition.** The current model has two ad-hoc terms fighting each
other — `TiltThrustLoss` punishes tilt beyond the free cosine, `ForeAftBoost`/`LateralBoost` reward
it — and an excess-spring standing in for drag (see the corrections below). Replacing all four with one drag term makes the
model *smaller*, gives top speed a physical cause, and delivers the feel strawberry already asked
for, because the requested feel and the real equations are the same thing (see the deep dive).

Net change to term count: **−4 tuning constants, +3 physical ones** (drag area, ETL curve, ground
effect height), each of which has a real-world source per airframe.

## Corrections from reading the code before designing against it

Five things I assumed turned out to be wrong or already handled. Recording them because three would
have made this plan actively harmful.

0. **THE MODEL ALREADY HAS DRAG, and the whole fleet is calibrated against it.** My framing — "no
   drag below `Speed_Max`, then a wall" — is wrong. `Vehicle.cs:2309` sets **`LinearDamp = 0.35f`**
   on every heli, which is Godot's built-in `F = -damp · v`. And it is load-bearing: `Vehicle.cs:1732`
   states *"Thrust is **derived, not chosen**: terminal climb in this model is
   `(thrust - g) / LinearDamp`, so thrust = 9.8 + 0.35 × the real climb rate."*

   So every airframe's `HeliThrust` was solved from that damping constant against its real-world climb
   rate, and the fleet's relative performance rests on it. `HeliFlightTests` encodes the same
   arithmetic ("at T/W 1.20 and this drag the terminal climb is ~5.7 m/s").

   **This reframes Phase 1 completely.** It is not "add drag to a model that has none". It is
   **"replace LINEAR damping with QUADRATIC drag"** — changing the *power of v*, which is the part
   that is actually unphysical (air resistance goes as v², not v). And it means:
   - every `HeliThrust` must be **re-derived**, because its current value is a solution to an equation
     whose form is changing;
   - climb rates change unless the re-derivation targets the same terminal climb, so the existing
     climb bounds in `HeliFlightTests` are a real constraint, not incidental;
   - the honest benefit shrinks. Linear damping already gives an asymptotic approach to terminal
     velocity — so "acceleration tapers instead of hitting a wall" is **already true vertically**. The
     wall is only on the HORIZONTAL axis, where the excess-spring sits on top of the linear damp.

   **Recommendation for the fold-in: narrow Phase 1 to the horizontal axis.** Replace the
   excess-spring with quadratic fore/aft and lateral drag, leave `LinearDamp` doing the vertical, and
   re-derive nothing. That keeps every airframe's derived thrust valid, keeps the climb tests honest,
   and still buys the thing worth buying — a top speed that emerges from drag instead of a spring.

1. **The speed limit is NOT a hard clamp.** `Vehicle.cs:2964` applies a spring force proportional to
   the *excess* over `_speedMax`:
   ```csharp
   Vector3 excess = flat.Normalized() * (flat.Length() - _speedMax);
   ApplyCentralForce(-excess * Mass * 3.0f);
   ```
   So there is **zero drag below `Speed_Max`** and a strong restoring force above it. That is a more
   precise statement of the same problem — acceleration is undiminished all the way up and then hits
   a wall — and it means Phase 1 replaces a *force* with a *force*, not a clamp with a force.

2. **Angular control already uses real torque, not assigned angular velocity.** I suspected it
   violated VoX's rotational-inertia rule. It does not: the block at `:3045` is explicitly
   `REAL TORQUE, not an assigned angular velocity`, citing VoX's "not fake input inertia, real
   physics simulated inertia". **The requirement is already honoured and must not be disturbed.**

   However — the comment *above* that block still says *"Angular VELOCITY is driven toward the
   commanded rate rather than integrating torques"*, which is the old implementation and directly
   contradicts the code beneath it. **Fix the stale comment as part of this work.** This is the same
   failure that cost a day yesterday: `TryDrag`'s comment claimed the port had no equipment system
   long after `ESlotType` landed, and nobody re-read it.

3. **Torque coupling partly exists.** `TailLossTorque` (`:2999`) already applies unopposed yaw torque
   scaled by `spool * collective` **when the tail is dead** — including the cruel-but-correct detail
   that the collective you need to stay up is what worsens the spin. Phase 4 is therefore not "add
   coupling" but "**extend the existing coupling to the healthy case**, opposed by tail
   effectiveness", which is a much smaller and safer change than I had planned.

4. **Turbulence already exists** (`:3026`) and is added to the *command* rather than applied as a
   torque, so it rides the same inertia as pilot input. Leave it alone.

`_heliLevel` is confirmed 0 on every spec, with the term kept as a knob — no auto-levelling, as
required.

## Phase 1 — Drag replaces the excess-spring

Delete `TiltThrustLoss`, `ForeAftBoost`, `LateralBoost`. Apply thrust as the plain shaft vector
`b.Y * lift`, and add:

```
F_drag = -v̂ · ½ · ρ · |v|² · CdA          (ρ folded into CdA; no altitude model)
```

Split `CdA` into a fore/aft value and a larger lateral/vertical one, which is physically true (a
helicopter is streamlined forward and a barn door sideways) and *also* delivers what `ForeAftBoost`
was faking — forward runs build speed, sideways drift does not.

**Top speed becomes an equilibrium:** `T·sin(tilt_max) = ½ρv²·CdA_fwd`. Pick `CdA_fwd` per airframe
so that equilibrium lands on today's `Speed_Max`, so the fleet's balance is preserved on day one and
only the *approach* to top speed changes (asymptotic instead of a wall).

**Netcode constraint (the seam most likely to be missed):** `VehicleReplication`'s envelope derives
its cap from `Speed_Max`. Drag must not become "the speed check no longer rejects anything" — keep
the envelope clamp exactly as it is, as a *validation* bound, and let drag be what the simulation
actually does. If they ever disagree the envelope wins and logs.

## Phase 2 — Translational lift

A rotor is more efficient in forward flight. One curve on horizontal airspeed:

```
etl = 1 + EtlGain · smoothstep(EtlStart, EtlFull, |v_horizontal|)     // EtlGain ≈ 0.12–0.18
lift *= etl
```

`EtlStart ≈ 6 m/s`, `EtlFull ≈ 12 m/s` (real ETL is ~16–24 kt). Gives the distinct "through
translational lift" surge on accelerating away and the settling on slowing to a hover — the single
biggest feel win per line of code, and it is *forgiving*: it helps when moving, and its absence in a
hover is the state pilots already expect to be hardest.

## Phase 3 — Ground effect

Within roughly one rotor diameter of the ground the disc rides its own cushion:

```
h = ray distance below the hull
ige = 1 + IgeGain · (1 - clamp(h / RotorDiameter, 0, 1))²             // IgeGain ≈ 0.15
lift *= ige
```

Reuses the existing downward ray (`GroundedByRay`'s shape). Makes landings cushion, hover-taxi easy,
and a heavy lift off a confined pad genuinely harder — all in the forgiving direction.

## Phase 4 — Extend torque coupling to the healthy case

The dead-tail half already exists (`TailLossTorque`, `Vehicle.cs:2999`), scaled by
`spool * collective`. What is missing is the **always-on** coupling that a working tail rotor
*opposes*:

```
yawTorque += -TorqueCoupling · spool · collective · (1 - TailRotorNorm)
```

At `TailRotorNorm = 1` this is zero and nothing changes for a healthy aircraft; as the tail degrades
the coupling emerges continuously, and at 0 it reduces to exactly today's `TailLossTorque` behaviour.
That makes strawberry's "tail rotor hp low → reduced turning" and "tail dead → spin" one continuous
mechanism instead of a healthy case and a special case — and it means **the existing dead-tail
behaviour is the boundary condition the new term must reproduce**, which is a free regression check.

**Risk and mitigation:** an untuned constant yaw bias is maddening. Mitigate by scaling with
collective *change* as well as level, and by keeping the coupling small enough that a hover needs a
trim of pedal, not a fight. Ka-60/orca is coaxial in reality and would have no coupling at all —
**decision needed: model that, or keep the fleet uniform?** I lean uniform for now, flagged in-code.

## Phase 5 — Per-airframe coefficients from real aircraft

> **Correction found after the reviewers were briefed:** the fleet's `SpeedMax` is **already**
> derived from real aircraft. `Vehicle.cs:1687` reads *"the .dat says 16, but the fleet is balanced on
> the real UH-1's 222 km/h"*, and the heli specs carry 20/23/26 m/s rather than their `.dat` values.
> So this phase is not "derive speeds from real aircraft" — that is done. It is **derive `CdA` and
> `HeliThrust`** so that the drag equilibrium reproduces those already-correct top speeds. That makes
> the phase smaller and lower-risk than written, and it means Phase 1's calibration target is a number
> somebody already justified rather than one to invent.

Exact constants Phase 1 removes, for the implementer: `TiltThrustLoss = 0.55f` (`Vehicle.cs:46`),
`ForeAftBoost = 1.65f, LateralBoost = 1.15f` (`:50`), applied at `:2940` and `:2954-2956`.


strawberry mapped the fleet to real airframes (Mi-24, Ka-60, S-64, Littlebird, UH-1), so mass, disc
area and installed power are public. Derive `HeliThrust` and `CdA` from those rather than hand-tuning,
so a skycrane feels like a heavy lifter because its numbers say so. Keep the 20% maneuverability nerf
as a final multiplier on the torque values so it stays visible and revertible as one number.

## Explicitly NOT doing, and why

- **Retreating blade stall / dissymmetry of lift** — duplicates the speed ceiling drag already gives,
  and adds a roll-off departure a non-pilot cannot diagnose.
- **Vortex ring state** — the most realistic way to kill a player who did nothing obviously wrong. If
  it is ever added it needs a loud tell and a forgiving exit.
- **Blade-element AoA per element** — high cost, and the visible result is what ETL + drag already
  deliver.

Every one of these is skipped for *playability*, not difficulty. The principle: take the effects that
make the aircraft easier to read and harder to fly badly; skip the ones that make it unrecoverable.

## Preserved without exception

`HeliLevel` stays 0 (no auto-levelling). Angular damping stays damping, never a restoring force, so
rotation carries past stick centre. W/S collective, A/D pedals, mouse cyclic, view locked to the
airframe. Deadzone, reduced sensitivity, turbulence. The whole rotor-damage model. `StepHeli` stays
in `_PhysicsProcess` and every new term stays deterministic, or the bit-exact client-auth adopt path
stops being bit-exact.

## How each phase gets proven

Each phase is a separate commit with an L1 suite, and each check asserts a **sign or a difference**,
not a magnitude that a mirrored implementation would also satisfy:

- **Drag:** from a fixed tilt, terminal speed converges and stays within tolerance of the predicted
  equilibrium; and acceleration at 0.5·v_max is strictly greater than at 0.95·v_max (the asymptote —
  the thing the clamp could not produce).
- **ETL:** lift at 12 m/s horizontal is strictly greater than at 0 m/s for identical collective and
  attitude. Control: with `EtlGain = 0` the two are equal.
- **Ground effect:** lift at h = 2 m strictly greater than at h = 50 m, same inputs. Control: equal
  when `IgeGain = 0`.
- **Torque coupling:** with zero pedal input and a healthy tail, heading holds within a small band;
  with `TailRotorNorm = 0`, yaw rate grows monotonically. Both directions, so a sign flip fails.
- **No regression in feel:** the existing heli suites (`HeliFlightTests`, `HeliPartsTests`,
  `VehicleSeatTests`) stay green, and the MP envelope tests keep rejecting an over-speed claim.

Teeth check for every one of the above: revert the phase's term and require its own check to fail.

## Open questions for the reviewers

1. Coaxial orca — model the absent tail rotor, or keep the fleet uniform?
2. Is `Speed_Max`-preserving drag calibration the right call, or should the fleet's top speeds move
   to their real-aircraft values (and the MP envelope move with them)?
3. Is ground effect worth a second raycast per heli per physics tick, or should it reuse the existing
   ground ray's result even when that is a tick stale?
4. ~~Does anything else derive from `Speed_Max`?~~ **Answered by grep, not by asking.** For the
   ROTARY path the only consumers are `VehicleNetSync.cs:165` (which passes `SpeedMaxMps`,
   `ClimbMaxMps`, `FallMaxMps` into the server envelope) and the excess-spring at `Vehicle.cs:2964`.
   Every other `_speedMax` use (`:3116`, `:3132`, `:3135`, `:3418`) is on the WHEELED path — engine
   cutoff, steer-target scaling, `ForwardSpeedPct` — and `StepHeli` returns before all of them. So
   drag-based top speed touches exactly two places, both listed here.
