#!/usr/bin/env python3
"""Static OBJ geometry inspection. Requires numpy, matplotlib and Pillow.

This is NOT a Godot render. Colours are palette samples plus inspection lighting;
wheel centres use anchors minus WheelRestDrop, without simulated suspension.
"""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection
import numpy as np
from PIL import Image

from measure_vehicles import CONTENT, ROOT, obj, read_specs


def scene(key):
    spec = read_specs()[key]
    faces, colors = [], []
    palette = Image.open(CONTENT / spec["Palette"]).convert("RGBA")

    def add(mesh, solid=None, transform=None, atlas=None, glass=False):
        v = np.array(mesh["vertices"])
        if transform:
            v = transform(v)
        for face in mesh["faces"]:
            corners = [[int(i)-1 if i else -1 for i in c.split("/")] for c in face[:3]]
            tri = v[[c[0] for c in corners]]
            n = np.cross(tri[1]-tri[0],tri[2]-tri[0])
            norm = np.linalg.norm(n)
            if norm < 1e-12:
                continue
            if solid is None:
                uv = mesh["uvs"][corners[0][1]]
                color = np.array(atlas.getpixel((int((uv[0] % 1)*atlas.width),int((uv[1] % 1)*atlas.height))))/255
                if color[3]<0.5:
                    color = np.array([0.20,0.39,0.30,1.])
                else:
                    color[3] = 1
            else:
                color = np.array(solid,dtype=float)
            if not glass:
                light = np.array([-.6,.9,-.3]); light /= np.linalg.norm(light)
                color[:3] *= .6 + .4*abs(np.dot(n/norm,light))
            faces.append(tri[:,[2,0,1]])  # drawing axes Z, X, Y
            colors.append(color)

    add(spec["mesh"],atlas=palette)
    wheel = obj(CONTENT / spec["Wheel"])
    wheel_atlas = Image.open(CONTENT / spec["WheelTex"]).convert("RGBA")
    for i,(x,y,z,steer) in enumerate(spec["Wheels"]):
        scale = spec["radii"][i] / spec["WheelRadius"]
        add(wheel,transform=lambda v,x=x,y=y,z=z,scale=scale: v*np.array([-scale if x<0 else scale,scale,scale])+np.array([x,y-.25,z]),atlas=wheel_atlas)
    for part in spec["Parts"]:
        color = (.25,.25,.25,1) if "seats" in part else (.28,.23,.14,1) if "steer" in part else (.94,.89,.73,1) if "headlights" in part else (.56,.13,.13,1)
        add(obj(CONTENT / part),solid=color)
    for p in sorted(CONTENT.glob(spec["GlassMesh"].removesuffix(".txt")+"_*.txt")):
        add(obj(p),solid=(.62,.73,.78,.38),glass=True)
    return faces,colors


def main():
    fig = plt.figure(figsize=(15,10),facecolor="#f3f3ee")
    views = [("sedan","Sedan • source",12,-116), ("wagon","Wagon • front quarter",12,-116),
             ("wagon","Wagon • side (−Z is front)",0,-90), ("wagon","Wagon • rear quarter",16,58)]
    for i,(key,title,elev,azim) in enumerate(views,1):
        ax = fig.add_subplot(2,2,i,projection="3d",computed_zorder=False)
        faces,colors = scene(key)
        ax.add_collection3d(Poly3DCollection(faces,facecolors=colors,edgecolors=(.1,.1,.1,.12),linewidths=.2,zsort="average"))
        ax.set(xlim=(-3.6,3.6),ylim=(-1.8,1.8),zlim=(-.7,2.5),xlabel="Z (m)",ylabel="X (m)",zlabel="Y (m)")
        ax.set_box_aspect((7.2,3.6,3.2))
        ax.set_proj_type("ortho")
        ax.view_init(elev=elev,azim=azim)
        ax.set_title(title,loc="left",fontweight="bold")
        ax.set_facecolor("#f3f3ee")
        if elev == 0:
            ax.set_yticks([])
            ax.set_ylabel("")
    fig.suptitle("OBJ geometry preview — static mesh inspection, not a Godot render",fontsize=17,y=.98)
    fig.text(.5,.025,"Approximate palette colours and inspection lighting. No Godot materials, physics, transparency sorting or gameplay verification.",ha="center",fontsize=10)
    fig.subplots_adjust(top=.91,bottom=.08,wspace=.04,hspace=.1)
    out = ROOT / "notes/wagon_geometry.png"
    fig.savefig(out,dpi=145)
    print(out.relative_to(ROOT))


if __name__ == "__main__":
    main()
