#!/usr/bin/env python3
"""Decode an AssetRipper Unity Mesh .asset (YAML) -> OBJ, with Unity->Godot Z-flip."""
import re, sys, struct

FSIZE = {0:4, 1:2, 2:1, 3:1, 4:2, 5:2, 10:4, 11:4, 12:4}  # Unity channel format -> bytes/component

def decode(path, out_obj, flipz=True):
    txt = open(path, encoding='utf-8', errors='replace').read()
    vcount = int(re.search(r'm_VertexCount:\s*(\d+)', txt).group(1))
    idxfmt = int(re.search(r'm_IndexFormat:\s*(\d+)', txt).group(1))
    ibytes = bytes.fromhex(re.search(r'm_IndexBuffer:\s*([0-9a-fA-F]+)', txt).group(1))
    vbytes = bytes.fromhex(re.search(r'_typelessdata:\s*([0-9a-fA-F]+)', txt).group(1))
    chans = [(int(a),int(b),int(c),int(d)) for a,b,c,d in
             re.findall(r'-\s*stream:\s*(\d+)\s*\n\s*offset:\s*(\d+)\s*\n\s*format:\s*(\d+)\s*\n\s*dimension:\s*(\d+)', txt)]
    # Vertex data can be MULTI-STREAM (skinned meshes: pos/normal/tangent in stream 0, UV in stream 1). Unity lays
    # streams out PLANAR -- stream-0 block for all verts, then stream-1 block. Single-stream (regular meshes) is the
    # common case: one block, stride = dataSize/vertexCount.
    total_stride = len(vbytes) // vcount
    streams = sorted(set(st for st, off, fmt, dim in chans if dim > 0)) or [0]
    def chan_stride(s): return max((off + dim * FSIZE[fmt]) for st, off, fmt, dim in chans if st == s and dim > 0)
    strides, starts = {}, {}
    if len(streams) <= 1:
        strides[streams[0]] = total_stride; starts[streams[0]] = 0   # one stream absorbs any hidden/padding bytes
    else:
        higher = sum(chan_stride(s) for s in streams if s != 0)
        strides[0] = total_stride - higher                          # stream 0 = remainder (holds pos + any hidden bone/pad)
        for s in streams:
            if s != 0: strides[s] = chan_stride(s)
        acc = 0
        for s in sorted(streams):
            starts[s] = acc; acc += vcount * strides[s]
    pos = chans[0]
    nrm = chans[1] if len(chans) > 1 and chans[1][3] > 0 and chans[1][2] in (0,1) else None
    uv  = chans[4] if len(chans) > 4 and chans[4][3] > 0 and chans[4][2] in (0,1) else None
    def rd(ch, i, n):   # read n components by semantic (pos/normal=3, uv=2); ignore the parsed dim (can mis-parse)
        st,off,fmt,dim = ch
        code = 'e' if fmt == 1 else 'f'   # 1=Float16 (half), 0=Float32
        return struct.unpack_from('<'+code*n, vbytes, starts.get(st,0) + i*strides.get(st,total_stride) + off)
    verts, norms, uvs = [], [], []
    for i in range(vcount):
        p = rd(pos, i, 3); verts.append((p[0], p[1], -p[2] if flipz else p[2]))
        if nrm: n = rd(nrm, i, 3); norms.append((n[0], n[1], -n[2] if flipz else n[2]))
        if uv:  u = rd(uv, i, 2);  uvs.append((u[0], u[1]))
    n = len(ibytes)//(2 if idxfmt==0 else 4)
    idx = list(struct.unpack('<%d%s' % (n, 'H' if idxfmt==0 else 'I'), ibytes))
    with open(out_obj, 'w') as f:
        for v in verts: f.write("v %.6f %.6f %.6f\n" % v)
        for nn in norms: f.write("vn %.6f %.6f %.6f\n" % nn)
        for u in uvs: f.write("vt %.6f %.6f\n" % u)
        for t in range(0, len(idx)-2, 3):
            a, b, c = idx[t]+1, idx[t+1]+1, idx[t+2]+1
            if flipz: a, c = c, a
            if uvs and norms: f.write("f %d/%d/%d %d/%d/%d %d/%d/%d\n" % (a,a,a,b,b,b,c,c,c))
            elif uvs: f.write("f %d/%d %d/%d %d/%d\n" % (a,a,b,b,c,c))
            else: f.write("f %d %d %d\n" % (a,b,c))
    xs=[v[0] for v in verts]; ys=[v[1] for v in verts]; zs=[v[2] for v in verts]
    bb = (max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))
    print(f"{out_obj}: {vcount}v {len(idx)//3}t streams={len(streams)} uv={bool(uvs)} nrm={bool(norms)} bbox=({bb[0]:.3f},{bb[1]:.3f},{bb[2]:.3f})")
    return bb

if __name__ == '__main__':
    decode(sys.argv[1], sys.argv[2])
