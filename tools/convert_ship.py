"""Convert an extracted Ship_N.obj into a vehicle Body mesh (ship_body.txt) in the port's vehicle
convention: length along Z (forward -Z), deck/bridge UP (+Y), keel at y=0, recentred on X/Z.

These Objects/Large props are authored Z-UP (raw Unity coords, ObjMesh CONV=1); the repo rights them
with Basis(Vector3.Right, 270 deg) == SinkSource.UprightPlacement (a PITCH about X, NOT a roll -- a roll
uprights it visually but leaves the hull yawed 90 off its heading -> drives sideways, tinyclaw).
So: transform = Ry(yaw) @ Rx(270).  Rx(270) does the Z-up->Y-up upright; Ry(yaw) points the length down Z
and the bow to -Z (forward). Keeps UVs (vt) + faces (f); rotates verts + normals.
  python convert_ship.py <src.obj> <dst.txt> [yaw_deg=90]"""
import numpy as np, sys, math
src = sys.argv[1] if len(sys.argv) > 1 else 'game/content/objects/Ship_2.obj'
dst = sys.argv[2] if len(sys.argv) > 2 else 'game/content/ship_body.txt'
yaw_deg = float(sys.argv[3]) if len(sys.argv) > 3 else 90.0
raw = open(src, encoding='utf-8', errors='ignore').read().splitlines()

def Rx(t): c, s = math.cos(t), math.sin(t); return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
def Ry(t): c, s = math.cos(t), math.sin(t); return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
M = Ry(math.radians(yaw_deg)) @ Rx(math.radians(270))    # Z-up -> Y-up (pitch), then yaw length onto Z

vs = []
for L in raw:
    if L.startswith('v '):
        p = L.split(); vs.append([float(p[1]), float(p[2]), float(p[3])])
V = np.array(vs) @ M.T
off = np.array([(V[:, 0].max() + V[:, 0].min()) / 2, V[:, 1].min(), (V[:, 2].max() + V[:, 2].min()) / 2])

out, vi = [], 0
for L in raw:
    if L.startswith('v '):
        x, y, z = V[vi] - off; vi += 1
        out.append(f"v {x:.6f} {y:.6f} {z:.6f}")
    elif L.startswith('vn '):
        p = L.split(); n = np.array([float(p[1]), float(p[2]), float(p[3])]) @ M.T
        out.append(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}")
    else:
        out.append(L)
open(dst, 'w', encoding='utf-8').write("\n".join(out) + "\n")
B = V - off
print(f"wrote {dst}  yaw={yaw_deg}")
print(f"size  X{B[:,0].max()-B[:,0].min():.1f}(width) Y{B[:,1].max()-B[:,1].min():.1f}(height) Z{B[:,2].max()-B[:,2].min():.1f}(length)")
print(f"X[{B[:,0].min():.1f},{B[:,0].max():.1f}]  Y[{B[:,1].min():.1f},{B[:,1].max():.1f}]  Z[{B[:,2].min():.1f},{B[:,2].max():.1f}]")
