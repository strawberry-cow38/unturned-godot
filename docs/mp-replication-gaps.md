# Multiplayer replication gaps (unturnedGD)

Handed over by tinyclaw 2026-09-18 when TPW-GD phase one was assigned to them,
so these would not evaporate with the handoff. **Verified in the tree rather than
relayed** — each entry below was checked, and one turned out to be sharper than
the description.

## 1. Glass breakage is not replicated

`core/UnturnedNet/` carries no glass state. `GlassPane` plays its shatter, sound
and chip spawn locally (`PlayBreakEffect`-style, with the large cull AABB) and
then frees the pane. Nothing goes on the wire.

**Effect:** one player smashes a window, everybody else still sees it intact —
and can presumably still be stopped by its collider.

⚠ `WornGlasses` in `CombatReplication` is eyewear, not panes. Do not grep
"glasses" and conclude this is covered.

## 2. Fluids are simulated but not replicated

`core/UnturnedSim/FluidSolver.cs` and `FluidHoseRule.cs` exist; `core/UnturnedNet/`
has **no** fluid or power entry at all. So tanks, pumps, valves, purifiers and
the hose rule run per-client with no authority.

⚠ **`game/FluidNet.cs` is a NAME COLLISION.** It is the fluid *network* — the
pipe graph — and not networking. Anyone skimming filenames will read it as "the
fluid net code exists" and be wrong. Same trap as `LinkKind.Trail` meaning two
different things in ProcIsland.

## 3. Weather advances on FRAME DELTA, not the replicated day clock

`WeatherManager.HubProcess` (~L215):

    float dt = (float)delta * (Cycle != null ? Mathf.Max(0f, Cycle.Speed) : 1f);

so the weather state machine steps on local frame time scaled by the cycle speed.
The comment at L31 says why — *"so it fires even when the day clock is frozen
(renders)"* — which is a real requirement and the reason this is deliberate
rather than an oversight.

**But a replicated clock does exist**: `core/UnturnedNet/WorldReplication.cs`
`TimeOfDayAt(long tick)`, and `WorldSave.TimeOfDay01` persists it. Weather does
not read either, so in multiplayer **every client rolls its own weather** — one
player is in a storm while another has clear sky, and `Cycle.Overcast` /
`Cycle.StormAmount` / the `rain_daylight` shader global diverge with it.

**The fix has to keep the render path working.** Driving weather from
`TimeOfDayAt(tick)` when a server clock is present and falling back to frame
delta when there is not is the shape that satisfies both; a straight swap would
freeze weather in every offline render, which is what the L31 comment is
protecting.

---

*Not fixed here. Written down because knowledge that lives only in one agent's
notes is one handoff away from being rediscovered — which is the lesson that
produced `~/tpw-formats` the same night.*
