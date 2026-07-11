import math

def Rx(a):
    a = math.radians(a); c, s = math.cos(a), math.sin(a)
    return [[1, 0, 0], [0, c, -s], [0, s, c]]
def Ry(a):
    a = math.radians(a); c, s = math.cos(a), math.sin(a)
    return [[c, 0, s], [0, 1, 0], [-s, 0, c]]
def Rz(a):
    a = math.radians(a); c, s = math.cos(a), math.sin(a)
    return [[c, -s, 0], [s, c, 0], [0, 0, 1]]
def mm(A, B):
    return [[sum(A[i][k]*B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
def mv(M, v):
    return [sum(M[i][k]*v[k] for k in range(3)) for i in range(3)]
def unity(x, y, z):        # Unity eulerAngles: intrinsic Z,X,Y  => matrix Ry*Rx*Rz
    return mm(Ry(y), mm(Rx(x), Rz(z)))
def close(a, b):
    return all(abs(a[i][j]-b[i][j]) < 1e-4 for i in range(3) for j in range(3))
def fmt(v):
    return "(%+.2f,%+.2f,%+.2f)" % (v[0], v[1], v[2])

clocks = {"c0_norollCORRECT": (270, 0, 0), "c1_roll": (45, 270, 90),
          "c2_roll": (45, 90, 270), "c3_roll": (325, 270, 90)}
R0 = unity(*clocks["c0_norollCORRECT"])
print("=== Unity rotation of each Alberton clock (fwd = R*+Z, up = R*+Y) ===")
for name, e in clocks.items():
    R = unity(*e)
    fwd, up = mv(R, [0, 0, 1]), mv(R, [0, 1, 0])
    print("%-18s euler=%-16s same-as-c0=%s  fwd=%s up=%s" % (name, e, close(R, R0), fmt(fwd), fmt(up)))

# what the PORT's current heuristic produces: Basis(Y,180-ey)*Basis(X,ex)*Basis(Z,ez), then Godot fwd = -Z
print("\n=== current port heuristic Ry(180-ey)*Rx(ex)*Rz(ez) -> godot fwd=-Z, up=+Y ===")
def heuristic(x, y, z):
    return mm(Ry(180 - y), mm(Rx(x), Rz(z)))
H0 = heuristic(*clocks["c0_norollCORRECT"])
for name, e in clocks.items():
    H = heuristic(*e)
    fwd, up = mv(H, [0, 0, -1]), mv(H, [0, 1, 0])
    print("%-18s same-as-c0=%s  fwd=%s up=%s" % (name, close(H, H0), fmt(fwd), fmt(up)))
