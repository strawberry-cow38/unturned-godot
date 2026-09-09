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
        # Each new face is flat-shaded; its stored normals must follow its actual winding.
        for p in face:
            agreement = sum(x*y for x,y in zip(cross,normals[p[2]]))/area2
            assert agreement > 0.99999, (path.name, face, agreement, "normal disagrees with winding")
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
    for s,w in ((sn,wn),(sc,wc)):
        assert abs(s[0]-w[0])<2e-6
        assert abs((-1.56+(s[1]+1.62)*scale)-w[1])<2e-6
    assert any(abs(p[1]-1.125)<1e-6 and p[2]==-1.25 for p in wv)
    levels=(-.159,-.125,.101,.125,.875,1.,1.125)
    print('| front Y station | sedan | wagon |')
    print('| --- | ---: | ---: |')
    for y in levels:
        found=[]
        for vs in (sv,wv):
            vals=[p[1] for p in vs if p[2]<-1.25 and abs(p[1]-y)<.0005]
            assert vals, ('missing front Y level',y)
            found.append(min(vals,key=lambda value:abs(value-y)))
        assert abs(found[0]-found[1])<2e-6,(y,found)
        print(f'| {y:.3f} | {found[0]:.6f} | {found[1]:.6f} |')
    print(f'PASS sedan hood: centre (Y,Z) {sn} → {sc}; wagon {wn} → {wc}; Z scale {scale:.9f}')


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
        z = round((i-13)*5.80/26,2)
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
        if all(p[1] >= 1.125 and p[2] < 0 for p in tri) and n[2] < -.1 and abs(n[0]) < 1e-6:
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
    rear = obj(CONTENT/'wagon_glass_rear.txt')
    assert sorted({p[1:] for p in rear['vertices']}) == [(1.1,2.68),(1.92,2.56)], 'tailgate rake changed'
    print(f'PASS roof front coplanar with unchanged windscreen/A-post: top Z {expected_top:.6f}; '
          f'rake {math.degrees(math.atan(slope)):.6f}° from vertical; tailgate rake unchanged')


def check():
    bounds = {p.name: validate(p) for p in sorted(CONTENT.glob("wagon_*.txt"))}
    specs = read_specs(("wagon", "sedan"))
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
    lo,hi = bounds["wagon_body.txt"]
    assert all(abs(x-y) < 0.000001 for x,y in zip(lo+hi,(-1.26,-.27,-2.90,1.26,2.17,2.90))), (lo,hi)
    assert wagon["Wheels"] == [(-1.3,.25,-1.56,True),(1.3,.25,-1.56,True),(-1.3,.25,1.46,False),(1.3,.25,1.46,False)]
    assert abs(wagon["wheelbase"]-3.02)<1e-9 and wagon["tracks"] == [2.6,2.6]
    assert wagon["radii"] == sedan["radii"]
    assert wagon["Seats"] == sedan["Seats"] and len(wagon["Seats"]) == 4
    for field in ("Wheel", "WheelTex", "Engine", "SpeedMax", "Fuel", "Health"):
        assert wagon[field] == sedan[field], field
    assert wagon["Mass"] == 1650  # retained table: (sedan 1500 + police 1800)/2
    assert wagon["BoxSize"] == (2.5,.98,5.52) and wagon["BoxCenter"] == (0,.59,0)
    for field,delta in (("SpotPos",.150),("TailPos",.012)):
        for w,s in zip(wagon[field],sedan[field]):
            assert w[:2] == s[:2] and abs(w[2]-s[2]-delta)<1e-9
    assert abs(wagon["OmniPos"][2]-sedan["OmniPos"][2]-.150)<1e-9
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
    for lamp,delta in (("headlights",.150),("taillights",.012)):
        a=obj(CONTENT/f'sedan_{lamp}.txt');b=obj(CONTENT/f'wagon_{lamp}.txt')
        for axis in range(3):
            d=delta if axis==2 else 0
            assert abs(a['lo'][axis]+d-b['lo'][axis])<1e-6
            assert abs(a['hi'][axis]+d-b['hi'][axis])<1e-6
    print("PASS AABB, new axle rig, shared seats/wheels, moved lamps, fitted colliders, palette and assets")

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
    assert '"Sedan" or "Station Wagon" => new Vector3' in src
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
        lower_probes.append(((x,-.3,2.72),(x,.1,2.72)))
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
