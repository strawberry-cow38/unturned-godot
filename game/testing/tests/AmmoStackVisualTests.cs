using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // A dropped ammo stack shows more rounds the fuller it is (strawberry 2026-09-08: "stacks will show
    // visually, 1/4-2/4 1 round 2/4-3/4 2 round 3/4-4/4 3 rounds. the stack shape stays the same just hide rounds
    // in the stack"). His example is a THREE-round pile in quarters, i.e. one more band than rounds -- so a
    // five-round pile reads in sixths and the three-round case still lands exactly where he specified.
    //
    // ASSERTS ON THE MESH, NOT ON THE BANDS. Re-checking `VisibleRounds(96,128,3) == 3` would only confirm that
    // the test and the function agree about arithmetic I wrote twice; it passes just as happily when the manifest
    // has no `rounds`, when the prefix parse returns the whole file, or when the rounds are ordered so hiding one
    // leaves a cartridge floating where the bottom of the pile used to be. So every check below reads the real
    // ArrayMesh built from the real manifest entry and the real .txt on disk: its triangle count, and its HEIGHT.
    //
    // The GROUND check is the load-bearing one. Hiding rounds must never leave the remainder hovering where a
    // lower round used to be; if the file order changes and a ground round is the one dropped, every triangle
    // count above still passes and only the AABB notices. (It replaced an earlier "round N is highest" assert,
    // which was a PROXY: it held for these piles and would have forbidden a flat arrangement outright.)
    public class AmmoStackVisual : GameTest
    {
        public override string Name => "ammo.stack_visual";


        // ---- same-layer interpenetration (strawberry 2026-09-09: "some bullet tips are poking through
        // other bullets") --------------------------------------------------------------------------------
        //
        // This lived only in the build script that generated the meshes, which guards nothing once a mesh is
        // edited by hand or a caliber is retuned. Two rounds can pass clean THROUGH each other without
        // sharing a single plane, so no coplanar-face check can see it -- it needs a real overlap test.
        //
        // Convex-hull SAT on the XZ footprint, per layer. Layers are compared separately on purpose: a round
        // in an upper layer is MEANT to sink into the pair below (that is what makes it look seated and what
        // avoids coplanar contact), so a 3D test would reject the arrangement the design requires.

        static List<Vector2> Hull(List<Vector2> pts)
        {
            var p = pts.Select(v => new Vector2(Mathf.Round(v.X * 1e6f) / 1e6f, Mathf.Round(v.Y * 1e6f) / 1e6f))
                       .Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
            if (p.Count < 3) return p;
            List<Vector2> Half(IEnumerable<Vector2> seq)
            {
                var o = new List<Vector2>();
                foreach (var q in seq)
                {
                    while (o.Count >= 2 &&
                           (o[^1].X - o[^2].X) * (q.Y - o[^2].Y) - (o[^1].Y - o[^2].Y) * (q.X - o[^2].X) <= 0)
                        o.RemoveAt(o.Count - 1);
                    o.Add(q);
                }
                return o;
            }
            var lower = Half(p); var upper = Half(Enumerable.Reverse(p));
            lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        /// <summary>Penetration depth of two XZ footprints in metres; 0 when they are clear.</summary>
        static float Overlap(List<Vector2> a, List<Vector2> b)
        {
            var ha = Hull(a); var hb = Hull(b);
            float best = float.MaxValue;
            foreach (var poly in new[] { ha, hb })
                for (int i = 0; i < poly.Count; i++)
                {
                    var e = poly[(i + 1) % poly.Count] - poly[i];
                    var n = new Vector2(-e.Y, e.X);
                    if (n.Length() < 1e-9f) continue;
                    n = n.Normalized();
                    float amin = ha.Min(v => v.Dot(n)), amax = ha.Max(v => v.Dot(n));
                    float bmin = hb.Min(v => v.Dot(n)), bmax = hb.Max(v => v.Dot(n));
                    if (amax <= bmin || bmax <= amin) return 0f;
                    best = Mathf.Min(best, Mathf.Min(amax - bmin, bmax - amin));
                }
            return best == float.MaxValue ? 0f : best;
        }

        static int Tris(ArrayMesh m) => m == null || m.GetSurfaceCount() == 0 ? 0 : m.SurfaceGetArrayLen(0) / 3;
        static float Height(ArrayMesh m) => m == null ? 0f : m.GetAabb().Size.Y;

        public override IEnumerable<Step> Run()
        {
            // The bands are fractions of the asset's stackSize, and stackSize defaults to 1 until the catalog
            // wires it -- at which point every amount reads as a full stack and the whole feature silently
            // returns the complete mesh. That is what this test caught on its first run, so the setup is here
            // rather than assumed: the game calls RegisterAll on every world load (Main, WorldBuilder), the
            // bare L1 host does not.
            ItemCatalog.RegisterAll();

            // EVERY installed bundle, enumerated FROM THE MANIFEST. A hand-written id list has now gone
            // stale twice: the first passed while nine calibers had no bundle at all, and the second would
            // have missed the ten added after it. The floor below is the only hand-typed number left, and it
            // is there so DELETING bundles fails too rather than trivially passing on an empty list.
            var ammo = WorldItem.BundleIds();
            T.Check($"every installed bundle is enumerated ({ammo.Count} found)", ammo.Count >= 21);
            foreach (int id in ammo)
            {
                var asset = Assets.find((ushort)id);
                var cum = WorldItem.RoundsFor(id);
                string label = asset?.itemName ?? id.ToString();
                if (cum == null || cum.Length < 2) { T.Fail($"{label} (#{id}) has no `rounds` in the manifest"); continue; }

                int rounds = cum.Length, per = cum[0], stack = asset?.stackSize ?? 0;
                bool even = true;
                for (int i = 0; i < rounds; i++) if (cum[i] != per * (i + 1)) even = false;
                T.Check($"{label}: rounds are evenly sized ({string.Join(",", cum)})", even);
                T.Check($"{label}: a full stack draws all {rounds} rounds",
                        Tris(WorldItem.MeshForStack(id, stack)) == per * rounds);
                T.Check($"{label}: an amount of 1 still draws a round", Tris(WorldItem.MeshForStack(id, 1)) == per);


                // NO ROUND MAY PIERCE ANOTHER IN ITS OWN LAYER.
                var meshFull = WorldItem.MeshForStack(id, stack);
                var arr = meshFull.SurfaceGetArrays(0);
                var verts = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                int vpr = verts.Length / rounds;                 // 3 corners per tri, tris grouped per round
                var foot = new List<List<Vector2>>();
                var baseY = new List<float>();
                for (int r = 0; r < rounds; r++)
                {
                    var seg = verts.Skip(r * vpr).Take(vpr).ToList();
                    foot.Add(seg.Select(v => new Vector2(v.X, v.Z)).ToList());
                    baseY.Add(Mathf.Round(seg.Min(v => v.Y) * 1e5f) / 1e5f);
                }
                float worst = 0f; string where = "";
                for (int x1 = 0; x1 < rounds; x1++)
                    for (int x2 = x1 + 1; x2 < rounds; x2++)
                        if (Mathf.Abs(baseY[x1] - baseY[x2]) < 1e-5f)
                        {
                            float o = Overlap(foot[x1], foot[x2]);
                            if (o > worst) { worst = o; where = $"{x1}/{x2}"; }
                        }
                T.Check($"{label}: no round pierces another in its layer (worst {worst * 1000:0.000}mm {where})",
                        worst < 1e-4f);

                // Bands = rounds + 1, inclusive at the low edge. Walk every boundary and the amount below it.
                int bands = rounds + 1;
                for (int k = 1; k <= rounds; k++)
                {
                    int at = (int)System.Math.Ceiling((double)stack * k / bands);
                    var m = WorldItem.MeshForStack(id, at);
                    T.Check($"{label}: {at}/{stack} shows {k}", Tris(m) == per * k);
                    if (k > 1)
                        T.Check($"{label}: {at - 1}/{stack} shows {k - 1}",
                                Tris(WorldItem.MeshForStack(id, at - 1)) == per * (k - 1));
                    // ...and no prefix may float: hiding rounds must never lift the pile off the ground.
                    T.Check($"{label}: {k} round(s) sits on the ground", m.GetAabb().Position.Y <= 1e-4f);
                }
            }

            // CONTROL: an ordinary single-object item has no `rounds` in the manifest and must be immune -- same
            // mesh at every amount. Without this the checks above would still pass if MeshForAmount started
            // truncating every mesh in the game.
            var jeans = WorldItem.MeshForStack(2, 1);        // Work Jeans, stackSize 1, no `rounds`
            var jeansFull = WorldItem.MeshForStack(2, 99);
            T.Check("control: a non-bundle item has a mesh at all", Tris(jeans) > 0);
            T.Check("control: a non-bundle item ignores amount", Tris(jeans) == Tris(jeansFull));

            yield break;
        }
    }
}
