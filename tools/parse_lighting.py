import struct
data = open(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Environment\Lighting.dat", "rb").read()
p = 0
def rb():
    global p; v = data[p]; p += 1; return v
def rs():
    global p; v = struct.unpack_from("<f", data, p)[0]; p += 4; return v
def rc():
    return (rb(), rb(), rb())   # RGB bytes, /255
version = rb()
azimuth = rs(); bias = rs(); fade = rs(); time = rs()
moon = rb()
seaLevel = rs(); snowLevel = rs()
canRain = rb(); canSnow = rb()
rainFreq = rs(); rainDur = rs(); snowFreq = rs(); snowDur = rs()
COLORS = ["SUN", "SEA", "FOG", "SKY_SKY", "SKY_EQUATOR", "SKY_GROUND", "AMBIENT_SKY", "AMBIENT_EQUATOR", "AMBIENT_GROUND", "CLOUDS", "RAYS", "PARTICLE_LIGHTING"]
TIMES = ["DAWN", "MIDDAY", "DUSK", "MIDNIGHT"]
print(f"version={version} azimuth={azimuth:.3f} bias={bias:.3f} fade={fade:.3f} time={time:.3f} moon={moon} sea={seaLevel:.3f} snow={snowLevel:.3f}")
for t in range(4):
    cols = [rc() for _ in range(12)]
    singles = [rs() for _ in range(5)]
    print(f"=== {TIMES[t]} ===")
    for i, c in enumerate(cols):
        print(f"  {COLORS[i]:16s} {c[0]:3d},{c[1]:3d},{c[2]:3d}   ({c[0]/255:.3f},{c[1]/255:.3f},{c[2]/255:.3f})")
    print(f"  singles: {[round(s,3) for s in singles]}")
print(f"consumed {p} / {len(data)} bytes")
