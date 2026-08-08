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

    // An imported roof, checked against the MESH IT CAME FROM rather than against a screenshot.
    //
    // This exists because a render cannot check it. Every roof plane the importer emitted was in the right
    // place, at the right pitch, inside the source bounding box -- and every one of them was mirrored about
    // the vertical, rising toward its eave instead of toward the ridge. From a three-quarter view that is
    // four dark slabs at plausible angles; I read the same frame twice and concluded the geometry was fine.
    // The only thing that settles it is putting the rebuilt surface and the source plane in one assertion.
    public class BuildToolImportedRoofsMatchTheSourcePlanes : GameTest
    {
        public override string Name => "buildtool.imported_roofs_match_the_source_planes";

        public override IEnumerable<Step> Run()
        {
            string obj = ProjectSettings.GlobalizePath("res://content/objects/House_00.obj");
            if (!System.IO.File.Exists(obj)) { T.Fail("House_00.obj missing"); yield break; }

            // The source planes, derived here independently of the importer -- same negation, because that
            // one is separately established (0 of 732 agree with the file's vn without it).
            var mesh = ObjMesh.Load(obj);
            var v = (Vector3[])mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
            var xf = EditorObjects.Upright(0f);
            var srcPlanes = new List<(Vector3 N, float D)>();
            var srcArea = new Dictionary<(int, int, int, int), float>();      // real area per roof plane
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                Vector3 a = xf * v[i], b = xf * v[i + 1], c = xf * v[i + 2];
                var cr = (b - a).Cross(c - a);
                if (cr.LengthSquared() < 1e-9f) continue;
                var n = -cr.Normalized();
                if (n.Y <= 0.25f || n.Y > 0.98f) continue;         // sloped and upward: a roof
                float d = n.Dot(a);
                srcPlanes.Add((n, d));
                var key = (Mathf.RoundToInt(n.X * 20f), Mathf.RoundToInt(n.Y * 20f),
                           Mathf.RoundToInt(n.Z * 20f), Mathf.RoundToInt(d * 20f));
                srcArea.TryGetValue(key, out float had);
                srcArea[key] = had + cr.Length() * 0.5f;
            }
            T.Check($"the mesh has sloped roof planes to compare against ({srcPlanes.Count} triangles)",
                    srcPlanes.Count > 0);

            var plans = BuildingImport.FromObj(obj);
            var roofs = new List<WallPlan>();
            foreach (var pl in plans) if (pl.Kind == SurfaceKind.Roof) roofs.Add(pl);
            T.Check($"the importer emitted roofs ({roofs.Count})", roofs.Count > 0);
            if (roofs.Count == 0) yield break;

            // Rebuild each one as the real surface -- not as arithmetic repeating the importer's own
            // formula, which would agree with the bug.
            var built = new List<WallSurface>();
            foreach (var pl in roofs)
            {
                var w = new WallSurface
                {
                    Length = pl.Length, Height = pl.Height, Thickness = pl.Thickness, Kind = pl.Kind,
                    Position = new Vector3(pl.X, pl.Y, pl.Z),
                    RotationDegrees = new Vector3(pl.Pitch, pl.Yaw, 0f),
                };
                World.AddChild(w);
                built.Add(w);
            }
            yield return Step.Ticks(2);

            // the source mesh's own extent, for "is this roof even over the building"
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            foreach (var raw in v) { var pw = xf * raw; lo = lo.Min(pw); hi = hi.Max(pw); }

            int matched = 0;
            var eaves = new List<float>();
            foreach (var w in built)
            {
                var p00 = w.UVToWorld(0f, 0f);
                var p10 = w.UVToWorld(w.Length, 0f);
                var p01 = w.UVToWorld(0f, w.Height);
                var n = (p10 - p00).Cross(p01 - p00).Normalized();
                float best = -1f;
                foreach (var (sn, sd) in srcPlanes)
                {
                    if (Mathf.Abs(sn.Dot(p00) - sd) > 0.30f) continue;      // on that plane at all
                    best = Mathf.Max(best, Mathf.Abs(n.Dot(sn)));
                }
                // A mirrored surface still passes through its eave line, so an offset test alone would let
                // it through -- the direction is what has to agree.
                if (best > 0.99f) matched++;
                else GD.Print($"[roofcheck] roof at {p00} normal {n} matches no source plane (best |dot| {best:0.###})");

                // Two cheap independent properties. Deliberately NOT "the ridge end is nearer the building's
                // vertical axis than the eave end": I wrote that first and it failed a slope that is
                // correct, because House_00's main ridge crosses the origin, so moving inward along that
                // slope increases the radius. A heuristic that is only usually true is worse than no check.
                var eave = (p00 + p10) * 0.5f;
                var ridge = (p01 + w.UVToWorld(w.Length, w.Height)) * 0.5f;
                T.Check($"the slope rises ({eave.Y:0.00} -> {ridge.Y:0.00})", ridge.Y > eave.Y + 0.2f);
                eaves.Add(eave.Y);
                foreach (var c in new[] { p00, p10, p01, w.UVToWorld(w.Length, w.Height) })
                    T.Check($"roof corner {c} is over the building",
                            c.X >= lo.X - 1f && c.X <= hi.X + 1f && c.Z >= lo.Z - 1f && c.Z <= hi.Z + 1f
                            && c.Y <= hi.Y + 1f);
            }
            T.Check($"every rebuilt roof lies in a plane of the source mesh ({matched} of {built.Count})",
                    matched == built.Count);
            // one roof, so one eave line -- slopes landing at different heights cannot close against
            // each other whatever their individual pitches say
            float eLo = float.MaxValue, eHi = float.MinValue;
            foreach (float e in eaves) { eLo = Mathf.Min(eLo, e); eHi = Mathf.Max(eHi, e); }
            T.Check($"the slopes share an eave height ({eLo:0.00}..{eHi:0.00})", eHi - eLo < 0.5f);

            // AREA, which is the whole point of the trapezoid support. A cross-wing slope is cut by the
            // valley where it meets the main roof; emitted as its bounding rectangle it is 0.77 of its own
            // plane and the surplus hangs out over the valley. BREAK IT: drop the insets and two of the four
            // planes come back ~30% too big.
            foreach (var pl in roofs)
            {
                float area = pl.Length * pl.Height
                             - (pl.InsetL0 + pl.InsetL1) * 0.5f * pl.Height
                             - (pl.InsetR0 + pl.InsetR1) * 0.5f * pl.Height;
                var o = new Vector3(pl.X, pl.Y, pl.Z);
                float want = 0f;
                foreach (var kv in srcArea)
                {
                    var n = new Vector3(kv.Key.Item1 / 20f, kv.Key.Item2 / 20f, kv.Key.Item3 / 20f).Normalized();
                    if (Mathf.Abs(n.Dot(o) - kv.Key.Item4 / 20f) > 0.35f) continue;
                    want = Mathf.Max(want, kv.Value);
                }
                T.Check($"roof surface area {area:0.0} m2 matches its source plane's {want:0.0} m2",
                        want > 0f && Mathf.Abs(area - want) <= want * 0.06f);
            }
        }
    }
    // The two correctness changes from strawberry_cow's editor pass: walls land on a shared grid, and
    // openings stop against each other instead of being shoved out of the way.
    public class BuildToolGridAndHardEdges : GameTest
    {
        public override string Name => "buildtool.grid_snap_and_opening_hard_edges";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            tool.Active = true;
            yield return Step.Ticks(1);

            // ---- a wall's ENDS land on the grid, not just its length -------------------------------
            // BREAK IT: snap only the length (what it did before) -- the wall is an exact 3 m and still
            // starts at 1.4, so the next wall along cannot meet it and you get a hairline gap.
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            var off = tool.AddWall(new Vector3(1.4f, 0f, -2.2f), 0f, 4.1f);
            yield return Step.Ticks(1);
            float g = WallOpenings.GridStep;
            T.Check($"wall origin snapped to the grid ({off.Position.X:0.##}, {off.Position.Z:0.##})",
                    Mathf.Abs(off.Position.X / g - Mathf.Round(off.Position.X / g)) < 1e-3f
                    && Mathf.Abs(off.Position.Z / g - Mathf.Round(off.Position.Z / g)) < 1e-3f);
            T.Check($"and its length is still on the lattice ({off.Length:0.##})",
                    Mathf.Abs(off.Length / g - Mathf.Round(off.Length / g)) < 1e-3f);

            // two walls drawn from nearby off-grid points must end up sharing an endpoint exactly
            var a = tool.AddWall(new Vector3(0.4f, 0f, 0.3f), 0f, 3f);
            var b = tool.AddWall(new Vector3(2.6f, 0f, -0.4f), 90f, 3f);
            yield return Step.Ticks(1);
            // a runs +X from its origin, so it is a's END that must meet b's START
            T.Check($"two off-grid clicks produce walls that actually touch "
                    + $"({a.UVToWorld(a.Length, 0f)} / {b.Position})",
                    a.UVToWorld(a.Length, 0f).DistanceTo(b.Position) < 1e-3f);

            // ---- an opening stops against its neighbour ---------------------------------------------
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            var wall = tool.AddWall(Vector3.Zero, 0f, 12f);
            wall.Openings.Add(new WallOpening(1f, 1f, 3f, 2f));       // index 0, the one we drag
            wall.Openings.Add(new WallOpening(6f, 1f, 3f, 2f));       // index 1, the blocker
            wall.Rebuild();
            yield return Step.Ticks(1);

            // drag 0 hard right, straight through 1
            for (int i = 0; i < 12; i++) tool.MoveOpening(wall, 0, 4f + i * 0.8f, 2f, 0f);
            yield return Step.Ticks(1);
            var moved = wall.Openings[0];
            var block = wall.Openings[1];
            // BREAK IT: let Clamp's two-sided fallback stand -- the dragged opening ends up sitting on top
            // of its neighbour, which is exactly the "pushing things around" this replaced.
            T.Check($"the dragged opening did not end up overlapping its neighbour "
                    + $"(u {moved.U:0.##}..{moved.U1:0.##} vs {block.U:0.##}..{block.U1:0.##})",
                    !WallOpenings.Overlaps(moved, wall.Openings, 0));
            T.Check($"it stopped against it rather than stopping short ({moved.U1:0.##} vs {block.U:0.##})",
                    moved.U1 <= block.U + 1e-3f && moved.U1 > block.U - 0.35f);
            T.Check("and the blocker did not move", Mathf.Abs(block.U - 6f) < 1e-3f);

            // resizing into it stops too
            float before = wall.Openings[1].Width;
            for (int i = 0; i < 8; i++) tool.DragEdge(wall, 1, EditorBuildings.Drag.EdgeU0, 4f - i * 0.3f, 2f, 0f);
            yield return Step.Ticks(1);
            T.Check($"an edge dragged into a neighbour stops as well "
                    + $"({wall.Openings[1].U:0.##}, was 6.00, width {before:0.##} -> {wall.Openings[1].Width:0.##})",
                    !WallOpenings.Overlaps(wall.Openings[1], wall.Openings, 1));

            // ---- undo is reachable from this mode ---------------------------------------------------
            // Ctrl+Z is bound in _UnhandledInput, which a headless test cannot press; what it calls is
            // Editor.Undo, and what matters is that the building tool actually filled the stack.
            int depth = ed.UndoDepth;
            T.Check($"the building tool pushed undo steps ({depth})", depth > 0);
            int wallsBefore = tool.Walls.Count;
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"and undo takes one back ({wallsBefore} -> {tool.Walls.Count})",
                    tool.Walls.Count == wallsBefore - 1);
        }
    }
    // Two rooms drawn against each other put two walls on the shared edge. Coincident walls are not a
    // thicker wall -- they z-fight, double the collision, and an opening cut in one is filled back in by
    // the other.
    public class BuildToolMergesDuplicateWalls : GameTest
    {
        public override string Name => "buildtool.duplicate_walls_merge";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // exactly coincident -- the shared edge of two rooms
            var a = tool.AddWall(Vector3.Zero, 0f, 6f);
            var b = tool.AddWall(Vector3.Zero, 0f, 6f);
            a.Openings.Add(new WallOpening(1f, 0f, 2.5f, 3f));
            b.Openings.Add(new WallOpening(4.5f, 1f, 1f, 1f));
            a.Rebuild(); b.Rebuild();
            yield return Step.Ticks(1);
            T.Check($"two coincident walls fold into one ({tool.MergeDuplicateWalls()} merged, "
                    + $"{tool.Walls.Count} left)", tool.Walls.Count == 1);
            T.Check($"and the survivor kept both openings ({tool.Walls[0].Openings.Count})",
                    tool.Walls[0].Openings.Count == 2);

            // partial overlap: one long wall and a short one lying on top of half of it
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(Vector3.Zero, 0f, 12f);
            tool.AddWall(new Vector3(6f, 0f, 0f), 0f, 6f);
            yield return Step.Ticks(1);
            tool.MergeDuplicateWalls();
            T.Check($"an overlapping pair folds too ({tool.Walls.Count} left)", tool.Walls.Count == 1);
            T.Check($"and the survivor spans both ({tool.Walls[0].Length:0.#} m)",
                    Mathf.Abs(tool.Walls[0].Length - 12f) < 0.2f);

            // BREAK IT: merge on "same line" alone and this pair -- two walls END TO END, which is a
            // perfectly normal run -- collapses into one, silently deleting a wall the user placed.
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(Vector3.Zero, 0f, 6f);
            tool.AddWall(new Vector3(9f, 0f, 0f), 0f, 6f);          // a 3 m gap between them
            yield return Step.Ticks(1);
            tool.MergeDuplicateWalls();
            T.Check($"two walls in a row with a gap are left alone ({tool.Walls.Count})",
                    tool.Walls.Count == 2);

            // and walls on DIFFERENT lines are never confused for each other
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(Vector3.Zero, 0f, 6f);
            tool.AddWall(new Vector3(0f, 0f, 3f), 0f, 6f);          // parallel, 3 m apart
            tool.AddWall(Vector3.Zero, 90f, 6f);                    // perpendicular, shares a corner
            yield return Step.Ticks(1);
            tool.MergeDuplicateWalls();
            T.Check($"parallel-but-separate and perpendicular walls survive ({tool.Walls.Count})",
                    tool.Walls.Count == 3);
        }
    }
}
