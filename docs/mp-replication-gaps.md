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

## 3. The weather STATE MACHINE integrates its own time; the day clock is fine

⚠ **This entry was wrong in its first version and the correction matters, because
the wrong version sends you to wire up something that is already wired.**

The day clock **is** replicated and **is** read: `WorldNetSync.cs:61` does
`_dnc.Time = _server.Clock.TimeOfDayAt(tick)` — *"the authoritative clock IS the
tick"* — and L65 re-derives it to measure drift. So `Cycle.Time` is
server-authoritative today.

What `WeatherManager` does (~L215) is integrate its **own** state on

    float dt = (float)delta * (Cycle != null ? Mathf.Max(0f, Cycle.Speed) : 1f);

It reads Cycle's **Speed** and never its **Time**. So the clock is replicated and
the weather state riding on it is not: every client integrates its own storm, and
`Cycle.Overcast`, `Cycle.StormAmount` and the `rain_daylight` global diverge.

**The fix is to derive weather from `Cycle.Time`, which is already server-driven**
— not to replicate a clock.

⚠ **And the render concern in the first version was misattributed.** The comment
about running on frame delta *"so it fires even when the day clock is frozen
(renders)"* is at L31 and belongs to `_pendingThunder` — the boom queue — with
L326-330 stating it outright: *"the per-strike consequences that must ignore the
WEATHER clock: the flash FADE and the delayed thunder."* Those already run on
their own frame-delta path, deliberately, precisely so they survive a frozen
clock. A weather-state swap therefore does **not** endanger renders the way this
doc first claimed; the render-critical part is already separated.

**Genuinely open, not settled** (tinyclaw, and not checked by either of us):
whether weather state is cheaply *derivable* from `Cycle.Time` — a pure function
of time, seeded — or whether it genuinely needs integrating and therefore needs
its accumulator replicated instead. That decides whether this is a small change
or a real one, and nobody has looked.

---

*Not fixed here. Written down because knowledge that lives only in one agent's
notes is one handoff away from being rediscovered — which is the lesson that
produced `~/tpw-formats` the same night.*
