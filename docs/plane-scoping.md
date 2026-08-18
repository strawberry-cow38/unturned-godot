# Fixed-wing aircraft — scoping

Groundwork for strawberry's "plane time". Not a plan yet: this records what exists and what retail
actually does, so the decisions below are made against facts rather than guesses.

## What we have

**Three plane meshes, as static scenery props**, not vehicles: `Plane_1/2/3.obj` in
`game/content/objects/` (plus `_lod1` and `_tex.png` for each). Roughly **26 m wingspan** by 19–23 m long,
7.4–7.9 m tall — a small airliner, not a light aircraft. `Plane_2` and `Plane_3` share geometry (1188
verts) and differ only in texture; `Plane_1` is a separate, simpler mesh (776 verts).

Same situation as the container ship: the art is there, the vehicle is not.

Retail's own fixed-wing is the **Otter**, which `WorldBuilder.cs:1358` currently skips along with the Huey
(`if (type > 5) continue;` drops the air tables), so no aircraft spawns naturally on PEI today.

## What retail actually does — `EEngine.PLANE`

Read from `InteractableVehicle.cs:3533` for behavioural rules and values only. Worth writing down because
it differs from the helicopter in ways that are easy to assume wrong:

1. **Thrust is along body FORWARD**, not body up. The helicopter's whole feel — tilt and the lift vector
   takes you with it — does not transfer. A plane pushes forward and the wing does the rest.
2. **Lift ramps with forward speed**: `Lerp(0, 1, localVelocity.z / TargetForwardVelocity) * asset.lift`,
   applied along **world up** (`-Physics.gravity`), *not* body up. So in retail a banked plane keeps its
   full lift. That is unphysical — a real bank spills lift by `cos(roll)` and is how you turn — and it is
   the first thing to decide rather than inherit.
3. **No control authority on the ground.** Torque is applied only when the wheels are not grounded, so the
   nose cannot be yanked up while rolling. Takeoff is a speed gate, not a button.
4. **Steering FADES with speed**: `Lerp(airSteerMax, airSteerMin, normalizedSpeed)` — most authority at low
   speed, least at high. Backwards from a real aircraft, where control power grows with dynamic pressure.
   Deliberate arcade choice; ours to keep or invert.
5. **Sticky throttle, asymmetric**: W ramps toward target at `delta`, S bleeds at `delta/8`, hands-off
   bleeds at `delta/16`. Much slower to lose speed than to gain it.
6. **Axis split matches our helicopter**: mouse pitch + roll, `A`/`D` yaw. That is worth preserving — it
   is the control scheme VoX signed off on for rotary wing, and a second aircraft that flies differently
   on the same sticks is a usability tax.

## What the helicopter rework already gives us

`Staging-Tinyclaw` landed machinery a plane can reuse directly rather than reinventing:

- **Quadratic anisotropic drag** with the coefficient *derived* from thrust and `Speed_Max`
  (`LevelFlightAccel`), so top speed is a consequence rather than a typed number.
- **A ground-effect probe** that already casts from the rotor hub with a terrain mask — a wing needs the
  same thing at the same scale, and ground effect on takeoff/landing is more pronounced for fixed wing
  than for rotary.
- **The MP envelope discipline**: `Speed_Max × EnvelopeSlack` horizontally, `HeliClimbMax`/`FallMax`
  vertically with zero slack. Any plane spec has to be calibrated against the same server checks, and the
  heli work is a worked example of what happens when it is not.
- **A measurement suite shape that has teeth** (`vehicle.heli_speed`): level-flight terminal speed,
  convexity of the drag law, per-airframe magnitude bounds. A plane suite should be the same shape.

## Decisions needed before any of this is a plan

1. **Which mesh**, and is it a flyable-scale aircraft or a 26 m airliner? At that span it is closer to the
   container ship in feel than to the minicopter.
2. **Lift along world up (retail, arcade) or body up (physical, bank-to-turn)?** This is the single choice
   that decides whether the plane flies like Unturned's or like an aircraft. Everything else follows.
3. **Stall behaviour** — retail has none; below flying speed you simply sink. A stall is the main thing
   that makes fixed wing feel different from rotary, and also the main way to frustrate a player.
4. **Runway reality.** PEI has an airport, but takeoff needs enough flat ground and a wheel model that
   tolerates it. Worth checking before promising a rolling takeoff rather than a spawn-in-the-air.

None of these are mine to pick.
