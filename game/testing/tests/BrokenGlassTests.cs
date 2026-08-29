using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // strawberry_cow: "broken glass preset, places the glass shard props in the corners of an opening."
    //
    // The state this renders was ALREADY THERE: WallOpening.GlassBroken is written when a pane shatters and
    // survives save, load and rebuild, and HasGlass is `Glazed && !GlassBroken`. Until now it rendered as an
    // empty hole, so a shot-out window and a hole that was never glazed looked identical. These tests are
    // therefore mostly about the join: that authoring and shooting converge, that nothing new is persisted,
    // and that a remnant is scenery rather than a thing that blocks the hole it is in.
    static class Glass
    {
        public static EditorBuildings Rig(GameTest t, out Editor ed)
        {
            ed = new Editor(); t.World.AddChild(ed);
            var eb = new EditorBuildings(); t.World.AddChild(eb);
            eb.Setup(ed, null, null);
            eb.RestoreAll(new List<WallPlan>());
            return eb;
        }

        /// <summary>The shard node a wall is showing for its openings, or null.</summary>
        public static Node3D ShardsOn(WallSurface w)
        {
            foreach (var c in w.GetChildren())
                if (c is Node3D n && n.Name.ToString().StartsWith("GlassShards_")) return n;
            return null;
        }

        /// <summary>Which shard MESH sits in each corner, in order, as a vertex-count fingerprint.
        /// Glass_0 and Glass_1 have different vertex counts, so this identifies the arrangement without
        /// needing the meshes themselves -- which is what "the seed is stable" is a claim about.</summary>
        public static string Signature(WallSurface w)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in w.GetChildren())
            {
                if (c is not Node3D g || !g.Name.ToString().StartsWith("GlassShards_")) continue;
                sb.Append(g.Name).Append(':');
                foreach (var k in g.GetChildren())
                    if (k is MeshInstance3D mi && mi.Mesh != null)
                        sb.Append(mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length).Append(',');
                sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>Every shard node's name, so a collision that makes two of them indistinguishable is
        /// visible. Godot silently replaces a colliding name, so "they all got named" is not the same
        /// claim as "they are each named for their opening".</summary>
        public static List<string> ShardNodeNames(WallSurface w)
        {
            var names = new List<string>();
            foreach (var c in w.GetChildren())
                if (c is Node3D g && g.Name.ToString().StartsWith("GlassShards_")) names.Add(g.Name);
            return names;
        }

        public static int ShardCount(WallSurface w)
        {
            int n = 0;
            foreach (var c in w.GetChildren())
                if (c is Node3D g && g.Name.ToString().StartsWith("GlassShards_")) n += g.GetChildCount();
            return n;
        }
    }

    public class BrokenGlassLeavesShardsInTheOpening : GameTest
    {
        public override string Name => "buildtool.broken_glass_leaves_shards";

        public override IEnumerable<Step> Run()
        {
            var eb = Glass.Rig(this, out var ed);
            yield return Step.Ticks(1);

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            int i = eb.AddOpening(w, 3f, 1f, 1);                 // a window
            eb.SetOpeningGlass(w, i, glazed: true);
            yield return Step.Ticks(1);
            T.Check("the opening is glazed", w.Openings[i].Glazed);
            T.Check("and not broken yet", !w.Openings[i].GlassBroken);
            T.Check($"so it has no shards ({Glass.ShardCount(w)})", Glass.ShardCount(w) == 0);

            // BREAK IT: never call RebuildShards from Rebuild -> a smashed window stays an empty hole,
            // which is exactly the state this feature exists to end.
            T.Check("smashing it reports a change", eb.BreakGlass(w, i));
            yield return Step.Ticks(1);
            T.Check($"shards appear ({Glass.ShardCount(w)})", Glass.ShardCount(w) == 4);
            T.Check("one per corner", Glass.ShardCount(w) == 4);

            // BREAK IT: leave the pane alive when broken -> the window is smashed AND still glazed, so you
            // get shards in front of an intact pane.
            T.Check("and the glass itself is gone", !w.Openings[i].HasGlass);

            // Un-smashing puts it back: the panel checkbox goes both ways, so the teardown has to as well.
            T.Check("repairing reports a change", eb.BreakGlass(w, i, false));
            yield return Step.Ticks(1);
            T.Check($"and the shards go ({Glass.ShardCount(w)})", Glass.ShardCount(w) == 0);
            T.Check("with the glass back", w.Openings[i].HasGlass);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class BrokenGlassIsRefusedOnAnUnglazedHole : GameTest
    {
        public override string Name => "buildtool.broken_glass_needs_glass";

        public override IEnumerable<Step> Run()
        {
            // Broken is a state of GLASS. Marking a bare hole broken would store a combination no other code
            // path can produce -- HasGlass is already false for it -- and would render shards clinging to a
            // frame that never held a pane.
            var eb = Glass.Rig(this, out var ed);
            yield return Step.Ticks(1);

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            int i = eb.AddOpening(w, 3f, 0f, 0);                 // a DOOR: a hole, never glazed
            eb.SetOpeningGlass(w, i, glazed: false);
            yield return Step.Ticks(1);
            T.Check("the opening is not glazed", !w.Openings[i].Glazed);

            // BREAK IT: drop the Glazed guard from BreakGlass -> this returns true and the doorway grows
            // glass shards.
            T.Check("smashing a bare hole is refused", !eb.BreakGlass(w, i));
            yield return Step.Ticks(1);
            T.Check("nothing was marked", !w.Openings[i].GlassBroken);
            T.Check($"and no shards appeared ({Glass.ShardCount(w)})", Glass.ShardCount(w) == 0);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class SmashEveryWindowIsOneUndoStep : GameTest
    {
        public override string Name => "buildtool.smash_every_window_preset";

        public override IEnumerable<Step> Run()
        {
            var eb = Glass.Rig(this, out var ed);
            yield return Step.Ticks(1);
            float y = eb.ActiveFloorY;

            var a = eb.AddWall(new Vector3(0f, y, 0f), 0f, 12f);
            var b = eb.AddWall(new Vector3(0f, y, -9f), 0f, 12f);
            int a0 = eb.AddOpening(a, 2f, 1f, 1), a1 = eb.AddOpening(a, 7f, 1f, 1);
            int b0 = eb.AddOpening(b, 4f, 1f, 1);
            eb.SetOpeningGlass(a, a0, glazed: true);
            eb.SetOpeningGlass(a, a1, glazed: true);
            eb.SetOpeningGlass(b, b0, glazed: true);
            int doorway = eb.AddOpening(b, 9f, 0f, 0);           // stays a hole
            eb.SetOpeningGlass(b, doorway, glazed: false);
            yield return Step.Ticks(1);

            // BREAK IT: sweep every opening rather than every GLAZED one -> this returns 4 and the doorway
            // grows shards.
            int n = eb.BreakAllGlass();
            yield return Step.Ticks(1);
            T.Check($"three windows smashed, the doorway skipped ({n})", n == 3);
            T.Check($"wall A has two windows' worth of shards ({Glass.ShardCount(a)})", Glass.ShardCount(a) == 8);
            T.Check($"wall B has one ({Glass.ShardCount(b)})", Glass.ShardCount(b) == 4);

            // BREAK IT: name every shard node the same thing -> Godot suffixes the duplicate rather than
            // rejecting it, so the COUNT still comes out right and only the names show the collision. That
            // is why this asserts the names and not just the total.
            var an = Glass.ShardNodeNames(a);
            an.Sort();
            T.Check($"each shard node names its own opening ({string.Join(",", an)})",
                    an.Count == 2 && an[0] == "GlassShards_0" && an[1] == "GlassShards_1");

            // BREAK IT: push one undo step per opening -> this needs three presses, which is the complaint
            // that put a single undo on the whole-building recolour.
            T.Check("one undo step for the whole sweep", ed.Undo());
            yield return Step.Ticks(1);
            T.Check($"every window is back ({Glass.ShardCount(a)} + {Glass.ShardCount(b)})",
                    Glass.ShardCount(a) == 0 && Glass.ShardCount(b) == 0);
            T.Check("and they are glazed again", a.Openings[a0].HasGlass && b.Openings[b0].HasGlass);

            // Running it twice must not push a second, empty undo step.
            eb.BreakAllGlass();
            yield return Step.Ticks(1);
            int again = eb.BreakAllGlass();
            T.Check($"a second sweep changes nothing ({again})", again == 0);

            // BREAK IT: push an undo step even when nothing changed -> the next Ctrl+Z spends itself on the
            // empty step and the windows stay smashed, which reads as "undo is broken". The count alone
            // cannot see this: the no-op returns 0 either way. Only pressing undo can.
            yield return Step.Ticks(1);
            T.Check("undo after a no-op sweep still does something", ed.Undo());
            yield return Step.Ticks(1);
            T.Check($"and it actually repaired the windows ({Glass.ShardCount(a)} + {Glass.ShardCount(b)})",
                    Glass.ShardCount(a) == 0 && Glass.ShardCount(b) == 0);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class BrokenGlassSurvivesSaveAndDuplication : GameTest
    {
        public override string Name => "buildtool.broken_glass_persists_and_dupes";

        static string Path => ProjectSettings.GlobalizePath("res://content/buildings/") + "editor_none_Walls.dat";

        public override IEnumerable<Step> Run()
        {
            // The whole reason this rides GlassBroken instead of a new prop list: it persists and duplicates
            // with no new save code. That claim is worth checking rather than asserting.
            if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);
            var eb = Glass.Rig(this, out var ed);
            yield return Step.Ticks(1);

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            int i = eb.AddOpening(w, 3f, 1f, 1);
            eb.SetOpeningGlass(w, i, glazed: true);
            eb.BreakGlass(w, i);
            yield return Step.Ticks(1);
            T.Check($"smashed, with shards ({Glass.ShardCount(w)})", Glass.ShardCount(w) == 4);

            eb.Save();
            var back = WallSave.Read(System.IO.File.ReadAllLines(Path));
            T.Check($"one wall saved ({back.Count})", back.Count == 1);
            if (back.Count == 1 && back[0].Openings.Count == 1)
            {
                T.Check("the opening came back glazed", back[0].Openings[0].Glazed);
                T.Check("and still broken", back[0].Openings[0].GlassBroken);
            }
            else T.Check("the opening survived the save", false);

            // Ctrl+D: the copy is smashed too, because openings copy whole.
            int made = eb.DuplicateFloor();
            yield return Step.Ticks(1);
            T.Check($"duped one ({made})", made == 1);
            WallSurface copy = null;
            foreach (var s in eb.Walls) if (GodotObject.IsInstanceValid(s) && s != w) copy = s;
            T.Check("the copy exists", copy != null);
            if (copy != null)
            {
                T.Check($"and it is smashed too ({Glass.ShardCount(copy)})", Glass.ShardCount(copy) == 4);
                T.Check("with the flag intact", copy.Openings[0].GlassBroken);
            }

            // THE SEED IS STABLE ACROSS A ROUND TRIP. Which shard shape lands in which corner is derived
            // from the opening's own geometry rather than stored, so it has to survive U and V going out to
            // text and back. Seeded off raw floats it would not, and the building would look different
            // every time it was reopened for no reason the user did.
            //
            // BREAK IT: seed off (int)o.U instead of the quantised value -> the fingerprint changes.
            // A U THAT THE SAVE FORMAT ROUNDS. The format writes "0.####", so 2.99996 goes out as "3" and
            // comes back 3.0. That is the ONLY situation where quantising the seed matters, and an opening
            // sitting on a whole number -- which is every opening the tools place -- cannot express it:
            // both a quantised and a truncated seed survive a round trip that changes nothing.
            //
            // The first version of this test compared a signature before the save with one after, which
            // could not fail either: both sides run the SAME seed function, so a mutation to it moves both
            // and they still match. It has to be a value the round trip actually perturbs.
            var nudged = w.Openings[0];
            nudged.U = 2.99996f;
            w.Openings[0] = nudged;
            w.Rebuild();
            yield return Step.Ticks(1);
            eb.Save();

            string before = Glass.Signature(w);
            var reloaded = WallSave.Read(System.IO.File.ReadAllLines(Path));
            eb.RestoreAll(reloaded);
            yield return Step.Ticks(2);
            WallSurface fresh = null;
            foreach (var s2 in eb.Walls) if (GodotObject.IsInstanceValid(s2)) { fresh = s2; break; }
            T.Check("the wall reloaded", fresh != null);
            if (fresh != null)
            {
                string after = Glass.Signature(fresh);
                T.Check($"the shards came back smashed ({Glass.ShardCount(fresh)})", Glass.ShardCount(fresh) == 4);
                T.Check($"in the same arrangement\n   before: {before}\n   after:  {after}", before == after);
            }

            if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);   // shared boot: do not leak a layout
            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class ShardsAreSceneryNotStructure : GameTest
    {
        public override string Name => "buildtool.shards_are_scenery";

        public override IEnumerable<Step> Run()
        {
            // A smashed window is a hole you can shoot and walk through -- that is the point of smashing it.
            // A remnant that caught bullets or cast shadows would quietly undo that.
            var eb = Glass.Rig(this, out var ed);
            yield return Step.Ticks(1);

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            int i = eb.AddOpening(w, 3f, 1f, 1);
            eb.SetOpeningGlass(w, i, glazed: true);
            eb.BreakGlass(w, i);
            yield return Step.Ticks(1);

            var shards = Glass.ShardsOn(w);
            T.Check("shards exist", shards != null);
            if (shards == null) { eb.QueueFree(); ed.QueueFree(); yield break; }

            int bodies = 0, casting = 0, meshes = 0;
            foreach (var c in shards.GetChildren())
            {
                if (c is PhysicsBody3D || c is Area3D) bodies++;
                if (c is MeshInstance3D mi)
                {
                    meshes++;
                    if (mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) casting++;
                }
            }
            T.Check($"all four are meshes ({meshes})", meshes == 4);
            // BREAK IT: give a shard a collider -> the smashed window stops being a hole.
            T.Check($"none of them collide ({bodies})", bodies == 0);
            // BREAK IT: leave CastShadow default -> four shadow casters per broken window, and a derelict
            // has thirty of them.
            T.Check($"and none cast a shadow ({casting})", casting == 0);

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
