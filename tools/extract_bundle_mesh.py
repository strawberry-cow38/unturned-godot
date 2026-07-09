import UnityPy
bundle = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(bundle)
by_id = {o.path_id: o for o in env.objects}

def find_meshes(go_obj, acc, depth=0):
    if depth > 8 or not go_obj:
        return
    for comp in go_obj.read_typetree().get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if not co:
            continue
        if co.type.name == "MeshFilter":
            mp = co.read_typetree().get("m_Mesh", {}).get("m_PathID")
            if mp and mp in by_id and mp not in acc:
                acc.append(mp)
        elif co.type.name == "Transform":
            for ch in co.read_typetree().get("m_Children", []):
                ct = by_id.get(ch.get("m_PathID"))
                if not ct:
                    continue
                find_meshes(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")), acc, depth + 1)

def flip_place(text, off):
    ox, oy, oz = off
    out = []
    for line in text.splitlines():
        p = line.split()
        if len(p) >= 4 and p[0] == "v":
            out.append("v %r %r %r" % (float(p[1]) + ox, float(p[2]) + oy, -float(p[3]) + oz))
        elif len(p) >= 4 and p[0] == "vn":
            out.append("vn %s %s %r" % (p[1], p[2], -float(p[3])))
        elif p[:1] == ["f"] and len(p) >= 4:
            out.append("f " + " ".join(reversed(p[1:])))
        else:
            out.append(line)
    return "\n".join(out)

# hook positions from the eaglefire prefab, Z negated to match the flipped gun mesh
targets = {
    "military_30_mag":     ("items/magazines/military_30/magazine.prefab", (0.0, 0.017, 0.024)),
    "eaglefire_sight_real": ("items/sights/eaglefire_iron_sights/sight.prefab", (0.0, -0.240, -0.139)),
}
for out_name, (sub, off) in targets.items():
    prefab = next((obj for path, obj in env.container.items()
                   if path.lower().endswith(sub) and obj.type.name == "GameObject"), None)
    acc = []
    find_meshes(prefab, acc)
    if acc:
        text = by_id[acc[0]].read().export()
        open(rf"C:\claude-workspace\unturned-godot\game\content\{out_name}.txt", "w").write(flip_place(text, off))
        print(out_name, "placed at", off, "v-lines:", text.count("\nv "))
