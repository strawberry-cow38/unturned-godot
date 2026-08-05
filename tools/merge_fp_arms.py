import json, sys
RIG = sys.argv[1]; FP = sys.argv[2]
r = json.load(open(RIG))
fp = json.load(open(FP))
r["arms"] = fp                         # replace the (wrong, 3P-body-derived) arms with the real FP viewmodel arms
json.dump(r, open(RIG, "w"))
print("merged FP arms:", fp["vcount"], "verts,", len(fp["faces"]) // 3, "faces into rig.json")
