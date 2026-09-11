#!/usr/bin/env python3
"""Extract the retail destruction SOUND for each RESOURCE (tree, bush, clay/metal node).

Sibling of extract_rubble_sfx.py. That one covers OBJECTS (Rubble_Effect); resources are a
separate asset type with their own field: ResourceAsset.explosion, parsed from the resource
.dat's `Explosion` (ResourceAsset.cs:308) and resolved by FindExplosionEffectAsset (:117).

The mapping is per-RESOURCE, not per-species: Birch_0 and Birch_1 are different effects, and
the retail effect names line up 1:1 with the resource names, so nothing has to be guessed from
a filename. Several resources share one effect (every coloured bush is Foliage_0), so the wav
is written once per EFFECT ID and the resource map points at it.

    python extract_resource_sfx.py <BUNDLES_DIR> <OUT_DIR>
writes  <OUT_DIR>/resource_snd/<effectid>.wav
        <OUT_DIR>/resource_snd.json  { "resources": {name: effectid},
                                       "clips": {"<effectid>": {"snd","clip_name"} | null} }
"""
import UnityPy, json, os, re, glob, sys

BUND    = sys.argv[1]
OUTDIR  = sys.argv[2]
MB      = os.path.join(BUND, "core.masterbundle")
OUTSND  = os.path.join(OUTDIR, "resource_snd")
OUTJSON = os.path.join(OUTDIR, "resource_snd.json")

def kv(txt, k):
    m = re.search(r"(?im)^\s*" + re.escape(k) + r"\s+(\S+)", txt)
    return m.group(1) if m else None

# 1) effect id -> "category/name" relative to Bundles/Effects
id2eff = {}
for datp in glob.glob(os.path.join(BUND, "Effects", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    if (kv(txt, "Type") or "").lower() != "effect": continue
    eid = kv(txt, "ID")
    if eid and eid.isdigit():
        id2eff[int(eid)] = os.path.relpath(os.path.dirname(datp), os.path.join(BUND, "Effects")).replace("\\", "/").lower()

# 2) every resource -> its Explosion effect id. A resource with no Explosion is silent in
#    retail (Bush_0 and Bush_1 are), which is a real answer, so it is recorded as 0 rather
#    than dropped -- otherwise a later reader cannot tell "silent" from "not ripped".
resources = {}
for datp in glob.glob(os.path.join(BUND, "Trees", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    if (kv(txt, "Type") or "").lower() != "resource": continue
    e = kv(txt, "Explosion")
    resources[os.path.splitext(os.path.basename(datp))[0]] = int(e) if (e and e.isdigit()) else 0
want = sorted({e for e in resources.values() if e})
print(f"[res] {len(resources)} resources -> {len(want)} distinct explosion effects: {want}")

# 3) walk each effect prefab for an AudioSource -> AudioClip
os.makedirs(OUTSND, exist_ok=True)
env  = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont  = {p.lower(): o for p, o in env.container.items()}

def find_audiosource(prefab):
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

clips = {}
for eid in want:
    effpath = id2eff.get(eid)
    if not effpath:
        print(f"[res] {eid}: no effect .dat with that ID"); clips[str(eid)] = None; continue
    prefab = cont.get(f"assets/coremasterbundle/effects/{effpath}/effect.prefab")
    if not prefab:
        print(f"[res] {eid} ({effpath}): NO PREFAB"); clips[str(eid)] = None; continue
    au = find_audiosource(prefab)
    if not au:
        print(f"[res] {eid} ({effpath}): no AudioSource (VFX-only effect)"); clips[str(eid)] = None; continue
    ref = au.get("m_audioClip", {}) or {}
    cid = ref.get("m_PathID")
    clip_obj = by_id.get(cid) if cid else None
    if not clip_obj or clip_obj.type.name != "AudioClip":
        print(f"[res] {eid} ({effpath}): clip ref empty or external"); clips[str(eid)] = None; continue
    clip = clip_obj.read()
    samples = {}
    try: samples = clip.samples
    except Exception as ex: print(f"[res] {eid} ({effpath}): decode FAILED: {ex}")
    if not samples:
        print(f"[res] {eid} ({effpath}): AudioClip {getattr(clip,'m_Name','?')} no decodable samples")
        clips[str(eid)] = {"snd": None, "clip_name": getattr(clip, "m_Name", None)}; continue
    name, data = next(iter(samples.items()))
    fn = f"{eid}{os.path.splitext(name)[1] or '.wav'}"
    open(os.path.join(OUTSND, fn), "wb").write(data)
    clips[str(eid)] = {"snd": fn, "clip_name": clip.m_Name}
    print(f"[res] {eid:4d} {str(effpath):24s} clip={clip.m_Name} -> {fn} ({len(data)} bytes)")

json.dump({"resources": resources, "clips": clips}, open(OUTJSON, "w"), indent=1, sort_keys=True)
got = sum(1 for v in clips.values() if v and v.get("snd"))
print(f"[res] DONE: {got}/{len(want)} effect sounds -> {OUTSND}/ + {OUTJSON}")
