#!/usr/bin/env python3
"""Author one donor-derived reload and an axial pump action; Python stdlib only."""
import argparse, bisect, csv, hashlib, json, math
from pathlib import Path
DEFAULT_SOURCE = str(Path(__file__).resolve().parents[1]/'game/content/rig.json')

def add(a,b): return [x+y for x,y in zip(a,b)]
def sub(a,b): return [x-y for x,y in zip(a,b)]
def norm(a): return math.sqrt(sum(x*x for x in a))
def unit(q): return [x/norm(q) for x in q]
def lerp(a,b,u): return [x+(y-x)*u for x,y in zip(a,b)]
def slerp(a,b,u):
    a,b=unit(a),unit(b); dot=sum(x*y for x,y in zip(a,b))
    if dot<0: b=[-x for x in b]; dot=-dot
    dot=min(1.,max(-1.,dot))
    if dot>.9995: return unit(lerp(a,b,u))
    th=math.acos(dot); s=math.sin(th)
    return [(math.sin((1-u)*th)*x+math.sin(u*th)*y)/s for x,y in zip(a,b)]
def sample(keys,t,rot=False):
    i=bisect.bisect_right([k[0] for k in keys],t)
    if i==0: v=keys[0][1:]
    elif i==len(keys): v=keys[-1][1:]
    else:
        a,b=keys[i-1],keys[i]; u=(t-a[0])/(b[0]-a[0])
        v=slerp(a[1:],b[1:],u) if rot else lerp(a[1:],b[1:],u)
    return unit(v) if rot else v

def mm(a,b): return [[sum(a[i][k]*b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
def mv(a,v): return [sum(row[k]*v[k] for k in range(3)) for row in a]
def inv(a):
    x,y,z=a
    cross=lambda a,b:[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]
    c=[cross(y,z),cross(z,x),cross(x,y)]; d=sum(x[i]*c[0][i] for i in range(3))
    return [[c[j][i]/d for j in range(3)] for i in range(3)]
def mat(q,s=(1,1,1)):
    x,y,z,w=unit(q)
    r=[[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
       [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
       [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]]
    return [[row[j]*s[j] for j in range(3)] for row in r]
MOUNT=mat([0,0,math.sqrt(.5),math.sqrt(.5)])
def fk(r,clip,t):
    out={}; arr=[]
    for b in r['bones']:
        tr=clip['tracks'].get(b['name'],{})
        p=sample(tr['pos'],t) if 'pos' in tr else b['pos']
        q=sample(tr['rot'],t,True) if 'rot' in tr else b['rot']
        s=sample(tr['scale'],t) if 'scale' in tr else b['scale']
        m=mat(q,s)
        if b['parent']>=0:
            pm,pp=arr[b['parent']]; p=add(pp,mv(pm,p)); m=mm(pm,m)
        arr.append((m,p)); out[b['name']]=(m,p)
    return out

def palm(r,clip,t,offset=(-.32,0,0)):
    f=fk(r,clip,t); lm,lp=f['Left_Hand']; rm,rp=f['Right_Hook']
    # Gun transform is world(Right_Hook) * Rz(+90deg), no extra translation.
    return mv(inv(mm(rm,MOUNT)),sub(add(lp,mv(lm,offset)),rp))

def analyze(r):
    for name in ['Bluntforce_Reload','Bluntforce_Hammer']:
        c=r['anims'][name]; print(name)
        for i in range(round(c['length']*30)+1):
            t=min(i/30,c['length']); p=palm(r,c,t)
            print(f'{t:.6f} '+ ' '.join(f'{v:.6f}' for v in p))


def digest(obj):
    return hashlib.sha256(json.dumps(obj,sort_keys=True,separators=(',',':')).encode()).hexdigest()

def write_json(path,obj):
    path.write_text(json.dumps(obj,indent=2,allow_nan=False)+'\n')

def extrema(values):
    # Strict directional extrema, ignoring plateaus and insignificant roundoff.
    signs=[]
    for i in range(1,len(values)):
        d=values[i]-values[i-1]
        if abs(d)>1e-5: signs.append((i,1 if d>0 else -1))
    return [signs[j][0]-1 for j in range(1,len(signs)) if signs[j][1]!=signs[j-1][1]]

def author(args,r):
    out=Path(args.out_dir);out.mkdir(parents=True,exist_ok=True)
    reload=r['anims']['Bluntforce_Reload']; hammer=r['anims']['Bluntforce_Hammer']
    assert len(r['bones'])==17
    # Frame 29 and 59 are matching contact poses. Remove intervening repeated taps.
    rk=reload['tracks']['Left_Arm']['rot']
    cut_a=rk[29][0];cut_b=rk[59][0]
    windows=[(0.,cut_a),(cut_b,reload['length'])]
    kept=sum(b-a for a,b in windows); rate=kept/args.length
    seam=norm(sub(palm(r,reload,cut_a),palm(r,reload,cut_b)))
    seam_pos=0.;seam_ang=0.
    for tr in reload['tracks'].values():
        seam_pos=max(seam_pos,norm(sub(sample(tr['pos'],cut_a),sample(tr['pos'],cut_b))))
        q1,q2=sample(tr['rot'],cut_a,True),sample(tr['rot'],cut_b,True)
        seam_ang=max(seam_ang,2*math.acos(min(1,abs(sum(x*y for x,y in zip(q1,q2))))))
    assert seam_pos<1e-5 and seam_ang<1e-4, 'Donor contact seam no longer matches'
    tracks={}
    for bone,tr in reload['tracks'].items():
        tracks[bone]={}
        for channel,keys in tr.items():
            authored=[]; accumulated=0.
            for start,end in windows:
                times=[start]+[k[0] for k in keys if start<k[0]<end]+[end]
                for t in times:
                    nt=(accumulated+t-start)/rate
                    if authored and abs(nt-authored[-1][0])<1e-8: continue
                    authored.append([nt]+sample(keys,t,channel=='rot'))
                accumulated+=end-start
            authored[0][0]=0.; authored[-1][0]=args.length
            # Keep genuinely static donor channels minimal, retaining both boundary poses.
            if all(norm(sub(k[1:],authored[0][1:]))<1e-7 for k in authored):
                authored=[authored[0],authored[-1]]
            tracks[bone][channel]=authored
    clip={'fps':reload['fps'],'length':args.length,'loop':False,'tracks':tracks}
    write_json(out/'mossberg_anims.json',{'Bluntforce_Reload_OneShell':clip})
    # Axial motion in the animated gun frame, not the world or Left_Hook frame.
    # "rest" means Hammer time-zero posed rest, NOT bind/T pose.
    hlen=hammer['length']; times=sorted(set([0.,hlen]+[k[0] for tr in hammer['tracks'].values() for keys in tr.values() for k in keys if 0<=k[0]<=hlen]))
    rest=palm(r,hammer,0.)[1]
    hrows=[];action_keys=[]
    for t in times:
        p=palm(r,hammer,t); raw=p[1]-rest
        delta=max(-args.max_pump,min(0.,raw))
        if t==0. or t==hlen: delta=0.
        action_keys.append({'time':t,'localGunYDelta':delta})
        hrows.append([t]+p+[raw,delta,.6937+delta])
    pump={'Bluntforce_Hammer':{'fps':hammer['fps'],'length':hlen,'loop':False,
        'pumpCenterLocalY':.6937,'units':'meters','axis':'localGunY',
        'negativeIsRear':True,'interpolation':'linear','keys':action_keys}}
    write_json(out/'mossberg_pump_action.json',pump)
    # Numerical evidence for main's renderer; no geometry is authored.
    with (out/'palm_motion.csv').open('w',newline='') as f:
        w=csv.writer(f);w.writerow(['clip','time','palmGunX','palmGunY','palmGunZ','rawYDelta','clampedYDelta','pumpCenterY'])
        for i in range(round(reload['length']*30)+1):
            t=min(i/30,reload['length']); w.writerow(['Bluntforce_Reload',t]+palm(r,reload,t)+['','',''])
        for row in hrows: w.writerow(['Bluntforce_Hammer']+row)
        for i in range(round(args.length*30)+1):
            t=min(i/30,args.length); w.writerow(['Bluntforce_Reload_OneShell',t]+palm(r,clip,t)+['','',''])
    max_qerr=0.;total_keys=0
    for tr in tracks.values():
        for ch,keys in tr.items():
            assert keys[0][0]==0 and keys[-1][0]==args.length
            assert all(keys[i][0]<keys[i+1][0] for i in range(len(keys)-1))
            assert all(math.isfinite(x) for k in keys for x in k)
            if ch=='rot': max_qerr=max(max_qerr,max(abs(norm(k[1:])-1) for k in keys))
            total_keys+=len(keys)
    assert max_qerr<1e-12
    assert action_keys[0]['localGunYDelta']==action_keys[-1]['localGunYDelta']==0
    actual=max(-k['localGunYDelta'] for k in action_keys)
    assert 0<=actual<=args.max_pump
    # Each final local channel matches its initial rest pose (up to donor precision).
    close_pos=max(norm(sub(tr['pos'][0][1:],tr['pos'][-1][1:])) for tr in tracks.values())
    close_ang=max(2*math.acos(min(1,abs(sum(x*y for x,y in zip(tr['rot'][0][1:],tr['rot'][-1][1:]))))) for tr in tracks.values())
    assert close_pos<1e-5 and close_ang<1e-4
    sampled=[palm(r,clip,args.length*i/330) for i in range(331)]
    analysis={'source_path':str(Path(args.source).resolve()),
        'source_sha256':hashlib.sha256(Path(args.source).read_bytes()).hexdigest(),
        'donor_canonical_sha256':{n:digest(r['anims'][n]) for n in ['Bluntforce_Reload','Bluntforce_Hammer']},
        'reload_source_windows_seconds':windows,'removed_seconds':cut_b-cut_a,
        'retained_source_seconds':kept,'output_length_seconds':args.length,'speed_multiplier':rate,
        'insertion_source_window_seconds':[reload['tracks']['Left_Arm']['rot'][24][0],cut_a],
        'insertion_output_window_seconds':[reload['tracks']['Left_Arm']['rot'][24][0]/rate,cut_a/rate],
        'insertion_duration_seconds':(cut_a-reload['tracks']['Left_Arm']['rot'][24][0])/rate,
        'pump_rest_palm_gun_y':rest,'pump_raw_max_rear_m':max(-row[4] for row in hrows),
        'pump_actual_max_rear_m':actual,'pump_center_local_y':.6937,
        'pump_center_range_m':[.6937-actual,.6937],
        'validation':{'bone_tracks':len(tracks),'total_keys':total_keys,'max_quaternion_norm_error':max_qerr,
            'seam_position_max_m':seam_pos,'seam_rotation_max_rad':seam_ang,'seam_palm_gap_m':seam,
            'closing_position_max_m':close_pos,'closing_rotation_max_rad':close_ang,
            'authored_palm_x_extrema_times':[i*args.length/330 for i in extrema([p[0] for p in sampled])],
            'time_zero_and_final_keys':True,'finite_monotonic_keys':True,'pump_bounded_and_ends_zero':True,
            'visually_verified':False}}
    write_json(out/'authoring_report.json',analysis)
    report=f'''Mossberg / Bluntforce single-shell authoring (rig data only)
Source: {args.source}
Source SHA-256: {analysis['source_sha256']}
Canonical donor hashes: see authoring_report.json (JSON sorted keys, compact separators).

Reload: Bluntforce_Reload_OneShell, 17 donor bone tracks, fps 30, loop false.
Retained windows: [0, {cut_a:.9f}] + [{cut_b:.9f}, {reload['length']:.9f}] seconds.
Removed [{cut_a:.9f}, {cut_b:.9f}]: repeated small insertion/tapping cycles only.
This is a crop-and-splice, NOT a contiguous crop or accelerated full multi-tap clip.
The donor has one large fetch, several small contact taps, and one return; no contiguous
0.7-1.2-second window contains the full fetch/insert/return. Matching frame-29/frame-59
contact poses allow a direct splice (no synthetic movement or IK).
Kept {kept:.9f}s, retimed uniformly to {args.length:.6f}s ({rate:.6f}x).
Fetch: source 0 to about 0.333333; approach/contact: about 0.333333 to 0.800000;
one inward insertion: source 0.800000 to {cut_a:.9f}, output
{analysis['insertion_output_window_seconds'][0]:.6f} to {analysis['insertion_output_window_seconds'][1]:.6f}
({analysis['insertion_duration_seconds']:.6f}s). Then return from source {cut_b:.9f} to end.
Boundary rotations are normalized shortest-arc SLERP; positions linear; held outside
sparse constant channels. Original interior keys retained and retimed, with explicit
time-zero/final keys. Static channels reduced to two keys; donor closing rest retained.

FK: parent-local T*R*S, quaternion xyzw. Gun world = Right_Hook world * Rz(+90deg),
zero extra mount translation. Palm/contact = Left_Hand world * (-0.32,0,0,1),
NOT Left_Hook at x=-0.508. Inverse gun matrix supplies palm local coordinates.
Pump: original Bluntforce_Hammer length {hlen:.9f}s. Rest is Hammer time zero,
not bind pose. deltaY(t) = clamp(palmGunY(t) - palmGunY(0), -{args.max_pump}, 0).
Fixed nominal pump center local Y=0.6937; animated center=0.6937+deltaY.
No X/Z pump translation or rotation is authored; transverse palm motion is ignored.
Raw rear excursion {analysis['pump_raw_max_rear_m']:.9f}m; actual max rear
{actual:.9f}m (negative delta), center range [{.6937-actual:.6f},0.693700]m.
Both action endpoints are exactly zero. Linear interpolation between action keys.
Pump JSON is a dictionary keyed Bluntforce_Hammer; each key has time (s) and
localGunYDelta (m). It is independent of the {args.length:.6f}s reload, to play with the existing Hammer.

Validation: all 17 track names retained; finite strictly increasing times; all channels
span 0 to length; max quaternion norm error {max_qerr:.3g}; seam position
{seam_pos:.3g}m / angle {seam_ang:.3g}rad; closing pose position {close_pos:.3g}m /
angle {close_ang:.3g}rad. Pump bounds and endpoint checks pass.
Ambiguity: insertion phases inferred from kinematics, not shell events or load-port
geometry. A full hand cycle is {args.length:.6f}s; the inward contact subphase is much shorter.
A smaller requested clamp can leave axial palm/pump mismatch; the measured raw and
used excursion are reported above. This authoring script does not visually verify the output.
Reproduce: python3 author_mossberg_anims.py --source PATH --out-dir DIR
Optional: --length 1.1 --max-pump 0.19; --analyze prints full donor palm trajectories.
No repo files were modified; no network or non-source assets were used.
'''
    (out/'authoring_report.txt').write_text(report)
    print(report)

if __name__=='__main__':
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--source',default=DEFAULT_SOURCE)
    ap.add_argument('--out-dir',default=str(Path(__file__).resolve().parent))
    ap.add_argument('--length',type=float,default=1.1)
    ap.add_argument('--max-pump',type=float,default=.19)
    ap.add_argument('--analyze',action='store_true'); args=ap.parse_args()
    if not (.7<=args.length<=1.2): ap.error('--length must be 0.7..1.2 seconds')
    if not (0<args.max_pump<=.20): ap.error('--max-pump must be >0 and <=0.20 meters')
    r=json.loads(Path(args.source).read_bytes())
    if args.analyze: analyze(r)
    else: author(args,r)
