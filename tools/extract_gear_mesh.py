#!/usr/bin/env python3
"""extract_gear_mesh.py -- rip GEAR (hat/vest/mask/glasses/backpack) worn meshes + albedo from
core.masterbundle. Part of the Unturned->Godot clothing/playermodel port (phase 2).

Unturned gear are attachment PREFABS (not body textures): the worn model lives in
  assets/coremasterbundle/items/<slot>/<folder>/<slot_base>.prefab   (hat.prefab / vest.prefab / ...)
as a root GameObject ("Hat"/"Vest"/...) with a LODGroup + a child "Model_0" (LOD0) carrying a
MeshFilter + MeshRenderer. We resolve id -> prefab (via the on-disk .dat index, same as
extract_clothing_tex.py), pull Model_0's mesh -> .obj, and its material's _MainTex -> albedo .png.
In P3 these get Instantiate'd on the skull (hat/mask/glasses) or spine (vest/backpack) bone.

MESH ORIENTATION: emitted with the BODY/rig convention -- Unity(LH Y-up) -> Godot(RH Y-up) by negating
Z on position+normal and reversing triangle winding (matches tools/rig_extract.py's `[px,py,-pz]` +
`faces.extend([a,c,b])`, i.e. the skeleton these attach to). NOT extract_gun.py's extra X-negate (that
was a viewmodel-hand fix). UVs written raw; ContentProvider.ParseObj applies the `1f - v` flip at load.
A `--flipx` escape hatch is provided in case a piece comes out L/R-mirrored on-body at the P3 gate.
Model_0's baked LOCAL transform (pos/rot/scale) is printed -- P3 needs it for bone placement.

FLAT-COLOR GEAR: many gear materials carry NO _MainTex, just a flat `_Color` (e.g. Construction Helmet
= yellow). For those, no albedo .png is written; the _Color is printed as an sRGB hex tint for P3 to
flat-shade the mesh, and the TSV albedo column is left empty.

Usage:
  extract_gear_mesh.py --ids 27,10          # Tophat (hat) + Police Vest (vest), slot auto from catalog
  extract_gear_mesh.py --slot hat --all     # scale-out: every hat prefab
Options: --bundle PATH  --out DIR  --tsv PATH  --catalog PATH  --items-root PATH  --flipx
Re-runnable: rewrites the named items' mesh/albedo and upserts their rows in clothing_content.tsv.
"""
import UnityPy, argparse, os, sys, glob

BOX = "/home/ec2-user"
DEF_BUNDLE = f"{BOX}/unturned-bundles/Bundles/core.masterbundle"
DEF_ITEMS  = f"{BOX}/unturned-bundles/Bundles/Items"
HERE = os.path.dirname(os.path.abspath(__file__))
DEF_OUT    = os.path.join(HERE, "..", "game", "content", "clothing")
DEF_TSV    = os.path.join(HERE, "..", "game", "content", "clothing_content.tsv")

# slot -> (on-disk Items subdir, container prefab basename, attach bone [for P3 reference])
SLOTS = {
    "hat":      ("Hats", "hat", "Skull"),
    "mask":     ("Masks", "mask", "Skull"),
    "glasses":  ("Glasses", "glasses", "Skull"),
    "vest":     ("Vests", "vest", "Spine"),
    "backpack": ("Backpacks", "backpack", "Spine"),
}
TSV_HEADER = "id\tslot\tguid\talbedo\temission\tmetallic\tmesh"
# material texture-slot -> our column
TEX_SLOTS = {"_MainTex": "albedo", "_EmissionMap": "emission", "_MetallicGlossMap": "metallic", "_SpecGlossMap": "metallic"}


def read_dat(path):
    d = {}
    with open(path, encoding="utf-8-sig", errors="ignore") as f:
        for line in f:
            s = line.strip()
            if not s or s[0] in "[]{}/":
                continue
            p = s.split(None, 1)
            if p and p[0] not in d:
                d[p[0]] = p[1].strip() if len(p) == 2 else ""
    return d


def index_items(items_root, slot):
    subdir = SLOTS[slot][0]
    idx = {}
    for dat in glob.glob(os.path.join(items_root, subdir, "*", "*.dat")):
        if os.path.basename(dat).lower() == "english.dat":
            continue
        kv = read_dat(dat)
        if "ID" not in kv:
            continue
        try:
            iid = int(kv["ID"])
        except ValueError:
            continue
        idx[iid] = (os.path.basename(os.path.dirname(dat)), kv.get("GUID", ""))
    return idx


def slot_of_id(catalog, iid):
    with open(catalog, encoding="utf-8") as f:
        for line in f:
            c = line.rstrip("\n").split("\t")
            if len(c) >= 3 and c[0].isdigit() and int(c[0]) == iid:
                return c[2].lower()
    return None


def load_tsv(path):
    rows = {}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                c = line.rstrip("\n").split("\t")
                if c and c[0].isdigit():
                    rows[int(c[0])] = c
    return rows


def save_tsv(path, rows):
    with open(path, "w", encoding="utf-8") as f:
        f.write(TSV_HEADER + "\n")
        for iid in sorted(rows):
            f.write("\t".join((rows[iid] + [""] * 7)[:7]) + "\n")


def to_hex(col):
    return "#%02x%02x%02x" % tuple(max(0, min(255, round(col.get(k, 0.0) * 255))) for k in ("r", "g", "b"))


class Prefab:
    """Read-only walk of a prefab's GameObject/Transform tree from a UnityPy env."""
    def __init__(self, env):
        self.by_id = {o.path_id: o for o in env.objects}

    def comps(self, go_tt):
        out = []
        for c in go_tt.get("m_Component", []):
            cc = c.get("component", c) if isinstance(c, dict) else c
            co = self.by_id.get(cc.get("m_PathID"))
            if co:
                out.append(co)
        return out

    def find_model(self, root_go):
        """DFS for (mesh_obj, meshrenderer_obj, local_transform_tt). Prefers a GO named Model_0 with a
        MeshFilter/SkinnedMeshRenderer; falls back to the first renderable found."""
        best = None
        def rec(go, depth):
            nonlocal best
            tt = go.read_typetree()
            name = tt.get("m_Name", "")
            cs = self.comps(tt)
            mesh_obj = renderer = tr_tt = None
            for c in cs:
                if c.type.name == "MeshFilter":
                    mt = c.read_typetree()
                    mesh_obj = self.by_id.get(mt.get("m_Mesh", {}).get("m_PathID"))
                elif c.type.name == "SkinnedMeshRenderer":
                    mt = c.read_typetree()
                    mesh_obj = mesh_obj or self.by_id.get(mt.get("m_Mesh", {}).get("m_PathID"))
                    renderer = c
                elif c.type.name == "MeshRenderer":
                    renderer = c
                elif c.type.name == "Transform":
                    tr_tt = c.read_typetree()
            if mesh_obj is not None:
                cand = (mesh_obj, renderer, tr_tt, name)
                if best is None or name.lower() == "model_0":
                    best = cand
            if tr_tt:
                for ch in tr_tt.get("m_Children", []):
                    ct = self.by_id.get(ch.get("m_PathID"))
                    if ct:
                        cgo = self.by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
                        if cgo:
                            rec(cgo, depth + 1)
        rec(root_go, 0)
        return best

    def material_texs(self, renderer):
        """{column: texture_obj} + flat _Color, from the renderer's first material."""
        texs, color = {}, None
        if renderer is None:
            return texs, color
        mats = renderer.read_typetree().get("m_Materials", [])
        mo = self.by_id.get(mats[0].get("m_PathID")) if mats else None
        if not mo:
            return texs, color
        sp = mo.read_typetree().get("m_SavedProperties", {})
        for pair in sp.get("m_TexEnvs", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            col = TEX_SLOTS.get(nm)
            if col and isinstance(val, dict):
                to = self.by_id.get(val.get("m_Texture", {}).get("m_PathID"))
                if to and col not in texs:
                    texs[col] = to
        for pair in sp.get("m_Colors", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            if nm == "_Color":
                color = val
        return texs, color


def mesh_to_obj(mesh_obj, out_path, name, flipx=False):
    """UnityPy mesh -> Godot .obj. Body convention: negate Z (and X if flipx) on pos+normal, reverse
    winding, raw UVs. Returns (nverts, ntris, bbox)."""
    txt = mesh_obj.read().export()  # Wavefront OBJ text (Unity space)
    V, N, T, F = [], [], [], []
    sx = -1.0 if flipx else 1.0
    for line in txt.splitlines():
        p = line.split()
        if not p:
            continue
        if p[0] == "v":
            V.append((sx * float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vn":
            N.append((sx * float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vt":
            T.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/")
                idx.append((int(q[0]), int(q[1]) if len(q) > 1 and q[1] else None, int(q[2]) if len(q) > 2 and q[2] else None))
            F.append(list(reversed(idx)))  # reverse winding to compensate the Z (handedness) flip
    L = [f"# {name} (gear rip -> Godot: Z{'+X' if flipx else ''} negated + winding reversed)"]
    L += ["v %.6f %.6f %.6f" % v for v in V]
    L += ["vt %s %s" % t for t in T]
    L += ["vn %.6f %.6f %.6f" % n for n in N]
    for f in F:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    open(out_path, "w").write("\n".join(L) + "\n")
    xs = [v[0] for v in V]; ys = [v[1] for v in V]; zs = [v[2] for v in V]
    bbox = (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)) if V else (0,) * 6
    return len(V), len(F), bbox


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=DEF_BUNDLE)
    ap.add_argument("--items-root", default=DEF_ITEMS)
    ap.add_argument("--out", default=DEF_OUT)
    ap.add_argument("--tsv", default=DEF_TSV)
    ap.add_argument("--catalog", default=os.path.join(HERE, "..", "game", "content", "items_catalog.tsv"))
    ap.add_argument("--slot", choices=list(SLOTS))
    ap.add_argument("--ids")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--flipx", action="store_true", help="also negate X (if a piece is L/R-mirrored on-body)")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    env = UnityPy.load(args.bundle)
    container = {p.lower(): o for p, o in env.container.items()}
    pf = Prefab(env)

    work = []
    if args.all:
        if not args.slot:
            sys.exit("--all requires --slot")
        for iid in index_items(args.items_root, args.slot):
            work.append((iid, args.slot))
    else:
        if not args.ids:
            sys.exit("give --ids or --all")
        for tok in args.ids.split(","):
            iid = int(tok)
            slot = args.slot or slot_of_id(args.catalog, iid)
            if slot not in SLOTS:
                print(f"SKIP id {iid}: unsupported gear slot {slot!r}")
                continue
            work.append((iid, slot))

    idx_cache = {}
    rows = load_tsv(args.tsv)
    n_ok = 0
    for iid, slot in work:
        if slot not in idx_cache:
            idx_cache[slot] = index_items(args.items_root, slot)
        entry = idx_cache[slot].get(iid)
        if not entry:
            print(f"SKIP id {iid} ({slot}): no on-disk .dat")
            continue
        folder, guid = entry
        subdir, pbase, bone = SLOTS[slot]
        name = folder.lower()
        prefab_path = f"assets/coremasterbundle/items/{subdir.lower()}/{name}/{pbase}.prefab"
        root = container.get(prefab_path)
        if not root:
            print(f"SKIP id {iid} ({slot}) {folder}: no {pbase}.prefab in bundle")
            continue
        found = pf.find_model(root)
        if not found:
            print(f"SKIP id {iid} ({slot}) {folder}: no Model_0 mesh under prefab")
            continue
        mesh_obj, renderer, tr_tt, model_name = found

        obj_fn = f"{name}_{slot}.obj"
        nv, nt, bbox = mesh_to_obj(mesh_obj, os.path.join(args.out, obj_fn), f"{name}_{slot}", flipx=args.flipx)

        texs, color = pf.material_texs(renderer)
        got = {}
        for col, to in texs.items():
            try:
                img = to.read().image.convert("RGBA")
                png = f"{name}_{slot}_{col}.png"
                img.save(os.path.join(args.out, png))
                got[col] = (png, img.size)
            except Exception as e:
                print(f"   WARN {col} tex on {folder}: {e}")

        rel = lambda fn: f"clothing/{fn}"
        rows[iid] = [
            str(iid), slot, guid,
            rel(got["albedo"][0]) if "albedo" in got else "",
            rel(got["emission"][0]) if "emission" in got else "",
            rel(got["metallic"][0]) if "metallic" in got else "",
            rel(obj_fn),
        ]
        n_ok += 1
        lp = (tr_tt or {}).get("m_LocalPosition", {})
        pos = (round(lp.get("x", 0), 4), round(lp.get("y", 0), 4), round(lp.get("z", 0), 4))
        tint = to_hex(color) if (color and "albedo" not in got) else "-"
        texinfo = " ".join(f"{k}={v[1][0]}x{v[1][1]}" for k, v in got.items()) or "(flat _Color %s)" % tint
        print(f"OK  id {iid:5} {slot:5} {folder:24} mesh[{model_name}] {nv}v/{nt}t {texinfo}  bone={bone} model0Pos={pos}")
        print(f"      bbox x[{bbox[0]:.3f},{bbox[1]:.3f}] y[{bbox[2]:.3f},{bbox[3]:.3f}] z[{bbox[4]:.3f},{bbox[5]:.3f}]")

    save_tsv(args.tsv, rows)
    print(f"\n{n_ok} gear item(s) ripped -> {os.path.relpath(args.out)} ; manifest {os.path.relpath(args.tsv)} ({len(rows)} rows)")


if __name__ == "__main__":
    main()
