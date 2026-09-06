"""Derive the chainsaw's IDLE loop from its one retail `use` clip.

strawberry 2026-09-06: "change the chainsaw's idle sound to be continuous, but distinct from the sawing
sound". The idle used to be the use clip looped at pitch 0.8, which was neither -- looping a cutting burst
replays its attack every cycle (pulsing, not droning) and it was the sawing sound because it WAS the sawing
sound.

Writes four peak-matched candidates; melee_chainsaw_idle.wav is whichever one is chosen (currently C).
Run from the repo root:  python3 tools/make_chainsaw_idle.py [outdir]
"""
import wave, struct, math, sys
SRC='game/content/audio/items/melee_chainsaw_use.wav'
OUT=sys.argv[1] if len(sys.argv)>1 else 'game/content/audio/items/'
w=wave.open(SRC); n=w.getnframes(); ch=w.getnchannels(); sr=w.getframerate()
s=struct.unpack('<%dh'%(n*ch), w.readframes(n))
L=[s[i*ch] for i in range(n)]; R=[s[i*ch+1] for i in range(n)] if ch==2 else L[:]

# the SUSTAINED BODY (the engine texture), skipping the attack and the decay tail
a,b = int(0.30*sr), int(0.51*sr)
bl, br = L[a:b], R[a:b]

def flatten(x, sr, win=0.005):
    """Hold the level constant so the saw's chop stops reading as separate bites and becomes a drone.
    This is what makes the idle CONTINUOUS rather than a fast loop of a cutting sound."""
    bs=max(1,int(win*sr)); out=x[:]; 
    env=[]
    for i in range(0,len(x),bs):
        blk=x[i:i+bs]; env.append(max(1,max(abs(v) for v in blk)))
    # Smooth only ENOUGH that the gain curve itself has no steps. The first version averaged +/-2 blocks of
    # 12 ms = 60 ms, which is WIDER than the chop being removed -- so it smoothed away its own correction and
    # measured identical to the untouched slice. The window has to be narrower than the thing you are flattening.
    sm=[]
    for i,e in enumerate(env):
        lo=max(0,i-1); hi=min(len(env),i+2); sm.append(sum(env[lo:hi])/(hi-lo))
    tgt=sum(sm)/len(sm)
    for i in range(len(x)):
        g=tgt/sm[min(i//bs,len(sm)-1)]
        out[i]=int(max(-32768,min(32767,x[i]*g)))
    return out

def lowpass(x, sr, fc):
    a=math.exp(-2*math.pi*fc/sr); y=0.0; out=[]
    for v in x:
        y=(1-a)*v+a*y; out.append(int(max(-32768,min(32767,y))))
    return out

def resample(x, ratio):
    """ratio<1 = pitched DOWN and longer."""
    m=int(len(x)/ratio); out=[]
    for i in range(m):
        p=i*ratio; j=int(p); f=p-j
        v = x[j]*(1-f) + x[min(j+1,len(x)-1)]*f
        out.append(int(v))
    return out

def seamless(x, sr, xf=0.045):
    """Crossfade the HEAD with what FOLLOWS the body, so the loop point has no seam."""
    k=int(xf*sr)
    if len(x) < 3*k: return x
    body=x[:-k]
    out=body[:]
    for i in range(k):
        t=i/k
        out[i]=int(body[i]*t + x[len(x)-k+i]*(1-t))
    return out

def peak(x):
    return max(1,max(abs(v) for v in x))

def write(name, l, r, target=11000):
    g=target/max(peak(l),peak(r))   # PEAK-MATCH every candidate so a preference is about character, not volume
    l=[int(max(-32768,min(32767,v*g))) for v in l]; r=[int(max(-32768,min(32767,v*g))) for v in r]
    o=wave.open(OUT+name,'wb'); o.setnchannels(2); o.setsampwidth(2); o.setframerate(sr)
    o.writeframes(struct.pack('<%dh'%(len(l)*2), *[v for pair in zip(l,r) for v in pair])); o.close()
    print(f"{name}  {len(l)/sr:.3f}s")

fl, fr = flatten(bl,sr), flatten(br,sr)
cands = {
 'idle_A_drone.wav':      (resample(fl,0.70), resample(fr,0.70), None),
 'idle_B_deep.wav':       (resample(fl,0.55), resample(fr,0.55), None),
 'idle_C_muffled.wav':    (resample(fl,0.65), resample(fr,0.65), 2600),
 'idle_D_chop.wav':       (resample(bl,0.65), resample(br,0.65), None),
}
for name,(l,r,fc) in cands.items():
    if fc: l,r = lowpass(l,sr,fc), lowpass(r,sr,fc)
    write(name, seamless(l,sr), seamless(r,sr))
