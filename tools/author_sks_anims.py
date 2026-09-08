#!/usr/bin/env python3
"""SKS clips derived from the existing Zubeknakov rig, with contact-driven IK.
Writes additions only; never opens the shared rig for writing. scipy/numpy required.
The block hand has no thumb bone. Its medial face performs the stripper press.
"""
import argparse,copy,hashlib,json,math,pathlib
import numpy as np
from scipy.spatial.transform import Rotation as R,Slerp

def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pristine",default="/tmp/snow/guns/rig.json.PRISTINE",help="Read-only source rig; never modified")
    parser.add_argument("--out-dir",default="/tmp/snow/guns",help="Directory for animation additions, action tracks and authoring report")
    args=parser.parse_args(argv)
    ROOT=pathlib.Path(args.out_dir)
    ROOT.mkdir(parents=True,exist_ok=True)
    pristine_bytes=pathlib.Path(args.pristine).read_bytes()
    rig=json.loads(pristine_bytes)
    bones=rig['bones']; ix={b['name']:i for i,b in enumerate(bones)}; anims=rig['anims']
    I=R.identity(); MOUNT=R.from_euler('z',90,degrees=True)
    def smooth(x):
     x=np.clip(x,0.,1.);return x*x*(3-2*x)
    def curve(keys,t):
     if t<=keys[0][0]:return np.array(keys[0][1:])
     if t>=keys[-1][0]:return np.array(keys[-1][1:])
     for a,b in zip(keys,keys[1:]):
      if a[0]<=t<=b[0]:return np.array(a[1:])+(np.array(b[1:])-a[1:])*smooth((t-a[0])/(b[0]-a[0]))
    def mixrot(a,b,f):return a*R.from_rotvec((a.inv()*b).as_rotvec()*f)
    def sample(a,t):
     out=[]
     for b in bones:
      tr=a['tracks'].get(b['name'],{});v={}
      for c in ('rot','pos'):
       k=np.array(tr.get(c,[[0]+b[c]]));ts=k[:,0]; vals=k[:,1:];tt=np.clip(t,ts[0],ts[-1])
       v[c]=R.from_quat(vals[0]) if c=='rot' and len(k)==1 else Slerp(ts,R.from_quat(vals))([tt])[0] if c=='rot' else np.array([np.interp(tt,ts,vals[:,j]) for j in range(3)])
      out.append(v)
     return out
    def fk(p):
     out=[]
     for b,v in zip(bones,p):
      q,pt=v['rot'],v['pos'];pa=b['parent']
      if pa>=0:q=out[pa][0]*q;pt=out[pa][1]+out[pa][0].apply(pt)
      out.append((q,pt))
     return out
    def setglobal(p,bn,q=None,pt=None):
     i=ix[bn];pa=bones[i]['parent'];g=fk(p);pq,pp=g[pa] if pa>=0 else (I,np.zeros(3))
     if q is not None:p[i]['rot']=pq.inv()*q
     if pt is not None:p[i]['pos']=pq.inv().apply(pt-pp)
    def gun(p):
     q,pt=fk(p)[ix['Right_Hook']];return q*MOUNT,pt
    # Physical endcap centre of the skinned mitten (Hook at -.508 is beyond mesh).
    CONTACT=np.array([-.32,0.,0.])
    def palm(p,side):
     q,pt=fk(p)[ix[side+'_Hand']];return pt+q.apply(CONTACT)
    def unit(v):return v/max(1e-12,np.linalg.norm(v))
    def align(a,b):
     a,b=unit(a),unit(b);cross=np.cross(a,b);dot=np.clip(np.dot(a,b),-1,1)
     if np.linalg.norm(cross)<1e-9:return I if dot>0 else R.from_rotvec(unit(np.cross(a,[0,1,0] if abs(a[1])<.9 else [1,0,0]))*np.pi)
     return R.from_rotvec(unit(cross)*math.acos(dot))
    errors=[]
    def ik(p,side,target,pole=None):
     """Three-link FABRIK from donor shoulder, preserving every segment length.
     Shoulder translation supplies the missing clavicle's reach only when necessary.
     """
     g=fk(p);si=ix[side+'_Shoulder'];ai=ix[side+'_Arm'];hi=ix[side+'_Hand']
     sq,root=g[si];aq,upper=g[ai];hq,elbow=g[hi]
     points=np.array([root,upper,elbow,palm(p,side)])
     lengths=np.linalg.norm(np.diff(points,axis=0),axis=1);reach=sum(lengths)
     d=np.linalg.norm(target-root)
     if d>reach-.025:
      move=unit(target-root)*(d-reach+.025)
      points+=move;root=points[0].copy()
      setglobal(p,side+'_Shoulder',pt=root)
     for _ in range(60):
      points[3]=target
      for j in (2,1,0):points[j]=points[j+1]+unit(points[j]-points[j+1])*lengths[j]
      points[0]=root
      for j in (0,1,2):points[j+1]=points[j]+unit(points[j+1]-points[j])*lengths[j]
      if np.linalg.norm(points[3]-target)<1e-8:break
     # World-space shortest swings retain each donor bone's roll/twist.
     nsq=align(upper-g[si][1],points[1]-points[0])*sq
     naq=align(elbow-upper,points[2]-points[1])*aq
     nhq=align(hq.apply(CONTACT),points[3]-points[2])*hq
     setglobal(p,side+'_Shoulder',q=nsq)
     setglobal(p,side+'_Arm',q=naq)
     setglobal(p,side+'_Hand',q=nhq)
     errors.append(float(np.linalg.norm(palm(p,side)-target)))
     return p

    # Surface orientation exposes the clip on the screen-right of the left mitten.
    # These axes are derived from the desired forearm reach, not blind quaternions:
    # local -X reaches up/right; local Y runs along camera depth. The selected
    # vertex lies on the actual skinned hand's rightmost edge in this orientation.
    PRESS_CONTACT=np.array([-.376,-.199,.174])
    px=np.array([-math.cos(math.radians(20)),-math.sin(math.radians(20)),0.]);py=np.array([0.,0.,-1.])
    PRESS_ROT=R.from_matrix(np.column_stack((px,py,np.cross(px,py))))
    def ik_surface(p,side,target,hand_rotation,contact=PRESS_CONTACT,blend=1.):
        g=fk(p);si=ix[side+'_Shoulder'];ai=ix[side+'_Arm'];hi=ix[side+'_Hand']
        sq,root=g[si];aq,upper=g[ai];hq,wrist=g[hi]
        desired_wrist=target-hand_rotation.apply(contact)
        l1=np.linalg.norm(p[ai]['pos']);l2=np.linalg.norm(p[hi]['pos'])
        vec=desired_wrist-root;distance=np.linalg.norm(vec);axis=unit(vec)
        if distance>l1+l2-.025:
            root=root+axis*(distance-l1-l2+.025)
            setglobal(p,side+'_Shoulder',pt=root)
            vec=desired_wrist-root;distance=np.linalg.norm(vec);axis=unit(vec)
        stable_pole=np.array([-.6 if side=='Left' else .6,-.6,.4])
        pole=unit(upper-g[si][1])*(1-blend)+unit(stable_pole)*blend
        perp=unit(pole-axis*np.dot(pole,axis))
        a=(l1*l1-l2*l2+distance*distance)/(2*distance)
        desired_upper=root+axis*a+perp*math.sqrt(max(0,l1*l1-a*a))
        donor=fk(ready);dsq,ds=donor[si];daq,da=donor[ai];dhq,dh=donor[hi]
        nsq=mixrot(align(upper-g[si][1],desired_upper-root)*sq,align(da-ds,desired_upper-root)*dsq,blend)
        naq=mixrot(align(wrist-upper,desired_wrist-desired_upper)*aq,align(dh-da,desired_wrist-desired_upper)*daq,blend)
        setglobal(p,side+'_Shoulder',q=nsq)
        setglobal(p,side+'_Arm',q=naq)
        setglobal(p,side+'_Hand',q=hand_rotation)
        hq,hp=fk(p)[hi];errors.append(float(np.linalg.norm(hp+hq.apply(contact)-target)))
        return p
    base=sample(anims['Zubeknakov_Equip'],10)
    # The fixed SKS fore-end is longer than AK: support the wood ahead of the magazine.
    SUPPORT=np.array([.025,.335,.115])
    def support(p,blend=1.):
     q,pt=gun(p);start=palm(p,'Left');return ik(p,'Left',start*(1-blend)+(pt+q.apply(SUPPORT))*blend)
    ready=support(copy.deepcopy(base))
    READY_G=gun(ready)
    def present(p,amount):
     """Move donor's entire grip chain together; grip-to-gun transform is invariant."""
     g=fk(p);q,pt=g[ix['Right_Shoulder']];gq,gp=gun(p)
     delta=R.from_euler('xyz',[-12*amount,20*amount,-18*amount],degrees=True)
     shift=np.array([-.16,-.01,-.12])*amount
     setglobal(p,'Right_Shoulder',delta*q,gp+delta.apply(pt-gp)+shift)
     return p
    # The mitten stays to the left of the clip. Its medial lower corner presses the stack.
    # +X shooter-left; +Y muzzle; -Z gun-up.
    RELOAD_HAND=[
     [0,*SUPPORT],[.25,.16,.26,.10],[.55,.31,-.05,.23],
     [.85,.32,.05,-.23],[1.10,.23,.10,-.40],[1.30,.21,.10,-.405],
     [1.55,.195,.10,-.425],[2.25,.195,.10,-.305],
     [2.40,.235,.10,-.355],[2.55,.30,.08,-.39],
     [2.75,.05,.075,-.36],[2.90,-.155,.075,-.26],
     [3.10,-.155,.060,-.26],[3.18,-.205,.060,-.285],
     [3.35,.08,.16,-.29],[3.65,.10,.32,.045],[4.,*SUPPORT]]
    RELOAD_SURFACE=[
        [0,.025,.335,.115],[.55,.31,-.05,.10],
        [.85,.11,.05,-.37],[1.10,0,.125,-.305],[1.30,0,.125,-.31],
        [1.55,0,.125,-.31],[2.25,0,.125,-.19],
        [2.40,0,.10,-.31],[2.55,.13,.09,-.45],
        [2.75,-.04,.075,-.24],[2.90,-.088,.075,-.155],
        [3.10,-.088,.060,-.155],[3.18,-.13,.06,-.23],
        [3.35,.08,.16,-.29],[3.65,.10,.32,.045],[4.,.025,.335,.115]]
    HAMMER_HAND=[
     [0,-.03598008,-.14528738,.14889058], # replaced below from exact ready palm
     [.22,-.17,.10,.03],[.40,-.235,.165,-.17],
     [.60,-.235,.165,-.17],[.90,-.235,.075,-.17],
     [.96,-.275,.075,-.19],[1.10,-.22,.04,.03],
     [1.40,-.03598008,-.14528738,.14889058]]
    bq,bp=gun(ready);r_local=bq.inv().apply(palm(ready,'Right')-bp)
    HAMMER_HAND[0]=[0,*r_local];HAMMER_HAND[-1]=[1.4,*r_local]
    def bake(name,donor,length,make):
     # Copy clip dictionary before replacing sampled tracks; retain bone set/order/fields.
     a=copy.deepcopy(anims[donor]);a['fps']=30.;a['length']=length;a['loop']=False
     times=np.linspace(0,length,round(length*30)+1); poses=[make(float(t)) for t in times]
     for bi,b in enumerate(bones):
      tr=a['tracks'].setdefault(b['name'],{})
      for channel in ('rot','pos'):
       vals=[po[bi][channel].as_quat() if channel=='rot' else po[bi][channel] for po in poses]
       if channel=='rot':
        for j in range(1,len(vals)):
         if np.dot(vals[j-1],vals[j])<0:vals[j]=-vals[j]
       tr[channel]=[[round(float(t),9),*[round(float(v),10) for v in val]] for t,val in zip(times,vals)]
     return a
    def reload_normal(t):
        p=copy.deepcopy(ready);f=float(curve([[0,0],[.4,1],[3.4,1],[4,0]],t)[0]);present(p,f)
        q,pt=gun(p);ik(p,'Left',pt+q.apply(curve(RELOAD_HAND,t)))
        return p
    def reload(t):
        p=reload_normal(t);q,pt=gun(p)
        weight=float(curve([[0,0],[.25,0],[.95,1],[3.18,1],[4,0]],t)[0])
        if weight>0:
            # Blend in world space from a fixed boundary orientation. Blending
            # changing local IK rotations can cross the quaternion half-turn.
            boundary=.25 if t<.95 else 4.
            refq=fk(reload_normal(boundary))[ix['Left_Hand']][0]
            handq=mixrot(refq,PRESS_ROT,weight)
            currentq,currentp=fk(reload_normal(boundary))[ix['Left_Hand']]
            oldsurface=currentp+currentq.apply(PRESS_CONTACT)
            target=oldsurface*(1-weight)+(pt+q.apply(curve(RELOAD_SURFACE,t)))*weight
            ik_surface(p,'Left',target,handq,blend=weight)
        return p
    def hammer(t):
     # The clip container is copied from Zubeknakov_Hammer; its new contact motion
     # starts from the IK-adjusted Zubeknakov_Equip ready anatomy. Keeping the gun
     # world mount fixed compensates the moving right hand.
     p=copy.deepcopy(ready);f=float(curve([[0,0],[.30,1],[1.06,1],[1.4,0]],t)[0]);present(p,f*.6)
     support(p);q,pt=gun(p);target=pt+q.apply(curve(HAMMER_HAND,t));ik(p,'Right',target)
     setglobal(p,'Right_Hook',q*MOUNT.inv(),pt)
     return p
    def equip(t):
     p=sample(anims['Zubeknakov_Equip'],t/1.8*anims['Zubeknakov_Equip']['length'])
     support(p,smooth((t-.4)/.9))
     return p
    def sprint(t,start):
     src=anims['Zubeknakov_Sprint_Start' if start else 'Zubeknakov_Sprint_Stop'];length=.4 if start else 11/30
     p=sample(src,t/length*src['length']);support(p)
     return p
    # Copy the Aim container and bake the SKS ready pose at every key: zero delta preserves
    # the already calibrated SKS iron axis exactly; ADS alignment supplies translation.
    def aim(t):return copy.deepcopy(ready)
    clips={
     'Sks_Reload':bake('Sks_Reload','Zubeknakov_Reload',4.,reload),
     'Sks_Hammer':bake('Sks_Hammer','Zubeknakov_Hammer',1.4,hammer),
     'Sks_Aim':bake('Sks_Aim','Zubeknakov_Aim',4/30,aim),
     'Sks_Equip':bake('Sks_Equip','Zubeknakov_Equip',1.8,equip),
     'Sks_Sprint_Start':bake('Sks_Sprint_Start','Zubeknakov_Sprint_Start',.4,lambda t:sprint(t,True)),
     'Sks_Sprint_Stop':bake('Sks_Sprint_Stop','Zubeknakov_Sprint_Stop',11/30,lambda t:sprint(t,False)),
    }
    (ROOT/'sks_anims.json').write_text(json.dumps(clips,separators=(',',':')))
    # Mechanism props use the same smooth interpolation as the contact authoring.
    CLIP_POS=[[0,.31,-.05,.10],[.55,.31,-.05,.10],[.85,.11,.05,-.25],[1.10,0,.10,-.185],[1.30,0,.10,-.190],[2.40,0,.10,-.190],[2.55,.13,.09,-.33],[2.85,.50,-.10,-.19],[4,.50,-.10,-.19]]
    def channel(keys,length):return [[round(float(t),9),*[round(float(v),10) for v in curve(keys,float(t))]] for t in np.linspace(0,length,round(length*30)+1)]
    actions={
     'reload':{
      'length':4.,'bolt':channel([[0,.09],[2.90,.09],[3.10,.105],[3.16,.105],[3.30,0],[4,0]],4),
      'clip_pos':channel(CLIP_POS,4),'clip_rot':[[0,0,0,0,1],[4,0,0,0,1]],
      'strip':channel([[0,0],[1.55,0],[2.25,1],[4,1]],4),
      'visible':[[0,0],[.58,1],[2.85,0],[4,0]],
      'left_surface':channel(RELOAD_SURFACE,4),
     },
     'hammer':{'length':1.4,'bolt':channel([[0,0],[.60,0],[.90,.09],[.96,.09],[1.08,0],[1.4,0]],1.4),'right_palm':channel(HAMMER_HAND,1.4)}
    }
    for key in actions['reload']['clip_pos']:
        t=key[0]
        if .25<t<.95:
            po=sample(clips['Sks_Reload'],t);gq,gp=gun(po);hq,hp=fk(po)[ix['Left_Hand']]
            key[1:]=[round(float(v),10) for v in gq.inv().apply(hp+hq.apply(PRESS_CONTACT)-gp)+np.array([0,0,.12])]
    (ROOT/'sks_action_tracks.json').write_text(json.dumps(actions,separators=(',',':')))
    meta={'clips':{k:{'fps':a['fps'],'length':a['length'],'donor':'Zubeknakov_'+k[4:]} for k,a in clips.items()},'max_ik_target_error_m':max(errors),'hand_contact_local':CONTACT.tolist(),'foreend_support_gun_local':SUPPORT.tolist(),'zero_additive_aim':True,'reload_timeline':{'1.30':'clip seated above fixed magazine','1.55–2.25':'straight-down 120 mm stripping stroke','2.55':'empty clip flick','3.10':'left hand slight rear tug on right-side handle','3.16–3.30':'bolt release/closure','4.00':'ready'},'limitations':'Original mesh uses broad mittens without articulated thumb/fingers; thumb press represented by medial hand surface. Inspect retains explicit existing donor.'}
    # Audit the serialized additions, not just unrounded authoring poses.
    ref=sample(clips['Sks_Reload'],0);q0,p0=gun(ref);hq0,hp0=fk(ref)[ix['Right_Hand']]
    rq0=q0.inv()*hq0;rp0=q0.inv().apply(hp0-p0)
    grip_pos=grip_rot=support_error=0.
    for t in np.linspace(0,4,121):
     po=sample(clips['Sks_Reload'],float(t));gq,gp=gun(po);hq,hp=fk(po)[ix['Right_Hand']]
     grip_pos=max(grip_pos,float(np.linalg.norm(gq.inv().apply(hp-gp)-rp0)))
     grip_rot=max(grip_rot,float((rq0.inv()*gq.inv()*hq).magnitude()))
    for t in np.linspace(0,1.4,43):
     po=sample(clips['Sks_Hammer'],float(t));gq,gp=gun(po)
     support_error=max(support_error,float(np.linalg.norm(gq.inv().apply(palm(po,'Left')-gp)-SUPPORT)))
    continuity={}
    for name,clip in clips.items():
     max_degrees=0.;max_bone=None;max_frame=None;min_dot=1.;strict_times=True;max_position_step=0.
     for bone,track in clip['tracks'].items():
      keys=np.array(track['rot']);qs=R.from_quat(keys[:,1:]);dots=np.sum(keys[:-1,1:]*keys[1:,1:],axis=1)
      degrees=(qs[:-1].inv()*qs[1:]).magnitude()*180/np.pi;j=int(np.argmax(degrees))
      if degrees[j]>max_degrees:max_degrees=float(degrees[j]);max_bone=bone;max_frame=j
      min_dot=min(min_dot,float(min(dots)))
      for channel,keys in track.items():
       keys=np.array(keys);strict_times &= bool(np.all(np.diff(keys[:,0])>0))
       if channel=='pos':max_position_step=max(max_position_step,float(max(np.linalg.norm(np.diff(keys[:,1:],axis=0),axis=1))))
     continuity[name]={'max_adjacent_rotation_degrees':max_degrees,'bone':max_bone,'start_frame':max_frame,'minimum_adjacent_quaternion_dot':min_dot,'strictly_increasing_key_times':strict_times,'max_adjacent_local_position_step_m':max_position_step}
    press_points=[]
    for t in np.linspace(1.55,2.25,22):
     po=sample(clips['Sks_Reload'],float(t));gq,gp=gun(po);hq,hp=fk(po)[ix['Left_Hand']];press_points.append(gq.inv().apply(hp+hq.apply(PRESS_CONTACT)-gp))
    press_points=np.array(press_points)
    meta['provenance']={
     'pristine_sha256':hashlib.sha256(pristine_bytes).hexdigest(),
     'clip_containers':'Deep copies of matching Zubeknakov action clips; original bone/track ordering retained.',
     'ready_anatomy':'Zubeknakov_Equip final pose, with support hand retargeted to the SKS fore-end.',
     'reload_and_hammer':'New contact trajectories over donor Equip ready anatomy. AK magazine-swap and left-hand rack motion are replaced, not retained.',
     'equip_and_sprint':'Original matching donor motion sampled at longer SKS timing, with left support contact solved per frame.',
     'aim':'Matching Aim container with every key set to the same SKS ready pose; zero additive delta.',
     'solver':'Three-link FABRIK for support/carry; reload uses prescribed hand orientation and two-link IK to an actual mitten edge, retaining donor bone lengths. Shoulder translation supplies missing clavicle reach only when necessary.'
    }
    meta['numeric_audits']={
     'reload_right_grip_max_translation_error_m':grip_pos,
     'reload_right_grip_max_rotation_error_rad':grip_rot,
     'hammer_left_foreend_max_contact_error_m':support_error,
     'press_gun_local_delta_m':(press_points[-1]-press_points[0]).tolist(),
     'press_max_lateral_deviation_m':float(np.max(np.linalg.norm(press_points[:,:2]-press_points[0,:2],axis=1))),
     'press_target_gun_local_delta_m':(curve(RELOAD_SURFACE,2.25)-curve(RELOAD_SURFACE,1.55)).tolist(),
        'press_hand_surface_local_m':PRESS_CONTACT.tolist(),
     'serialized_key_continuity':continuity,
    }
    assert grip_pos<1e-6 and grip_rot<1e-6 and support_error<1e-6
    assert all(v['strictly_increasing_key_times'] and v['minimum_adjacent_quaternion_dot']>0 for v in continuity.values())
    (ROOT/'sks_anims_authoring.json').write_text(json.dumps(meta,indent=2)+'\n')
    print(json.dumps(meta,indent=2))

if __name__ == "__main__":
    main()
