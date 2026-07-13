colors = {}
for ln in open("/tmp/sight_colors.txt"):
    ln = ln.strip()
    if not ln or "\t" not in ln: continue
    gun, col = ln.split("\t")
    r, g, b = [float(x) for x in col.split(",")]
    if max(r, g, b) > 0.7:   # near-white = a textured scope body (no _MainTex extracted yet) -> gray fallback, not a white blob
        r, g, b = 0.3, 0.3, 0.3
    colors[gun] = f"{r},{g},{b}"
tsv = "/tmp/unturned-godot-repo/game/content/sights.tsv"
out = []
for ln in open(tsv):
    ln = ln.rstrip("\n")
    if not ln.strip(): continue
    c = ln.split("\t")
    if len(c) >= 4: c = c[:3]   # drop any existing color col
    out.append("\t".join(c) + "\t" + colors.get(c[0], "0.3,0.3,0.3"))
open(tsv, "w").write("\n".join(out) + "\n")
print("applied real sight colors to", len(out), "sights")
for l in out[:4]: print(" ", l)
