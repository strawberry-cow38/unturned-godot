using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A dropped ammo stack shows a round per quarter of its stackSize (strawberry 2026-09-08: "stacks will show
    // visually, 1/4-2/4 1 round 2/4-3/4 2 round 3/4-4/4 3 rounds. the stack shape stays the same just hide rounds
    // in the stack").
    //
    // ASSERTS ON THE MESH, NOT ON THE BANDS. Re-checking `VisibleRounds(96,128,3) == 3` would only confirm that
    // the test and the function agree about arithmetic I wrote twice; it passes just as happily when the manifest
    // has no `rounds`, when the prefix parse returns the whole file, or when the rounds are ordered so hiding one
    // leaves a cartridge floating where the bottom of the pile used to be. So every check below reads the real
    // ArrayMesh built from the real manifest entry and the real .txt on disk: its triangle count, and its HEIGHT.
    //
    // Height is the load-bearing one. Both bundles are two rounds on the ground with a third resting on top, so
    // hiding a round must lower the mesh; if the file order ever changes and the ground round is the one dropped,
    // the triangle count still falls by exactly the same amount and only the AABB notices.
    public class AmmoStackVisual : GameTest
    {
        public override string Name => "ammo.stack_visual";

        static int Tris(ArrayMesh m) => m == null || m.GetSurfaceCount() == 0 ? 0 : m.SurfaceGetArrayLen(0) / 3;
        static float Height(ArrayMesh m) => m == null ? 0f : m.GetAabb().Size.Y;

        public override IEnumerable<Step> Run()
        {
            // The bands are quarters of the asset's stackSize, and stackSize defaults to 1 until the catalog
            // wires it -- at which point every amount reads as a full stack and the whole feature silently
            // returns the 3-round mesh. That is what this test caught on its first run, so the setup is here
            // rather than assumed: the game calls RegisterAll on every world load (Main, WorldBuilder), the
            // bare L1 host does not.
            ItemCatalog.RegisterAll();

            // (id, stackSize, tris per round, expected full-pile height in metres)
            var beds = new[]
            {
                (id: 5004, stack: 128, per: 22, label: "5.56 FMJ"),
                (id: 113,  stack: 32,  per: 20, label: "12 gauge buckshot"),
            };

            foreach (var b in beds)
            {
                T.Check($"{b.label}: stackSize is {b.stack}", Assets.find((ushort)b.id)?.stackSize == b.stack);

                var full = WorldItem.MeshForStack(b.id, b.stack);
                T.Check($"{b.label}: full stack is 3 rounds ({b.per * 3} tris)", Tris(full) == b.per * 3);

                var two = WorldItem.MeshForStack(b.id, (int)(b.stack * 0.60f));   // inside [2/4, 3/4)
                var one = WorldItem.MeshForStack(b.id, (int)(b.stack * 0.30f));   // inside [1/4, 2/4)
                T.Check($"{b.label}: 60% of a stack is 2 rounds", Tris(two) == b.per * 2);
                T.Check($"{b.label}: 30% of a stack is 1 round", Tris(one) == b.per);

                // Band edges, where an inclusive/exclusive slip lives.
                T.Check($"{b.label}: exactly 3/4 is 3 rounds", Tris(WorldItem.MeshForStack(b.id, b.stack * 3 / 4)) == b.per * 3);
                T.Check($"{b.label}: one under 3/4 is 2 rounds", Tris(WorldItem.MeshForStack(b.id, b.stack * 3 / 4 - 1)) == b.per * 2);
                T.Check($"{b.label}: exactly 1/2 is 2 rounds", Tris(WorldItem.MeshForStack(b.id, b.stack / 2)) == b.per * 2);
                T.Check($"{b.label}: one under 1/2 is 1 round", Tris(WorldItem.MeshForStack(b.id, b.stack / 2 - 1)) == b.per);

                // The single ejected round -- the case the whole feature exists to serve, and the one that sits
                // below every band strawberry named. It must draw ONE round, never zero: a dropped item that
                // renders nothing is indistinguishable from a crash.
                var single = WorldItem.MeshForStack(b.id, 1);
                T.Check($"{b.label}: an amount of 1 still draws a round", Tris(single) == b.per);

                // HEIGHT: hiding must take the TOP round off, so a 1- or 2-round pile is shorter than the full one.
                float hFull = Height(full), hTwo = Height(two), hOne = Height(one);
                T.Check($"{b.label}: 2 rounds is shorter than 3 ({hTwo:0.0000} < {hFull:0.0000})", hTwo < hFull - 0.0005f);
                T.Check($"{b.label}: 1 round is no taller than 2", hOne <= hTwo + 1e-5f);
                // ...and the two ground rounds are the SAME height as one of them, which is what proves the
                // survivors are the pair lying on the ground rather than an arbitrary two of the three.
                T.Check($"{b.label}: the two survivors both lie on the ground", Mathf.Abs(hOne - hTwo) < 1e-5f);
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
