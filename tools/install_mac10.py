#!/usr/bin/env python3
"""Install the approved V13 cosmetic MAC-10 art; runtime gun frame is (-L,U,-H).
The magazine/irons remain separate assets. No retail mesh data is copied.
"""
from pathlib import Path
import json,math,struct,zlib,copy
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'game/content'
PALETTE=[(49,50,49),(40,40,40),(65,64,65),(32,32,32),(83,84,82),(112,112,112)]
MAG_PALETTE=[(76,78,80)]
MAG_HOOK=(0,-.02779783393501805,.17472924187725633)
SIGHT_HOOK=(0,-.197,-.188)

def runtime(p):return (-p[0],p[1],-p[2])
def cross(a,b):return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def sub(a,b):return tuple(x-y for x,y in zip(a,b))
def png(palette):
 colors=palette+[palette[-1]]*(8-len(palette));raw=b''.join(b'\0'+bytes(sum((list(c) for c in colors[i*4:i*4+4]),[])) for i in range(2))
 def chunk(t,d):return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
 return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',4,2,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b'')
def write(path,parts,palette,pivot=(0,0,0)):
 lines=['# Approved original MAC-10 V13 game art; port frame (-L,U,-H).','s off'];allv=[];vo=no=0
 for c in range(len(palette)):lines.append('vt %.10g %.10g'%(((c%4)+.5)/4,1-((c//4)+.5)/2))
 for part in parts:
  vs=[sub(runtime(v),pivot) for v in part['vertices']];allv+=vs;lines+=['o '+part['name'],'g '+part['name']]
  lines += ['v '+' '.join(format(x,'.10g') for x in p) for p in vs]
  for ind,col in part['faces']:
   a,b,c=[vs[i] for i in ind];n=cross(sub(b,a),sub(c,a));size=math.sqrt(sum(x*x for x in n));assert size>1e-12
   lines.append('vn '+' '.join(format(x/size,'.10g') for x in n));no+=1
   lines.append('f '+' '.join('%d/%d/%d'%(vo+i+1,col+1,no) for i in ind))
  vo+=len(vs)
 path.write_text('\n'.join(lines)+'\n')
 lo=[min(v[i] for v in allv) for i in range(3)];hi=[max(v[i] for v in allv) for i in range(3)]
 return dict(box=[hi[i]-lo[i] for i in range(3)],center=[(hi[i]+lo[i])/2 for i in range(3)],parts=len(parts),triangles=sum(len(p['faces']) for p in parts))
def row(file,key,value):
 p=OUT/file;lines=p.read_bytes().splitlines(keepends=True)
 preserved=b''.join(l for l in lines if l.split(b'\t',1)[0]!=key.encode())
 if preserved and not preserved.endswith(b'\n'):preserved+=b'\n'
 p.write_bytes(preserved+(key+'\t'+value+'\n').encode())
def main():
 source=json.loads((ROOT/'tools/models/mac10_v13.json').read_text());body=source['body']+source['extended_stock'];sights=source['front']+source['rear'];mag=source['magazine']
 stats={}
 for stem,parts,pal,pivot in [('mac10_gun',body,PALETTE,(0,0,0)),('mac10_sight',sights,PALETTE,SIGHT_HOOK),('mag_mac10',mag,MAG_PALETTE,MAG_HOOK),('mac10_stock_extended',source['extended_stock'],PALETTE,(0,0,0))]:
  stats[stem]=write(OUT/(stem+'.txt'),parts,pal,pivot)
  texstem='mac10' if stem=='mac10_gun' else stem
  (OUT/(texstem+'_albedo.png')).write_bytes(png(pal))
 items=json.loads((OUT/'items/items_manifest.json').read_text())
 for ident,name,typ,parts,pal,pivot in [(9145,'MAC-10','Gun',body,PALETTE,(0,0,0)),(9146,'MAC-10 Magazine','Magazine',mag,MAG_PALETTE,MAG_HOOK),(9147,'MAC-10 Iron Sights','Sight',sights,PALETTE,SIGHT_HOOK)]:
  meta=write(OUT/'items'/(str(ident)+'.txt'),parts,pal,pivot);(OUT/'items'/(str(ident)+'.png')).write_bytes(png(pal))
  items[str(ident)]=dict(name=name,type=typ,obj=str(ident)+'.txt',tex=str(ident)+'.png',color=None,box=meta['box'],center=meta['center'],parts=meta['parts'])
 (OUT/'items/items_manifest.json').write_text(json.dumps(items,separators=(',',':'))+'\n')
 row('guns_visual.tsv','mac10','0,0.295,-0.0881588447653\t0,-0.392,-0.188\t1\tmag_mac10.txt\t1,1,1')
 row('guns_maghook.tsv','mac10',','.join(format(v,'.12g') for v in MAG_HOOK)+'\tmac10')
 row('sights.tsv','mac10',f'mac10_sight.txt\t0,-0.197,-0.188\t1,1,1\tmac10_sight_albedo.png')
 # Factory irons only at present: no invented aftermarket optic rail.
 report=dict(source='approved V13',stock='extended, fixed default; no stock toggle or animation',gun_frame='(-L,U,-H), already port-converted; no second sign flip',magazine_hook=MAG_HOOK,sight_mount=SIGHT_HOOK,aim_hook=(0,-.392,-.188),muzzle_hook=(0,.295,-.0881588447653),meshes=stats)
 (ROOT/'docs/MAC10_ASSET_LAYOUT.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
