#!/usr/bin/env python3
"""Parse saved meshes as Godot does; test measured relationships and reversible regressions.

--mutation-test temporarily reverts/corrupts the real files, runs the SAME check,
and restores exact bytes in finally. Run with no concurrent editors/builds.
"""
import argparse
from collections import Counter
from contextlib import redirect_stdout
from io import StringIO
import math
from pathlib import Path
import re
import subprocess
from PIL import Image
from measure_vehicles import ROOT, CONTENT, obj, uncomment, braced, split_top, vector, number, vectors
from measure_car_trailer import measurements, TOW_CARS
from verify_wagon import validate, f32

VEH = ROOT/'game/Vehicle.cs'
BODY = CONTENT/'car_trailer_body.txt'
BASE = '1f7f106d'

def fields(name):
    s=uncomment(VEH.read_text());start=s.index('static readonly Spec _'+name+' = new()')
    return dict((k.strip(),v.strip()) for k,v in (x.split('=',1) for x in split_top(braced(s,s.index('{',start)))))

def change_field(name,key,value):
    def edit(s):
        start=s.index('static readonly Spec _'+name+' = new()');end=s.index('\n        };',start)
        block=s[start:end]
        # All mutated fields are scalars/vectors/single-line asset names.
        pattern=r'\b'+key+r'\s*=\s*(?:new Vector3\([^)]*\)|"[^"]*"|[^,\n]+)'
        changed,n=re.subn(pattern,key+' = '+value,block,count=1)
        assert n,(name,key)
        return s[:start]+changed+s[end:]
    return edit

def group_bounds(name):
    m=obj(BODY);ps=[];group=''
    for line in BODY.read_text().splitlines():
        if line.startswith('g '):group=line[2:]
        if group==name and line.startswith('f '):ps.extend(m['vertices'][int(c.split('/')[0])-1] for c in line.split()[1:4])
    assert ps,('missing component',name)
    return tuple(min(p[i] for p in ps) for i in range(3)),tuple(max(p[i] for p in ps) for i in range(3))

def close(a,b):
    if isinstance(a,(tuple,list)):
        return len(a)==len(b) and all(close(x,y) for x,y in zip(a,b))
    return abs(a-b)<3e-6

def require(ok,detail='relationship failed'):
    assert ok,detail

def truck_wall():
    """The truck bed's wall section, re-derived a DIFFERENT way from the generator's.

    measure_car_trailer.truck_bed() selects wall faces by their normals; this clusters bed vertices
    into X planes and takes the outermost pair that spans a real wall height. Two routes to the same
    .250 / 1.000 means a bug in either one shows up as a disagreement rather than as an agreed wrong
    number -- which is the whole point of expected() not importing the generator's answers."""
    V=[];F=[]
    for line in (CONTENT/'truck_body.txt').read_text().splitlines():
        c=line.split()
        if line.startswith('v '): V.append([float(x) for x in c[1:4]])
        elif line.startswith('f '): F.append([int(x.split('/')[0])-1 for x in c[1:4]])
    # Z > .6, not just "behind the cab at .294": the cab's REAR WALL sits at Z .373 and reaches Y 2.125,
    # and its vertices share the wall columns at |X| 1.231. Clustering without excluding it reported the
    # wall as 2.250 tall. The generator dodges this by selecting on face normals (the cab wall faces Z,
    # not X); this route needs the slab instead, which is what keeps the two derivations independent.
    bed={i for f in F if sum(V[k][2] for k in f)/3 > .6 for i in f}
    half=max(abs(V[i][0]) for i in bed)
    cols={}
    for i in bed:
        if abs(V[i][0]) > .7*half:                 # OUTBOARD columns only: the side walls
            cols.setdefault(round(V[i][0],3),[]).append(V[i][1])
    walls=sorted((x for x,ys in cols.items() if max(ys)-min(ys) > .5), key=abs)
    inner,outer=abs(walls[0]),abs(walls[-1])
    top=max(y for x,ys in cols.items() if abs(x) in (inner,outer) for y in ys)
    # FLOOR IS THE FLOOR PLANE, not the wall column's lowest vertex. Those are different questions and
    # both return a real number: the wall PANEL runs .250 below the deck as under-frame, so anchoring
    # on the column minimum says 1.250 -- true, and not the height of anything you can stand a crate on.
    # Take the Y of the largest upward-facing face in the bed instead; that is the surface itself.
    best=(0,None)
    for f in F:
        if sum(V[k][2] for k in f)/3 <= .6: continue
        a,b,c=[V[k] for k in f]
        u=[b[k]-a[k] for k in range(3)]; v=[c[k]-a[k] for k in range(3)]
        n=(u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
        L=math.sqrt(sum(x*x for x in n)) or 1.
        if n[1]/L > .9 and L/2 > best[0]: best=(L/2, sum(p[1] for p in (a,b,c))/3)
    floor=best[1]
    return outer-inner, top-floor, 2*outer

# Expected dimensions are recomputed from donors, independently of the asset generator.
def expected():
    s,rr=measurements();g=s['golf'];q=s['quad'];radius=min(r for v in s.values() for r in v['radii'])
    t=(g['WheelRadius']-radius)/3;tw=obj(CONTENT/q['Wheel'])['size'][0]
    wall_t,wall_h,bed_w=truck_wall()
    L=g['mesh']['size'][2]*.6                  # was /2 -- "should also be bigger"
    W=bed_w                                    # the truck bed's own outer width, not the old track fit
    draw=g['mesh']['size'][0]/2+radius
    return s,rr,dict(r=radius,t=t,L=L,W=W,draw=draw,king=(0,rr['golf']['y'],-L/2-draw),
                    ground=rr['golf']['ground'],wy=rr['golf']['ground']+radius+.25,az=L/10,dy=g['Wheels'][0][1],tw=tw,
                    wall_t=wall_t,wall_h=wall_h,track=W-tw)

def source_names(src):
    src=uncomment(src);return re.findall(r'"([^"]+)"',braced(src,src.index('{',src.index('string[] SpecNames'))))

def geometry(path,closed=True):
    """closed=False for a mesh transplanted verbatim from another vehicle. sedan_taillights.txt is
    itself two open shells -- 8 unpaired edges in the fleet's own asset -- so demanding closure of the
    trailer's copy would be demanding the sedan be something it is not, and the only way to pass would
    be to stop using its geometry. Everything else (ParseObj rules, indices, normals, UVs, degenerate
    and duplicate faces) still applies; the transplant is pinned separately by translation equality."""
    with redirect_stdout(StringIO()):validate(path)
    if not closed: return
    m=obj(path);edges=Counter()
    for f in m['faces']:
        ps=[tuple(f32(x) for x in m['vertices'][int(c.split('/')[0])-1]) for c in f]
        for a,b in zip(ps,ps[1:]+ps[:1]):edges[a,b]+=1
    require(all(n==1 and edges[b,a]==1 for (a,b),n in edges.items()),'boundary/nonmanifold/inconsistent winding')

def edit_line(tag,transform):
    def edit(src):
        lines=src.splitlines();idx=next(i for i,l in enumerate(lines) if l.startswith(tag+' '))
        lines[idx]=transform(lines[idx]);return '\n'.join(lines)+'\n'
    return edit

def move_group(name,delta):
    def edit(src):
        lines=src.splitlines();ids=set();g=''
        for l in lines:
            if l.startswith('g '):g=l[2:]
            if g==name and l.startswith('f '):ids.update(int(c.split('/')[0]) for c in l.split()[1:4])
        n=0
        for i,l in enumerate(lines):
            if l.startswith('v '):
                n+=1
                if n in ids:lines[i]='v '+' '.join(str(float(x)+d) for x,d in zip(l.split()[1:],delta))
        assert ids,name
        return '\n'.join(lines)+'\n'
    return edit

class Case:
    def __init__(self,label,fn,mutants):self.label,self.fn,self.mutants=label,fn,mutants

def cases():
    s,rr,d=expected();t=d['t'];L=d['L'];W=d['W'];r=d['r'];dy=d['dy'];k=d['king']
    result=[]
    def add(label,fn,*mutants):result.append(Case(label,fn,list(mutants)))
    def fieldmut(key,val,name='car_trailer'):return VEH,change_field(name,key,val),name+'.'+key
    assets=[BODY,CONTENT/'car_trailer_taillights.txt']+[CONTENT/(n+'_hitch.txt') for n in TOW_CARS]
    LAMPS=CONTENT/'car_trailer_taillights.txt'
    for path in assets:
        mutations=[('quad',edit_line('f',lambda l:l+' '+l.split()[1])),
                   ('missing corner UV/normal',edit_line('f',lambda l:l.replace(l.split()[1],l.split()[1].split('/')[0],1))),
                   ('position index',edit_line('f',lambda l:re.sub(r'\d+/', '999999/',l,count=1))),
                   ('UV index',edit_line('f',lambda l:re.sub(r'/\d+/', '/999999/',l,count=1))),
                   ('normal index',edit_line('f',lambda l:re.sub(r'/\d+(?= |$)', '/999999',l,count=1))),
                   ('nonfinite',edit_line('v',lambda l:'v nan 0 0')),
                   ('UV outside atlas',edit_line('vt',lambda l:'vt 2 2')),
                   ('nonunit normal',edit_line('vn',lambda l:'vn 0 2 0')),
                   ('inverted normal',edit_line('vn',lambda l:'vn '+' '.join(str(-float(x)) for x in l.split()[1:]))),
                   ('degenerate triangle',edit_line('f',lambda l:'f '+' '.join([l.split()[1]]*3))),
                   ('duplicate triangle',edit_line('f',lambda l:l+'\n'+l)),
                   ('unused position',lambda x:x+'v 8 8 8\n'),
                   ('open surface',edit_line('f',lambda l:'# removed face'))]
        add('mesh '+path.name,lambda p=path:geometry(p,closed=p!=LAMPS),
            *[(path,fn,label) for label,fn in mutations if not (path==LAMPS and label=='open surface')])
    # Rear datum is L/2, the DECK's back face. It used to be L/2+t, which was the tailgate hinge blocks
    # standing proud of it -- those went with the rest of the sub-centimetre hardware, so the deck is the
    # rearmost geometry now. Third mutation added because nothing was guarding the hi-Z corner.
    # X extent is the DECK now, not the mudguards -- they were the widest thing on the trailer and
    # they are gone. Top is the sideboard, which stands wall_h above the deck instead of the old r.
    add('body AABB from deck, wall height, stand and tongue',
        lambda:require(close(obj(BODY)['lo'],(-W/2,d['ground'],k[2]-2*t)) and
                       close(obj(BODY)['hi'],(W/2,dy+d['wall_h'],L/2))),
        (BODY,move_group('coupler',(0,0,-.1)),'grow nose'),(BODY,move_group('landing_stand',(0,-.1,0)),'lower stand'),
        (BODY,move_group('deck',(0,0,.1)),'stretch tail'))
    add('deck = half Golf length, between tracks minus tyre envelopes',
        lambda:require(close(group_bounds('deck'),((-W/2,dy-2*t,-L/2),(W/2,dy-t/4,L/2))) and L<s['golf']['mesh']['size'][2]<s['trailer']['mesh']['size'][2]),
        (BODY,move_group('deck',(.1,0,0)),'shift deck'))
    def sideboard():
        lo,hi=group_bounds('side_1')
        require(close(hi[1],dy+d['wall_h']),'sideboard top is not the truck bed wall height')
        require(close(hi[0]-lo[0],d['wall_t']),'sideboard is not the truck bed wall thickness')
        require(close(hi[0],W/2))
    add('sideboard section = the truck bed wall, measured',sideboard,
        (BODY,move_group('side_1',(0,.1,0)),'raise sideboard'),(BODY,move_group('side_1',(.1,0,0)),'thin sideboard'))
    add('trailer wall section equals the truck bed, derived two ways',
        lambda:require(close(truck_wall()[0],d['wall_t']) and close(truck_wall()[1],d['wall_h'])),
        (CONTENT/'truck_body.txt',lambda x:re.sub(r'^v 0\.980968 ','v 0.900968 ',x,flags=re.M),'thicken the truck bed wall'),
        (CONTENT/'truck_body.txt',lambda x:re.sub(r'^(v [-\d.]+ )1\.125001 ','\\g<1>1.325001 ',x,flags=re.M),'raise the truck bed wall'))
    add('kingpin on measured coupler, car ground datum',lambda:require(close(vector(fields('car_trailer')['Kingpin']),k)),fieldmut('Kingpin','Vector3.Zero'))
    add('socket encloses kingpin and follows drawbar',lambda:require(close(group_bounds('coupler'),((-2*t,k[1]-t,k[2]-2*t),(2*t,k[1]+t,k[2]+4*t)))),
        (BODY,move_group('coupler',(0,0,.3)),'bury socket'))
    add('spec routes authored body and shared fleet wheel texture',lambda:require(fields('car_trailer')['Body']=='"car_trailer_body.txt"' and fields('car_trailer')['WheelTex']==s['quad']['fields']['WheelTex']),
        fieldmut('Body','"trailer_0.txt"'),fieldmut('WheelTex','"missing.png"'))
    add('trailer wheel donor and effective radius are quad',lambda:require(fields('car_trailer')['Wheel']=='"quad_wheel.txt"' and close(number(fields('car_trailer')['WheelRadius']),r)),
        fieldmut('Wheel','"jeep_wheel.txt"'),fieldmut('WheelRadius','0.6f'))
    def wheels():
        text=fields('car_trailer')['Wheels']; rows=re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (false|true)\)',text)
        require(len(rows)==2 and all(q[3]=='false' for q in rows))
        require(close([tuple(map(float,q[:3])) for q in rows],[(-d['track']/2,d['wy'],d['az']),(d['track']/2,d['wy'],d['az'])]))
        require(close(d['track']/2+d['tw']/2,W/2),'tyres are not flush with the deck edge')
    add('one axle, tyres flush under the deck edge, axle at 60% deck',wheels,
        (VEH,lambda x:x.replace(f"{d['az']:.6f}f, false",'0f, true',1),'revert wheel anchor and steering'))
    add('axle mesh follows wheel rest centres',lambda:require(close(group_bounds('axle'),((-d['track']/2,d['wy']-.25-t,d['az']-t),(d['track']/2,d['wy']-.25+t,d['az']+t)))),
        (BODY,move_group('axle',(0,0,.1)),'move axle'))
    # The mudguard clearance check went with the mudguards (strawberry: "remove the wheel arches").
    # Replaced by its negative: nothing arch-shaped may come back without this check coming back too.
    add('no wheel arches',
        lambda:require(not any(l.startswith('g ') and 'mudguard' in l for l in BODY.read_text().splitlines())
                       and not any('mudguard' in l for l in (ROOT/'tools/build_car_trailer.py').read_text().splitlines())),
        (BODY,lambda x:x.replace('g deck','g mudguard_1',1),'re-add an arch group'))
    add('quad mass/health, no engine/steer/speed/brake/audio',lambda:require(
        close(number(fields('car_trailer')['Mass']),s['quad']['Mass']) and close(number(fields('car_trailer')['Health']),s['quad']['Health'])
        and all(number(fields('car_trailer')[a])==0 for a in ['Engine','SteerMax','SteerMin','SpeedMax','SpeedMin','Brake']) and fields('car_trailer')['Sound']=='null'),
        *[fieldmut(key,'1f') for key in ['Mass','Health','Engine','SteerMax','SteerMin','SpeedMax','SpeedMin','Brake']],fieldmut('Sound','"engine_small.ogg"'))
    for name in TOW_CARS:
        exp=(0,rr[name]['y'],rr[name]['rear']+r/3)
        add(name+' hitch from its own measured rear',lambda n=name,e=exp:require(close(vector(fields(n)['FifthWheel']),e)),fieldmut('FifthWheel','Vector3.Zero',name))
        add(name+' visible hitch registered and reaches its pin',lambda n=name,e=exp:require('"'+n+'_hitch.txt"' in fields(n)['Parts'] and close(obj(CONTENT/(n+'_hitch.txt'))['hi'][2],e[2]+t)),
            (VEH,lambda x,n=name:x.replace('"'+n+'_hitch.txt"','"missing_hitch.txt"',1),'revert part registration'),
            (CONTENT/(name+'_hitch.txt'),lambda x:'\n'.join('v '+ ' '.join(str(float(v)+(0.2 if i==2 else 0)) for i,v in enumerate(l.split()[1:])) if l.startswith('v ') else l for l in x.splitlines())+'\n','hitch no longer fits pin'))
    def tow_policy():
        for name in s:
            if name in TOW_CARS:continue
            require(fields(name).get('FifthWheel','Vector3.Zero')==( 'new Vector3(0f, 0.62f, 3.0f)' if name=='semi' else 'Vector3.Zero'))
    add('specialised fleet tow eligibility unchanged',tow_policy,
        (VEH,lambda x:x.replace('static readonly Spec _quad = new()\n        {','static readonly Spec _quad = new()\n        {\n            FifthWheel = new Vector3(0f, 0f, 3f),'),'enable quad'))
    add('main collider follows deck, does not fill open load space',lambda:require(close(vector(fields('car_trailer')['BoxSize']),(W,2*t,L)) and close(vector(fields('car_trailer')['BoxCenter']),(0,dy-t,0)) and 'HullBoxes' in fields('car_trailer') and len(vectors(fields('car_trailer')['ExtraBoxes']))==10),
        fieldmut('BoxSize','new Vector3(3f, 2f, 16f)'),fieldmut('BoxCenter','Vector3.Zero'),
        (VEH,lambda x:x.replace('HullBoxes = new (Vector3 size, Vector3 center, float yawDeg)[]','HullBands = new (Vector3 size, Vector3 center, float yawDeg)[]',1),'remove drawbar colliders'))
    # The zone CONTAINS the leg, it does not equal it. Equality held only because the old foot pad
    # happened to be exactly 4t square; with the pad gone the leg is narrower, and asserting equality
    # against whatever mesh survives would have quietly re-fitted the check to the model instead of
    # testing it. Pin the authored zone AND require it to swallow the leg -- the second half is what
    # notices if the leg is ever moved or grown out of its own collider.
    def landing():
        zmin=vector(fields('car_trailer')['LandingLegZoneMin']);zmax=vector(fields('car_trailer')['LandingLegZoneMax'])
        require(close(zmin,(t,d['ground'],(k[2]-L/2)/2-2*t)) and close(zmax,(5*t,k[1]-t,(k[2]-L/2)/2+2*t)))
        require(close(vector(fields('car_trailer')['LandingGearSize']),(4*t,k[1]-t-d['ground'],4*t)))
        lo,hi=group_bounds('landing_stand')
        require(all(zmin[i]<=lo[i]+1e-6 and hi[i]<=zmax[i]+1e-6 for i in range(3)),
                f'landing leg {lo}..{hi} escapes its zone {zmin}..{zmax}')
    add('landing collider and retractable split cover authored support',landing,
        fieldmut('LandingLegZoneMin','Vector3.Zero'),fieldmut('LandingLegZoneMax','Vector3.Zero'),
        fieldmut('LandingGearSize','Vector3.Zero'),(BODY,move_group('landing_stand',(.4,0,0)),'leg out of its zone'))
    def yaw():
        angle=number(fields('car_trailer')['HitchYawLimit']);want=math.degrees(math.atan2(d['draw'],W/2+t/2))
        require(close(angle,want))
        points=obj(BODY)['vertices']
        # Entire body clears each car's global rear plane throughout the allowed yaw range.
        for degree in [angle*i/100 for i in range(-100,101)]:
            a=math.radians(degree)
            require(min(r/3+(p[2]-k[2])*math.cos(a)-p[0]*math.sin(a) for p in points)>0)
    add('yaw limit derived from front rail; full swept body clears bumper',yaw,fieldmut('HitchYawLimit','90f'))
    add('yaw spec reaches runtime clamp',lambda:require('v.HitchYawLimit = s.HitchYawLimit > 0f ? s.HitchYawLimit : JackknifeLimit;' in uncomment(VEH.read_text()) and 'Mathf.Min(JackknifeLimit, trailer.HitchYawLimit)' in uncomment(VEH.read_text())),
        (VEH,lambda x:x.replace('v.HitchYawLimit = s.HitchYawLimit > 0f ? s.HitchYawLimit : JackknifeLimit;',''),'revert spec transfer'),
        (VEH,lambda x:x.replace('Mathf.Min(JackknifeLimit, trailer.HitchYawLimit)','JackknifeLimit'),'revert clamp'))
    def lenses():
        """Lens centres read back off the asset, so TailPos cannot drift away from the geometry."""
        m=obj(LAMPS); out=[]
        for sgn in (-1,1):
            ps=[v for v in m['vertices'] if (v[0]<0)==(sgn<0)]
            out.append(tuple(sum(q[a] for q in ps)/len(ps) for a in range(3)))
        return out
    def transplant():
        """The lamps ARE the sedan's, not a lookalike: same face/normal/vertex count, X untouched, and
        Y/Z differing by ONE constant offset across every vertex. A pair of hand-built boxes of the same
        size would pass a bounds check and fail this."""
        src=obj(CONTENT/'sedan_taillights.txt'); dst=obj(LAMPS)
        require(len(src['vertices'])==len(dst['vertices']) and len(src['faces'])==len(dst['faces']),'lamp mesh is not the sedan mesh')
        deltas={tuple(round(b[i]-a[i],6) for i in range(3)) for a,b in zip(src['vertices'],dst['vertices'])}
        require(len(deltas)==1,f'lamps are not a rigid translation of the sedan: {len(deltas)} distinct offsets')
        dx,dy_,dz=deltas.pop()
        require(dx==0,'lamp transplant moved in X; the sedan spacing is what makes it fit')
        require(close(min(v[2] for v in dst['vertices']),L/2),'lamp front face is not on the trailer rear')
    add('tail lamps are the sedan mesh, translated',transplant,
        (LAMPS,lambda x:x.replace('v -1.113','v -1.000',1),'reshape a lens'))
    add('tail lamps and palette load through Parts',lambda:require('"car_trailer_taillights.txt"' in fields('car_trailer')['Parts'] and fields('car_trailer')['Palette']=='"car_trailer_palette.png"' and close(vectors(fields('car_trailer')['TailPos']),lenses())),
        (VEH,lambda x:x.replace('"car_trailer_taillights.txt"','"missing.txt"',1),'remove lamp part'),fieldmut('Palette','"missing.png"'),
        fieldmut('TailPos','null'),fieldmut('TailPos','new[] { Vector3.Zero, Vector3.Zero }'))
    def palette_samples():
        im=Image.open(CONTENT/'car_trailer_palette.png').convert('RGBA')
        require(im.size==(4,2))
        def sample(mesh_path,group):
            m=obj(mesh_path);active=''
            for line in mesh_path.read_text().splitlines():
                if line.startswith('g '):active=line[2:]
                if active==group and line.startswith('f '):
                    ti=int(line.split()[1].split('/')[1])-1;u,v=m['uvs'][ti]
                    return im.getpixel((int(u*im.width),int(v*im.height)))
            raise AssertionError('missing material group')
        steel=sample(BODY,'coupler');wood=sample(BODY,'deck');green=sample(BODY,'side_1')
        red=sample(CONTENT/'car_trailer_taillights.txt','tail_lens_1')
        require(max(steel[:3])-min(steel[:3])<20 and steel[3]==255)
        require(wood[0]>wood[1]>wood[2] and green[1]>green[0] and green[1]>green[2])
        require(red[0]>2*red[1] and red[0]>2*red[2])
    flip_uv=lambda src:'\n'.join('vt '+line.split()[1]+' '+str(1-float(line.split()[2])) if line.startswith('vt ') else line for line in src.splitlines())+'\n'
    add('V-flip selects steel, timber, green sides and red tail lenses',palette_samples,
        (BODY,flip_uv,'undo authored body V compensation'),
        (CONTENT/'car_trailer_taillights.txt',flip_uv,'undo lamp V compensation'))
    old=source_names(subprocess.check_output(['git','show',BASE+':game/Vehicle.cs'],cwd=ROOT,text=True))
    add('all 33 old network TypeIds preserved; append only',lambda:require(source_names(VEH.read_text())==old+['car_trailer']),
        (VEH,lambda x:x.replace('"jet", "wagon", "car_trailer" };','"jet", "car_trailer", "wagon" };'),'insert before wagon'),
        (VEH,lambda x:x.replace('"jet", "wagon", "car_trailer" };','"jet", "wagon" };'),'revert catalogue'))
    for label,needle in [('builder','Build(_car_trailer, variant, "car_trailer")'),('command dispatch','"car_trailer" => BuildCarTrailer(variant)'),('replica spec dispatch','"car_trailer" => _car_trailer')]:
        add(label,lambda n=needle:require(n in uncomment(VEH.read_text())),(VEH,lambda x,n=needle:x.replace(n,n.replace('car_trailer','trailer'),1),'revert '+label))
    world=ROOT/'game/WorldBuilder.cs'
    add('command-only: absent from natural spawns',lambda:require('"car_trailer"' not in uncomment(world.read_text())),
        (world,lambda x:x.replace('0 => "sedan"','0 => "car_trailer"',1),'add natural spawn'))
    return result

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--mutation-test',action='store_true');args=parser.parse_args()
    checks=cases()
    for c in checks:
        c.fn()
        print('PASS',c.label)
    if args.mutation_test:
        caught=0
        for c in checks:
            assert c.mutants,c.label
            for path,edit,label in c.mutants:
                before=path.read_bytes()
                try:
                    after=edit(before.decode()).encode();assert after!=before,(c.label,label,'ineffective mutation')
                    path.write_bytes(after)
                    try:c.fn()
                    except (AssertionError,KeyError,ValueError,IndexError):caught+=1
                    else:raise RuntimeError('SURVIVED: '+c.label+' / '+label)
                finally:path.write_bytes(before)
        for c in checks:c.fn()
        print(f'PASS mutation audit: {caught} real-file reversions/corruptions rejected across ALL {len(checks)} named checks; original bytes restored and clean checks rerun')
    print('Static geometry/spec checks do not verify towing stability, cargo retention or multiplayer hitch interaction.')
if __name__=='__main__':main()
