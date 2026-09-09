using Godot;
using SDG.Unturned;
using System.Collections.Generic;

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

            // (id, stackSize, tris per round, how many rounds the pile has)
            var beds = new[]
            {
                (id: 5004, stack: 128, per: 22, rounds: 5, label: "5.56 FMJ"),
                (id: 113,  stack: 32,  per: 20, rounds: 3, label: "12 gauge buckshot"),
            };

            foreach (var b in beds)
            {
                T.Check($"{b.label}: stackSize is {b.stack}", Assets.find((ushort)b.id)?.stackSize == b.stack);

                var full = WorldItem.MeshForStack(b.id, b.stack);
                T.Check($"{b.label}: a full stack is {b.rounds} rounds ({b.per * b.rounds} tris)",
                        Tris(full) == b.per * b.rounds);

                // BANDS = ROUNDS + 1, inclusive at the low edge. Walk EVERY boundary rather than sampling a
                // couple: the band count changed with the pile size, and an off-by-one here shows up only at
                // the exact edges -- ceil-vs-floor got all of them wrong while the midpoints stayed right.
                int bands = b.rounds + 1;
                for (int k = 1; k <= b.rounds; k++)
                {
                    int at = (int)System.Math.Ceiling((double)b.stack * k / bands);   // first amount in band k
                    T.Check($"{b.label}: {at}/{b.stack} shows {k}", Tris(WorldItem.MeshForStack(b.id, at)) == b.per * k);
                    if (k > 1)
                    {
                        int below = at - 1;
                        T.Check($"{b.label}: {below}/{b.stack} shows {k - 1}",
                                Tris(WorldItem.MeshForStack(b.id, below)) == b.per * (k - 1));
                    }
                }

                // The single ejected round -- the case the whole feature exists to serve, and it sits below
                // every band. It must draw ONE round, never zero: a dropped item that renders nothing is
                // indistinguishable from a crash.
                T.Check($"{b.label}: an amount of 1 still draws a round", Tris(WorldItem.MeshForStack(b.id, 1)) == b.per);

                // HEIGHT + GROUND. Hiding must never leave the remainder hovering where a lower round was,
                // which is the failure a triangle count cannot see: reorder the rounds in the .txt and every
                // count above still passes. Checked for every prefix the visual can draw.
                float ground = Height(WorldItem.MeshForStack(b.id, 1));
                for (int k = 1; k <= b.rounds; k++)
                {
                    var m = WorldItem.MeshForStack(b.id, (int)System.Math.Ceiling((double)b.stack * k / bands));
                    T.Check($"{b.label}: {k} round(s) sits on the ground", m.GetAabb().Position.Y <= 1e-4f);
                    T.Check($"{b.label}: {k} round(s) is no taller than the full pile", Height(m) <= Height(full) + 1e-5f);
                }
                T.Check($"{b.label}: the full pile is taller than one round", Height(full) > ground + 0.0005f);
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
