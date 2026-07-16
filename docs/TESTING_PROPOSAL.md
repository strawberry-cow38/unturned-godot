Repo surveyed — I have what I need: the harness/driver code in `game/Main.cs`, `PowerNet.cs`, `Deployable.cs`, `WorldItem.cs`, the five NUnit core suites, and the capture/settle machinery. Here is the proposal.

---

# PROPOSAL — Automated Testing for unturned-godot

*Factorio-style test culture, sized for a 2–3 person team + an AI agent, on this exact repo.*

## 0. TL;DR

Three layers, one command, one output convention:

| Layer | What | Runner | Cost | Target share |
|---|---|---|---|---|
| **L0 — pure logic** | engine-free C# (`core/` + extracted game logic) | `dotnet test` (NUnit, already in place) | **ms/test, ~5 s/suite** | most tests |
| **L1 — in-engine sim** | gameplay tests inside ONE Godot boot (`--tests`), no eyeballs | new `TestHost` node, ~300 LOC, zero new deps | **1 boot (~15 s) + ~0.1–2 s/test** | the tricky stuff: power net, physics settle, player/zombie behavior |
| **L2 — visual golden** | today's `--shot` PNGs, diffed against committed baselines | `tools/visual_tests.py` manifest runner | ~20–30 s/scenario, parallelized ×2 | ~10–15 curated scenes, nightly + on-demand |

- **One command**: `./test.sh` → runs L0, then L1, (optionally L2), prints grep-able `[TEST] name | PASS/FAIL | detail | repro: <cmd>` lines, a one-line summary, writes `junit.xml` + `results.json`, exits non-zero on failure.
- **Every bug fix ships with a regression test** — named after the bug, in the cheapest layer that reproduces it.
- **Robotic manager**: a cron on this box runs `./test.sh` nightly against fresh `main` and pings Discord on failure. (tinyclaw already lives in the channel — the Factorio "robotic manager" is nearly free here.)
- Recommendation on frameworks: **keep NUnit for L0, build the small custom L1 runner, do NOT adopt GdUnit4 yet** — reasoning and risk notes in §2.

The single highest-leverage fact: **this repo already writes tests — it just doesn't have a runner.** `PronetestDriver`, `BrokenTestDriver`, `GrenadeTestDriver`, `MeleeTestDriver`, `FallTestDriver` (game/Main.cs:3362–3532) are frame-scripted assertions with PASS/FAIL prints; `[POWERTEST]`/`[MANAGETEST]`/`[LAMPDBG]` (Main.cs:1176, 3339, 3353) are assertions wearing print-statement costumes. Each one burns a full engine boot and reports in a bespoke tag. The proposal is mostly: give these a registry, a shared boot, and a shared result format.

---

## 1. Current state (what I verified in the repo)

- `game/Main.cs` is 3,533 lines; ~25 `--flag=` scene builders and ~30 `UG_*` env toggles route into throwaway scenes. Verification is (a) `GetViewport().GetTexture().GetImage().SavePng` at a hand-tuned settle frame (the else-if ladder at Main.cs:3342–3352 — frame 90 for itemtest, 120 for drivetest, 50 for wiretest…), and/or (b) tagged prints.
- Real unit tests exist and are healthy: five NUnit suites under `tests/` (~1,100 green), `net8.0`, NUnit 3.14 + adapter 4.6, plain `dotnet test`. Crucially, **the decoupling pattern is already proven**: `core/UnturnedSim/PlayerMovementSim.cs` is engine-free retail movement logic tested in `tests/UnturnedSim.Tests` — no Godot boot.
- The game layer is **entirely code-built** (only `game/Main.tscn` exists; every harness constructs nodes in C#). This matters: scene-file-oriented test frameworks buy little here.
- Physics is Jolt at a fixed 50 Hz (`game/project.godot:22–23`) and the sim spine (`SimDriver.cs` → `SimClock`) is already deterministic-fixed-step by design. Determinism is a solved architecture problem here; it just isn't *enforced* by tests yet.
- Every scenario = one engine boot under xvfb + lavapipe ≈ 15–40 s. Ten scenarios ≈ 4–5 minutes serial, and the agent pays it on every iteration.

---

## 2. Tooling: what exists for Godot 4 C#, and what to actually use

| Tool | What it is | Fit here | Verdict |
|---|---|---|---|
| **NUnit via `dotnet test`** (in repo) | engine-free unit tests | already 1,100 green | **Keep. Expand.** This is L0. |
| **gdUnit4Net** (`gdUnit4.api` + `gdUnit4.test.adapter` NuGet, MikeSchulze/gdUnit4Net) | C# test framework whose VSTest adapter boots Godot (via `GODOT_BIN`) and runs `[TestSuite]` classes *inside* the engine through `dotnet test`; `ISceneRunner` simulates frames/input/signals; standard TRX/JUnit loggers work | The only serious, maintained C# scene-test framework for Godot 4. But: engine-version coupling (adapter releases historically lag new Godot minors — verify 4.6 support before committing), and it wants to own engine launch args via `.runsettings`, which fights this box's `xvfb + VK_ICD_FILENAMES + lavapipe` wrapper. Its scene-runner API is oriented around `.tscn` scenes, which this repo doesn't use. | **Evaluate later (Phase 5), don't block on it.** |
| **GoDotTest** (Chickensoft) | in-game test runner invoked by CLI switch; coverlet coverage via attached debugger | Same in-engine model as the custom runner below, but brings Chickensoft conventions + wiring; coverage story is its main unique value | Optional later; not worth the dependency now |
| **GUT** | GDScript-only | n/a | No |
| **Custom `TestHost`** (~300 LOC, below) | registry + coroutine driver + reporter inside the existing Main entry | Exactly matches how the repo already builds scenes and drivers; zero deps; output format fully under our control (agent-first) | **Build this. It's Phase 1–2.** |

Justification for custom-over-gdUnit4 (this repo specifically, not general advice): the five existing `*TestDriver` classes are already 80 % of an in-engine test — what's missing is discovery, batching, teardown, and reporting, all of which are trivial C#. gdUnit4Net's genuine advantages (IDE test explorer, `dotnet test` UX) matter less when the primary consumer is an agent grepping stdout on a headless ARM box, and its risks (4.6 adapter lag, launch-arg plumbing through `.runsettings` instead of the proven xvfb wrapper) land exactly on this project's weirdest constraints. Revisit once the test corpus is worth migrating — the authoring convention below is deliberately close to gdUnit4's (setup → simulate frames → assert), so a later port is mechanical.

---

## 3. The layered taxonomy + speed strategy

### L0 — pure logic under `dotnet test` (milliseconds; the default destination for new logic)

**Fact that unlocks this layer:** Godot 4's C# math types — `Vector3`, `Basis`, `Transform3D`, `Aabb`, `Color` — are pure managed structs. They work in a plain `dotnet test` process with **no engine**, by referencing the `GodotSharp` NuGet package (version-matched to 4.6). Only `GodotObject` descendants (Node, Resource, servers) need a live engine. So "gameplay code that touches Godot types" is *not* automatically engine-bound — only code that touches *nodes* is.

**The recipe, concretely, on `PowerNet`:** `game/PowerNet.cs` is already a static solver — but coupled to the tree via `tree.GetNodesInGroup("wires"/"deployables")` (PowerNet.cs:27–28) and reading `Deployable`/`Wire`/`ConnectionPort` nodes. Extract the algorithm into `core/UnturnedSim/PowerSolver.cs` operating on plain data:

```csharp
// core/UnturnedSim/PowerSolver.cs — engine-free
public sealed class PowerPort { public PortKind Kind; public float Watts; public float Live; public bool Powered; public float Draw; public int Owner; }
public sealed class PowerDevice { public bool Producing; public bool OnFire; public List<PowerPort> Ports = new(); }
public readonly record struct PowerWire(PowerPort Source, PowerPort Consumer);

public static class PowerSolver
{
    public static void Solve(IReadOnlyList<PowerDevice> devices, IReadOnlyList<PowerWire> wires) { /* body = today's Recompute + TraceLoad, minus IsInstanceValid noise */ }
}
```

`PowerNet.Recompute` becomes a thin adapter: walk the groups once, build the plain lists, call `Solve`, write results back to the ports. Behavior identical; the iteration/passthrough/fire/load logic (the part that actually has bugs) now runs under NUnit in microseconds, including cases that are miserable to stage as scenes (10-deep chains, cycles, over-draw, two generators feeding one consumer). Side benefit: this is the same "port logic to core" move the project already made for movement (`PlayerMovementSim`) — it's the house style, not a new idea.

**Candidates for the same extraction, in priority order** (trickiest-first, per Factorio): PowerNet solver; grenade/explosion falloff math (currently asserted in-engine at Main.cs:3516 — `175*(1-r/8)` is pure math); stance→stealth-radius table (Main.cs:3439); crafting/blueprint resolution (`game/inventory/Crafting.cs`); inventory grid placement (`PlayerInventory.cs`); loot-table rolls (seeded). Don't extract rendering/physics-adjacent code — that's what L1 is for.

**Speed**: whole L0 tier stays under ~10 s. Run first, always.

### L1 — batched in-engine tests: one boot, many tests

**The design.** A new `game/testing/TestHost.cs` + `--tests[=filter]` flag in `Main.cs` (one new `else if`, replacing none of the existing flags yet):

```csharp
// game/testing/GameTest.cs
public abstract partial class GameTest
{
    public abstract string Name { get; }            // "power.chain_passthrough"
    public virtual int Tier => 1;                   // 0 = smoke, 1 = feature, 2 = slow/composite — run order
    public virtual double TimeoutSimSeconds => 15;  // sim-time watchdog
    public Node3D World;                            // per-test sandbox, provided by the host
    protected TestContext T;                        // Check/Fail/log
    public abstract IEnumerable<Step> Run();        // coroutine, advanced once per physics tick
    protected Step Ticks(int n) => Step.Ticks(n);
    protected Step Until(Func<bool> cond, double maxSimSeconds = 5) => Step.Until(cond, maxSimSeconds);
}
```

`TestHost` (a Node added by Main when `--tests` is passed):

1. **Discovers** all `GameTest` subclasses via reflection, filters by the `--tests=power.*`-style glob, **sorts by Tier then name** — simplest failing case surfaces first, Factorio-style.
2. Per test: creates a fresh `Node3D` sandbox under itself, instantiates the test, then advances the coroutine in `_PhysicsProcess` — `yield Ticks(90)` consumes 90 physics ticks, `yield Until(() => item.Settled)` polls with a sim-time cap.
3. **Teardown**: `sandbox.QueueFree()`, wait one frame, reset known global state — this is a real hazard in this codebase and must be a checklist, not a hope: `PowerNet` statics `_dirty/_lastWires` (PowerNet.cs:13–14) via a new `PowerNet.ResetForTests()`, `WorldItem` statics (`NoDropRotation`, the model `_cache` can stay), `Engine.TimeScale`.
4. **Reports** (format in §4), writes `junit.xml` + `results.json`, `GetTree().Quit(failed == 0 ? 0 : 1)`.
5. **Error strictness**: `test.sh` captures stderr and fails the run if Godot `ERROR:` / `SCRIPT ERROR:` lines appear outside an allowlist. Cheap, and it catches the "silent red spam" class of regression the same way Factorio treats any error as failure.
6. `--failfast` stops at first failure (default in agent inner-loop; CI runs everything).

**Why coroutines**: the existing drivers are phase machines — `BrokenTestDriver`'s `switch(_phase)` (Main.cs:3468–3488) becomes linear, readable code: *drop → `yield Until(() => P.Broken, 4)` → check radius → consume medkit → `yield Ticks(2)` → check mended*. Same logic, half the code, impossible to forget the timeout branch (the host owns the watchdog).

**Making one boot fast enough:**

- **Boot cost is paid once.** Today: N scenarios × ~26 s. Proposed: 1 boot (~15 s) + Σ test sim-time. The full current driver corpus (~12 scenarios × 100–200 ticks) is ~25–30 s of 50 Hz sim-time *at real-time rates* — and we don't run at real-time:
- **Fast frame pumping.** Two mechanisms, in preference order, to be settled by a half-day spike:
  1. `--headless` + `Engine.TimeScale = 20` + raised `Engine.MaxPhysicsStepsPerFrame`. The CLAUDE.md "headless hangs" warning is about the *shot/movie* harnesses, which wait for rendered frames that never come (Main.cs:3355 even prints that hint). A `TestHost` that never touches the viewport and quits itself has no such dependency — physics and `_PhysicsProcess` run fine under the dummy renderer. If this works (verify in the spike), L1 needs **no xvfb, no lavapipe**, and the whole tier likely lands under ~30 s.
  2. Fallback (known-proven path): the existing movie-mode wrapper — `xvfb-run … --write-movie /tmp/t/m.avi --fixed-fps 60 -- --tests` — movie mode decouples frames from wall clock and pumps as fast as the CPU rasterizes; shrink the window to 320×180 to make lavapipe frames near-free.
- **Content lazily.** Tests that need the item catalog call `ItemCatalog.RegisterAll()` themselves (as `BuildItemTest` does at Main.cs:1427); the host loads nothing up front.
- **Parallel shards (later, Factorio-style):** `--tests=<glob>` already enables `./test.sh --shards 2` running two engine processes on disjoint tag sets (this box has 4 cores; lavapipe wants 2 of them — shard only in headless mode). Do this only when the suite outgrows ~90 s.
- **Not proposed: a warm persistent engine host.** Godot-mono assembly reload outside the editor is fragile; the complexity isn't worth it while a boot is ~15 s. Revisit only if L1 exceeds a few minutes.

**Determinism discipline (make it a rule, it's nearly free here):** fixed 50 Hz Jolt + `SimClock` already give per-tick determinism. Add: (1) every test that uses randomness seeds it — provide `T.Rng` (seeded from the test name, printed on failure, overridable via `UG_SEED` for replay); (2) assert positions/velocities with epsilons, never exact floats across physics; (3) never assert on wall-clock or render-frame counts — sim ticks only. Flakiness then has one remaining source (Jolt island ordering), which epsilon asserts absorb.

### L2 — visual golden-image tests (keep the eyeballs, add regression)

The `--shot` PNGs are genuinely good agent UX — keep that path untouched for interactive work. What's missing is *regressibility*. Add a manifest-driven wrapper, no engine changes needed:

`tests/visual/manifest.json`:
```json
[
  { "name": "deploy.wiretest_lamps",
    "args": ["--deploytest", "--shot={OUT}"],
    "env":  { "UG_WIRETEST": "1" },
    "tolerance": 0.02 },
  { "name": "vehicle.ambulance_side",
    "args": ["--vehicle={TMP}", "--gun=eaglefire"],
    "env":  { "UG_QUICK": "1", "UG_VSIDE": "2" },
    "capture": "rig_00.png",
    "tolerance": 0.015 }
]
```

`tools/visual_tests.py`: for each entry, run the existing xvfb/lavapipe command, then compare against `tests/visual/golden/<name>.png` — mean-absolute-error over RGB (PIL, ~15 lines) plus a changed-pixel count; on failure, write `<name>.diff.png` (amplified delta) next to the report and print the repro command. `--update <name>` re-baselines after a human/agent approves the new image. Baselines are small PNGs, committed to git (this repo already tracks binaries).

Flake control, in order of preference: capture frames are already deterministic per scenario (the settle-frame ladder moves into the manifest as data); lavapipe is a software rasterizer, so identical input → identical output on this box; the real variance sources are **particles and any `GD.Randomize`-seeded VFX** — golden scenes should either avoid live particles, freeze them (`UG_GOLDEN=1` → `SpeedScale=0` after preprocess), or carry a higher tolerance. Start with tolerance 2 % MAE and tighten per-scene; a wrong-shader or missing-mesh regression moves MAE by 10×that, so even loose tolerances catch the class of bug these scenes exist for.

Scope discipline: L2 is for **"does it render right"** (palette/paint shader, lamp glow, outline overlay, damage-stage smoke, ghost materials) — 10–15 curated scenes, run nightly + when touching render code. It is *not* for logic: every `[POWERTEST]`-style fact currently proven by looking at a PNG gets an L1 assertion instead, and the PNG becomes corroboration.

---

## 4. One output convention (agent-parseable ≡ human-readable)

All three layers emit the same line grammar on stdout:

```
[TEST] power.chain_passthrough        | PASS | 0.14s
[TEST] power.wire_clear_unpowers      | FAIL | consumer.Powered expected=False actual=True (after wire removed)
       repro: ./test.sh --only power.wire_clear_unpowers
       hint : simplest failing tier=1; see game/testing/PowerTests.cs:41
[TEST] item.trimesh_no_tunnel         | PASS | 1.82s
[SUITE] L1 in-engine: 23 passed, 1 failed, 0 skipped in 41.2s
[SUMMARY] TOTAL: 1121 passed, 1 failed | first failure: power.wire_clear_unpowers | junit: /tmp/ugtest/junit.xml
```

- **Agent greps**: `grep -E '^\[(TEST|SUITE|SUMMARY)\].*FAIL' out.log` — every failure carries expected/actual *and a copy-pasteable minimal repro* on the next line. Machine detail beyond that lives in `/tmp/ugtest/results.json` (name, tier, status, message, duration, repro, artifacts).
- **Humans read**: the same lines (aligned, optionally colored on a tty), plus `junit.xml` for any CI UI. `dotnet test` layers already produce TRX; add the `JunitXml.TestLogger` NuGet to the five test csprojs so L0 emits JUnit natively (`dotnet test --logger "junit;LogFilePath=..."`); `test.sh` merges the layer reports.
- **Simplest-failure-first** (Factorio's dependency ordering, pragmatically): run order is L0 → L1 tier 0 (smoke: engine boots, content manifest loads, one deployable spawns) → L1 tier 1 (features) → L1 tier 2 (composites like the PEI world smoke) → L2. `[SUMMARY]` always names the *first* failure in that order — that is the one to debug. A full dependency *graph* is overkill at this scale; three tiers gives 90 % of the value.
- **Exit codes**: 0 clean; 1 test failure; 2 infrastructure failure (boot hang, xvfb death) — so the agent can distinguish "my change broke logic" from "the harness broke."

`./test.sh` (repo root): `--l0`, `--l1`, `--visual`, `--all`, `--only <glob>`, `--failfast`, `--update <visual-name>`. Default = `--l0 --l1` (the sub-60 s set). This is *the* command in CLAUDE.md; nobody memorizes layer invocations.

---

## 5. TDD + agent bug-hunting workflow

**Inner loop** (this is Kovarex's "constant fast switching", made concrete for this repo):

1. Write the failing test in the cheapest layer that can express the bug. Power-net logic → L0 (`tests/UnturnedSim.Tests/PowerSolverTests.cs`, ~2 s feedback). Anything needing Jolt or nodes → L1 (`game/testing/*.cs`, ~20 s feedback incl. build). Render appearance → L2.
2. `./test.sh --only <name>` → confirm it fails *for the stated reason* (the runner prints expected/actual — check it, don't pattern-match on red).
3. Fix. Re-run `--only`. Then `./test.sh` (full L0+L1) before commit — sub-60 s keeps this honest.
4. **Regression rule**: every bug that reaches `main` gets a test named for the bug *in the same commit as the fix*, and PROGRESS.md's entry names the test. Recent history shows the gap this closes: commit `5bbe90d` ("stop dropped items falling through the ground") shipped with a repro *harness* (`UG_TRIMESH`, Main.cs:1440) but no guard — §6's `item.trimesh_no_tunnel` is exactly that harness promoted to a test, and it would now fail loudly if CCD regressed.

**Converting an existing harness scenario — the wire-power checks, worked through.** Today `[POWERTEST]`/`[MANAGETEST]`/`[FIRETEST-*]`/`[LAMPDBG]` are one env-flag-gated scene (Main.cs:1164–1188, 3335–3340, 3353) requiring up to four separate boots (`UG_WIRETEST`, `+UG_WIREMANAGE`, `+UG_WIREFIRE`, `+UG_WIREOFF`) and an agent that knows the tag zoo. They become four L1 tests sharing a helper (`PowerRig.Build(World)` — generator + spotlight + wire, lifted verbatim from `BuildDeployTest`), each ~15 lines, all in **one** boot, self-describing by name: `power.gen_powers_spotlight`, `power.wire_clear_unpowers`, `power.fire_stops_conduction`, `power.spotlight_lamps_follow_power`. The env flags then die (§7 table).

---

## 6. Worked examples

**(a) L0 — power chain passthrough (post-extraction, `tests/UnturnedSim.Tests/PowerSolverTests.cs`):**

```csharp
[Test]
public void Chain_Passthrough_Feeds_Second_Consumer_And_Loads_Generator()
{
    var gen  = Rig.Generator(watts: 4000);            // tiny builders over PowerDevice/PowerPort
    var spotA = Rig.Spotlight(usage: 250);
    var spotB = Rig.Spotlight(usage: 250);
    var wires = new[] { Rig.Wire(gen.Out, spotA.In), Rig.Wire(spotA.Pass, spotB.In) };

    PowerSolver.Solve(new[] { gen.Dev, spotA.Dev, spotB.Dev }, wires);

    Assert.That(spotA.In.Powered, Is.True);
    Assert.That(spotB.In.Live,    Is.EqualTo(3750f).Within(0.01f));   // leftover re-export
    Assert.That(spotB.In.Powered, Is.True);
    Assert.That(gen.Out.Draw,     Is.EqualTo(500f).Within(0.01f));    // both consumers traced
}
```
Run: `dotnet test tests/UnturnedSim.Tests --filter PowerSolver` → standard NUnit output, ~2 s total.

**(b) L1 — dropped item must not tunnel through thin trimesh (guards commit `5bbe90d`; `game/testing/ItemTests.cs`):**

```csharp
public partial class ItemTrimeshNoTunnel : GameTest
{
    public override string Name => "item.trimesh_no_tunnel";
    public override IEnumerable<Step> Run()
    {
        SDG.Unturned.ItemCatalog.RegisterAll();
        var ground = new StaticBody3D { CollisionLayer = 1 };          // the thin-trimesh repro from Main.cs:1440
        ground.AddChild(new CollisionShape3D { Shape = new PlaneMesh { Size = new Vector2(24, 8) }.CreateTrimeshShape() });
        World.AddChild(ground);
        var item = WorldItem.Spawn(World, new Item(15), new Vector3(0, 1.2f, 0));

        yield return Until(() => item.Settled, maxSimSeconds: 4);       // expose Settled (the _settled field exists)

        T.Check("settled before timeout", item.Settled);
        T.Check($"rests on surface (y={item.GlobalPosition.Y:F3})", item.GlobalPosition.Y > -0.05f);
    }
}
```

**(c) L1 — spotlight lamps follow power (the `[LAMPDBG]` + `[MANAGETEST]` facts, one test):**

```csharp
public partial class SpotlightLampsFollowPower : GameTest
{
    public override string Name => "power.spotlight_lamps_follow_power";
    public override IEnumerable<Step> Run()
    {
        var rig = PowerRig.Build(World);                                // gen + spotlight + wire (from BuildDeployTest)
        rig.Gen.TogglePower(); PowerNet.Recompute(GetTree());
        yield return Ticks(55);                                         // lamp warmup envelope (Main.cs:3351)
        T.Check("consumer powered", rig.Spot.DebugConsumerPowered);     // Deployable.cs:250-251
        T.Check("lamps lit",        rig.Spot.DebugLampsLit);

        rig.Wire.RemoveFromGroup("wires"); PowerNet.Recompute(GetTree());
        yield return Ticks(2);
        T.Check("unpowered after wire clear", !rig.Spot.DebugConsumerPowered);
        T.Check("lamps dark after wire clear", !rig.Spot.DebugLampsLit);
    }
}
```

Run everything: `./test.sh` →
```
[SUITE] L0 dotnet: 1108 passed in 9.8s
[TEST] smoke.engine_and_content       | PASS | 0.9s
[TEST] item.trimesh_no_tunnel         | PASS | 1.7s
[TEST] power.spotlight_lamps_follow_power | PASS | 1.3s
...
[SUITE] L1 in-engine: 14 passed in 38.5s
[SUMMARY] TOTAL: 1122 passed, 0 failed | junit: /tmp/ugtest/junit.xml
```

---

## 7. Migration plan (incremental, each phase independently useful)

**Phase 1 — the contract (do first).** `test.sh` wrapping the five existing `dotnet test` suites + JUnit logger + `[SUITE]/[SUMMARY]` lines. Add the regression-rule and `./test.sh` to CLAUDE.md. *No new code paths at risk.*

**Phase 2 — TestHost + first ports.** `game/testing/{GameTest,TestHost,TestContext}.cs`, `--tests` flag in Main.cs. Spike headless-vs-movie-mode pumping (half day, decides the L1 launch line). Port the five self-quitting drivers (Pronetest, Broken, Grenade, Melee, Fall — Main.cs:3362–3532) and the four wire-power checks. Delete the drivers + `UG_WIREMANAGE/WIREFIRE/WIREOFF` branches once green. **This phase kills most of the per-iteration boot tax.**

**Phase 3 — extraction to L0.** `PowerSolver` out of `PowerNet` (§3), explosion falloff, stance table. Each extraction lands with its NUnit suite.

**Phase 4 — visual goldens.** `manifest.json` + `tools/visual_tests.py` + `--update`; seed with ~10 scenes (wiretest lamps, deploy damage stages, ambulance side/vside, ghost valid/invalid, outline focus, night headlights). Nightly + on-demand.

**Phase 5 — robotic manager + extras.** Cron on this box: pull `main` fresh → `./test.sh --all` → on failure, Discord-ping the channel with the `[SUMMARY]` line + first-failure repro (blame the commit range since last green — `git log --oneline lastgood..HEAD`). Then, as appetite allows: 2-way L1 sharding, gdUnit4Net evaluation, coverlet coverage on `core/` (`dotnet test /p:CollectCoverage=true` — cheap since L0 is plain NUnit; in-engine coverage is not worth chasing yet).

**UG_* flag disposition** (pattern for all ~30): *becomes a named test* — `UG_WIRETEST/WIREMANAGE/WIREFIRE/WIREOFF/WIREWRECK`, `UG_TRIMESH/NOCCD`, `UG_DEPLOYDMG` (assert stage state in L1 + golden for looks); *becomes a manifest entry* — `UG_VSIDE/SIDE/CAMDIST/QUICK`, `UG_LOADBAR`, `UG_DEPLOYFOCUS`, `UG_WIREARROWS`; *stays a dev toggle* (legit interactive knobs) — `UG_CLIP`, `UG_NOROT`, `UG_FOCUS`, `UG_NAV*`, `UG_SEED`. Main.cs sheds its test-orchestration code but keeps the scene *builders* (goldens and tests both reuse them via helpers like `PowerRig`).

**Factorio extras scorecard:**

| Practice | Adaptation here | Value/effort |
|---|---|---|
| Robotic manager | cron + `./test.sh` + Discord ping (infra already exists) | **High / trivial** |
| Regression test per bug | convention in CLAUDE.md + PROGRESS.md linkage | **High / zero** |
| Simplest-failure-first | tier ordering + `--failfast` + `[SUMMARY]` first-failure | **High / trivial** |
| Full game headless in-test | L1 tier-2 `world.pei_smoke`: build PEI (`--peiplay` path), spawn player, 250 ticks, zero errors | High / medium |
| Parallel graphics tests | L2 runs concurrently with L1 in `test.sh`; 2 xvfb workers | Medium / low |
| Test-first the trickiest systems | order in Phase 2/3: power net, item physics, player stances/vitals, zombie sensing | High / it *is* the plan |
| Coverage tooling | coverlet on `core/` only | Medium / low (defer in-engine) |

**Assumptions to verify in the Phase 2 spike** (flagged, not silently relied on): (1) `--headless` + self-quitting TestHost doesn't hang (fallback: movie-mode wrapper, already proven); (2) `Engine.TimeScale` acceleration keeps Jolt results within the epsilons used (fallback: real-time ticks — the batched suite is still ~10× faster than today); (3) `GodotSharp` NuGet math structs behave identically to in-engine (they're the same managed code; a 5-test sanity suite pins it).

The end state: an agent's verification loop drops from *"N × 26 s boots + grep a tag zoo + eyeball PNGs"* to *"`./test.sh` — under a minute, one grep, PNGs only when the question is actually visual"* — and every bug the project has already paid to find stays found.
