using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>THROWAWAY PROBE. Measures what the river cut actually leaves behind, instead of me reasoning
    /// about it a fourth time. Reports the closest surviving (un-holed) terrain quad CORNER to the centreline;
    /// anything below the half-width is a quad hanging over the water.</summary>
    public sealed class RiverOverhangProbe : GameTest
    {
        public override string Name => "river.overhang_probe";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr);
            terr.EditHeight(300f, -300f, 120f, 40f);   // not flat: a plane hides a floor that does not follow
            terr.RebuildAll();
            yield return Ticks(1);

            var (minX, _, _, maxZ) = terr.WorldBoundsXZ();
            const float UNIT = 4f;
            float half = 8f;
            // a STRAIGHT river, which is the case strawberry drew
            var a = new Vector3(minX + 200f, 0f, maxZ - 300f);
            var b = new Vector3(minX + 600f, 0f, maxZ - 300f);
            terr.CarveRiver(a, b, half, 4f);
            yield return Ticks(1);

            // Walk every quad in the neighbourhood; for each SURVIVING one, find its nearest corner's distance
            // to the segment. The minimum over all survivors is the overhang measurement.
            float closest = float.MaxValue; int cgx = -1, cgy = -1;
            int holed = 0, alive = 0;
            for (int gx = 0; gx < 250; gx++)
                for (int gy = 0; gy < 250; gy++)
                {
                    bool isHole = terr.IsHole(gx, gy);
                    if (isHole) { holed++; continue; }
                    float best = float.MaxValue;
                    for (int cx = 0; cx <= 1; cx++)
                        for (int cy = 0; cy <= 1; cy++)
                        {
                            float wx = minX + (gx + cx) * UNIT;
                            float wz = maxZ - (gy + cy) * UNIT;
                            // distance from (wx,wz) to segment a-b in the XZ plane
                            var p = new Vector2(wx, wz);
                            var A = new Vector2(a.X, a.Z); var B = new Vector2(b.X, b.Z);
                            var ab = B - A; float t = Mathf.Clamp((p - A).Dot(ab) / ab.LengthSquared(), 0f, 1f);
                            float d = (p - (A + ab * t)).Length();
                            if (d < best) best = d;
                        }
                    if (best < 400f) alive++;
                    if (best < closest) { closest = best; cgx = gx; cgy = gy; }
                }

            // ...and the OUTERMOST holed corner: the shelf has to reach at least this far, or there is a band
            // of removed terrain with no bed under it.
            float farthestHole = 0f;
            for (int gx = 0; gx < 250; gx++)
                for (int gy = 0; gy < 250; gy++)
                {
                    if (!terr.IsHole(gx, gy)) continue;
                    for (int cx = 0; cx <= 1; cx++)
                        for (int cy = 0; cy <= 1; cy++)
                        {
                            float wx = minX + (gx + cx) * UNIT;
                            float wz = maxZ - (gy + cy) * UNIT;
                            var p = new Vector2(wx, wz);
                            var A = new Vector2(a.X, a.Z); var B = new Vector2(b.X, b.Z);
                            var ab = B - A; float t = Mathf.Clamp((p - A).Dot(ab) / ab.LengthSquared(), 0f, 1f);
                            float d = (p - (A + ab * t)).Length();
                            if (d > farthestHole) farthestHole = d;
                        }
                }

            float shelfOuter = Terrain.RiverShelfOuterFor(half);
            GD.Print($"[river-probe] half={half} holed={holed} closest surviving corner={closest:F3} m " +
                     $"farthest holed corner={farthestHole:F3} m shelfOuter={shelfOuter:F3} m");

            T.Check($"no surviving terrain quad reaches inside the channel (closest corner {closest:F2} m vs half {half} m)",
                    closest >= half);
            // THE ONE THAT WAS ACTUALLY BROKEN. The bed must provide terrain-height geometry everywhere the cut
            // removed some, or the surviving quads at the far side of that band hang over nothing.
            T.Check($"the shelf spans every hole it cut (shelf {shelfOuter:F2} m vs farthest hole {farthestHole:F2} m)",
                    shelfOuter >= farthestHole);

            // THE CHECK WITH TEETH. The two above BOTH passed while the bug was live: the mask was always
            // right and the old sloped apron reached exactly as far as the shelf does. What was wrong is that
            // between the bank and the outer rim the apron was still CLIMBING, so it sat below the terrain that
            // survives from 8.94 m out -- and those quads hung over it. Extent was never the problem; height
            // was. So sample the bed's own top surface out in that band and require it to be at terrain level.
            int lowSamples = 0, samples = 0; float worstDrop = 0f;
            var dir = new Vector2(b.X - a.X, b.Z - a.Z).Normalized();
            var nrm = new Vector2(-dir.Y, dir.X);
            for (float t2 = 0.25f; t2 <= 0.75f; t2 += 0.1f)
            {
                var mid = new Vector2(a.X, a.Z).Lerp(new Vector2(b.X, b.Z), t2);
                for (float off = half + 1.5f; off <= shelfOuter - 0.5f; off += 1.0f)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        float wx = mid.X + nrm.X * off * sgn, wz = mid.Y + nrm.Y * off * sgn;
                        float top = terr.BedTopNear(wx, wz, 2.0f);
                        if (top == float.MinValue) continue;
                        float ground = terr.SampleHeight(wx, wz);
                        samples++;
                        float drop = ground - top;
                        if (drop > 0.25f) { lowSamples++; if (drop > worstDrop) worstDrop = drop; }
                    }
            }
            GD.Print($"[river-probe] band samples={samples} below-terrain={lowSamples} worstDrop={worstDrop:F2} m");
            T.Check($"outside the bank the bed sits AT terrain level, not under it ({lowSamples}/{samples} low, worst {worstDrop:F2} m)",
                    samples > 0 && lowSamples == 0);
            yield break;
        }
    }
}
