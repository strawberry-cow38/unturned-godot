#!/usr/bin/env python3
"""Which ripped audio clips can any code path actually reach, and which are dead on disk.

Written after reporting content/audio/explosions as 60 unused prefixes when 44 of its 66 clips were already
playing -- through content/effects/rubble_snd, a SECOND rip of the same retail audio with its own loader.
A grep for callers of one folder cannot see the other, so this checks both axes:

  REACHABLE  a clip is reachable if its full name appears in a res:// literal, or if any `_`-delimited prefix
             of its name is used as a Bank()/Pick() prefix. Prefixes are matched loosely on purpose (helpers
             build them, e.g. FootSurface(sf) + "_walk"), which errs towards calling things reachable -- so a
             clip this reports as DEAD really has no caller.
  TWINNED    a clip byte-identical to one in another folder is heard whenever that twin is, however unused
             its own copy looks.

    python tools/audio_coverage.py [repo_root]
"""
import hashlib, os, re, sys, collections

ROOT  = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONT  = os.path.join(ROOT, "game", "content")
EXTS  = (".wav", ".ogg")

src = []
for base in ("game", "core"):
    for dp, _, fns in os.walk(os.path.join(ROOT, base)):
        for fn in fns:
            if fn.endswith(".cs"): src.append(open(os.path.join(dp, fn), encoding="utf-8", errors="ignore").read())
SRC = "\n".join(src)

# every quoted string in the source, and the subset used as a Bank/Pick prefix
quoted   = set(re.findall(r'"([A-Za-z0-9_./-]{2,})"', SRC))
prefixes = set(re.findall(r'(?:Bank|Pick)\(\s*"[a-z]+"\s*,\s*"([A-Za-z0-9_]+)"', SRC)) | quoted

# ...AND the clip names that live in DATA rather than in code. Half of what a source-only pass calls dead is
# wired through a table: consumable_sounds.tsv names a use clip per item id, effects/rubble_snd.json names one
# per effect id, and the prop-door catalog names a clip stem. A tool that cannot read those reports phantom
# gaps, which is worse than reporting none -- you go and "fix" something that already works.
DATA_EXT = (".tsv", ".json", ".txt", ".dat", ".csv")
data_words = set()
for dp, _, fns in os.walk(CONT):
    for fn in fns:
        if not fn.lower().endswith(DATA_EXT): continue
        try: txt = open(os.path.join(dp, fn), encoding="utf-8", errors="ignore").read()
        except OSError: continue
        data_words.update(re.findall(r"[A-Za-z0-9_.-]{3,}", txt))

clips = []
for dp, _, fns in os.walk(CONT):
    for fn in fns:
        if fn.lower().endswith(EXTS):
            p = os.path.join(dp, fn)
            clips.append((os.path.relpath(p, CONT).replace("\\", "/"), p))

by_hash = collections.defaultdict(list)
for rel, p in clips:
    by_hash[hashlib.md5(open(p, "rb").read()).hexdigest()].append(rel)

def reachable(rel):
    stem = os.path.splitext(os.path.basename(rel))[0]
    if rel in SRC or stem in quoted or os.path.basename(rel) in SRC: return "named"
    if stem in data_words or os.path.basename(rel) in data_words: return "data"
    parts = stem.split("_")
    for i in range(len(parts), 0, -1):
        if "_".join(parts[:i]) in prefixes: return "prefix"
    return None

state = {}
for rel, _ in clips:
    state[rel] = reachable(rel)

dead = collections.defaultdict(list)
for rel, _ in clips:
    if state[rel]: continue
    h = hashlib.md5(open(os.path.join(CONT, rel), "rb").read()).hexdigest()
    twin = next((t for t in by_hash[h] if t != rel and state.get(t)), None)
    if twin: continue                      # heard through its twin's folder
    dead[os.path.dirname(rel)].append(os.path.basename(rel))

tot = len(clips)
live = sum(1 for r in state.values() if r)
byhow = collections.Counter(v for v in state.values() if v)
print(f"{tot} clips, {live} reachable ({dict(byhow)}), {tot - live} not")
dupes = sum(len(v) - 1 for v in by_hash.values() if len(v) > 1)
print(f"{dupes} clips are byte-identical copies of another clip in the tree\n")
print("UNREACHABLE, and with no reachable twin -- grouped by folder:")
for folder in sorted(dead):
    names = sorted(dead[folder])
    pfx = collections.Counter(n.split("_")[0] for n in names)
    print(f"\n  {folder}/  ({len(names)} clips, {len(pfx)} prefixes)")
    for p, c in sorted(pfx.items()):
        print(f"      {p:34s} x{c}")
