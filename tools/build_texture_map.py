#!/usr/bin/env python3
"""build_texture_map.py (v2, per-GameObject pairing) -- compose mesh_guid -> albedo .png.
Chain: prefab (a MeshFilter + a MeshRenderer on the SAME m_GameObject give mesh<->material) ->
material (_MainTex -> texture guid) -> texture .png (.meta guid). v1 paired first-mesh with
first-material globally, which mispairs multi-part prefabs; v2 groups components by m_GameObject.
Also emits a name-consistency report as a sanity check (mesh name vs texture name)."""
import os, re, glob, json

EP = r"C:\claude-workspace\ripped-mb\ExportedProject\Assets"
OUT = r"C:\claude-workspace\ripped-mb\converted\texture_manifest.json"
MESH_MANIFEST = r"C:\claude-workspace\ripped-mb\converted\manifest.json"

def guid_of_meta(path):
    try:
        t = open(path, encoding="utf-8", errors="replace").read()
        m = re.search(r"guid:\s*([0-9a-fA-F]{32})", t)
        return m.group(1) if m else None
    except Exception:
        return None

# 1) material guid -> texture guid
mat_to_tex = {}
for mat in glob.glob(os.path.join(EP, "Material", "*.mat")):
    mg = guid_of_meta(mat + ".meta")
    if not mg:
        continue
    t = open(mat, encoding="utf-8", errors="replace").read()
    m = re.search(r"_MainTex:\s*\n\s*m_Texture:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})", t)
    if m:
        mat_to_tex[mg] = m.group(1)

# 2) texture guid -> png path + name
tex_to_png = {}
for png in glob.glob(os.path.join(EP, "Texture2D", "*.png")):
    tg = guid_of_meta(png + ".meta")
    if tg:
        tex_to_png[tg] = png

# 3) prefabs: pair MeshFilter.mesh <-> MeshRenderer.material by shared m_GameObject
mesh_to_mat = {}
GO = re.compile(r"m_GameObject:\s*\{fileID:\s*(-?\d+)\}")
MESH = re.compile(r"m_Mesh:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})")
MAT = re.compile(r"m_Materials:\s*\n\s*-\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})")
for pf in glob.glob(os.path.join(EP, "coremasterbundle", "**", "*.prefab"), recursive=True):
    try:
        t = open(pf, encoding="utf-8", errors="replace").read()
    except Exception:
        continue
    go_mesh, go_mat = {}, {}
    for block in re.split(r"\n--- ", t):
        if "MeshFilter:" in block:
            g, m = GO.search(block), MESH.search(block)
            if g and m:
                go_mesh[g.group(1)] = m.group(1)
        elif "MeshRenderer:" in block:
            g, m = GO.search(block), MAT.search(block)
            if g and m:
                go_mat[g.group(1)] = m.group(1)
    for go, mesh_g in go_mesh.items():
        if go in go_mat:
            mesh_to_mat.setdefault(mesh_g, go_mat[go])

# compose mesh guid -> png
out = {}
for mesh_g, mat_g in mesh_to_mat.items():
    tex_g = mat_to_tex.get(mat_g)
    if tex_g and tex_g in tex_to_png:
        out[mesh_g] = tex_to_png[tex_g]

json.dump(out, open(OUT, "w"), indent=0)

# --- validation: name consistency (mesh name vs texture name) ---
mesh_names = {}
try:
    mm = json.load(open(MESH_MANIFEST))
    for g, rel in mm.items():
        mesh_names[g] = os.path.splitext(os.path.basename(rel))[0]
except Exception:
    pass

def base(s):
    # lowercase, drop non-alnum, strip trailing variant indices (Golf_Bag_1 -> golfbag)
    s = re.sub(r"(_\d+)+$", "", s)
    return re.sub(r"[^a-z0-9]", "", s.lower())

exact, related, atlas, diff, samples = 0, 0, 0, 0, []
for mesh_g, png in out.items():
    mn = mesh_names.get(mesh_g, "?")
    tn = os.path.splitext(os.path.basename(png))[0]
    a, b = base(mn), base(tn)
    if a and a == b:
        exact += 1
    elif a and (a in b or b in a):
        related += 1
    elif re.search(r"atlas|_\d+_\d+|palette|shared|albedo|texture_\d", tn.lower()):
        atlas += 1
    else:
        diff += 1
        if len(samples) < 15:
            samples.append(f"{mn}  ->  {tn}")

total = len(out)
good = exact + related + atlas
print(f"materials w/ _MainTex: {len(mat_to_tex)} | textures: {len(tex_to_png)} | "
      f"mesh->mat(per-GO): {len(mesh_to_mat)} | mesh->png: {total}")
print(f"NAME CHECK: exact {exact} | related-variant {related} | atlas/generic {atlas} | "
      f"unexplained-differ {diff}  ({100.0*good/total:.1f}% name-consistent)")
print("sample 'unexplained-differ' (mesh -> texture):")
for s in samples:
    print("  ", s)
