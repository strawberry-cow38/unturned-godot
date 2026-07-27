#!/usr/bin/env python3
"""
shot — take a screenshot of the game. One front door.

    tools/shot.py list
    tools/shot.py deploy               -> .testresults/shots/deploy.png
    tools/shot.py pei -o /tmp/pei.png

Why this exists
---------------
strawberry: "i tell you guys to give me screenshots and u do it -- thats all i know."
That is the whole requirement, and it was not being met: every request went through
whichever bespoke `--flag` one of us happened to remember, each needing its own env,
renderer and settle timing. Miss one and the run hangs or hands back a blank frame, and
the honest report becomes "the tool broke" -- which from the outside looks like rot and
is really a coin flip on remembering the incantation.

So: the incantation lives here, once.

  - `--rendering-driver vulkan` + xvfb + movie-mode. NOT `--headless`, which renders
    nothing and yields a blank or absent capture. This is the single most common way
    these "break".
  - UG_UNTURNED_DIR defaulted, because forgetting it is what made --navshot hang past
    200s while the same code captured in 53s with it set.
  - the result is CHECKED: a missing file, an empty file, or a solid-colour frame is a
    failure with a reason, not a PNG nobody looks at closely.

Failures are loud and say which of those happened. Adding a scene is one line in SCENES.
"""

import argparse
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GODOT = os.environ.get("GODOT", os.path.expanduser(
    "~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64"))
VK_ICD = "/usr/share/vulkan/icd.d/lvp_icd.aarch64.json"
UNTURNED = os.environ.get("UG_UNTURNED_DIR", "/home/ec2-user/unturned")
OUT_DIR = os.path.join(ROOT, ".testresults/shots")

# name -> (args, env, needs_map, seconds, blurb).  {OUT} is the png path, {TMP} its directory.
# The env matters as much as the args: the vehicle scene without UG_QUICK runs a slow six-frame
# cadence and looks like a hang. Copying a manifest entry's args and dropping its env is how you
# get a correct-looking invocation that behaves like a broken one.
SCENES = {
    # No aliases that quietly substitute a different scene. A "door" entry pointing at the
    # generator rig would hand back a confident, wrong picture -- the failure mode this whole
    # tool exists to stop. A scene is here only when it renders the thing it is named after.
    "deploy":   (["--deploytest", "--shot={OUT}"], {}, False, 120, "generator + spotlight rig (the golden scene)"),
    "vehicle":  (["--vehicle={TMP}"], {"UG_QUICK": "1", "UG_VSIDE": "2"}, False, 180, "jeep beauty shot"),
    "menu":     (["--menushot={TMP}"], {}, False, 300, "the 3D barn main menu, 5 camera anchors"),
    "nav":      (["--navshot={OUT}"], {}, True, 300, "PEI navmesh overlay + zombie vision cones"),
    "pei":      (["--peidrive", "--shot={OUT}"], {}, True, 400, "PEI world, drivable"),
    "editor":   (["--editor", "--shot={OUT}"], {}, True, 400, "map editor over PEI"),
    "objects":  (["--objects", "--shot={OUT}"], {}, True, 400, "ripped prop showcase"),
}
MULTI = {"menu": "menu_00.png", "vehicle": "rig_00.png"}   # scenes whose capture lands under {TMP}


def check(path):
    """A file is not a screenshot. Say which way it failed."""
    if not os.path.isfile(path):
        return "no image was produced"
    if os.path.getsize(path) == 0:
        return "the image file is empty (0 bytes)"
    try:
        from PIL import Image
        im = Image.open(path).convert("RGB")
    except Exception as e:
        return f"the image could not be opened ({e})"
    lo_hi = im.convert("RGB").getextrema()
    if all(lo == hi for lo, hi in lo_hi):
        return f"the frame is a solid colour {tuple(v[0] for v in lo_hi)} -- nothing rendered"
    return None


def take(scene, out, verbose):
    args, scene_env, needs_map, budget, _ = SCENES[scene]
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    tmp = os.path.dirname(out)
    if needs_map and not os.path.isdir(UNTURNED):
        print(f"  FAILED  {scene}: needs the retail map data, but UG_UNTURNED_DIR "
              f"({UNTURNED}) is not a directory.", file=sys.stderr)
        print( "          Set UG_UNTURNED_DIR to your Unturned folder and retry.", file=sys.stderr)
        return 2

    a = [x.replace("{OUT}", out).replace("{TMP}", tmp) for x in args]
    cmd = ["xvfb-run", "-a", GODOT, "--path", os.path.join(ROOT, "game"),
           "--rendering-driver", "vulkan",
           "--write-movie", os.path.join(tmp, f".{scene}.avi"), "--fixed-fps", "30", "--"] + a
    env = dict(os.environ, VK_ICD_FILENAMES=VK_ICD, UG_UNTURNED_DIR=UNTURNED,
               UG_SHOT_TIMEOUT=str(budget), **scene_env)
    log = os.path.join(tmp, f".{scene}.log")
    t0 = time.time()
    try:
        with open(log, "w") as lf:
            rc = subprocess.run(cmd, env=env, stdout=lf, stderr=subprocess.STDOUT,
                                timeout=budget + 60, check=False).returncode
    except subprocess.TimeoutExpired:
        print(f"  FAILED  {scene}: hung past {budget + 60}s (the in-game watchdog should have "
              f"fired at {budget}s -- see {log})", file=sys.stderr)
        return 2
    dt = time.time() - t0

    cap = os.path.join(tmp, MULTI[scene]) if scene in MULTI else out
    why = check(cap)
    if why:
        print(f"  FAILED  {scene}: {why} (exit {rc}, {dt:.0f}s)", file=sys.stderr)
        for line in reversed(open(log, encoding="utf-8", errors="replace").read().splitlines()):
            if "[SHOT]" in line or "ERROR" in line or "Unturned map terrain not found" in line:
                print(f"          {line.strip()}", file=sys.stderr)
                break
        else:
            print(f"          full log: {log}", file=sys.stderr)
        return 1

    from PIL import Image
    w, h = Image.open(cap).size
    print(f"  OK      {scene}: {cap}  ({w}x{h}, {os.path.getsize(cap)//1024}kb, {dt:.0f}s)")
    if verbose:
        print(f"          log: {log}")
    return 0


def main():
    ap = argparse.ArgumentParser(description="take a screenshot of the game (one front door)")
    ap.add_argument("scene", nargs="?", help="scene name, or 'list'")
    ap.add_argument("-o", "--out", help="output png (default .testresults/shots/<scene>.png)")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if not args.scene or args.scene == "list":
        print("scenes:")
        for n, (_, _e, needs, budget, blurb) in sorted(SCENES.items()):
            print(f"  {n:<9} {blurb}{'   [needs retail map data]' if needs else ''}")
        print(f"\nmap data: {UNTURNED} {'(present)' if os.path.isdir(UNTURNED) else '(MISSING)'}")
        return 0
    if args.scene not in SCENES:
        print(f"unknown scene '{args.scene}'. try: {', '.join(sorted(SCENES))}", file=sys.stderr)
        return 2
    out = args.out or os.path.join(OUT_DIR, f"{args.scene}.png")
    return take(args.scene, os.path.abspath(out), args.verbose)


if __name__ == "__main__":
    sys.exit(main())
