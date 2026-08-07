using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // In-engine cover for the building tool. The partition maths is L0'd in WallOpeningsTests; what needs a
    // running engine is everything downstream of it -- the collider, the committed mesh, and the undo stack --
    // and all three have already been wrong in ways that render perfectly.
    public class BuildToolColliderMatchesTheHole : GameTest
    {
        public override string Name => "buildtool.collider_matches_the_hole";

        public override IEnumerable<Step> Run()
        {
            // The claim the whole generate-don't-cut design rests on: the hole you can see IS the hole you can
            // walk through, because the same partition produces both. A CSG hole needs its collider rebuilt
            // separately and can silently disagree -- see-through-but-not-walk-through.
            var w = new WallSurface { Length = 12f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            w.Openings.Add(new WallOpening(4f, 0f, 2.5f, WallOpenings.DoorHeight - 0.5f));   // a doorway
            w.Rebuild();
            yield return Step.Ticks(2);          // let Jolt take the new shapes

            var space = World.GetWorld3D().DirectSpaceState;
            bool Blocked(float u, float v)
            {
                var a = w.UVToWorld(u, v) + new Vector3(0f, 0f, 3f);
                var b = w.UVToWorld(u, v) - new Vector3(0f, 0f, 3f);
                var q = new PhysicsRayQueryParameters3D { From = a, To = b, CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }

            T.Check("solid wall blocks", Blocked(1f, 2f));
            T.Check("mid-doorway is clear", !Blocked(5.25f, 1.5f));
            T.Check("the jamb beside the doorway blocks", Blocked(3.5f, 1.5f));
            T.Check("the lintel above the doorway blocks", Blocked(5.25f, 4.1f));

            // and the mesh agrees with the collider -- checked against the partition, not against itself
            var solids = WallOpenings.Solids(w.Length, w.Height, w.Openings);
            float area = 0f;
            foreach (var s in solids) area += s.Area;
            float expect = 12f * WallOpenings.DoorHeight - 2.5f * (WallOpenings.DoorHeight - 0.5f);
            T.Check($"partition area matches the wall minus its hole ({area:0.##} vs {expect:0.##})",
                    Mathf.Abs(area - expect) < 1e-2f);
        }
    }

    public class BuildToolTrimIsFlatShaded : GameTest
    {
        public override string Name => "buildtool.trim_is_flat_shaded";

        public override IEnumerable<Step> Run()
        {
            // SurfaceTool's default smooth group averages the normals of every face meeting at a position, so
            // an indexed pile of boxes lights as one rounded shell and a 0.20 jamb necks like a turned
            // spindle. Nothing throws and the frame still renders, so only the vertex count catches it:
            // flat-shaded boxes cannot share a vertex between two faces, which leaves ~2 verts per triangle,
            // while smoothing collapses each corner onto one and drops it below 1.
            var w = new WallSurface { Length = 12f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            w.Openings.Add(new WallOpening(2f, 0f, 2.5f, WallOpenings.DoorHeight - 0.5f));
            w.Openings.Add(new WallOpening(6f, WallOpenings.WindowSill, 3.31f, WallOpenings.WindowHeight));
            w.Rebuild();
            yield return Step.Ticks(1);

            foreach (var (label, path) in new[] { ("wall", "Mesh"), ("trim", "TrimMesh") })
            {
                var mesh = w.GetNode<MeshInstance3D>(path).Mesh;
                if (mesh == null || mesh.GetSurfaceCount() == 0) { T.Fail($"{label} mesh is empty"); continue; }
                var arr = mesh.SurfaceGetArrays(0);
                int nv = ((Vector3[])arr[(int)Mesh.ArrayType.Vertex]).Length;
                int nt = ((int[])arr[(int)Mesh.ArrayType.Index]).Length / 3;
                float ratio = nt > 0 ? nv / (float)nt : 0f;
                T.Check($"{label} is flat-shaded: {ratio:0.00} verts/tri over {nt} tris (smoothed lands near 0.5)",
                        ratio > 1.5f);
            }
        }
    }

    public class BuildToolMaterialIsARetailPalette : GameTest
    {
        public override string Name => "buildtool.material_is_a_retail_palette";

        public override IEnumerable<Step> Run()
        {
            T.Check($"palettes loaded off content/wall_palettes.tsv ({WallMaterials.Count})", WallMaterials.Count >= 50);

            // Pinned against a building anyone can look at, for the same reason as the L0 palette tests: a
            // silent shift in which texel means "wall" parses, loads and renders, and only stops looking like
            // Unturned. A fire station is red with white trim.
            int fire = -1;
            for (int i = 0; i < WallMaterials.Count; i++) if (WallMaterials.At(i).Name == "Fire_0") fire = i;
            T.Check("Fire_0 is in the table", fire >= 0);
            if (fire < 0) yield break;

            var w = new WallSurface { Length = 6f, MaterialId = fire };
            World.AddChild(w);
            w.Openings.Add(new WallOpening(2f, WallOpenings.WindowSill, 2.81f, WallOpenings.WindowHeight));
            w.Rebuild();
            yield return Step.Ticks(1);

            var wallMat = (StandardMaterial3D)w.GetNode<MeshInstance3D>("Mesh").MaterialOverride;
            var trimMat = (StandardMaterial3D)w.GetNode<MeshInstance3D>("TrimMesh").MaterialOverride;
            var c = wallMat.AlbedoColor;
            var t = trimMat.AlbedoColor;
            T.Check($"the wall wears the palette's red (got {c.R8},{c.G8},{c.B8})", c.R8 == 160 && c.G8 == 42 && c.B8 == 42);
            T.Check($"the reveal wears its white trim (got {t.R8},{t.G8},{t.B8})", t.R8 == 219 && t.G8 == 219 && t.B8 == 219);
            T.Check("wall and reveal are not the same colour", c != t);
        }
    }

    public class BuildToolWallDeleteIsUndoable : GameTest
    {
        public override string Name => "buildtool.wall_delete_is_undoable";

        public override IEnumerable<Step> Run()
        {
            // Deleting a wall used to push an EMPTY undo action, which is worse than pushing none: the step is
            // consumed, so Ctrl+Z fires, reports success, and does nothing -- and the wall is gone for good.
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);

            // Counted as a DELTA off whatever Setup loaded. An absolute count here would pass or fail on
            // whether some earlier test left a saved layout on disk, which is a property of the run order and
            // not of the code under test.
            int start = tool.Walls.Count;
            var w = tool.AddWall(Vector3.Zero, 0f, 12f);
            tool.AddOpening(w, 3f, 0f, 0);                 // a door
            tool.AddOpening(w, 8f, 2f, 1);                 // a window
            int openings = w.Openings.Count;
            float len = w.Length;
            T.Check($"wall placed with {openings} openings", tool.Walls.Count == start + 1 && openings == 2);
            yield return Step.Ticks(1);

            tool.DeleteWall(w);
            T.Check("wall is gone", tool.Walls.Count == start);

            T.Check("undo reports it did something", ed.Undo());
            yield return Step.Ticks(1);
            T.Check("the wall came back", tool.Walls.Count == start + 1);
            if (tool.Walls.Count != start + 1) yield break;
            var back = tool.Walls[tool.Walls.Count - 1];
            T.Check($"with its openings ({back.Openings.Count} of {openings})", back.Openings.Count == openings);
            T.Check($"and its length ({back.Length:0.##} of {len:0.##})", Mathf.Abs(back.Length - len) < 1e-3f);
            T.Check("and it is pickable again -- a restored wall nothing can select is still lost",
                    back.BodyRid.IsValid);
        }
    }

    public class BuildToolWallsSurviveSaveAndLoad : GameTest
    {
        public override string Name => "buildtool.walls_survive_save_and_load";

        // res://content/buildings/editor_<map>_Walls.dat, and MapName is null here because calling
        // Editor.Setup would take over the Editor.Instance static for the rest of the shared boot.
        static string Path => ProjectSettings.GlobalizePath("res://content/buildings/") + "editor_none_Walls.dat";

        public override IEnumerable<Step> Run()
        {
            // Drawn walls used to live only in the session: lay out a building, hit Save, exit, find nothing.
            // The round trip goes through the real file, not just the formatter -- WallSaveTests already
            // covers the text, and what is worth checking in-engine is that the walls come back as WALLS:
            // pickable, with their holes, at their size.
            if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);

            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            T.Check("starts empty", tool.Walls.Count == 0);

            var a = tool.AddWall(new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddOpening(a, 3f, 0f, 0);
            tool.AddOpening(a, 8f, 2f, 1);
            tool.SelectMaterial(20);                       // Fire_0
            tool.SetMaterial(a, 20);
            var b = tool.AddWall(new Vector3(-6f, 0f, -9f), -90f, 9f);
            b.Thickness = WallOpenings.InteriorThickness;
            // OFF the lattice on purpose. Drawing snaps to 3 m, but loading and importing must not: a wall
            // measured off a retail mesh is any length at all, and rounding it on the way in makes Load stop
            // being the inverse of Save. Every other wall here is lattice-aligned, so without this the whole
            // suite is blind to a snap on the spawn path.
            b.Length = 7.43f;
            b.Rebuild();
            yield return Step.Ticks(1);

            T.Check("saved both walls", tool.Save() == 2);
            T.Check("the file exists", System.IO.File.Exists(Path));
            // (deleted at the end -- this file lives in the content tree)

            // reload into a SECOND tool, the way opening the map again would
            var tool2 = new EditorBuildings();
            World.AddChild(tool2);
            tool2.Setup(ed, null, null);                   // Setup loads
            yield return Step.Ticks(1);

            T.Check($"loaded both walls ({tool2.Walls.Count})", tool2.Walls.Count == 2);
            if (tool2.Walls.Count != 2) { System.IO.File.Delete(Path); yield break; }
            var la = tool2.Walls[0];
            var lb = tool2.Walls[1];
            T.Check($"first wall keeps its openings ({la.Openings.Count})", la.Openings.Count == 2);
            T.Check($"and its length ({la.Length:0.##})", Mathf.Abs(la.Length - 12f) < 1e-3f);
            T.Check($"and its palette ({la.MaterialId})", la.MaterialId == 20);
            T.Check($"second wall keeps its yaw ({lb.RotationDegrees.Y:0.#})", Mathf.Abs(lb.RotationDegrees.Y + 90f) < 1e-3f);
            T.Check($"and its off-lattice length, unrounded ({lb.Length:0.###}, want 7.43)", Mathf.Abs(lb.Length - 7.43f) < 1e-2f);
            T.Check($"and its partition thickness ({lb.Thickness:0.##})",
                    Mathf.Abs(lb.Thickness - WallOpenings.InteriorThickness) < 1e-3f);
            T.Check("a loaded wall is pickable -- one nothing can select is still lost", la.BodyRid.IsValid);

            // deleting the last wall and saving must overwrite, or the building comes back next session
            foreach (var w in new List<WallSurface>(tool2.Walls)) tool2.RemoveWall(w);
            T.Check("an empty layout saves as empty", tool2.Save() == 0);
            var tool3 = new EditorBuildings();
            World.AddChild(tool3);
            tool3.Setup(ed, null, null);
            T.Check("and stays deleted on reload", tool3.Walls.Count == 0);

            System.IO.File.Delete(Path);
        }
    }

    public class BuildToolBakeRoundTripsThroughThePropPipeline : GameTest
    {
        public override string Name => "buildtool.bake_round_trips_through_the_prop_pipeline";

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");
        const string Nm = "__l1_bake_test";

        static void Clean()
        {
            foreach (var f in new[] { Dir + Nm + ".obj", Dir + Nm + "_tex.png",
                                      EditorBuildings.BuildingSourcePath(Nm) })
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            // and take the name back out of the registry: LoadBakedBuildings already skips entries with no
            // .obj, so leaving it is harmless in the editor -- but it accumulates test names in a file that
            // lives in the content tree, and litter nobody put there is litter nobody dares delete.
            string list = EditorBuildings.BakedListPath();
            if (!System.IO.File.Exists(list)) return;
            var keep = new List<string>();
            foreach (var l in System.IO.File.ReadAllLines(list))
                if (l.Trim() != Nm && l.Trim().Length > 0) keep.Add(l.Trim());
            if (keep.Count > 0) System.IO.File.WriteAllLines(list, keep);
            else System.IO.File.Delete(list);
        }

        /// <summary>Mean agreement between each triangle's winding normal and its stored vertex normal.
        /// Sign, not magnitude, is the answer -- and it is only meaningful compared against a mesh known to be
        /// right, which is why this is also run on a retail prop below.</summary>
        static float WindingAgreement(ArrayMesh m)
        {
            var arr = m.SurfaceGetArrays(0);
            var v = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
            var n = (Vector3[])arr[(int)Mesh.ArrayType.Normal];
            // ObjMesh.Load commits UNINDEXED triangles, and an absent index array arrives as an EMPTY one
            // rather than null -- so a plain null check still left the count at zero and this returned 0 for
            // the retail prop as well as the baked one. A blind instrument agreeing with itself reads exactly
            // like a real failure, and it cost two rounds here. Length, not null.
            var ix = (int[])arr[(int)Mesh.ArrayType.Index];
            bool indexed = ix != null && ix.Length > 0;
            int count = indexed ? ix.Length : v.Length;
            int At(int i) => indexed ? ix[i] : i;
            float sum = 0f; int c = 0;
            for (int i = 0; i + 2 < count; i += 3)
            {
                Vector3 a = v[At(i)], b = v[At(i + 1)], d = v[At(i + 2)];
                var g = (b - a).Cross(d - a);
                if (g.LengthSquared() < 1e-12f) continue;
                var stored = (n[At(i)] + n[At(i + 1)] + n[At(i + 2)]);
                if (stored.LengthSquared() < 1e-12f) continue;
                sum += g.Normalized().Dot(stored.Normalized());
                c++;
            }
            return c == 0 ? 0f : sum / c;
        }

        public override IEnumerable<Step> Run()
        {
            // A baked building is an .obj plus a palette PNG, placed by the same code as every ripped prop --
            // so the only thing that can really go wrong is the FRAME. Prop meshes are authored lying down and
            // pitched 270 about X at placement, and a sign error there bakes a building that loads on its side
            // or mirrored. Both look deliberate in a screenshot, so this compares geometry instead: the mesh
            // that comes back out through the real loader must occupy the same box the walls did.
            Clean();
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            var a = tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddOpening(a, 3f, 0f, 0);
            var b = tool.AddWall(o + new Vector3(-6f, 0f, -9f), -90f, 9f);
            yield return Step.Ticks(1);

            // the box the walls actually occupy, in building-local space
            var want = new Aabb();
            bool first = true;
            foreach (var w in tool.Walls)
                foreach (var node in new[] { "Mesh", "TrimMesh" })
                {
                    var mi = w.GetNodeOrNull<MeshInstance3D>(node);
                    if (mi?.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                    // Transform the WHOLE box. This used to pair the transformed position with the mesh's
                    // UNROTATED size, which for the yaw--90 wall is wrong by that wall's entire run -- and the
                    // union with the other wall happened to paper over it in this exact layout. A ruler that
                    // reads correctly by coincidence is worse than no ruler.
                    var full = w.GlobalTransform * mi.Mesh.GetAabb();
                    full.Position -= o;
                    want = first ? full : want.Merge(full);
                    first = false;
                }

            T.Check("baked", tool.Bake(Nm) == Nm);
            T.Check("wrote the mesh", System.IO.File.Exists(Dir + Nm + ".obj"));
            T.Check("wrote the palette", System.IO.File.Exists(Dir + Nm + "_tex.png"));
            T.Check("kept the source so it stays editable", System.IO.File.Exists(EditorBuildings.BuildingSourcePath(Nm)));

            var loaded = ObjMesh.Load(Dir + Nm + ".obj");
            if (loaded == null || loaded.GetSurfaceCount() == 0) { T.Fail("the baked obj did not load"); Clean(); yield break; }

            // through the REAL placement basis, not a hand-written one
            var placed = new Transform3D(EditorObjects.Upright(0f), Vector3.Zero) * loaded.GetAabb();
            T.Check($"stands the right way up: height {placed.Size.Y:0.##} vs {want.Size.Y:0.##}",
                    Mathf.Abs(placed.Size.Y - want.Size.Y) < 0.05f);
            T.Check($"and the right way round: footprint {placed.Size.X:0.##}x{placed.Size.Z:0.##} vs {want.Size.X:0.##}x{want.Size.Z:0.##}",
                    Mathf.Abs(placed.Size.X - want.Size.X) < 0.05f && Mathf.Abs(placed.Size.Z - want.Size.Z) < 0.05f);
            T.Check($"and in the right place: origin {placed.Position} vs {want.Position}",
                    (placed.Position - want.Position).Length() < 0.05f);

            // Winding judged against a mesh known to be right rather than against a convention I reasoned out:
            // whatever sign a retail prop gives through this loader is the correct one.
            float mine = WindingAgreement(loaded);
            var retail = ObjMesh.Load(Dir + "House_00.obj");
            if (retail == null || retail.GetSurfaceCount() == 0) T.Fail("House_00.obj missing -- cannot judge winding");
            else
            {
                float theirs = WindingAgreement(retail);
                // |theirs| > 0.3 is the instrument's own self-check: a reading near zero means it is not
                // measuring winding at all, and must not be read as agreement.
                T.Check($"the reference reading is meaningful (House_00 {theirs:0.00}, near 0 = blind)", Mathf.Abs(theirs) > 0.3f);
                T.Check($"faces point the same way as a retail prop (baked {mine:0.00}, House_00 {theirs:0.00})",
                        Mathf.Abs(mine) > 0.3f && Mathf.Sign(mine) == Mathf.Sign(theirs));
            }

            // Every UV must land on a texel that actually holds a colour. The unused texels are filled
            // MAGENTA on purpose, and the first bake sampled nothing else: V was inverted here and again in
            // the writer, so every face read the wrong palette row. Geometry and winding were both perfect,
            // which is why neither check above noticed -- the building was the right shape in the wrong paint.
            var tex = new Image();
            if (tex.Load(Dir + Nm + "_tex.png") != Error.Ok) T.Fail("baked palette did not load");
            else
            {
                var ua = (Vector2[])loaded.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV];
                int bad = 0, sampled = 0;
                var seen = new HashSet<int>();
                foreach (var uvp in ua)
                {
                    int px = Mathf.Clamp((int)(uvp.X * tex.GetWidth()), 0, tex.GetWidth() - 1);
                    int py = Mathf.Clamp((int)(uvp.Y * tex.GetHeight()), 0, tex.GetHeight() - 1);
                    seen.Add(py * tex.GetWidth() + px);
                    sampled++;
                    var c = tex.GetPixel(px, py);
                    if (c.R8 == 255 && c.G8 == 0 && c.B8 == 255) bad++;
                }
                T.Check($"no face samples an unused palette texel ({bad} of {sampled} landed on the magenta fill)", bad == 0);
                T.Check($"and it uses BOTH the wall and the reveal colour ({seen.Count} distinct texels)", seen.Count >= 2);
            }

            // and it is now a placeable prop, without reopening the editor
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            T.Check("it is in the props palette", new List<string>(objs.Catalog).Contains(Nm));

            Clean();
        }
    }

    public class BuildToolFloorIsAWallLyingDown : GameTest
    {
        public override string Name => "buildtool.floor_is_a_wall_lying_down";

        public override IEnumerable<Step> Run()
        {
            // A floor is the same surface pitched flat, so what needs proving is not the partition -- that is
            // already covered upright -- but that the pitch lands the slab where a person would stand on it,
            // and that its hole is a hole in the thing you walk on too.
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), 0f, 12f);
            yield return Step.Ticks(1);

            var floor = tool.AddSlab(SurfaceKind.Floor);
            T.Check("a floor was added", floor != null);
            if (floor == null) yield break;
            var roof = tool.AddSlab(SurfaceKind.Roof);
            T.Check("and a roof", roof != null);
            if (roof == null) yield break;
            yield return Step.Ticks(2);

            // The slab's TOP is the surface you stand on. Half a thickness out and you spawn inside the floor,
            // which reads in game as falling through it -- and looks perfectly fine in a screenshot.
            float floorTop = floor.Position.Y + floor.Thickness * 0.5f;
            float roofBottom = roof.Position.Y - roof.Thickness * 0.5f;
            T.Check($"the floor's top meets the walls' base ({floorTop:0.###} vs {o.Y:0.###})",
                    Mathf.Abs(floorTop - o.Y) < 1e-3f);
            T.Check($"the roof's underside meets the walls' head ({roofBottom:0.###} vs {o.Y + WallOpenings.DoorHeight:0.###})",
                    Mathf.Abs(roofBottom - (o.Y + WallOpenings.DoorHeight)) < 1e-3f);
            T.Check("a slab is a surface, not a new kind of object", floor.Kind == SurfaceKind.Floor && roof.Kind == SurfaceKind.Roof);
            T.Check($"pitched flat ({floor.RotationDegrees.X:0.#} deg)", Mathf.Abs(floor.RotationDegrees.X + 90f) < 1e-3f);

            // It spans the walls and stops FLUSH with their outer face -- not overhanging (retail does not)
            // and not stopping at the centre-line (which leaves the outer half of every wall poking through).
            // Walls run x -6..6 and z 0..-9 on their mid-planes, at 0.70 thick, so 12.7 x 9.7.
            float want = WallOpenings.DefaultThickness;
            T.Check($"it stops flush with the outer wall face ({floor.Length:0.###} x {floor.Height:0.###}, want {12f + want:0.###} x {9f + want:0.###})",
                    Mathf.Abs(floor.Length - (12f + want)) < 1e-2f && Mathf.Abs(floor.Height - (9f + want)) < 1e-2f);

            // and you can stand on it -- checked through physics, not by reading back the numbers that made it
            var space = World.GetWorld3D().DirectSpaceState;
            bool Hits(Vector3 at)
            {
                var q = new PhysicsRayQueryParameters3D
                {
                    From = at + new Vector3(0f, 3f, 0f), To = at - new Vector3(0f, 3f, 0f), CollisionMask = 1u << 0,
                };
                return space.IntersectRay(q).Count > 0;
            }
            T.Check("the floor is solid underfoot", Hits(o + new Vector3(0f, 0f, -4.5f)));

            // a stairwell: an opening in a floor is the same opening
            floor.Openings.Add(new WallOpening(floor.Length * 0.5f - 1.25f, floor.Height * 0.5f - 1.25f, 2.5f, 2.5f));
            floor.Rebuild();
            yield return Step.Ticks(2);
            var mid = floor.UVToWorld(floor.Length * 0.5f, floor.Height * 0.5f);
            T.Check("a stairwell hole is open underfoot", !Hits(mid));
            T.Check("and the slab beside it still is not", Hits(o + new Vector3(-5f, 0f, -4.5f)));
        }
    }

    public class BuildToolSlabsSurviveSaveAndBake : GameTest
    {
        public override string Name => "buildtool.slabs_survive_save_and_bake";

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");
        const string Nm = "__l1_slab_test";

        static void Clean()
        {
            // ...including the map save this test writes. It was cleaning the objects dir and leaving
            // editor_none_Walls.dat behind every run -- litter in the content tree, from the test whose own
            // Clean comment lectures about litter.
            foreach (var f in new[] { Dir + Nm + ".obj", Dir + Nm + "_tex.png", EditorBuildings.BuildingSourcePath(Nm),
                                      ProjectSettings.GlobalizePath("res://content/buildings/") + "editor_none_Walls.dat" })
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            string list = EditorBuildings.BakedListPath();
            if (!System.IO.File.Exists(list)) return;
            var keep = new List<string>();
            foreach (var l in System.IO.File.ReadAllLines(list))
                if (l.Trim() != Nm && l.Trim().Length > 0) keep.Add(l.Trim());
            if (keep.Count > 0) System.IO.File.WriteAllLines(list, keep);
            else System.IO.File.Delete(list);
        }

        public override IEnumerable<Step> Run()
        {
            // Pitch and kind are trailing fields on a format that already shipped. Dropping either loses a
            // floor QUIETLY -- it reloads as an upright wall standing where the floor was, which looks like a
            // stray wall rather than like a bug in saving.
            Clean();
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), 0f, 12f);
            var floor = tool.AddSlab(SurfaceKind.Floor);
            tool.AddSlab(SurfaceKind.Roof);
            float len = floor.Length, dep = floor.Height, y = floor.Position.Y;
            yield return Step.Ticks(1);

            T.Check("saved all four surfaces", tool.Save() == 4);
            var tool2 = new EditorBuildings();
            World.AddChild(tool2);
            tool2.Setup(ed, null, null);
            yield return Step.Ticks(1);

            int floors = 0, roofs = 0, walls = 0;
            WallSurface back = null;
            foreach (var w in tool2.Walls)
            {
                if (w.Kind == SurfaceKind.Floor) { floors++; back = w; }
                else if (w.Kind == SurfaceKind.Roof) roofs++;
                else walls++;
            }
            T.Check($"reloaded 2 walls, 1 floor, 1 roof (got {walls}/{floors}/{roofs})", walls == 2 && floors == 1 && roofs == 1);
            if (back != null)
            {
                T.Check($"the floor came back lying down ({back.RotationDegrees.X:0.#} deg)", Mathf.Abs(back.RotationDegrees.X + 90f) < 1e-2f);
                T.Check($"at its height ({back.Position.Y:0.###} vs {y:0.###})", Mathf.Abs(back.Position.Y - y) < 1e-2f);
                T.Check($"and its span ({back.Length:0.##}x{back.Height:0.##} vs {len:0.##}x{dep:0.##})",
                        Mathf.Abs(back.Length - len) < 1e-2f && Mathf.Abs(back.Height - dep) < 1e-2f);
            }

            // the bake walks every surface, not just the upright ones
            T.Check("baked", tool2.Bake(Nm) == Nm);
            var loaded = ObjMesh.Load(Dir + Nm + ".obj");
            if (loaded == null || loaded.GetSurfaceCount() == 0) { T.Fail("baked obj did not load"); Clean(); yield break; }
            var placed = new Transform3D(EditorObjects.Upright(0f), Vector3.Zero) * loaded.GetAabb();
            // walls alone are 4.25 tall; with a floor under and a roof over it must be taller than that
            T.Check($"the baked prop includes the slabs (height {placed.Size.Y:0.##} > wall height {WallOpenings.DoorHeight:0.##})",
                    placed.Size.Y > WallOpenings.DoorHeight + 0.5f);
            Clean();
        }
    }

    public class BuildToolGableRoofClosesAtARidge : GameTest
    {
        public override string Name => "buildtool.gable_roof_closes_at_a_ridge";

        public override IEnumerable<Step> Run()
        {
            // Checked as GEOMETRY, not against the formula that built it: the two slopes must meet at one
            // line, that line must be above the walls, the eaves must sit ON the walls, and the angle
            // recovered from the surface itself must be the angle asked for. Re-deriving half*tan(pitch) here
            // would just agree with the constructor, including when the constructor is wrong.
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            const float Pitch = 20f;
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);        // runs along X
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), 0f, 12f);       // runs along X
            var endA = tool.AddWall(o + new Vector3(-6f, 0f, -9f), -90f, 9f);   // runs along Z
            var endB = tool.AddWall(o + new Vector3(6f, 0f, -9f), -90f, 9f);
            yield return Step.Ticks(1);

            T.Check("gable roof added", tool.AddGableRoof(Pitch) > 0);
            yield return Step.Ticks(2);

            var roofs = new List<WallSurface>();
            foreach (var w in tool.Walls) if (w.Kind == SurfaceKind.Roof) roofs.Add(w);
            T.Check($"two slopes ({roofs.Count})", roofs.Count == 2);
            if (roofs.Count != 2) yield break;

            float wallTop = o.Y + WallOpenings.DoorHeight;
            var eaveA = roofs[0].UVToWorld(roofs[0].Length * 0.5f, 0f);
            var ridgeA = roofs[0].UVToWorld(roofs[0].Length * 0.5f, roofs[0].Height);
            var eaveB = roofs[1].UVToWorld(roofs[1].Length * 0.5f, 0f);
            var ridgeB = roofs[1].UVToWorld(roofs[1].Length * 0.5f, roofs[1].Height);

            T.Check($"the eaves sit on the walls ({eaveA.Y:0.##} / {eaveB.Y:0.##} vs {wallTop:0.##})",
                    Mathf.Abs(eaveA.Y - wallTop) < 1e-2f && Mathf.Abs(eaveB.Y - wallTop) < 1e-2f);
            T.Check($"the two slopes meet at one ridge (gap {(ridgeA - ridgeB).Length():0.###} m)",
                    (ridgeA - ridgeB).Length() < 2e-2f);
            T.Check($"the ridge is above the walls ({ridgeA.Y - wallTop:0.##} m of rise)", ridgeA.Y - wallTop > 0.5f);

            float run = new Vector2(ridgeA.X - eaveA.X, ridgeA.Z - eaveA.Z).Length();
            float got = Mathf.RadToDeg(Mathf.Atan2(ridgeA.Y - eaveA.Y, run));
            T.Check($"and the slope is the pitch asked for ({got:0.#} deg vs {Pitch:0.#})", Mathf.Abs(got - Pitch) < 0.5f);

            // the walls across the ridge become gable ends; the ones along it stay flat-topped
            int gabled = 0, flatTop = 0;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Wall) continue;
                if (w.GableRise > 0.01f) gabled++; else flatTop++;
            }
            T.Check($"only the two end walls are gabled ({gabled} gabled, {flatTop} flat-topped)", gabled == 2 && flatTop == 2);
            T.Check("and they are the ones across the ridge", endA.GableRise > 0.01f && endB.GableRise > 0.01f);

            // A gable's collider must be the TRIANGLE, not a box round it -- a box fills the two wedges of air
            // beside the peak, and you collide with a roof corner that is not there.
            var space = World.GetWorld3D().DirectSpaceState;
            bool Solid(Vector3 at)
            {
                var q = new PhysicsRayQueryParameters3D
                { From = at + new Vector3(0.9f, 0f, 0f), To = at - new Vector3(0.9f, 0f, 0f), CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }
            var peak = endA.UVToWorld(endA.Length * 0.5f, endA.Height + endA.GableRise * 0.5f);
            var corner = endA.UVToWorld(endA.Length * 0.06f, endA.Height + endA.GableRise * 0.85f);
            T.Check("the gable itself is solid", Solid(peak));
            T.Check("but the air beside the peak is not", !Solid(corner));
        }
    }

    public class BuildToolFoundationIsASkirt : GameTest
    {
        public override string Name => "buildtool.foundation_is_a_skirt";

        public override IEnumerable<Step> Run()
        {
            // Measured off retail: all 52 buildings sink below ground, as a hollow skirt (hundreds of m2 of
            // side face, essentially no bottom) 5-6 m deep. So a foundation is a wall under a wall, and the
            // thing worth checking is that it MEETS the wall -- a gap there is daylight under the building on
            // any slope, and invisible from above.
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), -90f, 9f);
            yield return Step.Ticks(1);

            T.Check("one foundation per wall", tool.AddFoundation() == 3);
            yield return Step.Ticks(2);

            int n = 0;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Foundation) continue;
                n++;
                T.Check($"foundation {n} meets its wall ({w.Position.Y + w.Height:0.###} vs {o.Y:0.###})",
                        Mathf.Abs((w.Position.Y + w.Height) - o.Y) < 1e-3f);
                T.Check($"and reaches the measured depth ({w.Height:0.##} m)",
                        Mathf.Abs(w.Height - WallOpenings.FoundationDepth) < 1e-3f);
            }
            T.Check($"three of them ({n})", n == 3);

            // hollow, not a block: solid where a wall is, open in the middle of the room
            var space = World.GetWorld3D().DirectSpaceState;
            bool Solid(Vector3 at)
            {
                var q = new PhysicsRayQueryParameters3D
                { From = at + new Vector3(0f, 0f, 2f), To = at - new Vector3(0f, 0f, 2f), CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }
            T.Check("solid under a wall", Solid(o + new Vector3(0f, -3f, 0f)));
            T.Check("hollow under the middle of the room", !Solid(o + new Vector3(0f, -3f, -4.5f)));
        }
    }

    public class BuildToolCornersAreSolvedOnBake : GameTest
    {
        public override string Name => "buildtool.corners_are_solved_on_bake";

        public override IEnumerable<Step> Run()
        {
            // Two walls meeting at their centre-lines leave a quarter of a thickness MISSING at the outer
            // corner -- a square notch you can see through from outside, which nothing on the inside fills.
            // While drawing, walls are meant to just interpenetrate; the solve happens once, at bake.
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            foreach (var old in new List<WallSurface>(tool.Walls)) tool.RemoveWall(old);

            var o = EditorBuildings.StageOrigin;
            // Yaw -90 turns local +X into world +Z, so this wall runs from z=-9 UP TO the corner at z=0 --
            // it ends there, it does not start there. Starting it at the corner instead sends it away across
            // the notch and fills the very gap the test is looking for, which reads as the corner already
            // being solved.
            var a = tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);       // along +X, ending at x=+6
            var b = tool.AddWall(o + new Vector3(6f, 0f, -9f), -90f, 9f);      // along +Z, ending at z=0
            float lenA = a.Length, lenB = b.Length;
            yield return Step.Ticks(2);

            var space = World.GetWorld3D().DirectSpaceState;
            // The ray has to START OUTSIDE the geometry. IntersectRay does not report a hit from inside a
            // shape by default, so a probe beginning at mid-wall height reports "not solid" while standing in
            // the middle of a wall -- which is indistinguishable from the corner logic having failed. Caught
            // by the sanity check beside the real one, which is what that check is for.
            bool Solid(Vector3 at)
            {
                var q = new PhysicsRayQueryParameters3D
                { From = at + new Vector3(0f, 6f, 0f), To = at - new Vector3(0f, 0.5f, 0f), CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }
            // dead centre of the missing quarter: outside both centre-lines, inside both outer faces
            float q4 = WallOpenings.DefaultThickness * 0.25f;
            var notch = o + new Vector3(6f + q4, 2f, q4);

            T.Check("the outer corner is open while drawing", !Solid(notch));
            T.Check("and the walls next to it are not", Solid(o + new Vector3(3f, 2f, 0f)));

            var undo = tool.SolveCorners();
            yield return Step.Ticks(2);
            T.Check("solving fills the outer corner", Solid(notch));
            T.Check($"by running each wall past the junction ({a.Length - lenA:0.###} m, want {WallOpenings.DefaultThickness * 0.5f:0.###})",
                    Mathf.Abs((a.Length - lenA) - WallOpenings.DefaultThickness * 0.5f) < 1e-3f);
            T.Check($"both of them ({b.Length - lenB:0.###} m)",
                    Mathf.Abs((b.Length - lenB) - WallOpenings.DefaultThickness * 0.5f) < 1e-3f);

            tool.RestoreCorners(undo);
            yield return Step.Ticks(2);
            T.Check("and it is put back afterwards, so editing is unaffected",
                    Mathf.Abs(a.Length - lenA) < 1e-4f && Mathf.Abs(b.Length - lenB) < 1e-4f);
            T.Check("the corner is open again", !Solid(notch));

            // Foundations get solved too: the same notch exists in the buried skirt, where nothing would ever
            // reveal it except ground falling away beside the building.
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddWall(o + new Vector3(6f, 0f, -9f), -90f, 9f);
            tool.AddFoundation();
            yield return Step.Ticks(2);
            var below = o + new Vector3(6f + q4, -3f, q4);
            T.Check("the foundation corner is open too, before solving", !Solid(below));
            var u3 = tool.SolveCorners();
            yield return Step.Ticks(2);
            T.Check("and solving fills it", Solid(below));
            int solvedFoundations = 0;
            foreach (var (w, _, _) in u3) if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Foundation) solvedFoundations++;
            T.Check($"both foundation runs were extended ({solvedFoundations})", solvedFoundations == 2);
            tool.RestoreCorners(u3);

            // parallel walls are a seam, not a corner -- extending those just overlaps two walls end to end
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            var p1 = tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 6f);
            var p2 = tool.AddWall(o + new Vector3(0f, 0f, 0f), 0f, 6f);
            float l1 = p1.Length, l2 = p2.Length;
            yield return Step.Ticks(1);
            var u2 = tool.SolveCorners();
            T.Check("two walls in a straight line are left alone",
                    Mathf.Abs(p1.Length - l1) < 1e-4f && Mathf.Abs(p2.Length - l2) < 1e-4f);
            tool.RestoreCorners(u2);
        }
    }

    public class BuildToolImportsARetailBuilding : GameTest
    {
        public override string Name => "buildtool.imports_a_retail_building";

        public override IEnumerable<Step> Run()
        {
            // The translator, against a real ripped building rather than a synthetic one -- the round trip is
            // already L0'd on clean input, and what only a real mesh can test is whether the panel finding
            // survives geometry nobody authored for us.
            string obj = ProjectSettings.GlobalizePath("res://content/objects/House_00.obj");
            if (!System.IO.File.Exists(obj)) { T.Fail("House_00.obj missing"); yield break; }

            var plans = BuildingImport.FromObj(obj);
            T.Check($"it recovered walls at all ({plans.Count})", plans.Count >= 4);
            if (plans.Count == 0) yield break;

            // Heights are the sharpest check on the FRAME. Import off the raw mesh instead of the upright one
            // and the building comes back on its side: the walls are still walls, still rectangular, still
            // paired -- they are just 16 m "tall" and 4 m long, which no other assertion here would notice.
            int sane = 0, tall = 0;
            float maxLen = 0f;
            foreach (var p in plans)
            {
                if (p.Height > 1.5f && p.Height < 14f) sane++;
                if (p.Height >= 14f) tall++;
                maxLen = Mathf.Max(maxLen, p.Length);
            }
            T.Check($"wall heights are wall-shaped ({sane} of {plans.Count} between 1.5 and 14 m, {tall} absurd)",
                    sane > plans.Count / 2 && tall == 0);
            T.Check($"and the longest run matches a house, not a continent ({maxLen:0.#} m)", maxLen > 4f && maxLen < 40f);

            int withOpenings = 0, openings = 0;
            foreach (var p in plans) { if (p.Openings.Count > 0) withOpenings++; openings += p.Openings.Count; }
            T.Check($"it found openings ({openings} across {withOpenings} walls)", openings > 0);

            // thickness comes from PAIRING the two faces of each wall; unpaired, everything falls back to the
            // default and this spread collapses to a single value
            // >= 2 distinct, not >= 1: every non-empty plan list has at least one thickness, including the
            // all-defaulted collapse this check exists to catch. It asserted >= 1 and could not fail.
            var seen = new HashSet<int>();
            int defaulted = 0;
            foreach (var p in plans)
            {
                seen.Add(Mathf.RoundToInt(p.Thickness * 100f));
                if (Mathf.Abs(p.Thickness - WallOpenings.DefaultThickness) < 1e-3f) defaulted++;
            }
            T.Check($"thicknesses were measured off the mesh, not defaulted ({seen.Count} distinct, {defaulted}/{plans.Count} at the default)",
                    seen.Count >= 2 || defaulted < plans.Count);
            foreach (var p in plans)
                T.Check($"wall thickness {p.Thickness:0.##} is plausible", p.Thickness >= 0.2f && p.Thickness <= 1.2f);

            // and what comes out must go straight back in: an import that cannot be saved is not a port
            string text = WallSave.Write(plans);
            var back = WallSave.Read(new List<string>(text.Split('\n')));
            T.Check($"the import round-trips through the save format ({back.Count} of {plans.Count})", back.Count == plans.Count);
            // and it round-trips the VALUES, not just the row count -- a formatter that wrote zeroes would
            // have passed the count check.
            int drift = 0;
            for (int i = 0; i < Mathf.Min(back.Count, plans.Count); i++)
            {
                var x = plans[i];
                var y = back[i];
                if (Mathf.Abs(x.X - y.X) > 1e-2f || Mathf.Abs(x.Y - y.Y) > 1e-2f || Mathf.Abs(x.Z - y.Z) > 1e-2f
                    || Mathf.Abs(x.Length - y.Length) > 1e-2f || Mathf.Abs(x.Height - y.Height) > 1e-2f
                    || Mathf.Abs(x.Thickness - y.Thickness) > 1e-2f || Mathf.Abs(Mathf.Wrap(x.Yaw - y.Yaw, -180f, 180f)) > 1e-2f
                    || x.Openings.Count != y.Openings.Count) drift++;
            }
            T.Check($"every wall came back identical ({drift} drifted)", drift == 0);
            yield break;
        }
    }

    public class BuildToolTrimIsShootableButNotSolid : GameTest
    {
        public override string Name => "buildtool.trim_is_shootable_but_not_solid";

        public override IEnumerable<Step> Run()
        {
            // strawberry: "the trim in game has collision no? maybe have bullet etc collision but omit player
            // collision." The body for that existed and was documented -- and nothing ever put a shape in it,
            // so it was a dead node and editor frames were not shootable at all. Worse, a BAKED building's
            // frames ARE solid (the prop path trimeshes the whole render mesh), so the same building behaved
            // differently before and after baking, which is the kind of difference nobody thinks to check.
            var w = new WallSurface { Length = 12f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            w.Openings.Add(new WallOpening(4f, 0f, 2.5f, WallOpenings.DoorHeight - 0.5f));
            w.Rebuild();
            yield return Step.Ticks(2);

            var space = World.GetWorld3D().DirectSpaceState;
            bool Hit(float u, float v, uint layer)
            {
                var a = w.UVToWorld(u, v) + new Vector3(0f, 0f, 3f);
                var b = w.UVToWorld(u, v) - new Vector3(0f, 0f, 3f);
                var q = new PhysicsRayQueryParameters3D { From = a, To = b, CollisionMask = layer };
                return space.IntersectRay(q).Count > 0;
            }

            const uint World0 = 1u << 0, Props = 1u << 6;
            // just inside the doorway edge: the reveal lining is there, the wall is not
            float u = 4f + WallSurface.TrimProfile * 0.5f, v = 2f;
            T.Check("a bullet ray hits the doorframe", Hit(u, v, Props));
            T.Check("but movement passes through it", !Hit(u, v, World0));
            T.Check("mid-doorway is clear on both layers", !Hit(5.25f, 2f, Props) && !Hit(5.25f, 2f, World0));
            T.Check("and the wall beside it still stops movement", Hit(1f, 2f, World0));
        }
    }
}
