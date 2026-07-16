#!/usr/bin/env python3
"""Visual golden-image tests -- L2 of docs/TESTING_PROPOSAL.md (phase 4).

Each tests/visual/manifest.json entry renders one scene through the existing xvfb + lavapipe +
movie-mode harness and diffs the captured PNG against tests/visual/golden/<name>.png:
mean-absolute-error over RGB (0..1) plus a changed-pixel fraction. On failure an amplified
<name>.diff.png lands next to the capture in .testresults/visual/.

Usage:
  tools/visual_tests.py                 run everything in the manifest
  tools/visual_tests.py --only 'power.*'
  tools/visual_tests.py --update all    re-baseline every golden (after a human/agent approves)
  tools/visual_tests.py --update NAME   re-baseline one

Manifest entry: { "name": str, "args": [...], "env": {...}, "tolerance": float,
                  "capture": "rig_00.png" (optional; default = the {OUT} shot path) }
Placeholders in args: {OUT} = the capture png path, {TMP} = the per-test work dir.
Exit codes match test.sh: 0 clean, 1 golden mismatch, 2 infrastructure failure.
"""
import fnmatch, json, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GODOT = os.environ.get("GODOT", os.path.expanduser(
    "~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64"))
MANIFEST = os.path.join(ROOT, "tests/visual/manifest.json")
GOLDEN_DIR = os.path.join(ROOT, "tests/visual/golden")
WORK_DIR = os.path.join(ROOT, ".testresults/visual")
VK_ICD = "/usr/share/vulkan/icd.d/lvp_icd.aarch64.json"
RENDER_TIMEOUT = 180  # s per scene; a hang is an infra failure, not a mismatch


def render(entry, work):
    """Run one scene through xvfb+lavapipe+movie-mode; return the captured png path or (None, why)."""
    os.makedirs(work, exist_ok=True)
    out = os.path.join(work, "shot.png")
    args = [a.replace("{OUT}", out).replace("{TMP}", work) for a in entry["args"]]
    cmd = ["xvfb-run", "-a", GODOT, "--path", os.path.join(ROOT, "game"),
           "--rendering-driver", "vulkan",
           "--write-movie", os.path.join(work, "mov.avi"), "--fixed-fps", "30", "--"] + args
    env = dict(os.environ, VK_ICD_FILENAMES=VK_ICD, **entry.get("env", {}))
    log = os.path.join(work, "render.log")
    try:
        with open(log, "w") as lf:
            subprocess.run(cmd, env=env, stdout=lf, stderr=subprocess.STDOUT,
                           timeout=RENDER_TIMEOUT, check=False)
    except subprocess.TimeoutExpired:
        return None, f"render timed out after {RENDER_TIMEOUT}s (see {os.path.relpath(log, ROOT)})"
    cap = os.path.join(work, entry["capture"]) if "capture" in entry else out
    if not os.path.isfile(cap):
        return None, f"no capture produced at {os.path.relpath(cap, ROOT)} (see {os.path.relpath(log, ROOT)})"
    return cap, None


def compare(captured, golden, diff_out):
    """Return (mae 0..1, changed-pixel fraction) and write an amplified diff image."""
    from PIL import Image, ImageChops
    a = Image.open(captured).convert("RGB")
    b = Image.open(golden).convert("RGB")
    if a.size != b.size:
        return None, None, f"size mismatch: captured {a.size} vs golden {b.size}"
    diff = ImageChops.difference(a, b)
    hist = diff.histogram()  # 3 x 256 channel histograms
    total = a.size[0] * a.size[1] * 3
    mae = sum(i * hist[c * 256 + i] for c in range(3) for i in range(256)) / (total * 255.0)
    # changed pixels: any channel differing by > 12/255 (below that is raster noise)
    changed = sum(hist[c * 256 + i] for c in range(3) for i in range(13, 256)) / float(total)
    diff.point(lambda v: min(255, v * 8)).save(diff_out)  # amplified delta for eyeballs
    return mae, changed, None


def main():
    only, update = "*", None
    argv = sys.argv[1:]
    while argv:
        a = argv.pop(0)
        if a == "--only" and argv: only = argv.pop(0)
        elif a == "--update" and argv: update = argv.pop(0)
        elif a in ("-h", "--help"): print(__doc__); return 0
        else: print(f"unknown arg: {a} (see --help)"); return 2
    if not os.path.isfile(MANIFEST):
        print(f"[VISUAL] no manifest at {MANIFEST}"); return 2
    entries = [e for e in json.load(open(MANIFEST)) if fnmatch.fnmatch(e["name"], only)]
    if update and update != "all":
        entries = [e for e in entries if e["name"] == update]
    if not entries:
        print(f"[VISUAL] no manifest entries match '{update or only}'"); return 2

    os.makedirs(GOLDEN_DIR, exist_ok=True)
    passed = failed = infra = updated = 0
    t0 = time.time()
    for e in entries:
        name = e["name"]
        work = os.path.join(WORK_DIR, name)
        shutil.rmtree(work, ignore_errors=True)
        ts = time.time()
        cap, why = render(e, work)
        secs = time.time() - ts
        golden = os.path.join(GOLDEN_DIR, f"{name}.png")
        if cap is None:
            print(f"[TEST] visual.{name:<36} | ERROR | {why}")
            infra += 1
            continue
        if update:
            shutil.copyfile(cap, golden)
            print(f"[TEST] visual.{name:<36} | BASELINED | {os.path.relpath(golden, ROOT)} ({secs:.0f}s)")
            updated += 1
            continue
        if not os.path.isfile(golden):
            print(f"[TEST] visual.{name:<36} | FAIL | no golden -- bake one: tools/visual_tests.py --update {name}")
            failed += 1
            continue
        mae, changed, why = compare(cap, golden, os.path.join(work, f"{name}.diff.png"))
        if why is not None:
            print(f"[TEST] visual.{name:<36} | FAIL | {why}")
            failed += 1
            continue
        tol = e.get("tolerance", 0.02)
        if mae <= tol:
            print(f"[TEST] visual.{name:<36} | PASS | mae={mae:.4f} (tol {tol}) changed={changed:.1%} ({secs:.0f}s)")
            passed += 1
        else:
            print(f"[TEST] visual.{name:<36} | FAIL | mae={mae:.4f} > tol {tol} changed={changed:.1%}")
            print(f"         captured: {os.path.relpath(cap, ROOT)}")
            print(f"         diff    : {os.path.relpath(os.path.join(work, name + '.diff.png'), ROOT)}")
            print(f"         repro   : python3 tools/visual_tests.py --only {name}   (re-baseline: --update {name})")
            failed += 1
    print(f"[VISUAL] passed={passed} failed={failed} infra={infra} updated={updated} duration={time.time() - t0:.0f}s")
    return 2 if infra else (1 if failed else 0)


if __name__ == "__main__":
    sys.exit(main())
