using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Placeable DOOR spawn seam, mirroring FridgeDeploy/FluidDeploy: a DeployableDef carrying a DoorProp name
    // places a standalone swinging door instead of a plain Deployable body.
    //
    // This deliberately reuses ObjectDoor -- the CONTAINER door -- rather than the old building Door.cs.
    // strawberry_cow, 2026-08-09: "screw the door mechanics. they were done unattended and never actually
    // properly tested. for all im concerned, that door doesnt exist. the container doors ARE tested and
    // proven, however." That is the better evidence: Door.cs had 11 green headless tests, one hardcoded demo
    // instance in WorldBuilder, no way for a player to make one, and a BoxMesh for a leaf. ObjectDoor gets
    // opened by hand in game every session.
    //
    // What comes free by reusing it, none of which is re-implemented here: the catalog-driven hinge
    // (pivot/axis/signed angle/duration), the retail easing curves sampled off the real animation clips, the
    // per-toggle sound, the group sync that swings a double door's two leaves together, and a leaf collider
    // that TRACKS the swing -- solid at the endpoints, never solid mid-swing so it cannot trap you, and held
    // open when a body is standing in its volume.
    //
    // A standalone door needs no extra collision work: ObjectDoor's leaf collider already blocks movement
    // (StoreShelf: "the player can't walk through the doorway anymore"), which is the one thing a container
    // door was expected to borrow from its parent prop and turns out to own itself.
    public static class DoorDeploy
    {
        static Dictionary<string, List<WorldBuilder.DoorCatalogEntry>> _cat;
        static readonly Dictionary<string, Material> _mats = new();

        /// <summary>Place a door prop at <paramref name="pos"/> facing <paramref name="yawDeg"/>. Returns the
        /// host node, or null if the prop has no catalog entry / no loadable leaf mesh -- callers treat null
        /// as "not a door" and fall through, same as FridgeDeploy.</summary>
        public static Node3D SpawnFor(DeployableDef def, Node parent, Vector3 pos, float yawDeg)
            => SpawnProp(def?.DoorProp, parent, pos, yawDeg);

        /// <summary>Place a door by PROP NAME, optionally scaled to fit an opening.
        ///
        /// Split out of SpawnFor so a wall can hang a door in one of its holes (strawberry_cow: "have these
        /// doors as things i can enable on relevant openings") through the same catalog lookup, the same
        /// hinge data and the same stand-up as a standalone placement. Two callers, one implementation --
        /// a second copy would be a second thing to get the stand-up wrong in.
        ///
        /// `fit` scales the leaf to a hole of that width/height. The scale is baked into the MESH and the
        /// PIVOT rather than set on a node: a non-uniform Scale on an ancestor of the hinge shears the leaf
        /// as it swings, because the rotation would then happen in scaled space. Scaling the geometry keeps
        /// the swing rigid.</summary>
        public static Node3D SpawnProp(string prop, Node parent, Vector3 pos, float yawDeg, Vector2? fit = null)
        {
            if (string.IsNullOrEmpty(prop) || parent == null) return null;
            var def = new DeployableDef { DoorProp = prop };

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            _cat ??= WorldBuilder.LoadDoorCatalog(dir);
            if (_cat == null || !_cat.TryGetValue(def.DoorProp, out var leaves) || leaves.Count == 0)
                leaves = WoodenLeaves(def.DoorProp, dir);
            if (leaves == null || leaves.Count == 0) return null;

            var host = new Node3D { Name = def.DoorProp };
            parent.AddChild(host);
            host.GlobalPosition = pos;
            host.RotationDegrees = new Vector3(0f, yawDeg, 0f);

            // STAND-UP = 270 about X, and this number has been wrong once already, so here is the measurement
            // rather than an appeal to convention.
            //
            // These rips carry their HEIGHT on +Z: Door_Pine measures 2.45 x 0.51 x 2.80 (X x Y x Z), the
            // container leaves are the same shape (Container_0 = 1.44 x 0.37 x 3.24). +270 about X maps +Z to
            // +Y and the door stands up. +90 maps +Z to -Y and it points at the floor.
            //
            // I had 270, switched it to DeployableDef.StandRotX (90) on the "barricades stand up with +90"
            // convention, and shipped a door lying on its side -- strawberry_cow spotted it in one look:
            // "genuinely i have no idea where u went wrong. its laying on its side." The convention is real
            // and correct for the deployable TABLE's meshes; it is just not true of these assets, and I
            // checked the convention instead of checking the asset. StoreShelf uses 270 on the container
            // leaves for exactly this reason.
            //
            // Deliberately NOT auto-derived from "make the longest axis vertical": Hatch_Pine is 1.80 x 1.80 x
            // 0.40 and is SUPPOSED to lie flat -- it is a floor hatch. A heuristic that stands every door up
            // would break the one door that belongs on the ground. The regression test asserts the placed
            // height against the mesh's own Z extent, which catches a wrong rotation whatever the cause.
            //
            // Yaw stays on the HOST so the catalog's pivot and axis are consumed in the flat authored frame
            // the extractor wrote them in -- "hinge params are pre-stand-up, compose accordingly".
            var xform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(270f)), Vector3.Zero);
            var mat = MatFor(def.DoorProp, dir);

            var made = new List<ObjectDoor>();
            foreach (var e in leaves)
            {
                var leaf = ObjMesh.Load(dir + e.MeshFile);
                // SAY SO. A silent `continue` here spawns a door with a working hinge, a working collider and
                // no visible leaf -- which reads in game as "the door is invisible", not as "the mesh is
                // missing", and sends you looking at the wrong system.
                if (leaf == null) { GD.PrintErr($"[door] {def.DoorProp}: leaf mesh '{e.MeshFile}' failed to load"); continue; }

                // Fit to the hole. Width is authored X and height is authored Z (these rips stand up via 270),
                // so a hole of w x h wants scale (w/sizeX, 1, h/sizeZ) IN THE FLAT FRAME -- the same frame the
                // pivot is expressed in, which is why the pivot scales with it.
                var pivotL = e.Pivot;
                if (fit is Vector2 f)
                {
                    var sz = leaf.GetAabb().Size;
                    var sc = new Vector3(sz.X > 1e-3f ? f.X / sz.X : 1f, 1f, sz.Z > 1e-3f ? f.Y / sz.Z : 1f);
                    leaf = Scaled(leaf, sc);
                    pivotL *= sc;
                }
                string curveBase = e.MeshFile.EndsWith("_door.obj")
                    ? e.MeshFile.Substring(0, e.MeshFile.Length - "_door.obj".Length)
                    : def.DoorProp;
                made.Add(ObjectDoor.Spawn(host, xform, pivotL, e.Axis, e.AngleDeg, e.DurationSec, leaf, mat,
                    startOpen: false,
                    openCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "open"),
                    closeCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "close"),
                    soundName: e.Sound));
            }
            if (made.Count == 0) { host.QueueFree(); return null; }

            // SIT ON THE SURFACE. These meshes are centred on their own origin (Door_Pine spans z -1.40..+1.40),
            // so placing the host at the surface buries half the door -- which does not read as "sunk", it
            // reads as a squat door, and strawberry_cow called it "laying on its side" from a render. It is
            // standing; it is just half underground.
            //
            // MEASURED off the placed result rather than derived: DeployableDef.GroundLift computes the same
            // thing but bakes StandRotX into its own basis, so it silently disagrees with any door that does
            // not use that rotation. Reading back the actual lowest corner is correct for whatever basis the
            // leaf ended up with, and it is also right for Hatch, which lies flat on purpose.
            float lowest = float.MaxValue;
            foreach (var d in made)
                foreach (var n in d.GetChildren())
                    if (n is Node3D piv)
                        foreach (var c in piv.GetChildren())
                            if (c is MeshInstance3D mi && mi.Mesh != null)
                            {
                                var ab = mi.Mesh.GetAabb();
                                for (int i = 0; i < 8; i++)
                                    lowest = Mathf.Min(lowest, (mi.GlobalTransform * ab.GetEndpoint(i)).Y);
                            }
            if (lowest < float.MaxValue) host.GlobalPosition += Vector3.Up * (pos.Y - lowest);

            // A double door is two catalog lines under one prop name, exactly like Wardrobe_0's Left/Right --
            // grouping makes both leaves answer one interaction, and cow tools split the wooden Doubledoor's
            // two hinges out for this reason.
            if (made.Count > 1) foreach (var d in made) d.SetGroup(made);
            return host;
        }

        // ---- the WOODEN barricade doors -------------------------------------------------------------
        // A second catalog, deliberately not merged into doors.txt. Each file is the output of its own
        // extractor (tools/extract_doors.py for the container props, tools/extract_wooden_door_anims.py for
        // these), so hand-appending these twelve into doors.txt would put rows in a generated file that the
        // next regeneration silently deletes. Two producers, two files, one consumer -- here.
        //
        // The anim rows are keyed by FORM (Door / Doubledoor / Gate / Hatch) while the meshes are per form AND
        // wood (Door_Birch, Door_Maple, ...), so "Door_Birch" resolves its hinge from the "Door" row and its
        // mesh from its own name. The pivots and axes are in the flat authored frame, which is exactly the
        // space the leaf transform stands up from.
        static Dictionary<string, List<(Vector3 Pivot, Vector3 Axis, float Angle, float Dur)>> _wood;

        /// <summary>The wooden catalogue entry a placement would use, for tests -- so a test checks the entry
        /// the game builds rather than re-reading the file and re-deriving the rebase itself.</summary>
        public static List<WorldBuilder.DoorCatalogEntry> WoodenLeavesForTest(string prop, string dir) => WoodenLeaves(prop, dir);

        static List<WorldBuilder.DoorCatalogEntry> WoodenLeaves(string prop, string dir)
        {
            if (_wood == null)
            {
                _wood = new Dictionary<string, List<(Vector3, Vector3, float, float)>>();
                string p = dir + "wooden_door_anims.txt";
                if (System.IO.File.Exists(p))
                {
                    float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var line in System.IO.File.ReadLines(p))
                    {
                        var f = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 10) continue;
                        if (!_wood.TryGetValue(f[0], out var list))
                            _wood[f[0]] = list = new List<(Vector3, Vector3, float, float)>();
                        list.Add((new Vector3(F(f[2]), F(f[3]), F(f[4])),
                                  new Vector3(F(f[5]), F(f[6]), F(f[7])), F(f[8]), F(f[9])));
                    }
                }
            }
            int us = prop.IndexOf('_');
            string form = us > 0 ? prop.Substring(0, us) : prop;
            if (!_wood.TryGetValue(form, out var hinges) || hinges.Count == 0) return null;

            // Doubledoor is TWO hinges against ONE mesh, so honouring both would draw the whole door twice and
            // swing each copy from a different edge. Splitting the mesh into panels is real work and is not
            // done yet -- until it is, this refuses rather than shipping a door that looks like two doors
            // clipping through each other.
            if (hinges.Count > 1)
            {
                GD.PrintErr($"[door] {prop}: {hinges.Count} hinges but one mesh -- the panel split is not implemented, skipping");
                return null;
            }

            var h = hinges[0];
            // strawberry_cow asked for 90 and the rips are 90-100 (retail), so the magnitude is clamped and
            // the SIGN kept -- the sign is which way it swings, and flipping it mirrors the door.
            float angle = Mathf.Sign(h.Angle) * Mathf.Min(90f, Mathf.Abs(h.Angle));

            // REBASE THE PIVOT INTO THE LEAF MESH'S OWN FRAME. The anim rows were authored against a leaf
            // CENTRED on its origin; these rips are not. Door_Pine spans x -2.35..+0.10, so its centre sits at
            // -1.125 while the row says the hinge is at +1.125 -- the exact negation, and 1.02 m clear of any
            // geometry. The door then swung around a point out in mid-air beside itself
            // (strawberry_cow: "it hinges on a weird point far off from where it should").
            //
            // Adding the mesh's own centre puts it back: 1.125 + (-1.125) = 0.0, which is 0.10 m inside the
            // leaf's +X edge -- the SAME inset the authored frame had (1.125 of a 1.225 half-width). Not a
            // fudge factor; the two frames differ by exactly the mesh's offset.
            //
            // Only the WOODEN path needs this. Every container leaf in doors.txt already carries a pivot
            // inside its own mesh (Fridge_0 0.613 in -0.66..0.66, Oven_0 0.500 in -0.75..0.75) -- those rows
            // and those meshes came out of the same extraction, and rebasing them would break them.
            var pivot = h.Pivot;
            var leafMesh = ObjMesh.Load(dir + prop + ".obj");
            if (leafMesh != null)
            {
                var c = leafMesh.GetAabb().GetCenter();
                pivot += new Vector3(c.X, c.Y, c.Z);
            }

            return new List<WorldBuilder.DoorCatalogEntry>
            {
                new WorldBuilder.DoorCatalogEntry
                {
                    MeshFile = prop + ".obj", Pivot = pivot, Axis = h.Axis,
                    // Retail's 0.6333 s reads sluggish next to the container doors' 0.4667 (master: "make it
                    // open way quicker"), so the wooden leaves take the same duration the fridges and wardrobes
                    // already use rather than a number invented for them.
                    AngleDeg = angle, DurationSec = 0.4667f, DefaultOpen = false, Sound = "DoorHandle",
                },
            };
        }

        /// <summary>A copy of `src` with its vertices scaled. Scaling the GEOMETRY rather than putting a Scale
        /// on a node above the hinge: a non-uniform scale on an ancestor makes the swing shear, because the
        /// rotation then happens in scaled space and a rotated non-uniform scale is not a rigid transform.
        /// cow tools' warning about stretched planks is about the fit itself; this is about the swing.</summary>
        static ArrayMesh Scaled(Mesh src, Vector3 s)
        {
            var outm = new ArrayMesh();
            for (int i = 0; i < src.GetSurfaceCount(); i++)
            {
                var arr = src.SurfaceGetArrays(i);
                var vs = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                if (vs == null) continue;
                for (int k = 0; k < vs.Length; k++) vs[k] *= s;
                arr[(int)Mesh.ArrayType.Vertex] = vs;
                // normals do not survive a non-uniform scale unchanged -- rescale by the INVERSE and
                // renormalise, or a stretched door lights as if it were still its original shape
                var ns = arr[(int)Mesh.ArrayType.Normal].As<Vector3[]>();
                if (ns != null)
                {
                    var inv = new Vector3(s.X != 0f ? 1f / s.X : 1f, s.Y != 0f ? 1f / s.Y : 1f, s.Z != 0f ? 1f / s.Z : 1f);
                    for (int k = 0; k < ns.Length; k++) ns[k] = (ns[k] * inv).Normalized();
                    arr[(int)Mesh.ArrayType.Normal] = ns;
                }
                // SurfaceGetPrimitiveType lives on ArrayMesh, not Mesh. These are all ObjMesh output (triangles),
                // but ask when we can rather than assuming -- a silently-wrong primitive type renders nothing.
                var prim = src is ArrayMesh am ? am.SurfaceGetPrimitiveType(i) : Mesh.PrimitiveType.Triangles;
                outm.AddSurfaceFromArrays(prim, arr);
            }
            return outm;
        }

        static Material MatFor(string prop, string dir)
        {
            if (_mats.TryGetValue(prop, out var cached)) return cached;
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            string tp = dir + prop + "_tex.png";
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mm.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // tiny palette texel
            }
            else mm.AlbedoColor = new Color(0.62f, 0.62f, 0.64f);
            _mats[prop] = mm;
            return mm;
        }

        /// <summary>Test seam: forget the cached catalog so a test can write doors.txt and have it re-read.</summary>
        public static void ForgetCatalog() { _cat = null; _wood = null; _mats.Clear(); }
    }
}
