#!/usr/bin/env python3
"""Validate saved wagon OBJ assets and the SP/MP registrations without Godot.

This is a static check, not a substitute for an in-engine spawn/drive/render.
Uses float32 input, ParseObj's positive one-based indices and its V flip.
"""
import math
from collections import Counter
from pathlib import Path
import re
import struct
import subprocess

from build_wagon import FLOOR_Y
from measure_vehicles import ROOT, CONTENT, read_specs, uncomment, braced, vector, obj


def f32(value):
    return struct.unpack("f", struct.pack("f", float(value)))[0]


def segment_hits(a, b, triangle):
    """Moller-Trumbore intersection; distances bounded to an open segment."""
    sub = lambda x,y: tuple(u-v for u,v in zip(x,y))
    dot = lambda x,y: sum(u*v for u,v in zip(x,y))
    cross = lambda x,y: (x[1]*y[2]-x[2]*y[1],x[2]*y[0]-x[0]*y[2],x[0]*y[1]-x[1]*y[0])
    p,q,r = triangle
    d,e1,e2 = sub(b,a),sub(q,p),sub(r,p)
    h = cross(d,e2)
    det = dot(e1,h)
    if abs(det)<1e-10:
        return False
    s = sub(a,p)
    u = dot(s,h)/det
    v = dot(d,cross(s,e1))/det
    t = dot(e2,cross(s,e1))/det
    return 0 <= u <= 1 and 0 <= v and u+v <= 1 and 0 < t < 1


def validate(path):
    vertices, uvs, normals, faces = [], [], [], []
    for line_number, raw in enumerate(path.read_text().split("\n"), 1):
        line = raw.rstrip("\r")
        if not line or line[0] == "#":
            continue
        t = [p for p in line.split(" ") if p]
        if not t:
            continue
        if t[0] == "v":
            vertices.append(tuple(f32(x) for x in t[1:4]))
        elif t[0] == "vt":
            uvs.append((f32(t[1]), f32(1-f32(t[2]))))
        elif t[0] == "vn":
            normals.append(tuple(f32(x) for x in t[1:4]))
        elif t[0] == "f":
            assert len(t) == 4, (path.name, line_number, "not exactly three corners")
            corners = []
            for c in t[1:4]:
                p = c.split("/")
                assert len(p) == 3 and all(p), (path.name, line_number, "missing vt/vn")
                corners.append(tuple(int(i)-1 for i in p))
            faces.append(corners)
    assert vertices and uvs and normals and faces, (path.name, "missing records")
    assert all(math.isfinite(x) for data in (vertices,uvs,normals) for v in data for x in v)
    assert {c[0] for face in faces for c in face} == set(range(len(vertices))), (path.name, "unused vertices")
    assert len({tuple(sorted(vertices[c[0]] for c in face)) for face in faces}) == len(faces), (path.name, "duplicate triangles")
    min_area = math.inf
    for face in faces:
        for vi,ti,ni in face:
            assert 0 <= vi < len(vertices) and 0 <= ti < len(uvs) and 0 <= ni < len(normals), (path.name, face, "index out of range")
            assert all(0 <= x <= 1 for x in uvs[ti]), (path.name, "UV outside palette")
            assert abs(sum(x*x for x in normals[ni])-1) < 0.000003, (path.name, "nonunit normal")
        a,b,c = (vertices[p[0]] for p in face)
        e1,e2 = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
        cross = (e1[1]*e2[2]-e1[2]*e2[1], e1[2]*e2[0]-e1[0]*e2[2], e1[0]*e2[1]-e1[1]*e2[0])
        area2 = math.sqrt(sum(x*x for x in cross))
        assert area2 > 1e-12, (path.name, face, "zero-area triangle")
        min_area = min(min_area, area2/2)
        # Authored faces are flat-shaded. Translated sedan seats/wheel retain
        # the donor's smooth normals, checked against the donor below.
        for p in face:
            agreement = sum(x*y for x,y in zip(cross,normals[p[2]]))/area2
            threshold = 0 if path.name in ('wagon_seats.txt','wagon_steer.txt') else 0.99999
            assert agreement > threshold, (path.name, face, agreement, "normal disagrees with winding")
    lo = tuple(min(v[i] for v in vertices) for i in range(3))
    hi = tuple(max(v[i] for v in vertices) for i in range(3))
    print(f"PASS {path.name}: {len(vertices)} v, {len(faces)} triangles; minimum area {min_area:.12g} m²")
    return lo,hi


def welded_edges(mesh):
    """Count geometric edges after welding positions, ignoring flat-normal/UV seams."""
    positions = [tuple(f32(x) for x in v) for v in mesh['vertices']]
    edges = Counter()
    for face in mesh['faces']:
        points = [positions[int(c.split('/')[0])-1] for c in face]
        assert len(points) == 3
        for a,b in zip(points, points[1:]+points[:1]):
            edges[tuple(sorted((a,b)))] += 1
    return len(set(positions)), edges


def surface_audit(mesh):
    """Exhaustive saved-triangle audit, including overlaps edge counts miss."""
    sub = lambda a,b: tuple(x-y for x,y in zip(a,b))
    dot = lambda a,b: sum(x*y for x,y in zip(a,b))
    cross = lambda a,b: (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
    tris = [tuple(tuple(f32(x) for x in mesh['vertices'][int(c.split('/')[0])-1]) for c in f) for f in mesh['faces']]
    oriented = Counter((a,b) for t in tris for a,b in zip(t,t[1:]+t[:1]))
    assert all(n==1 and oriented[b,a]==1 for (a,b),n in oriented.items()), 'inconsistent shell winding'
    graph = {}
    for a,b in oriented:
        graph.setdefault(a,set()).add(b)
    seen=set(); pending=[next(iter(graph))]
    while pending:
        p=pending.pop()
        if p not in seen:
            seen.add(p); pending.extend(graph[p]-seen)
    assert len(seen)==len(graph), 'disconnected or enclosed stray component'
    volume = sum(dot(a,cross(b,c))/6 for a,b,c in tris)
    assert volume>0, ('inside-out shell',volume)
    assert len(mesh['vertices'])==len(graph), 'unnecessary duplicate position records'

    def clipped_area(a,b,normal):
        # Sutherland-Hodgman triangle clipping in the dominant projection.
        axis=max(range(3),key=lambda i:abs(normal[i])); axes=[i for i in range(3) if i!=axis]
        a=[tuple(p[i] for i in axes) for p in a]; b=[tuple(p[i] for i in axes) for p in b]
        def side(p,q,r):
            return (q[0]-p[0])*(r[1]-p[1])-(q[1]-p[1])*(r[0]-p[0])
        orient=1 if side(*b)>0 else -1
        for p,q in zip(b,b[1:]+b[:1]):
            result=[]
            for u,v in zip(a,a[1:]+a[:1]):
                du,dv=orient*side(p,q,u),orient*side(p,q,v)
                if du>=0:
                    result.append(u)
                if (du>0 and dv<0) or (du<0 and dv>0):
                    t=du/(du-dv)
                    result.append(tuple(u[i]+t*(v[i]-u[i]) for i in range(2)))
            a=result
            if not a:
                return 0
        return abs(sum(p[0]*q[1]-p[1]*q[0] for p,q in zip(a,a[1:]+a[:1])))/2

    def pierces(a,b,tri):
        p,q,r=tri; d=sub(b,a); e1,e2=sub(q,p),sub(r,p)
        h=cross(d,e2); det=dot(e1,h)
        if abs(det)<1e-12:
            return False
        s=sub(a,p); u=dot(s,h)/det; v=dot(d,cross(s,e1))/det; t=dot(e2,cross(s,e1))/det
        return 1e-6<t<1-1e-6 and u>1e-6 and v>1e-6 and u+v<1-1e-6

    boxes=[(tuple(min(p[i] for p in t) for i in range(3)),tuple(max(p[i] for p in t) for i in range(3))) for t in tris]
    normals=[]
    for a,b,c in tris:
        n=cross(sub(b,a),sub(c,a)); length=math.sqrt(dot(n,n))
        normals.append(tuple(x/length for x in n))
    pairs=0
    for i,a in enumerate(tris):
        for j in range(i):
            b=tris[j]
            if any(boxes[i][0][k]>boxes[j][1][k]+2e-6 or boxes[j][0][k]>boxes[i][1][k]+2e-6 for k in range(3)):
                continue
            pairs+=1
            if abs(dot(normals[i],normals[j]))>1-1e-8 and all(abs(dot(sub(p,a[0]),normals[i]))<2e-6 for p in b):
                assert clipped_area(a,b,normals[i])<1e-9, ('overlapping coplanar triangles',i,j,a,b)
            else:
                assert not any(pierces(u,v,b) for u,v in zip(a,a[1:]+a[:1])), ('intersecting triangles',i,j)
                assert not any(pierces(u,v,a) for u,v in zip(b,b[1:]+b[:1])), ('intersecting triangles',i,j)
    print(f'PASS single connected, outward shell; no coplanar overlaps or triangle piercings ({pairs} candidate pairs); signed volume {volume:.6f} m³')


def check_hood(sedan, wagon):
    # Independently read the saved assets, rather than trusting generator output.
    sv=sorted(set(sedan['vertices'])); wv=sorted(set(wagon['vertices']))
    def station(vs, y, x):
        ps=[p for p in vs if p[2]<-1.1 and abs(abs(p[0])-x)<.02 and abs(p[1]-y)<2e-6]
        assert ps, ('missing hood station',y,x)
        z=min(p[2] for p in ps); ps=[p for p in ps if abs(p[2]-z)<.00002]
        return tuple(sum(p[i] for p in ps)/len(ps) for i in (1,2))
    sn=station(sv,.999953,.98096); sc=station(sv,1.125,.98096)
    wn=station(wv,.999953,.98); wc=station(wv,1.125,.98)
    scale=1.34/1.389812
    assert abs(sn[0]-wn[0])<2e-6
    assert abs((-1.56+(sn[1]+1.62)*scale)-wn[1])<2e-6
    assert wc == (1.125,-1.25), ('hood must meet windscreen directly',wc)
    levels=(.125,.875,.999953,1.125)  # retain hood/valance, not the deleted bumper lip
    print('| front Y station | sedan | wagon |')
    print('| --- | ---: | ---: |')
    for y in levels:
        found=[]
        for vs in (sv,wv):
            vals=[p[1] for p in vs if p[2]<=-1.25 and abs(p[1]-y)<.0005]
            assert vals, ('missing front Y level',y)
            found.append(min(vals,key=lambda value:abs(value-y)))
        assert abs(found[0]-found[1])<2e-6,(y,found)
        print(f'| {y:.3f} | {found[0]:.6f} | {found[1]:.6f} |')
    print(f'PASS sedan hood: centre (Y,Z) {sn} → {sc}; wagon {wn} → {wc}; nose Z scale {scale:.9f}, rear extended to glass')


def check_straight_sides(mesh):
    """Intersect saved triangles with Z planes, including between vertex stations."""
    tris = [tuple(tuple(f32(x) for x in mesh['vertices'][int(c.split('/')[0])-1])
                  for c in face) for face in mesh['faces']]

    def width(z):
        xs = []
        for tri in tris:
            for a,b in zip(tri,tri[1:]+tri[:1]):
                if abs(a[2]-z) < 1e-6:
                    xs.append(a[0])
                if min(a[2],b[2]) < z < max(a[2],b[2]):
                    t = (z-a[2])/(b[2]-a[2])
                    xs.append(a[0]+t*(b[0]-a[0]))
        assert xs, ('empty body section',z)
        return max(xs)-min(xs)

    # Include every change in the mesh and the spans between them, in addition
    # to the review's displayed stations. Sparse vertex bands can miss panels.
    stations = sorted({p[2] for tri in tris for p in tri})
    probes = stations + [(a+b)/2 for a,b in zip(stations,stations[1:])]
    for z in probes:
        assert abs(width(z)-2.52) < 1e-6, ('body width varies',z,width(z))
    print('| Z (m) | body X extent (m) |')
    print('| ---: | ---: |')
    for i in range(27):
        z = stations[0]+i*(stations[-1]-stations[0])/26
        w = width(z)
        assert abs(w-2.52) < 1e-6, ('review width station',z,w)
        print(f'| {z:+.2f} | {w:.3f} |')

    # Full extent alone would allow inset doors below a wide roof. Check every
    # exterior side face as well, then probe the four actual pillar surfaces.
    sides = 0
    for face,tri in zip(mesh['faces'],tris):
        n = mesh['normals'][int(face[0].split('/')[2])-1]
        if abs(n[0]) > .99999 and all(n[0]*p[0] > 1.0 for p in tri):
            assert all(abs(abs(p[0])-1.26) < 1e-6 for p in tri), ('inset outer wall/post',tri)
            sides += 1
    assert sides > 0
    for sign in (-1,1):
        for label,z in (('A',-.91),('B',.245),('C',1.45),('D',2.52)):
            assert any(segment_hits((sign*1.259,1.5,z),(sign*1.261,1.5,z),t) for t in tris), ('pillar off wall',label,sign)
    # The gate itself must fill the rear at full width, even if the bumper
    # would hide a boot-lid notch from the plan-envelope measurement.
    for x in (-1.25,-1.10,-.5,0,.5,1.10,1.25):
        for y in (.3,.7,1.05):
            assert any(segment_hits((x,y,2.679),(x,y,2.681),t) for t in tris), ('recessed/missing tailgate',x,y)
    for path in sorted(CONTENT.glob('wagon_glass_[lr]_*.txt')):
        assert all(abs(abs(p[0])-1.256) < 1e-6 for p in obj(path)['vertices']), ('side glass off wall',path)
    print(f'PASS constant width at {len(probes)} vertex/mid-span sections plus 27 review stations; '
          f'{sides} flush side triangles, all eight posts, 21 full-width gate probes, six side panes')


def check_roof_rake(mesh):
    glass = obj(CONTENT/'wagon_glass_windshield.txt')
    yz = sorted({p[1:] for p in glass['vertices']})
    assert yz == [(1.125,-1.25),(1.92,-.8)], ('windscreen rake/layout changed',yz)
    (y0,z0),(y1,z1) = yz
    slope = (z1-z0)/(y1-y0)
    expected_top = z1+(2.17-y1)*slope
    # Find the roof's forward-facing triangles independently of their index.
    front, posts = [], []
    for face in mesh['faces']:
        tri = [mesh['vertices'][int(c.split('/')[0])-1] for c in face]
        n = mesh['normals'][int(face[0].split('/')[2])-1]
        if all(p[1] >= .99 and p[2] < 0 for p in tri) and n[2] < -.5 and abs(n[0]) < 1e-5:
            (front if all(p[1] >= 1.92 for p in tri) else posts).append(tri)
            for x,y,z in tri:
                assert abs(z-(z0+(y-y0)*slope)) < 1e-6, ('roof front breaks A-pillar plane',x,y,z)
            assert sum(a*b for a,b in zip(n,glass['normals'][0])) > .999999, ('roof/glass normal mismatch',n)
    assert front, 'missing slanted roof front'
    assert len(posts) == 4, ('missing A-pillar forward faces',len(posts))
    corners = {p for tri in front for p in tri}
    for x in (-1.26,1.26):
        for y,z in ((1.92,-.8),(2.17,round(expected_top,6))):
            assert (x,y,z) in corners, ('roof edge endpoint',x,y,z)
    print(f'PASS roof front coplanar with unchanged windscreen/A-post: top Z {expected_top:.6f}; '
          f'rake {math.degrees(math.atan(slope)):.6f}° from vertical')
    rear = obj(CONTENT/'wagon_glass_rear.txt')
    yz = sorted({p[1:] for p in rear['vertices']})
    assert yz == [(1.1,2.68),(1.92,2.56)], 'tailgate rake changed'
    (y0,z0),(y1,z1) = yz
    slope = (z1-z0)/(y1-y0)
    expected_top = z1+(2.17-y1)*slope
    cap,posts = [],[]
    for face in mesh['faces']:
        tri = [mesh['vertices'][int(c.split('/')[0])-1] for c in face]
        n = mesh['normals'][int(face[0].split('/')[2])-1]
        if all(p[1] >= 1.1 and p[2] > 2.5 for p in tri) and n[2] > .5 and abs(n[0]) < 1e-5:
            (cap if all(p[1] >= 1.92 for p in tri) else posts).append(tri)
            assert all(abs(z-(z0+(y-y0)*slope)) < 1e-6 for x,y,z in tri), ('roof rear breaks D-pillar plane',tri)
            assert sum(a*b for a,b in zip(n,rear['normals'][0])) > .999999
    assert cap and len(posts) == 4, 'missing rear roof cap or D-pillar faces'
    corners = {p for tri in cap for p in tri}
    for x in (-1.26,1.26):
        for y,z in ((1.92,2.56),(2.17,round(expected_top,6))):
            assert (x,y,z) in corners, ('rear roof endpoint',x,y,z)
    print(f'PASS rear roof continues unchanged D-pillar/glass plane: top Z {expected_top:.9f}; '
          f'rake {math.degrees(math.atan(slope)):.6f}° from vertical')


def junction_faces(mesh):
    """All triangles intersecting either review box, not just their centroids."""
    rows = []
    for sign in (-1,1):
        lo = (-1.35 if sign < 0 else .85, .80, -2.90)
        hi = (-.85 if sign < 0 else 1.35, 1.30, -2.10)
        for face_id,face in enumerate(mesh['faces']):
            tri = tuple(tuple(f32(x) for x in mesh['vertices'][int(c.split('/')[0])-1]) for c in face)
            clipped = list(tri)
            # Clip the actual polygon against all six box planes. This also
            # includes triangles crossing the box with no vertex inside it.
            for axis in range(3):
                for bound,direction in ((lo[axis],1),(hi[axis],-1)):
                    result = []
                    for a,b in zip(clipped,clipped[1:]+clipped[:1]):
                        da,db = direction*(a[axis]-bound),direction*(b[axis]-bound)
                        if da >= 0:
                            result.append(a)
                        if (da < 0 < db) or (db < 0 < da):
                            t = da/(da-db)
                            result.append(tuple(a[i]+t*(b[i]-a[i]) for i in range(3)))
                    clipped = result
            if not clipped:
                continue
            normal = mesh['normals'][int(face[0].split('/')[2])-1]
            centroid = tuple(sum(p[i] for p in tri)/3 for i in range(3))
            inside = all(lo[i] <= centroid[i] <= hi[i] for i in range(3))
            rows.append((sign,face_id,tri,normal,inside))
    return rows


def check_cowl_junction(mesh):
    """Measure continuous fascia trim, planar bevels and the shelf-free join."""
    triangles = []
    fascia = []
    for face_id,face in enumerate(mesh['faces']):
        tri = tuple(tuple(f32(x) for x in mesh['vertices'][int(c.split('/')[0])-1]) for c in face)
        n = mesh['normals'][int(face[0].split('/')[2])-1]
        triangles.append((face_id,tri,n))
        if n[2] < -.99 and all(p[2] < -2.7 and p[1] >= .125-1e-6 for p in tri):
            fascia.append((face_id,tri,n))
    assert fascia, 'missing front fascia'
    yn = max(p[1] for _,tri,_ in fascia for p in tri)
    zn = min(p[2] for _,tri,_ in fascia for p in tri if abs(p[1]-yn) < 1e-6)
    yv = min(p[1] for _,tri,_ in fascia for p in tri)
    zv = min(p[2] for _,tri,_ in fascia for p in tri)

    def nose_at(x):
        points = []
        for _,tri,_ in fascia:
            for a,b in zip(tri,tri[1:]+tri[:1]):
                if abs(a[0]-x) < 1e-6:
                    points.append(a)
                if min(a[0],b[0]) < x < max(a[0],b[0]):
                    t = (x-a[0])/(b[0]-a[0])
                    points.append(tuple(a[i]+t*(b[i]-a[i]) for i in range(3)))
        assert points, ('missing nose section',x)
        return max(points,key=lambda p:p[1])

    xs = sorted({p[0] for _,tri,_ in fascia for p in tri})
    # Include both sides of the break, the whole strip, and all mesh stations.
    probes = xs + [(a+b)/2 for a,b in zip(xs,xs[1:])]
    probes += [sign*(.98+.28*i/100) for sign in (-1,1) for i in range(101)]
    for x in probes:
        _,y,z = nose_at(x)
        expected_y = yn-.125*max(0,(abs(x)-.98)/.28)
        expected_z = zv+(zn-zv)*(expected_y-yv)/(yn-yv)
        assert abs(y-expected_y) < 1e-6 and abs(z-expected_z) < 1e-6, ('nose top edge steps',x,y,z,expected_y,expected_z)
    for face_id,tri,n in fascia:
        assert abs(n[0]) < 1e-5, ('twisted fascia normal',face_id,n)
        for x,y,z in tri:
            expected = zv+(zn-zv)*(y-yv)/(yn-yv)
            assert abs(z-expected) < 1e-6, ('nonplanar fascia wedge',face_id,(x,y,z),expected)
    print('| nose X | top Y | top Z |')
    print('| ---: | ---: | ---: |')
    for x in (-1.26,-.98,-.48,0,.48,.98,1.26):
        _,y,z = nose_at(x)
        print(f'| {x:+.2f} | {y:.6f} | {z:.6f} |')
    print(f'PASS one fascia plane ({len(fascia)} triangles); continuous bevel top edge across {len(probes)} sections')

    rear_y,rear_z = 1.125,-1.25
    hood_slope = (rear_y-yn)/(rear_z-zn)
    outer_y = yn-.125
    outer_z = zv+(zn-zv)*(outer_y-yv)/(yn-yv)
    bevel_slope = (yn+hood_slope*(outer_z-zn)-outer_y)/.28
    hood_count = 0
    strips = {-1: [], 1: []}
    shelf = []
    for face_id,tri,n in triangles:
        if n[1] <= .5 or not all(p[1] >= .8 and zn-.01 <= p[2] <= rear_z+1e-6 for p in tri):
            continue
        if n[1] > .999999:
            shelf.append(face_id)
        for x,y,z in tri:
            expected = yn+hood_slope*(z-zn)-bevel_slope*max(0,abs(x)-.98)
            assert abs(y-expected) < 1e-6, ('hood/bevel plane mismatch',face_id,(x,y,z),expected)
        if all(abs(p[0]) <= .98+1e-6 for p in tri):
            hood_count += 1
        else:
            sign = -1 if tri[0][0] < 0 else 1
            assert all(sign*p[0] >= .98-1e-6 for p in tri), ('triangle crosses hood crease',face_id)
            strips[sign].append((face_id,tri,n))
    assert hood_count and all(strips.values()), 'missing centre hood or outer strips'
    assert not shelf, ('horizontal hood shelf remains',shelf)
    # Each strip must have area spanning the entire nose-to-A-post quadrilateral.
    # Its rear edge is in the same windscreen plane as the inner hood endpoint.
    screen_slope = (1.92-rear_y)/(-.8-rear_z)
    for sign,faces in strips.items():
        corners = {p for _,tri,_ in faces for p in tri}
        rear = [p for p in corners if p[2] > -1.5]
        assert any(abs(abs(p[0])-1.26)<1e-6 for p in rear), ('bevel stops short of wall',sign)
        assert any(abs(p[0]-sign*.98)<1e-6 and abs(p[2]-rear_z)<1e-6 for p in rear)
        for x,y,z in rear:
            assert abs(y-(rear_y+screen_slope*(z-rear_z))) < 2e-6, ('bevel does not meet A-post plane',sign,(x,y,z))
        expected_n = (sign*bevel_slope,1,-hood_slope)
        length = math.sqrt(sum(v*v for v in expected_n))
        expected_n = tuple(v/length for v in expected_n)
        for face_id,_,n in faces:
            assert sum(a*b for a,b in zip(n,expected_n)) > .999999, ('nonplanar bevel',face_id,n)
        tilt = math.degrees(math.acos(expected_n[1]))
        print(f'PASS {"left" if sign<0 else "right"} planar outer strip: {len(faces)} triangles, tilt {tilt:.6f}°, normal {expected_n}; full width to A-post plane')
    print(f'PASS centre hood ({hood_count} triangles, tilt {math.degrees(math.atan(hood_slope)):.6f}°) reaches windscreen; horizontal shelf faces {len(shelf)}')

    rows = junction_faces(mesh)
    print('Review boxes: X [-1.35,-0.85] / [0.85,1.35], Y [0.80,1.30], Z [-2.90,-2.10]; zero-based face IDs')
    print('| side | face | centroid in box | normal | all three vertices |')
    print('| --- | ---: | --- | --- | --- |')
    def fmt_point(p):
        return '('+', '.join(f'{v:.6f}' for v in p)+')'
    for sign,face_id,tri,n,inside in rows:
        print(f"| {'left' if sign < 0 else 'right'} | f{face_id} | {'yes' if inside else 'no'} | {fmt_point(n)} | {'; '.join(fmt_point(p) for p in tri)} |")
    for sign in (-1,1):
        selected = [r for r in rows if r[0] == sign and r[4]]
        assert selected, ('empty review box',sign)
        assert not any(-r[3][2] > max(abs(r[3][0]),abs(r[3][1])) for r in selected), ('forward-facing wedge in centroid box',sign)
        assert sum(r[3][1] > .5 for r in selected) == 1, ('expected one hood surface in centroid box',sign)
    forward = [r for r in rows if -r[3][2] > max(abs(r[3][0]),abs(r[3][1]))]
    print(f'PASS {len(rows)} intersecting faces listed; forward-facing centroid count 0; '
          f'forward-facing intersection count {len(forward)} (ordinary coplanar fascia, all listed)')


def check_stripped_shell(mesh):
    triangles = [[mesh['vertices'][int(c.split('/')[0])-1] for c in f] for f in mesh['faces']]
    # Every sub-sill corner is now on the floor, including the former lip sites.
    # EXTERIOR flank only. The interior passenger floor legitimately sits at -0.120, above the outer
    # sill; the old form of this check excluded it by testing y < -.12 against a -0.270 floor, which
    # worked only because -0.120 is not strictly less than -0.12 -- it would have started failing the
    # moment the floor moved. Scope it to the outer wall, which is what a sub-sill lip means.
    outer_low = [(x,y,z) for x,y,z in mesh['vertices'] if abs(x) >= 1.2 and y < 0]
    assert outer_low and all(abs(y-FLOOR_Y) < 1e-9 for x,y,z in outer_low), \
        ('sub-sill lip/bevel remains', sorted({round(y,4) for _,y,_ in outer_low}))
    assert not any(abs(y+.158647)<.00001 or abs(y+.125)<.00001 for x,y,z in mesh['vertices'])
    floor_area = 0
    floor_corners = set()
    fascia_count = gate_count = 0
    zn = min(z for x,y,z in mesh['vertices'] if y == .999953)
    zv = min(z for x,y,z in mesh['vertices'] if y == .125)
    for f,tri in zip(mesh['faces'],triangles):
        n = mesh['normals'][int(f[0].split('/')[2])-1]
        if n[1] < 0 and any(p[1] < FLOOR_Y+1e-9 for p in tri):
            assert all(abs(p[1]-FLOOR_Y) < 1e-9 for p in tri) and n == (0,-1,0), ('sloping underside',tri,n)
            a,b,c = tri
            floor_area += abs((b[0]-a[0])*(c[2]-a[2])-(b[2]-a[2])*(c[0]-a[0]))/2
            floor_corners.update(tri)
        if n[2] < -.99 and all(p[2] < -2.7 for p in tri):
            fascia_count += 1
            assert all(abs(z-(zv+(zn-zv)*(y-.125)/(.999953-.125)))<1e-6 for x,y,z in tri)
            assert all(mesh['uvs'][int(c.split('/')[1])-1] == (.125,.25) for c in f), 'grille/material patch remains'
        if all(p[2] >= 2.67 and p[1] <= 1.10 for p in tri) and n[2] > .9:
            gate_count += 1
            assert all(p[2] == 2.68 for p in tri), 'rear bumper/handle remains in body'
            assert all(mesh['uvs'][int(c.split('/')[1])-1] == (.125,.25) for c in f), 'tailgate material patch remains'
    assert fascia_count and gate_count
    assert abs(floor_area-2.52*(mesh['hi'][2]-mesh['lo'][2])) < 1e-6, ('incomplete flat floor',floor_area)
    for x in (-1.26,1.26):
        for z in (mesh['lo'][2],2.68):
            assert (x,FLOOR_Y,z) in floor_corners
    for x in (-1.259,-1.12,-.99,0,.99,1.12,1.259):
        for z in (-2.8,-2.5,-1.56,0,1.46,2.65):
            assert any(segment_hits((x,FLOOR_Y-.001,z),(x,FLOOR_Y+.001,z),tri) for tri in triangles), ('floor gap',x,z)
    print(f'PASS painted fascia/tailgate, no body bumpers/latch/lip; full-width flat floor {floor_area:.6f} m² at Y {FLOOR_Y:.3f}')


def check_bumper_height(wagon):
    floor = wagon['mesh']['lo'][1]
    for end in ('front','rear'):
        lip = obj(CONTENT/f'wagon_bumper_{end}.txt')['lo'][1]
        assert abs(lip-floor)<1e-6, ('bumper lip must sit FLUSH with the floor, as on the sedan/hatchback/golf outer flank',end,lip,floor)
    print(f'PASS both bumper lips flush with the floor at {floor:.6f} -- the sedan/hatchback/golf outer-flank convention')


def check_cabin_fit(wagon, sedan):
    body = wagon['mesh']
    firewall = []
    for f in body['faces']:
        ps = [body['vertices'][int(c.split('/')[0])-1] for c in f]
        n = body['normals'][int(f[0].split('/')[2])-1]
        if n[2] > .99999 and all(z<0 and abs(x)<=.980001 for x,y,z in ps):
            firewall.extend(p[2] for p in ps)
    assert firewall and max(firewall)-min(firewall)<1e-6, 'missing cabin-front plane'
    plane = min(firewall)
    wheel_name = next(p for p in wagon['Parts'] if '_steer.txt' in p)
    wheel = obj(CONTENT/wheel_name)
    poke = plane-wheel['lo'][2]
    assert .11<=poke<=.13, ('wheel poke-through outside fleet 0.11..0.13',poke)
    reach = wagon['Seats'][0][2]-(wheel['lo'][2]+wheel['hi'][2])/2
    assert .79<=reach<=.83, ('wheel-to-driver-seat reach outside fleet 0.79..0.83',reach)

    seat_name = next(p for p in wagon['Parts'] if '_seats.txt' in p)
    seats = obj(CONTENT/seat_name)
    source = obj(CONTENT/'sedan_seats.txt')
    assert len(wagon['Seats']) == len(sedan['Seats']) == 4
    assert len(seats['vertices']) == len(source['vertices'])
    for v,s in zip(seats['vertices'],source['vertices']):
        row = 0 if s[2]<0 else 2
        seat = row+(1 if s[0]>0 else 0)
        delta = tuple(w-d for w,d in zip(wagon['Seats'][seat],sedan['Seats'][seat]))
        assert all(abs(v[i]-s[i]-delta[i])<1e-6 for i in range(3)), ('seat mesh disagrees with seat table',seat,v,s,delta)
    for i,(w,s) in enumerate(zip(wagon['Seats'],sedan['Seats'])):
        assert all(abs(w[k]-s[k]-(.205 if i<2 and k==2 else 0))<1e-6 for k in range(3)), ('seat table translation',i)
    assert seat_name == 'wagon_seats.txt' and wheel_name == 'wagon_steer.txt', 'wagon must own its interior meshes'
    for field in ('faces','uvs','normals'):
        assert seats[field] == source[field], ('seat donor data changed',field)
    front = [w for w,s in zip(seats['vertices'],source['vertices']) if s[2]<0]
    rear = [w for w,s in zip(seats['vertices'],source['vertices']) if s[2]>0]
    front_max = max(p[2] for p in front)
    rear_min, rear_max = min(p[2] for p in rear), max(p[2] for p in rear)
    # Disjoint Z bounds prove the rows cannot intersect, independent of X/Y.
    step = min(z for x,y,z in body['vertices'] if y==.18 and abs(x)<=.98)
    assert front_max < rear_min and front_max < step and rear_max < step, 'seats intersect rear row or load-floor step'
    donor_wheel = obj(CONTENT/'sedan_steer.txt')
    assert len(wheel['vertices']) == len(donor_wheel['vertices'])
    for w,s in zip(wheel['vertices'],donor_wheel['vertices']):
        assert all(abs(w[i]-s[i]-(.205 if i==2 else 0))<1e-6 for i in range(3)), 'steering mesh must translate +0.205 Z'
    for field in ('faces','uvs','normals'):
        assert wheel[field] == donor_wheel[field], ('wheel donor data changed',field)
    for field in ('SteerPivot','SteerAxis'):
        w,s = (vector(spec['fields'][field]) for spec in (wagon,sedan))
        assert all(abs(w[i]-s[i]-(.205 if field=='SteerPivot' and i==2 else 0))<1e-6 for i in range(3)), ('steering pivot/axis',field)
    print(f'PASS wheel poke-through {poke:.6f}, driver reach {reach:.6f}; front seat mesh/table agree +0.205 Z')
    print(f'PASS seat row gap {rear_min-front_max:.6f}; front/rear clearance to load step {step-front_max:.6f}/{step-rear_max:.6f}')


def check_exhaust(wagon):
    assert 'wagon_exhaust.txt' in wagon['Parts'], 'exhaust missing from Parts'
    mesh = obj(CONTENT/'wagon_exhaust.txt')
    box, center = wagon['BoxSize'], wagon['BoxCenter']
    formula = (box[0]/2-.3, max(.22,center[1]-box[1]/2+.18), center[2]+box[2]/2-.05)
    assert all(abs(a-b)<1e-6 for a,b in zip(formula,(.95,.28,2.649))), ('default exhaust point',formula)
    tip = ((mesh['lo'][0]+mesh['hi'][0])/2, (mesh['lo'][1]+mesh['hi'][1])/2, mesh['hi'][2])
    emitter = vector(wagon['fields']['ExhaustPos'])
    assert all(abs(a-b)<1e-6 for a,b in zip(tip,emitter)), ('smoke must leave pipe tip',tip,emitter)
    assert len(mesh['faces']) == 28
    # IT MUST READ AS A TUBE, NOT A BULB. The first pipe was 0.120 across and stood 0.050 proud of the
    # valance -- wider than it was long, and strawberry called it "the sphere it added as an exhaust".
    # Assert the proportion, which is the property that failed, rather than a tip coordinate that says
    # nothing about how it looks.
    bore = max(mesh['hi'][0]-mesh['lo'][0], mesh['hi'][1]-mesh['lo'][1])
    proud = tip[2]-wagon['mesh']['hi'][2]
    assert proud > bore, ('tailpipe is wider than it is proud -- reads as a bulb', proud, bore)
    assert proud >= .12, ('tailpipe barely clears the valance', proud)
    body_tris = [[wagon['mesh']['vertices'][int(c.split('/')[0])-1] for c in f] for f in wagon['mesh']['faces']]
    pipe_tris = [[mesh['vertices'][int(c.split('/')[0])-1] for c in f] for f in mesh['faces']]
    # Probe the mouth and smoke path; the decorative recess is 40 mm inboard.
    for dx,dy in ((0,0),(-.02,0),(.02,0),(0,-.02),(0,.02)):
        a = (tip[0]+dx,tip[1]+dy,tip[2]-.049)
        b = (a[0],a[1],tip[2]+.3)
        assert not any(segment_hits(a,b,t) for t in body_tris+pipe_tris), 'blocked pipe mouth/smoke path'
    print(f'PASS 28-triangle exhaust: formula {formula}; tip/emitter {tip}; bore {bore:.3f} vs {proud:.3f} proud of the valance ({proud/bore:.2f}:1, so a tube not a bulb); clear outlet')


def check_donor_parts(wagon, sedan):
    for lamp,field,delta in (('headlights','SpotPos',.150),('taillights','TailPos',.012)):
        source = obj(CONTENT/f'sedan_{lamp}.txt')
        part = obj(CONTENT/f'wagon_{lamp}.txt')
        assert len(part['faces']) == len(source['faces']) == 20
        # Check every corner and UV, not merely the same bounding box.
        for sf,pf in zip(source['faces'],part['faces']):
            for sc,pc in zip(sf,pf):
                sv,st,_ = (int(i)-1 for i in sc.split('/'))
                pv,pt,_ = (int(i)-1 for i in pc.split('/'))
                assert all(abs(part['vertices'][pv][i]-source['vertices'][sv][i]-(delta if i==2 else 0))<1e-6 for i in range(3)), 'lamp is not the original sedan translation'
                assert all(abs(a-b)<1e-6 for a,b in zip(part['uvs'][pt],source['uvs'][st])), 'donor lamp UV changed'
        assert len(wagon[field]) == len(sedan[field]) == 2
        positions = list(zip(wagon[field],sedan[field]))
        if lamp == 'headlights':
            positions.append((wagon['OmniPos'],sedan['OmniPos']))
        for w,g in positions:
            assert all(abs(w[i]-g[i]-(delta if i==2 else 0))<1e-6 for i in range(3)), ('emitter not translated with sedan lens',field)
        sign = -1 if lamp == 'headlights' else 1
        for face in part['faces']:
            if sign*part['normals'][int(face[0].split('/')[2])-1][2] <= .5:
                continue
            for corner in face:
                x,y,z = part['vertices'][int(corner.split('/')[0])-1]
                # Independent plane from the retained, measured nose stations.
                surface_z = -2.792392+.034564*(y-.125)/.874953 if sign<0 else 2.68
                assert sign*(z-surface_z) >= .001998, ('buried outward lens corner',lamp,x,y,z)
        print(f'PASS sedan {lamp}: complete donor topology/UVs and emitters translated Z {delta:+.6f}')

    def on_triangle(p, tri):
        a,b,c = tri
        u = tuple(b[i]-a[i] for i in range(3)); v = tuple(c[i]-a[i] for i in range(3))
        q = tuple(p[i]-a[i] for i in range(3))
        dot = lambda a,b: sum(x*y for x,y in zip(a,b))
        uu,uv,vv,qu,qv = dot(u,u),dot(u,v),dot(v,v),dot(q,u),dot(q,v)
        det = uu*vv-uv*uv
        s,t = (qu*vv-qv*uv)/det,(qv*uu-qu*uv)/det
        return s >= -1e-5 and t >= -1e-5 and s+t <= 1+1e-5 and sum((q[i]-s*u[i]-t*v[i])**2 for i in range(3)) < 16e-12

    source = obj(CONTENT/'hatchback_body.txt')
    body = wagon['mesh']
    zn = min(z for x,y,z in body['vertices'] if y == .999953)
    zv = min(z for x,y,z in body['vertices'] if y == .125)
    for label,sign,ids in (('front',-1,range(290,318)),('rear',1,range(318,346))):
        name = f'wagon_bumper_{label}.txt'
        assert name in wagon['Parts'], ('bumper missing from Parts',name)
        part = obj(CONTENT/name)
        # Independently identified donor faces (before refitting), not generator selection.
        donor = [[source['vertices'][int(c.split('/')[0])-1] for c in source['faces'][i]] for i in ids]
        ps = {p for tri in donor for p in tri}
        lo = tuple(min(p[i] for p in ps) for i in range(3)); hi = tuple(max(p[i] for p in ps) for i in range(3))
        scale = 2.52/(hi[0]-lo[0]); dy = FLOOR_Y-lo[1]
        dz = part['lo'][2]-lo[2] if sign<0 else part['hi'][2]-hi[2]
        assert part['lo'][0] == -1.26 and part['hi'][0] == 1.26 and abs(part['lo'][1]-FLOOR_Y) < 1e-9
        assert abs((part['lo'][2] if sign<0 else part['hi'][2])-(-2.949005 if sign<0 else 2.826938)) < 1e-6, 'bumper Z moved'
        assert abs(part['size'][1]-(hi[1]-lo[1])) < 1e-6
        part_tris = [sorted(part['vertices'][int(c.split('/')[0])-1] for c in f) for f in part['faces']]
        outer_count = 0
        for face_id,tri in zip(ids,donor):
            face = source['faces'][face_id]
            normal = source['normals'][int(face[0].split('/')[2])-1]
            if sign*normal[2] <= .9:
                continue
            expected = sorted(((x-(lo[0]+hi[0])/2)*scale,y+dy,z+dz) for x,y,z in tri)
            assert any(all(abs(a[i]-b[i])<4e-6 for a,b in zip(expected,t) for i in range(3)) for t in part_tris), ('missing donor outer face',label,face_id)
            outer_count += 1
        assert outer_count == 6, ('incomplete donor outward band',label)
        cap_count = donor_count = 0
        for f in part['faces']:
            tri = [part['vertices'][int(c.split('/')[0])-1] for c in f]
            # Attachment cap moves down with the entire part; its original Z
            # stays fixed, leaving 1.383 mm clearance at the sloping fascia.
            # Undo the part's drop to find where the sloping fascia was when the bumper was fitted.
            # The fitted lip is -0.120 and the finished lip is FLOOR_Y, so the drop -- and this
            # correction -- follow the floor. Hard-coding 0.035 here silently mis-sited the fascia
            # plane by 4 mm the moment the floor moved, and reported it as a bumper/body overlap.
            undrop = -.120 - FLOOR_Y
            distances = [sign*(z-(zv+(zn-zv)*(y+undrop-.125)/(.999953-.125) if sign<0 else 2.68)) for x,y,z in tri]
            assert min(distances) > -1e-6, 'bumper side overlaps body wall'
            if all(abs(d)<1e-6 for d in distances):
                cap_count += 1
            else:
                original = [((x/scale)+(lo[0]+hi[0])/2,y-dy,z-dz) for x,y,z in tri]
                assert any(all(on_triangle(p,t) for p in original) for t in donor), ('invented exposed bumper surface',label,tri)
                donor_count += 1
        assert donor_count and cap_count
        _,edges = welded_edges(part)
        assert all(n==2 for n in edges.values()), ('open donor bumper',label)
        surface_audit(part)
        print(f'PASS hatchback {label}: donor exterior surfaces, attachment cap, 2.52 m width; 0 / {len(edges)} boundary edges')


def check():
    bounds = {p.name: validate(p) for p in sorted(CONTENT.glob("wagon_*.txt"))}
    specs = read_specs(("wagon", "sedan", "golf"))
    sedan, wagon = specs["sedan"], specs["wagon"]
    print('| body | verts | welded | tris | boundary edges | % |')
    print('| --- | ---: | ---: | ---: | ---: | ---: |')
    for name in ('wagon','sedan','hatchback','van','truck'):
        mesh = obj(CONTENT / f'{name}_body.txt')
        welded,edges = welded_edges(mesh)
        boundary = sum(n == 1 for n in edges.values())
        print(f"| {name} | {len(mesh['vertices'])} | {welded} | {len(mesh['faces'])} | {boundary} / {len(edges)} | {100*boundary/len(edges):.1f}% |")
        if name == 'wagon':
            assert boundary == 0, 'wagon must have 0.0% boundary edges'
            assert all(n == 2 for n in edges.values()), 'wagon has an open or overused welded edge'
    surface_audit(wagon["mesh"])
    check_hood(sedan["mesh"], wagon["mesh"])
    check_straight_sides(wagon["mesh"])
    check_roof_rake(wagon["mesh"])
    check_cowl_junction(wagon["mesh"])
    check_stripped_shell(wagon["mesh"])
    check_bumper_height(wagon)
    check_cabin_fit(wagon, sedan)
    check_exhaust(wagon)
    check_donor_parts(wagon, sedan)
    lo,hi = bounds["wagon_body.txt"]
    # Re-derive the front extent by continuing the saved fascia to floor Y.
    vs = wagon['mesh']['vertices']
    zn = min(p[2] for p in vs if abs(p[1]-.999953)<1e-6)
    zv = min(p[2] for p in vs if p[1] == .125)
    floor_z = zv+(zn-zv)*(FLOOR_Y-.125)/(.999953-.125)
    assert all(abs(x-y) < 1e-6 for x,y in zip(lo+hi,(-1.26,FLOOR_Y,floor_z,1.26,2.17,2.68))), (lo,hi)
    assert wagon["Wheels"] == [(-1.3,.25,-1.56,True),(1.3,.25,-1.56,True),(-1.3,.25,1.46,False),(1.3,.25,1.46,False)]
    assert abs(wagon["wheelbase"]-3.02)<1e-9 and wagon["tracks"] == [2.6,2.6]
    assert wagon["radii"] == sedan["radii"]
    for field in ("Wheel", "WheelTex", "Engine", "SpeedMax", "Fuel", "Health"):
        assert wagon[field] == sedan[field], field
    assert wagon["Mass"] == 1650  # retained table: (sedan 1500 + police 1800)/2
    # THE MAIN BOX IS A FITTED LOWER SHELL, NOT AN ENCLOSING ONE. Every roofed car in this fleet
    # pairs a low main box with a separate RoofBox: sedan/police (2.5,0.916,5.656), hatchback
    # (2.5,0.916,5.261), humvee (2.5,1.032,5.029) -- all stopping below the beltline. An earlier pass
    # here asserted that the box must CONTAIN every vertex, which is the opposite of the intent and
    # passes only for a box that swallows the greenhouse: that makes the window apertures solid and
    # overlaps RoofBox("Station Wagon") at y 1.92..2.17. So assert the convention, not containment.
    box_lo = tuple(c-s/2 for c,s in zip(wagon['BoxCenter'],wagon['BoxSize']))
    box_hi = tuple(c+s/2 for c,s in zip(wagon['BoxCenter'],wagon['BoxSize']))
    BELTLINE = 1.10   # window aperture bases; posts run 1.10 -> 1.92
    assert box_hi[1] < BELTLINE, ('main box reaches the greenhouse', box_hi[1], BELTLINE)
    assert box_lo[1] > FLOOR_Y, ('main box dips below the floor', box_lo[1])
    # In family with the other roofed cars on both spent axes, so this cannot pass by going tiny.
    for i,name,famlo,famhi in ((1,'height',0.90,1.10),(2,'length',5.0,5.7)):
        assert famlo <= wagon['BoxSize'][i] <= famhi, ('main box out of fleet family',name,wagon['BoxSize'][i])
    # It must still sit centred on the real extents rather than drifting off the mesh.
    everything = [p for name in ('body','headlights','taillights','bumper_front','bumper_rear')
                  for p in obj(CONTENT/f'wagon_{name}.txt')['vertices']]
    zc = (min(p[2] for p in everything)+max(p[2] for p in everything))/2
    assert abs(wagon['BoxCenter'][2]-zc) < 0.01, ('box centre off the mesh centre',wagon['BoxCenter'][2],zc)
    # And the roof really is carried by the separate box, or the cabin has no collider at all.
    roof_lo = min(p[1] for p in obj(CONTENT/'wagon_body.txt')['vertices'] if p[1] > 1.5)
    assert roof_lo <= 1.93, ('roof slab not where RoofBox expects it', roof_lo)
    print(f"PASS main box is a fitted lower shell: top {box_hi[1]:.3f} < beltline {BELTLINE}, "
          f"Z {wagon['BoxSize'][2]:.3f} centred {wagon['BoxCenter'][2]:+.4f} on mesh centre {zc:+.4f}; "
          f"roof left to RoofBox")
    assert (CONTENT / wagon["Palette"]).read_bytes() == (CONTENT / sedan["Palette"]).read_bytes()
    for asset in [wagon[k] for k in ("Body","Wheel","WheelTex","Palette")] + wagon["Parts"]:
        assert (CONTENT / asset).is_file(), asset
    from analyze_vehicle_style import baseline
    fleet,_ = baseline()  # retained table, not a new fleet measurement
    for key,records in (("triangles",wagon["mesh"]["faces"]),):
        counts = [b[key] for b in fleet.values()]
        assert min(counts)<len(records)<max(counts)
        print(f"PASS body {key}: {len(records)} against road min/median/max {min(counts)}/{__import__('statistics').median(counts)}/{max(counts)}")
    # Originality: no complete body triangle shares the sedan's coordinates,
    # independent of winding, index reuse or UV/normal serialization.
    def signatures(mesh):
        return {tuple(sorted(mesh["vertices"][int(c.split('/')[0])-1] for c in face)) for face in mesh["faces"]}
    assert not (signatures(wagon['mesh']) & signatures(sedan['mesh'])), "copied sedan body panel"
    seats=obj(CONTENT/'sedan_seats.txt')
    assert seats['hi'][2] < 1.36, ('rear seatback intrudes into cargo deck',seats['hi'])
    print(f"PASS original body panels; rear seatback Z {seats['hi'][2]:.6f} < load floor start 1.36")
    print("PASS AABB, new axle rig, fitted seats, shared wheels, moved lamps, fitted colliders, palette and assets")

    src = uncomment((ROOT / "game/Vehicle.cs").read_text())
    build = re.search(r"public static Vehicle BuildByName\([^;]+;", src)[0]
    lookup = braced(src, src.index("{", src.index("static Spec SpecFor")))
    names_text = braced(src, src.index("{", src.index("string[] SpecNames")))
    names = re.findall(r'"([^"]+)"', names_text)
    assert names.count("wagon") == 1 and names[-1] == "wagon", "Append to preserve existing network TypeIds"
    assert '"wagon" => BuildWagon(variant)' in build
    assert '"wagon" => _wagon' in lookup
    assert re.search(r'BuildWagon\(int variant = 0\)\s*=>\s*Build\(_wagon, variant, "wagon"\)', src)
    roof = re.search(r'"Station Wagon"\s*=>\s*\((new Vector3\([^)]*\)),\s*(new Vector3\([^)]*\))\)', src)
    assert roof, "Missing roof/cabin registration"
    size,center = vector(roof[1]),vector(roof[2])
    assert abs(center[1]+size[1]/2-hi[1]) < 1e-6
    assert size == (2.52,.25,3.36) and center == (0,2.045,.88)
    sedan_pose = vector(re.search(r'"Sedan"\s*=>\s*(new Vector3\([^)]*\))',src)[1])
    wagon_pose = vector(re.search(r'"Station Wagon"\s*=>\s*(new Vector3\([^)]*\))',src)[1])
    assert all(abs(w-s-(.205 if i==2 else 0))<1e-6 for i,(w,s) in enumerate(zip(wagon_pose,sedan_pose))), 'driver body pose did not follow front row'
    assert 'name is "Sedan" or "Station Wagon"' in src
    labels = re.findall(r'"([^"]+)"', braced(src,src.index("{",src.index("string[] GlassPaneLabels"))))
    glass_base = wagon["GlassMesh"].removesuffix(".txt")
    pane_names = {f"{glass_base}_{label}.txt" for label in labels if (CONTENT / f"{glass_base}_{label}.txt").exists()}
    expected_panes = {f"wagon_glass_{label}.txt" for label in ("windshield","rear","l_front","r_front","l_rear","r_rear","l_mid1","r_mid1")}
    assert pane_names == expected_panes
    assert all(len(obj(CONTENT / p)["faces"]) == 2 for p in pane_names)
    triangles = [[wagon['mesh']['vertices'][int(c.split('/')[0])-1] for c in f] for f in wagon['mesh']['faces']]
    # Probe the actual exterior skin across each former arch, down to the sill,
    # plus the floor/sill join and rear underside. No inboard pocket backings.
    lower_probes = []
    for sign in (-1,1):
        for z in (-.75,-.25,.25,.65):
            lower_probes.append(((sign*1.005,-.3,z),(sign*1.005,.05,z)))
        for axle in (-1.56,1.46):
            for dz in (-.6,-.4,0,.4,.6):
                for y in (-.10,.2,.5,.7):
                    lower_probes.append(((sign*1.1,y,axle+dz),(sign*1.4,y,axle+dz)))
    for x in (-.8,0,.8):
        lower_probes.append(((x,-.3,2.65),(x,.1,2.65)))
    for a,b in lower_probes:
        assert any(segment_hits(a,b,t) for t in triangles), ('open lower body',a,b)
    print(f'PASS {len(lower_probes)} continuous side, floor/sill and rear-underside closure probes')
    for lamp,sign in (('headlights',-1),('taillights',1)):
        mesh = obj(CONTENT/f'wagon_{lamp}.txt')
        checked = 0
        for face in mesh['faces']:
            cs = [tuple(int(i)-1 for i in c.split('/')) for c in face]
            if mesh['normals'][cs[0][2]][2]*sign < .5:
                continue
            p = tuple(sum(mesh['vertices'][c[0]][i] for c in cs)/3 for i in range(3))
            a = (p[0],p[1],p[2]+sign*.001)
            assert not any(segment_hits(a,(p[0],p[1],sign*3.05),t) for t in triangles), (lamp,'buried outward lens',p)
            checked += 1
        assert checked >= 4, (lamp,'no outward lens faces')
        print(f'PASS {lamp}: {checked} outward lens faces visible past body')
    for path in pane_names:
        pane = obj(CONTENT/path)
        for face in pane['faces']:
            corners = [tuple(int(i)-1 for i in c.split('/')) for c in face]
            points = [pane['vertices'][c[0]] for c in corners]
            n = pane['normals'][corners[0][2]]
            # Four interior samples per glass triangle, through the outer body skin.
            for weights in ((1/3,1/3,1/3),(.6,.2,.2),(.2,.6,.2),(.2,.2,.6)):
                p = tuple(sum(w*q[i] for w,q in zip(weights,points)) for i in range(3))
                a = tuple(x-.03*y for x,y in zip(p,n))
                b = tuple(x+.03*y for x,y in zip(p,n))
                assert not any(segment_hits(a,b,t) for t in triangles), (path, 'body blocks window', p)
    for x in (-.8,0,.8):
        for z in (1.45,1.9,2.4):
            assert not any(segment_hits((x,.181,z),(x,1.919,z),t) for t in triangles), ('blocked cargo space',x,z)
    print('PASS 64 glass-interior probes and 9 vertical load-area probes are unobstructed')
    print("PASS BuildWagon / BuildByName / SpecNames / SpecFor / SeatTable / SeatOf / HandTunedSeatOf / RoofBox / eight pane labels")
    # Preserve older TypeIds, even after the task commit (compare against the parent version with no wagon).
    for revision in ("f9970da8^:game/Vehicle.cs",):
        old_src = uncomment(subprocess.check_output(["git","show",revision],cwd=ROOT,text=True))
        old_names = re.findall(r'"([^"]+)"', braced(old_src, old_src.index("{",old_src.index("string[] SpecNames"))))
        if "wagon" not in old_names:
            assert names[:-1] == old_names
            print(f"PASS all {len(old_names)} pre-wagon network TypeIds retained; wagon TypeId {names.index('wagon')}")
            break
    print("Static verifier does not execute Godot/render/drive tests; see WAGON_REPORT.md for separate runtime results")


if __name__ == "__main__":
    check()
