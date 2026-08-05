# Branch audit — 2026-08-05

strawberry: *"do a scan of all the branches we have. collapse what we dont need and see what would be
easy to merge without issues."*

Every branch trial-merged against `main` at `d9c2c43b`, so the conflict counts here are **measured,
not estimated**. This supersedes the NUMBERS in `BRANCH_MERGE_AUDIT.md` (2026-07-28) — that audit ran
when `main` was ~240 commits younger and every ahead/behind figure in it is now wrong. Its *reasoning*
still stands and is worth reading; only the arithmetic rotted. Which is the lesson for this page too,
so re-run the scan rather than trusting it once `main` moves:

```bash
for b in $(git branch -r --format='%(refname:short)' | grep -v HEAD | grep -v origin/main); do
  git merge-base --is-ancestor $b main && { echo "$b IN-MAIN"; continue; }
  echo "$b $(git rev-list --count main..$b) ahead :: $(git merge-tree --write-tree --name-only main $b \
    | grep -c '^CONFLICT') conflicts"
done
```

---

## 1. Already in `main` — delete, nothing is lost

`git merge-base --is-ancestor <branch> main` succeeds for each: every commit is reachable from `main`.

**Remote (13):** `boats-integration` · `boattest` · `durability-chip` · `feat-doors-beds-deadzones` ·
`fluid-io` · `headlight-beams` · `inv-source-tinyclaw` · `inv-trim` · `paperdoll-spin` ·
`shadow-cap` · `streetlight-glow-shootout` · `terrain-heightfield-collider` · `weapon-slots`

**Local (2):** `inv-gate` · `streetlight-emissive-lens`

## 2. Content is in `main`, the branch is not — delete, do not merge

These would **replay work already merged**, and conflict while doing it. Worth separating from §1
because the ancestor check says nothing about them: they look like legitimate unmerged features right
up until the duplicate lands.

| branch | why |
|---|---|
| `origin/watertest` (6) | rebased copies came in via `boats-integration`. A merge replays them — 3 conflicts, including add/add on `SwimTests.cs` |
| `beacon-review-fixes` (24, local) | contained in `origin/base-defense-mp` |
| `mp-fixtures` (27, local) | contained in `origin/base-defense-mp` |
| `sentry-mp` (30, local) | contained in `origin/base-defense-mp` |
| `mp-predict-a` (1, local) | its one commit is "build artifacts (tracked bin/obj churn)" — no source at all |

## 3. Merges clean today — zero conflicts

| branch | ahead | what | call |
|---|---|---|---|
| `origin/docs-function-index` | 3 | per-function reference, 675 functions, verified call sites | **merge** — docs only, no runtime risk |
| `origin/vm-capture` | 1 | composites the viewmodel viewport into the `--vm` still | **merge** — makes viewmodel renders actually show the viewmodel |
| `origin/feat-mp-ownership` | 1 | base ownership gate, closes five security TODOs | **merge**, but MP-facing: wants a second player to verify, not a green suite |
| `origin/zombie-shadow-diag` | 1 | disables skinned-character shadows to isolate a GPU cost | **delete** — a diagnostic experiment. Its finding belongs in a doc, not a branch |

## 4. One conflicted file — the cheap wins

| branch | ahead | conflict | note |
|---|---|---|---|
| `origin/water-splash` | 1 | `Terrain.cs` | retail splash on bullet impact + explosions in water. **The most valuable of these now that water has shipped** — the ocean currently swallows bullets in silence |
| `origin/vehicle-prop` | 1 | `WorldBuilder.cs` | vehicles collide with small props + crash-damage destructibles |
| `origin/gun-mags-2` | 5 | `Viewmodel.cs` | real magazine meshes + textures, 23–24 guns; mostly content |
| `origin/scope-zoom-wip` | 21 | `Viewmodel.cs` | 21 commits behind a **single** conflicted file — far cheaper than its size suggests |
| `origin/dedupe-tier1` | 20 | `ConnectionPort.cs` | same shape: large branch, one seam |
| `origin/renderstats` | 2 | `Main.cs`, `PlayerController.cs` | headless render-stat probe. Diagnostic — harvest into `tools/`, then delete |

### The two inventory branches still conflict with each other
`origin/ui-inv-fixes` (6) and `origin/inv-source-layout` (1) both touch **only**
`game/inventory/InventoryUI.cs`, and each conflicts with `main` in that same file. They are competing
designs for one screen: pick one, delete the other. Merging both means resolving the same file twice
against two different intents.

## 5. Real integrations — several conflicted files each

| branch | ahead | conflicted files |
|---|---|---|
| `origin/vehiclerework` | 12 | `DevConsole.cs`, `Vehicle.cs` |
| `origin/streetlight-mote-kill` | 2 | `ObjMesh.cs`, `StreetLight.cs`, `WorldBuilder.cs` |
| `origin/feat-tree-harvest` | 23 | `DeployableDef.cs`, `DevConsole.cs`, `Main.cs`, `PlayerController.cs` |
| `origin/feat-safezone-sign-airdrop` | 22 | the same four |
| `origin/feat-fishing` | 5 | `ItemAsset.cs`, `PlayerController.cs`, `Terrain.cs`, `InventoryUI.cs`, `InventoryTests.cs` |
| `origin/base-defense-mp` | 45 | 5 files — the only branch that took MP seriously |
| `origin/asset-factory` | 88 | 6 files — the largest |

**Local-only and unique.** No remote copy exists, so deleting these *does* lose work:
`editor-map-port` (6), `mp-vitals` (7), `mp-netobserve` (2), `feat-traps` (1), `feat-weather` (1),
`mp-hitbox-debug` (1), `sp-mp-unify` (1), `launcher-prune-branches` (1), `merge-integration` (50).

Push them somewhere before touching them — they exist on exactly one disk. `merge-integration` is a
stale integration attempt whose merges predate ~250 commits of `main`; its only value was the conflict
resolutions inside it, and those no longer apply to anything.

---

## Two gates worth keeping

**"Does a second player see it" is a merge gate, not a follow-up.** Carried from the 2026-07-28 audit
and still earning its place: `boats-integration` landed today with exactly that hole left open and
written down — swim state is not on the wire, so a remote player swimming is not shown swimming.

**A branch can be correct and still be wrong once merged.** `SwimStep` took its aim from the camera
basis. That was right on its branch, where third person was a fixed chase cam roughly down the look
axis, and wrong against `main`'s over-the-shoulder camera, which sits 2 m back and toed in 5°. Neither
side's tests could have caught it — the branch had no such camera, `main` had no water. **When a
branch reads shared state that `main` has since redefined, the seam needs its own test**, because
both halves passing is exactly what you will observe.
