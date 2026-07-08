#!/usr/bin/env python3
"""batch_convert.py -- run the Unity-mesh decoder over the whole ripped mesh tree, emit .obj +
a master GUID->asset manifest, and categorize anything the v0 decoder can't yet handle.
Runs on the 4080 where the ripped tree lives. See tools/unity_mesh_to_obj.py for the decode."""
import sys, os, glob, re, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unity_mesh_to_obj import parse_yaml_mesh, decode, write_obj

MESH_DIR = r"C:\claude-workspace\ripped-mb\ExportedProject\Assets\Mesh"
OUT_ROOT = r"C:\claude-workspace\ripped-mb\converted"
OUT_MESH = os.path.join(OUT_ROOT, "mesh")
os.makedirs(OUT_MESH, exist_ok=True)

def guid_of(asset_path):
    try:
        t = open(asset_path + ".meta", encoding="utf-8", errors="replace").read()
        m = re.search(r"guid:\s*([0-9a-fA-F]+)", t)
        return m.group(1) if m else None
    except Exception:
        return None

manifest, errs = {}, []
stats = dict(ok=0, skinned=0, compressed=0, multistream=0, nodata=0, noguid=0, err=0)
files = glob.glob(os.path.join(MESH_DIR, "*.asset"))
for f in files:
    try:
        text = open(f, encoding="utf-8", errors="replace").read()
        if re.search(r"m_MeshCompression:\s*[1-9]", text):
            stats["compressed"] += 1; continue
        mesh = parse_yaml_mesh(text)
        if any(c["stream"] != 0 and c["dimension"] > 0 for c in mesh["channels"]):
            stats["multistream"] += 1; continue
        if not mesh["vbuf"] or mesh["vcount"] == 0:
            stats["nodata"] += 1; continue
        positions, normals, uvs, tris = decode(mesh)
        # skinned meshes still yield valid bind-pose geometry (we ignore bone channels for now)
        if re.search(r"m_BoneNameHashes:\s*\n\s*-", text) or "m_BindPose: []" not in text:
            stats["skinned"] += 1
        name = os.path.splitext(os.path.basename(f))[0]
        out = os.path.join(OUT_MESH, name + ".obj")
        write_obj(out, mesh["name"], positions, normals, uvs, tris)
        g = guid_of(f)
        if g:
            manifest[g] = "mesh/" + name + ".obj"; stats["ok"] += 1
        else:
            stats["noguid"] += 1
    except Exception as e:
        stats["err"] += 1
        if len(errs) < 12:
            errs.append(f"{os.path.basename(f)}: {type(e).__name__}: {e}")

json.dump(manifest, open(os.path.join(OUT_ROOT, "manifest.json"), "w"), indent=0)
print("total mesh .asset files:", len(files))
print("STATS:", json.dumps(stats))
print("manifest GUID entries:", len(manifest))
if errs:
    print("sample errors:")
    for e in errs:
        print("  ", e)
