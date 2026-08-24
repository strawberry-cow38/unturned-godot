using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>What the river actually leaves behind, measured through the COLLIDER rather than reasoned about.
    ///
    /// Written after four wrong theories in a row about an overhang -- stair-stepping, persistence, mitre
    /// length, then depth order -- none of which survived contact with a measurement. The rule this encodes:
    /// ask the geometry, do not model it in your head.
    ///
    /// The terrain now CLIPS ITSELF to the bank (see Terrain._riverField), so the invariants are end-to-end:
    /// just outside the bank you land on terrain at ground level; just inside it you land on the riverbed, a
    /// depth below. A gap between them, or terrain hanging over the water, breaks one of the two.</summary>
    public sealed class RiverOverhangProbe : GameTest
    {
        public override string Name => "river.overhang_probe";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);
            terr.EditHeight(300f, -300f, 120f, 40f);   // sculpted: a flat plane hides a floor that does not follow
            terr.RebuildAll();
            yield return Ticks(2);

            var (minX, _, _, maxZ) = terr.WorldBoundsXZ();
            const float half = 8f, depth = 4f;
            var a = new Vector3(minX + 200f, 0f, maxZ - 300f);
            var b = new Vector3(minX + 600f, 0f, maxZ - 300f);
            terr.CarveRiver(a, b, half, depth);
            yield return Ticks(3);

            var space = World.GetWorld3D().DirectSpaceState;
            bool Probe(float wx, float wz, out float y)
            {
                y = 0f;
                var q = PhysicsRayQueryParameters3D.Create(new Vector3(wx, 400f, wz), new Vector3(wx, -400f, wz));
                q.CollisionMask = 1u << 0;
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) return false;
                y = ((Vector3)hit["position"]).Y;
                return true;
            }

            // WHAT IS ACTUALLY THERE? Name the collider instead of inferring from a miss.
            for (float off = 0f; off <= 12f; off += 4f)
            {
                float px = (a.X + b.X) * 0.5f, pz = a.Z + off;
                var qq = PhysicsRayQueryParameters3D.Create(new Vector3(px, 400f, pz), new Vector3(px, -400f, pz));
                qq.CollisionMask = 1u << 0;
                var hh = space.IntersectRay(qq);
                string what = hh.Count == 0 ? "NOTHING"
                    : $"{((Node)hh["collider"]).Name} @ y={((Vector3)hh["position"]).Y:F2}";
                GD.Print($"[river-probe] offset {off:F0} m -> {what} (ground {terr.SampleHeight(px, pz):F2})");
            }

            var dir = new Vector2(b.X - a.X, b.Z - a.Z).Normalized();
            var nrm = new Vector2(-dir.Y, dir.X);

            int outsideOk = 0, outsideN = 0, insideOk = 0, insideN = 0, insideNoHit = 0, insideOnTerrain = 0;
            float worstOutside = 0f, worstInside = 0f;
            for (float t = 0.2f; t <= 0.8f; t += 0.05f)
            {
                var mid = new Vector2(a.X, a.Z).Lerp(new Vector2(b.X, b.Z), t);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    // JUST OUTSIDE the bank: terrain must still be there, at ground level. This is the check
                    // that catches terrain clipped away too eagerly -- a gap between bank and ground.
                    float ox = mid.X + nrm.X * (half + 1.5f) * sgn, oz = mid.Y + nrm.Y * (half + 1.5f) * sgn;
                    outsideN++;
                    if (Probe(ox, oz, out float oy))
                    {
                        float ground = terr.SampleHeight(ox, oz);
                        float err = Mathf.Abs(oy - ground);
                        if (err <= 0.35f) outsideOk++;
                        if (err > worstOutside) worstOutside = err;
                    }

                    // JUST INSIDE the bank: you must land on the BED, clearly below ground. This is the check
                    // that catches terrain hanging over the channel -- the bug that shipped three times.
                    float ix = mid.X + nrm.X * (half - 2.5f) * sgn, iz = mid.Y + nrm.Y * (half - 2.5f) * sgn;
                    insideN++;
                    if (Probe(ix, iz, out float iy))
                    {
                        float ground = terr.SampleHeight(ix, iz);
                        float below = ground - iy;
                        if (below > depth * 0.5f) insideOk++; else insideOnTerrain++;
                        if (below < worstInside || worstInside == 0f) worstInside = below;
                    }
                    else insideNoHit++;   // distinct from landing on terrain: 0.00 alone cannot tell them apart
                }
            }

            GD.Print($"[river-probe] outside {outsideOk}/{outsideN} at ground (worst err {worstOutside:F2} m) | " +
                     $"inside {insideOk}/{insideN} on the bed (shallowest {worstInside:F2} m below ground) " +
                     $"[no-hit {insideNoHit}, landed-on-terrain {insideOnTerrain}]");

            T.Check($"terrain survives right up to the bank ({outsideOk}/{outsideN}, worst error {worstOutside:F2} m)",
                    outsideN > 0 && outsideOk == outsideN);
            T.Check($"inside the bank you land on the BED, not on terrain ({insideOk}/{insideN}, no-hit {insideNoHit}, on-terrain {insideOnTerrain})",
                    insideN > 0 && insideOk == insideN);
            yield break;
        }
    }
}
