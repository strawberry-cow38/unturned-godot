# Native source lamp geometry and glass breaks

`vehicle/lighting/geometry.rs` ports the original `Vehicle.Build` lamp paths from
`game/Vehicle.cs`. It separates the original triangles rather than covering them
with replacement shapes. The normal vehicle renderer consumes each lamp part
once and uses the remaining body mesh when the lamps were baked into it.

- Separate head/tail parts split by triangle-centroid X only when both halves
  exist. Every half has its own material and canonical source label. Single
  centered lamps retain the original unsplit, unshootable fallback.
- Baked semi headlight and trailer taillight zones retain the source branch
  ordering and all-corners-in-one-zone rule. The original landing-leg branch has
  priority. This does not add trailer coupling or landing-leg animation.
- The original `LensHull`/`ClassifyLamp` geometry chooses round3000K or rectangular
  4300K lamps. Baked lenses retain the original default tint. There is no generic
  bright-texel extraction in this source path.
- Missing authored emitter arrays derive positions from the original half AABB,
  including the front/rear0.06m offset. Explicit authored arrays retain their
  source order. Legacy material aliases are preserved: parts select the last
  half, baked zones the first, and the rear-box fallback can replace the alias.
- Red/blue lightbar parts retain independent source colors, center positions,
  and lit energy5. The hidden center amplifier is registered for source lamp-hit
  proximity but stays hidden, without a new collider or visible hull triangle.
- Helicopter navigation lenses retain the original mirrored mesh, port red and
  starboard green, energy/range1.4/2.2. Belly beacons use the exact largest
  triangle-centroid cluster, recenter it, turn its thin X axis downward, and use
  the source hull-bottom offset. Hind borrows the Skycrane lens; Orca selects
  ten of its twenty triangles. Minicopter retains the original small sphere
  fallback. Beacon lit energy/range is3.2/6. Aircraft fittings are not registered
  as shootable road lamps.

`BuiltLighting` contains source material aliases, lamp centers/half extents,
emitter positions, and lens colors. The root lighting presentation owns actual
light objects, clocks and audio; managed accepted state owns switches and broken
lamps. Those broader authority rules are documented in
[VEHICLE_LIGHTING_AUTHORITY.md](VEHICLE_LIGHTING_AUTHORITY.md).

`particles/glass.rs` is the shared original `GlassPane.SpawnShards` and
`PlayBreakSound` adapter. It reads retail rubble64, retains the original texture
availability gate, and deliberately renders **untextured** flat quads: the source
removed the sprite texture because its soft alpha multiplied the shard alpha
away. Lamp bursts produce six small shards at default particle quality, uniform
half alpha,1.2s lifetime,2.5–5.6m/s launch, gravity−7, original angle/spin ranges,
world-aligned box emission and the source0.9 explosiveness/0.5 birth randomness.
The source glass64 WAV is preferred, with `impact_glass.wav` as fallback; gain is
−2dB with unit6m/range80m.

Optional particle settings preserve existing emitters' defaults. Glass uses
the original two-angle CPUParticles3D spread and compressed birth-phase formula
from [Godot4.6 CPU particles](https://github.com/godotengine/godot/blob/4.6-stable/scene/3d/cpu_particles_3d.cpp).
Cosmetic random draws are native; they are not an authority or replay contract.

`lighting/breaks.rs` consumes `vehicleLampBroken` once per accepted sequence.
It runs after accepted events and body adoption, before material darkening.
Reliable facts and ordinary snapshots use separate channels, so a per-body,
seven-label color history also retains the first pre-break color if the snapshot
arrives first. It never compares process-local snapshot ticks with accepted
server ticks. A first replica already damaged starts with the damaged appearance;
head/tail repairs restore source colors, while later lightbar bursts retain the
source permanent gray even when snapshots skip a complete break/repair cycle. It uses the accepted position and
hull-rotated forward/backward direction, but leaves source box extents world
aligned. The hidden amplifier uses the original cream fallback. Damaged
snapshots, repairs, and duplicate events do not emit shards. Detached world
emitters and voices retire on session changes/disconnect; the vehicle geometry
uploader excludes particle emitters and hidden meshes.

Validation at this checkpoint:

- Four actual-content geometry tests cover triangle/attribute preservation,
  aliases, source zones, Kelvin shape classification, derived positions, hidden
  amplifier, navigation sides and exact aircraft cluster dimensions/counts.
- Ten shared particle tests pass, including delayed births across frame rates,
  box bounds, single-shot retirement and existing smoke/fire/origin behavior.
- Accepted-event ECS coverage checks source albedo, direction, box, six-shard
  count, amplifier fallback, duplicate/repair refusal and session cleanup. The
  delayed-event regression runs the real presentation systems with deliberately
  different client/server clock values, repair-before-fact, and skipped cycles.
- Shared vehicle audio pauses existing and pending sinks and preserves the car
  wind-down envelope during singleplayer pause. Network pause menus keep that
  clock running. Focused ECS tests exercise decoder settings and voice retirement;
  no output device is available for listening validation.
- The root police fixture uses a real UDP driver and normal host Fire against
  uploaded original vehicle triangles. Opened `bevy-vehicle-light-{off,low,high,
  broken-left,siren}.png` and JSON confirm both beams, accepted independent lamp
  damage with hull6000 unchanged, and repair/siren state. The near
  `bevy-vehicle-light-break-burst.png` shows six flat cream chips; its emitted mesh
  has36vertices. These are isolated production-system fixtures, not fullgame
  driver-input evidence.
- Opened `bevy-huey-source-lamps-{off,lit}` and
  `bevy-hind-source-lamps-{off,lit}` PNGs/JSON use accepted integrated-host
  ignition and production source spool/nav/beacon clocks. Huey shows the green
  starboard roof lens and belly flash; Hind shows the red port wing lens and
  centered downward beacon. Off snapshots have flags0/spool0/zero emission;
  lit snapshots have flags1/spool0.254 or0.228, nav emission2.6 and beacon6.
  Root independently opened all four. This stationary dark fixture proves lamp
  placement/emission, not flight input or full-body shading.
- Final focused results: four geometry, ten particles, three break-feedback and
  four vehicle-audio tests pass. The two aircraft render runs exit successfully
  without shader/asset errors. Root also peer-reviewed the geometry and exact
  source branch behavior.
- Reflected particle billboard behavior remains a separate limitation; this
  checkpoint does not claim it from main-camera images.
