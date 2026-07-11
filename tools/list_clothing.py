import UnityPy
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(MB)
shirts = [p for p, o in env.container.items() if o.type.name == "Texture2D" and p.lower().endswith("shirt.png")]
pants = [p for p, o in env.container.items() if o.type.name == "Texture2D" and p.lower().endswith("pants.png")]
print(f"{len(shirts)} shirt.png, {len(pants)} pants.png")
print("=== first 20 shirt paths ===")
for p in shirts[:20]:
    print(" ", p)
