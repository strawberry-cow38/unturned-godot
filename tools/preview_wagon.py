#!/usr/bin/env python3
"""Static OBJ geometry inspection. Requires numpy, matplotlib and Pillow.

This is NOT a Godot render. Colours are palette samples plus inspection lighting;
wheel centres use anchors minus WheelRestDrop, without simulated suspension.
"""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

from measure_vehicles import CONTENT, ROOT, obj, read_specs


def scene(key):
    spec = read_specs((key,))[key]
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


def raster_view(faces, colors, elev, azim):
    """Orthographic depth buffer: painter sorting exposes hidden cabin faces."""
    elev,azim=np.radians([elev,azim])
    toward=np.array([np.cos(elev)*np.cos(azim),np.cos(elev)*np.sin(azim),np.sin(elev)])
    right=np.array([-np.sin(azim),np.cos(azim),0])
    up=np.cross(toward,right)
    width,height=912,534
    extent=(-3.8,3.8,-1.25,3.2)
    pixels=np.tile(np.array([243.,243.,238.])/255,(height,width,1))
    depth=np.full((height,width),-np.inf)
    projected=[]
    for triangle,color in zip(faces,colors):
        p=np.array(triangle)@np.array([right,up,toward]).T
        p[:,0]=(p[:,0]-extent[0])/(extent[1]-extent[0])*width
        p[:,1]=(extent[3]-p[:,1])/(extent[3]-extent[2])*height
        projected.append((p,np.array(color)))
    opaque=[item for item in projected if item[1][3]>=1]
    glass=sorted((item for item in projected if item[1][3]<1),key=lambda item:item[0][:,2].mean())
    for p,color in opaque+glass:
        x0,y0=np.maximum(np.floor(p[:,:2].min(axis=0)).astype(int),0)
        x1,y1=np.minimum(np.ceil(p[:,:2].max(axis=0)).astype(int),(width-1,height-1))
        if x0>x1 or y0>y1:
            continue
        x,y=np.meshgrid(np.arange(x0,x1+1)+.5,np.arange(y0,y1+1)+.5)
        a,b,c=p
        det=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
        if abs(det)<1e-8:
            continue
        u=((b[1]-c[1])*(x-c[0])+(c[0]-b[0])*(y-c[1]))/det
        v=((c[1]-a[1])*(x-c[0])+(a[0]-c[0])*(y-c[1]))/det
        w=1-u-v
        z=u*a[2]+v*b[2]+w*c[2]
        patch=depth[y0:y1+1,x0:x1+1]
        visible=(u>=0)&(v>=0)&(w>=0)&(z>patch)
        rgb=pixels[y0:y1+1,x0:x1+1]
        rgb[visible]=color[:3]*color[3]+rgb[visible]*(1-color[3])
        if color[3]>=1:
            patch[visible]=z[visible]
    return pixels,extent


def main():
    fig = plt.figure(figsize=(18,10),facecolor="#f3f3ee")
    views = [("sedan","Sedan • fleet reference",0,-90),
             ("hatchback","Hatchback • fleet reference",0,-90),
             ("van","Van • fleet reference",0,-90),
             ("wagon","Original wagon • front quarter",14,-116),
             ("wagon","Original wagon • side (−Z is front)",0,-90),
             ("wagon","Original wagon • rear quarter",16,58)]
    for i,(key,title,elev,azim) in enumerate(views,1):
        ax = fig.add_subplot(2,3,i)
        faces,colors = scene(key)
        pixels,extent=raster_view(faces,colors,elev,azim)
        ax.imshow(pixels,extent=extent)
        ax.set(xlabel="Z (m)" if elev==0 else "Projected horizontal (m)",
               ylabel="Y (m)" if elev==0 else "Projected vertical (m)")
        ax.set_title(title,loc="left",fontweight="bold")
        ax.set_facecolor("#f3f3ee")
    fig.suptitle("OBJ geometry preview — static mesh inspection, not a Godot render",fontsize=17,y=.98)
    fig.text(.5,.025,"Approximate palette colours and inspection lighting. No Godot materials, physics, transparency sorting or gameplay verification.",ha="center",fontsize=10)
    fig.subplots_adjust(top=.91,bottom=.08,wspace=.04,hspace=.1)
    out = ROOT / "notes/wagon_geometry.png"
    fig.savefig(out,dpi=145)
    print(out.relative_to(ROOT))


if __name__ == "__main__":
    main()
