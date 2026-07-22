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
- **F1 (foundational, engine-free + testable):** FluidDef registry + FluidTank id; `FluidSolver` (mirror PowerSolver);
  L0 unit tests for FluidSolver (like the PowerSolver tests). NO Godot deps → fast, verifiable.
- **F2:** `FluidNet` adapter + `Hose` + fluid ports + the Source/Storage/Consumer container deployables (DeployableDef),
  placement, a working source→hose→storage flow in-world.
- **F3:** fill bars + the type-lock "cannot mix fluids" tooltip on a bad hose.
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
