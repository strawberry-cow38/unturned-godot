# Zombie FPS investigation — findings + what's still open

**Symptom (strawberry, 2026-07-25):** sitting STILL in a car in 3rd person inside a POI drops
**280 fps (vsync) → sub-20**. Recovers immediately in 1st person in the same car, and on foot in
either camera mode. 3p in a car outside a POI is fine. F3 shows nothing abnormal on the CPU side.
It only happens with zombies on. Hardware: 9800X3D / 64 GB / 4080 Super.

That combination is the whole constraint set: **stationary** (not traversal/streaming),
**3p + vehicle only** (camera-dependent), **POI only** (needs many zombies), **GPU-bound**.

## Ruled out — by reading, don't re-pay for these

| Theory | Why it's dead |
|---|---|
| Culling cone "collapses to 0,0" in a vehicle | `DriveVehicle` sets `GlobalPosition = _driving.GlobalPosition` every physics frame (`PlayerController.cs:4225`). The streaming anchor tracks the car correctly. |
| Outline overlay's 2nd full-scene pass | Already suppressed while driving — `OutlineOverlay.DrivingSuppress` set unconditionally in `_Process` (`PlayerController.cs:4029` → `OutlineOverlay.cs:58`). Its own comment names this exact bug. |
| Missing SP zombie animation cull | Real gap (see below) but can't be the cause: on foot in the same POI is fine, and SP zombies animate identically there. |
| Chase-cam collision raycasts | There are none. `PositionVehicleCam` is pure math (`PlayerController.cs:4243-4266`). |
| Zombie mesh being heavy | It's **154 verts / 488 tris**. 20 zombies ≈ 10k tris total. |
| Animation data (15.4 MB, 349 clips) loaded per zombie | Already shared — `_animCache` keyed by (rig, armsOnly) (`RiggedCharacter.cs:372`). |
| Software skinning | Renderer is **Forward Plus** (`project.godot` features), so skinning is GPU-side. |
| Scene complexity / more stuff in a wider frustum | 20 skinned characters is nothing for a 4080S. A *tank* is a multiplier, not a linear cost. |

## Found and FIXED

1. **Per-zombie texture duplication.** Every character re-read its atlas from disk and built its own
   `ImageTexture` — mesh and skin were cached, the texture never was. There are only **6** zombie
   atlases and **one** face texture, so 20 zombies uploaded ~21 redundant copies to VRAM.
   Now cached by path (`RiggedCharacter._texCache`). Material is deliberately NOT shared — `SetGhost`
   mutates it per instance, so sharing would ghost every zombie at once.

2. **Missing SP render cull.** SP zombies drew their full skinned rig at any distance; the cull only
   ever existed on the MP puppet path (`ZombiePuppets.cs:52-63`). Added distance cull + distance-based
   shadow casting in `ZombieController.CullRender()`. Test `zombie.distant_rig_culled`, mutation-proven.

3. **Flaky test cleared** (unrelated to fps): `zombie.face_on_skull` was asserting animation phase, not
   bone binding. See its commit.

**None of these explain 280 → sub-20.** They are optimisations. Recorded so nobody re-derives them.

## MEASURED — render volume is ruled out

Run it yourself: `./tools/zperf.sh [N]` (wraps `--zperf` in xvfb+lavapipe+vulkan).

**The instrument was broken and is now fixed.** The first two attempts measured under `--headless`,
where the engine renders nothing — every render counter reads zero and any timing is just the frame
pacer. That produced +250 ms one run and *negative* marginal cost the next. It must run with a real
rendering driver. lavapipe is a software rasteriser so absolute ms is meaningless, but draws /
primitives / vram are hardware-independent, which is exactly what exposes a multiplier.

```
N=20     88 draws (4.4 ea)    33,224 prims     vram ~50 MB
N=50    238 draws (4.8 ea)    91,844 prims     vram ~50 MB
N=100   478 draws (4.8 ea)   186,608 prims     vram ~50 MB
```

**Perfectly linear. There is no multiplier.** 20 zombies is 88 draw calls and 33k triangles, which a
4080S does not notice. cow tools measured the same independently with a real PEI POI in frame
(~290 draws, ~880k prims, 245 MB vram, 18 zombies) — same conclusion. The "every zombie cloned 80×"
hypothesis is **disproven by measurement**, not argument.

Toggling shadows in-scene isolates their share:

```
with shadows      4.8 draws + 1,866 prims per zombie
without           1.9 draws +   470 prims per zombie
```

So shadows are **~60% of zombie draws and ~75% of their triangles** — each zombie renders ~2.5× over
for the cascades. Worth capping (and it corroborates the uncapped 100 m / 4-split config below), but
it is a 2.5× on something already trivial. It is not a 14× collapse.

## It is the PHYSICS frame, not the renderer (measured on the real box, 2026-07-25)

strawberry's F3 in the tanked POI, having killed nothing:

```
FPS 26   frame 37.2 ms   worst 40.9 ms
cpu: process 7.6 ms   physics 32.2 ms
render: 487 draws   1313 objs   0.2M prims        <- trivial
zombie shadows OFF [F5]                            <- and still tanked
```

**87% of the frame is the physics tick.** Everything above about shadows, fill, cascades and draw
volume is therefore beside the point for this bug — the renderer was never involved. Zombie shadows
were already off in that reading and it was still 26 fps.

Two traps in that screenshot, both of which caught someone today:

- **`64 active bodies` is not the zombies.** They are `CharacterBody3D` — kinematic — and contribute
  nothing to that counter. Loot, vehicles and deployables do, and they are present with zombies off,
  when there is no tank. So the active-body count is background, not the delta.
- **`TimePhysicsProcess` is not the solver.** It is the whole physics frame including every
  `_PhysicsProcess` script callback. "64 bodies can't cost 32 ms" is correct *about the solver* and
  irrelevant to the number on screen.

### What the zombie AI script actually costs

`--zperf` with `UG_ZAI=1` spawns real (non-puppet) zombies plus a registered player and prints the
per-leg cost per physics tick. Physics is CPU, so unlike every render measurement this box is honest
about it, and it needs no rendering driver at all. Stable across every run:

```
z.move   ~0.013 ms per zombie per tick   (StepUp + MoveAndSlide; linear; FLAT GROUND -- best case)
z.rig    ~0.008 ms                       (animation, advanced at the 50 Hz physics rate)
z.sense  ~0.001 ms idle, ~0.01 ms hunting
                                          -> ~0.024 ms per idle zombie per tick
```

**At ~20 zombies that is half a millisecond.** The zombie AI script is not a 32 ms cost on any
hardware. Do not go hunting in `ZombieController`'s logic.

`z.rig` is NOT the animation cost. `Tick()` only calls `Advance()` for a Manual-mode mixer, and
`UsePhysicsAnimRate` puts zombies in Physics mode, so for a zombie `Tick()` decrements a float and
returns. The actual 17-bones-per-zombie posing is done by the engine's AnimationMixer inside the
physics frame, where no script timer reaches it. `z.rig` measures bookkeeping. Use F6 (cow tools'
`RiggedCharacter.SetAnimFrozen`, `_ap.Active = false`) to measure the real skeleton share.

Related and unfixed: the distance cull hides the rig but does **not** stop the mixer, so a culled
zombie at 200 m still poses all 17 bones every physics tick.

### GC is not the spike cause

Per-window collection counts against the physics frame, same process, three seconds apart:

```
win1  gc[0/0/0]   physics  25.2 ms
win2  gc[1/0/0]   physics 105.5 ms   (worst frame 215 ms)
win3  gc[0/0/0]   physics  29.6 ms
```

Near-zero collections through a 4x swing, matching the `gen0 +0` on strawberry's own F3. Not GC.

Worth fixing anyway, separately from this hunt: the managed heap climbed 152 -> 169 MB in twelve
seconds with 60 zombies. `HasLineOfSight` allocates three marshalled objects per zombie per tick --
a `PhysicsRayQueryParameters3D`, a `Godot.Collections.Array<Rid>`, and the `Dictionary` returned by
`IntersectRay` -- roughly 3000 allocations a second, all of which could be cached per zombie.

Two things that are NOT established, recorded so nobody quotes them:

- The engine-side share. FOUR instrument designs failed on this box: a single monitor sample, a
  windowed average, cross-process paired runs (13.3/13.5, 11.5/6.6, 17.3/33.8 ms -- the effect
  changed sign), and a same-run alternating A/B (still 105 ms vs 29 ms between adjacent windows).
  The variance exceeds the effect every time. **Do not attempt a fifth design here** -- this 4-core
  box cannot time a physics frame. Read `z.total` against the physics line on real hardware instead.
- An apparent superlinearity in zombie count. It was an artifact of the probe's spawn grid: at n=60
  the rows extend far enough that some zombies face the player, see him and switch to hunting. More
  zombies did not get more expensive, more zombies got *awake*.

## Still open — the real cause

**SUPERSEDED — read the physics-frame section above first.** Everything from here down was written
while the renderer was still the suspect, and the F3 reading killed that: 32.2 of 37.2 ms is the
physics tick, with zombie shadows already off. It is kept because the render measurements themselves
are sound and worth not repeating, not because the conclusions still point anywhere useful.

Prime suspect *was* **shadow cascades**, and the config supports it:

- `project.godot` has **no `[rendering]` section at all**, so everything is at Godot defaults.
- `DirectionalLight3D`s are created with `ShadowEnabled = true` and no cascade config →
  default **PSSM 4 splits**, 4096 shadow atlas, at a 2560×1440 default viewport. Shadow distance
  was the default **100 m** when strawberry hit the tank; `77b56234` has since capped the world
  sun to **40 m**, so a re-test on current `main` may already read differently. The zombie shadow
  radius here (28 m) deliberately sits inside that cap.
- Every shadow caster renders into up to 4 cascades. A **skinned** caster re-skins per cascade;
  static geometry doesn't. That is the one mechanism that is genuinely zombie-specific rather than
  scene-wide, and it scales with how much of the POI the cascades cover.
- The 3p chase cam sits up to **34 m back and elevated** (`PlayerController.cs:4261`), which is what
  balloons the cascade volume versus any 1p or on-foot view.

**Unverified.** Nobody has measured it. Do not treat the above as established.

### The probe's frame TIMING is not usable on this box. Its counters are.

Do not repeat this mistake — it was made three times in one session. `--zperf` prints frame time, and on
the ARM box under xvfb + lavapipe that number is worthless:

```
sun shadow off, 1 draw call, 2 primitives (an empty frame):   95 ms @ 640x360   160 ms @ 1920x1080
anything heavier, any resolution, any N:                      pins at ~160 ms
```

A one-quad frame cannot cost 95 ms in any renderer, so that is a fixed per-frame cost unrelated to the
scene, and ~160 ms is a hard ceiling everything saturates against. Every "no measurable difference"
timed at 1920x1080 was therefore the frame sitting on the ceiling, **not** the zombies being free. One
such reading was posted to the channel as evidence that fill was ruled out. It ruled out nothing.

Always run the `UG_ZSUN=noshadow` control alongside any timing: if a delta only exists when the floor
is present, the delta *was* the floor.

The counters (draws / objs / prims / vram) are unaffected by this — they are deterministic and
hardware-independent, and every conclusion above that rests on them still holds.

**Fill is NOT ruled out.** It is unmeasured, and it cannot be measured here.

Also note what headless *cannot* settle: cow tools ran the loaded POI + horde under both opengl3 and
Vulkan/Forward+ offline and got normal counts and normal wall-clock in both. An offline frame dump has
no display, no vsync and no present loop, so a real-time interactive stall would not show up there
even if it exists. Normal counts headless is evidence about **volume**, not about stalls.

## The measurement that ends it

F3 in-game overlay, read the **render** line (it is below the cpu line — easy to miss):

```
render: N draws   N objs   N.NM prims
mem:    static N MB   vram N MB
```

Read it twice — zombies ON vs OFF, 3p in the POI:

- **draws** in the thousands rather than tens → something draws each zombie many times over (structural)
- **prims** in the tens of millions → geometry multiplier (baseline is ~10k tris of zombies)
- **vram** climbing → per-instance resource duplication
- **all three flat but fps still tanked** → a stall, not volume: GPU→CPU sync or shader compilation,
  and hunted completely differently

Then, to confirm shadows specifically: flip zombie `CastShadow` fully Off. Tank vanishes → shadows,
and the fix is a proper shadow cull + a `DirectionalShadowMaxDistance` cap. Tank remains → it's the
zombie mesh/render path itself and the render line above says which.

And the fill discriminator, which is five seconds of work and settles the axis nothing here can reach:
set `rendering/scaling_3d/scale` to `0.5` and stand in the same 3p-car-POI spot. Fill cost scales with
pixels and nothing else does, so **fps roughly doubles → it is fill** (overdraw / shadow-map
rasterisation); **fps barely moves → it is not fill**, and what remains is a stall — a GPU→CPU sync or
synchronous pipeline compilation — which is hunted with GPU timings, not with counters.
