from PIL import Image
import os
d = r"C:\claude-workspace\zombie_skin"

# Unturned characters = a solid skin tone with the clothing item textures (each 128 in the char UV space,
# transparent outside their region) composited over. NO face in the atlas: the u[0.254-0.371] v[0.563-0.625]
# rect this script used to stamp the face into is NOT the head-front -- the mesh triangles sampling those
# texels are skinned to Left_Arm/Spine, so the baked face rendered as a decal ON THE LEFT ARM (live MP
# bug #36). The head-front UV really is a skin-only sliver (no dedicated face patch in UV0); the face is
# drawn at runtime by the Skull-bone-attached quad in RiggedCharacter.BuildFrom instead.
SKIN = (150, 158, 128, 255)   # sickly pale green-grey zombie flesh
atlas = Image.new("RGBA", (128, 128), SKIN)

for layer in ["sample_pants.png", "sample_shirt.png"]:   # pants first, shirt over
    p = os.path.join(d, layer)
    if os.path.exists(p):
        atlas.alpha_composite(Image.open(p).convert("RGBA"))

atlas.save(os.path.join(d, "zombie_atlas.png"))
# a 4x-nearest preview to eyeball
atlas.resize((512, 512), Image.NEAREST).save(os.path.join(d, "zombie_atlas_big.png"))
print("baked zombie_atlas.png (128) + preview")
