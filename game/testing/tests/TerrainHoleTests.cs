using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Terrain holes: a dug quad must be gone from the COLLIDER, not just from the picture.
    ///
    /// The whole point of a hole is that you can fall through it, so "it renders as a gap" is exactly half the
    /// feature and the wrong half to verify alone. Godot's HeightMapShape3D cannot express a hole at all -- it
    /// is a dense field with no absent sample -- so a holed chunk swaps to a trimesh built from the same quads
    /// the mesh emits. This test is what stops that swap silently leaving a phantom floor.
    ///
    /// The CONTROL leg is the load-bearing part. "A ray passed through the hole" is worthless on its own: a
    /// collider that failed to build at all, or a probe aimed off the map, passes it just as well. So every dug
    /// point is paired with a solid point that must still stop the ray, in the same chunk, from the same
    /// height.</summary>
    public sealed class TerrainHoleColliderTests : GameTest
    {
        public override string Name => "terrain.hole_collider_matches_mesh";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);

            // Sculpted, not flat -- same reasoning as terrain.collider_matches_sampled_height: on a flat plane a
            // transposed or mirrored hole mask lands on ground that looks identical, so the bug survives.
            terr.EditHeight(200f, -120f, 90f, 45f);
            terr.EditHeight(760f, -880f, 130f, -28f);
            terr.RebuildAll();
            yield return Ticks(2);

            T.Check("a fresh terrain reports no holes", terr.HoleCount == 0 && !terr.IsHole(10, 10));

            // Dig an ASYMMETRIC patch: (30..34, 20..24) has no twin at (20..24, 30..34), so a transposed mask
            // would put the hole somewhere the probes below do not look, and the control leg would fail.
            const int HX0 = 30, HX1 = 34, HY0 = 20, HY1 = 24;
            int dug = 0;
            for (int gx = HX0; gx <= HX1; gx++)
                for (int gy = HY0; gy <= HY1; gy++)
                    if (terr.SetHole(gx, gy, true)) dug++;
            T.Check($"digging marked every quad in the patch (dug {dug}, expected 25)", dug == 25);
            T.Check($"HoleCount agrees with what was dug ({terr.HoleCount})", terr.HoleCount == 25);
            T.Check("a quad outside the patch is still solid", !terr.IsHole(HX0 - 2, HY0));
            T.Check("re-digging the same quad reports no change", !terr.SetHole(HX0, HY0, true));

            terr.RebuildAll();
            yield return Ticks(3);   // let the physics server pick up the swapped shapes

            var space = World.GetWorld3D().DirectSpaceState;
            const float UNIT = 4f;
            var (minX, _, _, maxZ) = terr.WorldBoundsXZ();

            // Probe the CENTRE of a quad, not its corner: a corner is shared with three neighbours that are
            // still solid, so a corner ray hits their triangles and reports "solid" for a hole that is really
            // there. This is the difference between testing the feature and testing the sampling.
            bool RayHitsGround(int gx, int gy)
            {
                float wx = minX + (gx + 0.5f) * UNIT;
                float wz = maxZ - (gy + 0.5f) * UNIT;   // grid y walks -Z, as everywhere else in this port
                var q = PhysicsRayQueryParameters3D.Create(new Vector3(wx, 400f, wz), new Vector3(wx, -400f, wz));
                q.CollisionMask = 1u << 0;
                return space.IntersectRay(q).Count > 0;
            }

            int throughHole = 0, holePts = 0;
            for (int gx = HX0; gx <= HX1; gx++)
                for (int gy = HY0; gy <= HY1; gy++)
                { holePts++; if (!RayHitsGround(gx, gy)) throughHole++; }
            T.Check($"a ray passes through every dug quad ({throughHole}/{holePts})", throughHole == holePts);

            // THE CONTROL. Same chunk, same ray, quads that were never dug -- these MUST still stop it. Without
            // this leg the check above passes just as happily against a collider that never built.
            int solidPts = 0, stoppedSolid = 0;
            foreach (var (gx, gy) in new[] { (HX0 - 2, HY0), (HX1 + 2, HY1), (HX0, HY0 - 2), (HX1, HY1 + 2),
                                             (HX0 - 3, HY0 - 3), (HX1 + 3, HY1 + 3) })
            { solidPts++; if (RayHitsGround(gx, gy)) stoppedSolid++; }
            T.Check($"CONTROL: undug quads in the same chunk still stop the ray ({stoppedSolid}/{solidPts})",
                    stoppedSolid == solidPts);

            // Filling a hole must restore collision. A one-way feature would leave the editor unable to undo.
            for (int gx = HX0; gx <= HX1; gx++)
                for (int gy = HY0; gy <= HY1; gy++)
                    terr.SetHole(gx, gy, false);
            terr.RebuildAll();
            yield return Ticks(3);
            T.Check("filling the holes clears the count", terr.HoleCount == 0);
            int refilled = 0;
            for (int gx = HX0; gx <= HX1; gx++)
                for (int gy = HY0; gy <= HY1; gy++)
                    if (RayHitsGround(gx, gy)) refilled++;
            T.Check($"filled quads collide again ({refilled}/{holePts})", refilled == holePts);
        }
    }

    /// <summary>The hole mask survives a save/load round trip, bit-packing and all.</summary>
    public sealed class TerrainHolePersistenceTests : GameTest
    {
        public override string Name => "terrain.hole_persistence";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr);

            // A pattern that is NOT byte-aligned and NOT symmetric: bit-packing 8-per-byte makes an off-by-one
            // in the packer look fine on any run of 8, and a symmetric pattern survives a transpose.
            var dug = new List<(int, int)> { (3, 5), (3, 6), (4, 5), (17, 2), (40, 41), (0, 0), (1, 9), (63, 7) };
            foreach (var (x, y) in dug) terr.SetHole(x, y, true);
            T.Check($"dug the pattern ({terr.HoleCount})", terr.HoleCount == dug.Count);

            string path = "/tmp/ug_holetest/heights.bin.holes";
            terr.SaveHoles(path);
            T.Check("the holes file was written", System.IO.File.Exists(path));

            terr.LoadHoles(path);
            T.Check($"the count survives the round trip ({terr.HoleCount})", terr.HoleCount == dug.Count);
            int wrong = 0;
            foreach (var (x, y) in dug) if (!terr.IsHole(x, y)) wrong++;
            T.Check($"every dug quad came back dug ({dug.Count - wrong}/{dug.Count})", wrong == 0);
            // The other direction matters as much: an over-eager unpack sets neighbours it should not, and a
            // count check alone cannot see that if it also misses some.
            T.Check("a quad that was never dug is still solid", !terr.IsHole(3, 7) && !terr.IsHole(2, 5) && !terr.IsHole(41, 40));

            // No holes -> no file. An untouched map must not carry an empty holes file around, and a stale one
            // must not be able to resurrect holes into a map that was filled in.
            foreach (var (x, y) in dug) terr.SetHole(x, y, false);
            terr.SaveHoles(path);
            T.Check("saving with no holes removes the file", !System.IO.File.Exists(path));
            terr.LoadHoles(path);
            T.Check("loading a missing holes file is not an error, just no holes", terr.HoleCount == 0);

            // THE PATH A MAP ACTUALLY TAKES. Nothing calls SaveHoles/LoadHoles directly -- the editor saves a
            // heightmap and the game loads one, and holes ride along. Testing only the pair above would leave
            // the wiring untested, which is exactly how a dug map comes back filled in with every direct test
            // still green.
            string hm = "/tmp/ug_holetest/heights.bin";
            foreach (var (x, y) in dug) terr.SetHole(x, y, true);
            terr.SaveHeightmap(hm);
            T.Check("SaveHeightmap wrote the holes beside it", System.IO.File.Exists(hm + ".holes"));

            foreach (var (x, y) in dug) terr.SetHole(x, y, false);   // wipe them in memory ...
            T.Check("holes cleared in memory before the reload", terr.HoleCount == 0);
            T.Check("LoadHeightmap brings the holes back", terr.LoadHeightmap(hm) && terr.HoleCount == dug.Count);

            // And the reverse: a heightmap with NO holes file must not leave the previous map's holes standing.
            System.IO.File.Delete(hm + ".holes");
            T.Check("a heightmap with no holes file loads as solid",
                    terr.LoadHeightmap(hm) && terr.HoleCount == 0);

            yield break;
        }
    }
}
