# Unmerged branch verdicts — 2026-08-17

> **Merge phase VERIFIED.** Post-merge full sweep on `168c3855`: **2014 passed, 0 failed**, and the
> log is grep-clean of `NullReferenceException` / `SCRIPT ERROR` (checked by hand, because the harness
> does not check for them itself — see the review's finding on that). Up from 1958 pre-merge; the ten
> merged branches brought 56 tests with them. L0 1618, L1 370, L2 15.

Written after merging the ten tractable branches (`a650c9c1` → `168c3855`). These are the ones I did
**not** merge, with the reason and what it would actually take. Conflict counts are from
`git merge-tree` against main, which is a dry run — it does not touch the tree.

## The headline that reframes the whole audit

**The commit graph lies about what is unmerged here.** Five branches showed commits ahead of main and
carried *no* unmerged content at all (`feat/minicopter`, `feat/prop-instancing`,
`feat/building-tool`, `feat/barricades`, `3p-spine-lean` — all 0 ahead), and
`launcher-prune-branches` merged with a **zero-file content delta**. Meanwhile `mp-hitbox-debug`
showed 1 commit ahead and contained a real 270-line feature.

So neither "commits ahead" nor a keyword grep is a safe test. I got this wrong once in exactly that
way: I reported hitbox debug as already-on-main because 12 files matched the word "hitbox", when
those were incidental mentions in comments and the overlay itself was absent. **Check for the
defining symbol, not the topic word.**

---

## `feat-traps` — SUPERSEDED, with a real gap

*1 commit ahead, 651 behind, 7 conflicting paths.*

Main already implements traps, differently. Main has **Landmine, Spike, Charge, Barbedwire**; the
branch has **Landmine, Claymore, Barbedwire, Caltrop, Snare** built through a `MakeTrap` factory that
reads its values out of the ripped `.dat`. Merging duplicates Landmine and Barbedwire and clashes on
the shared deployable/item id space — which `DeployableDef`'s own comment warns about ("a door on
9140 shadows a magazine and neither errors").

**Verdict: do not merge.** Main's implementation is live and tested.

**The gap worth closing:** three trap types exist only on the branch — **Claymore, Caltrop, Snare**.
Porting those onto main's trap system is a small, well-defined feature task (three defs plus their
trigger behaviour), not a merge. The branch's `MakeTrap` is also worth reading first: it derives
`TrapLaunchSpeed` from `playerDamage * 0.1` because that is the source's documented default when the
key is absent, and none of the five `.dat` ship it.

---

## `feat-tree-harvest` — PORT, DO NOT MERGE

*23 ahead, 568 behind, **248 conflicting paths**, 171 files touched.*

This is the most valuable branch in the list and the least mergeable, and both facts have the same
cause: it is five features, not one, and it forked before ~570 commits of change to the files it
integrates with.

It adds, as **brand-new files that would merge without conflict at all**:

| Feature | Files |
|---|---|
| Tree/resource harvesting | `ResourceHarvestSim.cs`, `ResourceHarvestTable.cs`, `ResourceDebris.cs`, `resources_harvest.tsv`, `extract_resource_harvest.py` |
| Airdrops | `AirdropSim.cs`, `AirdropField.cs`, `AirdropCrate.cs`, `Dropship.cs`, `CarepackageFlare.cs`, `airdrop_nodes.tsv` |
| Safezones | `SafezoneSim.cs`, `SafezoneField.cs` |
| Signs | `SignText.cs`, `Sign.cs`, `SignWriteBox.cs` |
| Temperature | `TemperatureSim.cs`, `TemperatureField.cs` |

plus tests at all three levels (`TreeChopTests.cs`, `ServerChopTests.cs`,
`ResourceHarvestSimTests.cs`). **None of `ChopResource`, `TreeBar`, `SafezoneSim` or `FishingRod`'s
neighbours exist on main.**

The 248 conflicts are concentrated in the shared integration points — `PlayerController`,
`ResourceField`, `MeleeDef`, `WorldBuilder` — every one of which has changed enormously since the
fork (`PlayerController` alone is now 6.5k lines and was rewritten around client authority).

**Verdict: port it feature by feature, do not merge it.** Suggested order, smallest integration
surface first: signs → safezones → temperature → airdrops → tree harvesting. Take the new files
across wholesale, then re-attach each to today's `PlayerController`/`ResourceField` by hand. A merge
would produce 248 hand-resolved hunks with no test able to tell you which of them you got wrong —
and my own experience across the ten merges today is that the dangerous conflicts are exactly the
ones that still compile.

---

## `feat-safezone-sign-airdrop` — SUBSUMED by the above

*22 ahead, 573 behind, 245 conflicting paths.*

Its log is mostly the same tree commits, and its file set overlaps `feat-tree-harvest` almost
entirely — the two would conflict with **each other**, not just with main. Treat it as the same port,
and take whichever branch has the later version of each shared file (spot-check: this one's tip is
`a26c4f29 feat(trees): the swing that reaches the sim`, which is an *ancestor* of tree-harvest's tip,
so **tree-harvest wins** wherever they differ).

**Verdict: do not merge; fold into the tree-harvest port and delete.**

---

## `dedupe-tier1` — DOCS, mostly already actioned

*17 ahead, 571 behind, 233 conflicting paths, 133 files.*

The commits are overwhelmingly `docs(audit): N.NN dropped/checked` — an audit trail of a
deduplication review, most entries recording that an item was **dropped** after being checked. The
code changes it made have largely been overtaken.

**Verdict: do not merge.** If the audit document itself is worth keeping, `git show
dedupe-tier1:<path>` it onto main as a single docs commit rather than merging 233 paths of drift.

---

## Larger stale branches, not assessed in depth

- `base-defense-mp` — 42 ahead, 733 behind, 27 conflicts. Worth a real look; the conflict count is
  low relative to its size, which usually means it touches files main left alone.
- `merge-integration` — 50 ahead, 681 behind, 29 conflicts. An old integration branch; likely
  superseded wholesale, but that is a claim I have not verified.
- `mp-vitals` — 7 ahead, 950 behind, 43 conflicts. The vitals work it describes ("consume becomes
  server-authoritative") appears to have landed on main by another route; **verify by symbol before
  believing that**, per the headline above.
