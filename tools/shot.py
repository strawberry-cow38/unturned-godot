#!/usr/bin/env python3
"""
shot — take a screenshot of the game. One front door.

    tools/shot.py list
    tools/shot.py deploy               -> .shots/deploy.png
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
import signal
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GODOT = os.environ.get("GODOT", os.path.expanduser(
    "~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64"))
VK_ICD = "/usr/share/vulkan/icd.d/lvp_icd.aarch64.json"
UNTURNED = os.environ.get("UG_UNTURNED_DIR", "/home/ec2-user/unturned")
# NOT under .testresults: test.sh CLEARS that directory at the start of every run, and it used to be
# where these landed. So running any test while a render was in flight deleted the render's output from
# under a live Godot process -- no error, the render just kept burning a core and produced nothing, which
# from the outside is indistinguishable from "renders are slow again". It cost a 12-minute render, and
# then cost a finished one that was waiting to be sent. Its own directory removes the trap instead of
# asking everyone to remember the ordering.
OUT_DIR = os.path.join(ROOT, ".shots")

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
    "nav":      (["--navshot={OUT}"], {}, True, 300, "close-up: one nav pocket + zombie vision cones"),
    "navfull":  (["--navshot={OUT}"], {"UG_NAVFULL": "1"}, True, 300, "top-down island map of all 19 nav pockets"),
    "pei":      (["--peidrive", "--shot={OUT}"], {}, True, 400, "PEI world, drivable"),
    "editor":   (["--editor", "--shot={OUT}"], {}, True, 400, "map editor over PEI"),
    "editbuild":(["--editor", "--shot={OUT}"], {"UG_EDITTOOL": "buildings"}, True, 400, "the Buildings editor: a drawn building on the stage"),
    "editbake": (["--editor", "--shot={OUT}"], {"UG_EDITTOOL": "buildings", "UG_EDITBAKE": "1"}, True, 400,
                 "build -> bake -> placed back on the map as a prop"),
    # the translator, looked at rather than trusted. IMPORT=House_03 tools/shot.py editimport
    "editimport": (["--editor", "--shot={OUT}"],
                   {"UG_EDITTOOL": "buildings", "UG_EDITIMPORT": os.environ.get("IMPORT", "House_00")}, True, 400,
                   "a retail building ported into editable walls (set IMPORT=Name)"),
    "objects":  (["--objects", "--shot={OUT}"], {}, True, 400, "ripped prop showcase"),
    # the procedural island, in the engine rather than as a preview PNG: terrain, road props and
    # buildings as the player meets them. SEED=<n> picks the island. The camera opens over the
    # first town, which is also where a playtest drops you.
    "island":   (["--editor", "--shot={OUT}"], {"UG_GENSEED": os.environ.get("SEED", "1234")}, True, 400,
                 "a GENERATED island: monuments, roads and buildings (set SEED=n)"),
    "islandtop":(["--editor", "--shot={OUT}"], {"UG_GENSEED": os.environ.get("SEED", "1234"), "UG_GENTOP": "1"}, True, 400,
                 "the same island's first town from straight above -- shows whether streets JOIN"),
    "islandplay":(["--editor", "--shot={OUT}"], {"UG_GENSEED": os.environ.get("SEED", "1234"), "UG_GENPLAY": "1"}, True, 400,
                 "the menu's Generate Map end to end: generated island, standing in it on foot"),
    # one named prop at identity + RGB axes -- the diagnostic view for a model that looks wrong.
    # `PROP=Street_Light_0 tools/shot.py prop`
    "prop":     (["--proptest=" + os.environ.get("PROP", "Street_Light_0"), "--shot={OUT}"], {}, False, 200,
                 "ONE prop at identity + RGB axes (set PROP=Name)"),
    # building tool. `walls` is the room; `wallclose` is the frame/reveal detail straight on, because
    # frame width is invisible at room distance; `wallswatch` is one panel per retail palette.
    # The death screen. Dying is not something a headless run can do on its own, so UG_BOOTCMD fires the
    # console `kill` a few seconds in and UG_SHOTTIME captures once the ragdoll has flopped and the camera
    # has swung round a little. Without the boot command this state is simply unrenderable.
    "death":     (["--peiplay", "--shot={OUT}"],
                  # These are MOVIE seconds (fixed 30 fps), not wall clock: at ~1.5 s/frame on a software
                  # rasteriser, "10 s" is 300 frames and half an hour. 1.4 s = frame 42, just after peiplay's
                  # scripted drop lands and before it climbs into a vehicle, so the corpse is on foot; the
                  # shot at 3.2 s gives the ragdoll ~54 frames to settle and the camera a little orbit.
                  {"UG_BOOTCMD": "kill", "UG_BOOTCMD_AT": "1.4", "UG_SHOTTIME": "3.2"}, True, 700,
                  "the death screen: ragdoll + orbit cam + respawn options"),
    # The directional hurt indicator. `hurttest e` fires the wire event's cosmetics at a fixed compass
    # direction (east of the player) without touching HP -- see DevConsole's hurttest verb for why a real
    # hit is otherwise unrenderable here. Timed short: the wedge fades over 3s and the shot wants it near
    # full opacity, not caught mid-fade.
    "hurt":      (["--peiplay", "--shot={OUT}"],
                  {"UG_BOOTCMD": "hurttest e", "UG_BOOTCMD_AT": "1.4", "UG_SHOTTIME": "1.7"}, True, 700,
                  "the directional hurt indicator, hit fired from the east"),
    # Low-HP vignette + desaturation. `sethp 5` sets health directly (bypassing TakeDamage's server-sink
    # routing entirely) -- see DevConsole. The overlay's strength is SMOOTHED (~0.17s time constant), so the
    # shot fires a full second after sethp rather than right after it, to catch it settled rather than mid-ramp.
    "lowhp":     (["--peiplay", "--shot={OUT}"],
                  {"UG_BOOTCMD": "sethp 5", "UG_BOOTCMD_AT": "1.4", "UG_SHOTTIME": "2.4"}, True, 700,
                  "the low-HP vignette + desaturation at 5/100 health"),
    # NO `smoke` / `flare` / `holdnade` SCENES, and the absence is deliberate. All three were --peiplay with
    # a UG_BOOTCMD, and --peiplay's scripted player is in the jeep by frame 50 (~1.7 s) and drives off: the
    # flare one ran the full 12 minutes and captured the inside of a windscreen, and a 3 s smoke fuse cannot
    # resolve in frame at all. They were removed rather than left with plausible-looking timings, because a
    # scene that renders confidently and shows the wrong thing is exactly what the header above forbids
    # ("A scene is here only when it renders the thing it is named after"). `throwables` below does the job
    # on a bare stage in ~1 min; the in-hand pose is cow tools' 1P viewmodel and they render it themselves.
    # The throwable FX on a bare stage -- see BuildThrowTest for why --peiplay cannot show these (its
    # scripted player is in the jeep by 1.7 s). Cheap: a plane and some particles, ~1 min rather than 22.
    "throwables":(["--throwtest", "--shot={OUT}"], {"UG_SHOTTIME": "4.8"}, False, 400,
                  "thrown smoke (red/green/white) + two lit flares, ~1.8s after the fuse"),
    "walls":     (["--walls", "--shot={OUT}"], {}, False, 200, "building tool: a drawn room with openings"),
    "wallclose": (["--walls", "--shot={OUT}"], {"UG_WALLCLOSE": "1"}, False, 200, "close on one opening: reveal + frame"),
    "wallswatch":(["--walls", "--shot={OUT}"], {"UG_WALLSWATCH": "1"}, False, 200, "all 52 retail palettes, one panel each"),
}
MULTI = {"menu": "menu_00.png", "vehicle": "rig_00.png"}   # scenes whose capture lands under {TMP}


def reap(proc):
    """Kill the whole process GROUP, not just the child we can see.

    subprocess.run(timeout=...) kills only the direct child -- which here is `xvfb-run`, while the
    Godot it launched is a GRANDchild and quietly survives. A capture that never reaches its exit
    path does not idle, it spins: one such orphan ran 2h39m at 101% CPU and a leaked --dedicated
    server another 1h07m, together eating ~1.6 of this box's 4 cores. Nothing errors, so from the
    outside it just looks like "the headless renders got slow and flaky again" -- and the next
    person to run one blames the render code. Hence start_new_session + killpg.
    """
    for sig in (signal.SIGTERM, signal.SIGKILL):
        try:
            os.killpg(os.getpgid(proc.pid), sig)
        except (ProcessLookupError, PermissionError, OSError):
            return
        try:
            proc.wait(timeout=10)
            return
        except subprocess.TimeoutExpired:
            continue


def strays():
    """Long-lived Godot processes already running -- they will steal cores from this render."""
    try:
        ps = subprocess.run(["ps", "-o", "pid=,etimes=,pcpu=,args=", "-C",
                             os.path.basename(GODOT)], capture_output=True, text=True, timeout=10)
    except Exception:
        return []
    out = []
    for line in ps.stdout.splitlines():
        f = line.split(None, 3)
        if len(f) == 4 and f[1].isdigit() and int(f[1]) > 1800:   # older than any scene's budget
            out.append((f[0], int(f[1]), f[2]))
    return out


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


def take(scene, out, verbose, realtime=False):
    args, scene_env, needs_map, budget, _ = SCENES[scene]
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    tmp = os.path.dirname(out)
    if needs_map and not os.path.isdir(UNTURNED):
        print(f"  FAILED  {scene}: needs the retail map data, but UG_UNTURNED_DIR "
              f"({UNTURNED}) is not a directory.", file=sys.stderr)
        print( "          Set UG_UNTURNED_DIR to your Unturned folder and retry.", file=sys.stderr)
        return 2

    a = [x.replace("{OUT}", out).replace("{TMP}", tmp) for x in args]
    # MOVIE MODE IS THE DEFAULT AND IT IS A BLIND SPOT. --write-movie runs Godot at a fixed
    # deterministic step, which the real game never does, so anything real-time-specific cannot appear in
    # any shot taken here. That is not hypothetical: on 2026-08-08 impact chips were invisible in game for
    # hours while every harness render showed perfect cones -- the old, known-broken build rendered them
    # just as cleanly, so the instrument could not see the bug it was certifying.
    #
    # --realtime drops it. --shot= is an IN-ENGINE viewport capture, not a grab off the movie writer, so a
    # single-frame scene does not need movie mode at all (verified on claw, which has no GPU and renders
    # on lavapipe under xvfb). Movie mode stays the default because the multi-frame scenes -- menu, vehicle
    # -- depend on the fixed step to land their frames.
    #
    # GOTCHA when you use it: captures fire on a frame COUNT, so without --fixed-fps "frame 6" arrives at a
    # different wall-clock moment. Sample too early and you get a clean picture of a broken effect.
    movie = [] if realtime else ["--write-movie", os.path.join(tmp, f".{scene}.avi"), "--fixed-fps", "30"]
    cmd = ["xvfb-run", "-a", GODOT, "--path", os.path.join(ROOT, "game"),
           "--rendering-driver", "vulkan", "--audio-driver", "Dummy"] + movie + ["--"] + a
    env = dict(os.environ, VK_ICD_FILENAMES=VK_ICD, UG_UNTURNED_DIR=UNTURNED,
               UG_SHOT_TIMEOUT=str(budget), **scene_env)
    log = os.path.join(tmp, f".{scene}.log")
    for pid, secs, cpu in strays():
        print(f"  WARNING Godot pid {pid} has run {secs // 60}m at {cpu}% cpu and is competing "
              f"for cores with this render.", file=sys.stderr)
        print(f"          If it is a leftover it is safe to kill; CHECK FIRST -- this box is "
              f"shared, and a long test run looks the same from here.", file=sys.stderr)
    t0 = time.time()
    # start_new_session puts the child in its own process group so reap() can take the whole tree
    # down; without it a timeout leaves Godot orphaned and spinning. See reap().
    with open(log, "w") as lf:
        proc = subprocess.Popen(cmd, env=env, stdout=lf, stderr=subprocess.STDOUT,
                                start_new_session=True)
        try:
            rc = proc.wait(timeout=budget + 60)
        except subprocess.TimeoutExpired:
            reap(proc)
            print(f"  FAILED  {scene}: hung past {budget + 60}s (the in-game watchdog should have "
                  f"fired at {budget}s -- see {log}); killed its process group", file=sys.stderr)
            return 2
        except KeyboardInterrupt:
            reap(proc)                       # Ctrl-C must not leave a spinning capture behind
            raise
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
    ap.add_argument("-o", "--out", help="output png (default .shots/<scene>.png)")
    ap.add_argument("-v", "--verbose", action="store_true")
    ap.add_argument("--realtime", action="store_true",
                    help="drop movie mode and render in REAL TIME. Movie mode is a fixed deterministic "
                         "step and hides real-time-only bugs -- use this to verify anything timing "
                         "dependent (particles, one-shot bursts, anything spawned in a physics tick).")
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
    return take(args.scene, os.path.abspath(out), args.verbose, args.realtime)


if __name__ == "__main__":
    sys.exit(main())
