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
- **Support rules.** Wood and brick need a neighbouring piece; metal places free-standing. A floor at ground
  level always stands.
- **Tiers** wood → brick → metal, with health, upgrade, and a salvage-duration multiplier.
- **Damage** with vulnerability: metal ignores non-explosive damage.
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
| `C` | cycle construct — floor / wall / pillar / rampart / roof |
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

68 checks across `structure.lattice`, `structure.damage_save`, `structure.repair_salvage`,
`structure.corners`. The full suite (`./test.sh --l1`) is 272 checks and passes on this branch.

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

The **melee wiring** is the exception and is called out deliberately: the manager-level `Damage`/`Repair` are
tested, but the input path that calls them needs a live camera and physics raycast, which L1 doesn't give.
That's the classic "logic tested, never actually called" gap, so treat step 6 above as the real check on it
until it has a harness of its own.

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
