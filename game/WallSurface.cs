using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    // One wall: a rectangle plus a list of openings, materialised into boxes.
    //
    // The wall is GENERATED from its data, never cut. Rebuild() is called on every change -- including every
    // frame of a drag -- because a wall is a handful of boxes and regenerating is cheaper than reconciling.
    // That is what removes the bake step: the geometry you drag IS the final geometry, so a preview can never
    // disagree with a result. (Preview/result divergence is not hypothetical in this repo -- a barricade ghost
    // once lay on its side while the placed object stood upright, because the two had drifted apart.)
    //
    // The mesh node and the body are created ONCE and reused, so the Rid stays stable for picking and Jolt
    // sees shape swaps rather than body churn.
    public partial class WallSurface : Node3D
    {
        public float Length = 6f;                       // along local +X
        public float Height = WallOpenings.DoorHeight;  // along local +Y
        public float Thickness = WallOpenings.DefaultThickness;   // 0.70 exterior, 0.50 for partitions
        public readonly List<WallOpening> Openings = new();

        /// <summary>Which retail palette this wall wears. A "material" on these buildings is nothing but a
        /// palette -- there are no textures, only eight flat colours per model -- so the editor picks an id and
        /// the wall and its reveal take two MEASURED texels from it.
        ///
        /// The reveal IS a contrasting frame, and obviously so once the texture is sampled the way the engine
        /// samples it: Post_0 is orange trim on grey, Fire_0 white on red, Police_0 blue on tan. An earlier
        /// pass concluded the opposite because it read the palette without the V-flip that ObjMesh.cs applies
        /// to these same textures, which lands one row low -- every building came back a shade of brown.</summary>
        public int MaterialId;

        /// <summary>What this surface is for. Defaults and labels only -- Rebuild() never reads it. A floor is
        /// this same rectangle pitched flat, which is why there is no FloorSurface: the partition, the
        /// collider, the reveal lining and the bake all work already, and a stairwell is an opening.</summary>
        public SurfaceKind Kind = SurfaceKind.Wall;
        /// <summary>Paint this surface in a specific palette texel instead of the palette's wall colour.
        /// -1 (the default) means "the wall colour". One retail building is one PALETTE, not one colour.</summary>
        public int Texel = -1;
        /// <summary>The colour this surface wears, chosen from its palette by WHAT IT IS.
        ///
        /// One retail building is one palette and several colours -- cream walls, grey roof, white reveals --
        /// so a palette is not a colour, it is a set of roles. A roof painted the wall colour is the bug
        /// strawberry_cow spotted; picking by Kind is the general form of the fix rather than remembering to
        /// pass the right texel at each of the half-dozen places a surface gets spawned.
        /// Texel still overrides everything, for the importer's measured bands.</summary>
        public Color Tint
        {
            get
            {
                var m = WallMaterials.At(MaterialId);
                if (Texel >= 0 && Texel < 8) return m.Texels[Texel];
                return Kind == SurfaceKind.Roof && m.RoofTexel >= 0 && m.RoofTexel < 8
                       ? m.Texels[m.RoofTexel] : m.Wall;
            }
        }
        public Color TrimTint => WallMaterials.At(MaterialId).Reveal;
        public bool ShowTrim = true;

        /// <summary>How far this wall's top rises to a central peak, for a gable end. 0 = a flat top.
        ///
        /// ADDITIVE: the wall stays a rectangle and the partition never sees this, because a gable end really
        /// is a normal wall with a triangle sitting on it -- that is how retail builds them, and it keeps the
        /// one boundary shape the whole tool relies on. Making the boundary a pentagon instead would put a
        /// special case through Solids, the collider and every test that leans on them.</summary>
        public float GableRise;

        /// <summary>Trapezoid edges: how far this surface is set in from its left and right sides, at the
        /// BASE (…0) and at the TOP (…1), straight-line between. All zero -- the default, and every wall,
        /// floor and rectangular roof -- takes the plain box path below untouched.
        ///
        /// This exists because a cross-wing roof slope is not a rectangle. On House_00 the two 14-degree
        /// planes are trapezoids at 0.77 fill: one edge runs 5.10 m in at the eave to 0.10 m at the ridge,
        /// dead linear, which is the valley where the wing meets the main roof. Emitted as their bounding
        /// rectangles they overshot that valley by a quarter of their area each. A hip end is the same
        /// primitive with both top insets meeting.</summary>
        public float InsetL0, InsetL1, InsetR0, InsetR1;

        /// <summary>Paint the BACK face of this wall from a different palette entry. -1 (the default) means
        /// both sides are the same, and takes the original single-surface path untouched.
        ///
        /// A real building is rarely one colour through: the outside is siding and the inside is plaster, and
        /// a wall is the boundary between two rooms that need not agree. strawberry_cow: "make walls painted
        /// material-wise per side, not just overall material." Only the -Z face moves -- the edges stay with
        /// the front, because a jamb belongs to the opening it lines rather than to either room.</summary>
        public int MaterialIdBack = -1;
        public int TexelBack = -1;
        public bool TwoSided => MaterialIdBack >= 0;
        public Color BackTint
        {
            get
            {
                var m = WallMaterials.At(MaterialIdBack < 0 ? MaterialId : MaterialIdBack);
                if (TexelBack >= 0 && TexelBack < 8) return m.Texels[TexelBack];
                return Kind == SurfaceKind.Roof && m.RoofTexel >= 0 && m.RoofTexel < 8
                       ? m.Texels[m.RoofTexel] : m.Wall;
            }
        }
        public bool Tapered => InsetL0 > 0.02f || InsetL1 > 0.02f || InsetR0 > 0.02f || InsetR1 > 0.02f;

        /// <summary>Trim sits proud of BOTH faces and never scales with the opening -- widen a garage and the
        /// jambs move apart at constant thickness. Scaling the frame with the hole is what makes a parametric
        /// editor look like a stretched sprite.</summary>
        public const float TrimProfile = WallOpenings.TrimProfile;   // 0.20, retail-measured
        public const float TrimProud = 0.035f;                       // how far the bar stands off each face

        MeshInstance3D _mesh, _trimMesh, _backMesh;
        StandardMaterial3D _backMat;
        // Materials are made ONCE and recoloured. Rebuild() runs every frame of a drag, so allocating a
        // StandardMaterial3D per call hands the GC two new resources per wall per frame for a colour that
        // almost never changes.
        StandardMaterial3D _mat, _trimMat;
        StaticBody3D _body;          // wall solids: layer 0, the layer player movement collides against
        StaticBody3D _trimBody;      // trim: layer 6 (props) -- bullets and look-rays hit it, movement does not,
                                     // so a doorframe is shootable without snagging you on every doorway
        readonly List<CollisionShape3D> _shapes = new();
        readonly List<CollisionShape3D> _trimShapes = new();

        public override void _Ready()
        {
            AddToGroup("walls");   // play-mode registry: BarricadePlacer's Window mount enumerates this group to find the
                                   // window opening the player aims at -- a window HOLE has no collider, so a raycast can't hit it
            _mesh = new MeshInstance3D { Name = "Mesh" };
            AddChild(_mesh);
            _trimMesh = new MeshInstance3D { Name = "TrimMesh" };
            AddChild(_trimMesh);
            _backMesh = new MeshInstance3D { Name = "BackMesh" };
            AddChild(_backMesh);
            _body = new StaticBody3D { Name = "Solids", CollisionLayer = 1u << 0, CollisionMask = 0 };
            AddChild(_body);
            _trimBody = new StaticBody3D { Name = "Trim", CollisionLayer = 1u << 6, CollisionMask = 0 };
            AddChild(_trimBody);
            Rebuild();
        }

        public Rid BodyRid => _body != null ? _body.GetRid() : default;

        /// <summary>Regenerate mesh + collision from the current data. Safe to call every frame.</summary>
        public void Rebuild()
        {
            if (_mesh == null) return;
            var solids = WallOpenings.Solids(Length, Height, Openings);

            // Two meshes, two materials -- walls and trim are genuinely different surfaces, and a plain
            // AlbedoColor is what the rest of the repo uses. (Vertex colours needed the material to opt in and
            // silently rendered everything white, which is a bad way to find out your trim is invisible.)
            float t = Thickness * 0.5f;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetSmoothGroup(uint.MaxValue);     // = flat. See AddTrim's note; a box wants creased corners.
            SurfaceTool back = null;
            if (TwoSided)
            {
                back = new SurfaceTool();
                back.Begin(Mesh.PrimitiveType.Triangles);
                back.SetSmoothGroup(uint.MaxValue);
            }
            foreach (var s in solids)
                if (Tapered) AddTaperedSolid(st, s, t, back);
                else AddWallBox(st, s, solids, t, back);
            // GenerateNormals BEFORE Index: indexing welds vertices, and welding before normals exist lights
            // the mesh as one smooth blob instead of crisp box faces.
            if (GableRise > WallOpenings.Eps) AddGableCap(st, t);
            st.GenerateNormals();
            st.Index();
            _mesh.Mesh = st.Commit();
            _mat ??= new StandardMaterial3D { Roughness = 0.95f };
            _mat.AlbedoColor = Tint;
            _mesh.MaterialOverride = _mat;

            if (back != null)
            {
                back.GenerateNormals();
                back.Index();
                _backMesh.Mesh = back.Commit();
                _backMat ??= new StandardMaterial3D { Roughness = 0.95f };
                _backMat.AlbedoColor = BackTint;
                _backMesh.MaterialOverride = _backMat;
            }
            else _backMesh.Mesh = null;

            if (ShowTrim && Openings.Count > 0)
            {
                var tt = new SurfaceTool();
                tt.Begin(Mesh.PrimitiveType.Triangles);
                // FLAT, explicitly. SurfaceTool's default smooth group averages the normals of every face
                // meeting at a position, so an indexed pile of boxes lights as one rounded shell: the jamb of
                // a window bulges and necks like a turned spindle. On a 0.20 bar that is not subtle, and it
                // survives a shadows-off render, which is what rules out the obvious suspect.
                tt.SetSmoothGroup(uint.MaxValue);
                foreach (var o in Openings) AddTrim(tt, o);
                tt.GenerateNormals();
                tt.Index();
                _trimMesh.Mesh = tt.Commit();
                _trimMat ??= new StandardMaterial3D { Roughness = 0.9f };
                _trimMat.AlbedoColor = TrimTint;
                _trimMesh.MaterialOverride = _trimMat;
            }
            else _trimMesh.Mesh = null;

            // collision: one box per solid. Because the solids ARE the partition, the hole in the collider is
            // exactly the hole you can see -- the see-through-but-not-walk-through class of bug is impossible.
            // Reused, not respawned. QueueFree defers to the end of the frame, so freeing and re-adding every
            // shape each Rebuild leaves a drag running with two sets of colliders live at once -- and the
            // stale set is what a ray can still hit for the rest of that frame.
            int want = solids.Count + (GableRise > WallOpenings.Eps ? 1 : 0);
            while (_shapes.Count > want)
            {
                var last = _shapes[_shapes.Count - 1];
                _shapes.RemoveAt(_shapes.Count - 1);
                last.QueueFree();
            }
            while (_shapes.Count < want)
            {
                var cs = new CollisionShape3D();
                _body.AddChild(cs);
                _shapes.Add(cs);
            }
            for (int i = 0; i < solids.Count; i++)
            {
                var s = solids[i];
                if (!Tapered)
                {
                    if (_shapes[i].Shape is not BoxShape3D box) _shapes[i].Shape = box = new BoxShape3D();
                    box.Size = new Vector3(s.Width, s.Height, Thickness);
                    _shapes[i].Position = new Vector3((s.U0 + s.U1) * 0.5f, (s.V0 + s.V1) * 0.5f, 0f);
                    continue;
                }
                // A box around a trapezoid is solid where the mesh is not, which is the see-through-but-
                // not-walk-through bug this partition exists to make impossible. Same polygon as the mesh.
                var poly = ClipToTaper(s);
                if (poly.Count < 3) { _shapes[i].Shape = null; continue; }
                var pts = new Vector3[poly.Count * 2];
                for (int k = 0; k < poly.Count; k++)
                {
                    pts[k] = new Vector3(poly[k].X, poly[k].Y, -Thickness * 0.5f);
                    pts[k + poly.Count] = new Vector3(poly[k].X, poly[k].Y, Thickness * 0.5f);
                }
                SetHull(_shapes[i], pts);
                _shapes[i].Position = Vector3.Zero;     // the points are already in surface space
            }
            RebuildTrimCollision();

            if (GableRise > WallOpenings.Eps)
            {
                // A convex hull of the prism's six corners, NOT a box: a box round a gable fills the two
                // triangles of air beside the peak, and you would collide with a roof corner that is not there.
                float t2 = Thickness * 0.5f;
                var gcs = _shapes[solids.Count];
                SetHull(gcs, new[]
                {
                    new Vector3(0f, Height, -t2), new Vector3(Length, Height, -t2), new Vector3(Length * 0.5f, Height + GableRise, -t2),
                    new Vector3(0f, Height, t2), new Vector3(Length, Height, t2), new Vector3(Length * 0.5f, Height + GableRise, t2),
                });
                gcs.Position = Vector3.Zero;
            }
            RebuildGlass();
            RebuildDoors();
        }

        // ---- glazing -----------------------------------------------------------------------------------
        // A pane per glazed, unbroken opening. The wall owns WHETHER and WHERE; GlassPane owns what a pane
        // does when it is shot. Deliberately no reaching into the pane past its Build arguments -- the
        // shatter, its effect and its sound are cow tools' side of the line.

        readonly List<GlassPane> _panes = new();
        /// <summary>What each live pane was built from, so a Rebuild during a drag can tell "this pane is
        /// still right, just move it" from "this one has to be remade". Rebuild runs on every mouse move.</summary>
        readonly List<(int Op, float W, float H, int Tint, float Hp, bool Ind)> _paneSpec = new();

        // Remnants for openings whose glass is GONE. Pooled and spec-keyed exactly like the panes above,
        // because the failure they are avoiding is the same one: Rebuild runs on every edit, and rebuilding
        // four mesh instances per broken window per keystroke is the "million rebuilds each frame"
        // strawberry_cow warned about.
        readonly List<Node3D> _shards = new();
        readonly List<(int Op, float W, float H, int Tint)> _shardSpec = new();

        /// <summary>0xRRGGBB -> Color. 0 means UNSET rather than black, so a tint of pure black is not
        /// expressible; that is a deliberate trade for keeping the save token a single int, and black glass
        /// is not a thing anyone has asked for. Everything else in the 24-bit space round-trips.</summary>
        public static Color TintFromRgb(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
        // (no RgbFromTint: nothing packs a Color back to an int -- the swatches ARE ints. An unused inverse
        // is the same dead-API smell as a flag nothing reads; add it when something needs it.)

        // ---- a DOOR in an opening ----------------------------------------------------------------------
        // strawberry_cow 2026-08-09: "what i want is to have these doors as things i can enable on relevant
        // openings". Same ownership rule as the glass above: the OPENING carries which door it has, and this
        // materialises it. Built through DoorDeploy.SpawnProp -- the identical call a standalone placement
        // makes -- so the hinge, the stand-up, the easing curves and the sound cannot diverge between "a door
        // you planted" and "a door in a wall you drew".
        readonly List<Node3D> _doors = new();
        /// <summary>What each live door was built from, so a Rebuild mid-drag can tell "still right, just move
        /// it" from "remake it". Rebuild runs on every mouse move; respawning a door per frame would leave a
        /// trail of them, and QueueFree defers so the stale ones stay hittable for the rest of the frame.</summary>
        readonly List<(int Op, string Prop, float W, float H)> _doorSpec = new();

        void RebuildDoors()
        {
            for (int i = _doors.Count - 1; i >= 0; i--)
                if (!IsInstanceValid(_doors[i])) { _doors.RemoveAt(i); _doorSpec.RemoveAt(i); }

            var want = new List<int>();
            for (int i = 0; i < Openings.Count; i++)
                if (Openings[i].HasDoor) want.Add(i);

            while (_doors.Count > want.Count)
            {
                var last = _doors[_doors.Count - 1];
                _doors.RemoveAt(_doors.Count - 1);
                _doorSpec.RemoveAt(_doorSpec.Count - 1);
                if (IsInstanceValid(last)) { RemoveChild(last); last.QueueFree(); }
            }

            for (int k = 0; k < want.Count; k++)
            {
                var o = Openings[want[k]];
                var spec = (want[k], o.DoorProp, o.Width, o.Height);
                if (k < _doors.Count && _doorSpec[k] == spec) { PlaceDoor(_doors[k], o); continue; }

                var made = DoorDeploy.SpawnProp(o.DoorProp, this, Vector3.Zero, 0f, ClearOpeningSize(o));
                if (made == null)
                {
                    // SAY SO rather than leaving an empty hole that looks like a design choice.
                    GD.PrintErr($"[door] opening {want[k]}: '{o.DoorProp}' has no catalog entry -- no door built");
                    continue;
                }
                if (k < _doors.Count)
                {
                    var old2 = _doors[k];
                    if (IsInstanceValid(old2)) { RemoveChild(old2); old2.QueueFree(); }
                    _doors[k] = made; _doorSpec[k] = spec;
                }
                else { _doors.Add(made); _doorSpec.Add(spec); }
                PlaceDoor(made, o);
            }
        }

        /// <summary>The opening index a placed door sits in, given its HOST node (= a focused ObjectDoor's
        /// GetParent()). Reads the _doors/_doorSpec mapping RebuildDoors keeps exact; -1 if the host isn't one
        /// of ours. Lets barricade code ask "is the door in opening N barricaded?" (master 2026-09-01).</summary>
        public int OpeningIndexForDoorHost(Node3D host)
        {
            int k = _doors.IndexOf(host);
            return k >= 0 ? _doorSpec[k].Op : -1;
        }

        /// <summary>Sit the door in its hole, in WALL-LOCAL space. DoorDeploy places into world space for a
        /// standalone drop, so the host's own transform is overwritten here rather than passing a world point
        /// in -- the wall may be rotated, pitched, or lying down as a floor, and the hole's u/v is the only
        /// frame that is true in all of those.</summary>
        void PlaceDoor(Node3D host, WallOpening o)
        {
            if (host == null || !IsInstanceValid(host)) return;
            host.Transform = new Transform3D(Basis.Identity, new Vector3(o.U + o.Width * 0.5f, o.V, 0f));

            // Then align by MEASURING the leaf, on BOTH axes. These meshes are not centred on their own
            // origin: Door_Pine spans x -2.35..+0.10 (centre -1.12, it is anchored at the hinge) and
            // z -1.40..+1.40 (centred). So putting the host at the hole's centre hangs the door a metre to the
            // left of it -- strawberry_cow, off a render: "door looks like its half in the left wall."
            //
            // I fixed the VERTICAL offset this way an hour ago and did not think to ask the same question
            // about the horizontal, because the vertical one was visibly wrong and this one was not. Measure
            // both; the mesh's own anchor is not a thing to assume per axis.
            var lo = new Vector3(float.MaxValue, float.MaxValue, 0f);
            var hi = new Vector3(float.MinValue, float.MinValue, 0f);
            foreach (var d in host.GetChildren())
                if (d is Node3D dn)
                    foreach (var pv in dn.GetChildren())
                        if (pv is Node3D piv)
                            foreach (var c in piv.GetChildren())
                                if (c is MeshInstance3D mi && mi.Mesh != null)
                                {
                                    var ab = mi.Mesh.GetAabb();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        var q = ToLocal(mi.GlobalTransform * ab.GetEndpoint(i));
                                        lo.X = Mathf.Min(lo.X, q.X); hi.X = Mathf.Max(hi.X, q.X);
                                        lo.Y = Mathf.Min(lo.Y, q.Y); hi.Y = Mathf.Max(hi.Y, q.Y);
                                    }
                                }
            // Land it on the TRIM, not on the raw hole. The leaf is now fitted to ClearOpeningSize, so its
            // foot belongs on top of the sill lining where one exists; a floor-pinned doorway has no sill
            // lining and its foot stays on the floor. Getting the size right and the datum wrong just moves
            // the same 0.20 gap from the bottom of the door to the top of it.
            float footV = o.V + (o.V > WallOpenings.Eps ? TrimProfile : 0f);
            if (lo.X < float.MaxValue)
                host.Position += new Vector3((o.U + o.Width * 0.5f) - (lo.X + hi.X) * 0.5f,   // centre in the hole
                                             footV - lo.Y,                                    // and sit on its sill/floor
                                             0f);
        }

        void RebuildGlass()
        {
            // A shattered pane frees ITSELF, so the list can hold dead instances before we have looked at it.
            for (int i = _panes.Count - 1; i >= 0; i--)
                if (!IsInstanceValid(_panes[i])) { _panes.RemoveAt(i); _paneSpec.RemoveAt(i); }

            var want = new List<int>();
            for (int i = 0; i < Openings.Count; i++)
                if (Openings[i].HasGlass) want.Add(i);

            while (_panes.Count > want.Count)
            {
                var last = _panes[_panes.Count - 1];
                _panes.RemoveAt(_panes.Count - 1);
                _paneSpec.RemoveAt(_paneSpec.Count - 1);
                if (IsInstanceValid(last)) { RemoveChild(last); last.QueueFree(); }   // RemoveChild FIRST: QueueFree defers to end of frame, and until then a ray still hits it
            }

            for (int k = 0; k < want.Count; k++)
            {
                var o = Openings[want[k]];
                float hp = o.GlassHp > 0f ? o.GlassHp : 1f;
                var spec = (want[k], o.Width, o.Height, o.GlassTint, hp, o.GlassIndestructible);
                if (k < _panes.Count && _paneSpec[k] == spec)
                {
                    _panes[k].Position = new Vector3(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f, 0f);
                    continue;
                }
                var pane = GlassPane.Build(new Vector2(o.Width, o.Height),
                                           o.GlassTint != 0 ? TintFromRgb(o.GlassTint) : GlassPane.DefaultHue,
                                           hp, o.GlassIndestructible);
                // Resolve the opening at SHATTER time, not now: this closure outlives any number of Rebuilds,
                // and an index captured today points at a different opening once one is deleted.
                pane.OnShattered += () => MarkPaneBroken(pane);
                if (k < _panes.Count)
                {
                    var old = _panes[k];
                    if (IsInstanceValid(old)) { RemoveChild(old); old.QueueFree(); }
                    _panes[k] = pane; _paneSpec[k] = spec;
                }
                else { _panes.Add(pane); _paneSpec.Add(spec); }
                AddChild(pane);
                pane.Position = new Vector3(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f, 0f);
            }

            RebuildShards();
        }

        /// <summary>Jagged remnants for every opening that was glazed and is now broken.
        ///
        /// Driven off the SAME GlassBroken flag the shatter path writes, so authoring a broken window in the
        /// editor and shooting one out in play converge on one appearance -- and neither needs a byte of new
        /// save data, because the flag already persists.</summary>
        void RebuildShards()
        {
            for (int i = _shards.Count - 1; i >= 0; i--)
                if (!IsInstanceValid(_shards[i])) { _shards.RemoveAt(i); _shardSpec.RemoveAt(i); }

            var want = new List<int>();
            for (int i = 0; i < Openings.Count; i++)
                if (Openings[i].Glazed && Openings[i].GlassBroken) want.Add(i);

            while (_shards.Count > want.Count)
            {
                var last = _shards[_shards.Count - 1];
                _shards.RemoveAt(_shards.Count - 1);
                _shardSpec.RemoveAt(_shardSpec.Count - 1);
                if (IsInstanceValid(last)) { RemoveChild(last); last.QueueFree(); }
            }

            for (int k = 0; k < want.Count; k++)
            {
                var o = Openings[want[k]];
                var spec = (want[k], o.Width, o.Height, o.GlassTint);
                if (k < _shards.Count && _shardSpec[k] == spec)
                {
                    _shards[k].Position = new Vector3(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f, 0f);
                    continue;
                }
                var node = GlassShards.Build(new Vector2(o.Width, o.Height),
                                             o.GlassTint != 0 ? TintFromRgb(o.GlassTint) : GlassPane.DefaultHue,
                                             ShardSeed(o));
                if (node == null) continue;      // content missing: lose the shards, not the building

                if (k < _shards.Count)
                {
                    var old = _shards[k];
                    if (IsInstanceValid(old)) { RemoveChild(old); old.QueueFree(); }
                    _shards[k] = node; _shardSpec[k] = spec;
                }
                else { _shards.Add(node); _shardSpec.Add(spec); }
                AddChild(node);
                // NAMED PER OPENING, and named AFTER AddChild. Two broken windows on one wall both wanted
                // the name "GlassShards", and Godot silently discards a colliding name and substitutes a
                // generated one -- the second node came out as "@Node3D@46". Nothing broke, but anything
                // looking the shards up by name found one of the two, which is how the test for this first
                // reported four shards where there were eight.
                node.Name = $"GlassShards_{want[k]}";
                node.Position = new Vector3(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f, 0f);
            }
        }

        /// <summary>A per-opening seed derived from its own geometry, so which shard shape lands in which
        /// corner is stable across rebuild, save and load without storing anything.
        ///
        /// Quantised before hashing: U and V survive a text round trip as decimals, and seeding off raw
        /// floats would reshuffle every window the first time a save was reloaded -- a building that looks
        /// different after reopening it, for no reason the user did.</summary>
        static int ShardSeed(WallOpening o)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(o.U * 100f);
                h = h * 31 + Mathf.RoundToInt(o.V * 100f);
                h = h * 31 + Mathf.RoundToInt(o.Width * 100f);
                h = h * 31 + Mathf.RoundToInt(o.Height * 100f);
                return h;
            }
        }

        /// <summary>A pane shattered: record it on its opening so the hole stays a hole through save, load and
        /// rebuild. Looked up by pane REFERENCE rather than a captured index -- see the subscription above.</summary>
        void MarkPaneBroken(GlassPane pane)
        {
            int k = _panes.IndexOf(pane);
            if (k < 0 || k >= _paneSpec.Count) return;
            int op = _paneSpec[k].Op;
            if (op < 0 || op >= Openings.Count) return;
            var o = Openings[op];
            o.GlassBroken = true;
            Openings[op] = o;                     // struct: write it back or nothing happened

            // Flag only -- deliberately NO Rebuild() and no notification from in here.
            //
            // GlassPane.Shatter raises OnShattered and only AFTERWARDS reads GetTree() and GlobalPosition to
            // spawn its shards. Anything on this path that takes the pane out of the tree makes GetTree()
            // return null, and the shatter effect silently does not happen -- a broken window with no
            // breaking. Nothing needs the callback anyway: Save() reads the openings, so the flag alone
            // survives a save, and the pane has already removed itself from view.
            //
            // If a listener is ever genuinely needed here, defer it (Callable.From(...).CallDeferred()) so
            // the shatter finishes the frame it is in.
        }

        /// <summary>The triangular prism that turns a flat-topped wall into a gable end: apex over the middle
        /// of the run, base along the wall's head. Emitted as its own faces rather than by reshaping the wall,
        /// so nothing downstream has to know a wall can be non-rectangular.</summary>
        void AddGableCap(SurfaceTool st, float t)
        {
            float x0 = 0f, x1 = Length, mid = Length * 0.5f;
            float y0 = Height, y1 = Height + GableRise;
            Vector3 A = new(x0, y0, -t), B = new(x1, y0, -t), P = new(mid, y1, -t);   // back face
            Vector3 C = new(x0, y0, t), D = new(x1, y0, t), Q = new(mid, y1, t);      // front face

            // Godot treats CLOCKWISE as front-facing, so each face is emitted in the order that reads
            // anticlockwise from outside -- the same reversal AddBoxFaces does, for the same reason.
            Tri(st, P, B, A);        // -Z gable triangle
            Tri(st, C, D, Q);        // +Z gable triangle
            Quad(st, A, C, Q, P);    // left slope
            Quad(st, P, Q, D, B);    // right slope
            // no bottom face: it sits flush on the wall head and would z-fight the wall's own top
        }

        static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
        { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); }

        static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { Tri(st, a, b, c); Tri(st, a, c, d); }

        /// <summary>Colliders for the frames, on layer 6.
        ///
        /// _trimBody existed and was documented as "a doorframe is shootable without snagging you on every
        /// doorway" -- and nothing ever added a shape to it, so it was a dead node and editor-drawn frames
        /// were not shootable at all. Worse, a BAKED building's frames are solid (the prop path trimeshes the
        /// whole render mesh), so the same building behaved differently before and after baking.
        ///
        /// Layer 6 is the props layer: bullets and look-rays hit it, player movement does not.</summary>
        void RebuildTrimCollision()
        {
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            if (ShowTrim)
                foreach (var o in Openings)
                {
                    float t = Thickness * 0.5f + TrimProud, w = TrimProfile;
                    float u0 = o.U, u1 = o.U1, v0 = o.V, v1 = o.V1;
                    bool sill = o.V > WallOpenings.Eps;
                    float vb = sill ? v0 : v0;
                    boxes.Add((new Vector3(u0, vb, -t), new Vector3(u0 + w, v1, t)));
                    boxes.Add((new Vector3(u1 - w, vb, -t), new Vector3(u1, v1, t)));
                    boxes.Add((new Vector3(u0, v1 - w, -t), new Vector3(u1, v1, t)));
                    if (sill) boxes.Add((new Vector3(u0, v0, -t), new Vector3(u1, v0 + w, t)));
                }

            while (_trimShapes.Count > boxes.Count)
            {
                var last = _trimShapes[_trimShapes.Count - 1];
                _trimShapes.RemoveAt(_trimShapes.Count - 1);
                last.QueueFree();
            }
            while (_trimShapes.Count < boxes.Count)
            {
                var cs = new CollisionShape3D();
                _trimBody.AddChild(cs);
                _trimShapes.Add(cs);
            }
            for (int i = 0; i < boxes.Count; i++)
            {
                var (mn, mx) = boxes[i];
                if (_trimShapes[i].Shape is not BoxShape3D b) _trimShapes[i].Shape = b = new BoxShape3D();
                b.Size = mx - mn;
                _trimShapes[i].Position = (mn + mx) * 0.5f;
            }
        }

        void AddTrim(SurfaceTool st, WallOpening o)
        {
            // The frame LINES THE REVEAL -- it sits inside the hole spanning the wall thickness, not as a bar
            // on the face. That is what retail does: the dominant loose panel in every building measured is a
            // strip the length of an opening edge by the wall thickness (0.70), i.e. a reveal lining.
            //
            // A bar on the face leaves the wall's own cut faces exposed inside the frame -- a pale band on all
            // four sides of every opening, which is exactly what it looked like.
            // Every lining is grown by BURY past the hole edge so it INTERPENETRATES the wall, and the four
            // linings run edge to edge so they interpenetrate each other at the corners. Sized to meet exactly
            // instead, each lining's outer face lands on the wall's jamb face at the same depth -- coplanar
            // duplicates, which z-fight into a bowtie down the middle of the jamb that reads as broken frame
            // geometry. Overlap is free here: the surfaces that intersect are buried, and the two meshes are
            // one flat colour each, so there is nothing for the seam to show.
            float t = Thickness * 0.5f + TrimProud, w = TrimProfile;
            const float BURY = 0.01f;
            float u0 = o.U, u1 = o.U1, v0 = o.V, v1 = o.V1;
            bool sill = o.V > WallOpenings.Eps;                    // floor-pinned openings have none
            float vb = sill ? v0 - BURY : v0;
            AddBox(st, new Vector3(u0 - BURY, vb, -t), new Vector3(u0 + w, v1 + BURY, t));      // left lining
            AddBox(st, new Vector3(u1 - w, vb, -t), new Vector3(u1 + BURY, v1 + BURY, t));      // right lining
            AddBox(st, new Vector3(u0 - BURY, v1 - w, -t), new Vector3(u1 + BURY, v1 + BURY, t)); // head
            if (sill)
                AddBox(st, new Vector3(u0 - BURY, v0 - BURY, -t), new Vector3(u1 + BURY, v0 + w, t));
        }

        /// <summary>The CLEAR span inside an opening's trim -- what a door or a pane actually has to fit.
        ///
        /// AddTrim lays a lining of TrimProfile down each jamb and across the head, plus a sill only when the
        /// opening is NOT floor-pinned (`o.V > Eps`). So a doorway loses 2x0.20 across and 0.20 up, and a
        /// window loses 0.20 on all four sides. Fitting a door to o.Width x o.Height sizes it to the HOLE and
        /// it then fouls its own frame -- strawberry_cow: "scaled perfectly for the raw opening, but not the
        /// opening trim".
        ///
        /// Kept immediately next to AddTrim because they are one rule in two places, and this file's recurring
        /// defect is exactly that shape: a rule duplicated across call sites that has quietly drifted.</summary>
        public static Vector2 ClearOpeningSize(WallOpening o)
        {
            bool sill = o.V > WallOpenings.Eps;
            return new Vector2(Mathf.Max(0.01f, o.Width - TrimProfile * 2f),
                               Mathf.Max(0.01f, o.Height - TrimProfile * (sill ? 2f : 1f)));
        }

        /// <summary>The left and right cut lines, as u for a given v.</summary>
        float CutL(float v) => Mathf.Lerp(InsetL0, InsetL1, Height > WallOpenings.Eps ? v / Height : 0f);
        float CutR(float v) => Length - Mathf.Lerp(InsetR0, InsetR1, Height > WallOpenings.Eps ? v / Height : 0f);

        /// <summary>One solid of the partition, clipped to the trapezoid and extruded.
        ///
        /// Kept entirely separate from AddWallBox rather than generalising it: the box path runs for every
        /// wall in the game and knows which faces to omit where solids abut, and none of that needed to
        /// change to put a slanted edge on a roof.</summary>
        void AddTaperedSolid(SurfaceTool st, WallSolid s, float t, SurfaceTool backTool = null)
        {
            var poly = ClipToTaper(s);
            if (poly.Count < 3) return;
            var bt = backTool ?? st;
            for (int i = 1; i + 1 < poly.Count; i++)          // +Z face
            {
                Tri(st, new Vector3(poly[0].X, poly[0].Y, t), new Vector3(poly[i].X, poly[i].Y, t),
                        new Vector3(poly[i + 1].X, poly[i + 1].Y, t));
                Tri(bt, new Vector3(poly[0].X, poly[0].Y, -t), new Vector3(poly[i + 1].X, poly[i + 1].Y, -t),
                        new Vector3(poly[i].X, poly[i].Y, -t));
            }
            for (int i = 0; i < poly.Count; i++)              // the rim
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                Quad(st, new Vector3(a.X, a.Y, -t), new Vector3(b.X, b.Y, -t),
                         new Vector3(b.X, b.Y, t), new Vector3(a.X, a.Y, t));
            }
        }

        /// <summary>A solid rectangle clipped by the two cut lines. Sutherland-Hodgman against two
        /// half-planes; the result is convex, so a fan triangulates it and a convex hull collides it.</summary>
        /// <summary>Give a CollisionShape3D a convex hull of `pts`, POPULATING IT BEFORE IT IS ATTACHED.
        ///
        /// The order matters and is not cosmetic. Assigning a fresh empty ConvexPolygonShape3D to .Shape and
        /// filling in .Points afterwards makes the engine hull an EMPTY point set at the moment of assignment,
        /// which prints `ERROR: Failed to build convex hull` and a full C# backtrace. The collision that
        /// eventually lands is correct, so every test still passes -- the only cost is log noise, and log
        /// noise is not free: this repo has already had a real bug (leaked statics wiping a save) hide inside
        /// a green run because nobody reads a log that always has errors in it. 15 of these per full sweep.
        ///
        /// Both hull sites route through here rather than repeating the dance, because the version of this
        /// that only fixed the gable would have left the tapered path printing the same error on every import.</summary>
        static void SetHull(CollisionShape3D cs, Vector3[] pts)
        {
            if (cs.Shape is ConvexPolygonShape3D existing) { existing.Points = pts; return; }
            cs.Shape = new ConvexPolygonShape3D { Points = pts };
        }

        List<Vector2> ClipToTaper(WallSolid s)
        {
            var poly = new List<Vector2>
            {
                new(s.U0, s.V0), new(s.U1, s.V0), new(s.U1, s.V1), new(s.U0, s.V1),
            };
            // keep u >= CutL(v), then u <= CutR(v)
            poly = ClipHalfPlane(poly, p => p.X - CutL(p.Y));
            poly = ClipHalfPlane(poly, p => CutR(p.Y) - p.X);
            return poly;
        }

        static List<Vector2> ClipHalfPlane(List<Vector2> poly, System.Func<Vector2, float> keep)
        {
            var outp = new List<Vector2>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                float da = keep(a), db = keep(b);
                if (da >= 0f) outp.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float f = da / (da - db);
                    if (float.IsFinite(f)) outp.Add(a.Lerp(b, Mathf.Clamp(f, 0f, 1f)));
                }
            }
            return outp;
        }

        static void AddWallBox(SurfaceTool st, WallSolid s, List<WallSolid> all, float t,
                               SurfaceTool backTool = null)
        {
            bool left = !Abuts(all, s, -1, 0), right = !Abuts(all, s, 1, 0);
            bool down = !Abuts(all, s, 0, -1), up = !Abuts(all, s, 0, 1);
            AddBoxFaces(st, new Vector3(s.U0, s.V0, -t), new Vector3(s.U1, s.V1, t),
                        front: true, back: true, minU: left, maxU: right, minV: down, maxV: up, backTool);
        }

        /// <summary>Is another solid flush against this side, covering it completely?</summary>
        static bool Abuts(List<WallSolid> all, WallSolid s, int du, int dv)
        {
            const float E = 1e-3f;
            foreach (var o in all)
            {
                if (du != 0)
                {
                    float mine = du < 0 ? s.U0 : s.U1, theirs = du < 0 ? o.U1 : o.U0;
                    if (Mathf.Abs(mine - theirs) > E) continue;
                    if (o.V0 <= s.V0 + E && o.V1 >= s.V1 - E) return true;
                }
                else
                {
                    float mine = dv < 0 ? s.V0 : s.V1, theirs = dv < 0 ? o.V1 : o.V0;
                    if (Mathf.Abs(mine - theirs) > E) continue;
                    if (o.U0 <= s.U0 + E && o.U1 >= s.U1 - E) return true;
                }
            }
            return false;
        }

        static void AddBox(SurfaceTool st, Vector3 a, Vector3 b)
            => AddBoxFaces(st, a, b, true, true, true, true, true, true);

        static void AddBoxFaces(SurfaceTool st, Vector3 a, Vector3 b,
                                bool front, bool back, bool minU, bool maxU, bool minV, bool maxV,
                                SurfaceTool backTool = null)
        {
            Vector3[] v =
            {
                new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z),
                new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
            };
            var tris = new List<int[]>();
            var backTris = new List<int[]>();
            // the -Z face goes to its own surface when the wall is painted per side
            if (back) (backTool != null ? backTris : tris).AddRange(new[] { new[]{0,3,2}, new[]{0,2,1} });
            if (front) { tris.Add(new[]{4,5,6}); tris.Add(new[]{4,6,7}); }   // +Z
            if (minU)  { tris.Add(new[]{0,4,7}); tris.Add(new[]{0,7,3}); }   // -X
            if (maxU)  { tris.Add(new[]{1,2,6}); tris.Add(new[]{1,6,5}); }   // +X
            if (minV)  { tris.Add(new[]{0,1,5}); tris.Add(new[]{0,5,4}); }   // -Y
            if (maxV)  { tris.Add(new[]{3,7,6}); tris.Add(new[]{3,6,2}); }   // +Y
            // Godot treats CLOCKWISE as front-facing. The index table below is wound counter-clockwise-outward
            // (right-hand rule, outward normals), so emit each triangle REVERSED -- otherwise every face is
            // culled when seen from outside and lit from within, which reads as the whole thing being inside out.
            foreach (var tri in tris)
                for (int k = 2; k >= 0; k--)
                    st.AddVertex(v[tri[k]]);
            foreach (var tri in backTris)
                for (int k = 2; k >= 0; k--)
                    backTool.AddVertex(v[tri[k]]);
        }

        // ---- wall space <-> world -------------------------------------------------------------------
        // ONE projection pair, used by every caller. A second copy that disagrees on the sign of U is the
        // mirror bug that makes openings jump when the camera crosses the wall.

        public Vector3 UVToWorld(float u, float v) => ToGlobal(new Vector3(u, v, 0f));

        public bool WorldToUV(Vector3 world, out float u, out float v)
        {
            var l = ToLocal(world);
            u = l.X; v = l.Y;
            return u >= -WallOpenings.Eps && u <= Length + WallOpenings.Eps
                && v >= -WallOpenings.Eps && v <= Height + WallOpenings.Eps;
        }

        /// <summary>Where a camera ray meets this wall's plane, in wall space. Takes an explicit ray so it is
        /// testable without a camera or a mouse.</summary>
        /// <summary>Like RayToUV, but only true when the hit lands ON the surface rather than merely on
        /// the infinite plane it lies in. RayToUV deliberately stays loose -- dragging an opening past the
        /// edge has to keep tracking so it can clamp -- but PICKING must not, or the nearest PLANE wins over
        /// the wall you are actually pointing at, which is the whole "preview jumps to the wrong wall" bug.</summary>
        public bool RayToUVInside(Vector3 from, Vector3 dir, out float u, out float v)
        {
            if (!RayToUV(from, dir, out u, out v)) return false;
            const float E = WallOpenings.Eps;
            return u >= -E && u <= Length + E && v >= -E && v <= Height + E;
        }

        public bool RayToUV(Vector3 from, Vector3 dir, out float u, out float v)
        {
            u = v = 0f;
            Vector3 n = GlobalTransform.Basis.Z.Normalized();
            float denom = n.Dot(dir);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float dist = n.Dot(GlobalPosition - from) / denom;
            if (dist < 0f) return false;
            var hit = from + dir * dist;
            WorldToUV(hit, out u, out v);
            return true;
        }

        public int OpeningAt(float u, float v)
        {
            for (int i = 0; i < Openings.Count; i++)
            {
                var o = Openings[i];
                if (u >= o.U && u <= o.U1 && v >= o.V && v <= o.V1) return i;
            }
            return -1;
        }
    }
}
