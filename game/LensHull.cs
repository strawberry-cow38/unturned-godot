using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The convex hull of a lamp lens, projected on the car's XY plane. All that is left of HeadlightBeam, the visible
    // headlight shaft removed 2026-10-04 (strawberry: "remove the headlight faux beams + dust flecks") -- the hull also
    // decided the lamp's tint (round = warm, rectangular = cool), and that part is about the light itself.
    public static class LensHull
    {
        /// <summary>Convex hull (monotone chain) of a lamp's vertices projected on the car's XY plane -- the real
        /// lens outline. Vehicle.ClassifyLamp counts its corners to tell a round lamp from a rectangular one.</summary>
        public static Vector2[] Of(IEnumerable<Vector2> pts)
        {
            var p = new List<Vector2>();
            foreach (var q in pts) p.Add(q);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            for (int i = p.Count - 1; i > 0; i--) if (p[i].IsEqualApprox(p[i - 1])) p.RemoveAt(i);
            if (p.Count < 3) return p.ToArray();
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var lo = new List<Vector2>();
            foreach (var q in p) { while (lo.Count >= 2 && Cross(lo[^2], lo[^1], q) <= 0) lo.RemoveAt(lo.Count - 1); lo.Add(q); }
            var up = new List<Vector2>();
            for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (up.Count >= 2 && Cross(up[^2], up[^1], q) <= 0) up.RemoveAt(up.Count - 1); up.Add(q); }
            lo.RemoveAt(lo.Count - 1); up.RemoveAt(up.Count - 1);
            lo.AddRange(up);
            // Drop near-collinear corners. A lamp outline is a few straight runs, but float noise in the ripped
            // mesh leaves extra points sitting on those runs -- a jeep lamp hulls to 10 when it is really a
            // hexagon. They would make every rectangle count as round.
            for (int i = lo.Count - 1; i >= 0 && lo.Count > 3; i--)
            {
                var a2 = lo[(i - 1 + lo.Count) % lo.Count]; var b2 = lo[i]; var c2 = lo[(i + 1) % lo.Count];
                var e1 = (b2 - a2); var e2 = (c2 - b2);
                float len1 = e1.Length(), len2 = e2.Length();
                if (len1 < 1e-5f || len2 < 1e-5f) { lo.RemoveAt(i); continue; }
                float cr = Mathf.Abs(e1.X * e2.Y - e1.Y * e2.X) / (len1 * len2);   // |sin| of the turn
                if (cr < 0.08f) lo.RemoveAt(i);                                     // < ~4.6 degrees = straight
            }
            return lo.ToArray();
        }
    }
}
