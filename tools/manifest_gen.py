# Generate manifest.json (relpath -> sha256 + size) for the build_files tree, for the diff launcher.
import os, hashlib, json
root = r"C:\claude-workspace\unturned-godot\build_files"
files = {}
for dp, dn, fn in os.walk(root):
    for f in fn:
        full = os.path.join(dp, f)
        rel = os.path.relpath(full, root).replace("\\", "/")
        if rel == "manifest.json":
            continue
        h = hashlib.sha256()
        with open(full, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 16), b""):
                h.update(chunk)
        files[rel] = {"sha": h.hexdigest(), "size": os.path.getsize(full)}
json.dump({"version": "auto", "files": files}, open(os.path.join(root, "manifest.json"), "w"), indent=0)
print(f"manifest: {len(files)} files, total {sum(v['size'] for v in files.values())/1024/1024:.1f} MB")
