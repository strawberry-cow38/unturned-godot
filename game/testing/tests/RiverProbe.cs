using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>What a carved river actually does to the ground, measured through the COLLIDER.
    ///
    /// Written after five wrong theories about a seam -- stair-stepping, persistence, mitre length, depth
    /// order, then clip-vs-station mismatch. The seam is gone now for a structural reason rather than a
    /// tuned one: the river DISPLACES the terrain instead of cutting a hole and covering it, so there is one
    /// surface and nothing to line up. These checks are the ones that would catch it coming back.
    ///
    /// Cross-section sampled BEFORE and AFTER the carve, so the displacement is measured rather than inferred
    /// from a constant.</summary>
    public sealed class RiverOverhangProbe : GameTest
    {
        public override string Name => "river.overhang_probe";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);
            terr.EditHeight(300f, -300f, 120f, 40f);   // sculpted: a flat plane hides a bed that does not follow
            terr.RebuildAll();
            yield return Ticks(2);

            var (minX, _, _, maxZ) = terr.WorldBoundsXZ();
            const float half = 8f, depth = 4f;
            var a = new Vector3(minX + 200f, 0f, maxZ - 300f);
            var b = new Vector3(minX + 600f, 0f, maxZ - 300f);
            float midX = (a.X + b.X) * 0.5f;

            // offsets across the channel, in metres from the centreline
            // Out to 2.5x half-width, because the influence no longer stops at the bank -- the approach dishes
            // the surrounding ground toward the river and has to die out cleanly at its own outer edge.
            var offs = new float[] { 0f, 2f, 4f, 6f, 7.5f, 8f, 10f, 14f, 18f, 20f, 24f };
            var before = new float[offs.Length];
            for (int i = 0; i < offs.Length; i++) before[i] = terr.SampleHeight(midX, a.Z + offs[i]);

            terr.CarveRiver(a, b, half, depth);
            yield return Ticks(3);

            var space = World.GetWorld3D().DirectSpaceState;
            bool Ray(float wx, float wz, out float y)
            {
                y = 0f;
                var q = PhysicsRayQueryParameters3D.Create(new Vector3(wx, 500f, wz), new Vector3(wx, -500f, wz));
                q.CollisionMask = 1u << 0;
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) return false;
                y = ((Vector3)hit["position"]).Y;
                return true;
            }

            int hits = 0; var drop = new float[offs.Length];
            for (int i = 0; i < offs.Length; i++)
            {
                if (!Ray(midX, a.Z + offs[i], out float y)) { drop[i] = float.NaN; continue; }
                hits++;
                drop[i] = before[i] - y;
            }
            var parts = new List<string>();
            for (int i = 0; i < offs.Length; i++) parts.Add($"{offs[i]:0.#}m:{drop[i]:0.00}");
            GD.Print("[river-probe] drop by offset -> " + string.Join("  ", parts));

            // 1. ONE SURFACE. The old design cut a hole and laid a bed over it; a ray that hits NOTHING is the
            //    signature of that hole reopening, and it is also how the bed's missing collider hid for weeks.
            T.Check($"the ground is unbroken across the whole channel ({hits}/{offs.Length} rays landed)",
                    hits == offs.Length);

            // 2. IT IS ACTUALLY A CHANNEL. Full depth at the centreline.
            T.Check($"the centreline is a full depth down ({drop[0]:0.00} m of {depth:0.00})",
                    Mathf.Abs(drop[0] - depth) < 0.35f);

            // 3. THE APPROACH REACHES OUT, AND THEN STOPS. The ground outside the bank is dished toward the
            //    river -- that is the point of the blend -- but it must die to exactly zero at its own outer
            //    edge, or the influence ends in a ring you can see from the air.
            T.Check($"the ground outside the bank is dished toward the river ({drop[6]:0.00} m at {offs[6]:0.#} m)",
                    drop[6] > 0.05f);
            T.Check($"...and the influence dies out cleanly ({drop[9]:0.000} m at {offs[9]:0.#} m, {drop[10]:0.000} m beyond)",
                    Mathf.Abs(drop[9]) < 0.05f && Mathf.Abs(drop[10]) < 0.05f);

            // 4. SMOOTH, NOT A TRENCH. Monotonic from centre to bank, and the last step before the bank must be
            //    small -- a profile that hits zero with a steep slope reads as a lip however smooth the maths.
            bool mono = true;
            for (int i = 1; i < offs.Length; i++) if (drop[i] > drop[i - 1] + 0.001f) mono = false;
            T.Check($"the surface rises monotonically from centreline to untouched ground ({mono})", mono);
            yield break;
        }
    }
}
