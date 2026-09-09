#!/usr/bin/env python3
"""Build same-file, metre-for-metre bakeicon comparisons; no camera-fit scale illusion."""
import argparse
from pathlib import Path
from PIL import Image
from measure_vehicles import ROOT, CONTENT, obj, vector, uncomment, braced, split_top
from measure_car_trailer import measurements, design, CLASSES

def generate(cls='small'):
    specs,rear=measurements();d=design(specs,rear,cls);key=d['key']
    # 512 atlas, original texture sizes/UVs retained in separate tiles.
    atlas=Image.new('RGBA',(512,512),(128,128,128,255)); tiles={}
    for name,x,y in [('golf_palette.png',0,0),('jeep_wheel_albedo.png',256,0),('car_trailer_palette.png',256,8)]:
        im=Image.open(CONTENT/name).convert('RGBA');im.putalpha(255)
        atlas.paste(im,(x,y));tiles[name]=(x,y,*im.size)
    solid={}
    def tile(color):
        color=tuple(round(v*255) for v in color)+(255,)
        if color not in solid:
            x=300+len(solid);atlas.putpixel((x,0),color);solid[color]=(x,0,1,1)
        return solid[color]
    vs=[];ts=[];ns=[];fs=[]
    def add(name,tex,offset=(0,0,0),flip=False,hide_stand=False):
        m=obj(CONTENT/name); nv,nt,nn=len(vs),len(ts),len(ns)
        vs.extend(((-p[0] if flip else p[0])+offset[0],p[1]+offset[1],p[2]+offset[2]) for p in m['vertices'])
        x,y,w,h=tiles[tex] if isinstance(tex,str) else tile(tex)
        ts.extend(((x+u*w)/512,1-(y+v*h)/512) for u,v in m['uvs'])
        ns.extend((-n[0] if flip else n[0],n[1],n[2]) for n in m['normals'])
        group=''
        faces=[]
        for line in (CONTENT/name).read_text().splitlines():
            if line.startswith('g '):group=line[2:]
            if line.startswith('f '):faces.append((group,line.split()[1:4]))
        for group,face in faces:
            if hide_stand and group.startswith('landing_'):continue
            ff=[]
            for c in face:
                ids=[int(a) for a in c.split('/')];ff.append((ids[0]+nv,ids[1]+nt,ids[2]+nn))
            fs.append(ff[::-1] if flip else ff)

    def assembled(kind):
        vs.clear();ts.clear();ns.clear();fs.clear()
        # coupled shows exact pin alignment; beside makes the relative length easiest to read.
        car_off=(0,-rear['golf']['ground'],0)
        trailer_off=((d['track']+d['tyre_width']+d['radius']) if kind=='beside' else 0,-d['ground'],0)
        if kind=='coupled':trailer_off=(0,-d['ground'],rear['golf']['rear']+d['hitch_projection']-d['king'][2])
        if kind!='alone':
            car=specs['golf'];add(car['Body'],car['Palette'],car_off)
            colors=[(.59,.62,.62),(.25,.25,.25),(.28,.23,.14),(.94,.89,.73),(.56,.13,.13)]
            for part,color in zip(car['Parts'],colors):add(part,color,car_off)
            for x,y,z,_ in car['Wheels']:add(car['Wheel'],car['WheelTex'],(x+car_off[0],y-.25+car_off[1],z+car_off[2]),x<0)
        else:trailer_off=(0,-d['ground'],0)
        for name in [key+'_body.txt',key+'_taillights.txt']:add(name,'car_trailer_palette.png',trailer_off,hide_stand=kind=='coupled')
        # Read the wheel from the design, never a literal: this said 'quad_wheel.txt' and went on
        # rendering the old .450 wheel after the trailer moved to the car's .600, so the picture
        # disagreed with the spec and looked like the change had not landed.
        for z in d['axle_zs']:
            for sign in [-1,1]:add(d['wheel_mesh'].strip('"'),d['wheel_tex'].strip('"'),(sign*d['track']/2+trailer_off[0],d['wheel_center_y']+trailer_off[1],z+trailer_off[2]),sign<0)
        lines=['# Generated static comparison: original coordinates, no scaling. Nominal rest suspension, +Y up / -Z forward.']
        for tag,values in [('v',vs),('vt',ts),('vn',ns)]:lines.extend(tag+' '+' '.join(f'{x:.8f}' for x in p) for p in values)
        lines.extend('f '+' '.join('/'.join(map(str,c)) for c in f) for f in fs)
        (CONTENT/f'car_trailer_preview_{kind}.txt').write_text('\n'.join(lines)+'\n')

    def family():
        """All three sizes plus the Golf on one ground plane, no scaling -- the only picture that
        shows the family IS a family and not three renders that each look the same size alone."""
        vs.clear();ts.clear();ns.clear();fs.clear()
        car=specs['golf']; off=(0.,-rear['golf']['ground'],0.)
        add(car['Body'],car['Palette'],off)
        colors=[(.59,.62,.62),(.25,.25,.25),(.28,.23,.14),(.94,.89,.73),(.56,.13,.13)]
        for part,color in zip(car['Parts'],colors):add(part,color,off)
        for x,y,z,_ in car['Wheels']:add(car['Wheel'],car['WheelTex'],(x+off[0],y-.25+off[1],z+off[2]),x<0)
        x=car['mesh']['size'][0]/2
        for c in CLASSES:
            dc=design(specs,rear,c); x+=dc['deck_w']/2+dc['tyre_width']+.4
            o=(x,-dc['ground'],0.)
            for n in [dc['key']+'_body.txt',dc['key']+'_taillights.txt']:add(n,'car_trailer_palette.png',o)
            for z in dc['axle_zs']:
                for sign in [-1,1]:add(dc['wheel_mesh'].strip('"'),dc['wheel_tex'].strip('"'),
                                       (sign*dc['track']/2+o[0],dc['wheel_center_y']+o[1],z+o[2]),sign<0)
            x+=dc['deck_w']/2+dc['tyre_width']
        lines=['# All three trailer sizes beside the Golf. Original coordinates, no scaling.']
        for tag,values in [('v',vs),('vt',ts),('vn',ns)]:lines.extend(tag+' '+' '.join(f'{q:.8f}' for q in p) for p in values)
        lines.extend('f '+' '.join('/'.join(map(str,c)) for c in f) for f in fs)
        (CONTENT/'car_trailer_preview_family.txt').write_text('\n'.join(lines)+'\n')

    for kind in ['alone','beside','coupled']:assembled(kind)
    family()
    atlas.save(CONTENT/'car_trailer_preview_atlas.png')
    print(f'Generated {cls} alone/beside/coupled plus the three-size family (no scale transforms).')
if __name__=='__main__':generate()
