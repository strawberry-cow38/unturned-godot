"""Extract a map's AMBIENCE beds (Maps/<Map>/Environment/Ambience.unity3d) into content/audio/ambience/.

    python extract_ambience.py PEI Day Night        # default clips are Day Night
    python extract_ambience.py Washington

Source: LevelLighting.cs:1412-1422 loads that per-map bundle and pulls exactly five named clips --
Day, Night, Water, Wind, Below (PEI also ships a Rain) -- onto five AudioSources it holds as statics.
So the names are the contract, not a guess, and they are per MAP: PEI, Washington and Yukon ship
different bundles of different sizes.

TRANSCODED, not copied. UnityPy hands these back as WAV and they are enormous -- PEI's Day is 11 MB, the
five together are ~50 MB -- while every clip already in content/audio/ambience is ogg (the retail rain bed
is 705 KB). Committing raw WAV would put ~50 MB of PCM in the tree for one looping background bed. ffmpeg
does the conversion; see reference_ffmpeg_windows_4080 for where it lives on the box.

Output name is <map-lowercase>_<clip-lowercase>.ogg, e.g. pei_day.ogg, so GameAudio.Clip("ambience",
"pei_day") resolves it and two maps' beds cannot collide.
"""
import UnityPy, os, sys, subprocess, glob

MAP = sys.argv[1] if len(sys.argv) > 1 else "PEI"
CLIPS = [c for c in sys.argv[2:]] or ["Day", "Night"]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

SRC = ug_paths.map_file("Environment", "Ambience.unity3d", map_name=MAP)
OUT = os.path.join(os.path.dirname(ug_paths.objects_out()), "audio", "ambience")
os.makedirs(OUT, exist_ok=True)

def ffmpeg():
    """Where ffmpeg actually is, checked rather than assumed. The archive extracts to a versioned folder
    (ffmpeg-master-latest-win64-gpl/bin), NOT to ffmpeg/bin, and it is not on PATH -- so glob first and keep
    the bare name only as a genuine last resort. An earlier version of this returned "ffmpeg" unconditionally
    because the literal was in the same tuple as the real paths, and every convert died with WinError 2."""
    for c in glob.glob(r"C:\claude-workspace\ffmpeg\**\ffmpeg.exe", recursive=True):
        return c
    for c in (r"C:\claude-workspace\ffmpeg\bin\ffmpeg.exe", "/usr/bin/ffmpeg"):
        if os.path.isfile(c):
            return c
    return "ffmpeg"

FF = ffmpeg()
print(f"map={MAP}\n  src={SRC}\n  out={OUT}\n  ffmpeg={FF}")

env = UnityPy.load(SRC)
want = {c.lower() for c in CLIPS}
found = 0
for o in env.objects:
    if o.type.name != "AudioClip":
        continue
    d = o.read()
    name = getattr(d, "m_Name", "")
    if name.lower() not in want:
        continue
    for _, data in d.samples.items():
        tmp = os.path.join(OUT, f"_{MAP.lower()}_{name.lower()}.wav")
        dst = os.path.join(OUT, f"{MAP.lower()}_{name.lower()}.ogg")
        open(tmp, "wb").write(data)
        # -q:a 4 is the same ballpark as the retail ogg beds already in this folder; a looping ambient
        # bed is broadband hiss and does not reward a higher setting.
        r = subprocess.run([FF, "-y", "-loglevel", "error", "-i", tmp, "-c:a", "libvorbis", "-q:a", "4", dst])
        os.remove(tmp)
        if r.returncode != 0 or not os.path.isfile(dst):
            print(f"  !! ffmpeg FAILED for {name}"); continue
        print(f"  {name:6s} wav {len(data):>9,} -> {os.path.basename(dst)} {os.path.getsize(dst):>9,}")
        found += 1
        break
if not found:
    print(f"  !! no clips matched {sorted(want)} -- the bundle has: "
          + ", ".join(sorted(getattr(o.read(), 'm_Name', '?') for o in env.objects if o.type.name == 'AudioClip')))
