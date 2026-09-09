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
from measure_car_trailer import measurements, TOW_CARS, CLASSES
from verify_wagon import validate, f32

VEH = ROOT/'game/Vehicle.cs'
# THE SUITE RUNS ONCE PER SIZE CLASS. Everything below reads KEY/BODY/LAMPS rather than a literal, and
# main() rebinds them for dinky, small and medium in turn -- so a check written for one size is a check
# on all three, and a size that quietly stopped being regenerated fails instead of going unexamined.
KEY = 'dinky_trailer'
BODY = CONTENT/(KEY+'_body.txt')
LAMPS = CONTENT/(KEY+'_taillights.txt')

def select(cls):
    global KEY,BODY,LAMPS
    KEY = cls+'_trailer'; BODY = CONTENT/(KEY+'_body.txt'); LAMPS = CONTENT/(KEY+'_taillights.txt')
BASE = '1f7f106d'
RESIZE_BASE = '5297da9d'




def _steer_first(src,name):
    """Make the first wheel row of one spec a steered wheel at Z=0."""
    start=src.index('static readonly Spec _'+name+' = new()'); end=src.index('};',start)
    body=re.sub(r'\(-([\d.]+)f, ([\d.]+)f, (-?[\d.]+)f, false\)', r'(-\1f, \2f, 0f, true)', src[start:end], count=1)
    return src[:start]+body+src[end:]


def _retrack(src,name,half,new_half):
    """Move every wheel row of one spec onto a different track."""
    start=src.index('static readonly Spec _'+name+' = new()'); end=src.index('};',start)
    body=src[start:end].replace(f'-{half:.6f}f,',f'-{new_half:.6f}f,').replace(f'({half:.6f}f,',f'({new_half:.6f}f,')
    return src[:start]+body+src[end:]


def _rename_in_spec(src,name,old_key,new_key):
    """Rename a field, but only inside one named spec block."""
    start=src.index('static readonly Spec _'+name+' = new()')
    end=src.index('};',start)
    return src[:start]+src[start:end].replace(old_key+' = new',new_key+' = new',1)+src[end:]


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

def close(a,b,eps=3e-6):
    if isinstance(a,(tuple,list)):
        return len(a)==len(b) and all(close(x,y,eps) for x,y in zip(a,b))
    return abs(a-b)<eps

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


def _shift_axles(src,name,dz):
    """Move every wheel row of one spec along Z."""
    start=src.index('static readonly Spec _'+name+' = new()'); end=src.index('};',start)
    body=re.sub(r'(\(-?[\d.]+f, [\d.]+f, )(-?[\d.]+)(f, (?:false|true)\))',
                lambda m:m.group(1)+f'{float(m.group(2))+dz:.6f}'+m.group(3), src[start:end])
    return src[:start]+body+src[end:]


def _axle_centre(g,wall_t,radius,t,cls):
    """Axle-set centre for a class: 60% of the deck, unless the class pins its REAR axle to another
    class's setback from the tailgate, in which case the set hangs forward of that."""
    C=CLASSES[cls]; L=g['mesh']['size'][2]*C['length']
    ref=C.get('rear_setback_from')
    if not ref: return L/10
    R=CLASSES[ref]; RL=g['mesh']['size'][2]*R['length']; spacing=2*radius+t
    rear_last=RL/10+(spacing/2 if R['axles']>1 else 0)
    setback=RL/2-rear_last
    last=L/2-setback
    zs=[last-spacing*i for i in range(C['axles']-1,-1,-1)]
    return sum(zs)/len(zs)


def expected(cls='dinky'):
    s,rr=measurements();g=s['golf']
    radius=g['WheelRadius']                    # the CAR's wheel, not the quad's
    wheel=g['fields']['Wheel'];tw=obj(CONTENT/wheel.strip('"'))['size'][0]
    wall_t,wall_h,bed_w=truck_wall()
    # An ENCLOSED class runs its walls up to a roof taken from a fleet body instead of the truck bed's
    # own 1.000. Re-measured here off that body's mesh, not imported from the generator.
    roof_top=obj(CONTENT/s[CLASSES[cls]['roof_ref']]['fields']['Body'].strip('"'))['hi'][1] if CLASSES[cls].get('roof_ref') else None
    t=wall_t/5
    # The class table is the SPECIFICATION -- a length fraction and a count of half-wall-sections --
    # so importing it is importing the intent, not the answer. Everything it is applied to (the Golf's
    # length and track, the truck's wall section) is still re-measured here by a different route.
    C=CLASSES[cls]
    L=g['mesh']['size'][2]*C['length']
    track=g['tracks'][-1]+wall_t/2*C['width_steps']
    # The TYRES set the width, not the truck. Without arches, any flat-sided box wider than the tyre's
    # inner face (track/2 - tw/2) has the tyre buried in its sideboard; the bed's own 2.462 did exactly
    # that. The truck still governs the wall SECTION, which is what was actually asked for.
    # Uncapped for the wide classes -- the box stops being bounded by the tyre -- but the wheels still
    # mount on the SIDES in every class, so the track follows the box outward either way.
    W=track if C['wide'] else track-tw-2*t
    track=W+tw
    draw=g['mesh']['size'][0]/2+radius
    return s,rr,dict(r=radius,t=t,L=L,W=W,draw=draw,king=(0,rr['golf']['y'],-L/2-draw),
                    ground=rr['golf']['ground'],wy=rr['golf']['ground']+radius+.25,tw=tw,
                    az=_axle_centre(g,wall_t,radius,t,cls),
                    # deck sits so the wheel rest centre is the fleet's .2734 above the body's underside
                    dy=wall_t-((g['Wheels'][0][1]-.25)-obj(CONTENT/g['fields']['Body'].strip('"'))['lo'][1]),
                    ride=(g['Wheels'][0][1]-.25)-obj(CONTENT/g['fields']['Body'].strip('"'))['lo'][1],
                    # the sedan's own lamp inset from its body side -- re-measured here, not imported
                    lamp_inset=obj(CONTENT/'sedan_body.txt')['size'][0]/2-obj(CONTENT/'sedan_taillights.txt')['hi'][0],
                    axles=C['axles'], display=C['display'], cls=cls, wide=C['wide'], roof_top=roof_top,
                    wall_t=wall_t,bed_wall_h=wall_h,track=track,wheel=wheel,bed_w=bed_w,
                    wall_h=(roof_top-wall_t-(wall_t-((g['Wheels'][0][1]-.25)-obj(CONTENT/g['fields']['Body'].strip('"'))['lo'][1])))
                           if roof_top is not None else wall_h)

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

def cases(cls='dinky'):
    select(cls)
    s,rr,d=expected(cls);t=d['t'];L=d['L'];W=d['W'];r=d['r'];dy=d['dy'];k=d['king']
    result=[]
    def add(label,fn,*mutants):result.append(Case(label,fn,list(mutants)))
    def fieldmut(key,val,name=None):name=name or KEY;return VEH,change_field(name,key,val),name+'.'+key
    def shove(key,index,part,label,delta=(.1,0,0)):
        """Perturb one Vector3 member of a real spec array, keeping valid C# syntax.

        This used to substitute the value the same field held at RESIZE_BASE. That is a mutation only
        while the two differ -- and once the dinky class was re-derived at its ORIGINAL size, several
        of its colliders came back to exactly the historical numbers, so 'restore the old value'
        edited nothing and the audit reported an ineffective mutation. Perturbing what is there cannot
        coincide with it."""
        before=fields(KEY)[key]
        rows=split_top(braced(before,before.index('{')))
        members=split_top(rows[index][1:-1])
        nums=[float(x.rstrip('f')) for x in re.findall(r'(-?[\d.]+)f',members[part])]
        members[part]='new Vector3('+', '.join(f'{n+delta[i]:.6f}f' for i,n in enumerate(nums))+')'
        after=before.replace(rows[index],'('+', '.join(members)+')',1)
        return VEH,change_field(KEY,key,after),label

    def boxmut(key,index,part,value,label):
        """Replace one whole tuple member in a real spec array, keeping valid C# syntax."""
        before=fields(KEY)[key]
        rows=split_top(braced(before,before.index('{')))
        members=split_top(rows[index][1:-1]);members[part]=value
        after=before.replace(rows[index],'('+', '.join(members)+')',1)
        return VEH,change_field(KEY,key,after),label
    def boxes(key,spec=None):
        raw=(fields(KEY) if spec is None else spec)[key]
        return [split_top(row[1:-1]) for row in split_top(braced(raw,raw.index('{')))]
    assets=[BODY,LAMPS]+[CONTENT/(n+'_hitch.txt') for n in TOW_CARS]
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
    # X extent is the SIDEBOARD. It used to be the axle bar, spanning out to wheels that stood proud of
    # the box; both are gone. Top is the sideboard, wall_h above the deck; front is the coupler, rear the
    # tailgate -- the deck no longer overhangs it.
    add('body AABB from deck, wall height, stand and tongue',
        lambda:require(close(obj(BODY)['lo'],(-W/2,d['ground'],k[2]-2*t)) and
                       close(obj(BODY)['hi'],(W/2,dy+d['wall_h']+(d['wall_t']/2 if d['roof_top'] is not None else 0),L/2))),
        (BODY,move_group('coupler',(0,0,-.1)),'grow nose'),(BODY,move_group('landing_stand',(0,-.1,0)),'lower stand'),
        (BODY,move_group('tailgate',(0,0,.1)),'stretch tail'))   # the TAILGATE owns the rear datum now, not the deck
    wt_=d['wall_t']
    add('deck is one wall section thick, inset inside the walls',
        lambda:require(close(group_bounds('deck'),((-W/2+wt_/2,dy-wt_,-L/2+wt_/2),(W/2-wt_/2,dy,L/2-wt_/2)))
                       # Was "deck shorter than the Golf, Golf shorter than the semi". The first half
                       # stopped being true at the large class, which is 6.013 against the Golf's
                       # 5.229 -- a scale sanity clause that a bigger size outgrew. What still binds
                       # is the top of the range: no car trailer reaches the articulated semi's deck.
                       and L < s['trailer']['mesh']['size'][2]
                       and close(L, s['golf']['mesh']['size'][2]*CLASSES[cls]['length'], 1e-5)),
        (BODY,move_group('deck',(.1,0,0)),'shift deck'),(BODY,move_group('deck',(0,.1,0)),'raise deck'))
    def sideboard():
        lo,hi=group_bounds('side_1')
        require(close(hi[1],dy+d['wall_h']),
                'enclosed: sideboard top is not the roof underside' if d['roof_top'] is not None
                else 'sideboard top is not the truck bed wall height')
        require(close(hi[0]-lo[0],d['wall_t']),'sideboard is not the truck bed wall thickness')
        require(close(hi[0],W/2))
    def ride():
        """Off the MESH and the SPEC, not off expected()'s own arithmetic -- the first version of this
        compared two numbers I had derived, so no file mutation could fail it and the audit caught it
        surviving. Fleet constant: golf, sedan, hatchback, jeep, truck and van all rest their wheel
        centre .2734 above the body's lowest point. The trailer's sat at .0000, wheel centre exactly
        level with the deck underside, which is what made it read as a box on stilts."""
        anchor=float(re.search(r'\(-?[\d.]+f, ([-\d.]+)f,',fields(KEY)['Wheels']).group(1))
        require(close(anchor-.25-group_bounds('deck')[0][1], d['ride']),
                f'wheel rest {anchor-.25:.4f} sits {anchor-.25-group_bounds("deck")[0][1]:.4f} above the deck underside, fleet is {d["ride"]:.4f}')
    add('wheels ride the fleet relationship: rest centre .2734 above the body underside',ride,
        (BODY,move_group('deck',(0,.3,0)),'lift the body off its wheels'),
        fieldmut('Wheels','new (float, float, float, bool)[] { (-1.300000f, 0.900000f, 0.313712f, false), (1.300000f, 0.900000f, 0.313712f, false) }'))
    add('sideboard section = the truck bed wall, measured',sideboard,
        (BODY,move_group('side_1',(0,.1,0)),'raise sideboard'),(BODY,move_group('side_1',(.1,0,0)),'thin sideboard'))
    def donor_wall():
        # THICKNESS is the truck bed's in every class. HEIGHT is only the truck's for the OPEN ones:
        # an enclosed class runs its walls up to a roof taken from another fleet body instead, so
        # asserting the bed's 1.000 there would be asserting it is not a horsebox. What still binds
        # for an enclosed class is that the wall reaches the roof's underside.
        thickness,height,_=truck_wall()
        floor=group_bounds('deck')[1][1]
        for group in ('side_-1','side_1'):
            low,high=group_bounds(group)
            require(close(high[0]-low[0],thickness),group+' thickness differs from the measured truck bed')
            if d['roof_top'] is None:
                require(close(high[1]-floor,height),group+' does not reach the bed wall height')
            else:
                # The wall runs INTO the roof, not up to its underside: solids in this model overlap
                # rather than butt, because coincident corners get welded into a four-face edge.
                # The roof straddles the wall top by half a section each way, so the wall ends at the
                # roof's mid-height EXACTLY. A band check (anywhere inside the roof) was too loose:
                # a 100 mm wall move stayed inside it and the audit caught the mutation surviving.
                rl,rh=group_bounds('roof')
                require(close(high[1],(rl[1]+rh[1])/2,1e-5),
                        group+f' ends at {high[1]:.4f}, not the roof mid-height {(rl[1]+rh[1])/2:.4f}')
    add('trailer wall section equals the truck bed, derived two ways',donor_wall,
        (CONTENT/'truck_body.txt',lambda x:re.sub(r'^v 0\.980968 ','v 0.900968 ',x,flags=re.M),'thicken the truck bed wall'),
        # HEIGHT COMES FROM A DIFFERENT DONOR PER CLASS. Raising the truck bed's wall is a mutation for
        # an OPEN class only -- an enclosed one takes its height from its roof_ref body, and the audit
        # caught this surviving on the horsebox precisely because the truck no longer sets its walls.
        *([(CONTENT/'truck_body.txt',lambda x:re.sub(r'^(v [-\d.]+ )1\.125001 ','\\g<1>1.325001 ',x,flags=re.M),'raise the truck bed wall')]
          if d['roof_top'] is None else
          [(BODY,move_group('roof',(0,.3,0)),'lift the roof off its walls')]),
        (BODY,move_group('side_-1',(0,.1,0)),'raise trailer wall away from donor height'))
    add('kingpin on measured coupler, car ground datum',lambda:require(close(vector(fields(KEY)['Kingpin']),k)),fieldmut('Kingpin','Vector3.Zero'))
    add('socket encloses kingpin and follows drawbar',lambda:require(close(group_bounds('coupler'),((-2*t,k[1]-t,k[2]-2*t),(2*t,k[1]+t,k[2]+4*t)))),
        (BODY,move_group('coupler',(0,0,.3)),'bury socket'))
    add('spec routes authored body and shared fleet wheel texture',lambda:require(fields(KEY)['Body']=='"'+KEY+'_body.txt"' and fields(KEY)['WheelTex']==s['quad']['fields']['WheelTex']),
        fieldmut('Body','"trailer_0.txt"'),fieldmut('WheelTex','"missing.png"'))
    add('wheel donor and radius are the car\'s, not the quad\'s',
        lambda:require(fields(KEY)['Wheel']==d['wheel'] and close(number(fields(KEY)['WheelRadius']),r)
                       and r==s['golf']['WheelRadius'] and r>s['quad']['WheelRadius']),
        fieldmut('Wheel','"quad_wheel.txt"'),fieldmut('WheelRadius','0.45f'))
    def tyre_flush():
        """The tyre's OUTER face lands exactly on the sideboard's outer face (strawberry: "have the
        wheels flush with the trailer walls"). Read off the real mesh and the real spec, never off
        expected()'s own arithmetic, which cannot disagree with itself.

        FLUSH MEANS AGAINST, NOT INSIDE. Aligning the tyre's OUTER face with the wall instead puts the
        wheel through the cargo bay: the tyre tops out .620 above the deck floor, so its inner half
        rises into the box. The only arrangement with a wheel inboard of the wall needs the bed raised
        over it -- deck_y 1.100 against today's -.023, a lorry-height floor. This REPLACES an earlier
        rule that required a t gap here; that gap is what the axle bar spanned, and closing it is why
        the bar could go."""
        anchor=min(abs(float(q[0])) for q in re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (?:false|true)\)',fields(KEY)['Wheels']))
        inner_tyre=anchor-obj(CONTENT/fields(KEY)['Wheel'].strip('"'))['size'][0]/2
        outer_wall=max(abs(group_bounds('side_'+str(sg))[i][0]) for sg in (-1,1) for i in (0,1))
        require(close(inner_tyre,outer_wall,1e-5),
                f'tyre inner face {inner_tyre:.4f} does not meet the sideboard at {outer_wall:.4f}')
    # The wide classes' deck-over-the-wheels arrangement is gone: it was a flatbed, not what was asked
    # for. Every class mounts its wheels on the sides, so one flush rule covers all four again.
    add('tyres flush against the sideboards, tyre inner face on the wall',tyre_flush,
        (BODY,move_group('side_1',(.1,0,0)),'move the wall off the tyre'),
        (BODY,move_group('side_-1',(-.1,0,0)),'move the other wall off the tyre'),
        fieldmut('Wheels','new (float, float, float, bool)[] { (-1.300000f, 0.250000f, 0.313712f, false), (1.300000f, 0.250000f, 0.313712f, false) }'),
        # Widen the track back to the Golf's on EVERY wheel row of this class. Matching one row by its
        # literal Z stopped working the moment a class had two axles -- the medium's wheels are not at
        # az -- and an unmatched pattern is an ineffective mutation, not a passing check.
        (VEH,lambda x:_retrack(x,KEY,d['track']/2,d['track']/2+.3),'shove the track off the sideboard'))
    def wheels():
        """One row per wheel: every axle, both sides, all passive. The medium is the only tandem, and
        its two axles must sit at DISTINCT Z -- four wheels stacked on one Z passes a bare count."""
        text=fields(KEY)['Wheels']; rows=re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (false|true)\)',text)
        require(len(rows)==2*d['axles'],f'{len(rows)} wheels for {d["axles"]} axle(s)')
        require(all(q[3]=='false' for q in rows),'a trailer wheel is steered')
        zs=sorted({round(float(q[2]),6) for q in rows})
        require(len(zs)==d['axles'],f'{len(zs)} distinct axle Z for {d["axles"]} axle(s)')
        require(all(close(abs(float(q[0])),d['track']/2) and close(float(q[1]),d['wy']) for q in rows),
                'wheels are not on the track at the right height')
        if d['axles']==1: require(close(zs[0],d['az']),'single axle is not at 60% of the deck')
        else:
            require(close(sum(zs)/len(zs),d['az'],1e-6),'tandem is not centred on the 60% point')
            require(zs[-1]-zs[0] >= 2*d['r'],f'tandem tyres overlap in Z: {zs[-1]-zs[0]:.4f} < {2*d["r"]:.4f}')
        actual_track=float(rows[1][0])-float(rows[0][0])
        actual_width=group_bounds('side_1')[1][0]-group_bounds('side_-1')[0][0]
        require(close((actual_track-d['tw'])/2, actual_width/2, 1e-5),'tyre inner faces do not meet the sideboards')
        require(actual_track > actual_width,'wheels are not outboard of the box')
    if CLASSES[cls].get('rear_setback_from'):
        def setback():
            """This class's REAR axle sits the same distance in from its tailgate as the referenced
            class's does. Read off both real specs and both real meshes -- a cross-class invariant, so
            moving the reference's wheels and not this one's is a failure rather than a divergence."""
            ref=CLASSES[cls]['rear_setback_from']+'_trailer'
            def rear_of(k,body):
                zs=[float(q[2]) for q in re.findall(r'\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, (?:false|true)\)',fields(k)['Wheels'])]
                return obj(CONTENT/body)['hi'][2]-max(zs)
            mine=rear_of(KEY,KEY+'_body.txt'); theirs=rear_of(ref,ref+'_body.txt')
            require(close(mine,theirs,1e-4),f'rear axle sits {mine:.4f} from the tailgate, {ref} puts its own at {theirs:.4f}')
        add(f'rear axle setback matches the {CLASSES[cls]["rear_setback_from"]}',setback,
            (VEH,lambda x:_shift_axles(x,KEY,.4),'walk the axles forward'),
            (VEH,lambda x:_shift_axles(x,CLASSES[cls]['rear_setback_from']+'_trailer',.4),'move the reference instead'))
    add('axles: one row per wheel, passive, flush with the box, centred on 60% deck',wheels,
        # Same trap as the track mutation: the medium's wheels are not at az, so matching that literal
        # edited nothing. Steer the FIRST wheel row of this class's spec whatever its Z happens to be.
        (VEH,lambda x:_steer_first(x,KEY),'revert wheel anchor and steering'),
        # Widen the track rather than "revert" it to the Golf's: the WIDE classes already run the Golf
        # track, so reverting to it edited nothing and the audit reported an ineffective mutation.
        (VEH,lambda x:_retrack(x,KEY,d['track']/2,d['track']/2+.3),'shove the track outboard'))
    # The axle bar went with the outboard wheels -- it spanned out to them and was the thing that read
    # as inconsistent under the deck. Asserted ABSENT from both mesh and generator, same as the arches.
    add('no axle bar',
        lambda:require(not any(l.startswith('g ') and 'axle' in l for l in BODY.read_text().splitlines())
                       and "m.box('axle'" not in (ROOT/'tools/build_car_trailer.py').read_text()),
        (BODY,lambda x:x.replace('g deck','g axle',1),'re-add an axle group'))
    # The mudguard clearance check went with the mudguards (strawberry: "remove the wheel arches").
    # Replaced by its negative: nothing arch-shaped may come back without this check coming back too.
    add('no wheel arches',
        lambda:require(not any(l.startswith('g ') and 'mudguard' in l for l in BODY.read_text().splitlines())
                       and not any('mudguard' in l for l in (ROOT/'tools/build_car_trailer.py').read_text().splitlines())),
        (BODY,lambda x:x.replace('g deck','g mudguard_1',1),'re-add an arch group'))
    add('quad mass/health, no engine/steer/speed/brake/audio',lambda:require(
        close(number(fields(KEY)['Mass']),s['quad']['Mass']) and close(number(fields(KEY)['Health']),s['quad']['Health'])
        and all(number(fields(KEY)[a])==0 for a in ['Engine','SteerMax','SteerMin','SpeedMax','SpeedMin','Brake']) and fields(KEY)['Sound']=='null'),
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
        spec=fields(KEY);size=vector(spec['BoxSize']);centre=vector(spec['BoxCenter'])
        low=(group_bounds('side_-1')[0][0],group_bounds('deck')[0][1],group_bounds('headboard')[0][2])
        high=(group_bounds('side_1')[1][0],group_bounds('deck')[1][1],group_bounds('tailgate')[1][2])
        require(close(tuple(centre[i]-size[i]/2 for i in range(3)),low) and
                close(tuple(centre[i]+size[i]/2 for i in range(3)),high),'main collider differs from saved floor and outer walls')
        # 5 solid parts, 6 if the class has a roof. The point of the count is that the load space is
        # NOT filled -- one box per solid panel, never a hull that swallows the cargo volume.
        require('HullBoxes' in spec and len(boxes('ExtraBoxes'))==(6 if d['roof_top'] is not None else 5))
    add('main collider follows deck, does not fill open load space',main_collider,
        fieldmut('BoxSize','new Vector3(3f, 2f, 16f)'),fieldmut('BoxCenter','Vector3.Zero'),
        # SCOPED TO THIS CLASS'S SPEC. A bare replace(...,1) lands on the FIRST match in the file, which
        # is the dinky's block -- so once there were three trailer specs this mutation stopped touching
        # the class under test and the check survived it. The audit is what caught that.
        (VEH,lambda x:_rename_in_spec(x,KEY,'HullBoxes','HullBands'),'remove drawbar colliders'),
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
        require(close(vector(rows[2][0]),vector(fields(KEY)['BoxSize'])) and
                close(vector(rows[2][1]),vector(fields(KEY)['BoxCenter'])) and number(rows[2][2])==0)
    add('drawbar colliders fit saved beams with positive sizes; hull deck matches main box',drawbar_colliders,
        shove('HullBoxes',0,0,'grow the drawbar collider'),
        boxmut('HullBoxes',1,1,'Vector3.Zero','move drawbar collider'),
        boxmut('HullBoxes',0,2,'0f','remove drawbar yaw'),
        shove('HullBoxes',2,0,'stretch the hull deck box'),
        (BODY,move_group('drawbar_1',(0,.1,0)),'move beam outside its collider'))
    def wall_colliders():
        # An enclosed class carries one more: the roof is solid too. Order follows the generator.
        groups=['side_-1','side_1','headboard','tailgate']+(['roof'] if d['roof_top'] is not None else [])+['coupler']
        rows=boxes('ExtraBoxes');require(len(rows)==len(groups),f'{len(rows)} wall colliders for {len(groups)} solid parts')
        floor=group_bounds('deck')[1][1]
        for row,group in zip(rows,groups):
            size,centre=map(vector,row);require(all(v>0 for v in size),'nonpositive wall/socket collider size')
            low,high=(list(v) for v in group_bounds(group))
            if group not in ('coupler','roof'):low[1]=floor  # main slab covers the wall below the floor
            if group in ('headboard','tailgate'):
                low[0]=group_bounds('side_-1')[1][0];high[0]=group_bounds('side_1')[0][0]
            require(close(tuple(centre[i]-size[i]/2 for i in range(3)),low) and
                    close(tuple(centre[i]+size[i]/2 for i in range(3)),high),group+' collider does not follow mesh')
    add('wall and socket colliders follow saved mesh around the open cargo space',wall_colliders,
        shove('ExtraBoxes',0,0,'stretch the side collider'),
        shove('ExtraBoxes',1,1,'shift the side collider'),
        shove('ExtraBoxes',3,1,'shift the tailgate collider'),
        boxmut('ExtraBoxes',4,1,'Vector3.Zero','move socket collider'),
        (BODY,move_group('headboard',(0,0,.1)),'move headboard outside its collider'))
    # The zone CONTAINS the leg, it does not equal it. Equality held only because the old foot pad
    # happened to be exactly 4t square; with the pad gone the leg is narrower, and asserting equality
    # against whatever mesh survives would have quietly re-fitted the check to the model instead of
    # testing it. Pin the authored zone AND require it to swallow the leg -- the second half is what
    # notices if the leg is ever moved or grown out of its own collider.
    def landing():
        zmin=vector(fields(KEY)['LandingLegZoneMin']);zmax=vector(fields(KEY)['LandingLegZoneMax'])
        require(close(zmin,(t,d['ground'],(k[2]-L/2)/2-2*t)) and close(zmax,(5*t,k[1]-t,(k[2]-L/2)/2+2*t)))
        require(close(vector(fields(KEY)['LandingGearSize']),(4*t,k[1]-t-d['ground'],4*t)))
        centre=vector(fields(KEY)['LandingGearCenter'])
        size=vector(fields(KEY)['LandingGearSize'])
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
        angle=number(fields(KEY)['HitchYawLimit']);want=math.degrees(math.atan2(d['draw'],W/2+t/2))
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
        require(rx*lx < 0,'the two lenses did not move in opposite directions')
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
    add('tail lamps and palette load through Parts',lambda:require('"'+KEY+'_taillights.txt"' in fields(KEY)['Parts'] and fields(KEY)['Palette']=='"car_trailer_palette.png"' and close(vectors(fields(KEY)['TailPos']),lenses())),
        (VEH,lambda x:x.replace('"'+KEY+'_taillights.txt"','"missing.txt"',1),'remove lamp part'),fieldmut('Palette','"missing.png"'),
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
        red=sample(LAMPS,'tail_lens_1')
        require(max(steel[:3])-min(steel[:3])<20 and steel[3]==255)
        require(wood[0]>wood[1]>wood[2] and green[1]>green[0] and green[1]>green[2])
        require(red[0]>2*red[1] and red[0]>2*red[2])
    flip_uv=lambda src:'\n'.join('vt '+line.split()[1]+' '+str(1-float(line.split()[2])) if line.startswith('vt ') else line for line in src.splitlines())+'\n'
    add('V-flip selects steel, timber, green sides and red tail lenses',palette_samples,
        (BODY,flip_uv,'undo authored body V compensation'),
        (LAMPS,flip_uv,'undo lamp V compensation'))
    old=source_names(subprocess.check_output(['git','show',BASE+':game/Vehicle.cs'],cwd=ROOT,text=True))
    # The rename happened IN PLACE at index 33, so every TypeId before it is untouched and the two new
    # sizes are appended after. That ordering is the replicated protocol: an insert renumbers everyone.
    names=[c+'_trailer' for c in CLASSES]
    add('33 old network TypeIds preserved; the trailers appended in size order',
        lambda:require(source_names(VEH.read_text())==old+names),
        (VEH,lambda x:x.replace('"wagon", "dinky_trailer"','"dinky_trailer", "wagon"'),'insert before wagon'),
        (VEH,lambda x:x.replace(', "'+names[-1]+'" };',' };'),'drop the last size'),
        (VEH,lambda x:x.replace('"dinky_trailer", "small_trailer"','"small_trailer", "dinky_trailer"'),'swap dinky and small'))
    # "car_trailer" was the shipped spawn name; it stays as an ALIAS onto the dinky so the command
    # people already have keeps working. It must NOT be in SpecNames -- that would consume a TypeId.
    add('the old car_trailer name is an alias, not a TypeId',
        lambda:require('"car_trailer" => BuildDinkyTrailer(variant)' in uncomment(VEH.read_text())
                       and '"car_trailer" => _dinky_trailer' in uncomment(VEH.read_text())
                       and 'car_trailer' not in source_names(VEH.read_text())),
        (VEH,lambda x:x.replace('"car_trailer" => BuildDinkyTrailer(variant), ',''),'drop the build alias'),
        (VEH,lambda x:x.replace('"car_trailer" => _dinky_trailer, ',''),'drop the spec alias'),
        (VEH,lambda x:x.replace('"wagon", "dinky_trailer"','"wagon", "car_trailer", "dinky_trailer"'),'give the alias a TypeId'))
    for label,needle in [('builder','Build(_'+KEY+', variant, "'+KEY+'")'),
                         ('command dispatch','"'+KEY+'" => Build'+KEY.split('_')[0].capitalize()+'Trailer(variant)'),
                         ('replica spec dispatch','"'+KEY+'" => _'+KEY)]:
        add(label,lambda n=needle:require(n in uncomment(VEH.read_text())),(VEH,lambda x,n=needle:x.replace(n,n.replace('trailer','trailr'),1),'revert '+label))
    world=ROOT/'game/WorldBuilder.cs'
    add('command-only: absent from natural spawns',lambda:require('"'+KEY+'"' not in uncomment(world.read_text())),
        (world,lambda x:x.replace('0 => "sedan"','0 => "'+KEY+'"',1),'add natural spawn'))
    return result

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--mutation-test',action='store_true')
    parser.add_argument('--class',dest='only',default=None,help='verify one size class instead of all three')
    args=parser.parse_args()
    sizes=[args.only] if args.only else list(CLASSES)
    total_checks=total_caught=0
    for cls in sizes:
        checks=cases(cls)
        for c in checks:
            c.fn()
            print('PASS',cls,'|',c.label)
        total_checks+=len(checks)
        if args.mutation_test:
            for c in checks:
                assert c.mutants,(cls,c.label)
                for path,edit,label in c.mutants:
                    before=path.read_bytes()
                    try:
                        after=edit(before.decode()).encode();assert after!=before,(cls,c.label,label,'ineffective mutation')
                        path.write_bytes(after)
                        try:c.fn()
                        except (AssertionError,KeyError,ValueError,IndexError) as exc:
                            total_caught+=1
                            print('REJECT',cls,'|',c.label,'/',label,':',exc)
                        else:raise RuntimeError('SURVIVED: '+cls+' / '+c.label+' / '+label)
                    finally:path.write_bytes(before)
            for c in checks:c.fn()   # original bytes restored, clean checks rerun
    if args.mutation_test:
        print(f'PASS mutation audit: {total_caught} real-file reversions/corruptions rejected across '
              f'ALL {total_checks} named checks over {len(sizes)} size class(es); original bytes restored and clean checks rerun')
    else:
        print(f'PASS {total_checks} named checks over {len(sizes)} size class(es)')
    print('Static geometry/spec checks do not verify towing stability, cargo retention or multiplayer hitch interaction.')
if __name__=='__main__':main()
