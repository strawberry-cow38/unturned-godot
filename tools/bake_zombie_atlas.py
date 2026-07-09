from PIL import Image
import os
d = r"C:\claude-workspace\zombie_skin"

# Unturned characters = a solid skin tone with the clothing item textures (each 128 in the char UV space,
# transparent outside their region) composited over, plus the 16x16 face decal on the head UV patch.
SKIN = (150, 158, 128, 255)   # sickly pale green-grey zombie flesh
atlas = Image.new("RGBA", (128, 128), SKIN)

for layer in ["sample_pants.png", "sample_shirt.png"]:   # pants first, shirt over
    p = os.path.join(d, layer)
    if os.path.exists(p):
        atlas.alpha_composite(Image.open(p).convert("RGBA"))

# face decal onto the head-FRONT quad (the -Z head face = the port's forward): mesh UVs u[0.254-0.371]
# v[0.563-0.625]. The atlas is sampled by the mesh UVs directly (image y from top = v*H, same as the shirt).
face = Image.open(os.path.join(d, "face_19.png")).convert("RGBA")
fx0, fx1 = int(0.254 * 128), int(0.371 * 128)    # x 32-47
fy0, fy1 = int(0.563 * 128), int(0.625 * 128)    # y 72-80
atlas.alpha_composite(face.resize((fx1 - fx0, fy1 - fy0), Image.NEAREST), (fx0, fy0))

atlas.save(os.path.join(d, "zombie_atlas.png"))
# a 4x-nearest preview to eyeball
atlas.resize((512, 512), Image.NEAREST).save(os.path.join(d, "zombie_atlas_big.png"))
print("baked zombie_atlas.png (128) + preview")
