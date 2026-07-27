"""Where the Unturned bundles and our object content live, on whichever machine this runs.

The object extractors used to open with

    BUND = r"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Unturned\\Bundles"
    OUT  = r"C:\\claude-workspace\\unturned-godot\\game\\content\\objects"

which meant they ran on exactly one person's Windows box and died at the first glob anywhere
else -- including on the machine that actually rebuilds the content. Resolution order:

  1. the UG_BUNDLES / UG_OBJECTS_OUT environment variables, so an unusual install is one
     export away and nobody has to edit a script;
  2. the usual Steam locations for windows / linux / mac, plus this repo's local bundle copy;
  3. for output, the repo's own game/content/objects, located relative to THIS file -- so it
     is correct from any working directory and on any OS.

Failure is a message naming what was tried and which env var fixes it, not a stack trace.
"""
import os
import sys

_BUNDLE_CANDIDATES = [
    # this repo's local copy first: it is the one guaranteed to match what we ship
    "~/unturned-bundles/Bundles",
    # windows steam, default library + the common secondary-drive spellings
    r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles",
    r"C:\Program Files\Steam\steamapps\common\Unturned\Bundles",
    r"D:\SteamLibrary\steamapps\common\Unturned\Bundles",
    r"E:\SteamLibrary\steamapps\common\Unturned\Bundles",
    # linux steam (native + proton layouts)
    "~/.steam/steam/steamapps/common/Unturned/Bundles",
    "~/.local/share/Steam/steamapps/common/Unturned/Bundles",
    # mac
    "~/Library/Application Support/Steam/steamapps/common/Unturned/Bundles",
]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def bundles():
    """Directory holding core.masterbundle + Objects/. Raises SystemExit with what it tried."""
    tried = []
    env = os.environ.get("UG_BUNDLES")
    for cand in ([env] if env else []) + _BUNDLE_CANDIDATES:
        p = os.path.expanduser(cand)
        tried.append(p)
        if os.path.isfile(os.path.join(p, "core.masterbundle")):
            return p
    print("Could not find the Unturned Bundles folder (needs core.masterbundle inside).",
          file=sys.stderr)
    print("Set UG_BUNDLES to your Unturned/Bundles path. Tried:", file=sys.stderr)
    for p in tried:
        print("  " + p, file=sys.stderr)
    raise SystemExit(2)


def map_file(*parts, map_name="PEI"):
    """A file under a retail map's folder, e.g. map_file("Level", "Objects.dat").

    Looks under UG_UNTURNED_DIR (the same variable the render harness already uses), then
    beside whatever Bundles folder resolved, then the usual Steam installs.
    """
    def roots():
        """Lazy: a later root is only resolved if the earlier ones missed, so a working
        UG_UNTURNED_DIR never triggers bundles()' 'not found' diagnostics as a side effect."""
        env = os.environ.get("UG_UNTURNED_DIR")
        if env:
            yield os.path.expanduser(env)
        yield os.path.expanduser("~/unturned")
        try:
            yield os.path.dirname(bundles())         # <install>/Bundles -> <install>
        except SystemExit:
            return

    tried = []
    for r in roots():
        p = os.path.join(r, "Maps", map_name, *parts)
        tried.append(p)
        if os.path.isfile(p):
            return p
    print(f"Could not find Maps/{map_name}/{os.path.join(*parts)}.", file=sys.stderr)
    print("Set UG_UNTURNED_DIR to your Unturned install. Tried:", file=sys.stderr)
    for p in tried:
        print("  " + p, file=sys.stderr)
    raise SystemExit(2)


def objects_out():
    """Where extracted .obj / _tex.png / guid_mesh.txt go. Repo-relative, created if absent."""
    p = os.path.expanduser(os.environ.get("UG_OBJECTS_OUT")
                           or os.path.join(REPO, "game", "content", "objects"))
    os.makedirs(p, exist_ok=True)
    return p
