import UnityPy, os
_BUNDLE = os.environ.get("UG_MASTERBUNDLE") or r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(_BUNDLE)
# list AudioClips whose name/container hints plane/jet/engine
hits = []
for p, o in env.container.items():
    if o.type.name == "AudioClip" and any(k in p.lower() for k in ("plane","jet","otter","sandpiper","an2","engine")):
        try: nm = o.read().m_Name
        except Exception: nm = "?"
        hits.append((p, nm))
print("=== AudioClips (plane/jet/engine/otter) ===")
for p, nm in sorted(set(hits))[:40]: print(" ", nm, "|", p)
# also: any AudioClip anywhere with jet/plane in the NAME
print("=== AudioClips by NAME ===")
seen=set()
for o in env.objects:
    if o.type.name == "AudioClip":
        try: nm = o.read().m_Name
        except Exception: continue
        if any(k in nm.lower() for k in ("plane","jet")) and nm not in seen:
            seen.add(nm); print("  NAME:", nm)
