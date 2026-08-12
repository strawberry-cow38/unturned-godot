"""Find + export the Firemode effect's AudioClip (the tactical-light/flashlight TOGGLE sound,
GUID bc41e0feaebe4e788a3612811b8722d3, shared with the gun fire-selector click)."""
import UnityPy, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception: pass
BUND = ug_paths.bundles()
OUTDIR = r"C:\claude-workspace\firemode_out"
os.makedirs(OUTDIR, exist_ok=True)
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}

def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co

def export_clip(clip, tag=""):
    try:
        cn = clip.read_typetree().get("m_Name", "?")
    except Exception: cn = "?"
    print(f"    AudioClip '{cn}' (pid {clip.path_id}) {tag}")
    try:
        for fn, raw in clip.read().samples.items():
            outp = os.path.join(OUTDIR, fn)
            open(outp, "wb").write(raw)
            print(f"      -> exported {fn} ({len(raw)} bytes)")
    except Exception as e:
        print("      export err:", e)

print("=== container paths matching 'firemode' ===")
roots = []
for path, obj in env.container.items():
    if "firemode" in path.lower():
        print(f"  {path} -> {obj.type.name}")
        if obj.type.name == "GameObject": roots.append(obj)
        if obj.type.name == "AudioClip": export_clip(obj, "(direct)")

seen = set()
def walk(pid, depth=0):
    if pid in seen or depth > 14: return
    seen.add(pid)
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree(); nm = tt.get("m_Name", ""); tr = None
    for co in comps(tt):
        if co.type.name == "AudioSource":
            ast = co.read_typetree()
            clip = by_id.get((ast.get("m_audioClip") or {}).get("m_PathID"))
            if clip and clip.type.name == "AudioClip":
                print(f"  node '{nm}' AudioSource:")
                export_clip(clip)
        if co.type.name == "Transform": tr = co
    if tr:
        for ch in tr.read_typetree().get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), depth + 1)

print("=== walking firemode prefab(s) for AudioSource clips ===")
for r in roots:
    walk(r.path_id)
if not roots:
    print("no GameObject root found under 'firemode' container -- listing nearby AudioClips by name")
    for o in env.objects:
        if o.type.name == "AudioClip":
            try: nm = o.read_typetree().get("m_Name", "")
            except Exception: nm = "?"
            if any(k in nm.lower() for k in ("fire", "select", "click", "toggle", "switch", "mode")):
                export_clip(o, "(name-match)")
