# Helicopter physics — deep dive and where the current model stands

Read against `Vehicle.StepHeli` (`game/Vehicle.cs:2789+`) and the spec fields at `:654`. Companion to
`docs/heli-control-requirements.md`, which holds the control rules that were signed off in play and
that any rework must not break.

## What the model does today

```csharp
float spool = _rotorRpm * _rotorRpm;                                   // thrust ∝ RPM²   ✔ physical
float lift  = _heliThrust * spool * _inCollective * (0.20f + 0.80f * mainEff);
lift *= 1f - TiltThrustLoss * (1f - Mathf.Clamp(b.Y.Y, 0f, 1f));       // EXTRA tilt penalty
Vector3 t = b.Y * lift;                                                // thrust along the shaft ✔
// ...horizontal component then multiplied by ForeAftBoost / LateralBoost, vertical left alone
ApplyCentralForce(new Vector3(boosted.X, t.Y, boosted.Z) * Mass);
```

Three things here are already right, and are the reason the rework is a substitution rather than a
rewrite:

- **Thrust scales with RPM².** Rotor thrust really is proportional to the square of tip speed, so a
  cold start genuinely cannot lift and an engine cut leaves you coasting down rather than dropping.
- **Thrust acts along the shaft** (`b.Y`), so tilting the disc splits one vector — the free cosine.
  This is the equation strawberry described from feel: less lift and more drive as you tilt.
- **`HeliLevel = 0f` on every spec**, i.e. no auto-levelling, which is VoX's 01:15 rule already
  honoured. Attitude is state.

## The tension worth naming before changing anything

`TiltThrustLoss` takes a **second** bite out of lift beyond the cosine, and then `ForeAftBoost` /
`LateralBoost` multiply the horizontal component back **up**. Those two terms are fighting: one
punishes tilt, the other rewards it, and the net feel is whatever their tuning happens to land on.

In a physical model neither exists. You get:

```
thrust along shaft        T
vertical component        T·cos(tilt)     — the cosine, free
horizontal component      T·sin(tilt)     — the drive, free
opposed by drag           ½·ρ·v²·Cd·A     — which is what actually sets top speed
```

and forward speed settles where horizontal thrust equals drag. Today top speed is instead a **hard
clamp** (`Speed_Max`, because the MP envelope derives its cap from it), which is why acceleration
feels the same at 5 m/s and at 45 m/s right up until it abruptly stops.

**So the single highest-value change is: delete the two ad-hoc boost terms and the extra tilt
penalty, and add real drag.** That is fewer terms, not more, and it makes top speed an emergent
consequence of a number with a physical meaning rather than a tuning constant.

## What is genuinely missing, ranked by (feel gained ÷ risk of an unflyable aircraft)

| Effect | What it does | Verdict |
|---|---|---|
| **Parasitic + induced drag** | Sets top speed by equilibrium; makes deceleration real; costs lift in a climb | **Take.** Replaces a clamp with physics. Low risk. |
| **Translational lift (ETL)** | A rotor gets more efficient with forward airspeed (~8–12 m/s); real pilots feel a distinct "through ETL" surge and a settling on slowing down | **Take.** Big feel win, cheap: one curve on lift vs horizontal airspeed. |
| **Ground effect** | Extra lift within ~1 rotor diameter of the ground; makes landings cushion and hover-taxi easy | **Take.** Needs a ground raycast, which `GroundedByRay()` already provides the shape of. |
| **Main-rotor torque → yaw coupling** | The airframe wants to spin opposite the disc; the pedals hold it straight | **Take, carefully.** This is what makes a tail-rotor loss meaningful (strawberry already asked for "tail dead → spin"). Risk: a constant yaw bias is annoying if untuned, so scale it with collective. |
| **Dissymmetry of lift / retreating blade stall** | Roll-off and a hard speed ceiling at high forward speed | **Skip for now.** It duplicates what drag already gives (a speed ceiling) and adds an unrecoverable-departure mode a non-pilot cannot diagnose. |
| **Vortex ring state** | Descending fast into your own downwash → sudden lift collapse | **Skip, or make it survivable.** It is the single most realistic way to kill a player who did nothing obviously wrong. If it goes in, it needs a loud audible/visual tell and a forgiving exit. |
| **Blade-element AoA per element** | Full fidelity | **Skip.** Cost is high, and the visible result is mostly what ETL + drag already deliver. |

The pattern in that table is the actual design principle: **take the effects that make the aircraft
easier to read and harder to fly badly; skip the ones that make it unrecoverable for someone who is
not a pilot.** Every "skip" above is skipped for that reason, not because it is hard.

## Constraints the rework must not break

From `docs/heli-control-requirements.md`, all signed off in play:

1. **No auto-levelling.** Attitude persists without input (`HeliLevel` stays 0).
2. **Rotational inertia carries past stick centre** — damping, never a restoring force.
3. W/S = collective, A/D = pedals, mouse = cyclic; view matches the airframe exactly.
4. Deadzone between fore/aft and lateral cyclic; reduced sensitivity; light turbulence.
5. Rotor damage model: main low → less thrust; main dead → no climb; tail dead → spin, no climb;
   both dead → the airframe dies.
6. The 20% maneuverability nerf stays.

And two engineering constraints:

7. **The MP envelope derives its cap from `Speed_Max`** (`VehicleReplication`), so if drag replaces
   the clamp, the envelope must still bound what a client may claim — otherwise "realistic physics"
   becomes "the speed check no longer rejects anything".
8. **`StepHeli` runs in `_PhysicsProcess`** and must stay there; and every new term must be
   deterministic, or the bit-exact client-auth adopt path stops being bit-exact.

Constraint 7 is the one most likely to be missed: it is the seam where a physics change becomes a
netcode bug.

## Per-airframe balance

strawberry's mapping (2026-08-16 07:38) is real aircraft, so the new coefficients have real sources:
hind = Mi-24, orca = Ka-60, skycrane = S-64, hummingbird = Littlebird, huey = UH-1. Disc area, mass
and installed power for these are public, which means `HeliThrust` and the drag area can be
*derived* per airframe instead of hand-tuned — a skycrane should feel like a heavy lifter because its
numbers say so, not because someone picked 12.2.

Note the Ka-60 is **coaxial** in reality (no tail rotor, counter-rotating discs), so its torque
coupling and tail-loss behaviour would differ. Whether to model that or keep the fleet uniform is a
decision for the plan, not an implementation detail.
