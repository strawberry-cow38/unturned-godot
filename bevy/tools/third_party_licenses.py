#!/usr/bin/env python3
"""Collect the license notices of every Rust crate linked into the native binary.

Used by bevy/tools/package.sh when `cargo about` is not installed. Walks the
`cargo metadata` resolve graph from the root package over normal (non-dev,
non-build) dependencies for the target platform, and writes one text file with
each crate's name, version, SPDX expression and the verbatim text of its
LICENSE*/COPYING*/NOTICE*/COPYRIGHT* files. Identical texts are printed once
and referenced by number.

    third_party_licenses.py MANIFEST_PATH TARGET_TRIPLE OUT_FILE
"""
import hashlib
import json
import os
import re
import subprocess
import sys

NOTICE = re.compile(r"^(licen[cs]e|copying|notice|copyright|unlicense)([-_.].*)?$", re.I)


def main():
    manifest, triple, out = sys.argv[1:4]
    meta = json.loads(subprocess.run(
        ["cargo", "metadata", "--format-version", "1", "--locked",
         "--manifest-path", manifest, "--filter-platform", triple],
        check=True, capture_output=True, text=True).stdout)
    packages = {p["id"]: p for p in meta["packages"]}
    nodes = {n["id"]: n for n in meta["resolve"]["nodes"]}
    root = meta["resolve"]["root"]
    seen, queue = set(), [root]
    while queue:
        current = queue.pop()
        if current in seen:
            continue
        seen.add(current)
        for dep in nodes[current]["deps"]:
            if any(kind["kind"] is None for kind in dep["dep_kinds"]):
                queue.append(dep["pkg"])
    seen.discard(root)

    texts, order, entries, missing = {}, [], [], []
    for pid in sorted(seen, key=lambda i: (packages[i]["name"], packages[i]["version"])):
        package = packages[pid]
        directory = os.path.dirname(package["manifest_path"])
        files = sorted(f for f in os.listdir(directory)
                       if NOTICE.match(f) and os.path.isfile(os.path.join(directory, f)))
        if package.get("license_file"):
            files.append(package["license_file"])
        refs = []
        for name in dict.fromkeys(files):
            with open(os.path.join(directory, name), encoding="utf-8", errors="replace") as handle:
                text = handle.read().strip()
            key = hashlib.sha256(text.encode()).hexdigest()
            if key not in texts:
                texts[key] = len(order) + 1
                order.append(text)
            refs.append(f"{name} -> text {texts[key]}")
        if not refs:
            missing.append(package["name"])
        entries.append(f"{package['name']} {package['version']}  "
                       f"[{package.get('license') or 'see files'}]\n    "
                       + ("\n    ".join(refs) if refs else "(no license file in the crate; SPDX expression above applies)"))

    with open(out, "w", encoding="utf-8") as handle:
        handle.write("Third-party Rust crates linked into unturned-bevy\n")
        handle.write(f"Target {triple}; {len(entries)} crates; generated from `cargo metadata`.\n")
        handle.write("The Rust standard library is MIT OR Apache-2.0 (https://github.com/rust-lang/rust).\n\n")
        handle.write("\n".join(entries))
        for index, text in enumerate(order, 1):
            handle.write(f"\n\n{'=' * 78}\ntext {index}\n{'=' * 78}\n{text}\n")
    print(f"{len(entries)} crates, {len(order)} distinct license texts, "
          f"{len(missing)} without a license file -> {out}")


if __name__ == "__main__":
    main()
