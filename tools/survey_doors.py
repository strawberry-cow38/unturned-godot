"""Survey every door-ish prefab across the Unturned bundles: barricade doors (metal/other variants)
+ object facility doors (Vault/Jail/Prison/etc.). Read-only enumeration."""
import UnityPy, os, sys, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
BUND = ug_paths.bundles()
print("BUND =", BUND)
print("top-level:", sorted(os.listdir(BUND))[:60])
KW = ('door', 'gate', 'hatch', 'garage')
# load core.masterbundle + anything with 'object' in the path (the facility-door bundle(s))
targets = []
cm = os.path.join(BUND, "core.masterbundle")
if os.path.isfile(cm): targets.append(cm)
for f in glob.glob(os.path.join(BUND, '**', '*'), recursive=True):
    if os.path.isfile(f) and ('object' in os.path.basename(f).lower()) and f not in targets:
        targets.append(f)
print(f"scanning {len(targets)} bundle(s)")
for t in targets:
    try:
        env = UnityPy.load(t)
    except Exception as e:
        print(f"  skip {os.path.basename(t)}: {e}"); continue
    hits = sorted(set(p for p in env.container.keys()
                      if any(k in p.lower() for k in KW) and p.lower().endswith('.prefab')))
    if hits:
        print(f"=== {os.path.relpath(t, BUND)} ({len(hits)}) ===")
        for p in hits: print("  ", p)
