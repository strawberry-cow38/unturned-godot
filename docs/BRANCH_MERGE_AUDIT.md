# Unmerged branch audit — what it would take to land 217 commits

> **Superseded for NUMBERS by [`BRANCH_AUDIT_2026-08-05.md`](BRANCH_AUDIT_2026-08-05.md).** `main` has
> moved ~240 commits since this was written, so every ahead/behind figure and conflict claim below is
> stale. The REASONING here — the MP gate, the per-branch judgement calls, the evidence rules — still
> stands and is why this file is kept rather than replaced.


Requested by VoX, 2026-07-28: *"prepare all unmerged work (in our unturned feature branches) for
merging, remember to check for the entire feature flow, destruction, usage by players, MP support,
everything."*

This is the audit, not the merging. 15 branches are ahead of `main` by **217 commits** and most are
**130–171 commits behind** it; every one is a rebase with judgement calls, not a fast-forward. The
point of this document is to say which are nearly-there, which are half-features, and which should
never merge at all — before anyone spends a night rebasing the wrong one.

**Evidence rules used here.** `[VERIFIED]` = read from source at a cited file:line or a cited grep
over branch content. `[HEURISTIC]` = a pattern match over the branch diff (e.g. "adds no
`core/UnturnedNet/` change"), which is suggestive but is **not** proof a feature is broken — a change
to an already-replicated system can inherit replication without touching net code. `[UNVERIFIED]` =
stated by someone, or inferred, and not yet checked. Nothing here is asserted from a branch name or
a commit message.

---

## 0. The headline: the same bug, thirteen times

On 2026-07-27 the zombie rewrite turned out to be unshootable, deaf and invisible in multiplayer,
because every gameplay surface addressed a node group the new zombies could not join — code with
full unit coverage and **zero callers**. That was not a one-off.

**Of the 15 unmerged branches, exactly one (`base-defense-mp`) touches the replication layer at
all.** [VERIFIED — `git diff --name-only <merge-base> <branch> | grep UnturnedNet` over all 15]

That does not automatically mean the other 14 are broken in MP, and this document does not claim it
does. It means **the MP question has not been asked** of any of them, and where it has been asked
here, the answer has so far always been "gap":

- `asset-factory` — **[VERIFIED gap]** touches zero net files; `DeployableReplication.cs` on that
  branch contains 0 occurrences of `factory`. Factory-authored item/deployable ids are not in the
  replicated schema, so a second player sees none of them. Independently flagged by cow tools, who
  built it, and confirmed here rather than taken on trust.
- `watertest` (swimming) — **[VERIFIED gap]** `PlayerReplication.cs:127` documents the `Buttons`
  byte as *"bit 0 = jump; bits 1-2 = the on-foot stance"*, and the struct's fields are MoveX, MoveY,
  YawDegrees, Buttons, Jump (`PlayerReplication.cs:141-146`). There is no swim state on the wire, so
  a remote player swimming will not be shown swimming.

**Recommendation to VoX:** treat "does a second player see it / can a second player use it" as a
merge gate for every branch below, not as a follow-up. It is the failure mode this codebase actually
has, twice confirmed in two days.

---

## 1. Do not merge — diagnostics and duplicates

| Branch | Ahead | Verdict | Why |
|---|---|---|---|
| `renderstats` | 2 | **DIAGNOSTIC-ONLY** | Both commits are self-described diagnostics: a headless render-stat probe (`UG_RENDERSTATS`) and `UG_3P` to force a 3rd-person drive cam for reproducing a GPU tank. Instrumentation for one investigation. |
| `zombie-shadow-diag` | 1 | **DIAGNOSTIC-ONLY** | Single commit literally titled `DIAGNOSTIC: disable skinned-character shadow casting`. Merging it would disable character shadows game-wide. |
| `vm-capture` | 1 | **DIAGNOSTIC-ONLY** | Composites the viewmodel viewport into the `--vm` still capture. A capture-harness tweak. |
| `boattest` | 21 | **ABANDON — redundant** | **[VERIFIED]** `git merge-base --is-ancestor origin/boattest origin/boats-integration` succeeds: every commit is already contained in `boats-integration`. Delete it; do not audit or rebase it separately. |

Diagnostics are worth *reading* before deletion — `renderstats` in particular measures the render
line that the zombie perf work needed. Harvest anything useful into a tool, then delete the branch.
Leaving them alive implies they are pending work, which is how a 15-branch backlog happens.

---

## 2. The two inventory branches conflict with each other

`ui-inv-fixes` (6 ahead, 75 behind) and `inv-source-layout` (1 ahead, 72 behind) both change **only**
`game/inventory/InventoryUI.cs`, and **[VERIFIED]** neither contains the other
(`merge-base --is-ancestor` fails both ways). They are competing designs for the same screen:

- `ui-inv-fixes`: *"compact layout (undo the gappy spread) + clothing equip slots as horizontal tab
  grid"*, *"move PRIMARY/SECONDARY to the bottom-left under the clothing column (retail placement)"*
- `inv-source-layout`: *"match REAL PlayerDashboardInventoryUI — no clothing-slot list, char render
  fills the box"*

**Verdict: NEEDS-WORK (a decision, not code).** Landing both in either order guarantees a conflict in
one file and a layout that is neither design. This is a call for strawberry — pick one, delete the
other. Cheapest item on this list and it is blocked on a human, so ask early.

---

## 3. Feature branches

Ordered by how close to landable they look. `Destruction` / `MP` / `Player-reach` are
**[HEURISTIC]** unless a cited note upgrades them.

| Branch | Ahead / Behind | Tests | Destruction | MP | Player-reach | Verdict |
|---|---|---|---|---|---|---|
| `base-defense-mp` | 45 / 157 | 7 | yes | **yes** | yes | NEEDS-WORK (rebase) |
| `boats-integration` | 22 / **31** | 1 | — | — | yes | NEEDS-WORK (MP unasked) |
| `asset-factory` | 88 / 130 | 0 | — | **gap [VERIFIED]** | yes | NEEDS-WORK |
| `feat-fishing` | 5 / 156 | 3 | — | — | yes | NEEDS-WORK (MP unasked) |
| `watertest` | 6 / 165 | 1 | — | **gap [VERIFIED]** | yes | NEEDS-WORK |
| `vehiclerework` | 12 / 171 | 1 | — | — | — | NEEDS-WORK (see note) |
| `vehicle-prop` | 1 / 130 | 0 | **yes** | — | yes | NEEDS-WORK (small) |
| `water-splash` | 1 / 130 | 0 | — | — | yes | LIKELY MERGE-READY (small) |
| `gun-mags-2` | 5 / 139 | 0 | — | — | — | LIKELY MERGE-READY (content) |

### base-defense-mp — 45 ahead, 157 behind, 7 test files
The only branch that took MP seriously: it changes `DeployableReplication`, `NetWorldHost`,
`PlayerReplication`, `ServerCombat`, `ServerTransactions` and adds `core/UnturnedSim/SentryTargeting.cs`.
Recent commits are *fixes* (`fix(sentry): HIGH — placed sentries could never be powered`,
`base-defense: fix zombie-target crash (MED) + 2 lows from fable review`), which reads as a branch
that was reviewed and repaired rather than abandoned.

**Risk: it is 157 commits behind and its conflict surface is the entire net layer** — the same five
files today's zombie replication work touched. This is the highest-value and highest-risk merge on
the list. Rebase it early, while today's net changes are still fresh in someone's head, not in a
month. Its `SentryTargeting` also needs re-checking against the new zombie sim: sentries target
zombies, and zombies stopped being nodes. **[UNVERIFIED — must check before merge.]**

### boats-integration — 22 ahead, only 31 behind
**The least stale branch on the list by a wide margin** and therefore the cheapest real merge.
Contains `boattest` entirely. Adds the runabout + APC (body/palette/light content files), wires PEI
runabout coast spawns, and a 4th trap type (Barbed Wire). Conflict surface is only 3 files
(`Main.cs`, `PlayerController.cs`, `WorldBuilder.cs`).

Open questions before merge: do boats replicate (a boat is a vehicle, and vehicles already
replicate — this may be inherited, **[UNVERIFIED]**), and does Barbed Wire have destruction +
MP like the other traps?

### asset-factory — 88 ahead, 130 behind
The AssetFactory authoring tool: compose meshes into an asset, hand-place colliders/volumes/named
hook points with a gizmo, save a self-contained `.assetbundle` the game auto-loads
(`game/AssetFactoryEditor.cs`, 995 lines). Its own header says *"no more guessing mounts from bundle
math"* — the same problem strawberry hits tuning props over chat.

Blockers: the **[VERIFIED]** MP schema gap above; an 88-commit rebase whose conflict surface
includes `PlayerController.cs` and `Main.cs`, where today's zombie combat wiring and profiler work
landed; and **0 test files**. Those hunks must be resolved *against* the zombie changes rather than
by taking a side.

### vehiclerework — 12 ahead, 171 behind (the stalest)
Rope-tow force scaled by towed mass, steering debuff softened −35%→−15%, touching `Vehicle.cs`,
`DevConsole.cs` and a tow test. **Main has moved 171 commits since**, and vehicles/tow have been
worked on repeatedly in that window. **Before rebasing, check whether main already contains
equivalent tow fixes** — "not merged" is not "not built", and re-landing a superseded tuning change
would regress current behaviour. **[UNVERIFIED — this check is the first task on this branch.]**

### The three one-commit features
- `vehicle-prop` — vehicles collide with small props + crash-damage destructibles. Touches
  `MpLoopback.cs`, so it has at least been thought about in a loopback context.
- `water-splash` — retail water splash on bullet impact and explosions in water. Cosmetic; the most
  likely true fast-forward on the list.
- `inv-source-layout` — see §2, blocked on the design decision.

---

## 4. Recommended landing order

1. **Delete `boattest`** (contained), and **read-then-delete** `renderstats`, `zombie-shadow-diag`,
   `vm-capture`. Removes 4 of 15 with no risk and makes the real backlog visible.
2. **Ask strawberry to pick an inventory design** (§2). Blocked on a human, so ask now.
3. **`water-splash`, `gun-mags-2`** — small, low conflict, build confidence in the rebase routine.
4. **`boats-integration`** — only 31 behind, so it will only get harder; answer the boat-replication
   question first.
5. **`base-defense-mp`** — highest value, and its net-layer conflicts should be resolved while
   today's replication work is fresh.
6. **`asset-factory`** — after streetlights land (cow tools wants that as a clean base), and only
   with the MP schema gap either closed or explicitly accepted as a known limitation.
7. **`vehiclerework`** — last, and only after confirming main has not already superseded it.

Ownership note: strawberry gave tinyclaw standing merge authority, and told cow tools to merge their
own work. `asset-factory` is cow tools'; its `PlayerController.cs` / `Main.cs` conflicts sit on
tinyclaw's zombie code, so that rebase is a coordinated one — agreed 2026-07-28.

---

---

## Appendix: per-branch conflict surface

Files each branch changes that `main` has **also** changed since the fork point — i.e. where a
rebase will actually stop and ask. Generated by `comm -12` over the two diff file-lists against
each branch's own merge-base; `bin/`+`obj/` build artifacts excluded (the repo tracks them, and
they will conflict on every branch without meaning anything).

| Branch | Files changed | Conflicting files |
|---|---|---|
| `asset-factory` | 14+ | `game/Deployable.cs`, `game/DeployableDef.cs`, `game/DeployablePlacer.cs`, `game/DevConsole.cs`, `game/Main.cs`, `game/MainMenu.cs`, `game/PlayerController.cs`, `game/Vehicle.cs` |
| `base-defense-mp` | 13+ | `core/UnturnedNet/DeployableReplication.cs`, `core/UnturnedNet/NetWorldHost.cs`, `core/UnturnedNet/PlayerReplication.cs`, `core/UnturnedNet/ServerCombat.cs`, `core/UnturnedNet/ServerTransactions.cs`, `game/ClientWorldSession.cs`, `game/DedicatedServer.cs`, `game/DeployableDef.cs` |
| `boats-integration` | 14+ | `game/Main.cs`, `game/PlayerController.cs`, `game/WorldBuilder.cs` |
| `boattest` | 14+ | `game/Main.cs`, `game/PlayerController.cs`, `game/RiggedCharacter.cs`, `game/Vehicle.cs`, `game/Viewmodel.cs`, `game/WorldBuilder.cs`, `tools/montage_guns.py` |
| `vehiclerework` | 3+ | `game/DevConsole.cs`, `game/Vehicle.cs` |
| `watertest` | 8+ | `game/Main.cs`, `game/PlayerController.cs`, `game/RiggedCharacter.cs`, `game/Viewmodel.cs`, `tools/montage_guns.py` |
| `ui-inv-fixes` | 1+ | `game/inventory/InventoryUI.cs` |
| `gun-mags-2` | 14+ | `game/Viewmodel.cs` |
| `feat-fishing` | 10+ | `core/UnturnedSim/ItemAsset.cs`, `game/PlayerController.cs`, `game/inventory/InventoryUI.cs`, `game/inventory/ItemCatalog.cs`, `game/testing/tests/InventoryTests.cs` |
| `renderstats` | 2+ | `game/Main.cs`, `game/PlayerController.cs` |
| `zombie-shadow-diag` | 1+ | `game/RiggedCharacter.cs` |
| `water-splash` | 2+ | `game/PlayerController.cs` |
| `vm-capture` | 2+ | `game/Main.cs`, `game/Viewmodel.cs` |
| `vehicle-prop` | 3+ | `game/MpLoopback.cs`, `game/Vehicle.cs`, `game/WorldBuilder.cs` |
| `inv-source-layout` | 1+ | `game/inventory/InventoryUI.cs` |

`boats-integration` (3 conflicts, 31 behind) and the one-commit branches are the cheap ones.
`base-defense-mp` conflicts across five net-layer files and `asset-factory` across eight, both
including files today's zombie work touched — those two are the coordinated rebases.

---

## 5. What this audit does NOT establish

Stated plainly, because the failure this codebase keeps hitting is confident claims nobody checked:

- **Destruction / player-reach columns are [HEURISTIC]** — a diff-pattern match, not a play-test.
  A `yes` means the branch touches code of that shape, not that a player can reach it and break it.
- **A `—` in the MP column is "not asked", not "broken".** Only `asset-factory` and `watertest` have
  verified gaps. `boats-integration`, `feat-fishing`, `vehiclerework`, `vehicle-prop` and
  `water-splash` each need the question answered properly before merge.
- **Nothing here was built or run.** No branch was checked out, compiled, or play-tested; this is
  static analysis of diffs plus targeted source reading. A branch marked LIKELY MERGE-READY has not
  been proven to compile against current main.
- **`vehiclerework` may be entirely superseded** and no one has checked.
