"""Extract openable-prop DOOR LEAVES (Binary_State objects: fridges, wardrobes, cabinets...) that
extract_objects_v2 / extract_object_named cannot see: their walk() only reads MeshFilter, and a door leaf is
a SkinnedMeshRenderer (single-bone rig) with no MeshFilter at all.

GENERALIZED (v3) for MULTI-LEAF props (e.g. Wardrobe_0's two doors): discovers every SkinnedMeshRenderer under
the prefab generically (no hardcoded "Hinge_0" name), and for EACH one runs the full per-leaf pipeline
independently. Single-leaf props (Fridge_0) keep the exact legacy file naming (<name>_door.obj, _open.txt,
_close.txt) for byte-identical backward compatibility; multi-leaf props get leaf-qualified names
(<name>_<leafNodeName>_door.obj etc.) so each leaf's files are unambiguous. doors.txt keys ALL of a prop's
leaves under the SAME first-field prop name (one line per leaf) -- WorldBuilder.LoadDoorCatalog groups them
into a List<DoorCatalogEntry> per prop, so a single-leaf line is a list of 1 and nothing about the file format
changes for the fridge.

Per-leaf pipeline (proven on Fridge_0, unchanged in substance):
  - mesh vertices: read RAW from mesh.m_VertexData (packed buffer), NO z-negate, NO renderer/bone transform
    applied at all (Unity's bindpose definition makes the renderer transform cancel out at rest pose, so the
    raw buffer data already IS the correct rest-pose geometry -- see deer_rig_extract.py's `positions.append`
    for the source convention this follows). Winding/UV left RAW (game/ObjMesh.cs's loader reverses/V-flips
    exactly once at load time, same as extract_objects_v2.py's write_obj; do not also do it here).
  - hinge pivot: `inverse(mesh.m_BindPose[0])`'s translation (rig_extract.py's rule "bone = inverse(bindpose)"),
    NOT zflipped, NOT further corrected -- matches "prop space" (the frame the body mesh / WorldBuilder's
    placement basis use) directly; verified against Fridge_0's investigation ground truth to the given
    precision, and against WorldBuilder's real placement basis (lands within the body's own bounds).
  - swing axis: derived from the SAME per-clip rotation delta already computed for angleDeg (NOT from the
    bindpose's rest-pose rotation column -- see below for why that was wrong). Take the shared refAxis
    (largest-magnitude delta across this leaf's own clips, expressed in the bone's own bind-rest-local frame)
    and rotate it into prop space via qrot(Q_rest, refAxis), Q_rest = the bone's own m_LocalRotation. Works
    because Skeleton's own local rotation cancels Root's exactly (the same mechanism that lands Model_0 at
    identity), so "Skeleton-local" -- the frame Q_rest/refAxis live in -- IS prop space directly, no further
    correction needed. Verified to reproduce the identical direction the old bindpose-column formula gave for
    Fridge_0's Hinge and Wardrobe_0's Left_Hinge, but Wardrobe_0's Right_Hinge proved the old formula wrong:
    its bindpose rest-pose column-2 is not actually its hinge axis (authored differently from Left's -- NOT a
    mirroring issue: the leaf renderer's own accumulated scene rotation, what the old formula corrected by, is
    a pure rotation about a FIXED axis for every leaf checked on this prop, confirmed det=+1, so no correction
    built purely from that matrix could ever move a raw axis sitting exactly on its own fixed axis). Deriving
    straight from the clip's own rotation sidesteps the bad assumption entirely and needs no per-prop or
    per-leaf special-casing for mirrored geometry.
  - angleDeg / durationSec: DERIVED from the retail clip data itself (the settled/final angle magnitude and
    clip length of the "open"-role curve), NOT hardcoded per-prop -- for Fridge_0 this reproduces 135.00/0.4667
    exactly. The SIGN is whatever the clip data naturally gives; a rotation's handedness can still invert
    through this pipeline (confirmed on Fridge_0, which needed angleDeg flipped to -135 after render
    verification -- a gimbal-lock artifact of its Hinge bone resting at pitch=90), so treat the sign as
    unverified until rendered, and flip that ONE field in doors.txt if a leaf swings the wrong way (no
    re-extract needed).
  - defaultOpen: retail InteractableObjectBinaryState boots isUsed=false and (applyInstantly) jumps to the END
    of the clip literally NAMED "Close" -- true iff THAT named clip is the one classified as the opening
    motion for this leaf (per leaf: a prop with clip names inverted vs geometry, like Fridge_0, reads 1;
    normal-named props read 0).
  - clip ROLE classification is by DATA, not by Unity's clip name (see Fridge_0's inverted-name finding):
    whichever clip's endpoint is farther from the bone's own rest rotation is the "open" role, regardless of
    what Unity calls it.
  - Do NOT apply extract_objects_v2's "half position swap" hack here -- it exists to patch STATIC multi-part
    mesh offsets and would double-correct bind-pose-space data.

  python extract_doors.py Fridge_0        -> 1 leaf  (legacy naming, unchanged)
  python extract_doors.py Wardrobe_0      -> 2 leaves (Left_Hinge_0 / Right_Hinge_0, leaf-qualified naming)

doors.txt line format (11 or 12 space-separated fields; multiple lines may share field 0):
  <propName> <doorObjFile> px py pz ax ay az angleDeg durationSec defaultOpen(0/1) [soundClipStem]
  The 12th field (soundClipStem, e.g. "DoorHandle"/"HeavyMetalDoor") is OMITTED entirely for a prop with no
  AudioSource -- WorldBuilder.LoadDoorCatalog treats a missing 12th field as backward-compatible/silent, not
  an error.
"""
import UnityPy, os, glob, re, struct, math, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
TARGET = (sys.argv[1] if len(sys.argv) > 1 else "Fridge_0")

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
def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = [[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
         [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
         [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]]
    M = [[R[r][0]*s["x"], R[r][1]*s["y"], R[r][2]*s["z"], [pos["x"], pos["y"], pos["z"]][r]] for r in range(3)] + [[0, 0, 0, 1]]
    return M
def mat4_mul(A, B):
    return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qinv(q):
    x, y, z, w = q; return (-x, -y, -z, w)
def qrot(q, v):
    """Rotate 3-vector v by unit quaternion q=(x,y,z,w)."""
    qv = (q[0], q[1], q[2])
    uv = (qv[1]*v[2]-qv[2]*v[1], qv[2]*v[0]-qv[0]*v[2], qv[0]*v[1]-qv[1]*v[0])
    uuv = (qv[1]*uv[2]-qv[2]*uv[1], qv[2]*uv[0]-qv[0]*uv[2], qv[0]*uv[1]-qv[1]*uv[0])
    w = q[3]
    return tuple(v[i] + 2.0*(w*uv[i] + uuv[i]) for i in range(3))

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
def cl_(go): return getattr(go, "m_Components", None) or getattr(go, "m_Component", [])
def rd_(c):
    cp = c.component if hasattr(c, "component") else c
    return cp.read()
def tn_(co):
    try: return co.object_reader.type.name
    except Exception: return type(co).__name__
def qX(v): return v.X if hasattr(v, "X") else v.x
def qY(v): return v.Y if hasattr(v, "Y") else v.y
def qZ(v): return v.Z if hasattr(v, "Z") else v.z
def qW(v): return v.W if hasattr(v, "W") else v.w

# ---- door open/close SOUND (retail InteractableObjectBinaryState.updateAudioSourceComponent: plays the
# prop's OWN AudioSource clip on every TOGGLE -- same clip for open + close, NOT on load/snap; see
# game/ObjectDoor.cs's Toggle()). One clip per PROP (not per leaf) -- confirmed via
# tools/check_door_sounds.py against every prop currently in doors.txt: DoorHandle (furniture doors) /
# HeavyMetalDoor (Container_*), both m_Volume=0.125 linear. Reuses the SAME comps() walk already defined
# above (verbatim logic from the working reader in _ilspy_scratch/dump_audio.py, just against this file's
# own component-walk helper instead of re-deriving it) ----
def find_audio_clip_name(go):
    """This prop's AudioSource -> m_audioClip -> m_Name, or None if the prefab has no AudioSource (a
    silent door -- doors.txt simply gets no 12th field for it, which WorldBuilder.LoadDoorCatalog already
    treats as backward-compatible/absent)."""
    for co in comps(go.read_typetree()):
        if co.type.name == "AudioSource":
            ast = co.read_typetree()
            clippid = ast.get("m_audioClip", {}).get("m_PathID")
            clipo = by_id.get(clippid)
            if clipo: return clipo.read().m_Name
    return None

soundName = find_audio_clip_name(prefab)
print(f"door sound (AudioSource m_audioClip on {TARGET}'s prefab root): {soundName!r}")

# ---- ONE comprehensive walk: every node's (name -> go) and (name -> accumulated root-relative scenegraph
# matrix), PLUS every SkinnedMeshRenderer-bearing node (a door leaf), discovered generically -- no hardcoded
# "Hinge_0" / "Hinge" names, so this works for Fridge_0 (one leaf) and Wardrobe_0 (Left_Hinge_0/Right_Hinge_0)
# alike, and for whatever a future door prop's leaves happen to be named.
nodes_by_name = {}
scenegraph_M = {}
skinned_leaf_names = []
def walk_all(go_pid, parentM):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree()
    name = tt.get("m_Name", "") or ""
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = mat4_mul(parentM, trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"]))
    if name not in nodes_by_name:
        nodes_by_name[name] = go
        scenegraph_M[name] = M
        if comp_of(tt, ("SkinnedMeshRenderer",)):
            skinned_leaf_names.append(name)
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk_all(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)

rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
root_local_inv4 = mat_inv_affine(root_local)   # de-roots the walk exactly like extract_objects_v2.py's parentM=inv(root_local) start
walk_all(prefab.path_id, root_local_inv4)
print(f"skinned door leaves found under {TARGET}: {skinned_leaf_names}")
if not skinned_leaf_names:
    print(f"NO SkinnedMeshRenderer leaves found under {TARGET} -- not a Binary_State door prop, or structured differently"); sys.exit(1)

# ---- Root's Animation component -> "Open"/"Close" legacy clips (shared by every leaf of this prop) ----
root_go = nodes_by_name.get("Root")
anim = None
if root_go is not None:
    root_obj = root_go.read()
    for c in cl_(root_obj):
        if tn_(rd_(c)) == "Animation":
            anim = rd_(c); break
clips_by_name = {}
if anim is not None:
    all_clip_names = []
    for pp in (getattr(anim, "m_Animations", []) or []):
        try: clip_ = pp.read()
        except Exception: continue
        all_clip_names.append(clip_.m_Name)
        # ONLY the door clips -- tinyclaw found Dryer_0/Washer_0/Cooler_0's category of prop can carry an
        # EXTRA clip (idle/appliance-running/drum-spin) beyond the door's Open/Close pair. Not literally what
        # broke Dryer_0/Washer_0 here (see the sign-canonicalization fix below for the actual mechanism --
        # every one of these three only ever had exactly Open+Close on Root, confirmed), but a real prop
        # could plausibly have a genuine extra clip, and there is no reason to ever consider one: door swing
        # data only ever lives on clips literally named "Open"/"Close".
        if clip_.m_Name in ("Open", "Close"):
            clips_by_name[clip_.m_Name] = clip_
    extra = [n for n in all_clip_names if n not in ("Open", "Close")]
    print(f"clips on Root: {all_clip_names}" + (f"  (IGNORING non-door clip(s): {extra})" if extra else ""))
else:
    print("NOTE: no Root/Animation component found -- every leaf falls back to procedural easing (no curves, no derived angle/duration)")

def hinge_rot_keyframes(clip_, bone_name):
    for rcu in clip_.m_RotationCurves:
        if rcu.path.split("/")[-1] == bone_name:
            return [(k.time, (qX(k.value), qY(k.value), qZ(k.value), qW(k.value))) for k in rcu.curve.m_Curve]
    return None

def mat_of(bp):
    if hasattr(bp, "e00"): return [[getattr(bp, "e%d%d" % (r, c)) for c in range(4)] for r in range(4)]
    d = list(bp); return [[d[r*4+c] for c in range(4)] for r in range(4)]

curveDir = os.path.join(OUT, "door_curves")

def write_curve(path, samples):
    """Writes the normalized (t_norm, frac) curve; returns (farAng, length) -- the DERIVED angle magnitude
    and clip duration -- or None if the samples are degenerate (caller then has no angle/duration to catalog)."""
    farAng = max((samples[0][1], samples[-1][1]), key=abs)
    length = samples[-1][0]
    if abs(farAng) < 1e-6 or length < 1e-6:
        print(f"    SKIP {path}: degenerate (farAng={farAng} length={length})"); return None
    lines = ["%.6f %.6f" % (t/length, ang/farAng) for t, ang in samples]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "w").write("\n".join(lines) + "\n")
    print(f"    wrote {path}: {len(lines)} samples, farAng={farAng:.3f} length={length:.4f}")
    for t, ang in samples:
        print(f"      t={t:.4f} t_norm={t/length:.4f}  angle={ang:8.3f}  frac={ang/farAng:7.4f}")
    return farAng, length

def process_leaf(leaf_name):
    """Full per-leaf extraction: mesh + pivot + prop-space axis + open/close curves + defaultOpen + derived
    angle/duration. Returns a result dict, or None if this leaf cannot be cataloged (reported, not fatal --
    lets OTHER leaves of a multi-leaf prop still succeed; mesh/mirror geometry is still written even if the
    clip data needed for the catalog line could not be derived)."""
    print(f"--- leaf {leaf_name!r} ---")
    leaf_go = nodes_by_name[leaf_name]
    smr_co = comp_of(leaf_go.read_typetree(), ("SkinnedMeshRenderer",))
    if not smr_co:
        print(f"  {leaf_name} has no SkinnedMeshRenderer component (unexpected)"); return None
    smr = smr_co.read()
    mesh = smr.m_Mesh.read()
    NB = len(smr.m_Bones)
    VC = mesh.m_VertexData.m_VertexCount
    print(f"  bones={NB} verts={VC} bindposes={len(mesh.m_BindPose)}")
    if NB < 1:
        print(f"  {leaf_name}: no bones -- cannot derive a pivot/axis"); return None

    # ---- mesh geometry + skin index: RAW packed m_VertexData (verbatim channel/stride logic from
    # deer_rig_extract.py). Reading the skin index is NEW vs the single-bone Fridge_0 case: a multi-leaf prop's
    # SkinnedMeshRenderer.m_Bones array can be SHARED/padded across leaves -- confirmed on Wardrobe_0: BOTH
    # Left_Hinge_0 and Right_Hinge_0 report bones=2, and naively using bone[0] resolves to 'Left_Hinge' for
    # BOTH leaves (blindly trusting it would bind the right door to the left hinge). The actual per-vertex
    # blend index (channel 13, alongside blend weight in channel 12) tells us which bone slot THIS leaf's
    # geometry is really rigidly bound to.
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
        positions.append((px, py, pz))      # RAW, no z-negate -- matches the body mesh's own convention
        normals.append((nx, ny, nz))
        uvs.append((u, uvv))                # RAW, no V-flip -- ObjMesh.Load V-flips once at load

    if not positions:
        print(f"  NO GEOMETRY extracted for {leaf_name}"); return None

    # which bone slot is THIS leaf's mesh actually bound to? NOT read from per-vertex skin weights: this mesh
    # uses Unity's newer "variable bone count weights" format (confirmed via direct diagnostic on Wardrobe_0 --
    # m_VariableBoneCountWeights present, and the classic fixed blendweight/blendindices vertex channels
    # (12/13) are either absent or a different, non-fixed-width layout), so the deer_rig_extract.py-style
    # channel-12/13 byte read that works for the player/animal rigs does not apply here. Instead: match by
    # NAME, using the confirmed convention every Binary_State door leaf follows (Fridge_0's "Hinge_0" leaf /
    # "Hinge" bone; Wardrobe_0's "Left_Hinge_0"/"Right_Hinge_0" leaves / "Left_Hinge"/"Right_Hinge" bones) --
    # the leaf's own name IS its bone's name plus a "_0" suffix. Robust and avoids the binary format entirely;
    # confirmed necessary on Wardrobe_0, where BOTH leaves share one 2-bone array and bone[0] is 'Left_Hinge'
    # for both -- blindly trusting index 0 would bind the right door to the left hinge.
    expected_bone_name = leaf_name[:-2] if leaf_name.endswith("_0") else leaf_name
    boneIdx = 0
    bone_name = None
    for i in range(NB):
        bi_name = smr.m_Bones[i].read().m_GameObject.read().m_Name
        if bi_name == expected_bone_name:
            boneIdx = i; bone_name = bi_name; break
    if bone_name is None:
        # Used to default to bone[0] and continue -- REMOVED: confirmed on Cooler_0 that this is exactly what
        # turns a non-door SkinnedMeshRenderer leaf into a bogus doors.txt entry. Cooler_0 has TWO leaves,
        # 'Glass_0' (a window panel, no bone named 'Glass' anywhere in its rig) and 'Hinge_0' (the real door);
        # Glass_0's own m_Bones array is ['Hinge'] (shared/padded, same array Hinge_0 uses), so the old
        # fallback silently bound the glass panel to the door's OWN hinge bone/bindpose, producing a second,
        # spurious "Glass_0 door" entry with IDENTICAL pivot/axis/angle to the real one. Every confirmed real
        # door leaf checked so far (Fridge_0, Wardrobe_0 x2, Dryer_0, Washer_0, Cooler_0's own Hinge_0) hits
        # an EXACT name match; a leaf that doesn't is evidence it is NOT a hinge-door leaf at all, not a
        # reason to guess. Skip cleanly instead of fabricating geometry from an unrelated bone.
        #
        # NOTE for future maintainers: Container_0's 'Left_Hinge_1'/'Right_Hinge_1' leaves (out of scope here,
        # NOT re-extracted by this change) currently rely on this exact fallback (their own m_Bones also lack
        # an exact-name match) -- flagged separately, not fixed here; re-extracting Container_0 in the future
        # would now skip those two leaves instead of guessing bone[0].
        all_bone_names = [smr.m_Bones[i].read().m_GameObject.read().m_Name for i in range(NB)]
        print(f"  SKIP {leaf_name!r}: no bone named {expected_bone_name!r} found among this leaf's {NB} bones ({all_bone_names}) -- not a hinge-door leaf, no catalog entry, no mesh written")
        return None
    print(f"  bone[{boneIdx}] = {bone_name!r} (matched by name from leaf {leaf_name!r})" + (f"  [{NB} bones on this leaf's array]" if NB != 1 else ""))

    bindposes = [mat_of(bp) for bp in mesh.m_BindPose]
    bone_rest = mat_inv_affine(bindposes[boneIdx])
    pivot = [bone_rest[0][3], bone_rest[1][3], bone_rest[2][3]]
    print(f"  pivot = ({pivot[0]:.4f}, {pivot[1]:.4f}, {pivot[2]:.4f})")
    # Swing axis used to be derived here from the bindpose's rest-pose rotation column (3rd column) corrected
    # by the leaf renderer's own scene rotation -- REMOVED, see the axis derivation inside the clip-sampling
    # block below (near refAxis) for why that broke on Wardrobe_0's Right_Hinge (a "mirrored double-door"
    # symptom that turned out NOT to be a mirror at all -- det(leaf_R)=+1 for every leaf checked) and what
    # replaced it: the axis is now derived from the same clip rotation data already needed for angleDeg, not
    # from bindpose rest-pose bookkeeping.
    axis = None

    ib = bytes(mesh.m_IndexBuffer)
    tris = list(struct.unpack("<%dH" % (len(ib)//2), ib))
    faces = [(tris[k]+1, tris[k+1]+1, tris[k+2]+1) for k in range(0, len(tris), 3)]   # RAW winding, OBJ 1-indexed

    # naming: exact legacy convention when this prop has exactly one leaf (Fridge_0 byte-identical);
    # leaf-qualified for multi-leaf props so Left/Right never collide.
    base = TARGET if len(skinned_leaf_names) == 1 else f"{TARGET}_{leaf_name}"
    doorObjName = base + "_door.obj"

    Lw = ["v %.6f %.6f %.6f" % v for v in positions]
    Lw += ["vt %.6f %.6f" % t for t in uvs]
    Lw += ["vn %.6f %.6f %.6f" % n for n in normals]
    for (a, b, c) in faces:
        Lw.append("f %d/%d/%d %d/%d/%d %d/%d/%d" % (a, a, a, b, b, b, c, c, c))
    open(os.path.join(OUT, doorObjName), "w").write("\n".join(Lw) + "\n")
    print(f"  wrote {doorObjName}  verts={len(positions)} faces={len(faces)}")

    # retail clip sampling for THIS leaf's own bone, deriving angleDeg/durationSec + defaultOpen from the data
    # (not hardcoded) -- see module docstring for the full rationale (role-by-data, sign caveat, etc.)
    defaultOpen = False
    angleDeg = None
    durationSec = None
    if bone_name not in nodes_by_name:
        print(f"  NOTE: bone {bone_name!r} not found in scene graph -- cannot read its rest rotation; no curve/angle derived")
    elif not clips_by_name:
        print("  NOTE: no clips available -- no curve/angle derived")
    else:
        bone_tr = comp_of(nodes_by_name[bone_name].read_typetree(), ("Transform", "RectTransform")).read_typetree()
        Qr = bone_tr["m_LocalRotation"]
        Q_rest = (Qr["x"], Qr["y"], Qr["z"], Qr["w"])

        deltas_by_clip = {}
        for nm, clip_ in clips_by_name.items():
            kf = hinge_rot_keyframes(clip_, bone_name)
            if not kf: continue
            ds = []
            for t, Qk in kf:
                d = qmul(qinv(Q_rest), Qk)
                # Canonicalize the quaternion double-cover: q and -q represent the IDENTICAL rotation, but
                # Unity/the exporter is free to pick either sign per-keyframe with no guaranteed consistency
                # against Q_rest (read independently, from the Transform component, not the animation curve).
                # Confirmed on Dryer_0/Washer_0: their 'Close' clip's first keyframe is the exact negation of
                # Q_rest, so this delta came out as -Identity=(0,0,0,-1) instead of Identity=(0,0,0,1) --
                # 2*atan2(proj,w) then read a ~360 degree "rotation" for what is actually a ~0 degree one (the
                # reported "rotating-DRUM idle animation" symptom; there is in fact no such clip on either prop
                # -- both only ever have Open+Close -- this sign flip alone fully explains the observed farAng
                # and the missing "open" role in the role-classification dict below). Safe for every other
                # door checked (Fridge_0/Wardrobe_0/Cooler_0 all already have w>=0 everywhere -- a no-op
                # there), and safe in general as long as no door swings more than 180 degrees in one clip
                # (true for every prop seen so far: 80-135 degrees).
                if d[3] < 0:
                    d = (-d[0], -d[1], -d[2], -d[3])
                ds.append((t, d))
            deltas_by_clip[nm] = ds

        # shared reference axis for THIS leaf: the single largest-magnitude delta across its own clips (avoids
        # the near-zero-delta noise a naive "use each clip's own last keyframe" pick hits for whichever clip
        # ends near Q_rest -- confirmed on Fridge_0: that naive approach gave a garbage non-matching axis).
        best = None
        for nm, deltas in deltas_by_clip.items():
            for t, d in deltas:
                mag = math.sqrt(d[0]**2 + d[1]**2 + d[2]**2)
                if best is None or mag > best[0]: best = (mag, d)
        if best is None:
            print(f"  NOTE: no non-trivial {bone_name!r} rotation keyframes found in any clip -- no curve/angle derived")
        else:
            refAxis = (best[1][0], best[1][1], best[1][2])
            rlen = math.sqrt(sum(c*c for c in refAxis))
            refAxis = tuple(c/rlen for c in refAxis)

            # Swing axis, prop-space: rotate refAxis (still expressed in the bone's own bind-rest-local frame
            # -- see qmul(qinv(Q_rest), Qk) above) into its PARENT's frame (Skeleton) via the bone's own rest
            # rotation Q_rest. Skeleton's own local rotation cancels Root's exactly (the same mechanism that
            # lands Model_0 at identity -- verified for both Fridge_0 and Wardrobe_0), so "Skeleton-local" IS
            # prop space directly and no further correction is needed. Verified to reproduce the EXACT same
            # direction as the old bindpose-column formula for Fridge_0's Hinge (dot product 1.0000) and
            # Wardrobe_0's Left_Hinge (same axis, opposite sign) -- and to FIX Wardrobe_0's Right_Hinge, whose
            # bindpose rest-pose column-2 is not its actual hinge axis (authored differently from Left's;
            # nothing to do with mirroring -- leaf_R is a pure rotation about a fixed axis for every leaf here,
            # so no correction built purely from it could ever have moved a raw axis sitting on that same
            # fixed axis, regardless of which leaf it came from).
            axis_raw = qrot(Q_rest, refAxis)
            axisLen = math.sqrt(sum(a*a for a in axis_raw))
            axis = list(axis_raw) if axisLen < 1e-9 else [a/axisLen for a in axis_raw]
            print(f"    axis (prop-space, unit, from clip data) = ({axis[0]:.4f}, {axis[1]:.4f}, {axis[2]:.4f})")

            raw = {}
            for nm, deltas in deltas_by_clip.items():
                samples = []
                for t, d in deltas:
                    proj = d[0]*refAxis[0] + d[1]*refAxis[1] + d[2]*refAxis[2]
                    ang = math.degrees(2.0 * math.atan2(proj, d[3]))
                    samples.append((t, ang))
                raw[nm] = samples

            role = {}   # 'open' / 'close' -> (unity clip name, samples)
            for nm, samples in raw.items():
                firstAng, lastAng = samples[0][1], samples[-1][1]
                key = "open" if abs(lastAng) >= abs(firstAng) else "close"
                role.setdefault(key, (nm, samples))
            for r, (nm, samples) in role.items():
                print(f"    ROLE {r}: unity clip name {nm!r} ({len(samples)} samples), first={samples[0][1]:.3f} last={samples[-1][1]:.3f}")

            defaultOpen = role.get("open", (None, None))[0] == "Close"
            print(f"    defaultOpen = {defaultOpen}  (True iff the clip named 'Close' is the opening motion for THIS leaf)")

            if "open" in role:
                r = write_curve(os.path.join(curveDir, base + "_open.txt"), role["open"][1])
                if r: angleDeg, durationSec = r
            if "close" in role:
                write_curve(os.path.join(curveDir, base + "_close.txt"), role["close"][1])

    if angleDeg is None or durationSec is None or axis is None:
        print(f"  NOTE: could not derive angle/duration/axis for {leaf_name} from clip data -- no doors.txt entry for this leaf (mesh/curves already written above, if any)")
        return None

    return {"leaf": leaf_name, "meshFile": doorObjName, "pivot": pivot, "axis": axis,
            "angleDeg": angleDeg, "durationSec": durationSec, "defaultOpen": defaultOpen}

bodyPath = os.path.join(OUT, TARGET + ".obj")
if os.path.exists(bodyPath):
    bodyVerts = sum(1 for ln in open(bodyPath) if ln.startswith("v "))
    print(f"body {TARGET}.obj (unchanged by this script) verts={bodyVerts}")
else:
    print(f"NOTE: {TARGET}.obj (body) not found -- run extract_objects_v2.py / extract_object_named.py first")

entries = []
for leaf_name in skinned_leaf_names:
    r = process_leaf(leaf_name)
    if r: entries.append(r)

if not entries:
    print(f"NO door leaf catalog entries produced for {TARGET} -- doors.txt not modified")
else:
    catPath = os.path.join(OUT, "doors.txt")
    lines = []
    for e in entries:
        line = "%s %s %.6f %.6f %.6f %.6f %.6f %.6f %.4f %.4f %d" % (
            TARGET, e["meshFile"], e["pivot"][0], e["pivot"][1], e["pivot"][2],
            e["axis"][0], e["axis"][1], e["axis"][2], e["angleDeg"], e["durationSec"], 1 if e["defaultOpen"] else 0)
        if soundName:   # 12th field, OMITTED (not a placeholder) when this prop has no AudioSource -- matches
            line += " " + soundName   # WorldBuilder.LoadDoorCatalog's backward-compatible p.Length>=12 check
        lines.append(line)
    existing = []
    if os.path.exists(catPath):
        existing = [ln for ln in open(catPath).read().splitlines() if ln.strip() and not ln.split()[0] == TARGET]
    existing.extend(lines)
    open(catPath, "w").write("\n".join(existing) + "\n")
    print(f"wrote {len(lines)} doors.txt entries for {TARGET}:")
    for e, ln in zip(entries, lines):
        print(f"  [{e['leaf']}] {ln}")
    print("  (angleDeg is the easy-to-flip sign per leaf if the render shows a door swinging the wrong way -- no re-extract needed)")
