import UnityPy, os
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB)
targets = {
    "vehicles/engine_plane/engine_plane": "engine_plane",
    "vehicles/fighter_jet/engine": "fighterjet_engine",
    "vehicles/otter/ignition": "otter_ignition",
    "vehicles/fighter_jet/ignition": "fighterjet_ignition",
}
done = set()
for path, o in env.container.items():
    if o.type.name != "AudioClip": continue
    pl = path.lower()
    for key, out in targets.items():
        if key in pl and out not in done:
            try:
                for _, data in o.read().samples.items():
                    open(os.path.join(OUT, out + ".wav"), "wb").write(data)
                    done.add(out); print("wrote", out + ".wav", len(data), "bytes  <-", path)
            except Exception as e: print("FAIL", path, e)
print("done:", sorted(done))
