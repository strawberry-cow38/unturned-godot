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

            // FOUR walls, not two. With only a front and a back, "flush with the outer wall face" has no
            // answer along X -- there is no wall at those ends to be flush with -- and the old assertion
            // silently pinned the half-thickness the code happened to pad it by. Closing the room makes the
            // expected footprint a fact about the walls rather than about the implementation.
            var o = EditorBuildings.StageOrigin;
            tool.AddWall(o + new Vector3(-6f, 0f, 0f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), 0f, 12f);
            tool.AddWall(o + new Vector3(-6f, 0f, -9f), -90f, 9f);
            tool.AddWall(o + new Vector3(6f, 0f, -9f), -90f, 9f);
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
            //
            // BREAK IT: derive the footprint from the centrelines and add half a thickness. That is what this
            // did, and it was right only while walls stopped at their centreline corners -- once corner
            // solving ran on draw, a solved wall already reached the outer face and the slab hung over every
            // corner by that half-thickness. Bounding the real face corners is right either way.
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

            // The ends carry a gable; the walls along the ridge stay flat-topped.
            //
            // Found by ORIENTATION rather than by asking endA directly. Once the roof overhangs, the gable
            // rides a band stacked on the end wall instead of the end wall itself (the wall's own half-length
            // sets the triangle's slope; the leftover straight bit is the band). Naming endA here would be
            // asserting which OBJECT holds the gable, when what matters is that the end is closed and the
            // sides are not.
            var gabledEnds = new List<WallSurface>();
            int alongRidgeGabled = 0;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Wall || w.GableRise <= 0.01f) continue;
                float yaw = Mathf.Wrap(w.RotationDegrees.Y, 0f, 180f);
                bool runsAlongX = yaw < 45f || yaw > 135f;
                if (runsAlongX) alongRidgeGabled++; else gabledEnds.Add(w);
            }
            T.Check($"both ends across the ridge are gabled ({gabledEnds.Count})", gabledEnds.Count == 2);
            T.Check($"and nothing along the ridge is ({alongRidgeGabled})", alongRidgeGabled == 0);
            if (gabledEnds.Count == 0) yield break;
            var gEnd = gabledEnds[0];

            // the gable end reaches the ridge, whether it does it in one surface or as wall + band
            float endTop = gEnd.Position.Y + gEnd.Height + gEnd.GableRise;
            T.Check($"the gable end meets the ridge ({endTop:0.##} vs {ridgeA.Y:0.##})",
                    Mathf.Abs(endTop - ridgeA.Y) < 5e-2f);
            // and it does so at the ROOF's slope -- a gable steeper than the roof touches only at the apex
            // and leaves a wedge of daylight down both edges, which every count above still passes through.
            float gSlope = gEnd.GableRise / (gEnd.Length * 0.5f);
            T.Check($"at the roof's own slope ({Mathf.RadToDeg(Mathf.Atan(gSlope)):0.#} deg vs {Pitch:0.#})",
                    Mathf.Abs(Mathf.RadToDeg(Mathf.Atan(gSlope)) - Pitch) < 0.5f);

            // A gable's collider must be the TRIANGLE, not a box round it -- a box fills the two wedges of air
            // beside the peak, and you collide with a roof corner that is not there.
            var space = World.GetWorld3D().DirectSpaceState;
            bool Solid(Vector3 at)
            {
                var q = new PhysicsRayQueryParameters3D
                { From = at + new Vector3(0.9f, 0f, 0f), To = at - new Vector3(0.9f, 0f, 0f), CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }
            var peak = gEnd.UVToWorld(gEnd.Length * 0.5f, gEnd.Height + gEnd.GableRise * 0.5f);
            var corner = gEnd.UVToWorld(gEnd.Length * 0.06f, gEnd.Height + gEnd.GableRise * 0.85f);
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
            // WALLS only, and that is not the test being loosened to fit. Height means different things per
            // kind: on a wall it is how tall it stands, on a floor or roof it is how far the slab reaches in
            // plan, so House_00's ground floor is legitimately 21 m "high". Judging those by a wall's range
            // would fail correct data. The frame check for a slab is that it lies flat at the right level,
            // which is what buildtool.imports_floors asserts.
            int sane = 0, tall = 0, judged = 0;
            float maxLen = 0f;
            foreach (var p in plans)
            {
                maxLen = Mathf.Max(maxLen, p.Length);
                if (p.Kind != SurfaceKind.Wall) continue;
                judged++;
                if (p.Height > 1.5f && p.Height < 14f) sane++;
                if (p.Height >= 14f) tall++;
            }
            T.Check($"wall heights are wall-shaped ({sane} of {judged} between 1.5 and 14 m, {tall} absurd)",
                    judged > 0 && sane > judged / 2 && tall == 0);
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
    // Tee and cross junctions, which the endpoint-to-endpoint solver could not see, plus the idempotency
    // that lets the pass run over an import without growing a pilaster on every corner.
    public class BuildToolSolvesTeeAndCrossJunctions : GameTest
    {
        public override string Name => "buildtool.tee_and_cross_junctions";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // ---- TEE: a stem running into the middle of a through wall, stopping 0.5 m short -----------
            // BREAK IT: match endpoints only and the stem never sees the through wall at all, because its
            // end is nowhere near either of that wall's ends. Growth 0, gap stays.
            var through = tool.AddWall(Vector3.Zero, 0f, 12f);              // x 0..12 at z = 0
            var stem = tool.AddWall(new Vector3(6f, 0f, -6f), 270f, 6f);    // runs +Z toward the through wall
            stem.Length = 5.5f; stem.Rebuild();                             // ...and stops 0.5 m short
            yield return Step.Ticks(1);

            tool.SolveCorners();
            yield return Step.Ticks(1);
            T.Check($"the stem of a tee reaches the wall it runs into ({stem.Length:0.00} m, was 5.50)",
                    Mathf.Abs(stem.Length - 6f) < 0.02f);
            // and NOT past it: a tee is not a corner, the through wall's own body fills everything beyond
            T.Check($"but does not punch out the far side ({stem.Length:0.00} vs corner would be "
                    + $"{6f + through.Thickness * 0.5f:0.00})", stem.Length < 6f + 0.05f);
            T.Check("the through wall itself is untouched", Mathf.Abs(through.Length - 12f) < 1e-3f);

            // ---- IDEMPOTENT: the property that makes this safe on an import --------------------------
            // BREAK IT: grow by a relative half-thickness instead of to an absolute target and every call
            // walks the wall further out -- which is exactly the pilaster an imported building grew.
            float lenAfterFirst = stem.Length;
            var second = tool.SolveCorners();
            yield return Step.Ticks(1);
            T.Check($"a second solve changes nothing ({second.Count} walls touched)", second.Count == 0);
            T.Check($"and the stem did not creep ({stem.Length:0.00} vs {lenAfterFirst:0.00})",
                    Mathf.Abs(stem.Length - lenAfterFirst) < 1e-4f);

            // ---- CROSS: four walls all ending at one point --------------------------------------------
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            var arms = new List<WallSurface>();
            for (int k = 0; k < 4; k++)
            {
                float yaw = k * 90f;
                var dir = new Vector3(Mathf.Cos(Mathf.DegToRad(yaw)), 0f, -Mathf.Sin(Mathf.DegToRad(yaw)));
                arms.Add(tool.AddWall(-dir * 6f, yaw, 6f));                  // each ends at the origin
            }
            yield return Step.Ticks(1);
            var lens = new List<float>();
            foreach (var a in arms) lens.Add(a.Length);
            tool.SolveCorners();
            yield return Step.Ticks(1);

            int grew = 0;
            for (int k = 0; k < 4; k++) if (arms[k].Length > lens[k] + 1e-3f) grew++;
            T.Check($"every arm of a cross is carried through the junction ({grew} of 4)", grew == 4);
            foreach (var a in arms)
                T.Check($"...by half a wall, not more ({a.Length - 6f:0.00} m)",
                        a.Length - 6f > 0.1f && a.Length - 6f < 0.45f);
            var third = tool.SolveCorners();
            T.Check($"and a cross is idempotent too ({third.Count})", third.Count == 0);
        }
    }
    // strawberry_cow: "the under roof walls? may be rendered inside out." Derived, this looks true of
    // EVERY imported wall -- but a sign derivation is exactly what burned the roof pitch, so measure it.
    // The surface's own +Z must be the outward normal of the mesh face it was recovered from.
    public class BuildToolImportedWallsFaceOutward : GameTest
    {
        public override string Name => "buildtool.imported_walls_face_outward";

        public override IEnumerable<Step> Run()
        {
            string obj = ProjectSettings.GlobalizePath("res://content/objects/House_00.obj");
            if (!System.IO.File.Exists(obj)) { T.Fail("House_00.obj missing"); yield break; }

            var plans = BuildingImport.FromObj(obj);
            var walls = new List<WallPlan>();
            foreach (var pl in plans) if (pl.Kind == SurfaceKind.Wall) walls.Add(pl);
            T.Check($"there are walls to check ({walls.Count})", walls.Count > 0);
            if (walls.Count == 0) yield break;

            // the building's own centre, so "outward" is a fact about the mesh and not about the importer
            var mesh = ObjMesh.Load(obj);
            var v = (Vector3[])mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
            var xf = EditorObjects.Upright(0f);
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            foreach (var raw in v) { var pw = xf * raw; lo = lo.Min(pw); hi = hi.Max(pw); }
            var centre = new Vector3((lo.X + hi.X) * 0.5f, 0f, (lo.Z + hi.Z) * 0.5f);

            var built = new List<WallSurface>();
            foreach (var pl in walls)
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

            // Judge only the OUTERMOST wall in each of the four directions. My first pass took every wall
            // within a metre of the bounding box, which swept in the inner faces of the double-wall the
            // importer still emits when pairing misses -- and an inner face SHOULD point inward, so the test
            // was demanding the wrong thing of half its sample and could never pass. The extreme wall along
            // each axis is the one case where "outward" is not a judgement call.
            var pick = new WallSurface[4];
            var bestProj = new float[4] { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
            var dirs = new[] { Vector3.Right, Vector3.Left, Vector3.Back, Vector3.Forward };
            foreach (var w in built)
            {
                var mid = w.UVToWorld(w.Length * 0.5f, w.Height * 0.5f);
                for (int d = 0; d < 4; d++)
                {
                    float proj = new Vector3(mid.X - centre.X, 0f, mid.Z - centre.Z).Dot(dirs[d]);
                    if (proj > bestProj[d]) { bestProj[d] = proj; pick[d] = w; }
                }
            }

            int judged = 0, outward = 0;
            for (int d = 0; d < 4; d++)
            {
                var w = pick[d];
                if (w == null) continue;
                judged++;
                var face = w.GlobalTransform.Basis.Z.Normalized();
                float dot = face.Dot(dirs[d]);
                if (dot > 0f) outward++;
                else GD.Print($"[facing] INWARD toward {dirs[d]}: mid={w.UVToWorld(w.Length * 0.5f, w.Height * 0.5f)} "
                              + $"yaw={w.RotationDegrees.Y:0.#} len={w.Length:0.0} dot={dot:0.00}");
            }
            T.Check($"the outermost wall in each direction was found ({judged} of 4)", judged == 4);
            T.Check($"each one's front face points OUT of the building ({outward} of {judged})",
                    outward == judged);
        }
    }
    // "make sure all building tools are ctrl z-able". Three operations rearranged the whole building and
    // pushed nothing: recolouring every wall, merging duplicates, and importing over the stage.
    public class BuildToolWholeStageOperationsUndo : GameTest
    {
        public override string Name => "buildtool.whole_stage_operations_undo";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // ---- recolouring the whole building ------------------------------------------------------
            var a = tool.AddWall(Vector3.Zero, 0f, 6f);
            var b = tool.AddWall(new Vector3(0f, 0f, 6f), 0f, 6f);
            a.MaterialId = 3; b.MaterialId = 3; a.Rebuild(); b.Rebuild();
            yield return Step.Ticks(1);

            tool.CycleMaterial(+1);
            yield return Step.Ticks(1);
            T.Check($"cycling repainted the building ({tool.Walls[0].MaterialId})",
                    tool.Walls[0].MaterialId != 3);
            // BREAK IT: repaint every wall without snapshotting and Ctrl+Z cannot take it back -- a stray
            // scroll recolours the whole building permanently.
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"and undo puts the colour back ({tool.Walls.Count} walls, mat {tool.Walls[0].MaterialId})",
                    tool.Walls.Count == 2 && tool.Walls[0].MaterialId == 3);

            // ---- merging duplicates -------------------------------------------------------------------
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(Vector3.Zero, 0f, 6f);
            tool.AddWall(Vector3.Zero, 0f, 6f);          // the shared edge of two rooms
            yield return Step.Ticks(1);
            int beforeMerge = tool.Walls.Count;
            int depth = ed.UndoDepth;
            tool.MergeDuplicateWalls();
            yield return Step.Ticks(1);
            T.Check($"the duplicate folded ({beforeMerge} -> {tool.Walls.Count})", tool.Walls.Count == 1);
            // MergeDuplicateWalls itself does not push -- the caller wraps it, so drive it the way the
            // editor does and check the step exists AND restores.
            T.Check("a bare merge pushes nothing on its own", ed.UndoDepth == depth);

            // ---- and an import replaces everything ----------------------------------------------------
            string obj = ProjectSettings.GlobalizePath("res://content/objects/House_00.obj");
            if (System.IO.File.Exists(obj))
            {
                foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
                var keep = tool.AddWall(new Vector3(30f, 0f, 30f), 45f, 6f);
                float keptLen = keep.Length;
                yield return Step.Ticks(1);

                int n = tool.ImportRetail("House_00");
                yield return Step.Ticks(1);
                T.Check($"the import replaced the stage ({n} surfaces)", n > 4 && tool.Walls.Count > 4);
                // BREAK IT: the single most destructive button in the panel, and it pushed nothing.
                ed.Undo();
                yield return Step.Ticks(1);
                T.Check($"and undo gives the stage back ({tool.Walls.Count} wall)",
                        tool.Walls.Count == 1 && Mathf.Abs(tool.Walls[0].Length - keptLen) < 1e-3f);
            }
        }
    }
    // "add delete wall tool, drag remove parts of walls."
    public class BuildToolCutsSpansOutOfWalls : GameTest
    {
        public override string Name => "buildtool.cut_span_out_of_wall";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // ---- a bite out of the middle splits the wall in two ------------------------------------
            var w = tool.AddWall(Vector3.Zero, 0f, 12f);
            w.Openings.Add(new WallOpening(1f, 1f, 2f, 2f));      // lives in the left piece
            w.Openings.Add(new WallOpening(9f, 1f, 2f, 2f));      // lives in the right piece
            w.Openings.Add(new WallOpening(5.2f, 1f, 1.5f, 2f));  // straddles the cut
            w.Rebuild();
            var startWorld = w.UVToWorld(0f, 0f);
            yield return Step.Ticks(1);

            T.Check($"cutting the middle makes two walls ({tool.RemoveSpan(w, 5f, 7f)} pieces)",
                    tool.Walls.Count == 2);
            yield return Step.Ticks(1);
            var left = tool.Walls[0];
            var right = tool.Walls[1];
            T.Check($"the left piece stops at the cut ({left.Length:0.00})", Mathf.Abs(left.Length - 5f) < 0.05f);
            T.Check($"the right piece starts after it ({right.Length:0.00})", Mathf.Abs(right.Length - 5f) < 0.05f);
            T.Check($"and it starts where the cut ended ({right.UVToWorld(0f, 0f)})",
                    right.UVToWorld(0f, 0f).DistanceTo(startWorld + new Vector3(7f, 0f, 0f)) < 0.05f);
            T.Check($"the opening in the left piece stayed ({left.Openings.Count})", left.Openings.Count == 1);
            T.Check($"the one in the right piece came with it, re-based "
                    + $"({right.Openings.Count}, u={(right.Openings.Count > 0 ? right.Openings[0].U : -1f):0.0})",
                    right.Openings.Count == 1 && Mathf.Abs(right.Openings[0].U - 2f) < 0.05f);
            // BREAK IT: keep the straddling opening and it survives as a hole hanging off the cut edge,
            // half of it in a wall that no longer exists.
            T.Check("the one straddling the cut was dropped, not clipped",
                    left.Openings.Count + right.Openings.Count == 2);

            // ---- a cut at the end just shortens --------------------------------------------------------
            foreach (var x in new List<WallSurface>(tool.Walls)) tool.RemoveWall(x);
            var e = tool.AddWall(Vector3.Zero, 0f, 12f);
            yield return Step.Ticks(1);
            tool.RemoveSpan(e, 9f, 12f);
            yield return Step.Ticks(1);
            T.Check($"an end cut shortens rather than splits ({tool.Walls.Count} wall, {e.Length:0.0} m)",
                    tool.Walls.Count == 1 && Mathf.Abs(e.Length - 9f) < 0.05f);

            // ---- and a cut over the whole thing removes it ---------------------------------------------
            tool.RemoveSpan(e, -1f, 99f);
            yield return Step.Ticks(1);
            T.Check($"a full-length cut removes the wall ({tool.Walls.Count})", tool.Walls.Count == 0);
        }
    }
    // "make walls painted material-wise per side, not just overall material."
    public class BuildToolPaintsWallsPerSide : GameTest
    {
        public override string Name => "buildtool.per_side_wall_materials";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            var w = tool.AddWall(Vector3.Zero, 0f, 6f);
            w.MaterialId = 0;
            w.Rebuild();
            yield return Step.Ticks(1);

            // one-sided is the default and must stay on the original single-surface path
            T.Check("a plain wall has no back surface", w.GetNodeOrNull<MeshInstance3D>("BackMesh")?.Mesh == null);
            T.Check("and both sides report the same colour", w.Tint.IsEqualApprox(w.BackTint));

            // selecting the BACK face is what makes the picker paint it -- no mode to remember
            tool.SelectSide(w, back: true);
            T.Check("selecting a face reports which side", tool.SelectedBack);
            tool.SetMaterial(w, 7);
            yield return Step.Ticks(1);
            T.Check($"painting the back does not touch the front ({w.MaterialId})", w.MaterialId == 0);
            T.Check($"the back took the new palette ({w.MaterialIdBack})", w.MaterialIdBack == 7);
            var backMesh = w.GetNodeOrNull<MeshInstance3D>("BackMesh");
            T.Check("a back surface now exists", backMesh?.Mesh != null);
            // BREAK IT: leave the -Z faces on the front surface and both sides wear one colour -- the
            // feature silently does nothing while every count and flag still says it worked.
            T.Check($"and the two sides are actually different colours ({w.Tint} vs {w.BackTint})",
                    !w.Tint.IsEqualApprox(w.BackTint));

            // it has to survive being written out and read back, or it is lost the moment you reopen
            var plans = tool.Snapshot();
            string text = WallSave.Write(plans);
            var back = WallSave.Read(text.Split('\n'));
            T.Check($"per-side survives save + load ({back.Count} walls, back mat "
                    + $"{(back.Count > 0 ? back[0].MaterialBack : -99)})",
                    back.Count == 1 && back[0].MaterialBack == 7 && back[0].Material == 0);

            // and undo puts it back
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"undo restores the one-sided wall ({w.MaterialIdBack})", w.MaterialIdBack < 0);
        }
    }
    // "id also like to be able to select openings again once they are placed" was one half; carrying one
    // to a DIFFERENT wall was the other, and it is the last of the editor list.
    public class BuildToolMovesOpeningsBetweenWalls : GameTest
    {
        public override string Name => "buildtool.opening_moves_between_walls";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            var a = tool.AddWall(Vector3.Zero, 0f, 12f);
            var b = tool.AddWall(new Vector3(0f, 0f, 6f), 0f, 12f);
            a.Openings.Add(new WallOpening(2f, 1f, 3f, 2f, 999f, 1));
            a.Rebuild();
            yield return Step.Ticks(1);
            T.Check("it starts on the first wall", a.Openings.Count == 1 && b.Openings.Count == 0);

            T.Check("carrying it across succeeds", tool.ReparentOpening(a, 0, b, 6f, 2f));
            yield return Step.Ticks(1);
            T.Check($"it left the first wall ({a.Openings.Count})", a.Openings.Count == 0);
            T.Check($"and arrived on the second ({b.Openings.Count})", b.Openings.Count == 1);
            var moved = b.Openings[0];
            T.Check($"keeping its size ({moved.Width:0.#}x{moved.Height:0.#})",
                    Mathf.Abs(moved.Width - 3f) < 1e-3f && Mathf.Abs(moved.Height - 2f) < 1e-3f);
            T.Check($"and landing under the cursor ({moved.U + moved.Width * 0.5f:0.#}, wanted 6)",
                    Mathf.Abs(moved.U + moved.Width * 0.5f - 6f) < 0.35f);

            // BREAK IT: drop it wherever the cursor is without checking -- it lands on top of a neighbour
            // and the two holes merge into one ragged gap.
            b.Openings.Add(new WallOpening(0.5f, 1f, 2f, 2f));
            a.Openings.Add(new WallOpening(1f, 1f, 3f, 2f, 999f, 1));
            a.Rebuild(); b.Rebuild();
            yield return Step.Ticks(1);
            T.Check("a hop onto an occupied spot is refused",
                    !tool.ReparentOpening(a, a.Openings.Count - 1, b, 1.5f, 2f));
            T.Check($"and nothing moved ({a.Openings.Count} / {b.Openings.Count})",
                    a.Openings.Count == 1 && b.Openings.Count == 2);

            // an opening that cannot fit the target is refused too
            var tiny = tool.AddWall(new Vector3(0f, 0f, 12f), 0f, 3f);
            tiny.Length = 1.5f; tiny.Rebuild();
            yield return Step.Ticks(1);
            T.Check("and so is one too big for the wall it is dropped on",
                    !tool.ReparentOpening(a, 0, tiny, 0.75f, 2f));
        }
    }
    // The last of the importer's "not read at all" list: floors.
    public class BuildToolImportsFloors : GameTest
    {
        public override string Name => "buildtool.imports_floors";

        public override IEnumerable<Step> Run()
        {
            string obj = ProjectSettings.GlobalizePath("res://content/objects/House_00.obj");
            if (!System.IO.File.Exists(obj)) { T.Fail("House_00.obj missing"); yield break; }

            var plans = BuildingImport.FromObj(obj);
            var floors = new List<WallPlan>();
            foreach (var pl in plans) if (pl.Kind == SurfaceKind.Floor) floors.Add(pl);
            T.Check($"a floor came back ({floors.Count})", floors.Count >= 1);
            if (floors.Count == 0) yield break;

            // Measured off the mesh independently: the only substantial horizontal upward plane is the
            // brown ground slab at y = 0, 270 m2. Everything else up there is a sill or a ledge.
            var f = floors[0];
            T.Check($"it sits at ground level ({f.Y:0.00})", Mathf.Abs(f.Y) < 0.6f);
            T.Check($"and covers the building, not a ledge ({f.Length:0.#} x {f.Height:0.#} m)",
                    f.Length > 10f && f.Height > 10f && f.Length < 30f && f.Height < 30f);
            T.Check($"lying flat ({f.Pitch:0.#} deg)", Mathf.Abs(f.Pitch + 90f) < 1f);

            // BREAK IT: drop the roof-colour gate and the roof planes come back as floors too -- the exact
            // failure the first roof attempt had in the opposite direction, 174 horizontal triangles read as
            // building-sized slabs.
            int roofs = 0;
            foreach (var pl in plans) if (pl.Kind == SurfaceKind.Roof) roofs++;
            T.Check($"and the roofs are still roofs, not floors ({roofs} roof, {floors.Count} floor)",
                    roofs == 4 && floors.Count <= 2);

            // it has to survive the save format like everything else
            var back = WallSave.Read(WallSave.Write(plans).Split('\n'));
            int backFloors = 0;
            foreach (var pl in back) if (pl.Kind == SurfaceKind.Floor) backFloors++;
            T.Check($"floors round-trip through save ({backFloors})", backFloors == floors.Count);
        }
    }
    // "it doesnt undo roofs properly sometimes" -- the drawn roof's undo closed over the _drawingSlab
    // FIELD, which is nulled on release, so Ctrl+Z removed nothing. Auto-fit Add roof captured a local and
    // worked, which is exactly what made it "sometimes".
    public class BuildToolUndoRemovesDrawnSlabs : GameTest
    {
        public override string Name => "buildtool.drawn_slab_undo";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // the auto-fit path, which always worked -- kept as the control
            tool.AddWall(Vector3.Zero, 0f, 9f);
            tool.AddWall(new Vector3(9f, 0f, 0f), 90f, 9f);
            tool.AddWall(new Vector3(9f, 0f, -9f), 180f, 9f);
            tool.AddWall(Vector3.Zero, 270f, 9f);
            yield return Step.Ticks(1);
            int before = tool.Walls.Count;
            var slab = tool.AddSlab(SurfaceKind.Roof);
            yield return Step.Ticks(1);
            T.Check($"auto-fit roof placed ({tool.Walls.Count})", slab != null && tool.Walls.Count == before + 1);
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"and undoes ({tool.Walls.Count})", tool.Walls.Count == before);

            // BREAK IT: capture the field instead of the surface and this is where it shows -- the step
            // fires, removes nothing, and the count never drops. Silent, which is the worst kind.
            var stage = tool.Snapshot();
            var drawn = tool.AddSlab(SurfaceKind.Floor);
            yield return Step.Ticks(1);
            T.Check($"a second slab placed ({tool.Walls.Count})", tool.Walls.Count == before + 1);
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"undo actually removed it, not just fired ({tool.Walls.Count} vs {before})",
                    tool.Walls.Count == before);

            // and a gable drawn over a footprint is ONE undo step, not one per surface it made
            int depth = ed.UndoDepth;
            int made = tool.BuildGableOver(0f, 12f, -12f, 0f, 4.25f, 30f, 0, 0.7f);
            yield return Step.Ticks(1);
            T.Check($"a gable makes several surfaces ({made})", made >= 2);
            T.Check($"but pushes one step ({ed.UndoDepth - depth})", ed.UndoDepth - depth == 1);
            ed.Undo();
            yield return Step.Ticks(1);
            T.Check($"and one Ctrl+Z takes the whole roof back ({tool.Walls.Count} vs {before})",
                    tool.Walls.Count == before);
        }
    }
    // "add q & e to switch between 'floors'" -- and the pitched-roof overhang that landed with it.
    public class BuildToolFloorsAndOverhang : GameTest
    {
        public override string Name => "buildtool.floors_and_roof_overhang";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            T.Check($"starts on the ground floor ({tool.ActiveFloor}, y {tool.FloorY:0.00})",
                    tool.ActiveFloor == 0 && Mathf.Abs(tool.FloorY) < 1e-4f);
            tool.ActiveFloor = 2;
            T.Check($"floor 2 is two storeys up ({tool.FloorY:0.00} vs {2 * EditorBuildings.StoreyHeight:0.00})",
                    Mathf.Abs(tool.FloorY - 2 * EditorBuildings.StoreyHeight) < 1e-3f);
            T.Check("a storey is a door height, so floors stack without a gap",
                    Mathf.Abs(EditorBuildings.StoreyHeight - WallOpenings.DoorHeight) < 1e-4f);
            tool.ActiveFloor = 0;

            // ---- a PITCHED roof overhangs; a FLAT one does not -------------------------------------
            // BREAK IT: apply the overhang in AddSlab and the flat roof grows a ledge, which is the half
            // strawberry corrected me on within a minute of my shipping both.
            foreach (var w in new List<WallSurface>(tool.Walls)) tool.RemoveWall(w);
            tool.AddWall(Vector3.Zero, 0f, 9f);
            tool.AddWall(new Vector3(9f, 0f, 0f), 90f, 9f);
            tool.AddWall(new Vector3(9f, 0f, -9f), 180f, 9f);
            tool.AddWall(Vector3.Zero, 270f, 9f);
            yield return Step.Ticks(1);

            float wallMinX = float.MaxValue, wallMaxX = float.MinValue;
            foreach (var w in tool.Walls)
                foreach (float u in new[] { 0f, w.Length })
                {
                    var p = w.UVToWorld(u, 0f);
                    wallMinX = Mathf.Min(wallMinX, p.X); wallMaxX = Mathf.Max(wallMaxX, p.X);
                }

            tool.ActiveRoofPitch = 0f;
            var flat = tool.AddSlab(SurfaceKind.Roof);
            yield return Step.Ticks(1);
            float flatOver = flat.UVToWorld(flat.Length, 0f).X - wallMaxX;
            T.Check($"a flat roof stays flush ({flatOver:0.00} m past the wall line)", flatOver < 0.45f);
            tool.RemoveWall(flat);

            int n = tool.BuildGableOver(wallMinX - EditorBuildings.RoofOverhang - 0.35f,
                                        wallMaxX + EditorBuildings.RoofOverhang + 0.35f,
                                        -9.35f - EditorBuildings.RoofOverhang, 0.35f + EditorBuildings.RoofOverhang,
                                        4.25f, 20f, 0, 0.7f);
            yield return Step.Ticks(1);
            T.Check($"a pitched roof builds ({n} surfaces)", n >= 2);
            float reach = float.MinValue;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Roof) continue;
                foreach (float u in new[] { 0f, w.Length })
                    reach = Mathf.Max(reach, w.UVToWorld(u, 0f).X);
            }
            T.Check($"and it runs past the walls ({reach - wallMaxX:0.00} m past, want ~{EditorBuildings.RoofOverhang:0.00}+)",
                    reach - wallMaxX > EditorBuildings.RoofOverhang * 0.8f);
        }
    }

    // ---- glazing ------------------------------------------------------------------------------------
    // strawberry_cow: "implement nyatools' glass window fill for window pane openings, complete with plenty
    // of options. color hue, mark indestructable, set hp, etc. toggleable on/off so not every opening is a
    // window."

    public class BuildToolGlassSurvivesEditing : GameTest
    {
        public override string Name => "buildtool.glass_survives_editing";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            var w = tool.AddWall(Vector3.Zero, 0f, 12f);
            yield return Step.Ticks(1);

            tool.ActiveGlassTint = 0x8FBFA0;
            tool.ActiveGlassHp = 7f;
            tool.ActiveGlassIndestructible = true;
            int i = tool.AddOpening(w, 3f, WallOpenings.WindowSill + 1f, 1);   // archetype 1 = window, glazed by preset
            yield return Step.Ticks(1);
            T.Check($"an opening was placed ({i})", i >= 0);
            T.Check("the window preset glazes itself", w.Openings[i].Glazed);
            T.Check($"and carries the panel's options ({w.Openings[i].GlassHp:0})",
                    w.Openings[i].GlassTint == 0x8FBFA0 && w.Openings[i].GlassHp == 7f
                    && w.Openings[i].GlassIndestructible);

            // THE TRAP. WallOpening is a STRUCT and its constructor takes 4 of its 11 fields, so anything that
            // rebuilds one through `new WallOpening(...)` drops the rest. Clamp did exactly that, and Clamp
            // runs on every drag -- so a window would arrive at its destination unglazed, with no error, no
            // exception and every position assertion still passing. MovedTo copies the struct instead.
            tool.MoveOpening(w, i, 8f, WallOpenings.WindowSill + 1f, 0.1f);
            yield return Step.Ticks(1);
            var moved = w.Openings[i];
            T.Check($"the drag moved it (u {moved.U:0.0})", moved.U > 5f);
            T.Check("and the glass came with it", moved.Glazed && moved.GlassTint == 0x8FBFA0);
            T.Check($"including hp and indestructible ({moved.GlassHp:0})",
                    moved.GlassHp == 7f && moved.GlassIndestructible);

            // BREAK IT: put `new WallOpening(u, v, w, h, o.Depth, o.Archetype)` back in WallOpenings.Clamp and
            // every glass field above reads as its default the moment the window is nudged.

            var unglazed = tool.AddOpening(w, 1.5f, 0f, 0);       // archetype 0 = door
            yield return Step.Ticks(1);
            T.Check("a door is not glazed by default", !w.Openings[unglazed].Glazed);
        }
    }

    public class BuildToolGlassPanes : GameTest
    {
        public override string Name => "buildtool.glass_panes_fill_and_shatter";

        static int PaneCount(WallSurface w)
        {
            int n = 0;
            foreach (var c in w.GetChildren()) if (c is GlassPane p && GodotObject.IsInstanceValid(p)) n++;
            return n;
        }

        static GlassPane FirstPane(WallSurface w)
        {
            foreach (var c in w.GetChildren()) if (c is GlassPane p && GodotObject.IsInstanceValid(p)) return p;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var w = new WallSurface { Length = 12f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            var o = new WallOpening(3f, 1f, 3f, 2.75f) { Glazed = true, GlassHp = 3f };
            w.Openings.Add(o);
            w.Rebuild();
            yield return Step.Ticks(1);

            T.Check($"a glazed opening gets exactly one pane ({PaneCount(w)})", PaneCount(w) == 1);
            var pane = FirstPane(w);
            T.Check($"centred in the hole ({pane?.Position})",
                    pane != null && pane.Position.IsEqualApprox(new Vector3(4.5f, 2.375f, 0f)));
            // Assert the pane's actual GEOMETRY, not its transform. Position alone passes for a pane of any
            // size, so a half-height pane would sit perfectly centred in the opening with a gap top and
            // bottom and this test would still be green -- and a gap round the glass is precisely what you
            // would go looking for in a render.
            Vector3 box = default;
            foreach (var c in pane.GetChildren())
                if (c is MeshInstance3D mi && mi.Mesh is BoxMesh bm) box = bm.Size;
            T.Check($"and fills it edge to edge ({box.X:0.00} x {box.Y:0.00}, want 3.00 x 2.75)",
                    Mathf.Abs(box.X - 3f) < 0.001f && Mathf.Abs(box.Y - 2.75f) < 0.001f);
            T.Check($"as a thin sheet ({box.Z:0.00})", box.Z > 0f && box.Z < 0.1f);

            // Rebuild runs on every mouse move during a drag. If it respawned panes each time, a drag would
            // leave a trail of them -- and QueueFree defers, so the stale ones are still hittable that frame.
            w.Rebuild(); w.Rebuild(); w.Rebuild();
            yield return Step.Ticks(1);
            T.Check($"and rebuilding does not stack up more ({PaneCount(w)})", PaneCount(w) == 1);

            // hp is honoured: 3 hp does not fall to one point of damage
            pane.TakeDamage(1f);
            yield return Step.Ticks(1);
            T.Check($"a 3 hp pane survives 1 damage ({PaneCount(w)})", PaneCount(w) == 1);

            pane.TakeDamage(5f);
            yield return Step.Ticks(2);
            T.Check("shooting it out marks the opening broken", w.Openings[0].GlassBroken);
            T.Check("and a broken opening is no longer glass-filled", !w.Openings[0].HasGlass);
            w.Rebuild();
            yield return Step.Ticks(2);
            T.Check($"so a rebuild leaves the hole empty ({PaneCount(w)})", PaneCount(w) == 0);

            // BREAK IT: drop the GlassBroken write in MarkPaneBroken and the window repairs itself on the
            // next Rebuild -- you shoot it, it comes back, and nothing anywhere reports a problem.

            // indestructible: Build stored the flag and NOTHING read it until this test existed, so a pane
            // marked unbreakable shattered on the first shot while the editor checkbox looked wired.
            var w2 = new WallSurface { Length = 8f, Height = WallOpenings.DoorHeight };
            World.AddChild(w2);
            w2.Openings.Add(new WallOpening(2f, 1f, 3f, 2.75f) { Glazed = true, GlassIndestructible = true });
            w2.Rebuild();
            yield return Step.Ticks(1);
            var tough = FirstPane(w2);
            T.Check("an indestructible opening still gets a pane", tough != null);
            tough.TakeDamage(9999f);
            yield return Step.Ticks(2);
            T.Check($"and no amount of damage breaks it ({PaneCount(w2)})", PaneCount(w2) == 1);
            T.Check("nor does it mark itself broken", !w2.Openings[0].GlassBroken);
        }
    }

    public class BuildToolGlassPersists : GameTest
    {
        public override string Name => "buildtool.glass_survives_save_and_load";

        public override IEnumerable<Step> Run()
        {
            var plan = new WallPlan
            {
                X = 0f, Y = 0f, Z = 0f, Yaw = 0f, Length = 12f, Thickness = 0.7f,
                Material = 0, Height = WallOpenings.DoorHeight, Kind = SurfaceKind.Wall,
            };
            plan.Openings.Add(new WallOpening(3f, 1f, 3f, 2.75f)
            { Glazed = true, GlassTint = 0x6A9BC8, GlassHp = 5f, GlassIndestructible = true });
            plan.Openings.Add(new WallOpening(8f, 1f, 2f, 2f) { Glazed = true, GlassBroken = true });
            plan.Openings.Add(new WallOpening(0.5f, 0f, 2f, 3.75f));      // a plain door, no glazing

            string text = WallSave.Write(new List<WallPlan> { plan });
            var back = WallSave.Read(text.Split('\n'));
            yield return Step.Ticks(1);

            T.Check($"one wall read back ({back.Count})", back.Count == 1);
            T.Check($"with its three openings ({back[0].Openings.Count})", back[0].Openings.Count == 3);
            var a = back[0].Openings[0];
            T.Check($"tint survives ({a.GlassTint:X})", a.Glazed && a.GlassTint == 0x6A9BC8);
            T.Check($"hp survives ({a.GlassHp:0})", a.GlassHp == 5f);
            T.Check("indestructible survives", a.GlassIndestructible);
            // A window that was smashed has to come back smashed. Otherwise every save/load quietly repairs
            // the building, which looks like the save worked right up until you notice the glass is back.
            T.Check("a broken window loads back broken", back[0].Openings[1].GlassBroken);
            T.Check("and is still not filled", !back[0].Openings[1].HasGlass);
            T.Check("an unglazed opening stays unglazed", !back[0].Openings[2].Glazed);

            // Glazing is trailing and optional, so a building with no windows writes exactly what it always
            // did -- a format change that rewrites every existing save is a format change that can corrupt
            // one. The door line is the control.
            string doorLine = null;
            foreach (var l in text.Split('\n'))
                if (l.TrimStart().StartsWith("open 0.5")) doorLine = l.Trim();
            T.Check($"an unglazed opening writes the old 7 tokens ({doorLine})",
                    doorLine != null && doorLine.Split(' ').Length == 7);

            // and a file written before glass existed still loads
            var old = WallSave.Read(new[]
            {
                "wall 0 0 0 0 12 0.7 0 4.25 0 Wall 0 -1",
                "  open 3 1 3 2.75 999 1",
            });
            T.Check($"a pre-glass save still loads ({old.Count} wall)", old.Count == 1 && old[0].Openings.Count == 1);
            T.Check("as unglazed", !old[0].Openings[0].Glazed);
        }
    }

    public class BuildToolGlassUndo : GameTest
    {
        public override string Name => "buildtool.glass_edits_undo";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            var w = tool.AddWall(Vector3.Zero, 0f, 12f);
            int i = tool.AddOpening(w, 3f, WallOpenings.WindowSill + 1f, 1);
            yield return Step.Ticks(1);
            T.Check("placed glazed", w.Openings[i].Glazed);

            int depth = ed.UndoDepth;
            tool.ToggleGlass(w, i);
            yield return Step.Ticks(1);
            T.Check("toggling takes the glass out", !w.Openings[i].Glazed);
            T.Check($"and pushes exactly one step ({ed.UndoDepth - depth})", ed.UndoDepth - depth == 1);

            ed.Undo();
            yield return Step.Ticks(1);
            T.Check("Ctrl+Z puts it back", w.Openings[i].Glazed);

            // A no-op must not consume a step: an undo that fires and changes nothing is the failure mode
            // this editor has already shipped twice, and it reads as "Ctrl+Z is broken".
            depth = ed.UndoDepth;
            tool.SetOpeningGlass(w, i, glazed: true);
            yield return Step.Ticks(1);
            T.Check($"setting glass to what it already was pushes nothing ({ed.UndoDepth - depth})",
                    ed.UndoDepth == depth);

            // Re-glazing a smashed window has to un-smash it, or "turn it off and on again" leaves an opening
            // that claims to be glazed and shows nothing.
            tool.SetOpeningGlass(w, i, broken: true);
            yield return Step.Ticks(1);
            T.Check("marked broken", w.Openings[i].GlassBroken);
            tool.SetOpeningGlass(w, i, glazed: false);
            tool.SetOpeningGlass(w, i, glazed: true);
            yield return Step.Ticks(1);
            T.Check("re-glazing clears the break", w.Openings[i].Glazed && !w.Openings[i].GlassBroken);
        }
    }

    public class BuildToolDrawnRoofOverhang : GameTest
    {
        public override string Name => "buildtool.drawn_roof_overhangs_and_meets_its_gable";

        public override IEnumerable<Step> Run()
        {
            var tool = new EditorBuildings();
            World.AddChild(tool);
            var ed = new Editor();
            World.AddChild(ed);
            tool.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // a closed room: two walls along X, two across (the across ones become the gable ends)
            const float L = 12f, D = 9f;
            tool.AddWall(new Vector3(-L / 2f, 0f, 0f), 0f, L);
            tool.AddWall(new Vector3(-L / 2f, 0f, -D), 0f, L);
            var endA = tool.AddWall(new Vector3(-L / 2f, 0f, -D), -90f, D);
            var endB = tool.AddWall(new Vector3(L / 2f, 0f, -D), -90f, D);
            yield return Step.Ticks(1);

            const float Pitch = 20f;
            float wallTop = endA.Position.Y + endA.Height;
            // drawn EXACTLY on the walls -- the tool is what adds the overhang, which is the report
            float oh = WallOpenings.DefaultThickness * 0.5f + EditorBuildings.RoofOverhang;
            tool.BuildGableOver(-L / 2f - oh, L / 2f + oh, -D - oh, 0f + oh, wallTop, Pitch,
                                0, WallOpenings.DefaultThickness);
            yield return Step.Ticks(1);

            float roofMaxX = float.MinValue, roofMinX = float.MaxValue;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Roof) continue;
                foreach (float u in new[] { 0f, w.Length })
                {
                    var p = w.UVToWorld(u, 0f);
                    roofMaxX = Mathf.Max(roofMaxX, p.X); roofMinX = Mathf.Min(roofMinX, p.X);
                }
            }
            T.Check($"the roof runs past the wall line ({roofMaxX - L / 2f:0.00} m, want ~{oh:0.00})",
                    roofMaxX - L / 2f > EditorBuildings.RoofOverhang * 0.8f);

            // THE REAL CHECK. A gable triangle whose rise comes from the ROOF FOOTPRINT instead of its own
            // wall is steeper than the roof above it: it touches at the apex and opens a wedge of daylight
            // down both sloped edges. Every count, every position and the overhang check above all pass
            // while that is happening, so the only thing that catches it is comparing the two SLOPES.
            float want = Mathf.Tan(Mathf.DegToRad(Pitch));
            float steepest = 0f;
            int gabled = 0;
            foreach (var w in tool.Walls)
            {
                if (w.Kind != SurfaceKind.Wall || w.GableRise <= WallOpenings.Eps) continue;
                gabled++;
                steepest = Mathf.Max(steepest, w.GableRise / (w.Length * 0.5f));
            }
            T.Check($"both ends are gabled ({gabled})", gabled == 2);
            T.Check($"and the gable slope matches the roof ({steepest:0.000} vs {want:0.000})",
                    Mathf.Abs(steepest - want) < 0.01f);

            // BREAK IT: go back to GableRise = rise, taken from the footprint's half-span. The footprint is
            // wider than the wall by exactly the overhang, so the ratio comes out ABOVE tan(pitch) and this
            // check fails -- while the overhang check above, the surface counts and every position stay
            // green, which is how it survived until someone looked at the building.

            // the straight band that fills between the wall top and the now-shallower triangle
            float bandTop = float.MinValue;
            foreach (var w in tool.Walls)
                if (w.Kind == SurfaceKind.Wall && w.GableRise > WallOpenings.Eps)
                    bandTop = Mathf.Max(bandTop, w.Position.Y + w.Height + w.GableRise);
            float ridge = wallTop + (Mathf.Min(L + 2f * oh, D + 2f * oh) * 0.5f) * want;
            T.Check($"and the gable still reaches the ridge ({bandTop:0.00} vs {ridge:0.00})",
                    Mathf.Abs(bandTop - ridge) < 0.05f);
        }
    }

    // strawberry_cow 2026-08-09: "what i want is to have these doors as things i can enable on relevant
    // openings". Same ownership as the glass fill -- the OPENING carries the door, the wall materialises it.
    public class BuildToolOpeningDoors : GameTest
    {
        public override string Name => "buildtool.opening_can_hold_a_door";

        static int DoorCount(WallSurface w)
        {
            int n = 0;
            foreach (var c in w.GetChildren())
                if (c is Node3D h && h is not MeshInstance3D && h is not StaticBody3D && h is not GlassPane)
                    foreach (var g in h.GetChildren()) if (g is ObjectDoor) { n++; break; }
            return n;
        }
        static Node3D HostOf(WallSurface w)
        {
            foreach (var c in w.GetChildren())
                if (c is Node3D h)
                    foreach (var g in h.GetChildren()) if (g is ObjectDoor) return h;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var w = new WallSurface { Length = 12f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            var o = new WallOpening(3f, 0f, 2.5f, 3.75f) { DoorProp = "Door_Pine" };
            w.Openings.Add(o);
            w.Rebuild();
            yield return Step.Ticks(2);

            T.Check($"a doored opening gets exactly one door ({DoorCount(w)})", DoorCount(w) == 1);
            var host = HostOf(w);
            if (host == null) yield break;

            // IN the hole -- asserted on the door's GEOMETRY further down, not on the host's transform. The
            // host is deliberately offset from the opening centre to compensate for the leaf not being
            // centred on its own origin, so "host.Position.X == opening centre" is a check on the mechanism
            // that FAILS when the behaviour is right. Where the door actually is, is the claim worth making.

            // SIZED to the hole. The Door_Pine leaf is natively 2.45 x 2.80 and this hole is 2.5 x 3.75, so an
            // unscaled door is visibly short -- and "a door exists at the right place" passes either way.
            float lo = float.MaxValue, hi = float.MinValue, wl = float.MaxValue, wr = float.MinValue;
            foreach (var g in host.GetChildren())
                if (g is ObjectDoor d)
                    foreach (var pv in d.GetChildren())
                        if (pv is Node3D piv)
                            foreach (var c in piv.GetChildren())
                                if (c is MeshInstance3D mi && mi.Mesh != null)
                                {
                                    var ab = mi.Mesh.GetAabb();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        var pw = mi.GlobalTransform * ab.GetEndpoint(i);
                                        lo = Mathf.Min(lo, pw.Y); hi = Mathf.Max(hi, pw.Y);
                                        wl = Mathf.Min(wl, pw.X); wr = Mathf.Max(wr, pw.X);
                                    }
                                }
            T.Check($"scaled to the hole's height ({hi - lo:0.00} m vs the opening's {o.Height:0.00})",
                    Mathf.Abs((hi - lo) - o.Height) < 0.25f);
            T.Check($"and its width ({wr - wl:0.00} m vs {o.Width:0.00})",
                    Mathf.Abs((wr - wl) - o.Width) < 0.06f);
            T.Check($"sitting on the opening's sill, not the wall base (y {lo:0.00}, sill {o.V:0.00})",
                    Mathf.Abs(lo - o.V) < 0.25f);
            // CENTRED in the hole. The leaf is not centred on its own origin -- Door_Pine's geometry sits at
            // x -2.35..+0.10 about the hinge -- so a door positioned by its host lands a metre off and hangs
            // half inside the wall beside the opening. Asserting the door's WIDTH and the HOST's position both
            // pass while that is happening; only the leaf's own centre catches it.
            T.Check($"centred on the opening ({(wl + wr) * 0.5f:0.00} vs {o.U + o.Width * 0.5f:0.00})",
                    Mathf.Abs((wl + wr) * 0.5f - (o.U + o.Width * 0.5f)) < 0.06f);

            // reuse, not respawn: Rebuild runs every frame of a drag
            w.Rebuild(); w.Rebuild();
            yield return Step.Ticks(1);
            T.Check($"rebuilding does not stack up more ({DoorCount(w)})", DoorCount(w) == 1);

            // and taking the door off removes it
            var off = w.Openings[0]; off.DoorProp = null; w.Openings[0] = off;
            w.Rebuild();
            yield return Step.Ticks(2);
            T.Check($"clearing the door empties the hole ({DoorCount(w)})", DoorCount(w) == 0);
        }
    }

    public class BuildToolOpeningDoorPersists : GameTest
    {
        public override string Name => "buildtool.opening_door_survives_save";

        public override IEnumerable<Step> Run()
        {
            var plan = new WallPlan { Length = 12f, Thickness = 0.7f, Height = WallOpenings.DoorHeight };
            plan.Openings.Add(new WallOpening(3f, 0f, 2.5f, 3.75f) { DoorProp = "Door_Pine", DoorOpen = true });
            plan.Openings.Add(new WallOpening(8f, 1f, 3f, 2.75f) { Glazed = true, GlassTint = 0x6A9BC8 });
            plan.Openings.Add(new WallOpening(0.5f, 0f, 2f, 3f));

            string text = WallSave.Write(new List<WallPlan> { plan });
            var back = WallSave.Read(text.Split('\n'));
            yield return Step.Ticks(1);

            T.Check($"one wall, three openings ({back.Count}/{back[0].Openings.Count})",
                    back.Count == 1 && back[0].Openings.Count == 3);
            T.Check($"the door survives ('{back[0].Openings[0].DoorProp}')", back[0].Openings[0].DoorProp == "Door_Pine");
            T.Check("open/shut survives with it", back[0].Openings[0].DoorOpen);
            // the door tokens trail the GLAZING block, so a doored-but-unglazed opening still has to write the
            // glazing fields or the door name lands in the "glazed" slot
            T.Check("a doored opening is not accidentally glazed", !back[0].Openings[0].Glazed);
            T.Check($"glass still round-trips beside it ({back[0].Openings[1].GlassTint:X})",
                    back[0].Openings[1].Glazed && back[0].Openings[1].GlassTint == 0x6A9BC8);
            T.Check("and a plain opening stays plain",
                    !back[0].Openings[2].HasDoor && !back[0].Openings[2].Glazed);
        }
    }

    /// <summary>Every opening archetype that gets FILLED must be sized to the thing that fills it.
    ///
    /// The doorway was DoorHeight - 0.5 = 3.75, and DoorHeight is misnamed -- it is retail WALL_HEIGHT, which
    /// StoreyHeight and WallSurface.Height also read. So the default doorway was "a wall, less half a metre",
    /// 0.95 m taller than any door, and PlaceDoor dutifully stretched the leaf ~34% to fill it.
    ///
    /// The first version of this test checked archetype[0] and reported the sizing verified. The GARAGE had the
    /// same bug at the same time -- an 8.0 m opening for a 4.00 m gate -- and sailed through, because a test
    /// that checks one member of a family and calls the family covered is the same mistake as the bug it is
    /// chasing. Hence the loop and the explicit NoFill list.
    ///
    /// Measured against the real meshes rather than a second copy of the numbers, so re-extracting a prop moves
    /// the test with it. Asserting the doorway is "about 2.85" would pass by construction and prove nothing;
    /// the claim worth making is that it FITS THE ASSET.</summary>
    public class OpeningArchetypesFitTheirFills : GameTest
    {
        public override string Name => "buildtool.opening_archetypes_fit_their_fills";
        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        // archetype name -> the prop that fills it. Gate IS the garage door: wooden_door_anims.txt gives it the
        // hinge axis (1,0,0), an X-tilt -- a door that lifts rather than swings.
        static readonly (string Arch, string Prop)[] Fills =
        {
            ("door",   "Door_Pine"),
            ("garage", "Gate_Pine"),
        };

        // Openings that are holes on purpose. A porch is an open bay; the window family takes glass, which is
        // generated TO the opening instead of being a fixed-size prop.
        static readonly string[] NoFill = { "porch", "window", "tall win", "vent" };

        const float Slack = 0.20f;        // generous -- the doorway missed by 0.95 and the garage by 3.95
        const float MaxStretch = 1.08f;

        public override IEnumerable<Step> Run()
        {
            foreach (var (arch, prop) in Fills)
            {
                int ai = System.Array.FindIndex(EditorBuildings.Archetypes, x => x.Name == arch);
                if (ai < 0) { T.Check($"archetype '{arch}' exists", false); continue; }
                var a = EditorBuildings.Archetypes[ai];

                var mesh = ObjMesh.Load(Dir + prop + ".obj");
                if (mesh == null) { T.Check($"{prop}.obj loads", false); continue; }
                // These rips carry width on X and height on +Z (stood up by 270 about X when hung).
                var ab = mesh.GetAabb();
                float leafW = ab.Size.X, leafH = ab.Size.Z;

                T.Check($"{arch}: wide enough for {prop} ({a.Width:0.00} >= {leafW:0.00})", a.Width >= leafW - 0.01f);
                T.Check($"{arch}: not gaping beside it (slack {a.Width - leafW:0.00})", a.Width - leafW <= Slack);
                T.Check($"{arch}: tall enough ({a.Height:0.00} >= {leafH:0.00})", a.Height >= leafH - 0.01f);
                T.Check($"{arch}: not gaping above it (slack {a.Height - leafH:0.00})", a.Height - leafH <= Slack);

                // The consequence, stated directly. PlaceDoor scales the fill to whatever hole it is given, so
                // an oversized archetype is invisible to any check that only looks at the fill -- it obediently
                // fills the frame at any size. The RATIO is the only place it shows.
                T.Check($"{arch}: a default fill is not stretched ({a.Width / leafW:0.00}x wide, {a.Height / leafH:0.00}x tall)",
                        a.Width / leafW <= MaxStretch && a.Height / leafH <= MaxStretch);
            }

            // Completeness. This is the guard the first version of this test lacked: it checked archetype[0],
            // reported the sizing verified, and the garage was 2x its own gate the whole time it passed. The
            // garage was not knowingly exempt -- it was simply not looked at. An archetype in neither list now
            // fails here rather than shipping unsized.
            foreach (var a in EditorBuildings.Archetypes)
            {
                bool filled = System.Array.Exists(Fills, f => f.Arch == a.Name);
                bool exempt = System.Array.IndexOf(NoFill, a.Name) >= 0;
                T.Check($"archetype '{a.Name}' is either fitted to a fill or listed as fill-less", filled ^ exempt);
            }
            yield break;
        }
    }

    /// <summary>The palette's 3D preview must SPIN a prop, not swing it around the room.
    ///
    /// Prop geometry is not centred on its own origin -- Door_Pine's sits at x -2.35..+0.10, so its bulk is
    /// about 1.1 m off the node it hangs from. Rotating the mesh node therefore orbits the prop instead of
    /// turning it, and it leaves frame. Everything else about that render is correct: right mesh, right
    /// material, right size, right camera. Only the distance from the pivot shows it.
    ///
    /// Checked at rest AND after turning. Those catch different things: at rest catches the recentre being
    /// missing altogether (which is the bug that was here -- it reads 1.125 m, Door_Pine's whole offset), and
    /// the rotations catch the subtler version where the prop is recentred but the turntable turns about some
    /// other point, which is invisible until something actually moves.</summary>
    public class PalettePreviewSpinsInPlace : GameTest
    {
        public override string Name => "editor.palette_preview_spins_in_place";
        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        public override IEnumerable<Step> Run()
        {
            // Deliberately the WORST case in the catalog for this: a door leaf, anchored at its hinge.
            var mesh = ObjMesh.Load(Dir + "Door_Pine.obj");
            if (mesh == null) { T.Check("Door_Pine.obj loads", false); yield break; }
            float off = mesh.GetAabb().GetCenter().Length();
            T.Check($"Door_Pine is genuinely off its own origin ({off:0.00} m) -- else this proves nothing", off > 0.5f);

            var prev = new EditorPropPreview(_ => mesh, _ => new StandardMaterial3D());
            World.AddChild(prev);
            yield return Step.Ticks(1);

            prev.ShowOnStage("Door_Pine");
            yield return Step.Ticks(1);

            var at0 = prev.StageCentreForTest();
            T.Check($"at rest the prop sits on the pivot ({at0.Length():0.000} m)", at0.Length() < 0.02f);

            // A quarter turn and a half turn. An orbiting prop swings out by ~its own offset; a spinning one
            // does not move at all. Both angles, because a half turn alone would also pass for a prop mirrored
            // through the pivot.
            foreach (float deg in new[] { 90f, 180f })
            {
                prev.SpinToForTest(Mathf.DegToRad(deg));
                yield return Step.Ticks(1);
                var c = prev.StageCentreForTest();
                T.Check($"still on the pivot after {deg:0}° ({c.Length():0.000} m, was {off:0.00} off-origin)",
                        c.Length() < 0.02f);
            }

            prev.QueueFree();
        }
    }

    /// <summary>A door's hinge must lie ON its own leaf.
    ///
    /// The wooden anim rows were authored against a leaf centred on its origin; the rips are not. Door_Pine
    /// spans x -2.35..+0.10 while its row says the hinge is at +1.125 -- the exact negation of the mesh's
    /// centre, and a full metre clear of any geometry -- so the door swung around a point out in mid-air
    /// beside itself.
    ///
    /// The check is "the pivot is inside the leaf's own bounds", not "the pivot equals 0.0". Asserting the
    /// corrected NUMBER would just restate the fix and would still pass if a future re-extract moved the mesh
    /// again; asserting the RELATION is what actually has to hold, and it holds for every door.
    ///
    /// Container doors are in here too as the control: their rows already agree with their meshes, so they
    /// must pass WITHOUT any rebasing -- which is what makes the rebase provably specific to the wooden path
    /// rather than something applied everywhere until the symptom went away.</summary>
    public class DoorHingesLieOnTheirLeaf : GameTest
    {
        public override string Name => "door.hinge_lies_on_its_leaf";
        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        static bool Inside(Aabb b, Vector3 p, float slack)
        {
            var lo = b.Position - Vector3.One * slack;
            var hi = b.Position + b.Size + Vector3.One * slack;
            return p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z;
        }

        public override IEnumerable<Step> Run()
        {
            // WOODEN: the path that was broken. Ask DoorDeploy for the catalogue entry it will actually use.
            foreach (var prop in new[] { "Door_Pine", "Door_Birch", "Door_Maple", "Door_Metal" })
            {
                var entries = DoorDeploy.WoodenLeavesForTest(prop, Dir);
                if (entries == null || entries.Count == 0) { T.Check($"{prop}: has a hinge row", false); continue; }
                var mesh = ObjMesh.Load(Dir + entries[0].MeshFile);
                if (mesh == null) { T.Check($"{prop}: leaf loads", false); continue; }
                var b = mesh.GetAabb();
                var pv = entries[0].Pivot;
                T.Check($"{prop}: hinge {pv.X:0.00} lies on the leaf (x {b.Position.X:0.00}..{b.Position.X + b.Size.X:0.00})",
                        Inside(b, pv, 0.15f));
                T.Check($"{prop}: and near an EDGE, not the middle (|{pv.X:0.00}| vs half-width {b.Size.X * 0.5f:0.00})",
                        Mathf.Abs(pv.X - b.GetCenter().X) > b.Size.X * 0.25f);
                T.Check($"{prop}: swings quicker than retail's 0.6333 ({entries[0].DurationSec:0.000}s)",
                        entries[0].DurationSec < 0.55f);
            }

            // CONTAINER control: untouched by the rebase, and already correct.
            foreach (var (file, px) in new[]
            {
                ("Fridge_0_door.obj", 0.613031f),
                ("Oven_0_door.obj", 0.5f),
                ("Wardrobe_0_Left_Hinge_0_door.obj", -0.972499f),
            })
            {
                var m = ObjMesh.Load(Dir + file);
                if (m == null) { T.Check($"{file} loads", false); continue; }
                var b = m.GetAabb();
                T.Check($"control {file}: its row already sits on its mesh ({px:0.00} in {b.Position.X:0.00}..{b.Position.X + b.Size.X:0.00})",
                        px >= b.Position.X - 0.05f && px <= b.Position.X + b.Size.X + 0.05f);
            }
            yield break;
        }
    }
}
