# Helicopter controls — the stated requirements

Gathered from the Discord log rather than from memory, because the physics rework has to not
break feel that was already specified and signed off. Quotes are verbatim; the timestamps let you
find the surrounding conversation in `notes/chatlogs/discord.jsonl`.

## VoX — the flight model

- **2026-08-15 23:14** — "rust style helicopter controls (w for accelerating up, s for down, a and d
  [yaw])".
- **2026-08-16 01:10** — "less thrust from W, invert the roll direction (forward mouse moves
  forward)".
- **2026-08-16 01:15** — the load-bearing one: *"make sure that the flight model is that the
  vehicle's pitch and yaw are tracked as a **current value** and that the thrust applies in relation
  to that value. The mouse movements should impart changes on that value. **Right now your model
  keeps reverting the copter to upright even if no mouse is applied**"*.
  → **No auto-levelling.** Attitude is state, not a target to spring back to.
- **2026-08-16 01:16** — "make the flight physics realistic, with w and s corresponding to the
  throttle, a and d corresponding to the foot yaw controls, and the mouse movements corresponding to
  the flight stick."
- **2026-08-16 01:29** — one seat on the minicopter; "the player's view to tilt with the copter's
  roll and pitch"; and if neither W nor S is held the copter idles a little below hover.
- **2026-08-16 01:31** — "the player's view should exactly match the direction the minicopter is
  facing."
- **2026-08-16 02:05** — the second load-bearing one: *"the inertia should be on the heli, basically
  when you stop moving the mouse it should be like in real life if you returned the stick to neutral
  position, the vehicle itself has **rotational inertia** which will keep it rotating for a bit
  unless you counteract it with opposite stick input."*
  → Stick returns to centre; the airframe does not. Damping, not a restoring force.

## strawberry — the feel

- **2026-08-16 01:03** — a deadzone between fore/aft and left/right tilt; reduce upward thrust ~20%;
  **"reduce the upward thrust more when tilting forward, and increase the forward/back momentum when
  tilting forward/back"**; lower the stick sensitivity.
- **2026-08-16 01:51** — "adding inertia. joystick changes should feel slower, heavier and more
  sluggish. like the heli actually has weight. as well as minor turbulence at random intervals."
- **2026-08-16 08:14** — "nerf the maneuverability of all helis by like 20%" (applied).
- **2026-08-16 07:38** — balance each against its real airframe: hind = Mi-24, orca = Ka-60,
  skycrane = S-64, hummingbird = Littlebird, huey = Huey.
- **2026-08-16 02:48 / 04:25 / 06:03** — rotor damage model: main rotor low HP → reduced thrust; tail
  low → reduced turning; main dead → cannot gain height and sinks; tail dead → spin; both dead →
  kill the hull. Rotor RPM falls with damage; rotors stop when idle and on death.

## THE PART I GOT WRONG — corrected 2026-08-17 after review

I originally wrote here that strawberry's "reduce the upward thrust more when tilting forward, and
increase the forward/back momentum when tilting" was **not a fudge but the correct physics**, on the
grounds that a rotor thrusts along its shaft so tilting splits one vector into `T·cos(tilt)` up and
`T·sin(tilt)` forward. I concluded that "the existing feel requirements are the physics requirements"
and that a physical model would deliver the approved feel for free.

**That is backwards, and git proves it.**

`git log e52fa75d` — the commit that added `TiltThrustLoss`, `ForeAftBoost` and `LateralBoost` — is
timestamped **2026-08-16 01:24:54**, twenty-one minutes after her 01:03 message. And the parent
commit's lift code is:

```csharp
if (lift > 0f) ApplyCentralForce(b.Y * lift * Mass);      // e52fa75d^ -- what she was flying
```

That is the plain shaft vector. **The free cosine is exactly what she was already flying when she
asked for the change.** The operative word in her message is *more*: she asked for a departure from
the physical model, having flown it. `TiltThrustLoss = 0.55` and `ForeAftBoost = 1.65` are that
departure, and they are a signed-off playtest correction, not ad-hoc terms someone left lying around.

**And they are load-bearing for the spec, not just for feel.** Level flight requires `T·cos θ = g`,
so the maximum horizontal acceleration available is `√(T² − g²)` — and `LinearDamp = 0.35` opposes it:

| airframe | `√(T²−g²)` | drag at `Speed_Max` | reaches spec without the boost? |
|---|---|---|---|
| minicopter | 6.57 | 7.00 | **no** |
| skycrane | 7.27 | 7.70 | **no** |
| huey | 8.39 | 8.05 | barely |
| hind | 10.28 | 9.10 | yes |

Remove `ForeAftBoost` and the minicopter and skycrane cannot reach their own spec top speeds at all.

**The rule this restores:** where feel and physics disagree, **these quotes win** — which is what this
document already said everywhere else, and what I talked myself out of in this one section by
reasoning from equations instead of from the log. The equations were even right; they were just
answering a question nobody asked.

