#!/usr/bin/env python3
"""Reproduce notes/vehicle_measurements.md from OBJ vertices and Vehicle.cs literals.

No prefab assumptions: this deliberately rejects expressions it cannot evaluate.
Aircraft with factory-generated specs are inventoried geometrically, not inferred.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path
import re
import statistics

ROOT = Path(__file__).resolve().parents[1]
CONTENT = ROOT / "game/content"
ROAD = "jeep semi trailer quad bus sedan hatchback humvee roadster ambulance firetruck tractor ural police offroader truck van golf apc tank".split()
BOATS = ["runabout", "ship"]
SKIP = "hind orca huey skycrane hummingbird minicopter fighterjet".split()
EPS = 0.00001
NUMBER = r"[-+]?(?:\d[\d_]*(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?[fFdD]?"


def uncomment(src):
    return re.sub(r'"(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/',
                  lambda m: m[0] if m[0].startswith('"') else " ", src)


def split_top(src, separator=","):
    result, start, depth, quoted, escaped = [], 0, 0, False, False
    for i, c in enumerate(src):
        if quoted:
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == '"':
                quoted = False
        elif c == '"':
            quoted = True
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == separator and depth == 0:
            result.append(src[start:i].strip())
            start = i + 1
    result.append(src[start:].strip())
    return [x for x in result if x]


def braced(src, start):
    depth = 0
    for i in range(start, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[start + 1:i]
    raise ValueError("Unclosed initializer")


def number(src):
    src = src.strip()
    assert re.fullmatch(NUMBER, src), f"Not a numeric literal: {src}"
    return float(src.rstrip("fFdD").replace("_", ""))


def vector(src):
    if src == "Vector3.Zero":
        return (0., 0., 0.)
    m = re.fullmatch(r"new Vector3\((.*?)\)", src, re.S)
    assert m, f"Not a vector literal: {src}"
    v = tuple(number(x) for x in split_top(m[1]))
    assert len(v) == 3
    return v


def vectors(src):
    if src in (None, "null"):
        return []
    return [vector(m[0]) for m in re.finditer(r"new Vector3\([^()]*\)", src)]


def obj(path):
    vertices, uvs, normals, faces = [], [], [], []
    for line in path.read_text().splitlines():
        p = line.split()
        if not p or p[0].startswith("#"):
            continue
        if p[0] == "v":
            vertices.append(tuple(map(float, p[1:4])))
        elif p[0] == "vt":
            uvs.append((float(p[1]), 1 - float(p[2])))  # ParseObj V flip
        elif p[0] == "vn":
            normals.append(tuple(map(float, p[1:4])))
        elif p[0] == "f":
            faces.append(p[1:])
    assert vertices, path
    lo = tuple(min(v[i] for v in vertices) for i in range(3))
    hi = tuple(max(v[i] for v in vertices) for i in range(3))
    return dict(vertices=vertices, uvs=uvs, normals=normals, faces=faces,
                lo=lo, hi=hi, size=tuple(b - a for a, b in zip(lo, hi)))


def read_specs(names=None):
    src = uncomment((ROOT / "game/Vehicle.cs").read_text())
    seats_start = src.index("SeatTable =")
    seats_text = braced(src, src.index("{", seats_start))
    seats = {m[1]: vectors(m[2]) for m in re.finditer(r'\["([^"]+)"\]\s*=\s*new\[\]\s*\{([^}]+)\}', seats_text)}
    specs = {}
    global_mass = number(re.search(r"GlobalMass\s*=\s*(" + NUMBER + ")", src)[1])
    for m in re.finditer(r"static readonly Spec _(\w+)\s*=\s*new\(\)\s*\{", src):
        fields = dict(x.split("=", 1) for x in split_top(braced(src, m.end() - 1)))
        fields = {k.strip(): v.strip() for k, v in fields.items()}
        name = m[1]
        if name not in ROAD + BOATS + ["otter", "wagon"]:
            continue
        if names is not None and name not in names:
            continue
        data = dict(key=name, fields=fields)
        for key in ("Mass", "WheelRadius", "Engine", "SpeedMax", "Fuel", "Health"):
            data[key] = number(fields.get(key, "0"))
        data["mass_default"] = data["Mass"] == 0
        data["Mass"] = data["Mass"] or global_mass
        for key in ("BoxSize", "BoxCenter", "OmniPos"):
            data[key] = vector(fields.get(key, "Vector3.Zero"))
        for key in ("SpotPos", "TailPos"):
            data[key] = vectors(fields.get(key))
        data["Seats"] = vectors(fields["Seats"]) if "Seats" in fields else seats.get(name)
        data["seat_source"] = "Spec.Seats" if "Seats" in fields else "SeatTable"
        if data["Seats"] is None:
            # Trailer: Build() falls through SeatOf("Semi Trailer") to this exact default.
            fallback = re.search(r"static Vector3 SeatOf.*?_ => (new Vector3\([^()]*\))", src, re.S)
            data["Seats"] = [vector(fallback[1])]
            data["seat_source"] = "SeatOf fallback; no seat table"
        for key in ("Body", "Wheel", "WheelTex", "Palette", "GlassMesh"):
            data[key] = fields.get(key, "null").strip('"')
        if data["Body"] == "null":
            data["Body"] = re.findall(r'"([^"]+)"', fields["HeliBodyMeshes"])[0]
        data["Parts"] = re.findall(r'\(\s*"([^"]+)"', fields.get("Parts", ""))
        data["Wheels"] = [tuple(number(v) for v in m.groups()[:3]) + (m[4] == "true",)
                          for m in re.finditer(r"\(\s*(" + NUMBER + r")\s*,\s*(" + NUMBER + r")\s*,\s*(" + NUMBER + r")\s*,\s*(true|false)\s*\)", fields["Wheels"])]
        data["radii"] = ([number(x) for x in split_top(braced(fields["WheelRadii"], fields["WheelRadii"].index("{")))]
                         if "WheelRadii" in fields else [data["WheelRadius"]] * len(data["Wheels"]))
        assert len(data["radii"]) == len(data["Wheels"])
        axles = defaultdict(list)
        for i, wheel in enumerate(data["Wheels"]):
            axles[wheel[2]].append(i)
        data["axles"] = dict(sorted(axles.items()))
        data["wheelbase"] = max(axles) - min(axles) if len(axles) > 1 else None
        data["tracks"] = [max(data["Wheels"][i][0] for i in ids) - min(data["Wheels"][i][0] for i in ids)
                          for ids in data["axles"].values()]
        data["mesh"] = obj(CONTENT / data["Body"])
        specs[name] = data
    assert set(names if names is not None else ROAD + BOATS + ["otter"]) <= specs.keys()
    return specs


def fmt(x):
    return f"{x:.6f}"


def vec(v):
    return "(" + ", ".join(fmt(x) for x in v) + ")"


def vlist(vs):
    return "; ".join(f"{i}: {vec(v)}" for i, v in enumerate(vs)) or "null"


def table(out, headers, rows):
    out.append("| " + " | ".join(headers) + " |")
    out.append("| " + " | ".join("---" for _ in headers) + " |")
    out.extend("| " + " | ".join(str(x) for x in row) + " |" for row in rows)
    out.append("")


def geometry_row(name, mesh):
    lo, hi, size = mesh["lo"], mesh["hi"], mesh["size"]
    return [name, fmt(size[2]), fmt(size[0]), fmt(size[1]),
            *[f"{fmt(lo[i])} .. {fmt(hi[i])}" for i in range(3)],
            len(mesh["vertices"]), len(mesh["faces"])]


def write_notes(specs):
    base = {k: s for k, s in specs.items() if k != "wagon"}
    ordered = sorted(base, key=lambda k: (-base[k]["mesh"]["size"][0], k))
    if "wagon" in specs:
        ordered.append("wagon")
    files = list(CONTENT.glob("*_body.txt"))
    out = ["# Vehicle measurements", "",
           "Generated by `python3 tools/measure_vehicles.py`. Source: `game/Vehicle.cs`, `game/ContentProvider.cs`, and the named OBJ files under `game/content/`. All lengths are metres in the files' Godot frame: X lateral, +Y up, forward −Z. Unity Z has already been negated; no second negation or scale is applied. Values are computed before rounding; tables retain six decimal places.", "",
           "Body AABB uses every `v` record, including unreferenced vertices, and excludes wheels, Parts and separate glass. Vertices means OBJ `v` records; triangles means `f` records as loaded (only the first three corners). Neither count is a count of unique positions or the SurfaceTool-expanded vertex buffer.", "",
           f"Scope: {len(ROAD)} existing road specs (including the APC, tank and towed trailer), {len(BOATS)} boats, and Otter. The {len(files) - int('wagon' in specs)} pre-wagon `*_body.txt` files include `sks_action_body.txt`, a firearm component, and `train_body.txt`, owned by `Train.cs`. Neither has a Vehicle.Spec. `semi_0.txt`, `trailer_0.txt` and `tank_hull.txt` are included despite their different filenames. Aircraft spec/seat/light audits skipped explicitly: {', '.join(SKIP)}; their body geometry is still measured below. Scoutcopter has a procedural body and no corresponding `*_body.txt`; it is outside this mesh inventory.", "",
           "## Body mesh dimensions and counts", "",
           "Rows are sorted by width, descending (the new wagon, when present, is appended and excluded from baseline statistics). Road/boat classification is in the dynamics table.", ""]
    headers = ["Vehicle / mesh", "Length Z", "Width X", "Height Y", "X min .. max", "Y min .. max", "Z min .. max", "Vertices", "Triangles"]
    table(out, headers, [geometry_row(f"{k} / `{specs[k]['Body']}`", specs[k]["mesh"]) for k in ordered])
    out += ["## Wheels", "",
            "Axles are ordered by increasing Z (front to rear); wheel IDs are zero-based indices in `Wheels`. Wheelbase = largest anchor Z − smallest anchor Z. For the trailer this is only the rear bogie spacing, not hitch-to-axle distance. Track is max X − min X at each axle. Radius/ride entries use the same axle order and show each distinct value on that axle. Ride = anchor Y − effective radius, exactly as requested: a local tyre-bottom coordinate, not loaded ground clearance. Suspension rest drop, travel, compression and hull placement are excluded. `WheelRadii` overrides `WheelRadius` on semi, trailer and tractor. Boats and Otter have no wheel anchors; their declared radii are unused.", ""]
    rows = []
    for k in ordered:
        s = specs[k]
        axle_text, radii, ride = [], [], []
        for z, ids in s["axles"].items():
            axle_text.append(f"Z {fmt(z)}: " + ", ".join(f"{i}={vec(s['Wheels'][i][:3])}{'*' if s['Wheels'][i][3] else ''}" for i in ids))
            radii.append("/".join(fmt(r) for r in sorted({s['radii'][i] for i in ids})))
            ride.append("/".join(fmt(r) for r in sorted({s['Wheels'][i][1] - s['radii'][i] for i in ids})))
        rows.append([k, fmt(s["wheelbase"]) if s["wheelbase"] is not None else "—", "; ".join(map(fmt, s["tracks"])) or "—", fmt(s["WheelRadius"]), "; ".join(radii) or "—", "; ".join(ride) or "—", "; ".join(axle_text) or "[]"])
    table(out, ["Vehicle", "Wheelbase", "Track by axle F→R", "WheelRadius", "Effective radii F→R", "Ride Y−r F→R", "Anchors (X,Y,Z); * = steer"], rows)
    out += ["## Collider comparison", "",
            "The test is BoxCenter ± BoxSize/2 versus the body AABB, not size alone. Ratios are BoxSize / body extent in X,Y,Z. Missing faces list how far the mesh protrudes beyond each box face (X− is the minimum-X face). Tolerance = 0.000010 m. These are the requested main-box checks, not an assertion about all runtime collision: `RoofBox`, `ExtraBoxes`, pane colliders, and hull decomposition may add or replace shapes. Runtime mesh physics can disable fitted boxes after generating convex hulls. The sedan's BoxSize.Y is not its body height.", ""]
    rows, wheel_rows = [], []
    for k in ordered:
        s, missing, violations = specs[k], [], []
        lo, hi = s["mesh"]["lo"], s["mesh"]["hi"]
        for i, axis in enumerate("XYZ"):
            bmin, bmax = s["BoxCenter"][i] - s["BoxSize"][i]/2, s["BoxCenter"][i] + s["BoxSize"][i]/2
            if bmin - lo[i] > EPS:
                missing.append(f"{axis}− {fmt(bmin-lo[i])}")
            if hi[i] - bmax > EPS:
                missing.append(f"{axis}+ {fmt(hi[i]-bmax)}")
        rows.append([k, vec(s["BoxSize"]), vec(s["BoxCenter"]), vec([s["BoxSize"][i]/s["mesh"]["size"][i] for i in range(3)]), "NO: " + "; ".join(missing) if missing else "YES"])
        for j, w in enumerate(s["Wheels"]):
            for i in (0, 2):
                excess = max(lo[i] - w[i], w[i] - hi[i])
                if excess > EPS:
                    violations.append(f"{j} {'XZ'[i//2]}{'−' if w[i] < lo[i] else '+'} {fmt(excess)}")
        wheel_rows.append([k, "NO anchors" if not s["Wheels"] else ("FAIL" if violations else "PASS"), "; ".join(violations) or "—"])
    table(out, ["Vehicle", "BoxSize (X,Y,Z)", "BoxCenter (X,Y,Z)", "Box/mesh ratios (X,Y,Z)", "Encloses? / missing faces (m)"], rows)
    out += ["## Anchor containment sanity check", "", "Each wheel anchor is tested against both mesh X and Z intervals. This checks anchor centres, not tyre envelopes. Outboard anchors are reported even when consistent across the fleet.", ""]
    table(out, ["Vehicle", "X/Z result", "Wheel ID, offending face, distance outside (m)"], wheel_rows)
    health_scale = number(re.search(r"VehicleHealthScale\s*=\s*(" + NUMBER + ")", uncomment((ROOT / "game/Vehicle.cs").read_text()))[1])
    out += ["## Mass, capacity and drive", "", f"Fuel is the literal capacity in units = mL; litres = units / 1000. Health is the literal Spec.Health; runtime HealthMax multiplies it by VehicleHealthScale = {fmt(health_scale)}. Speed is the configured SpeedMax, not a measured driving result; km/h = m/s × 3.6. Engine is the Spec.Engine coefficient, not horsepower. A mass marked `fallback` uses GlobalMass because the spec declares no mass.", ""]
    table(out, ["Vehicle", "Class", "Mass kg", "Health", "Fuel units/mL", "Fuel L", "SpeedMax m/s", "km/h", "Engine"],
          [[k, "boat" if k in BOATS else "aircraft" if k == "otter" else "towed road" if k == "trailer" else "road", fmt(specs[k]["Mass"]) + (" (fallback)" if specs[k]["mass_default"] else ""), *[fmt(specs[k][v]) for v in ("Health", "Fuel")], fmt(specs[k]["Fuel"]/1000), fmt(specs[k]["SpeedMax"]), fmt(specs[k]["SpeedMax"]*3.6), fmt(specs[k]["Engine"])] for k in ordered])
    out += ["## Seats", "", "Positions are the seat table (or the explicit Spec.Seats override), in index order, with driver at index 0. These are not SeatOffset's visible-body rise or DriverEye. Trailer's fallback is reported as implemented; it is not evidence of a modelled passenger seat.", ""]
    table(out, ["Vehicle", "Count", "Source", "Seat index: (X,Y,Z)"], [[k, len(specs[k]["Seats"]), specs[k]["seat_source"], vlist(specs[k]["Seats"])] for k in ordered])
    out += ["## Light positions", "", "Null SpotPos/TailPos means no declared array; OmniPos defaults to zero where absent. These are light nodes, not inferred lens centres.", ""]
    table(out, ["Vehicle", "SpotPos (X,Y,Z)", "OmniPos (X,Y,Z)", "TailPos (X,Y,Z)"], [[k, vlist(specs[k]["SpotPos"]), vec(specs[k]["OmniPos"]), vlist(specs[k]["TailPos"])] for k in ordered])
    out += ["## Mesh attachments and palettes", "", "GlassMesh names can be prefixes: AttachGlass loads `<prefix>_<pane>.txt`, falling back to the named aggregate only when no pane files exist. Parts is the literal array; additional non-Parts attachments are listed separately. PaintMat samples `vehicle_paint.gdshader`: alpha < 0.5 selects spawn paint; other texels retain RGB. ParseObj flips vt V to 1−V.", ""]
    rows = []
    for k in ordered:
        s = specs[k]
        extras = [f"{f}: {s['fields'][f]}" for f in ("SeatModelFile", "SteerModel", "Treads", "TurretMeshes", "GunMesh", "HeliBodyMeshes") if f in s["fields"]]
        parts = "; ".join(s["Parts"]) or ("null" if "Parts" not in s["fields"] else "[]")
        rows.append([k, s["Wheel"], s["WheelTex"], s["Palette"], s["GlassMesh"], parts, "; ".join(extras) or "—"])
    table(out, ["Vehicle", "Wheel mesh", "Wheel texture", "Palette", "GlassMesh", "Parts meshes", "Other mesh fields"], rows)
    out += ["## Remaining body-file inventory", "", "Aircraft here have geometry/counts only; their spec fields and sanity checks were explicitly skipped above. Train uses Train.cs rather than Vehicle.Spec. The SKS row inventories the filename match but is excluded from vehicle statistics.", ""]
    used = {s["Body"] for s in specs.values()}
    remaining = sorted(p for p in files if p.name not in used)
    table(out, headers, [geometry_row(f"`{p.name}`", obj(p)) for p in remaining])
    out += ["## Duplicate-number sanity check", "", "Exact numeric equality is tested for the field groups below. Body AABB comparison uses 0.0001 m rounding to expose exporter differences of a few millionths of a metre. Shared defaults and reused geometry are possible explanations; equality alone does not establish an error. Null/empty arrays are excluded from duplication groups. Baseline only; wagon's declared inheritance is documented separately.", ""]
    duplicate_rows = []
    categories = {
        "Seats": lambda s: s["Seats"],
        "Wheel anchors incl. steer": lambda s: s["Wheels"],
        "BoxSize + BoxCenter": lambda s: (s["BoxSize"], s["BoxCenter"]),
        "SpotPos + OmniPos + TailPos": lambda s: (s["SpotPos"], s["OmniPos"], s["TailPos"]) if s["SpotPos"] or s["TailPos"] else None,
        "Radius + Engine + SpeedMax": lambda s: tuple(s[k] for k in ("WheelRadius", "Engine", "SpeedMax")),
        "Mass + Health + Fuel": lambda s: tuple(s[k] for k in ("Mass", "Health", "Fuel")),
        "AABB min + max (rounded 0.0001 m)": lambda s: tuple(round(v, 4) for bound in (s["mesh"]["lo"], s["mesh"]["hi"]) for v in bound),
        "Length + width (rounded 0.0001 m)": lambda s: tuple(round(s["mesh"]["size"][i], 4) for i in (2, 0)),
    }
    for label, getter in categories.items():
        groups = defaultdict(list)
        for k, s in base.items():
            value = getter(s)
            if value:
                groups[repr(value)].append(k)
        duplicate_rows.extend([label, ", ".join(sorted(names)), value] for value, names in groups.items() if len(names) > 1)
    table(out, ["Fields compared", "Vehicles sharing values", "Values (XYZ tuples where applicable)"], duplicate_rows)
    # Seat table includes the permitted skipped aircraft; audit these arrays without inferring their specs.
    src = uncomment((ROOT / "game/Vehicle.cs").read_text())
    seat_groups = defaultdict(list)
    for m in re.finditer(r'\["([^"]+)"\]\s*=\s*new\[\]\s*\{([^}]+)\}', braced(src, src.index("{", src.index("SeatTable =")))):
        if m[1] != "wagon":
            seat_groups[repr(vectors(m[2]))].append(m[1])
    out.append("Full SeatTable equality groups (including skipped aircraft): " + "; ".join(", ".join(v) for v in seat_groups.values() if len(v) > 1) + ".")
    out += ["", "## Sedan baseline and the ambulance constraint", ""]
    sedan, ambulance = base["sedan"]["mesh"], base["ambulance"]["mesh"]
    out.append(f"Measured sedan length {fmt(sedan['size'][2])} m − ambulance length {fmt(ambulance['size'][2])} m = {fmt(sedan['size'][2]-ambulance['size'][2])} m. Thus extending the sedan body cannot also keep the wagon shorter than this ambulance. This is a conflict in the requested constraints, not a reason to substitute BoxSize for a mesh measurement. Sedan BoxSize.Z = {fmt(base['sedan']['BoxSize'][2])} m; ambulance BoxSize.Z = {fmt(base['ambulance']['BoxSize'][2])} m.")
    if "wagon" in specs:
        design = ROOT / "notes/wagon_dimensions.md"
        assert design.exists(), "Write the wagon dimension derivation before updating fleet tables"
        out += ["", design.read_text().strip()]
    out += ["", "## Derived baseline fleet statistics", "",
            "Population A = all existing road specs, including tank/APC and the trailer (whose SpeedMax is zero). Population B = the same road specs plus runabout and ship. Aircraft, train, firearm and the new wagon are excluded. Each vehicle contributes once to dimensions/mass/speed/wheelbase; track contributes once per axle so different front/rear tracks remain represented. Boats contribute no wheelbase/track samples. Median is the midpoint of the two central values for an even sample count. All bounds are body-only. Width maxima identify the widest road vehicle without subtracting bounds.", ""]
    stats_rows = []
    for label, keys in (("A road", ROAD), ("B road + boat", ROAD + BOATS)):
        for metric in ("length", "width", "height", "wheelbase", "track", "mass", "top speed m/s", "top speed km/h"):
            pairs = []
            for k in keys:
                s = base[k]
                if metric in ("length", "width", "height"):
                    vals = [s["mesh"]["size"][("width", "height", "length").index(metric)]]
                elif metric == "wheelbase":
                    vals = [] if s["wheelbase"] is None else [s["wheelbase"]]
                elif metric == "track":
                    vals = s["tracks"]
                elif metric == "mass":
                    vals = [s["Mass"]]
                else:
                    vals = [s["SpeedMax"] * (3.6 if metric.endswith("km/h") else 1)]
                pairs.extend((x, k) for x in vals)
            values = [p[0] for p in pairs]
            low, high = min(values), max(values)
            stats_rows.append([label, metric, len(values), fmt(low), ", ".join(sorted({k for x, k in pairs if x == low})), fmt(statistics.median(values)), fmt(high), ", ".join(sorted({k for x, k in pairs if x == high}))])
    table(out, ["Population", "Metric (m unless stated)", "n", "Min", "Min vehicle(s)", "Median", "Max", "Max vehicle(s)"], stats_rows)
    target = ROOT / "notes/vehicle_measurements.md"
    target.parent.mkdir(exist_ok=True)
    target.write_text("\n".join(out))
    print(f"Wrote {target.relative_to(ROOT)}: {len(ordered)} complete specs, {len(remaining)} inventory-only meshes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    write_notes(read_specs())
