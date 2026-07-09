using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Bounded stand-in for Unturned's loot system. The real thing is map-data-driven: LevelItems places
    // ItemSpawnpoints that each reference a SpawnAsset (a weighted table of item ids / sub-tables, rolled per spawn).
    // We don't have map spawn data here, so this scatters N WorldItems around the player from a simple weighted table
    // (commoner items weigh more, like a spawn table's rarity spread) so there's loot to FIND in the world.
    public partial class LootSpawner : Node3D
    {
        // (item id, weight) -- a small stand-in spawn table
        static readonly (ushort id, int w)[] Table =
        {
            (13, 30), (14, 30), (95, 20),   // canned beans / bottled water / bandage -- common
            (4, 8), (363, 5),               // eaglefire / maplestrike -- guns, rarer
            (15, 3),                         // medkit -- rare
        };

        public int Count = 14;
        public float Radius = 26f;
        readonly RandomNumberGenerator _rng = new();

        public override void _Ready()
        {
            _rng.Randomize();
            int total = 0; foreach (var e in Table) total += e.w;
            for (int i = 0; i < Count; i++)
            {
                ushort id = Roll(total);
                float ang = _rng.Randf() * Mathf.Tau;
                float r = 3f + Mathf.Sqrt(_rng.Randf()) * Radius;      // uniform-ish over a ring around the player
                WorldItem.Spawn(this, new Item(id), new Vector3(Mathf.Cos(ang) * r, 0.1f, Mathf.Sin(ang) * r));
            }
        }

        ushort Roll(int total)
        {
            int r = _rng.RandiRange(0, total - 1), acc = 0;
            foreach (var e in Table) { acc += e.w; if (r < acc) return e.id; }
            return Table[0].id;
        }
    }
}
