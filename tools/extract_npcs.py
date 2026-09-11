#!/usr/bin/env python3
"""extract_npcs.py -- rip retail's NPC layer into content/npcs.json: the characters, their dialogue trees and
the vendors those trees open. Master 2026-09-11: "do human npcs, puppet players that stand around until
interacted with. RPG style dialogue, multiple options. shop ui following the same theme as the inventory ui."

WHY EXTRACT RATHER THAN AUTHOR. Retail already ships the whole thing as data -- 40 characters, 142 dialogues,
8 vendors, 31 quests -- and the dialogue format is a real branching tree with conditions and rewards, not a
list of lines. Writing our own would mean inventing a worse version of a format that is sitting on disk, and
throwing away every conversation that has already been written.

THE SHAPES, from Bundles/NPCs on the box:
  Characters/<name>/Asset.dat   Type NPC, ID, Shirt/Pants/Hat/Vest item ids, Face, Color_Skin, Color_Hair,
                                Dialogue <id>; English.dat has Name + Character.
  Dialogues/<npc>/<key>/Asset.dat  Messages N + Message_i_Pages; Responses N, and per response:
                                _Dialogue (where it goes), _Vendor (opens a shop), _Quest,
                                _Conditions (Flag_Bool/Flag_Short/Quest + Logic + Value) and
                                _Rewards (same types + Modification). English.dat has Message_i_Page_j and
                                Response_i. A response with no target ENDS the conversation -- that is how
                                "Goodbye" works, and it is an absence rather than a flag.
  Vendors/<name>/Asset.dat      Selling N / Buying N, each _Type (Item|Vehicle), _ID (a GUID), _Cost,
                                plus _Spawnpoint and _PaintColor for vehicles. English.dat Name (with retail
                                rich-text colour tags) + Description.

⚠ ITEM GUIDS ARE RESOLVED HERE, and BOTH forms are emitted. The port addresses items by numeric id; retail's
vendors address them by GUID. items_catalog.tsv carries the mapping, so this resolves it at extract time --
and keeps the guid beside the id so a FAILED resolve is visible in the data rather than becoming a silent 0.
That is the Reward_ID lesson: a wrong id in a populated id space is invisible.

Run on the box: python tools/extract_npcs.py
"""
import os, json, re, sys

BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\NPCs"
REPO = r"C:\claude-workspace\unturned-godot"
CATALOG = os.path.join(REPO, "game", "content", "items_catalog.tsv")
OUT = os.path.join(REPO, "game", "content", "npcs.json")

def parse_kv(path):
    """The flat `Key Value` .dat format, with // comments. Values keep their spaces (a Name is a sentence)."""
    d = {}
    if not os.path.exists(path): return d
    for raw in open(path, encoding="utf-8-sig", errors="ignore"):
        line = raw.strip()
        if not line or line.startswith("//"): continue
        parts = line.split(None, 1)
        d[parts[0]] = parts[1].strip() if len(parts) > 1 else ""
    return d

def i(d, k, dv=0):
    try: return int(d.get(k, dv))
    except (TypeError, ValueError): return dv

# ---- item GUID -> numeric id, from the catalog the port already ships ----
guid_to_id = {}
if os.path.exists(CATALOG):
    for line in open(CATALOG, encoding="utf-8", errors="ignore"):
        c = line.rstrip("\n").split("\t")
        if len(c) >= 8 and c[7].strip():
            guid_to_id[c[7].strip().lower()] = int(c[0])
else:
    print("!! no items_catalog.tsv -- every vendor item id will come out 0 and that will look like data, not a miss")

def resolve(ref):
    """⚠ AN ITEM REFERENCE IS A NUMERIC ID **OR** A GUID -- retail mixes both in the same Selling_N_ID field,
    and assuming guid made 69 of 78 vendor lines resolve to 0. They were ids like 121 and 333 all along. Same
    mixing as the dialogue targets; the pattern is the format's, not one asset's mistake."""
    ref = (ref or "").strip()
    if not ref: return 0
    if ref.isdigit(): return int(ref)
    return guid_to_id.get(ref.lower(), 0)

# ---- characters ----
characters = []
cdir = os.path.join(BASE, "Characters")
for name in sorted(os.listdir(cdir)) if os.path.isdir(cdir) else []:
    a = parse_kv(os.path.join(cdir, name, "Asset.dat"))
    if not a: continue
    e = parse_kv(os.path.join(cdir, name, "English.dat"))
    characters.append({
        "key": name, "id": i(a, "ID"), "guid": a.get("GUID", ""),
        "name": e.get("Name", name),
        "shirt": i(a, "Shirt"), "pants": i(a, "Pants"), "hat": i(a, "Hat"),
        "vest": i(a, "Vest"), "mask": i(a, "Mask"), "glasses": i(a, "Glasses"),
        "backpack": i(a, "Backpack"),
        "face": i(a, "Face"),
        "skin": a.get("Color_Skin", ""), "hair": a.get("Color_Hair", ""),
        "dialogue": i(a, "Dialogue"),
    })

def indexed(a, e, prefix, count_key):
    """Rebuild `Prefix_0_X`, `Prefix_1_X`... into a list. Retail counts them in count_key; trusting that count
    rather than scanning for keys is deliberate -- a response with NO keys at all (the bare "Goodbye") exists
    only in the count and the English file, and a scan would drop it."""
    out = []
    for n in range(i(a, count_key)):
        p = f"{prefix}_{n}_"
        out.append({"n": n, "keys": {k[len(p):]: v for k, v in a.items() if k.startswith(p)}})
    return out

def conditions_or_rewards(keys, a, prefix, kind):
    """`Response_0_Conditions 2` then Response_0_Condition_0_Type ... -- a nested indexed block."""
    out = []
    for n in range(int(keys.get(kind + "s", 0) or 0)):
        p = f"{prefix}_{kind}_{n}_"
        out.append({k[len(p):]: v for k, v in a.items() if k.startswith(p)})
    return out

# ---- dialogues ----
dialogues = []
ddir = os.path.join(BASE, "Dialogues")
for npc in sorted(os.listdir(ddir)) if os.path.isdir(ddir) else []:
    npcdir = os.path.join(ddir, npc)
    if not os.path.isdir(npcdir): continue
    for key in sorted(os.listdir(npcdir)):
        a = parse_kv(os.path.join(npcdir, key, "Asset.dat"))
        if a.get("Type") != "Dialogue": continue
        e = parse_kv(os.path.join(npcdir, key, "English.dat"))
        msgs = []
        for m in range(i(a, "Messages")):
            pages = [e.get(f"Message_{m}_Page_{p}", "") for p in range(i(a, f"Message_{m}_Pages"))]
            msgs.append({"pages": pages,
                         "conditions": conditions_or_rewards(
                             {"Conditions": a.get(f"Message_{m}_Conditions", 0)}, a, f"Message_{m}", "Condition")})
        resps = []
        for r in range(i(a, "Responses")):
            p = f"Response_{r}_"
            keys = {k[len(p):]: v for k, v in a.items() if k.startswith(p)}
            # ⚠ A TARGET CAN BE A GUID OR A NUMERIC ID -- retail mixes both in the same field, and assuming
            # numeric is what this crashed on first run (Response_N_Dialogue 'bf7a621c...'). Kept RAW here and
            # resolved against the guid maps after every asset has been read, because a dialogue can point
            # forward at one this loop has not reached yet.
            resps.append({
                "text": e.get(f"Response_{r}", ""),
                "dialogue_raw": keys.get("Dialogue", ""),
                "vendor_raw": keys.get("Vendor", ""),
                "quest_raw": keys.get("Quest", ""),
                "conditions": conditions_or_rewards(keys, a, p.rstrip("_"), "Condition"),
                "rewards": conditions_or_rewards(keys, a, p.rstrip("_"), "Reward"),
            })
        dialogues.append({"key": f"{npc}/{key}", "id": i(a, "ID"), "guid": a.get("GUID", ""),
                          "messages": msgs, "responses": resps})

# ---- vendors ----
vendors = []
vdir = os.path.join(BASE, "Vendors")
for root, _dirs, files in os.walk(vdir) if os.path.isdir(vdir) else []:
    if "Asset.dat" not in files: continue
    a = parse_kv(os.path.join(root, "Asset.dat"))
    if a.get("Type") != "Vendor": continue
    e = parse_kv(os.path.join(root, "English.dat"))
    def side(word):
        out = []
        for n in range(i(a, word)):
            p = f"{word}_{n}_"
            k = {kk[len(p):]: vv for kk, vv in a.items() if kk.startswith(p)}
            guid = k.get("ID", "")
            out.append({"type": k.get("Type", "Item"), "guid": guid, "item": resolve(guid),
                        "cost": int(k.get("Cost", 0) or 0),
                        "spawnpoint": k.get("Spawnpoint", ""), "paint": k.get("PaintColor", "")})
        return out
    vendors.append({"key": os.path.basename(root), "id": i(a, "ID"), "guid": a.get("GUID", ""),
                    "name": e.get("Name", os.path.basename(root)), "description": e.get("Description", ""),
                    "selling": side("Selling"), "buying": side("Buying")})

# ---- resolve the raw targets now that every id and guid is known ----
dlg_by_guid = {d["guid"].lower(): d["id"] for d in dialogues if d["guid"] and d["id"]}
# ⚠ VENDORS HAVE NO NUMERIC ID AT ALL -- their Asset.dat carries only a GUID, so a guid->id map pointed every
# one of them at 0 and made a real target look like "no vendor". They are addressed by GUID end to end, and
# the runtime looks them up the same way, so the resolved field keeps the guid rather than inventing a number.
ven_by_guid = {v["guid"].lower(): v["guid"].lower() for v in vendors if v["guid"]}

def as_id(raw, by_guid, what, owner):
    """A target is either a decimal id or a 32-hex guid. Anything else is data we do not understand, and it
    says so rather than becoming a 0 that reads as 'ends the conversation'."""
    raw = (raw or "").strip()
    if not raw: return 0
    if raw.isdigit(): return int(raw)
    got = by_guid.get(raw.lower(), 0)
    if got == 0: print(f"!! {owner}: unresolved {what} target {raw!r}")
    return got

unresolved_targets = 0
for d in dialogues:
    for r in d["responses"]:
        r["dialogue"] = as_id(r.pop("dialogue_raw"), dlg_by_guid, "dialogue", d["key"])
        vr = r.pop("vendor_raw").strip()
        # A vendor target stays a GUID (see above). A numeric one is a map-bundle vendor we have not read.
        r["vendor"] = vr.lower() if vr and not vr.isdigit() and vr.lower() in ven_by_guid else ""
        if vr and not r["vendor"]: print(f"!! {d['key']}: vendor target {vr!r} is not in core's Vendors/")
        q = r.pop("quest_raw").strip()
        r["quest"] = int(q) if q.isdigit() else 0   # quests are not extracted yet; the id is kept, unresolved guids are 0
        if r["dialogue"] == 0 and not r["vendor"] and r["quest"] == 0 and r["text"] and r["text"].lower() not in ("goodbye", "bye"):
            unresolved_targets += 1

data = {"characters": characters, "dialogues": dialogues, "vendors": vendors}
json.dump(data, open(OUT, "w", encoding="utf-8"), indent=1)

unresolved = sum(1 for v in vendors for s in (v["selling"] + v["buying"]) if s["type"] == "Item" and s["item"] == 0)
print(f"characters={len(characters)} dialogues={len(dialogues)} vendors={len(vendors)} -> {OUT} ({os.path.getsize(OUT)} bytes)")
print(f"responses with text but NO target (should be the Goodbyes only): {unresolved_targets}")
print(f"vendor lines: {sum(len(v['selling']) + len(v['buying']) for v in vendors)}, of which UNRESOLVED item guids: {unresolved}")
for c in characters[:3]:
    print(f"  {c['key']:18s} id={c['id']:4d} dialogue={c['dialogue']:4d} shirt={c['shirt']} face={c['face']} '{c['name']}'")
