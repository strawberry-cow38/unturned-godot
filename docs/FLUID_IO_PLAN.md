# Fluid IO System — Plan

strawberry (2026-07-22): "identical to power but uses hoses" — source container → hoses → consumers/storage,
`fluidID:name:amount` per container with fill bars. Branch `fluid-io` off main.

## Design (strawberry's answers, locked)

1. **Flow = rate-based, power-like.** Fluid flows to a consumer when its demand ≤ available supply (mirrors power's
   "consumer powered if it receives ≥ its usage"). Flow is a RATE (units/sec); the finite AMOUNT drains/fills over time.
2. **Strict type-lock, no mixing.** A hose to a mismatched fluid REJECTS at connect time with a "cannot mix fluids"
   tooltip. Empty containers adopt the first fluid that flows in. So every connected fluid network is single-type by
   construction → the solver stays type-agnostic (type enforced at hose creation).
3. **Storage vs consumer.** Storage ACCUMULATES if supplied (later: rain barrels collect rain). Consumers DELETE fluid;
   some are TRANSFORMERS with an OUTPUT fluid (refinery oil→gas, sluice water→dirty water).
4. **Splitters + combiners** — mirror power (fan-out / merge).
- **Electric pumps** (later): consume POWER (bridge to the power net) → give head lift + boost hose flow rate.

## Architecture — mirror power, parallel system

Power reference: `game/ConnectionPort.cs` + `Wire.cs` + `PowerNet.cs` (Godot adapter) + `core/UnturnedSim/PowerSolver.cs`
(engine-free algorithm) + `DeployableDef.cs` (typed ports). Fluid mirrors each:
- **`core/UnturnedSim/FluidSolver.cs`** — engine-free, a near-copy of PowerSolver in fluid terms: FluidDevice / FluidPort
  (Source/Consumer/Passthrough) / FluidHose; Solve() propagates flow RATE, splitter = 0-rate relay + N passthroughs,
  combiner = 2 inputs summed w/ proportional share, source-cap starvation, TraceLoad. (Kept separate from PowerSolver so
  head-lift / pump / transform-consumer logic can diverge later.)
- **`game/FluidNet.cs`** — Godot adapter (mirror PowerNet): walks the fluid deployable/hose node groups → FluidSolver
  records → Solve → writes flow back to ports, then per-tick moves the ACTUAL amount (source.Drain(flow·dt),
  dest.Fill(flow·dt)), respecting FluidTank capacity + remaining as rate caps.
- **`game/Hose.cs`** — mirror Wire; the hose tool rejects mismatched fluid types (tooltip).
- Fluid **ports** — reuse ConnectionPort with a fluid flag, or a parallel FluidPort (decide in F2).
- **Container deployables** (DeployableDef or a parallel def): Fluid Source, Storage, Consumer (+ transformer variant),
  Splitter, Combiner. Each carries a `FluidTank` (fluidID + name + amount + capacity).
- **Fill bars** — per-container bar (amount/capacity), colored per fluid, matching the power usage-bar style.

## Data model
- Extend the fluid registry: a `FluidDef` (id → name + color + ...) for Water / Fuel / Oil / Gas / DirtyWater / ...
  (the existing `FluidType {None, Fuel}` enum comment already says "extensible later"). `FluidTank` (game/FluidTank.cs,
  Amount/Capacity/Fill/Drain — reuse) gains the fluid id. `StationFuel` shared tanks stay for gas pumps.

## Phasing
- **F1 DONE (847b6f95):** FluidDef registry + FluidTank id; `FluidSolver` (mirror PowerSolver) + 7 L0 tests. No Godot deps.
- **F2 DONE (bf3bd685):** `FluidNet` adapter + `Hose` + `FluidContainer` (Source/Storage/Consumer) + `--fluidtest` headless flow (source→hose→storage fills, conserved).
- **F3 DONE (b7bd4938):** container tank mesh + InfoBillboard fill bar (name / bar colored by fluid / amount prompt); `UG_FLUIDRENDER=1` movie path. Render-verified bars fill/drain.
- **F3.5a DONE (f945e262):** `HosePort` physical port cube (StaticBody3D, own layer 1<<11) on each container; group `fluid_ports`.
- **F3.5b DONE (cf16b42f):** `Hose` polyline visual (mirror Wire.SetPoints, thicker); render draws the hose port-to-port.
- **F3.5c NEXT — the interactive HOSE TOOL + placement (the big one, needs an in-game session to verify):**
  1. Generalize `ToolDef.IsRope` (bool) → a `Kind {Wire, Rope, Hose}` enum; add a `ToolDef.Hose` entry (item id + held mesh — reuse wire_hold.obj tinted). Add `Viewmodel.IsHoseTool`/`IsHoseViewmodel`, `PlayerController.HoldingHoseTool`.
  2. `UpdateHoseLook` / `HoseLmb` / `HoseRmb` mirroring the wire tool but LEANER first pass: look at a HosePort (highlight + HUD via InfoLine), LMB a source→start, LMB a consumer→complete a straight hose (node routing/clear-hold = fast-follow), RMB cancel. Ray on `HosePort.PortLayer`.
  3. **Type-lock reject** = `CanCompleteHose(src, dst)`: opposite kinds, different container, dst unhosed, AND fluid types compatible (either tank None/empty → adopts, else equal). Mismatch → red HoseBad highlight + "cannot mix fluids" HUD. Extract the pure type-lock predicate so it's L0-testable without a session.
  4. On complete: `new Hose{Source,Consumer}`, AddChild, and if a tank was empty it adopts the other's type.
  - **In-game PLACEMENT:** mirror `DeployablePlacer` to place Source/Storage containers (later a real fluid item + DeployableDef entry; for now a debug spawn is fine to exercise the tool).
- **F4:** splitters + combiners.
- **F5:** electric pumps (power↔fluid bridge: head lift + flow-rate boost) + transformer consumers (refinery/sluice);
  rain barrels when weather lands.

## Testing — GO EASY, test each PHASE when done (strawberry 2026-07-22)
Not every chunk/commit. Build as I go; verify at phase boundaries only, and keep it light:
- **F1:** a FEW L0 `dotnet test` cases for FluidSolver (source→consumer flows / doesn't when under-supplied; a chain;
  a splitter fan-out; a combiner merge) — enough to trust the core, not an exhaustive suite.
- **F2+:** `dotnet build` + one headless check per phase (place source+storage+hose, tick, confirm storage fills / bars
  move / tooltip shows) — one render per phase, not per chunk.
- Commit source-only on `fluid-io` (no fix-branches). Reconcile with catboy at merge if any shared-file overlap.
