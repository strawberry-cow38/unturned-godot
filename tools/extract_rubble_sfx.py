#!/usr/bin/env python3
"""Extract the retail break SOUND (AudioClip) for each destructible prop's Rubble_Effect.

Sibling of extract_rubble_effects.py (which pulls the break VFX). Same id-resolution
(rubble.txt guid -> prop .dat -> Rubble_Effect -> EffectAsset), but walks the effect
prefab for an AudioSource, resolves its AudioClip, and exports the audio.

Runs where the Unturned bundles live (the 4080 box). Explicit paths via argv so it
doesn't depend on a repo checkout being next to the bundles:
    python extract_rubble_sfx.py <BUNDLES_DIR> <rubble.txt> <OUT_DIR>
writes  <OUT_DIR>/rubble_snd/<id>.<ext>  +  <OUT_DIR>/rubble_snd.json  { "<id>": {"snd","clip_name"} | null }
"""
import UnityPy, json, os, re, glob, sys

BUND   = sys.argv[1]
RUBBLE = sys.argv[2]
OUTDIR = sys.argv[3]
MB     = os.path.join(BUND, "core.masterbundle")
OUTSND = os.path.join(OUTDIR, "rubble_snd")
OUTJSON= os.path.join(OUTDIR, "rubble_snd.json")

def kv(txt, k):
    m = re.search(r"(?im)^\s*" + re.escape(k) + r"\s+(\S+)", txt)
    return m.group(1) if m else None

# 1) effect id -> "category/name" (relative to Bundles/Effects) from the effect .dats
id2eff = {}
for datp in glob.glob(os.path.join(BUND, "Effects", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    if (kv(txt, "Type") or "").lower() != "effect": continue
    eid = kv(txt, "ID")
    if eid and eid.isdigit():
        id2eff[int(eid)] = os.path.relpath(os.path.dirname(datp), os.path.join(BUND, "Effects")).replace("\\", "/").lower()

# 2) which effect ids the PLACED rubble props actually use
guid2dat = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    g = kv(txt, "GUID")
    if g: guid2dat[g.lower()] = txt
used = {}
for line in open(RUBBLE):
    p = line.split()
    if p and p[0] in guid2dat:
        e = kv(guid2dat[p[0]], "Rubble_Effect")
        if e and e.isdigit(): used[p[0]] = int(e)
used_ids = sorted(set(used.values()))
print(f"[snd] {len(used_ids)} effect ids used by placed rubble props: {used_ids}")

# 3) walk each effect prefab for an AudioSource -> AudioClip
os.makedirs(OUTSND, exist_ok=True)
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = {p.lower(): o for p, o in env.container.items()}

def find_audiosource(prefab):
    """walk the prefab tree; return the first AudioSource's typetree (carries m_audioClip)."""
    stack = [prefab]
    while stack:
        go = stack.pop()
        if not go: continue
        tt = go.read_typetree()
        for comp in tt.get("m_Component", []):
            co = by_id.get(comp.get("component", comp).get("m_PathID"))
            if not co: continue
            if co.type.name == "AudioSource":
                return co.read_typetree()
            if co.type.name == "Transform":
                for ch in co.read_typetree().get("m_Children", []):
                    ct = by_id.get(ch.get("m_PathID"))
                    if ct: stack.append(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")))
    return None

catalog = {}
for eid in used_ids:
    effpath = id2eff.get(eid)
    base = f"assets/coremasterbundle/effects/{effpath}"
    prefab = cont.get(base + "/effect.prefab")
    if not prefab:
        print(f"[snd] {eid} ({effpath}): NO PREFAB at {base}/effect.prefab"); catalog[str(eid)] = None; continue
    au = find_audiosource(prefab)
    if not au:
        print(f"[snd] {eid} ({effpath}): no AudioSource (VFX-only effect)"); catalog[str(eid)] = None; continue
    ref = au.get("m_audioClip", {}) or {}
    cid = ref.get("m_PathID"); fid = ref.get("m_FileID", 0)
    if not cid:
        print(f"[snd] {eid} ({effpath}): AudioSource but empty clip ref"); catalog[str(eid)] = None; continue
    clip_obj = by_id.get(cid)
    if not clip_obj or clip_obj.type.name != "AudioClip":
        print(f"[snd] {eid} ({effpath}): clip is EXTERNAL (fid={fid} pid={cid}) not in masterbundle"); catalog[str(eid)] = {"snd": None, "note": f"external fid={fid} pid={cid}"}; continue
    clip = clip_obj.read()
    try:
        samples = clip.samples   # {name: wav_bytes}
    except Exception as ex:
        print(f"[snd] {eid} ({effpath}): AudioClip {getattr(clip,'m_Name','?')} decode FAILED: {ex}"); catalog[str(eid)] = {"snd": None, "clip_name": getattr(clip,'m_Name',None)}; continue
    if not samples:
        print(f"[snd] {eid} ({effpath}): AudioClip {clip.m_Name} no decodable samples"); catalog[str(eid)] = {"snd": None, "clip_name": clip.m_Name}; continue
    name, data = next(iter(samples.items()))
    ext = os.path.splitext(name)[1] or ".wav"
    fn = f"{eid}{ext}"
    open(os.path.join(OUTSND, fn), "wb").write(data)
    catalog[str(eid)] = {"snd": fn, "clip_name": clip.m_Name}
    print(f"[snd] {eid:4d} {str(effpath):28s} clip={clip.m_Name} -> {fn} ({len(data)} bytes)")

json.dump(catalog, open(OUTJSON, "w"), indent=1, sort_keys=True)
got = sum(1 for v in catalog.values() if v and v.get("snd"))
print(f"[snd] DONE: {got}/{len(used_ids)} sounds -> {OUTSND}/ + {OUTJSON}")
