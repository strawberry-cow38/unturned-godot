import urllib.request, re, json, sys
# Walk an Unturned ragdoll prefab in resources.assets (collection I:8) and pull the real physics rig:
# per-bone Rigidbody (mass/drag), CharacterJoint (swing/twist limits, axes), CapsuleCollider (center/radius/height/dir).
BASE = 'http://localhost:5556/Assets/Yaml?Path='
def enc(pid): return '%7B%22C%22%3A%7B%22B%22%3A%7B%22P%22%3A%5B%5D%7D%2C%22I%22%3A8%7D%2C%22D%22%3A' + str(pid) + '%7D'
cache = {}
def fetch(pid):
    if pid in cache: return cache[pid]
    try: t = urllib.request.urlopen(BASE + enc(pid), timeout=20).read().decode('utf-8', 'replace')
    except Exception as e: t = ''
    cache[pid] = t; return t
def cls(t):
    m = re.search(r'!u!(\d+)', t); return int(m.group(1)) if m else -1
def f(t, key):
    m = re.search(key + r':\s*(-?[\d.eE+-]+)', t); return float(m.group(1)) if m else None
def vec(t, key):
    m = re.search(key + r':\s*\{([^}]*)\}', t)
    if not m: return None
    d = dict(re.findall(r'([xyz]):\s*(-?[\d.eE+-]+)', m.group(1)))
    return [float(d.get('x',0)), float(d.get('y',0)), float(d.get('z',0))]
def limit(t, key):  # block style: "m_Swing1Limit:\n    limit: N" (or inline {limit: N})
    m = re.search(key + r':\s*\n\s*limit:\s*(-?[\d.eE+-]+)', t)
    if m: return float(m.group(1))
    m = re.search(key + r':\s*\{[^}]*limit:\s*(-?[\d.eE+-]+)', t)
    return float(m.group(1)) if m else None
def pathid(t, key):
    m = re.search(key + r':\s*\{[^}]*m_PathID:\s*(\d+)', t); return int(m.group(1)) if m else 0

def gameobject_name(t):
    m = re.search(r'm_Name:\s*(.+)', t); return m.group(1).strip() if m else '?'
def components(t):  # list of component pathIDs
    blk = re.search(r'm_Component:(.*?)m_Layer:', t, re.S)
    return [int(x) for x in re.findall(r'm_PathID:\s*(\d+)', blk.group(1))] if blk else []
def transform_children(t):
    blk = re.search(r'm_Children:(.*?)m_Father:', t, re.S)
    return [int(x) for x in re.findall(r'm_PathID:\s*(\d+)', blk.group(1))] if blk else []

bones = {}
RAWJOINT = ''
def walk(go_pid, depth=0):
    go = fetch(go_pid)
    if cls(go) != 1: return
    name = gameobject_name(go)
    comps = components(go)
    tf = None; rb = None; joint = None; box = None
    for c in comps:
        t = fetch(c); k = cls(t)
        if k == 4: tf = t
        elif k == 54: rb = t
        elif k == 144: joint = t
        elif k == 65: box = t
    entry = {'name': name, 'depth': depth}
    if rb is not None:
        entry['rb'] = {'mass': f(rb,'m_Mass'), 'drag': f(rb,'m_Drag'), 'adrag': f(rb,'m_AngularDrag')}
    if box is not None:
        entry['box'] = {'center': vec(box,'m_Center'), 'size': vec(box,'m_Size')}
    if joint is not None:
        entry['joint'] = {
            'axis': vec(joint,'m_Axis'), 'swingAxis': vec(joint,'m_SwingAxis'),
            'lowTwist': limit(joint,'m_LowTwistLimit'), 'highTwist': limit(joint,'m_HighTwistLimit'),
            'swing1': limit(joint,'m_Swing1Limit'), 'swing2': limit(joint,'m_Swing2Limit'),
            'enablePreprocessing': f(joint,'m_EnablePreprocessing'),
        }
        global RAWJOINT
        if not RAWJOINT: RAWJOINT = joint
    if rb or joint or box:
        bones[name] = entry
    if tf is not None:
        for ch in transform_children(tf):
            cht = fetch(ch)
            walk(pathid(cht, 'm_GameObject'), depth+1)

ROOT = int(sys.argv[1]) if len(sys.argv) > 1 else 2277
root_go = fetch(ROOT)
# find the root Transform -> walk it
for c in components(root_go):
    if cls(fetch(c)) == 4:
        for ch in transform_children(fetch(c)):
            walk(pathid(fetch(ch), 'm_GameObject'))
        break
print('BONES WITH PHYSICS:', len(bones))
for n, e in bones.items():
    print(json.dumps({n: e}))
print('=== RAW JOINT (class 144) sample ===')
print(RAWJOINT[:1400])
json.dump(bones, open(r'C:\claude-workspace\ragdoll_%d.json' % ROOT, 'w'), indent=0)
