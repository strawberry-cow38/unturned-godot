#!/usr/bin/env python3
"""probe_traps.py -- find the container paths of the trap items (landmine/spike/charge/detonator) in
core.masterbundle so I can adapt extract_battery.py to rip their real world meshes. Throwaway probe."""
import UnityPy
env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
terms = ["landmine", "charge", "spike", "mine", "trap", "claymore", "detonator", "barbed", "wire", "generator", "battery"]
for t in terms:
    hits = sorted({p for p in env.container if t in p.lower()})
    prefabs = [p for p in hits if p.lower().endswith("item.prefab")]
    print(f"== {t} == total={len(hits)} prefabs={len(prefabs)}")
    for p in (prefabs or hits)[:12]:
        print("   ", p)
