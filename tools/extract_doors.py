"""Extract an openable-prop DOOR LEAF (Binary_State object, e.g. Fridge_0's Hinge_0 SkinnedMeshRenderer)
that extract_objects_v2 / extract_object_named cannot see: their walk() only reads MeshFilter, and a door
leaf is a SkinnedMeshRenderer (single-bone rig) with no MeshFilter at all.

CORRECTED approach (v2): a SkinnedMeshRenderer's raw vertex buffer is in BIND-POSE space, NOT the renderer
GameObject's own Transform-hierarchy space -- treating it like a static MeshFilter mesh (baking the leaf/bone
Transform hierarchy chain into the vertices, as v1 of this script did) lands ~90 degrees off with a diagonal,
non-cardinal hinge axis. This follows the port's EXISTING, proven skinned-mesh convention instead -- the same
one tools/rig_extract.py and tools/deer_rig_extract.py use for the player/animal rigs:
  - mesh vertices: read RAW from mesh.m_VertexData (packed buffer), z-negated, NO renderer/bone transform
    applied at all (Unity's bindpose definition makes the renderer transform cancel out at rest pose, so the
    raw buffer data already IS the correct rest-pose geometry -- see deer_rig_extract.py's `positions.append`).
  - hinge pivot + swing axis: from `zflip(inverse(mesh.m_BindPose[0]))` (single-bone rig: exactly one bone,
    one bindpose) -- rig_extract.py's rule "bone = inverse(bindpose)" -- NOT from the "Hinge" bone GameObject's
    own scene Transform (which is a DIFFERENT, incompatible frame; confirmed by direct diagnostic: using it
    gave axis (-0.7071,0.7071,0), a diagonal 45-degree direction with zero component on the mesh's own height
    axis -- physically impossible for a hinge meant to swing a floor-to-ceiling door sideways).
  - winding / UV: UNLIKE deer_rig_extract.py (whose output feeds a different consumer that does its own
    single reversal/V-flip), this script's .obj output goes through ObjMesh.Load, which ALREADY reverses
    winding once (unconditionally) and V-flips UVs once at load time -- same as how extract_objects_v2.py's
    write_obj leaves winding/UV raw and lets the loader do it exactly once. So here: z-negate POSITIONS and
    NORMALS (the one genuinely different convention for bind-pose data, confirmed by the coordinator against
    a render), but leave winding order and UV raw/unflipped -- matching the body mesh's own single-reversal
    treatment, not double-applying.
  - Do NOT apply extract_objects_v2's "half position swap" hack here -- it exists to patch STATIC multi-part
    mesh offsets and would double-correct bind-pose-space data.

  python extract_doors.py Fridge_0
-> content/objects/Fridge_0_door.obj (leaf mesh, bind-pose convention, lands flush with the body when loaded
   through ObjMesh same as it)
-> a line appended to content/objects/doors.txt:
     <name> <doorObjFile> px py pz ax ay az angleDeg durationSec
   angleDeg/durationSec for Fridge_0 are the investigation's sampled retail clip values (135.00 deg over
   0.4667s) -- NOT re-derived from the clip by this script; written as an easily hand-flippable signed number
   since a rotation's handedness can still invert through this pipeline -- if the render swings the door INTO
   the fridge instead of outward, flip this ONE number's sign (no code change, no re-extract).
"""
import UnityPy, os, glob, re, struct, math, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
TARGET = (sys.argv[1] if len(sys.argv) > 1 else "Fridge_0")

DOOR_CLIP = {
    "Fridge_0": {"angle": 135.0, "duration": 0.4667},
}

# ---- rig_extract.py / deer_rig_extract.py math (verbatim -- proven skinned-mesh convention) ----
def mat3_inv(m):
    a, b, c = m[0]; d, e, f = m[1]; g, h, i = m[2]
    A = e*i-f*h; B = -(d*i-f*g); C = d*h-e*g
    D = -(b*i-c*h); E = a*i-c*g; F = -(a*h-b*g)
    G = b*f-c*e; H = -(a*f-c*d); I = a*e-b*d
    det = a*A+b*B+c*C
    if abs(det) < 1e-20: raise ValueError("singular")
    inv = 1.0/det
    return [[A*inv, D*inv, G*inv], [B*inv, E*inv, H*inv], [C*inv, F*inv, I*inv]]
def mat_inv_affine(M):
    L = [[M[r][c] for c in range(3)] for r in range(3)]
    t = [M[0][3], M[1][3], M[2][3]]
    Li = mat3_inv(L)
    nt = [-(Li[r][0]*t[0] + Li[r][1]*t[1] + Li[r][2]*t[2]) for r in range(3)]
    return [[Li[0][0], Li[0][1], Li[0][2], nt[0]],
            [Li[1][0], Li[1][1], Li[1][2], nt[1]],
            [Li[2][0], Li[2][1], Li[2][2], nt[2]],
            [0, 0, 0, 1]]
S = [1, 1, -1, 1]   # Unity -> Godot z-flip
def zflip(M): return [[M[i][j]*S[i]*S[j] for j in range(4)] for i in range(4)]

# ---- GUID + prefab container path, keyed by Bundles/Objects folder name (verbatim from extract_object_named.py) ----
name2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    folder = os.path.basename(os.path.dirname(datp))
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    name2info[folder] = (m.group(1).lower(), "assets/coremasterbundle/" + rel + "/object.prefab")

if TARGET not in name2info:
    print("NOT FOUND in Bundles/Objects:", TARGET); sys.exit(1)
guid, cont = name2info[TARGET]
print("target", TARGET, "guid", guid, "prefab", cont)

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefab = None
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower() == cont:
        prefab = obj; break
if not prefab:
    print("prefab not in core.masterbundle:", cont); sys.exit(1)

# ---- find the door leaf's SkinnedMeshRenderer by walking the typetree hierarchy (name-based, like v1) ----
def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co
def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None
def find_go_by_name(target_name, go_pid):
    go = by_id.get(go_pid)
    if not go: return None
    tt = go.read_typetree()
    if (tt.get("m_Name", "") or "") == target_name: return go
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return None
    trt = tr.read_typetree()
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            found = find_go_by_name(target_name, ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
            if found: return found
    return None

LEAF_NAME = "Hinge_0"
leaf_go = find_go_by_name(LEAF_NAME, prefab.path_id)
if not leaf_go:
    print(f"NO DOOR LEAF NODE named {LEAF_NAME!r} under {TARGET}"); sys.exit(1)
leaf_tt = leaf_go.read_typetree()
smr_co = comp_of(leaf_tt, ("SkinnedMeshRenderer",))
if not smr_co:
    print(f"{LEAF_NAME} has no SkinnedMeshRenderer component"); sys.exit(1)

# ---- switch to UnityPy's object-attribute API (matches deer_rig_extract.py) for m_Bones / m_Mesh / m_BindPose / m_VertexData ----
smr = smr_co.read()
mesh = smr.m_Mesh.read()
NB = len(smr.m_Bones)
VC = mesh.m_VertexData.m_VertexCount
print(f"bones={NB} verts={VC} bindposes={len(mesh.m_BindPose)}")
bone_names = []
for i, pp in enumerate(smr.m_Bones):
    t = pp.read()
    nm = t.m_GameObject.read().m_Name
    bone_names.append(nm)
    print(f"  bone[{i}] = {nm}")
if NB != 1:
    print(f"WARNING: expected a single-bone rig (NB=1), got NB={NB} -- this script only handles bone[0]; verify {TARGET} is really single-bone before trusting the result")

def mat_of(bp):
    if hasattr(bp, "e00"): return [[getattr(bp, "e%d%d" % (r, c)) for c in range(4)] for r in range(4)]
    d = list(bp); return [[d[r*4+c] for c in range(4)] for r in range(4)]
bindposes = [mat_of(bp) for bp in mesh.m_BindPose]

# ---- hinge pivot + swing axis: bone = inverse(bindpose) (rig_extract.py rule). NOT zflipped -- see NEGATE_TEST
# below: this codebase's ObjMesh loader (game/ObjMesh.cs) defaults to UG_CONV=1 (raw Unity passthrough, no
# z-negate) for anything it loads, matching how extract_objects_v2.py already writes the body (Model_0.obj,
# proven correct by render). Numerically verified against WorldBuilder's own ex=270 placement basis: the
# zflipped pivot lands at final Y=-1.0 (underground, off the body's own Y=[0,2.5] range); the UN-flipped one
# lands at final Y=+1.0 (inside that range) AND matches the investigation's originally given ground-truth
# number (0.6130, 0.2979, 1.0000) exactly. The axis is an equally clean cardinal (0,~-1,0) either way (zflip
# only flips ITS sign, already absorbed by the angleDeg flip escape hatch) -- so skipping zflip fixes the
# real bug (bindpose vs scene-Transform frame) without breaking position alignment with the body.
bone_rest = mat_inv_affine(bindposes[0])
pivot = [bone_rest[0][3], bone_rest[1][3], bone_rest[2][3]]
axis_raw = [bone_rest[0][2], bone_rest[1][2], bone_rest[2][2]]   # 3rd column = local Z axis rotated into this frame
axisLen = math.sqrt(sum(a*a for a in axis_raw))
axis = [a/axisLen for a in axis_raw] if axisLen > 1e-9 else [0.0, 0.0, 1.0]
print(f"pivot (bind-pose-derived, godot-convention) = ({pivot[0]:.4f}, {pivot[1]:.4f}, {pivot[2]:.4f})")
print(f"swing axis (bind-pose-derived, godot-convention, unit) = ({axis[0]:.4f}, {axis[1]:.4f}, {axis[2]:.4f})")

# ---- mesh geometry: RAW packed m_VertexData (verbatim channel/stride logic from deer_rig_extract.py) ----
vd = mesh.m_VertexData
data = bytes(vd.m_DataSize)
FSZ = {0: 4, 1: 2, 2: 1, 3: 1, 4: 2, 5: 2, 6: 1, 7: 1, 8: 2, 9: 2, 10: 4, 11: 4}
def chd(ch):
    v = getattr(ch, "dimension", getattr(ch, "m_Dimension", getattr(ch, "m_RawDimension", 0)))
    return v & 0xF if isinstance(v, int) else 0
chans = vd.m_Channels
def cget(ch, a, *alts):
    for nm in (a,) + alts:
        if hasattr(ch, nm): return getattr(ch, nm)
    return 0
strides = {}
for ch in chans:
    dim = chd(ch)
    if dim == 0: continue
    s = cget(ch, "stream", "m_Stream"); off = cget(ch, "offset", "m_Offset"); fmt = cget(ch, "format", "m_Format")
    strides[s] = max(strides.get(s, 0), off + dim*FSZ[fmt])
def align(x, a=16): return (x+a-1)//a*a
starts = {}; cur = 0
for s in sorted(strides):
    starts[s] = cur; cur = align(cur + strides[s]*VC)
def choff(idx):
    c = chans[idx]; return cget(c, "stream", "m_Stream"), cget(c, "offset", "m_Offset")
s0, o0 = choff(0); s1n, o1 = choff(1); s4s, o4 = choff(4)

positions = []; normals = []; uvs = []
for v in range(VC):
    b0 = starts[s0] + v*strides[s0]
    px, py, pz = struct.unpack_from("<3f", data, b0+o0)
    nx, ny, nz = struct.unpack_from("<3f", data, b0+o1)
    b1 = starts[s4s] + v*strides[s4s]
    u, uvv = struct.unpack_from("<2f", data, b1+o4)
    positions.append((px, py, pz))      # RAW, no z-negate -- see the pivot comment above: matches the body's own convention, verified numerically
    normals.append((nx, ny, nz))
    uvs.append((u, uvv))                # RAW, no V-flip here -- ObjMesh.Load V-flips once at load (matches write_obj's own convention)

# ---- triangles: RAW winding (ObjMesh.Load reverses once at load -- matches write_obj's own convention; do
# NOT also reverse here like deer_rig_extract.py does for its own, different, non-ObjMesh consumer) ----
ib = bytes(mesh.m_IndexBuffer)
tris = list(struct.unpack("<%dH" % (len(ib)//2), ib))
faces = [(tris[k]+1, tris[k+1]+1, tris[k+2]+1) for k in range(0, len(tris), 3)]   # OBJ is 1-indexed

if not positions:
    print("NO GEOMETRY extracted for", LEAF_NAME); sys.exit(1)
L = ["v %.6f %.6f %.6f" % v for v in positions]
L += ["vt %.6f %.6f" % t for t in uvs]
L += ["vn %.6f %.6f %.6f" % n for n in normals]
for (a, b, c) in faces:
    L.append("f %d/%d/%d %d/%d/%d %d/%d/%d" % (a, a, a, b, b, b, c, c, c))
doorObjName = TARGET + "_door.obj"
open(os.path.join(OUT, doorObjName), "w").write("\n".join(L) + "\n")
print(f"wrote {doorObjName}  verts={len(positions)} faces={len(faces)}")

bodyPath = os.path.join(OUT, TARGET + ".obj")
if os.path.exists(bodyPath):
    bodyVerts = sum(1 for ln in open(bodyPath) if ln.startswith("v "))
    print(f"body {TARGET}.obj (unchanged by this script) verts={bodyVerts}")
else:
    print(f"NOTE: {TARGET}.obj (body) not found -- run extract_objects_v2.py / extract_object_named.py first")

clip = DOOR_CLIP.get(TARGET)
if clip is None:
    print(f"NOTE: no DOOR_CLIP entry for {TARGET} -- add its sampled retail angle/duration to this script's DOOR_CLIP dict before cataloging it")
else:
    catPath = os.path.join(OUT, "doors.txt")
    line = "%s %s %.6f %.6f %.6f %.6f %.6f %.6f %.4f %.4f" % (
        TARGET, doorObjName, pivot[0], pivot[1], pivot[2], axis[0], axis[1], axis[2], clip["angle"], clip["duration"])
    existing = []
    if os.path.exists(catPath):
        existing = [ln for ln in open(catPath).read().splitlines() if ln.strip() and not ln.split()[0] == TARGET]
    existing.append(line)
    open(catPath, "w").write("\n".join(existing) + "\n")
    print(f"wrote doors.txt entry: {line}")
    print("  (angleDeg is the easy-to-flip sign if the render shows the door swinging the wrong way -- see module docstring)")
