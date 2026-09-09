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
RESIZE_BASE = '5297da9d'

def fields(name,src=None):
    s=uncomment(VEH.read_text() if src is None else src);start=s.index('static readonly Spec _'+name+' = new()')
    return dict((k.strip(),v.strip()) for k,v in (x.split('=',1) for x in split_top(braced(s,s.index('{',start)))))

def change_field(name,key,value):
    def edit(s):
        start=s.index('static readonly Spec _'+name+' = new()');end=s.index('\n        };',start)
        block=s[start:end]
        # Replace the entire initializer, including multiline arrays. Stopping at the first comma
        # inside Wheels/TailPos left broken C# behind and could reject syntax instead of the regression.
        match=re.search(r'\b'+re.escape(key)+r'\s*=\s*',block)
        assert match,(name,key)
        before=split_top(block[match.end():])[0]
        changed=block[:match.end()]+value+block[match.end()+len(before):]
        return s[:start]+changed+s[end:]
    return edit

def group_points(name):
    m=obj(BODY);ps=[];group=''
    for line in BODY.read_text().splitlines():
        if line.startswith('g '):group=line[2:]
        if group==name and line.startswith('f '):ps.extend(m['vertices'][int(c.split('/')[0])-1] for c in line.split()[1:4])
    assert ps,('missing component',name)
    return ps

def group_bounds(name):
    ps=group_points(name)
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
    s,rr=measurements();g=s['golf']
    radius=g['WheelRadius']                    # the CAR's wheel, not the quad's
    wheel=g['fields']['Wheel'];tw=obj(CONTENT/wheel.strip('"'))['size'][0]
    wall_t,wall_h,bed_w=truck_wall()
    t=wall_t/5
    track=g['tracks'][-1]+wall_t/2             # half a truck wall section wider than the Golf track
    L=g['mesh']['size'][2]*.75                 # three quarters of the measured Golf body length
    # The TYRES set the width, not the truck. Without arches, any flat-sided box wider than the tyre's
    # inner face (track/2 - tw/2) has the tyre buried in its sideboard; the bed's own 2.462 did exactly
    # that. The truck still governs the wall SECTION, which is what was actually asked for.
    W=track-tw-2*t
    draw=g['mesh']['size'][0]/2+radius
    return s,rr,dict(r=radius,t=t,L=L,W=W,draw=draw,king=(0,rr['golf']['y'],-L/2-draw),
                    ground=rr['golf']['ground'],wy=rr['golf']['ground']+radius+.25,az=L/10,tw=tw,
                    # deck sits so the wheel rest centre is the fleet's .2734 above the body's underside
                    dy=wall_t-((g['Wheels'][0][1]-.25)-obj(CONTENT/g['fields']['Body'].strip('"'))['lo'][1]),
                    ride=(g['Wheels'][0][1]-.25)-obj(CONTENT/g['fields']['Body'].strip('"'))['lo'][1],
                    # the sedan's own lamp inset from its body side -- re-measured here, not imported
                    lamp_inset=obj(CONTENT/'sedan_body.txt')['size'][0]/2-obj(CONTENT/'sedan_taillights.txt')['hi'][0],
                    wall_t=wall_t,wall_h=wall_h,track=track,wheel=wheel,bed_w=bed_w)

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
    def boxmut(key,index,part,value,label):
        """Replace one whole tuple member in a real spec array, keeping valid C# syntax."""
        before=fields('car_trailer')[key]
        rows=split_top(braced(before,before.index('{')))
        members=split_top(rows[index][1:-1]);members[part]=value
        after=before.replace(rows[index],'('+', '.join(members)+')',1)
        return VEH,change_field('car_trailer',key,after),label
    def boxes(key,spec=None):
        raw=(fields('car_trailer') if spec is None else spec)[key]
        return [split_top(row[1:-1]) for row in split_top(braced(raw,raw.index('{')))]
    previous=fields('car_trailer',subprocess.check_output(['git','show',RESIZE_BASE+':game/Vehicle.cs'],cwd=ROOT,text=True))
    old_hulls,old_walls=boxes('HullBoxes',previous),boxes('ExtraBoxes',previous)
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
    # X extent is the AXLE, spanning the widened track to reach wheels that sit proud of the deck the way
    # every car's do. Top is the sideboard, wall_h above the deck; front is the coupler, rear the
    # tailgate -- the deck no longer overhangs it.
    add('body AABB from deck, wall height, stand and tongue',
        lambda:require(close(obj(BODY)['lo'],(-d['track']/2,d['ground'],k[2]-2*t)) and
                       close(obj(BODY)['hi'],(d['track']/2,dy+d['wall_h'],L/2))),
        (BODY,move_group('coupler',(0,0,-.1)),'grow nose'),(BODY,move_group('landing_stand',(0,-.1,0)),'lower stand'),
        (BODY,move_group('tailgate',(0,0,.1)),'stretch tail'))   # the TAILGATE owns the rear datum now, not the deck
    wt_=d['wall_t']
    add('deck is one wall section thick, inset inside the walls',
        lambda:require(close(group_bounds('deck'),((-W/2+wt_/2,dy-wt_,-L/2+wt_/2),(W/2-wt_/2,dy,L/2-wt_/2)))
                       and L<s['golf']['mesh']['size'][2]<s['trailer']['mesh']['size'][2]),
        (BODY,move_group('deck',(.1,0,0)),'shift deck'),(BODY,move_group('deck',(0,.1,0)),'raise deck'))
    def sideboard():
        lo,hi=group_bounds('side_1')
        require(close(hi[1],dy+d['wall_h']),'sideboard top is not the truck bed wall height')
        require(close(hi[0]-lo[0],d['wall_t']),'sideboard is not the truck bed wall thickness')
        require(close(hi[0],W/2))
    def ride():
        """Off the MESH and the SPEC, not off expected()'s own arithmetic -- the first version of this
        compared two numbers I had derived, so no file mutation could fail it and the audit caught it
        surviving. Fleet constant: golf, sedan, hatchback, jeep, truck and van all rest their wheel
        centre .2734 above the body's lowest point. The trailer's sat at .0000, wheel centre exactly
        level with the deck underside, which is what made it read as a box on stilts."""
        anchor=float(re.search(r'\(-?[\d.]+f, ([-\d.]+)f,',fields('car_trailer')['Wheels']).group(1))
        require(close(anchor-.25-group_bounds('deck')[0][1], d['ride']),
                f'wheel rest {anchor-.25:.4f} sits {anchor-.25-group_bounds("deck")[0][1]:.4f} above the deck underside, fleet is {d["ride"]:.4f}')
    add('wheels ride the fleet relationship: rest centre .2734 above the body underside',ride,
        (BODY,move_group('deck',(0,.3,0)),'lift the body off its wheels'),
        fieldmut('Wheels','new (float, float, float, bool)[] { (-1.300000f, 0.900000f, 0.313712f, false), (1.300000f, 0.900000f, 0.313712f, false) }'))
    add('sideboard section = the truck bed wall, measured',sideboard,
        (BODY,move_group('side_1',(0,.1,0)),'raise sideboard'),(BODY,move_group('side_1',(.1,0,0)),'thin sideboard'))
    def donor_wall():
        thickness,height,_=truck_wall()
        floor=group_bounds('deck')[1][1]
        for group in ('side_-1','side_1'):
            low,high=group_bounds(group)
            require(close(high[0]-low[0],thickness) and close(high[1]-floor,height),
                    group+' section differs from the measured truck bed')
    add('trailer wall section equals the truck bed, derived two ways',donor_wall,
        (CONTENT/'truck_body.txt',lambda x:re.sub(r'^v 0\.980968 ','v 0.900968 ',x,flags=re.M),'thicken the truck bed wall'),
        (CONTENT/'truck_body.txt',lambda x:re.sub(r'^(v [-\d.]+ )1\.125001 ','\\g<1>1.325001 ',x,flags=re.M),'raise the truck bed wall'),
        (BODY,move_group('side_-1',(0,.1,0)),'raise trailer wall away from donor height'))
    add('kingpin on measured coupler, car ground datum',lambda:require(close(vector(fields('car_trailer')['Kingpin']),k)),fieldmut('Kingpin','Vector3.Zero'))
    add('socket encloses kingpin and follows drawbar',lambda:require(close(group_bounds('coupler'),((-2*t,k[1]-t,k[2]-2*t),(2*t,k[1]+t,k[2]+4*t)))),
        (BODY,move_group('coupler',(0,0,.3)),'bury socket'))
    add('spec routes authored body and shared fleet wheel texture',lambda:require(fields('car_trailer')['Body']=='"car_trailer_body.txt"' and fields('car_trailer')['WheelTex']==s['quad']['fields']['WheelTex']),
        fieldmut('Body','"trailer_0.txt"'),fieldmut('WheelTex','"missing.png"'))
    add('wheel donor and radius are the car\'s, not the quad\'s',
        lambda:require(fields('car_trailer')['Wheel']==d['wheel'] and close(number(fields('car_trailer')['WheelRadius']),r)
                       and r==s['golf']['WheelRadius'] and r>s['quad']['WheelRadius']),
        fieldmut('Wheel','"quad_wheel.txt"'),fieldmut('WheelRadius','0.45f'))
    def tyre_clear():
        """No tyre buried in the bodywork -- read off the ACTUAL mesh and the ACTUAL spec, not off
        expected()'s own arithmetic, which cannot disagree with itself. Widening the track must preserve
        the same t gap; restoring the Golf track under the wider box must fail this check too."""
        anchor=min(abs(float(q[0])) for q in re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (?:false|true)\)',fields('car_trailer')['Wheels']))
        inner=anchor-obj(CONTENT/fields('car_trailer')['Wheel'].strip('"'))['size'][0]/2
        outer=max(abs(group_bounds('side_'+str(sg))[i][0]) for sg in (-1,1) for i in (0,1))
        require(inner-outer >= t-1e-6, f'tyre inner face {inner:.4f} vs sideboard outer {outer:.4f}: gap {inner-outer:+.4f}')
    add('no tyre buried in the sideboard, which is why the box is not the bed width',tyre_clear,
        (BODY,move_group('side_1',(.1,0,0)),'widen the box into the tyre'),
        (BODY,move_group('side_-1',(-.1,0,0)),'widen the other side into the tyre'),
        fieldmut('Wheels','new (float, float, float, bool)[] { (-1.000000f, 0.250000f, 0.313712f, false), (1.000000f, 0.250000f, 0.313712f, false) }'),
        (VEH,lambda x:x.replace(f"{d['track']/2:.6f}f, {d['wy']:.6f}f, {d['az']:.6f}f, false",
                               f"{s['golf']['tracks'][-1]/2:.6f}f, {d['wy']:.6f}f, {d['az']:.6f}f, false"),
         'restore Golf track under the wider box'))
    def wheels():
        text=fields('car_trailer')['Wheels']; rows=re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (false|true)\)',text)
        require(len(rows)==2 and all(q[3]=='false' for q in rows))
        require(close([tuple(map(float,q[:3])) for q in rows],[(-d['track']/2,d['wy'],d['az']),(d['track']/2,d['wy'],d['az'])]))
        actual_track=float(rows[1][0])-float(rows[0][0])
        actual_width=group_bounds('side_1')[1][0]-group_bounds('side_-1')[0][0]
        proud=(actual_track+d['tw']-actual_width)/2
        require(proud>0,'tyres are tucked inside the deck; every car in the fleet carries them proud')
        require(close(actual_track-s['golf']['tracks'][-1],wt_/2),'track increase is not half a truck wall section')
    add('one axle on the widened Golf track, tyres proud like a car, axle at 60% deck',wheels,
        (VEH,lambda x:x.replace(f"{d['az']:.6f}f, false",'0f, true',1),'revert wheel anchor and steering'),
        (VEH,lambda x:x.replace(f"{d['track']/2:.6f}f, {d['wy']:.6f}f, {d['az']:.6f}f, false",
                               f"{s['golf']['tracks'][-1]/2:.6f}f, {d['wy']:.6f}f, {d['az']:.6f}f, false"),
         'revert track increase'))
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
        exp=(0,rr[name]['y'],rr[name]['rear']+3*t)
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
    def main_collider():
        spec=fields('car_trailer');size=vector(spec['BoxSize']);centre=vector(spec['BoxCenter'])
        low=(group_bounds('side_-1')[0][0],group_bounds('deck')[0][1],group_bounds('headboard')[0][2])
        high=(group_bounds('side_1')[1][0],group_bounds('deck')[1][1],group_bounds('tailgate')[1][2])
        require(close(tuple(centre[i]-size[i]/2 for i in range(3)),low) and
                close(tuple(centre[i]+size[i]/2 for i in range(3)),high),'main collider differs from saved floor and outer walls')
        require('HullBoxes' in spec and len(boxes('ExtraBoxes'))==5)
    add('main collider follows deck, does not fill open load space',main_collider,
        fieldmut('BoxSize','new Vector3(3f, 2f, 16f)'),fieldmut('BoxCenter','Vector3.Zero'),
        (VEH,lambda x:x.replace('HullBoxes = new (Vector3 size, Vector3 center, float yawDeg)[]','HullBands = new (Vector3 size, Vector3 center, float yawDeg)[]',1),'remove drawbar colliders'),
        (BODY,move_group('deck',(0,.1,0)),'move floor outside main collider'))
    def drawbar_colliders():
        rows=boxes('HullBoxes');require(len(rows)==3)
        for row,group in zip(rows[:2],('drawbar_-1','drawbar_1')):
            size,centre=map(vector,row[:2]);angle=math.radians(number(row[2]))
            require(all(v>0 for v in size),'nonpositive drawbar collider size')
            # Inverse of the runtime Y rotation. Read every beam vertex, then require the collider
            # to be its tight enclosing box in that frame (including a descending beam's full height).
            local=[]
            for p in group_points(group):
                x,y,z=(p[i]-centre[i] for i in range(3))
                local.append((math.cos(angle)*x-math.sin(angle)*z,y,math.sin(angle)*x+math.cos(angle)*z))
            for axis in range(3):
                require(close(min(p[axis] for p in local),-size[axis]/2) and
                        close(max(p[axis] for p in local),size[axis]/2),group+' escapes or does not fit its collider')
        require(close(vector(rows[2][0]),vector(fields('car_trailer')['BoxSize'])) and
                close(vector(rows[2][1]),vector(fields('car_trailer')['BoxCenter'])) and number(rows[2][2])==0)
    add('drawbar colliders fit saved beams with positive sizes; hull deck matches main box',drawbar_colliders,
        boxmut('HullBoxes',0,0,old_hulls[0][0],'restore old negative drawbar size'),
        boxmut('HullBoxes',1,1,'Vector3.Zero','move drawbar collider'),
        boxmut('HullBoxes',0,2,'0f','remove drawbar yaw'),
        boxmut('HullBoxes',2,0,old_hulls[2][0],'restore old hull deck size'),
        (BODY,move_group('drawbar_1',(0,.1,0)),'move beam outside its collider'))
    def wall_colliders():
        rows=boxes('ExtraBoxes');require(len(rows)==5)
        floor=group_bounds('deck')[1][1]
        for row,group in zip(rows,('side_-1','side_1','headboard','tailgate','coupler')):
            size,centre=map(vector,row);require(all(v>0 for v in size),'nonpositive wall/socket collider size')
            low,high=(list(v) for v in group_bounds(group))
            if group!='coupler':low[1]=floor  # main slab covers the wall below the floor
            if group in ('headboard','tailgate'):
                low[0]=group_bounds('side_-1')[1][0];high[0]=group_bounds('side_1')[0][0]
            require(close(tuple(centre[i]-size[i]/2 for i in range(3)),low) and
                    close(tuple(centre[i]+size[i]/2 for i in range(3)),high),group+' collider does not follow mesh')
    add('wall and socket colliders follow saved mesh around the open cargo space',wall_colliders,
        boxmut('ExtraBoxes',0,0,old_walls[0][0],'restore old side collider length'),
        boxmut('ExtraBoxes',1,1,old_walls[1][1],'restore old side collider spacing'),
        boxmut('ExtraBoxes',3,1,old_walls[3][1],'restore old tailgate collider position'),
        boxmut('ExtraBoxes',4,1,'Vector3.Zero','move socket collider'),
        (BODY,move_group('headboard',(0,0,.1)),'move headboard outside its collider'))
    # The zone CONTAINS the leg, it does not equal it. Equality held only because the old foot pad
    # happened to be exactly 4t square; with the pad gone the leg is narrower, and asserting equality
    # against whatever mesh survives would have quietly re-fitted the check to the model instead of
    # testing it. Pin the authored zone AND require it to swallow the leg -- the second half is what
    # notices if the leg is ever moved or grown out of its own collider.
    def landing():
        zmin=vector(fields('car_trailer')['LandingLegZoneMin']);zmax=vector(fields('car_trailer')['LandingLegZoneMax'])
        require(close(zmin,(t,d['ground'],(k[2]-L/2)/2-2*t)) and close(zmax,(5*t,k[1]-t,(k[2]-L/2)/2+2*t)))
        require(close(vector(fields('car_trailer')['LandingGearSize']),(4*t,k[1]-t-d['ground'],4*t)))
        centre=vector(fields('car_trailer')['LandingGearCenter'])
        size=vector(fields('car_trailer')['LandingGearSize'])
        require(close(tuple(centre[i]-size[i]/2 for i in range(3)),zmin) and
                close(tuple(centre[i]+size[i]/2 for i in range(3)),zmax),'landing collider differs from its split zone')
        lo,hi=group_bounds('landing_stand')
        require(all(zmin[i]<=lo[i]+1e-6 and hi[i]<=zmax[i]+1e-6 for i in range(3)),
                f'landing leg {lo}..{hi} escapes its zone {zmin}..{zmax}')
    add('landing collider and retractable split cover authored support',landing,
        fieldmut('LandingLegZoneMin','Vector3.Zero'),fieldmut('LandingLegZoneMax','Vector3.Zero'),
        fieldmut('LandingGearSize','Vector3.Zero'),fieldmut('LandingGearCenter','Vector3.Zero'),
        (BODY,move_group('landing_stand',(.4,0,0)),'leg out of its zone'))
    def yaw():
        angle=number(fields('car_trailer')['HitchYawLimit']);want=math.degrees(math.atan2(d['draw'],W/2+t/2))
        require(close(angle,want))
        points=obj(BODY)['vertices']
        # Entire body clears each car's global rear plane throughout the allowed yaw range.
        for degree in [angle*i/100 for i in range(-100,101)]:
            a=math.radians(degree)
            require(min(3*t+(p[2]-k[2])*math.cos(a)-p[0]*math.sin(a) for p in points)>0)
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
        # RIGID PER LENS, and the OUTCOME is what gets pinned rather than the two shifts being exactly
        # mirrored -- the sedan's own pair is 23 um asymmetric (max |X| 1.113476 left, 1.113453 right),
        # so solving each side to the same inset gives shifts that are equal and opposite only to that
        # tolerance. On a 2.100 deck the sedan's +/-1.1135 hung 63.5 mm out past the sides, so the
        # spacing had to move; the lens SHAPE does not.
        sides={}
        for a,b in zip(src['vertices'],dst['vertices']):
            sides.setdefault(a[0]>0,set()).add(tuple(round(b[i]-a[i],6) for i in range(3)))
        require(set(sides)=={True,False},'lamps are not two lenses')
        require(all(len(v)==1 for v in sides.values()),'a lens is not rigid: its vertices moved by different offsets')
        (rx,ry,rz),(lx,ly,lz)=sides[True].pop(),sides[False].pop()
        require(rx<0<lx,'lenses did not move toward each other')
        require(close(ry,ly) and close(rz,lz),'the two lenses moved differently in Y or Z')
        want=W/2-d['lamp_inset']
        require(close(max(v[0] for v in dst['vertices']),want) and close(min(v[0] for v in dst['vertices']),-want),
                f'lenses are not inset {d["lamp_inset"]:.4f} from each trailer side the way the sedan insets its own')
        dy_,dz=ry,rz
        body=obj(CONTENT/'sedan_body.txt'); rear=src['hi'][2]
        proud=rear-max(v[2] for v in body['vertices'] if 1.5 < v[2] <= rear+1e-6)
        require(close(max(v[2] for v in dst['vertices']),L/2+proud),
                f'lens should stand {proud:.4f} m proud of the tailgate, the way it does on the sedan')
    add('tail lamps are the sedan mesh, translated',transplant,
        (LAMPS,lambda x:re.sub(r'^v (-?[\d.]+)', lambda m:'v '+('%.9f'%(float(m.group(1))*0.9)), x, count=1, flags=re.M),'reshape a lens'),
        (LAMPS,lambda x:re.sub(r'^v (-?[\d.]+)', lambda m:'v '+('%.9f'%(float(m.group(1))+0.05)), x, count=1, flags=re.M),'shift one vertex of a lens'))
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
                    except (AssertionError,KeyError,ValueError,IndexError) as exc:
                        caught+=1
                        print('REJECT',c.label,'/',label,':',exc)
                    else:raise RuntimeError('SURVIVED: '+c.label+' / '+label)
                finally:path.write_bytes(before)
        for c in checks:c.fn()
        print(f'PASS mutation audit: {caught} real-file reversions/corruptions rejected across ALL {len(checks)} named checks; original bytes restored and clean checks rerun')
    print('Static geometry/spec checks do not verify towing stability, cargo retention or multiplayer hitch interaction.')
if __name__=='__main__':main()
