#!/usr/bin/env python3
"""Split the existing Mossberg mesh without changing its shape; add a ramp front post.
Original face indices/winding/normals/UVs retained. Wood is item 9148, with the original grey pump and matching gameplay.
"""
from pathlib import Path
import json,subprocess,hashlib,math
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'game/content';SRC=ROOT/'tools/models/mossberg'
def parse(p):
 d={'v':[],'vt':[],'vn':[],'f':[]}
 for l in p.read_text().splitlines():
  t=l.split()
  if t and t[0] in ('v','vt','vn'):d[t[0]].append(tuple(float(x) for x in t[1:]))
  elif t and t[0]=='f':d['f'].append(tuple(tuple(int(x)-1 for x in c.split('/')) for c in t[1:]))
 return d
def write(p,d,faces,wood=False,stock=()):
 lines=['# Mossberg original geometry partition; source frame/winding preserved.','s off'];count=0
 for fi in faces:
  f=d['f'][fi];ids=[]
  for v,t,n in f:
   xyz=d['v'][v];uv=d['vt'][t];normal=d['vn'][n]
   if wood:uv=(.75,.5) if fi in stock else (uv[0]*.5,uv[1])
   lines += ['v '+' '.join(format(x,'.10g') for x in xyz),'vt '+' '.join(format(x,'.10g') for x in uv),'vn '+' '.join(format(x,'.10g') for x in normal)]
   count+=1;ids.append(f'{count}/{count}/{count}')
  lines.append('f '+' '.join(ids))
 p.write_text('\n'.join(lines)+'\n')
def append_front(sight):
 # Native Y forward, -Z up. Ramp nests into barrel; its tip matches existing ADS height .173.
 profile=[(.995,.123),(1.040,.123),(1.040,.173),(1.032,.173)]
 vs=[(x,y,-z) for x in (-.0045,.0045) for y,z in profile]
 # Produce outward winding before the established importer reverses for Godot.
 fs=[(2,1,0),(3,2,0),(4,5,6),(4,6,7)]
 for a in range(4):
  b=(a+1)%4;fs += [(a,b,b+4),(a,b+4,a+4)]
 # Native transform determinant is -1 here because profile construction uses x,y,h; correct signs by normals.
 for inds in fs:
  a,b,c=[vs[k] for k in inds];ab=[b[k]-a[k] for k in range(3)];ac=[c[k]-a[k] for k in range(3)]
  normal=(ab[1]*ac[2]-ab[2]*ac[1],ab[2]*ac[0]-ab[0]*ac[2],ab[0]*ac[1]-ab[1]*ac[0]);q=math.sqrt(sum(x*x for x in normal))
  # Reverse from h-space prism to z-space geometry. Store normal from corrected winding.
  inds=tuple(reversed(inds));normal=tuple(-x/q for x in normal)
  corners=[]
  for i in inds:
   v=vs[i];sight['v'].append((v[0],v[1]-.0555,v[2]+.102));sight['vt'].append((.5,.5));sight['vn'].append(normal)
   corners.append((len(sight['v'])-1,len(sight['vt'])-1,len(sight['vn'])-1))
  sight['f'].append(tuple(corners))
def main():
 gun=parse(SRC/'source_gun.txt');sight=parse(SRC/'source_sight.txt');assert len(gun['f'])==124 and len(sight['f'])==104
 pump=set(range(20));stock={i for i,f in enumerate(gun['f']) if i>=68 and all(gun['v'][v][1]<=-.14018 for v,t,n in f)};assert len(stock)==30
 body=sorted(set(range(124))-pump);assert len(body)==104
 # Stock, including both rear-end triangles, is all wood. Pump remains the original grey.
 woodstock=stock
 write(OUT/'bluntforce_gun.txt',gun,body);write(OUT/'bluntforce_pump.txt',gun,sorted(pump))
 write(OUT/'bluntforce_wood_gun.txt',gun,body,wood=True,stock=woodstock)
 write(OUT/'items/9148.txt',gun,body,wood=True,stock=woodstock)
 # The metal half is bit-for-bit the original atlas; only isolated stock faces point to the brown half.
 subprocess.run(['convert',str(OUT/'bluntforce_albedo.png'),'-size','256x256','xc:rgb(118,81,52)','+append',str(OUT/'bluntforce_wood_albedo.png')],check=True)
 append_front(sight);assert len(sight['f'])==116
 write(OUT/'bluntforce_sight.txt',sight,range(len(sight['f'])))
 write(OUT/'items/112.txt',gun,body);write(OUT/'items/114.txt',sight,range(len(sight['f'])))
 (OUT/'items/9148.png').write_bytes((OUT/'bluntforce_wood_albedo.png').read_bytes())
 manifest_path=OUT/'items/items_manifest.json';manifest=json.loads(manifest_path.read_text())
 manifest['9148']=dict(manifest['112'],name='Mossberg 500 Wooden',obj='9148.txt',tex='9148.png')
 for ident,d,faceids,parts in [('112',gun,body,5),('114',sight,range(len(sight['f'])),2),('9148',gun,body,5)]:
  verts=[d['v'][v] for fi in faceids for v,t,n in d['f'][fi]]
  lo=[min(v[k] for v in verts) for k in range(3)];hi=[max(v[k] for v in verts) for k in range(3)]
  manifest[ident]['box']=[hi[k]-lo[k] for k in range(3)];manifest[ident]['center']=[(hi[k]+lo[k])/2 for k in range(3)];manifest[ident]['parts']=parts
 manifest_path.write_text(json.dumps(manifest,separators=(',',':'))+'\n')
 report={'source_sha256' :hashlib.sha256((SRC/'source_gun.txt').read_bytes()).hexdigest(),'gun_triangles':104,'pump_triangles':20,'sights_triangles':116,'original_rear_sight_triangles':104,'new_front_triangles':12,'wood_stock_faces':sorted(woodstock),'pump_faces':list(range(20)),'front_tip_native':[0,1.036,-.173],'wood_variant':'Mossberg 500 Wooden / item 9148; grey pump; stock rear face wood; no loot entry','assembly_triangle_count':240}
 (ROOT/'docs/MOSSBERG_PARTS.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
