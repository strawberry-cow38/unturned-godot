import struct
d = open(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Level\Objects.dat", "rb").read()
pos = 0
def u8():
    global pos; v = d[pos]; pos += 1; return v
def u16():
    global pos; v = struct.unpack_from("<H", d, pos)[0]; pos += 2; return v
def u32():
    global pos; v = struct.unpack_from("<I", d, pos)[0]; pos += 4; return v
def f():
    global pos; v = struct.unpack_from("<f", d, pos)[0]; pos += 4; return v
def guid():
    global pos; ln = struct.unpack_from("<H", d, pos)[0]; pos += 2; g = d[pos:pos+ln]; pos += ln; return g
version = u8(); avail = u32()
n = 0
for x in range(64):
    for y in range(64):
        count = u16()
        for i in range(count):
            pt = (f(), f(), f())
            save = pos
            raw5 = [struct.unpack_from("<f", d, save + 4*k)[0] for k in range(5)]
            eu = (f(), f(), f()); sc = (f(), f(), f())
            oid = u16(); g = guid(); origin = u8(); inst = u32(); mg = guid(); mi = u32(); cull = u8()
            if n < 12:
                print("obj%d pt=%.1f,%.1f,%.1f  raw[3..7]=[%.4f %.4f %.4f %.4f %.4f]  euRead=%.3f,%.3f,%.3f scRead=%.3f,%.3f,%.3f" % (
                    n, pt[0], pt[1], pt[2], raw5[0], raw5[1], raw5[2], raw5[3], raw5[4], eu[0], eu[1], eu[2], sc[0], sc[1], sc[2]))
            n += 1
print("total", n, "consumed", pos, "/", len(d), "leftover", len(d) - pos)
