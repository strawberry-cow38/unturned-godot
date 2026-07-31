"""Bake the harvest rules for every retail resource (tree/bush/rock) -> content/resources_harvest.tsv.

What a tree drops is NOT in the tree's .dat. `Reward_ID` is a legacy SPAWN TABLE id -- retail runs it
through SpawnTableTool.ResolveLegacyId -- and reading it as an item id is a trap that produces a
working-looking wrong port: birch is Reward_ID 515, and item 515 in our catalog is Cooked Venison.
Resolved properly, 515 is Spawns/Resources/Tree_Birch, a WEIGHTED table of Birch Log / Birch Stick.

So this walks two directories: Bundles/Trees/<name>/<name>.dat for the per-type numbers, and
Bundles/Spawns/**/*.dat for the reward tables, and emits the join.

Spawn .dat files come in two shapes and both are live in the same install:
  legacy  Tables 2 / Table_0_Asset_ID 37 / Table_0_Weight 60
  modern  Tables [ { LegacyAssetId 39  Weight 120 } ... ]
Parsing only one silently yields an empty table for the other half of the trees.

Output columns: name, assetId, health, rewardXp, resetSeconds, rewardMin, rewardMax, hasDebris,
isForage, and the resolved drops as `itemId:weight|itemId:weight|...`.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = os.path.normpath(os.path.join(ug_paths.objects_out(), "..", "resources_harvest.tsv"))


def dat_of(folder):
    """The asset .dat, never the English.dat localisation file beside it."""
    best = None
    for f in sorted(os.listdir(folder)):
        if not f.lower().endswith(".dat") or f.lower().startswith("english"):
            continue
        p = os.path.join(folder, f)
        txt = open(p, encoding="utf-8-sig", errors="ignore").read()
        if re.search(r"(?m)^\s*Type\s+\w+", txt):
            best = best or p
    return best


def key(txt, name, default=None):
    m = re.search(r"(?m)^\s*%s\s+(\S+)" % re.escape(name), txt)
    return m.group(1) if m else default


def flag(txt, name):
    return re.search(r"(?m)^\s*%s\s*$" % re.escape(name), txt) is not None


# ---- spawn tables, by legacy id ----------------------------------------------------------------
tables = {}
spawn_root = os.path.join(BUND, "Spawns")
for root, _dirs, files in os.walk(spawn_root):
    for f in files:
        if not f.lower().endswith(".dat") or f.lower().startswith("english"):
            continue
        txt = open(os.path.join(root, f), encoding="utf-8-sig", errors="ignore").read()
        if (key(txt, "Type") or "").lower() != "spawn":
            continue
        sid = key(txt, "ID")
        if not sid or not sid.isdigit():
            continue
        entries = []
        # modern block form
        for m in re.finditer(r"LegacyAssetId\s+(\d+)\s*\n\s*Weight\s+(\d+)", txt):
            entries.append((int(m.group(1)), int(m.group(2))))
        # legacy flat form
        for m in re.finditer(r"(?m)^\s*Table_(\d+)_Asset_ID\s+(\d+)", txt):
            i, aid = m.group(1), int(m.group(2))
            w = key(txt, "Table_%s_Weight" % i, "1")
            entries.append((aid, int(w) if w.isdigit() else 1))
        if entries:
            tables[int(sid)] = entries
print("spawn tables parsed:", len(tables))

# ---- resources ---------------------------------------------------------------------------------
rows = []
trees_root = os.path.join(BUND, "Trees")
for folder in sorted(os.listdir(trees_root)):
    d = os.path.join(trees_root, folder)
    if not os.path.isdir(d):
        continue
    p = dat_of(d)
    if not p:
        continue
    txt = open(p, encoding="utf-8-sig", errors="ignore").read()
    if (key(txt, "Type") or "").lower() != "resource":
        continue
    aid = key(txt, "ID", "0")
    reward_id = int(key(txt, "Reward_ID", "0") or 0)
    log_id = int(key(txt, "Log", "0") or 0)
    stick_id = int(key(txt, "Stick", "0") or 0)

    drops = []
    if reward_id and reward_id in tables:
        drops = tables[reward_id]
    elif reward_id:
        print("  WARNING: %s Reward_ID %d has no spawn table" % (folder, reward_id), file=sys.stderr)
    elif log_id or stick_id:
        # The fallback branch retail keeps for assets with no table. No vanilla tree uses it, but a
        # map/mod asset can, and a silently-empty drop list is worse than an explicit one.
        drops = [(i, 1) for i in (log_id, stick_id) if i]

    rows.append((
        folder, aid,
        key(txt, "Health", "0"), key(txt, "Reward_XP", "0"), key(txt, "Reset", "0"),
        key(txt, "Reward_Min", "0"), key(txt, "Reward_Max", "0"),
        "1" if flag(txt, "Has_Debris") else "0",
        "1" if flag(txt, "Forage") else "0",
        "|".join("%d:%d" % (i, w) for i, w in drops),
    ))

with open(OUT, "w", encoding="utf-8") as f:
    f.write("name\tassetId\thealth\trewardXp\tresetSec\trewardMin\trewardMax\thasDebris\tisForage\tdrops\n")
    for r in rows:
        f.write("\t".join(str(x) for x in r) + "\n")

harvestable = [r for r in rows if r[-1]]
print("wrote %d resources -> %s (%d with a resolved drop table)" % (len(rows), OUT, len(harvestable)))
for r in rows:
    if r[0].split("_")[0] in ("Birch", "Maple", "Pine") and r[0].endswith("_0"):
        print("  %-10s hp=%-5s xp=%-3s reset=%-4s rewards=%s-%s drops=%s"
              % (r[0], r[2], r[3], r[4], r[5], r[6], r[9]))
