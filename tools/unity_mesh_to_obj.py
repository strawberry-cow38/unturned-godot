#!/usr/bin/env python3
"""unity_mesh_to_obj.py -- convert an AssetRipper-exported Unity YAML mesh (.asset,
class !u!43, serializedVersion 11, m_VertexData/_typelessdata interleaved stream) to a
Wavefront .obj that Godot imports natively.

Part of the Unturned->Godot rip pipeline (converter v0). Unity is left-handed Y-up;
Godot is right-handed Y-up. We negate Z on position+normal and reverse triangle winding
so faces stay outward -- matching the GodotCompat adapter's (x,y,z)->(x,y,-z) convention.

Handles VertexFormat 0=Float32 and 1=Float16. Channel index semantics (Unity):
0=Position 1=Normal 2=Tangent 3=Color 4=UV0 5=UV1 ... dimension 0 => channel absent.
Index buffer: m_IndexFormat 0=UInt16, 1=UInt32 (LE hex).
"""
import sys, struct, re

FMT_SIZE = {0: 4, 1: 2}  # Float32, Float16 (only these appear in Unturned meshes)

def half_to_float(h):
    return struct.unpack('<e', struct.pack('<H', h))[0]

def read_scalar(buf, off, fmt):
    if fmt == 0:
        return struct.unpack_from('<f', buf, off)[0]
    if fmt == 1:
        return half_to_float(struct.unpack_from('<H', buf, off)[0])
    raise ValueError(f"unsupported vertex format {fmt}")

def parse_yaml_mesh(text):
    """Minimal targeted parse -- the file is regular AssetRipper output, not arbitrary YAML."""
    def find(key):
        m = re.search(rf'^\s*{re.escape(key)}:\s*(.*)$', text, re.M)
        return m.group(1).strip() if m else None

    name = find('m_Name') or 'mesh'
    index_format = int(find('m_IndexFormat') or 0)
    vcount = int(re.search(r'm_VertexCount:\s*(\d+)', text).group(1))
    data_size = int(re.search(r'm_DataSize:\s*(\d+)', text).group(1))
    stride = data_size // vcount

    # channels: ordered list under m_Channels
    chan_block = re.search(r'm_Channels:\s*\n(.*?)\n\s*m_DataSize:', text, re.S).group(1)
    channels = []
    for cm in re.finditer(r'-\s*stream:\s*(\d+)\s*\n\s*offset:\s*(\d+)\s*\n\s*format:\s*(\d+)\s*\n\s*dimension:\s*(\d+)', chan_block):
        channels.append(dict(stream=int(cm.group(1)), offset=int(cm.group(2)),
                             format=int(cm.group(3)), dimension=int(cm.group(4))))

    typeless_hex = re.search(r'_typelessdata:\s*([0-9a-fA-F]+)', text).group(1)
    vbuf = bytes.fromhex(typeless_hex)

    ibuf_hex = re.search(r'm_IndexBuffer:\s*([0-9a-fA-F]+)', text).group(1)
    ibuf = bytes.fromhex(ibuf_hex)

    # submeshes
    subs = []
    for sm in re.finditer(r'-\s*serializedVersion:\s*2\s*\n\s*firstByte:\s*(\d+)\s*\n\s*indexCount:\s*(\d+)\s*\n\s*topology:\s*(\d+)\s*\n\s*baseVertex:\s*(\d+)\s*\n\s*firstVertex:\s*(\d+)', text):
        subs.append(dict(firstByte=int(sm.group(1)), indexCount=int(sm.group(2)),
                         topology=int(sm.group(3)), baseVertex=int(sm.group(4)),
                         firstVertex=int(sm.group(5))))
    return dict(name=name, index_format=index_format, vcount=vcount, stride=stride,
                channels=channels, vbuf=vbuf, ibuf=ibuf, subs=subs)

def decode(mesh):
    ch = mesh['channels']
    n = mesh['vcount']
    vbuf = mesh['vbuf']

    # Multi-stream aware: each vertex STREAM is a contiguous block of n*strideS bytes, blocks concatenated in
    # ascending stream order. strideS = max(offset+size) over that stream's active channels. Skinned meshes
    # put positions/normals/uv in stream 0 and bone weights/indices in stream 1 -- so reading stream 0 gives
    # the bind-pose geometry (what we want; animation comes later).
    streams_used = sorted({c['stream'] for c in ch if c['dimension'] > 0})
    if len(streams_used) <= 1:
        # single stream: use the TRUE total stride (m_DataSize/vcount) so padding/alignment is respected
        s0 = streams_used[0] if streams_used else 0
        stream_stride = {s0: (len(vbuf) // n if n else 0)}
        stream_start = {s0: 0}
    else:
        # multi-stream (skinned): each stream is n*strideS bytes, blocks concatenated in stream order
        stream_stride = {}
        for c in ch:
            if c['dimension'] <= 0:
                continue
            size = c['dimension'] * FMT_SIZE.get(c['format'], 4)
            stream_stride[c['stream']] = max(stream_stride.get(c['stream'], 0), c['offset'] + size)
        stream_start, acc = {}, 0
        for s in sorted(stream_stride):
            stream_start[s] = acc
            acc += stream_stride[s] * n

    def chan(i):
        return ch[i] if i < len(ch) and ch[i]['dimension'] > 0 else None
    pos_c, nrm_c, uv_c = chan(0), chan(1), chan(4)

    def read_vec(c, dims, v):
        base = stream_start[c['stream']] + v * stream_stride[c['stream']] + c['offset']
        return tuple(read_scalar(vbuf, base + k * FMT_SIZE[c['format']], c['format']) for k in range(dims))

    positions = [read_vec(pos_c, 3, v) for v in range(n)]
    normals = [read_vec(nrm_c, 3, v) for v in range(n)] if nrm_c else []
    uvs = [read_vec(uv_c, 2, v) for v in range(n)] if uv_c else []

    isize = 2 if mesh['index_format'] == 0 else 4
    ifmt = '<H' if isize == 2 else '<I'
    tris = []
    for s in mesh['subs']:
        start = s['firstByte']
        for t in range(s['indexCount'] // 3):
            a = struct.unpack_from(ifmt, mesh['ibuf'], start + (t*3+0)*isize)[0] + s['baseVertex']
            b = struct.unpack_from(ifmt, mesh['ibuf'], start + (t*3+1)*isize)[0] + s['baseVertex']
            c = struct.unpack_from(ifmt, mesh['ibuf'], start + (t*3+2)*isize)[0] + s['baseVertex']
            tris.append((a, b, c))
    return positions, normals, uvs, tris

def write_obj(path, name, positions, normals, uvs, tris):
    L = [f"# {name}  (Unturned rip -> Godot, Z negated + winding reversed for RH Y-up)"]
    for x, y, z in positions:
        L.append(f"v {x:.6f} {y:.6f} {-z:.6f}")
    for u, v in uvs:
        L.append(f"vt {u:.6f} {v:.6f}")
    for x, y, z in normals:
        L.append(f"vn {x:.6f} {y:.6f} {-z:.6f}")
    have_uv, have_n = bool(uvs), bool(normals)
    for a, b, c in tris:
        # reverse winding (a,c,b) to compensate the Z flip
        def ref(i):
            i1 = i + 1
            if have_uv and have_n: return f"{i1}/{i1}/{i1}"
            if have_n: return f"{i1}//{i1}"
            if have_uv: return f"{i1}/{i1}"
            return f"{i1}"
        L.append(f"f {ref(a)} {ref(c)} {ref(b)}")
    open(path, 'w').write("\n".join(L) + "\n")
    return len(positions), len(tris)

def main():
    src, dst = sys.argv[1], sys.argv[2]
    mesh = parse_yaml_mesh(open(src, encoding='utf-8', errors='replace').read())
    positions, normals, uvs, tris = decode(mesh)
    nv, nt = write_obj(dst, mesh['name'], positions, normals, uvs, tris)
    xs = [p[0] for p in positions]; ys = [p[1] for p in positions]; zs = [p[2] for p in positions]
    print(f"{mesh['name']}: {nv} verts, {nt} tris, {len(mesh['channels'])} channels, stride {mesh['stride']}")
    print(f"  bbox x[{min(xs):.3f},{max(xs):.3f}] y[{min(ys):.3f},{max(ys):.3f}] z[{min(zs):.3f},{max(zs):.3f}]")
    print(f"  normals={bool(normals)} uvs={bool(uvs)} -> {dst}")

if __name__ == '__main__':
    main()
