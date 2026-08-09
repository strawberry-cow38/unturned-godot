"""Extract the SWING ANIMATION params (axis / angle / duration / pivot) for the wooden door barricades
(Door / Doubledoor / Gate / Hatch). Each barricade.prefab's Animation has Open/Close legacy clips (2 keys)
rotating a Hinge bone -- Doubledoor has Left_Hinge + Right_Hinge (two panels). Axis/angle math is verbatim
from extract_doors.py (clip-delta refAxis -> qrot(Q_rest) into prop space; sign left for render verification).
Pivot + axis are emitted in the SAME de-root + X-negate convention as extract_wooden_doors.py's meshes, so
they can drive those meshes directly. Writes content/objects/wooden_door_anims.txt + prints per hinge.

  wooden_door_anims.txt line:  <Form> <hingeName> px py pz  ax ay az  angleDeg durationSec
"""
import UnityPy, os, math, sys
sys.path.insert(0, r"C:\claude-workspace\unturned-godot\tools")
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
FORMS = [("Door", "door_birch"), ("Doubledoor", "doubledoor_birch"), ("Gate", "gate_birch"), ("Hatch", "hatch_birch")]

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
container = {p.lower(): obj for p, obj in env.container.items()}

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
def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = [[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)], [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)], [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]]
    return [[R[r][0]*s["x"], R[r][1]*s["y"], R[r][2]*s["z"], [pos["x"], pos["y"], pos["z"]][r]] for r in range(3)] + [[0, 0, 0, 1]]
def mat4_mul(A, B): return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def mat3_inv(m):
    a, b, c = m[0][:3]; d, e, f = m[1][:3]; g, h, i = m[2][:3]
    A = e*i-f*h; B = -(d*i-f*g); C = d*h-e*g
    det = a*A+b*B+c*C
    inv = 1.0/det
    return [[A*inv, -(b*i-c*h)*inv, (b*f-c*e)*inv], [B*inv, (a*i-c*g)*inv, -(a*f-c*d)*inv], [C*inv, -(a*h-b*g)*inv, (a*e-b*d)*inv]]
def mat_inv_affine(M):
    Li = mat3_inv(M); t = [M[0][3], M[1][3], M[2][3]]
    nt = [-(Li[r][0]*t[0]+Li[r][1]*t[1]+Li[r][2]*t[2]) for r in range(3)]
    return [[Li[0][0], Li[0][1], Li[0][2], nt[0]], [Li[1][0], Li[1][1], Li[1][2], nt[1]], [Li[2][0], Li[2][1], Li[2][2], nt[2]], [0, 0, 0, 1]]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qinv(q): x, y, z, w = q; return (-x, -y, -z, w)
def qrot(q, v):
    qv = (q[0], q[1], q[2]); uv = (qv[1]*v[2]-qv[2]*v[1], qv[2]*v[0]-qv[0]*v[2], qv[0]*v[1]-qv[1]*v[0])
    uuv = (qv[1]*uv[2]-qv[2]*uv[1], qv[2]*uv[0]-qv[0]*uv[2], qv[0]*uv[1]-qv[1]*uv[0]); w = q[3]
    return tuple(v[i] + 2.0*(w*uv[i]+uuv[i]) for i in range(3))
def qX(v): return v.X if hasattr(v, "X") else v.x
def qY(v): return v.Y if hasattr(v, "Y") else v.y
def qZ(v): return v.Z if hasattr(v, "Z") else v.z
def qW(v): return v.W if hasattr(v, "W") else v.w

def hinge_keys(clip, bone_name):
    for rcu in clip.m_RotationCurves:
        if rcu.path.split("/")[-1] == bone_name:
            return [(k.time, (qX(k.value), qY(k.value), qZ(k.value), qW(k.value))) for k in rcu.curve.m_Curve]
    return None

catalog = []
for FormName, src in FORMS:
    cont = f"assets/coremasterbundle/items/barricades/{src}/barricade.prefab"
    prefab = container[cont]
    print(f"===== {FormName} ({src}) =====")
    # de-root exactly like extract_wooden_doors.py's mesh walk (parentM = inv(root_local))
    rtt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
    root_local = trs(rtt["m_LocalPosition"], rtt["m_LocalRotation"], rtt["m_LocalScale"])
    root_inv = mat_inv_affine(root_local)
    nodes = {}
    def walk(go_pid, parentM):
        go = by_id.get(go_pid)
        if not go: return
        tt = go.read_typetree()
        name = tt.get("m_Name", "") or ""
        tr = comp_of(tt, ("Transform", "RectTransform"))
        if not tr: return
        trt = tr.read_typetree()
        lr = trt["m_LocalRotation"]
        M = mat4_mul(parentM, trs(trt["m_LocalPosition"], lr, trt["m_LocalScale"]))
        if name not in nodes:
            nodes[name] = (M, (lr["x"], lr["y"], lr["z"], lr["w"]))
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    walk(prefab.path_id, root_inv)

    animco = comp_of(prefab.read_typetree(), ("Animation",))
    anim = animco.read()
    clips = {}
    for cpref in (getattr(anim, "m_Animations", []) or []):
        clip = cpref.read()
        if clip.m_Name in ("Open", "Close"): clips[clip.m_Name] = clip
    # hinge bones = last segment of each Open-clip rotation-curve path that names a *_Hinge (not the Skeleton root)
    hinge_names = []
    for rcu in clips["Open"].m_RotationCurves:
        seg = rcu.path.split("/")[-1]
        if "Hinge" in seg and seg not in hinge_names: hinge_names.append(seg)
    print(f"  hinges: {hinge_names}")

    for hinge in hinge_names:
        if hinge not in nodes:
            print(f"  !! hinge {hinge} not in scene graph"); continue
        M, Q_rest = nodes[hinge]
        pivot = [-M[0][3], M[1][3], M[2][3]]   # X-negate to match extract_wooden_doors.py's mesh space
        # deltas across Open+Close, canonicalized (verbatim extract_doors.py)
        best = None; open_keys = None
        for nm, clip in clips.items():
            kf = hinge_keys(clip, hinge)
            if not kf: continue
            if nm == "Open": open_keys = kf
            for t, Qk in kf:
                d = qmul(qinv(Q_rest), Qk)
                if d[3] < 0: d = (-d[0], -d[1], -d[2], -d[3])
                mag = math.sqrt(d[0]**2+d[1]**2+d[2]**2)
                if best is None or mag > best[0]: best = (mag, d)
        if best is None: print(f"  !! {hinge}: no rotation keys"); continue
        refAxis = [best[1][0], best[1][1], best[1][2]]
        rl = math.sqrt(sum(c*c for c in refAxis)) or 1.0
        refAxis = [c/rl for c in refAxis]
        ax = qrot(Q_rest, refAxis)
        al = math.sqrt(sum(a*a for a in ax)) or 1.0
        axis = [-ax[0]/al, ax[1]/al, ax[2]/al]   # X-negate direction to match the X-negated mesh
        # angle = the MAX swing magnitude (the 'best' largest-delta keyframe). NOT the Open clip's last key:
        # some forms (the Doubledoor) author the Open/Close names inverted, so that key can be the CLOSED pose
        # (angle ~0). 'best' is the farthest-from-rest keyframe across both clips = the true swing. (extract_doors.py)
        bd = best[1]
        proj = bd[0]*refAxis[0] + bd[1]*refAxis[1] + bd[2]*refAxis[2]
        angleDeg = math.degrees(2.0*math.atan2(proj, bd[3]))
        duration = open_keys[-1][0]
        print(f"  {hinge}: pivot=({pivot[0]:.3f},{pivot[1]:.3f},{pivot[2]:.3f}) axis=({axis[0]:.3f},{axis[1]:.3f},{axis[2]:.3f}) angle={angleDeg:.1f} dur={duration:.3f}s")
        catalog.append(f"{FormName} {hinge} {pivot[0]:.5f} {pivot[1]:.5f} {pivot[2]:.5f} {axis[0]:.5f} {axis[1]:.5f} {axis[2]:.5f} {angleDeg:.3f} {duration:.4f}")

open(os.path.join(OUT, "wooden_door_anims.txt"), "w").write("\n".join(catalog) + "\n")
print(f"\nDONE -> wooden_door_anims.txt ({len(catalog)} hinges)")
