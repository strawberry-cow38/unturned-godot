"""Steam economy item definitions from Unturned's EconInfo.bin (the cache the game keeps at its root; ~16k itemdefs).

Cosmetics, mystery boxes and keys have NO English.dat -- their display name + description live here (retail shows the
econ name for those). Layout = TempSteamworksEconomy.ReadEconInfo (U3-SDK), a .NET BinaryReader stream:
  int32 version (3); int32 count; per item: name, display_type, description, name_color (7-bit-length UTF8 strings),
  int32 itemdefid, bool marketable, int32 scraps, 16-byte System.Guid (target_game_asset_guid = the item .dat GUID),
  int32 item_skin, int32 item_effect, int32 quality, int32 econ_type, [v>=2] int64 creationTimeUtc, [v>=3] bool promo.
Usage: by_guid = econ_info.load()  ->  { guid32hex: (name, description, display_type) }  (skins/effects skipped).
"""
import os, struct, uuid

DEFAULT = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\EconInfo.bin"

def _read_str(b, o):
    n = 0; shift = 0
    while True:
        c = b[o]; o += 1
        n |= (c & 0x7f) << shift
        if not (c & 0x80): break
        shift += 7
    return b[o:o + n].decode("utf-8", "replace"), o + n

def iter_items(path=DEFAULT):
    b = open(path, "rb").read()
    version, count = struct.unpack_from("<ii", b, 0); o = 8
    for _ in range(count):
        name, o = _read_str(b, o); display_type, o = _read_str(b, o)
        description, o = _read_str(b, o); name_color, o = _read_str(b, o)
        itemdefid, marketable, scraps = struct.unpack_from("<i?i", b, o); o += 9
        guid = uuid.UUID(bytes_le=b[o:o + 16]).hex; o += 16
        item_skin, item_effect, quality, econ_type = struct.unpack_from("<iiii", b, o); o += 16
        if version >= 2: o += 8
        if version >= 3: o += 1
        yield dict(name=name, display_type=display_type, description=description, name_color=name_color,
                   itemdefid=itemdefid, guid=guid, item_skin=item_skin, item_effect=item_effect, quality=quality, econ_type=econ_type)

def load(path=DEFAULT):
    """The plain (no skin, no mythical effect) econ item per game asset GUID; first seen wins."""
    out = {}
    if not os.path.exists(path): return out
    for it in iter_items(path):
        if it["item_skin"] or it["item_effect"] or not it["name"]: continue
        out.setdefault(it["guid"], (it["name"], it["description"], it["display_type"]))
    return out

if __name__ == "__main__":
    m = load()
    print("econ itemdefs by guid:", len(m))
    for g, (n, d, t) in list(m.items())[:5]: print(" ", g, "|", n, "|", t, "|", d[:50])
