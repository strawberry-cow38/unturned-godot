#!/usr/bin/env python3
"""build_texture_map.py -- compose mesh_guid -> albedo .png across the ripped tree, so the ContentProvider
can texture a ripped mesh. Chain: prefab (MeshFilter m_Mesh + MeshRenderer m_Materials) -> material
(_MainTex -> m_Texture guid) -> texture .png (.meta guid). Unturned uses shared atlas textures; the mesh
UVs already index into them, so applying the atlas as albedo is correct. First-pass: takes the first
mesh + first material per prefab (fine for single-mesh props)."""
import os, re, glob, json

EP = r"C:\claude-workspace\ripped-mb\ExportedProject\Assets"
OUT = r"C:\claude-workspace\ripped-mb\converted\texture_manifest.json"

def guid_of_meta(path):
    try:
        t = open(path, encoding="utf-8", errors="replace").read()
        m = re.search(r"guid:\s*([0-9a-fA-F]{32})", t)
        return m.group(1) if m else None
    except Exception:
        return None

# 1) material guid -> texture guid (via _MainTex)
mat_to_tex = {}
for mat in glob.glob(os.path.join(EP, "Material", "*.mat")):
    mg = guid_of_meta(mat + ".meta")
    if not mg:
        continue
    t = open(mat, encoding="utf-8", errors="replace").read()
    m = re.search(r"_MainTex:\s*\n\s*m_Texture:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})", t)
    if m:
        mat_to_tex[mg] = m.group(1)

# 2) texture guid -> png absolute path
tex_to_png = {}
for png in glob.glob(os.path.join(EP, "Texture2D", "*.png")):
    tg = guid_of_meta(png + ".meta")
    if tg:
        tex_to_png[tg] = png

# 3) prefab: mesh guid -> material guid (first of each)
mesh_to_mat = {}
for pf in glob.glob(os.path.join(EP, "coremasterbundle", "**", "*.prefab"), recursive=True):
    try:
        t = open(pf, encoding="utf-8", errors="replace").read()
    except Exception:
        continue
    mm = re.search(r"m_Mesh:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})", t)
    ma = re.search(r"m_Materials:\s*\n\s*-\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})", t)
    if mm and ma:
        mesh_to_mat.setdefault(mm.group(1), ma.group(1))

# compose mesh guid -> png
out = {}
for mesh_g, mat_g in mesh_to_mat.items():
    tex_g = mat_to_tex.get(mat_g)
    if not tex_g:
        continue
    png = tex_to_png.get(tex_g)
    if png:
        out[mesh_g] = png

json.dump(out, open(OUT, "w"), indent=0)
print(f"materials with _MainTex: {len(mat_to_tex)} | textures: {len(tex_to_png)} | "
      f"prefab mesh->mat: {len(mesh_to_mat)} | mesh->png: {len(out)}")
