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

## The part worth noticing before designing anything

strawberry's "reduce the upward thrust more when tilting forward, and increase the forward/back
momentum when tilting" is **not a fudge — it is the correct physics**, and the requested feel and
the real model agree here. A rotor produces thrust along its own shaft, so tilting the disc splits
one vector:

```
vertical   = T · cos(tilt)      ← lost lift when tilted, exactly as asked
horizontal = T · sin(tilt)      ← the forward drive, exactly as asked
```

That means a genuinely physical model delivers the feel that was already approved, rather than
fighting it. The same is true of VoX's two rules: an attitude that persists without input and keeps
rotating after the stick centres is what a rigid body with angular momentum does when you stop
applying a moment. **The existing feel requirements are the physics requirements.** Anywhere the
rework has to choose, these quotes win — they were signed off in play.

Open question for the rework, not settled here: how far to take blade-element effects (angle of
attack per blade element, translational lift, retreating-blade stall, ground effect, vortex ring
state). Each adds fidelity and each adds a way for the aircraft to become unflyable for a player
who is not a pilot. That trade is the substance of the plan, not an implementation detail.
