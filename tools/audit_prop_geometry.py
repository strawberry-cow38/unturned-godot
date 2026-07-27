#!/usr/bin/env python3
"""audit_prop_geometry -- does our extracted geometry match what retail actually renders?

Written after the Street_Light_0 miss, where a permanently-visible concrete base was dropped
because its MESH asset happened to be named "Model_0_Dead" while the extractor filtered on mesh
name. The lesson was that grepping for the symptom finds one bug; deriving the rule finds the
class. So this compares two computed sets per prop:

    RETAIL   what Unity draws for a freshly-placed, unpowered object
    OURS     what tools/extract_objects_v2.py writes into the .obj

and reports every disagreement, in BOTH directions:

  MISSING       retail draws it, we dropped it        -> holes in the model (the street light base)
  BAKED_ON      retail hides it by default, we kept it -> permanently-lit / permanently-open props

Retail's rules, from the decompiled game rather than from convention:
  * InteractableObjectRubble.updateRubble only ever SetActives the children literally named
    "Alive" / "Dead". Anything parented elsewhere renders in BOTH states -- so a part is hidden
    only if a Dead/Ragdoll/Effect/Finale/Drop ancestor owns it, never because of its mesh name.
  * InteractableObjectBinaryState.initToggleGameObject/updateToggleGameObject find a child named
    "Toggle" and SetActive it to `isUsed` (or `isUsed && isWired` when Interactability_Power is
    STAY). A fresh unpowered object therefore renders with the Toggle subtree OFF.
  * A Unity LODGroup only governs the renderers it lists. A renderer in NO LOD level is drawn at
    every distance, so selecting "LOD0 members" alone silently drops it.

Usage:  python3 tools/audit_prop_geometry.py [--all]
        (default lists only props placed on PEI, i.e. ones a player can actually walk up to)
"""
import os
import re
import sys
import glob

import numpy as np
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()

# subtrees retail hides on a fresh, undamaged, unpowered object
HIDDEN = {"dead", "ragdoll", "ragdolls", "effect", "finale", "drop", "editor"}
TOGGLE = "toggle"
# the extractor's own node-name prune list (tools/extract_objects_v2.py walk())
SKIP = {"dead", "ragdoll", "effect", "nav", "block", "trap"}


def main():
    show_all = "--all" in sys.argv
    # --selfcheck re-introduces the mesh-name "dead" filter this audit was written to catch, and
    # asserts the two props it hid come back as MISSING. A detector that cannot reproduce its own
    # founding bug is decoration; this is the one assertion that proves it has teeth.
    selfcheck = "--selfcheck" in sys.argv

    guid_of, name_of = {}, {}
    for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
        try:
            txt = open(datp, "r", errors="ignore").read()
        except OSError:
            continue
        m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
        if not m:
            continue
        folder = os.path.basename(os.path.dirname(datp))
        rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
        cont = "assets/coremasterbundle/" + rel + "/object.prefab"
        guid_of[cont] = m.group(1).lower()
        name_of[cont] = folder
        pw = re.search(r"^Interactability_Power\s+(\S+)", txt, re.M)
        name_of[cont + "#power"] = pw.group(1) if pw else ""

    shipped = {l.split()[0] for l in open(os.path.join(OUT, "guid_mesh.txt")) if l.split()}
    placed = {l.split()[0] for l in open(os.path.join(OUT, "placements.txt")) if l.split()}

    env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
    by_id = {o.path_id: o for o in env.objects}

    def comps(tt):
        for comp in tt.get("m_Component", []):
            c = comp.get("component", comp) if isinstance(comp, dict) else comp
            pid = c.get("m_PathID") if isinstance(c, dict) else None
            co = by_id.get(pid)
            if co:
                yield co

    def comp_of(tt, names):
        for co in comps(tt):
            if co.type.name in names:
                return co
        return None

    def find_lodgroup(go, depth=0):
        if not go or depth > 10:
            return None
        tt = go.read_typetree()
        kids = []
        for co in comps(tt):
            if co.type.name == "LODGroup":
                return co
            if co.type.name == "Transform":
                kids = co.read_typetree().get("m_Children", [])
        for ch in kids:
            ct = by_id.get(ch.get("m_PathID"))
            if ct:
                r = find_lodgroup(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")), depth + 1)
                if r:
                    return r
        return None

    findings = []
    for path, obj in env.container.items():
        if obj.type.name != "GameObject" or not path.lower().endswith("/object.prefab"):
            continue
        cont = path.lower()
        guid = guid_of.get(cont)
        if not guid:
            continue
        prop = name_of.get(cont, "?")
        power = name_of.get(cont + "#power", "")

        # ---- full tree: every mesh-bearing node, with the ancestry that decides visibility ----
        nodes = {}     # go_pid -> dict(path, mesh, renderer, hidden, toggled, pruned)

        def tree(pid, trail, hidden, toggled, pruned):
            go = by_id.get(pid)
            if not go:
                return
            tt = go.read_typetree()
            nm = (tt.get("m_Name", "") or "")
            low = nm.lower()
            hidden = hidden or low in HIDDEN
            toggled = toggled or low == TOGGLE
            pruned = pruned or low in SKIP
            tr = comp_of(tt, ("Transform", "RectTransform"))
            if not tr:
                return
            here = trail + "/" + nm
            mf = comp_of(tt, ("MeshFilter",))
            mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
            if mp and mp in by_id:
                mr = comp_of(tt, ("MeshRenderer",))
                nodes[pid] = dict(path=here, mesh=by_id[mp].read_typetree().get("m_Name", ""),
                                  rend=mr.path_id if mr else None, hidden=hidden,
                                  toggled=toggled, pruned=pruned)
            for ch in tr.read_typetree().get("m_Children", []):
                ct = by_id.get(ch.get("m_PathID"))
                if ct:
                    tree(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"),
                         here, hidden, toggled, pruned)

        tree(obj.path_id, "", False, False, False)
        if not nodes:
            continue

        lg = find_lodgroup(obj)
        lod_of = {}          # renderer path_id -> set of LOD indices
        if lg:
            for i, lod in enumerate(lg.read_typetree().get("m_LODs", [])):
                for r in lod.get("renderers", lod.get("_renderers", [])):
                    rp = (r.get("renderer") or {}).get("m_PathID")
                    lod_of.setdefault(rp, set()).add(i)

        for pid, n in nodes.items():
            lods = lod_of.get(n["rend"], set())
            # RETAIL: drawn on a fresh unpowered object at close range?
            in_no_lod = bool(lg) and not lods
            retail = (not n["hidden"]) and (not lg or 0 in lods or in_no_lod)
            # OURS: survives the extractor's node prune, then its LOD0 selection
            ours = (not n["pruned"]) and (not lg or 0 in lods)
            if selfcheck and "dead" in n["mesh"].lower():
                ours = False        # the old, wrong mesh-name filter

            if retail and not n["toggled"] and not ours:
                findings.append((prop, guid, "MISSING", n["path"], n["mesh"],
                                 "not in any LOD level" if in_no_lod else
                                 ("pruned by node name" if n["pruned"] else "not in LOD0"), power))
            elif n["toggled"] and ours:
                findings.append((prop, guid, "BAKED_ON", n["path"], n["mesh"],
                                 f"Toggle subtree is OFF until used"
                                 + (" + wired" if power == "Stay" else ""), power))

    def bucket(f):
        return "PEI" if f[1] in placed else ("extracted" if f[1] in shipped else "unused")

    if selfcheck:
        want = {"Street_Light_0", "Traffic_Light_0"}
        got = {f[0] for f in findings if f[2] == "MISSING"}
        missed = want - got
        print("SELFCHECK: simulating the old mesh-name 'dead' filter")
        print(f"  MISSING props detected: {sorted(got)}")
        if missed:
            print(f"  FAIL: did not re-detect {sorted(missed)} -- the audit is blind to its own bug")
            return 1
        print("  PASS: the audit reproduces the street-light bug it was written for")
        return 0

    rows = [f for f in findings if show_all or bucket(f) == "PEI"]
    print(f"props audited: {len(set(guid_of.values()))}   findings: {len(findings)}"
          f"   (on PEI: {sum(1 for f in findings if bucket(f)=='PEI')})\n")
    if not rows:
        print("  no disagreements in scope.")
        return 0
    for kind in ("MISSING", "BAKED_ON"):
        sel = [f for f in rows if f[2] == kind]
        if not sel:
            continue
        print(f"== {kind} ({len(sel)}) "
              + ("-- retail draws it, we dropped it" if kind == "MISSING"
                 else "-- retail hides it by default, we render it always") + "\n")
        for prop, guid, _k, p, mesh, why, power in sorted(sel):
            print(f"  {prop:<24} {bucket((prop,guid)):<10} {p}")
            print(f"      mesh={mesh:<16} {why}" + (f"   power={power}" if power else ""))
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
