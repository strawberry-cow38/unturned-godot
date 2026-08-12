#!/usr/bin/env python3
# MAP-AWARE terrain-layer albedo bake. Terrain.cs blends 8 per-texel splat layers by sampling a Texture2DArray
# of layer albedos (content/<terrain dir>/layer0..7.png). Those were PEI's, hardcoded -- so Washington's correct
# splat was painted with PEI's textures + PEI's layer->material mapping.
#
# A map's Level.hierarchy gives each Landscape tile a "Materials" [8 GUIDs] array (the splat layer -> material
# mapping); the materials are SHARED LandscapeMaterialAssets referenced by GUID (Washington layer0 = Yukon_Dirt_00).
# Each LandscapeMaterialAsset carries Asset{Texture{Path ...}} = its albedo in core.masterbundle. We read the 8
# GUIDs, resolve each to its albedo, and extract -> content/terrain_<map>/layer{l}.png.
#   python terrain_map.py Washington   -> content\terrain_washington\layer0..7.png
import UnityPy, os, re, sys, glob

MAP  = sys.argv[1] if len(sys.argv) > 1 else "PEI"
UNT  = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
BUND = UNT + r"\Bundles\core.masterbundle"
HIER = rf"{UNT}\Maps\{MAP}\Level.hierarchy"
MATROOT = UNT + r"\Bundles\Assets\Landscapes\Materials"
OUT  = (r"C:\claude-workspace\unturned-godot\game\content\terrain"
        if MAP.upper() == "PEI" else
        rf"C:\claude-workspace\unturned-godot\game\content\terrain_{MAP.lower()}")
os.makedirs(OUT, exist_ok=True)

# 1) the 8 splat layer GUIDs, in order, from the first tile's "Materials" array (all tiles share the palette)
h = open(HIER, encoding="utf-8", errors="ignore").read()
mblock = re.search(r'"Materials"\s*\[(.*?)\]', h, re.S)
guids = re.findall(r'"GUID"\s*"([0-9a-fA-F]{32})"', mblock.group(1)) if mblock else []
print(f"{MAP}: {len(guids)} layer GUIDs")

# 2) GUID -> (albedo texture path, material name) over every shared LandscapeMaterialAsset
guid_tex = {}
for ap in glob.glob(MATROOT + r"\**\*.asset", recursive=True):
    try: txt = open(ap, encoding="utf-8", errors="ignore").read()
    except Exception: continue
    if "LandscapeMaterialAsset" not in txt: continue
    gm = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    tm = re.search(r"Texture\s*\{[^}]*?Path\s+(\S+)", txt, re.S)
    if gm and tm: guid_tex[gm.group(1).lower()] = (tm.group(1), os.path.splitext(os.path.basename(ap))[0])

# 3) extract each layer's albedo
env = UnityPy.load(BUND)
cont = {p.lower(): o for p, o in env.container.items()}
# "Washington_Grass_01_Material" -> "Grass 01"; "Russia_Road_00_Material" -> "Road". The editor paint UI shows
# THESE per map (EditorTerrain reads layers.txt), so Washington's 2nd grass reads "Grass 01" instead of PEI's
# hardcoded "Snow", its Farm_Corn reads "Corn" not "Wheat", etc. -- the labels match the ACTUAL materials.
def simplify(matname):
    s = re.sub(r"_Material$", "", matname or "")
    s = re.sub(r"^(PEI|Washington|Yukon|Russia|Germany|Greece|France|Canada|Belgium)_", "", s, flags=re.I)
    m = re.match(r"(.+?)_0*(\d+)$", s)
    if m:
        base = m.group(1).replace("_", " "); num = int(m.group(2))
        return base if num == 0 else f"{base} {num:02d}"
    return s.replace("_", " ")

labels = [f"Layer {l}" for l in range(len(guids))]
for l, g in enumerate(guids):
    hit = guid_tex.get(g.lower())
    if not hit:
        print(f"  layer{l} {g}: UNRESOLVED (no LandscapeMaterialAsset)"); continue
    texpath, matname = hit
    labels[l] = simplify(matname)
    k = texpath.replace("\\", "/").lower()
    o = cont.get("assets/coremasterbundle/" + k) or next((v for kk, v in cont.items() if kk.endswith(k) and v.type.name == "Texture2D"), None)
    if o and o.type.name == "Texture2D":
        o.read().image.save(os.path.join(OUT, f"layer{l}.png"))
        print(f"  layer{l} {g} -> {matname}  ({texpath})")
    else:
        print(f"  layer{l} {g} -> {matname}: TEXTURE NOT FOUND ({texpath})")
# map-aware paint labels for the editor (EditorTerrain reads content/<dir>/layers.txt; falls back to PEI names if absent)
open(os.path.join(OUT, "layers.txt"), "w").write("\n".join(labels) + "\n")
print("layers:", labels)
print("DONE ->", OUT)
