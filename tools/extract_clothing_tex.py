#!/usr/bin/env python3
"""extract_clothing_tex.py -- rip shirt/pants CLOTHING TEXTURES from core.masterbundle.

Part of the Unturned->Godot clothing/playermodel port (phase 2). Mirrors tools/extract_gun.py's
UnityPy container-filter + `to.read().image` texture dump, but for the body-painted clothing atlas:
each shirt/pants item is a set of textures (albedo `shirt.png`/`pants.png`, optional `emission.png`,
optional `metallic.png`) that P3's ported StandardClothes shader lerps onto the single skinned body.

Item id -> bundle asset linkage: the on-disk Bundles/Items/<Slot>/<Folder>/<Folder>.dat carries the
item ID + GUID + Type; the masterbundle stores that item's textures under the LOWERCASED folder name at
  assets/coremasterbundle/items/<slot>/<folder_lower>/{shirt|pants|emission|metallic}.png
so we index the .dats once (id -> slot/folder/guid) and pull each requested id's textures.

TEXTURE ORIENTATION: saved exactly as UnityPy decodes (`.image`, upright) -- identical to every other
ripped texture in the repo (guns/vehicles/crops never flip). The Unity->Godot V-flip for MESH textures is
handled at mesh-sample time (ContentProvider.ParseObj does `1f - v`; rig_extract bakes `1.0-uv`). Whether
the BODY atlas needs a V-flip on-body is the P3 render gate (UV-atlas fidelity) -- not decided here.

Usage:
  extract_clothing_tex.py --ids 3,232,154            # shirts+pants by item id (slot auto from catalog)
  extract_clothing_tex.py --slot pants --ids 2,209,212
  extract_clothing_tex.py --slot shirt --all         # scale-out: every shirt with a bundle texture
Options: --bundle PATH  --out DIR  --tsv PATH  --catalog PATH  --items-root PATH
Re-runnable: each run rewrites the named items' textures and upserts their rows in clothing_content.tsv.
"""
import UnityPy, argparse, os, sys, glob, re

BOX = "/home/ec2-user"
DEF_BUNDLE = f"{BOX}/unturned-bundles/Bundles/core.masterbundle"
DEF_ITEMS  = f"{BOX}/unturned-bundles/Bundles/Items"
HERE = os.path.dirname(os.path.abspath(__file__))
DEF_OUT    = os.path.join(HERE, "..", "game", "content", "clothing")
DEF_TSV    = os.path.join(HERE, "..", "game", "content", "clothing_content.tsv")

# slot -> (on-disk Items subdir, container albedo basename)
SLOTS = {"shirt": ("Shirts", "shirt"), "pants": ("Pants", "pants")}
# extra maps present alongside the albedo for some items
EXTRA_MAPS = ["emission", "metallic"]

TSV_HEADER = "id\tslot\tguid\talbedo\temission\tmetallic\tmesh"


def read_dat(path):
    """Return dict of the first-token->rest keys in an Unturned .dat (BOM-tolerant, flat keys only)."""
    d = {}
    with open(path, encoding="utf-8-sig", errors="ignore") as f:
        for line in f:
            s = line.strip()
            if not s or s[0] in "[]{}/":
                continue
            parts = s.split(None, 1)
            if len(parts) == 2 and parts[0] not in d:
                d[parts[0]] = parts[1].strip()
            elif len(parts) == 1 and parts[0] not in d:
                d[parts[0]] = ""
    return d


def index_items(items_root, slot):
    """id(int) -> (folder_name, guid, slot) for every item .dat under Items/<Slot>/."""
    subdir, _ = SLOTS[slot]
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
        folder = os.path.basename(os.path.dirname(dat))
        idx[iid] = (folder, kv.get("GUID", ""), slot)
    return idx


def slot_of_id(catalog, iid):
    """Look up an item id's slot (Shirt/Pants) from items_catalog.tsv column 3."""
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
                if len(c) >= 1 and c[0].isdigit():
                    rows[int(c[0])] = c
    return rows


def save_tsv(path, rows):
    with open(path, "w", encoding="utf-8") as f:
        f.write(TSV_HEADER + "\n")
        for iid in sorted(rows):
            c = rows[iid]
            c = (c + [""] * 7)[:7]
            f.write("\t".join(c) + "\n")


def save_tex(env_container, container_path, out_png):
    o = env_container.get(container_path)
    if not o or o.type.name != "Texture2D":
        return None
    img = o.read().image.convert("RGBA")
    img.save(out_png)
    return img.size


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=DEF_BUNDLE)
    ap.add_argument("--items-root", default=DEF_ITEMS)
    ap.add_argument("--out", default=DEF_OUT)
    ap.add_argument("--tsv", default=DEF_TSV)
    ap.add_argument("--catalog", default=os.path.join(HERE, "..", "game", "content", "items_catalog.tsv"))
    ap.add_argument("--slot", choices=list(SLOTS), help="force slot (else auto per-id from catalog)")
    ap.add_argument("--ids", help="comma-separated item ids")
    ap.add_argument("--all", action="store_true", help="every item of --slot that has a bundle texture")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    env = UnityPy.load(args.bundle)
    container = {p.lower(): o for p, o in env.container.items()}

    # decide (id, slot) work list
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
                print(f"SKIP id {iid}: unknown/unsupported slot {slot!r}")
                continue
            work.append((iid, slot))

    # per-slot .dat indices (built once)
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
        folder, guid, _ = entry
        subdir, alb_base = SLOTS[slot]  # subdir e.g. "Shirts"/"Pants" -> container dir lowercased
        base = f"assets/coremasterbundle/items/{subdir.lower()}/{folder.lower()}"
        name = folder.lower()

        alb_png = f"{name}_{alb_base}.png"
        size = save_tex(container, f"{base}/{alb_base}.png", os.path.join(args.out, alb_png))
        if not size:
            print(f"SKIP id {iid} ({slot}) {folder}: no bundle {alb_base}.png at {base}")
            continue

        got = {"albedo": (alb_png, size)}
        for m in EXTRA_MAPS:
            png = f"{name}_{m}.png"
            sz = save_tex(container, f"{base}/{m}.png", os.path.join(args.out, png))
            if sz:
                got[m] = (png, sz)

        # relative-to-content-root paths (content root = game/content; clothing/ subdir)
        rel = lambda fn: f"clothing/{fn}"
        rows[iid] = [
            str(iid), slot, guid,
            rel(got["albedo"][0]),
            rel(got["emission"][0]) if "emission" in got else "",
            rel(got["metallic"][0]) if "metallic" in got else "",
            "",  # mesh: shirt/pants are texture-only (mesh-override shirts deferred)
        ]
        n_ok += 1
        extras = " ".join(f"{k}={v[1][0]}x{v[1][1]}" for k, v in got.items())
        print(f"OK  id {iid:5} {slot:5} {folder:28} {extras}")

    save_tsv(args.tsv, rows)
    print(f"\n{n_ok} item(s) ripped -> {os.path.relpath(args.out)} ; manifest {os.path.relpath(args.tsv)} ({len(rows)} rows)")


if __name__ == "__main__":
    main()
