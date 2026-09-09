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
    specs = read_specs()
    sedan, wagon = specs["sedan"], specs["wagon"]
    shift = wagon["TailPos"][0][2] - sedan["TailPos"][0][2]
    assert abs(shift-0.25) < 1e-9
    lo,hi = bounds["wagon_body.txt"]
    expected_lo = sedan["mesh"]["lo"]
    expected_hi = (*sedan["mesh"]["hi"][:2], sedan["mesh"]["hi"][2]+shift)
    assert all(abs(x-y) < 0.000001 for x,y in zip(lo+hi,expected_lo+expected_hi)), (lo,hi)
    assert wagon["Wheels"] == sedan["Wheels"]
    assert wagon["radii"] == sedan["radii"]
    assert wagon["Seats"] == sedan["Seats"] and len(wagon["Seats"]) == 4
    for field in ("Wheel", "WheelTex", "SpotPos", "OmniPos", "Engine", "SpeedMax", "Fuel", "Health"):
        assert wagon[field] == sedan[field], field
    assert wagon["Mass"] == (sedan["Mass"]+specs["police"]["Mass"])/2
    assert abs(wagon["BoxSize"][2]-sedan["BoxSize"][2]-shift) < 1e-9
    assert abs(wagon["BoxCenter"][2]-sedan["BoxCenter"][2]-shift/2) < 1e-9
    # Extending the rear collider must leave its front face fixed.
    assert abs((wagon["BoxCenter"][2]-wagon["BoxSize"][2]/2)-(sedan["BoxCenter"][2]-sedan["BoxSize"][2]/2)) < 1e-9
    for w,s in zip(wagon["TailPos"], sedan["TailPos"]):
        assert w[:2] == s[:2] and abs(w[2]-s[2]-shift)<1e-9
    assert (CONTENT / wagon["Palette"]).read_bytes() == (CONTENT / sedan["Palette"]).read_bytes()
    for asset in [wagon[k] for k in ("Body","Wheel","WheelTex","Palette")] + wagon["Parts"]:
        assert (CONTENT / asset).is_file(), asset
    assert len(wagon["mesh"]["faces"]) < 2*len(sedan["mesh"]["faces"])
    assert len(wagon["mesh"]["vertices"]) < 2*len(sedan["mesh"]["vertices"])
    print("PASS AABB, sedan rig/seat/drive inheritance, lamp delta, collider extension, palette and asset references")

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
    assert abs(size[1]-shift)<1e-9
    assert '"Sedan" or "Station Wagon" => new Vector3' in src
    assert 'name is "Sedan" or "Station Wagon"' in src
    labels = re.findall(r'"([^"]+)"', braced(src,src.index("{",src.index("string[] GlassPaneLabels"))))
    glass_base = wagon["GlassMesh"].removesuffix(".txt")
    pane_names = {f"{glass_base}_{label}.txt" for label in labels if (CONTENT / f"{glass_base}_{label}.txt").exists()}
    expected_panes = {f"wagon_glass_{label}.txt" for label in ("windshield","rear","l_front","r_front","l_rear","r_rear","l_mid1","r_mid1")}
    assert pane_names == expected_panes
    assert all(len(obj(CONTENT / p)["faces"]) == 2 for p in pane_names)
    print("PASS BuildWagon / BuildByName / SpecNames / SpecFor / SeatTable / SeatOf / HandTunedSeatOf / RoofBox / eight pane labels")
    # Preserve older TypeIds, even after the task commit (compare against the parent version with no wagon).
    for revision in ("HEAD:game/Vehicle.cs", "HEAD^:game/Vehicle.cs"):
        old_src = uncomment(subprocess.check_output(["git","show",revision],cwd=ROOT,text=True))
        old_names = re.findall(r'"([^"]+)"', braced(old_src, old_src.index("{",old_src.index("string[] SpecNames"))))
        if "wagon" not in old_names:
            assert names[:-1] == old_names
            print(f"PASS all {len(old_names)} pre-wagon network TypeIds retained; wagon TypeId {names.index('wagon')}")
            break
    print("Static verifier does not execute Godot/render/drive tests; see WAGON_REPORT.md for separate runtime results")


if __name__ == "__main__":
    check()
