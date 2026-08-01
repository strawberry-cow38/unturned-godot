using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The terrain collider is built from the SAME _grid that SampleHeight reads, so the two must agree exactly at
    // every grid vertex. That invariant is what makes the collision shape swappable: a trimesh and a heightfield
    // are both "correct" only if a ray lands where SampleHeight says the ground is.
    //
    // Why this test exists: HeightMapShape3D indexes ROW-MAJOR IN Z (data[z*w+x]) while the terrain mesh builds its
    // verts X-MAJOR, and this port NEGATES Z. Get either wrong and you produce a collider that loads clean, reports
    // no error, renders the right picture -- and drops the player through the world, because the COLLISION is
    // transposed or mirrored relative to the visible surface. Nothing else in the suite would notice.
    //
    // The terrain is sculpted ASYMMETRICALLY on purpose. On flat ground (or a symmetric hill) a transposed collider
    // is indistinguishable from a correct one, so a test built on CreateFlat's default plane would pass through
    // every one of those bugs.
    public sealed class TerrainColliderMatchesHeightTests : GameTest
    {
        public override string Name => "terrain.collider_matches_sampled_height";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);
            var (minX, maxX, minZ, maxZ) = terr.WorldBoundsXZ();

            // Distinct bumps at NON-mirror, NON-transpose positions: (200,-120) has no twin at (120,-200), and the
            // +Z half differs from the -Z half. Any axis swap or flip therefore moves a hill somewhere a ray checks.
            terr.EditHeight(200f, -120f, 90f, 45f);
            terr.EditHeight(760f, -880f, 130f, -28f);
            terr.EditHeight(150f, -900f, 70f, 22f);
            terr.EditHeight(880f, -200f, 60f, 12f);
            terr.RebuildAll();
            yield return Ticks(3);   // let the physics server register the rebuilt shapes before querying

            var space = World.GetWorld3D().DirectSpaceState;
            const float UNIT = 4f;
            int gw = (int)System.MathF.Round((maxX - minX) / UNIT) + 1, gh = (int)System.MathF.Round((maxZ - minZ) / UNIT) + 1;

            // Pass 1: a coarse sweep of the whole map, and Pass 2: the CHUNK SEAMS specifically. Chunks are 48 cells
            // but 49 samples and the physics backend pads heightfield dimensions internally, so a seam defect can
            // sit entirely between the sweep's stride. Strides are coprime-ish with 48 to avoid sampling in phase.
            int checkedPts = 0, missed = 0, offBy = 0; float worst = 0f; string worstAt = "";
            bool sculpted = false;
            void Probe(float x, float z)
            {
                float expect = terr.SampleHeight(x, z);
                var q = PhysicsRayQueryParameters3D.Create(new Vector3(x, expect + 200f, z), new Vector3(x, expect - 200f, z));
                var r = space.IntersectRay(q);
                checkedPts++;
                if (!r.ContainsKey("position")) { missed++; return; }
                float got = ((Vector3)r["position"]).Y;
                float d = System.MathF.Abs(got - expect);
                if (d > worst) { worst = d; worstAt = $"({x:F0},{z:F0}) expect {expect:F2} got {got:F2}"; }
                if (d > 0.05f) offBy++;
                if (System.MathF.Abs(expect - 30f) > 1f) sculpted = true;   // proves we probed real relief, not just the flat plane
            }

            for (int gx = 1; gx < gw - 1; gx += 13)
                for (int gy = 1; gy < gh - 1; gy += 13)
                    Probe(minX + gx * UNIT, maxZ - gy * UNIT);
            int sweepPts = checkedPts;

            for (int gx = 48; gx < gw - 1; gx += 48)
                for (int gy = 1; gy < gh - 1; gy += 7)
                    foreach (int ox in new[] { -1, 0, 1 })
                        Probe(minX + (gx + ox) * UNIT, maxZ - gy * UNIT);

            T.Check($"probed {checkedPts} grid vertices ({sweepPts} sweep + {checkedPts - sweepPts} seam)", checkedPts > 500);
            T.Check("probes covered sculpted relief, not just flat ground", sculpted);
            T.Check($"every ray hit the terrain collider (missed {missed})", missed == 0);
            T.Check($"collider height matches SampleHeight within 5cm (off {offBy}, worst {worst:F3}m at {worstAt})", offBy == 0);

            // A transposed/mirrored collider can still be hit by every downward ray -- it just holds the WRONG shape.
            // Assert the tallest sculpted peak is actually solid where the visual surface claims it is, and that the
            // point mirrored across the diagonal is NOT at that height (which is what a transpose would produce).
            float peakX = 200f, peakZ = -120f;
            float peakH = terr.SampleHeight(peakX, peakZ), twinH = terr.SampleHeight(-peakZ, -peakX);
            T.Check($"the asymmetric peak is distinguishable from its transpose ({peakH:F1} vs {twinH:F1})", System.MathF.Abs(peakH - twinH) > 5f);

            var pq = PhysicsRayQueryParameters3D.Create(new Vector3(peakX, peakH + 60f, peakZ), new Vector3(peakX, peakH - 60f, peakZ));
            var pr = space.IntersectRay(pq);
            T.Check("a ray at the peak hits solid ground", pr.ContainsKey("position"));
            if (pr.ContainsKey("position"))
                T.Check($"the peak's collider is at the peak's visual height ({((Vector3)pr["position"]).Y:F2} vs {peakH:F2})",
                        System.MathF.Abs(((Vector3)pr["position"]).Y - peakH) < 0.05f);

            // The gameplay symptom of a bad seam is an invisible wall, which no vertical ray can see: fire horizontal
            // rays across each chunk seam, clear of the local surface. Anything hit up there is phantom geometry.
            int walls = 0, crossings = 0;
            for (int gx = 48; gx < gw - 1; gx += 48)
                for (int gy = 6; gy < gh - 6; gy += 17)
                {
                    float xs = minX + gx * UNIT, z = maxZ - gy * UNIT;
                    float clear = System.MathF.Max(terr.SampleHeight(xs - 8f, z), System.MathF.Max(terr.SampleHeight(xs, z), terr.SampleHeight(xs + 8f, z))) + 3f;
                    var wq = PhysicsRayQueryParameters3D.Create(new Vector3(xs - 8f, clear, z), new Vector3(xs + 8f, clear, z));
                    crossings++;
                    if (space.IntersectRay(wq).ContainsKey("position")) walls++;
                }
            T.Check($"no phantom walls at chunk seams ({walls} hits / {crossings} crossings)", walls == 0);
        }
    }
}
