import UnityPy, os, subprocess
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(MB)
out = r"C:\claude-workspace\zombie_sounds"
os.makedirs(out, exist_ok=True)
FF = r"C:\claude-workspace\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe"

count = 0
for path, obj in dict(env.container).items():
    if obj.type.name != "AudioClip":
        continue
    pl = str(path).lower()
    if "sounds/zombies/" not in pl:
        continue
    base = pl.rsplit("/", 1)[-1]
    base = base.rsplit(".", 1)[0]   # roar_0 / groan_0 / spit_0
    if not (base.startswith("roar_") or base.startswith("groan_") or base.startswith("spit_")):
        continue
    d = obj.read()
    samples = d.samples  # {name: wav_bytes}
    if not samples:
        print("no samples for", pl); continue
    wavbytes = list(samples.values())[0]
    wav = os.path.join(out, base + ".wav")
    open(wav, "wb").write(wavbytes)
    ogg = os.path.join(out, "z" + base + ".ogg")
    r = subprocess.run([FF, "-y", "-i", wav, ogg], capture_output=True, text=True)
    ok = os.path.exists(ogg)
    print(f"{'OK ' if ok else 'FAIL'} z{base}.ogg  ({pl})")
    count += ok
print("total oggs:", count)
