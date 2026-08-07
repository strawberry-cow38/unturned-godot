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
            b.Rebuild();
            yield return Step.Ticks(1);

            T.Check("saved both walls", tool.Save() == 2);
            T.Check("the file exists", System.IO.File.Exists(Path));

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
                    var box = (w.GlobalTransform * mi.Mesh.GetAabb()).Position - o;
                    var full = new Aabb(box, mi.Mesh.GetAabb().Size);
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
}
