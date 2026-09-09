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

            // EVERY ammo id that carries a bundle, and the round count comes from the MANIFEST rather than
            // from a list in here -- a hardcoded pair passed happily while nine other calibers had no bundle
            // at all, and would go stale again the next time one is added.
            int[] ammo = { 113, 381, 5000, 5001, 5002, 5003, 5004, 5005, 5006, 103, 108 };
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
