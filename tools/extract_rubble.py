#!/usr/bin/env python3
"""Extract the DESTRUCTIBLE-PROP (rubble) data for the placed PEI objects, from the retail object .dat files.

Retail model (U3-SDK ObjectAsset / InteractableObjectRubble): an object with `Rubble` != None is
destructible -- it has `Rubble_Health` (ushort) per section, breaks when a section hits 0, respawns after
`Rubble_Reset` seconds, plays `Rubble_Effect`, and drops `Interactability_Drops` loot ids. We only need the
per-GUID scalars here (health/reset/effect/drops + destroy-vs-section mode); the mesh already extracts via
guid_mesh. Pure text parse of ~/unturned-bundles (no masterbundle) -> game/content/objects/rubble.txt, one
line per PLACED destructible GUID:

    <guid> <health> <reset> <mode> <ndrops> <dropId>...

mode: 0=DESTROY (whole object vanishes), 1=SECTION (per-section), 2=HUMAN (ragdoll). Loaded at world-build.
"""
import os, re, glob, sys

HOME = os.path.expanduser("~")
BUND = os.path.join(HOME, "unturned-bundles", "Bundles", "Objects")
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLACE = os.path.join(REPO, "game", "content", "objects", "placements.txt")
OUT = os.path.join(REPO, "game", "content", "objects", "rubble.txt")

RUBBLE_MODE = {"none": None, "destroy": 0, "section": 1, "human": 2}

def kv(txt, key):
    m = re.search(r"(?im)^\s*" + re.escape(key) + r"\s+(\S+)", txt)
    return m.group(1) if m else None

# 1) parse every object .dat -> guid -> rubble scalars (only the destructible ones)
guid_rubble = {}
for datp in glob.glob(os.path.join(BUND, "**", "*.dat"), recursive=True):
    # utf-8-sig strips the BOM the Unturned .dat files carry -- otherwise the FIRST line
    # (which holds GUID) never matches ^\s*GUID (\s doesn't consume ﻿), silently zeroing the catalog.
    try: txt = open(datp, "r", encoding="utf-8-sig", errors="ignore").read()
    except Exception: continue
    g = kv(txt, "GUID")
    if not g: continue
    mode = RUBBLE_MODE.get((kv(txt, "Rubble") or "none").lower(), None)
    if mode is None: continue   # not destructible
    # retail: rubbleIsVulnerable = !dat.ContainsKey("Rubble_Invulnerable") (ObjectAsset.cs:1113); gun/melee
    # only damage the rubble when vulnerable (UseableGun/UseableMelee). An invulnerable-flagged prop looks
    # like rubble but eats bullets forever, so it isn't a weapon-destructible -- exclude it. (0 on PEI today.)
    if re.search(r"(?im)^\s*Rubble_Invulnerable\b", txt): continue
    health = int(kv(txt, "Rubble_Health") or 0)
    if health <= 0: continue    # rubble with no health = indestructible in practice
    reset = int(float(kv(txt, "Rubble_Reset") or 0))
    ndrops = int(kv(txt, "Interactability_Drops") or 0)
    drops = []
    for i in range(ndrops):
        d = kv(txt, f"Interactability_Drop_{i}")
        if d: drops.append(int(d))
    effect = int(kv(txt, "Rubble_Effect") or 0)   # the retail break effect id (-> game/content/effects/rubble_fx.json)
    guid_rubble[g.lower()] = (health, reset, mode, effect, drops)

# 2) keep only GUIDs actually placed on PEI (focus + smaller table)
placed = set()
if os.path.exists(PLACE):
    for line in open(PLACE, errors="ignore"):
        p = line.split()
        if p: placed.add(p[0].lower())

lines, n = [], 0
for g, (health, reset, mode, effect, drops) in sorted(guid_rubble.items()):
    if placed and g not in placed: continue
    lines.append(f"{g} {health} {reset} {mode} {effect} {len(drops)}" + ("".join(f" {d}" for d in drops)))
    n += 1

os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, "w").write("\n".join(lines) + ("\n" if lines else ""))
print(f"[rubble] {len(guid_rubble)} destructible object types in the catalog; {n} of them placed on PEI -> {OUT}")
if n:
    print("[rubble] sample:", lines[0])
