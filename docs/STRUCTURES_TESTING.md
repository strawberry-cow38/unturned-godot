# Structures — how to test it

Branch: `feat/structures`. Companion branch: `feat/barricades` (cow tools).
**`feat/build-integration`** is both of them merged with the held-item wiring done — test there if you want
one session with everything in it. See "The integration branch" at the bottom.

## What's new

`BuildTool` used to be the building system: a box mesh snapped to a 3 m grid that spawned a loose
`StaticBody` with no health, no slot bookkeeping and no persistence. Its own header called it a stand-in.
It's now just input + preview over `StructureManager`, which owns the real thing.

- **6 m tile lattice.** The old grid was 3 m, labelled "Unturned's structure tile size". 3 m is the *half*
  edge, used for pivot maths. Anything built on a 3 m lattice looks fine alone and can never line up with a
  real foundation.
- **Face vs edge snapping.** Floors/roofs snap to tile centres; walls/ramparts snap to side midpoints and
  take the facing that side implies. A floor slot and a wall slot at the same coordinates are distinct, so a
  wall doesn't block the floor of its own tile.
- **Corner snapping for pillars/posts.** Faces snap to tile centres and edges to side midpoints, but a pillar
  belongs at the tile *corner* — the odd multiples of the half edge. Snapping it like a floor would park it in
  the middle of the tile it is supposed to hold up.
- **Doorways.** A wall-class piece with a real hole — three solids around the opening, so the *collider* has
  the hole too. A doorway you can see through but not walk through reads as a stuck door, not as missing
  geometry. A doorway and a wall resolve to the same slot key, so an edge holds one or the other and the
  mutual exclusion is structural rather than a rule someone has to remember. `StructureManager.DoorSocket`
  gives the frame a door leaf hangs in, and returns null for a plain wall rather than a plausible transform
  that would mount a door inside solid geometry. Opening size (2.0 × 3.0 m) is **ours**, like the tier health.
- **Support rules.** Wood and brick need a neighbouring piece; metal places free-standing. A floor at ground
  level always stands.
- **Tiers** wood → brick → metal, with health, upgrade, and a salvage-duration multiplier.
- **Damage** with vulnerability: metal ignores non-explosive damage.
- **Charges actually raid.** `DetonateTrap` damaged nearby *deployables* and left every wall untouched, so a
  charge would flatten the generator next to a base and not scratch the base. It now goes through
  `StructureManager.Explode`, and `structure.charge_raid` drives the real path — plant a charge, fire it the
  way a detonator does, check the wall — because a suite that only calls the manager passes whether or not
  anything in the game ever reaches it.
- **Explosions** (`StructureManager.Explode`), reimplemented from SDK `StructureDrop.cs:52-70`: range measured
  to the piece's *closest point* rather than its origin, linear `1 - range/radius` falloff, and a
  **line-of-sight test** so a piece behind another piece is shielded. That last one is what makes layering
  walls worth doing — without it a single charge at the front door damages every wall in the building at once
  and the whole upgrade ladder is decoration. Explosive damage ignores tier vulnerability, so metal is not
  immune the way it is to melee.
- **Repair / salvage**, both reporting what actually happened (see "Deliberate choices").
- **Persistence** to `user://structures.json`, loaded on entry and saved on exit + on window close.

## Run it

```bash
cd ~/projects/unturned-godot
git checkout feat/structures
UG_UNTURNED_DIR=/home/ec2-user/unturned ./run.sh        # or however you normally launch a playable session
```

In game:

| key | what |
|---|---|
| `B` | toggle build mode |
| `C` | cycle construct — floor / wall / doorway / pillar / rampart / roof |
| `V` | cycle tier — wood / brick / metal |
| `LMB` | place |
| `R` | salvage the piece you're aiming at |
| `Y` | upgrade the piece you're aiming at one tier |
| `G` | melee the piece you're aiming at (blowtorch equipped → repairs instead) |

The readout at the bottom of the screen shows the current construct, tier and health, and **why** a slot is
refused — "slot taken" and "no support" are different mistakes and the ghost alone can't tell you which.

### The render, without launching a session

```bash
VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.aarch64.json \
UG_UNTURNED_DIR=/home/ec2-user/unturned \
xvfb-run -a ~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64 \
  --path game -- --build --shot=/tmp/structures.png
```

Note the `--` before the game args, and that `--shot=` takes a **file** path, not a directory.

## Tests

```bash
./test.sh --l1 --only 'structure.*'
```

134 checks across `structure.lattice`, `structure.damage_save`, `structure.query`,
`structure.repair_salvage`, `structure.explosion`, `structure.charge_raid`, `structure.doorway`,
`structure.aimed_actions` and `structure.barricade_seam`.

Two of these were verified by deliberately breaking the code and confirming the test goes red — the
explosion suite with the line-of-sight rule removed (the far wall then takes 148 damage through a wall), and
the seam suite with the wrong hook wired. A green test nobody has seen fail is a guess.

## What to actually poke at

1. **Build a floor, then a wall on its edge.** The wall should sit at the tile's side, not its centre.
2. **Try to place a wood wall in mid-air.** Refused, reason "no support". Switch to metal (`V` twice) — it
   places.
3. **Place two pieces in the same slot.** Second is refused, reason "slot taken".
4. **Upgrade path**: place wood, check the tint and the health in the readout, then place metal alongside.
5. **Salvage** with `R` — confirm it takes down the piece you're *looking at*, not the ghost's slot.
6. **Hit a wood wall with a melee weapon** (`G`). It should lose health and eventually break. Now try the
   same on a **metal** wall — it should shrug the hit off entirely. That asymmetry is retail's `isVulnerable`
   and it's the reason climbing the tier ladder is worth doing.
7. **Blowtorch a damaged piece** — it repairs rather than hits, matching how vehicles and generators already
   behave.
8. **Quit and relaunch.** Your base should still be there.
9. **Build two parallel walls and blow up the outer one.** The inner wall should take nothing while the outer
   one stands, and start taking damage once it falls. That is the shielding rule, and it is the difference
   between a base and a pile of tiles.
10. **Plant a remote charge against a wall and fire it.** A point-blank charge (1000 structure damage) should
    flatten wood and brick outright. This is the actual raid loop, end to end.

## Deliberate choices worth arguing with

- **Per-tier health (300 / 600 / 1000) is ours, not retail's.** Retail keeps health per-asset in `.dat` files
  we don't have locally. Rather than invent numbers and present them as ported, they're declared in
  `StructureCatalog`, pinned by a test, and flagged for replacement.
- **Load bypasses the support check.** A saved base is already known-good; re-validating on load would delete
  any piece whose supporting neighbour hadn't been restored yet, so load *order* would silently eat parts of
  someone's base.
- **Auto-persist is off by default**, switched on only by the game's own provisioning. Any manager a test
  constructs is inert on disk — an L1 run that wrote its fixtures over `user://structures.json` would destroy
  a real base and pass while doing it.
- **Repair returns what it actually restored** and salvage returns `-1` for "nothing there" rather than `0`,
  because `0` is a legitimate tier and callers charge/refund materials off those return values.

## Verified how

Placement, snapping, support, damage, upgrade, repair, salvage and save/load are covered by L1 checks that
assert on outcomes — where a piece physically ends up, what the manager actually returns — rather than
re-deriving the rule under test.

The **crosshair path** (melee / salvage / upgrade) has its own harness now — `structure.aimed_actions` drives
a real `PlayerController` with a real camera and raycast. It was previously listed here as untested, and that
gap was hiding a live bug: `AimedStructure` took the nearest piece within 3 m of the hit, measured to each
piece's *origin*, and an origin sits at its base. Aiming at the upper half of a wall found the floor beside it,
and past 3 m up found nothing — so melee, salvage and upgrade silently did nothing near the top of a wall,
with every manager-level test green because none went through the aim. It resolves by collider now.

That test is also a lesson in its own right. It passed alone and failed in the full suite, because
`Rigs.Player` blocks ~3 s loading gun assets when run cold; warm, it returns instantly and the player's
deferred setup lands in the gap between aiming and checking, resetting the camera. The aim is a
**precondition**, so it is re-established immediately before each action with no yield in between, and
asserted rather than assumed.

## The integration branch

`feat/build-integration` = `feat/structures` + `feat/barricades`, plus the one piece neither branch could land
alone: the held-item place flow in `PlayerController`, which both subsystems needed and only one of us could
edit without a conflict.

What the wiring is:

- the placer is now `BarricadePlacer` (an API superset of `DeployablePlacer`, identical for `Floor` defs, but
  it also accepts wall and ceiling faces);
- the surface **normal** is frozen at the click alongside the point and yaw, and passed to `Freeze`. Freeze
  point+yaw only and a wall barricade snaps flat for the length of the place animation, then lands correctly —
  a visible pop that reads as a glitch rather than a bug;
- a def whose `Mount != Floor` spawns through `Barricade.PlaceOnSurface`; every existing deployable is `Floor`
  and takes the original `Deployable.Spawn` path untouched.

### The seam bug worth knowing about

`BarricadePlacer`'s header proposed wiring `placer.CanAttach = StructureManager.Instance.CanAttach`. Don't.
`CanAttach` answers *"is there a structure face here"*, and on open terrain the honest answer is **no** — while
the placer reads a false as *"you may not build here"*. Wired that way, every generator, crate and charge
becomes unplaceable on the ground, and **both branches stay fully green**, because the barricade tests build a
placer with no hook at all. The hook is the one thing they structurally cannot see.

The wiring is `StructureManager.BarricadeAttachHook`, which abstains instead of refusing, and which takes the
**collider** rather than the hit point. Point-and-radius cannot work here in either direction: a piece's origin
is its base, so a hit 2 m up a 4.25 m wall is nowhere near it, and widening the radius to compensate makes a
generator on the ground beside that wall resolve *to the wall*, whose horizontal face disagrees with the
ground's up-normal, and get refused.

Two smaller fixes at the same seam:

- supplying `CanAttach` used to **replace** the placer's own attachability rule instead of adding to it, so
  wiring structures in would have silently made barricades stackable on other barricades. Both gates now apply.
- `structure.barricade_seam` (17 checks) covers this, and was itself verified by wiring the *wrong* hook and
  confirming it goes red. Its first version passed for the wrong reason — the gate abstained on the wall
  instead of agreeing with it — so it now asserts that the same wall piece **refuses** an up-normal, which is
  only true if the gate is actually engaging.

### Still open

- `MetalBarricade` (id 9120) is not in `DeployableDef.All`/`ById`, so it is reachable from a def reference but
  not yet obtainable as an item. It joins the rail when item-id→def placement is wired.
- MP placement carries yaw only, so a wall barricade's full orientation does not survive the wire yet.

## Known, not-mine

`power.wind_turbine` is **flaky**, not consistently red. It failed on clean `origin/main` with this work
stashed, then passed on a later full run of this branch. So it is pre-existing and unrelated to structures,
but "main is red" overstates it — if you see it fail, re-run before believing it.
