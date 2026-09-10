#!/usr/bin/env python3
"""Run extract_consumable_equipable over every consumable in consumable_anims.tsv (the 170 items that already
have their own CE_n/CU_n clips), so each one gets the parts its Use animation was always trying to move.
Reports which items have BONE parts -- those are the ones whose animation actually had something to drive."""
import subprocess, sys, os, collections
TSV = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.tsv"
OUT = sys.argv[1] if len(sys.argv) > 1 else r"C:\claude-workspace\consumeparts"
HERE = os.path.dirname(os.path.abspath(__file__))
names = [l.split("\t")[0].strip() for l in open(TSV) if l.strip()]
os.makedirs(OUT, exist_ok=True)
ok = fail = 0
boned = []
parts_hist = collections.Counter()
for n in names:
    r = subprocess.run([sys.executable, os.path.join(HERE, "extract_consumable_equipable.py"), n, OUT],
                       capture_output=True, text=True)
    tail = (r.stdout or "").strip().splitlines()
    last = tail[-1] if tail else (r.stderr or "").strip()[:80]
    if r.returncode == 0 and ": " in last and "parts" in last:
        ok += 1
        cnt = int(last.split(": ")[1].split(" parts")[0])
        parts_hist[cnt] += 1
        pf = os.path.join(OUT, n.lower() + "_parts.tsv")
        if os.path.exists(pf) and any(l.lower().startswith("bone_") for l in open(pf)):
            boned.append(n)
    else:
        fail += 1
        print("FAIL %-22s %s" % (n, last))
print("\nok=%d fail=%d" % (ok, fail))
print("parts-per-item histogram:", dict(sorted(parts_hist.items())))
print("items WITH Bone_n parts (animated sub-pieces): %d" % len(boned))
print("  " + ", ".join(boned[:40]) + (" ..." if len(boned) > 40 else ""))
