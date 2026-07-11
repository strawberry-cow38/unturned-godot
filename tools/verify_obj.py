import glob, os
D = r"C:\claude-workspace\unturned-godot\game\content\resources"
files = glob.glob(os.path.join(D, "*.obj"))
bad = 0
for p in files:
    o = open(p).read().splitlines()
    vn = sum(1 for l in o if l.startswith("vn "))
    vt = sum(1 for l in o if l.startswith("vt "))
    v  = sum(1 for l in o if l.startswith("v "))
    mxn = mxt = 0
    for l in o:
        if l.startswith("f "):
            for tok in l.split()[1:]:
                pr = tok.split("/")
                if len(pr) > 1 and pr[1]: mxt = max(mxt, int(pr[1]))
                if len(pr) > 2 and pr[2]: mxn = max(mxn, int(pr[2]))
    if mxn > vn or mxt > vt:
        bad += 1; print(f"BROKEN {os.path.basename(p)}: maxVNref={mxn} vn={vn} | maxVTref={mxt} vt={vt}")
print(f"checked {len(files)} obj files; {bad} broken")
