"""Scrap minicopter for Blender 5.1: blender -b --python scene.py

All authored AND Blender world coordinates are metres, right handed, Y up,
nose -Z. This deliberately differs from Blender's usual Z-up convention.
The body export contains no rotor blades. The preview adds two blade meshes.
Studio geometry is excluded. OBJ vertices are world-space; solid colours are
in minicopter.mtl. The .blend and JSON also retain the exact rotor anchors.
"""

import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector, Matrix


OUT = Path(__file__).resolve().parent
sys.path.insert(0, str(OUT))
from verify_obj import verify_exports
MAIN_HUB = Vector((0.00, 1.22, 0.55))
TAIL_HUB = Vector((0.09, 0.02, 2.46))
MAIN_RADIUS = 2.85
TAIL_RADIUS = 0.34
LIMITS = {
    "body": ((0.00, 0.05, 0.20), (1.05, 0.80, 1.60)),
    "front_axle": ((0.00, -0.46, -1.05), (1.85, 0.22, 0.30)),
    "keel_tail": ((0.00, -0.30, 1.30), (0.24, 0.24, 2.60)),
    "tail_wheel": ((0.00, -0.50, 2.35), (0.20, 0.30, 0.20)),
}


def bounds(points):
    return [[min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)]]


def pretty(value):
    return tuple(round(float(v), 6) for v in value)


def material(name, rgb=None, hex_colour=None):
    """A solid diffuse colour; no texture nodes or metallic/specular shader."""
    if hex_colour:
        srgb = [int(hex_colour[i:i+2], 16) / 255 for i in (1, 3, 5)]
        rgb = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055)**2.4
               for c in srgb]
    else:
        srgb = [12.92*c if c <= 0.0031308 else 1.055*c**(1/2.4)-0.055
                for c in rgb]
    mat = bpy.data.materials.new(name)
    mat["srgb"] = srgb
    if hex_colour:
        mat["srgb_hex"] = hex_colour
    mat.diffuse_color = (*rgb, 1.0)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    diffuse = nodes.new("ShaderNodeBsdfDiffuse")
    diffuse.inputs["Color"].default_value = (*rgb, 1.0)
    diffuse.inputs["Roughness"].default_value = 0.0
    output = nodes.new("ShaderNodeOutputMaterial")
    mat.node_tree.links.new(diffuse.outputs[0], output.inputs["Surface"])
    return mat


class MeshBuilder:
    """Small polygon primitives, combined without adding bevels or subdivisions."""

    def __init__(self, name, palette, origin=(0, 0, 0)):
        self.name, self.palette = name, palette
        self.origin = Vector(origin)
        self.vertices, self.faces, self.materials = [], [], []
        self.groups = {}
        self.face_groups = []

    def add(self, verts, faces, mat=0, group=None):
        start = len(self.vertices)
        self.vertices.extend(tuple(Vector(v) - self.origin) for v in verts)
        self.faces.extend(tuple(start + i for i in face) for face in faces)
        self.materials.extend([mat] * len(faces) if isinstance(mat, int) else mat)
        self.face_groups.extend([group or self.name] * len(faces))
        if group:
            self.groups.setdefault(group, []).extend(tuple(v) for v in verts)

    def box(self, center, size, mat=0, group=None):
        c, h = Vector(center), Vector(size) * 0.5
        verts = [c + Vector((x*h.x, y*h.y, z*h.z)) for x,y,z in
                 [(-1,-1,-1), (1,-1,-1), (1,1,-1), (-1,1,-1),
                  (-1,-1,1), (1,-1,1), (1,1,1), (-1,1,1)]]
        self.add(verts, [(0,3,2,1), (4,5,6,7), (0,1,5,4),
                         (1,2,6,5), (2,3,7,6), (3,0,4,7)], mat, group)

    def tube(self, a, b, radius, mat=0, group=None, caps=False, sides=6):
        """Six flat sides. Hidden welded ends omit caps to save triangles."""
        a, b = Vector(a), Vector(b)
        axis = (b-a).normalized()
        reference = Vector((0,1,0)) if abs(axis.y) < 0.95 else Vector((0,0,1))
        u = reference.cross(axis).normalized()
        v = axis.cross(u)
        ring = [radius * (math.cos(i*math.tau/sides)*u +
                          math.sin(i*math.tau/sides)*v) for i in range(sides)]
        verts = [p + offset for p in (a,b) for offset in ring]
        faces = [(i, (i+1)%sides, (i+1)%sides+sides, i+sides)
                 for i in range(sides)]
        if caps:
            faces += [tuple(reversed(range(sides))), tuple(range(sides, 2*sides))]
        self.add(verts, faces, mat, group)

    def wheel(self, center, width, radius, group):
        # Axis X. Six sides with a flat contact patch at center.y - radius.
        center = Vector(center)
        circumradius = radius / math.cos(math.pi/6)
        verts = [center + Vector((x*width/2,
                 circumradius*math.cos(math.pi/6+i*math.tau/6),
                 circumradius*math.sin(math.pi/6+i*math.tau/6)))
                 for x in (-1,1) for i in range(6)]
        faces = [(i,(i+1)%6,(i+1)%6+6,i+6) for i in range(6)]
        faces += [tuple(reversed(range(6))), tuple(range(6,12))]
        self.add(verts, faces, [1]*6+[0,0], group)

    def finish(self, collection):
        mesh = bpy.data.meshes.new(self.name + "Mesh")
        mesh.from_pydata(self.vertices, [], self.faces)
        for mat in self.palette:
            mesh.materials.append(mat)
        for poly, mat in zip(mesh.polygons, self.materials):
            poly.material_index = mat
            poly.use_smooth = False
        names = list(dict.fromkeys(self.face_groups))
        part_ids = mesh.attributes.new("part_id", "INT", "FACE")
        for slot, name in zip(part_ids.data, self.face_groups):
            slot.value = names.index(name)
        mesh.update()
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bmesh.ops.triangulate(bm, faces=list(bm.faces),
                             quad_method="FIXED", ngon_method="EAR_CLIP")
        bm.to_mesh(mesh)
        bm.free()
        mesh.update()
        obj = bpy.data.objects.new(self.name, mesh)
        collection.objects.link(obj)
        obj.location = self.origin
        obj["part_names"] = names
        return obj


def build_vehicle(palette, collection):
    body = MeshBuilder("Airframe", palette)
    # 7 cm welded rails and crossmembers: an open ladder, not a solid chassis.
    # The cockpit is 0.72 m wide, pulled in around a single 0.50 m seat.
    for x in (-0.325, 0.325):
        body.tube((x,-0.315,-0.565), (x,-0.315,0.965), 0.035, 0, "body")
    for z in (-0.565, 0.965):
        body.tube((-0.325,-0.315,z), (0.325,-0.315,z), 0.035, 0, "body")
    # Seat mount, slanted back stays, and rear diagonal engine cradle.
    body.box((0,-0.255,-0.20), (0.62,0.12,0.16), 0, "body")
    for x in (-0.29, 0.29):
        body.tube((x,-0.315,0.27), (x,0.405,0.245), 0.032, 0, "body")
        body.tube((x,0.405,0.245), (x,-0.315,0.94), 0.032, 0, "body")

    # One connected L-section shell, centred on X=0. Brown contact surfaces.
    # A distinct mesh permits independent seat-count verification in the OBJ.
    seat = MeshBuilder("PilotSeat", palette)
    profile = [(-0.23,-0.53), (-0.11,-0.53), (-0.11,-0.005),
               (0.45,0.14), (0.45,0.27), (-0.23,0.085)]
    verts = [(side*(0.21 if y > 0.4 else 0.25), y, z)
             for side in (-1,1) for y,z in profile]
    faces = [(i,(i+1)%6,(i+1)%6+6,i+6) for i in range(6)]
    faces += [tuple(reversed(range(6))), tuple(range(6,12))]
    seat.add(verts, faces, 3, "body")

    # The cyclic mounts ahead of the pan; no stalk intersects the seat shell.
    body.tube((0,-0.315,-0.565), (0,0.10,-0.54), 0.028, 0, "body")
    body.box((0,0.10,-0.54), (0.15,0.055,0.065), 1, "body")

    # A narrow centred engine behind the pilot, with an exposed hex pulley.
    body.box((0,-0.14,0.59), (0.42,0.31,0.49), 1, "body")
    body.box((0,0.075,0.59), (0.48,0.12,0.40), 0, "body")
    body.tube((0.215,-0.10,0.61), (0.335,-0.10,0.61),
              0.14, 0, "body", caps=True)
    body.tube((-0.235,-0.03,0.64), (-0.29,0.17,0.89),
              0.043, 1, "body", caps=True)

    # Broad, dull RED scrap fuel can, with a low carry handle and filler cap.
    # These use the supplied colours directly, without textures or extra hues.
    body.box((0,0.245,0.805), (0.62,0.29,0.33), 2, "body")
    for x in (-0.10,0.10):
        body.box((x,0.410,0.81), (0.035,0.040,0.055), 2, "body")
    body.box((0,0.4375,0.81), (0.235,0.025,0.055), 2, "body")
    body.tube((0.235,0.39,0.805), (0.235,0.422,0.805),
              0.045, 0, "body", caps=True)
    # A few large paint losses: flat polygons using the SAME supplied brown.
    # Tiny offsets keep these from z-fighting with the can, without textures.
    body.add([(-0.295,0.108,0.6398), (-0.295,0.172,0.6398),
              (-0.249,0.161,0.6398), (-0.224,0.124,0.6398),
              (-0.152,0.108,0.6398)], [(0,1,2,3,4)], 3, "body")
    body.add([(0.3102,0.108,0.69), (0.3102,0.171,0.69),
              (0.3102,0.184,0.733), (0.3102,0.142,0.749),
              (0.3102,0.108,0.811)], [(0,1,2,3,4)], 3, "body")
    # Small repurposed red brackets bring the rusty accent toward the nose.
    for side in (-1,1):
        body.box((side*0.316,-0.244,-0.31), (0.055,0.14,0.34), 2, "body")

    # Low axle and short nose outriggers. Front wheels fit the given axle box.
    body.box((0,-0.46,-1.05), (1.65,0.075,0.30), 0, "front_axle")
    for side in (-1,1):
        body.wheel((side*0.835,-0.46,-1.05), 0.18, 0.11, "front_axle")
        body.tube((side*0.29,-0.315,-0.53), (side*0.69,-0.46,-1.05),
                  0.038, 0, "landing_struts")
        body.box((side*0.255,-0.403,-0.89), (0.22,0.055,0.27), 0, "footrests")

    # The boom occupies the exact 0.24 x 0.24 x 2.60 envelope, tapering aft.
    verts = [(x*h, -0.30+y*h, z) for z,h in ((0,0.12),(2.60,0.065))
             for x,y in ((-1,-1),(1,-1),(1,1),(-1,1))]
    body.add(verts, [(0,3,2,1),(4,5,6,7),(0,1,5,4),
                     (1,2,6,5),(2,3,7,6),(3,0,4,7)], 0, "keel_tail")
    worn_top = [(-0.073,1.21), (-0.070,1.43), (-0.014,1.51),
                (0.021,1.39), (0.008,1.23)]
    body.add([(x,-0.30+0.12-0.055*z/2.6+0.0002,z) for x,z in worn_top],
             [(0,1,2,3,4)], 3, "keel_tail")
    # The smaller tail wheel shares the front wheels' y=-0.57 contact plane.
    # Hitboxes are containment envelopes; a wheel need not fill its box.
    body.wheel((0,-0.50,2.35), 0.16, 0.07, "tail_wheel")
    body.box((0,-0.405,2.35), (0.075,0.11,0.10), 0, "tail_wheel")
    body.tube((0,-0.30,2.46), (0,0.02,2.46), 0.05, 0, "tail_support")
    body.tube((0,0.02,2.46), TAIL_HUB, 0.055, 0, "tail_support")

    # Central mast and a triangular pair of stays behind the one pilot seat.
    body.tube((0,-0.22,0.55), MAIN_HUB, 0.06, 0, "mast", caps=True)
    for x in (-0.29,0.29):
        body.tube((x,-0.285,0.90), (0,0.87,0.55), 0.035, 0, "mast_stays")

    # Housings belong to the BODY and survive removal of the blade meshes.
    main_housing = MeshBuilder("MainRotorHousing", palette, MAIN_HUB)
    main_housing.tube(MAIN_HUB + Vector((0,-0.06,0)), MAIN_HUB + Vector((0,0.06,0)),
                      0.135, 0, "main_housing", caps=True)
    tail_housing = MeshBuilder("TailRotorHousing", palette, TAIL_HUB)
    tail_housing.tube(TAIL_HUB+Vector((-0.035,0,0)), TAIL_HUB+Vector((0.035,0,0)),
                      0.067, 2, "tail_housing", caps=True)

    main = MeshBuilder("PreviewMainBlades", palette, MAIN_HUB)
    # 42 degrees gives a useful stopped-rotor silhouette and ~4.2 m length.
    angle = math.radians(42)
    direction = Vector((math.cos(angle),0,math.sin(angle)))
    chord = Vector((-math.sin(angle),0,math.cos(angle)))
    normal = Vector((0,1,0))
    for side in (-1,1):
        add_blade(main, MAIN_HUB, side*direction, chord, normal,
                  MAIN_RADIUS, 0.13, 0.10, 0.085, tip_band=True)

    tail = MeshBuilder("PreviewTailBlades", palette, TAIL_HUB)
    # Axis X; blades are in the YZ plane, exactly perpendicular to the main.
    direction = Vector((0,0,1))
    chord = Vector((0,1,0))
    for side in (-1,1):
        add_blade(tail, TAIL_HUB, side*direction, chord, Vector((1,0,0)),
                  TAIL_RADIUS, 0.065, 0.045, 0.045, tip_band=False)

    body_objects = [b.finish(collection) for b in (body,seat,main_housing,tail_housing)]
    for obj in body_objects:
        weather_frame(obj)
    preview_collection = bpy.data.collections.new("PREVIEW ONLY - engine creates blades")
    bpy.context.scene.collection.children.link(preview_collection)
    blades = [b.finish(preview_collection) for b in (main,tail)]
    for obj, axis in zip(blades, ((0,1,0),(1,0,0))):
        obj["spin_axis_Y_up"] = axis
        obj["pivot_Y_up_metres"] = list(obj.location)
        obj["preview_only_do_not_ship"] = True
    for name, verts in seat.groups.items():
        body.groups.setdefault(name, []).extend(verts)
    bpy.context.view_layer.update()
    return body_objects, blades, body.groups


def weather_frame(obj):
    """Assign scrap-steel colours to existing triangles; never change geometry.

    Indices are local to each named part's triangulated faces. Deliberate,
    asymmetric selections leave most steel black, pick out grey facets, and
    place sparse oxide wedges on rails, stays, wheel rims and exposed edges.
    The boom's existing irregular wear polygon supplies a larger rust patch.
    Red paint, rubber/engine charcoal and the brown seat are left untouched.
    """
    if obj.name not in {"Airframe", "MainRotorHousing"}:
        return
    # Palette slots: 0 black steel, 4 grey steel, 5 dark oxide, 6 orange oxide.
    regions = {
        "body": {
            4: [0, 140, 6, 146, 12, 152, 25, 165, 44, 57, 197,
                72, 212, 75, 215, 76, 216, 78, 218, 85, 227, 228,
                120, 268, 125, 275, 276, 277],
            5: [19, 170, 184, 229],
            6: [147, 167, 122],
        },
        "front_axle": {
            4: [4, 26, 12, 34, 36, 13, 37, 39, 20, 46, 48, 21, 49, 51],
            5: [22, 35], 6: [50],
        },
        "landing_struts": {4: [0, 12, 6, 18], 5: [20], 6: [2]},
        "footrests": {4: [4, 10, 22], 5: [16], 6: [18]},
        "keel_tail": {4: [4, 11], 5: [13], 6: [6, 14]},
        "tail_wheel": {4: [6, 20, 22, 7, 23, 25, 12, 30], 5: [29], 6: [24]},
        "tail_support": {4: [3, 15, 7, 19], 5: [16], 6: [23]},
        "mast": {4: [2, 10, 3], 5: [11]},
        "mast_stays": {4: [0, 12, 8], 5: [20], 6: [17]},
        "main_housing": {4: [7, 17, 18, 19, 3, 11], 5: [12]},
    }
    names = obj["part_names"]
    ids = obj.data.attributes["part_id"].data
    for group, materials in regions.items():
        faces = [p for p in obj.data.polygons if names[ids[p.index].value] == group]
        if not faces:
            continue
        selected = set()
        for mat, indices in materials.items():
            for i in indices:
                assert i not in selected, (group, i)
                selected.add(i)
                face = faces[i]
                # Only the pre-existing brown boom patch may change with steel.
                assert face.material_index == 0 or (group == "keel_tail" and face.material_index == 3), (obj.name, group, i)
                face.material_index = mat


def add_blade(builder, hub, direction, chord, normal, radius,
              root_half_width, tip_half_width, root, tip_band):
    # Tip corners, not just the centerline, are at the specified swept radius.
    end = math.sqrt(radius*radius - tip_half_width*tip_half_width)
    sections = [(root,root_half_width)]
    if tip_band:
        s = end - 0.31
        w = root_half_width + (tip_half_width-root_half_width)*(s-root)/(end-root)
        sections.append((s,w))
    sections.append((end,tip_half_width))
    verts = [hub+direction*s+chord*(side*w)+normal*(height*0.025)
             for s,w in sections for side,height in ((-1,-1),(1,-1),(1,1),(-1,1))]
    faces, mats = [(3,2,1,0)], [1]
    for section in range(len(sections)-1):
        for edge in range(4):
            i, j = 4*section, (edge+1)%4
            faces.append((i+edge,i+j,i+j+4,i+edge+4))
            mats.append(2 if tip_band and section == 1 else (0 if not tip_band else 1))
    faces.append(tuple(range(len(verts)-4,len(verts))))
    mats.append(2)
    builder.add(verts, faces, mats)


def world_vertices(obj):
    return [tuple(obj.matrix_world @ v.co) for v in obj.data.vertices]


def validate_model(body_objects, blades, groups):
    objects = body_objects + blades
    report = {"coordinate_system": "right-handed, Y up, nose -Z; metres",
              "parts": {}, "rotors": {}}
    for name, (center,size) in LIMITS.items():
        expected = [list(Vector(center)-Vector(size)/2),
                    list(Vector(center)+Vector(size)/2)]
        measured = bounds(groups[name])
        assert all(measured[0][i] >= expected[0][i]-1e-6 and
                   measured[1][i] <= expected[1][i]+1e-6 for i in range(3)), \
            f"{name} exceeds its hitbox: {measured} vs {expected}"
        report["parts"][name] = {"hitbox_min_max": expected, "mesh_min_max": measured}
        print(f"HITBOX {name}: PASS; mesh min={pretty(measured[0])} max={pretty(measured[1])}", flush=True)
    for obj, hub, radius, axis in zip(blades, (MAIN_HUB,TAIL_HUB),
                                    (MAIN_RADIUS,TAIL_RADIUS), (1,0)):
        assert (obj.matrix_world.translation-hub).length < 1e-6
        offsets = [Vector(v)-hub for v in world_vertices(obj)]
        measured_radius = max(math.sqrt(sum(v[i]**2 for i in range(3) if i != axis))
                              for v in offsets)
        assert abs(measured_radius-radius) < 1e-6, (obj.name, measured_radius)
        assert all(not p.use_smooth and len(p.vertices) == 3 for p in obj.data.polygons)
        report["rotors"][obj.name] = {"origin": list(hub), "radius": radius,
                                      "axis": [int(i == axis) for i in range(3)]}
    report["triangle_count"] = sum(len(obj.data.polygons) for obj in objects)
    assert 200 <= report["triangle_count"] <= 700, report["triangle_count"]
    report["bounding_box_min_max"] = bounds([v for obj in objects for v in world_vertices(obj)])
    return report


def export_obj(path, objects):
    """Write world-space Y-up triangles with flat normals and semantic groups.

    No axis conversion: Blender world coordinates already use the game basis.
    OBJ/MTL names avoid spaces so both files work with simple game importers.
    """
    lines = ["# Metres; Y up; nose -Z", "mtllib minicopter.mtl", "s off"]
    vertex_offset, normal_offset = 1, 1
    for obj in objects:
        lines.append(f"o {obj.name}")
        for vertex in world_vertices(obj):
            lines.append("v " + " ".join(f"{c:.9f}" for c in vertex))
        transform = obj.matrix_world.to_3x3().inverted().transposed()
        for poly in obj.data.polygons:
            normal = (transform @ poly.normal).normalized()
            lines.append("vn " + " ".join(f"{c:.9f}" for c in normal))
        previous_group, previous_material = None, None
        ids = obj.data.attributes["part_id"].data
        names = obj["part_names"]
        for poly in obj.data.polygons:
            assert len(poly.vertices) == 3 and not poly.use_smooth
            group = names[ids[poly.index].value]
            mat = obj.data.materials[poly.material_index].name
            if group != previous_group:
                lines.append(f"g {group}")
                previous_group = group
            if mat != previous_material:
                lines.append(f"usemtl {mat}")
                previous_material = mat
            lines.append("f " + " ".join(
                f"{vertex_offset+i}//{normal_offset+poly.index}" for i in poly.vertices))
        vertex_offset += len(obj.data.vertices)
        normal_offset += len(obj.data.polygons)
    path.write_text("\n".join(lines) + "\n")


def export_and_verify(body_objects, blades, palette, report):
    lines = ["# Solid diffuse colours. Kd channels use the supplied sRGB swatches.",
             "# No textures, specular highlights, or metallic shading."]
    for mat in palette:
        lines.extend([f"newmtl {mat.name}",
                      "Kd " + " ".join(f"{c:.9f}" for c in mat["srgb"]),
                      "Ka 0.000000000 0.000000000 0.000000000",
                      "Ks 0.000000000 0.000000000 0.000000000",
                      "Ns 0", "d 1", "illum 1", ""])
    (OUT / "minicopter.mtl").write_text("\n".join(lines))
    export_obj(OUT / "minicopter_body.obj", body_objects)
    export_obj(OUT / "minicopter_preview.obj", body_objects + blades)
    # Replace the old ambiguous filename with the same safe, blade-free body.
    (OUT / "minicopter.obj").write_bytes((OUT / "minicopter_body.obj").read_bytes())
    report["exports"] = verify_exports(OUT)


def aim_y_up(obj, target):
    forward = (Vector(target)-obj.location).normalized()
    right = forward.cross(Vector((0,1,0))).normalized()
    up = right.cross(forward).normalized()
    obj.rotation_euler = Matrix((right,up,-forward)).transposed().to_euler()


def setup_studio(objects):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.eevee.taa_render_samples = 48
    scene.eevee.use_raytracing = False
    scene.eevee.use_fast_gi = True
    scene.eevee.fast_gi_method = "AMBIENT_OCCLUSION_ONLY"
    scene.eevee.fast_gi_distance = 0.7
    scene.render.resolution_x, scene.render.resolution_y = 1280, 720
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.render.film_transparent = False
    scene.render.filepath = str(OUT / "minicopter_render.png")
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    # Preserve the dull, oxidised appearance instead of over-lighting red paint.
    scene.view_settings.exposure = -0.85
    scene.view_settings.gamma = 1
    world = bpy.data.worlds.new("Neutral studio")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.32,0.35,0.37,1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.35
    scene.world = world
    stage = bpy.data.collections.new("Studio - excluded from OBJ")
    scene.collection.children.link(stage)
    ground = MeshBuilder("StudioFloor", [material("Neutral ground", (0.38,0.40,0.40))])
    ground.add([(-200,-0.577,-200),(-200,-0.577,200),
                (200,-0.577,200),(200,-0.577,-200)], [(0,1,2,3)])
    floor = ground.finish(stage)
    floor.pass_index = 0
    for obj in objects:
        obj.pass_index = 1
    target = Vector((0,0.26,0.48))
    data = bpy.data.cameras.new("Three-quarter front")
    camera = bpy.data.objects.new("Camera", data)
    stage.objects.link(camera)
    camera.location = target + Vector((6.8,4.8,-9.3))
    aim_y_up(camera, target)
    data.type = "ORTHO"
    data.lens = 45
    data.clip_end = 500
    scene.camera = camera
    bpy.context.view_layer.update()
    # Fit the complete rotor tips in the 16:9 composition, with even margins.
    inverse = camera.matrix_world.inverted()
    points = [inverse @ Vector(v) for obj in objects for v in world_vertices(obj)]
    bb = bounds(points)
    aspect = 1280/720
    data.ortho_scale = max(bb[1][0]-bb[0][0], aspect*(bb[1][1]-bb[0][1])) * 1.20
    camera.location += camera.rotation_euler.to_matrix() @ Vector(
        ((bb[0][0]+bb[1][0])/2, (bb[0][1]+bb[1][1])/2, 0))
    for name, position, power, size, colour in [
        ("Key", (-3.5,6,-4.5), 500, 4.0, (1.0,0.96,0.90)),
        ("Fill", (4.5,3,-1), 220, 5.0, (0.86,0.92,1.0)),
        ("Rim", (1,4,5.5), 600, 3.0, (1.0,0.98,0.91)),
    ]:
        light_data = bpy.data.lights.new(name, "AREA")
        light_data.energy, light_data.shape, light_data.size = power, "DISK", size
        light_data.color = colour
        light = bpy.data.objects.new(name, light_data)
        stage.objects.link(light)
        light.location = position
        aim_y_up(light, (0,0.0,0.6))
    bpy.context.view_layer.update()
    return scene


def verify_render(scene, objects):
    # Reload the on-disk PNG, not the potentially stale Render Result buffer.
    from bpy_extras.object_utils import world_to_camera_view
    import numpy as np
    path = Path(scene.render.filepath)
    assert path.is_file() and path.stat().st_size > 10000, "Missing/empty PNG"
    img = bpy.data.images.load(str(path), check_existing=False)
    assert tuple(img.size) == (1280,720), tuple(img.size)
    pixels = np.empty(1280*720*4, dtype=np.float32)
    img.pixels.foreach_get(pixels)
    pixels = pixels.reshape(720,1280,4)
    rgb = pixels[:,:,:3]
    assert np.isfinite(rgb).all() and float(rgb.max()) > 0.20, "Black/invalid PNG"
    assert float(np.std(rgb)) > 0.035, "Uniform/empty PNG"
    # The exposed seat + engine must differ from the neutral background.
    p = world_to_camera_view(scene, scene.camera, Vector((0,-0.01,0.25)))
    x, y = int(p.x*1280), int(p.y*720)
    crop = rgb[max(0,y-65):min(720,y+65),max(0,x-90):min(1280,x+90)]
    assert crop.size and float(np.std(crop)) > 0.06, "Vehicle not visible in PNG"
    red_pixels = (rgb[:,:,0] > rgb[:,:,1]*1.7) & (rgb[:,:,0] > rgb[:,:,2]*1.5) & (rgb[:,:,0] > 0.15)
    assert int(red_pixels.sum()) > 700, "Dull red fuel can is not visible"
    for obj in objects:
        for v in world_vertices(obj):
            p = world_to_camera_view(scene, scene.camera, Vector(v))
            assert 0.02 < p.x < 0.98 and 0.02 < p.y < 0.98 and p.z > 0, "Vehicle cropped"
    result = {"size": [1280,720], "bytes": path.stat().st_size,
              "rgb_std": float(np.std(rgb)), "vehicle_crop_std": float(np.std(crop)),
              "visible_red_pixels": int(red_pixels.sum()), "engine": scene.render.engine,
              "verified": True}
    used_materials = {mat for obj in scene.objects if obj.type == "MESH"
                      for mat in obj.data.materials}
    for mat in used_materials:
        assert {node.type for node in mat.node_tree.nodes} == {"BSDF_DIFFUSE", "OUTPUT_MATERIAL"}
    result["material_hex"] = {
        mat.name: "#" + "".join(f"{round(c*255):02x}" for c in mat["srgb"])
        for mat in sorted(used_materials, key=lambda mat: mat.name)
    }
    bpy.data.images.remove(img)
    print(f"PNG READ-BACK: PASS {result}", flush=True)
    return result


def main():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.gravity = (0,-9.81,0)
    collection = bpy.data.collections.new("Minicopter")
    scene.collection.children.link(collection)
    palette = [
        material("Frame_Black_25282a", hex_colour="#25282a"),
        # Preserve the previous model's neutral rubber/engine colour.
        material("Charcoal_Rubber_Engine", (0.035,0.043,0.037)),
        material("Fuel_Red_9e291f", hex_colour="#9e291f"),
        material("Seat_Brown_6b472a", hex_colour="#6b472a"),
        material("Steel_Grey_55595b", hex_colour="#55595b"),
        material("Rust_Dark_804322", hex_colour="#804322"),
        material("Rust_Orange_b46532", hex_colour="#b46532"),
    ]
    body_objects, blades, groups = build_vehicle(palette, collection)
    objects = body_objects + blades
    report = validate_model(body_objects, blades, groups)
    export_and_verify(body_objects, blades, palette, report)
    scene = setup_studio(objects)
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT / "minicopter.blend"), check_existing=False)
    bpy.ops.render.render(write_still=True)
    report["render"] = verify_render(scene, objects)
    (OUT / "minicopter_checks.json").write_text(json.dumps(report, indent=2) + "\n")
    print("COMPLETE: minicopter_body.obj; minicopter_preview.obj; minicopter_render.png", flush=True)


if __name__ == "__main__":
    main()
