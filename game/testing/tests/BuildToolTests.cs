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
}
