#!/usr/bin/env python3
"""Validate saved wagon OBJ assets and the SP/MP registrations without Godot.

This is a static check, not a substitute for an in-engine spawn/drive/render.
Uses float32 input, ParseObj's positive one-based indices and its V flip.
"""
import math
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


def check():
    bounds = {p.name: validate(p) for p in sorted(CONTENT.glob("wagon_*.txt"))}
    specs = read_specs(("wagon", "sedan"))
    sedan, wagon = specs["sedan"], specs["wagon"]
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
    for key,records in (("vertices",wagon["mesh"]["vertices"]),("triangles",wagon["mesh"]["faces"])):
        counts = [b[key] for b in fleet.values()]
        assert min(counts)<len(records)<max(counts)
        print(f"PASS body {key}: {len(records)} against road min/median/max {min(counts)}/{__import__('statistics').median(counts)}/{max(counts)}")
    # Originality: no complete body triangle shares the sedan's coordinates,
    # independent of winding, index reuse or UV/normal serialization.
    def signatures(mesh):
        return {tuple(sorted(mesh["vertices"][int(c.split('/')[0])-1] for c in face)) for face in mesh["faces"]}
    assert not (signatures(wagon['mesh']) & signatures(sedan['mesh'])), "copied sedan body panel"
    source=(ROOT/'tools/build_wagon.py').read_text()
    assert 'sedan_body.txt' not in source and 'read_specs' not in source
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
    assert size == (2.46,.25,3.36) and center == (0,2.045,.88)
    assert '"Sedan" or "Station Wagon" => new Vector3' in src
    assert 'name is "Sedan" or "Station Wagon"' in src
    labels = re.findall(r'"([^"]+)"', braced(src,src.index("{",src.index("string[] GlassPaneLabels"))))
    glass_base = wagon["GlassMesh"].removesuffix(".txt")
    pane_names = {f"{glass_base}_{label}.txt" for label in labels if (CONTENT / f"{glass_base}_{label}.txt").exists()}
    expected_panes = {f"wagon_glass_{label}.txt" for label in ("windshield","rear","l_front","r_front","l_rear","r_rear","l_mid1","r_mid1")}
    assert pane_names == expected_panes
    assert all(len(obj(CONTENT / p)["faces"]) == 2 for p in pane_names)
    triangles = [[wagon['mesh']['vertices'][int(c.split('/')[0])-1] for c in f] for f in wagon['mesh']['faces']]
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
