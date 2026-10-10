using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Where the undergrowth is, as plain numbers, so a vehicle can be slowed by ploughing through it
    /// (strawberry 2026-10-10: "as well as driving through bushes which slow the vehicle down").
    ///
    /// ⚠⚠ DELIBERATELY NOT COLLIDERS, and that is forced rather than chosen. Bushes, hedges, crops, vines and
    /// cane are WALK-THROUGH props: WorldBuilder.IsWalkThrough gives them no collider at all, on purpose, so you
    /// can walk into a hedge instead of bouncing off it. There is therefore nothing for a ray or a shape query to
    /// hit, and "just give them colliders" would undo that decision AND add thousands of bodies to a world whose
    /// collider budget is already streamed (ColliderBudget) -- to implement a drag effect that needs no contact
    /// response at all. This is the same call ResourceField already made for tree canopies: keep the volume as
    /// numbers, query it in software, leave physics out of it.
    ///
    /// A uniform grid rather than anything cleverer because the query is "what is within a couple of metres of
    /// this point", the data is static after world build, and one dictionary lookup per vehicle at 10 Hz is
    /// already far below the cost the TickHub exists to have removed.</summary>
    public static class BushField
    {
        const float Cell = 8f;   // metres. Comfortably larger than the biggest bush radius + a vehicle half-width,
                                 // so a query only ever has to read the 2x2 cells its sample circle can touch.
        struct Bush { public float X, Z, Y, R; }
        static readonly Dictionary<(int, int), List<Bush>> _cells = new();
        static int _count;

        /// <summary>How many bushes are registered. Test seam, and the thing to check first when the effect
        /// "does not work": an empty field drags nothing and looks identical to a broken multiplier.</summary>
        public static int Count => _count;

        static (int, int) Key(float x, float z) => (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        public static void Clear() { _cells.Clear(); _count = 0; }

        /// <summary>Register one bush. <paramref name="radius"/> is its ground footprint, not its height.</summary>
        public static void Add(Vector3 pos, float radius)
        {
            if (radius <= 0.01f) return;
            var k = Key(pos.X, pos.Z);
            if (!_cells.TryGetValue(k, out var list)) { list = new List<Bush>(); _cells[k] = list; }
            list.Add(new Bush { X = pos.X, Z = pos.Z, Y = pos.Y, R = radius });
            _count++;
        }

        /// <summary>How much extra rolling drag the undergrowth at <paramref name="pos"/> adds, as a multiplier
        /// on rolling resistance. 1.0 = clear ground, so a world with no bushes (or before one is built) changes
        /// nothing by construction.
        ///
        /// <paramref name="halfWidth"/> is the vehicle's half-width: a quad slips between bushes a bus ploughs
        /// through, which falls out of the geometry rather than needing a per-hull dial.
        ///
        /// ⚠ HEIGHT IS CHECKED. Without it a bridge deck or a multi-storey car park would be dragged by whatever
        /// is growing on the ground beneath it -- the grid is 2D and the world is not.</summary>
        public static float DragAt(Vector3 pos, float halfWidth)
        {
            if (_count == 0) return 1f;
            float reach = halfWidth + MaxBushRadius;
            int x0 = Mathf.FloorToInt((pos.X - reach) / Cell), x1 = Mathf.FloorToInt((pos.X + reach) / Cell);
            int z0 = Mathf.FloorToInt((pos.Z - reach) / Cell), z1 = Mathf.FloorToInt((pos.Z + reach) / Cell);
            float sum = 0f;
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!_cells.TryGetValue((cx, cz), out var list)) continue;
                    foreach (var b in list)
                    {
                        if (pos.Y - b.Y < -1.5f || pos.Y - b.Y > BushHeight) continue;   // driving under/over it, not through it
                        float dx = pos.X - b.X, dz = pos.Z - b.Z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz), span = b.R + halfWidth;
                        if (d >= span) continue;
                        // Overlap-weighted, so clipping a hedge's edge costs a fraction of driving down the
                        // middle of it. Linear in penetration depth: a bush is springy, not a kerb.
                        sum += (1f - d / span);
                    }
                }
            return 1f + Mathf.Min(sum, MaxStack) * DragPerBush;
        }

        /// <summary>The biggest footprint any registered bush has, so DragAt knows how far to look. Tracked
        /// rather than assumed: a hedge row is wider than a shrub and a fixed guess would miss it.</summary>
        public static float MaxBushRadius { get; private set; } = 1.2f;
        public static void NoteRadius(float r) { if (r > MaxBushRadius) MaxBushRadius = r; }

        /// <summary>Rolling-drag added per bush fully driven through. 1.35 is a deliberate design number, not a
        /// measured one -- there is no Crr for "a hedge" -- chosen so a single shrub is a noticeable tug and a
        /// hedgerow is a wall of resistance without being an invisible brake wall.</summary>
        public const float DragPerBush = 1.35f;
        /// <summary>Cap on how many bushes can pile onto one vehicle at once. Without it a dense thicket
        /// multiplies into a dead stop, which reads as hitting an invisible collider -- the exact thing keeping
        /// these out of physics was meant to avoid.</summary>
        public const float MaxStack = 3f;
        /// <summary>How far above a bush's base it still counts as being driven through.</summary>
        public const float BushHeight = 2.5f;
    }
}
