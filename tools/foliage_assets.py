import glob, re, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
# 7 foliage asset GUIDs (raw + net) -> index
pairs = [
    ("4da0faae3d7d7a4a85d2b1419a6a2ff4", "aefaa04d7d3d4a7a85d2b1419a6a2ff4"),
    ("99fb28c9e9ba474395563319a64f6461", "c928fb99bae9434795563319a64f6461"),
    ("587833709f9e4d40b3bf2d539b740593", "703378589e9f404db3bf2d539b740593"),
    ("976245e1c362274482d40a2fcd15e306", "e145629762c3442782d40a2fcd15e306"),
    ("c3a1a075b5328b49aa7c7c1d59dc5122", "75a0a1c332b5498baa7c7c1d59dc5122"),
    ("e8fd1c8d3db2ad4895b49b3cbf5e18d2", "8d1cfde8b23d48ad95b49b3cbf5e18d2"),
    ("1f2b0ed0b8f73549be1ba5c85ef3b716", "d00e2b1ff7b84935be1ba5c85ef3b716"),
]
guid2idx = {}
for i, (raw, net) in enumerate(pairs):
    guid2idx[raw] = i; guid2idx[net] = i
found = {}
for datp in glob.glob(os.path.join(BUND, "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"\bGUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    g = m.group(1).lower()
    if g in guid2idx:
        idx = guid2idx[g]
        name = os.path.basename(os.path.dirname(datp))
        mesh = re.search(r"^\s*Mesh\s+(.+)$", txt, re.MULTILINE | re.IGNORECASE)
        mat = re.search(r"^\s*Material\s+(.+)$", txt, re.MULTILINE | re.IGNORECASE)
        typ = re.search(r"^\s*Type\s+(.+)$", txt, re.MULTILINE | re.IGNORECASE)
        found[idx] = (name, typ.group(1).strip() if typ else "?", mesh.group(1).strip() if mesh else "?", mat.group(1).strip() if mat else "?", os.path.relpath(datp, BUND))
for i in range(len(pairs)):
    if i in found:
        n, t, me, ma, rel = found[i]
        print(f"[{i}] {n}  (Type={t})")
        print(f"     Mesh={me}")
        print(f"     Material={ma}")
        print(f"     {rel}")
    else:
        print(f"[{i}] NOT FOUND (guid {pairs[i][1]})")
