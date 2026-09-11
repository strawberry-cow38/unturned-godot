using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A StorageCrate whose contents are ROLLED from a PEI item drop table (Spawns/Items.dat) when it spawns. Master's
    // loot-rework step 1: a placeable, F-openable crate stocked from a real PEI table -- placed in the editor, tested in SP.
    // (LootTables.Load must have run first -- the SP crate spawner does that with PEI's Items.dat.)
    public partial class LootCrate : StorageCrate
    {
        public int TableIndex = 0;    // which PEI item table to roll
        public int MinItems = 3, MaxItems = 8;

        public static LootCrate Spawn(Node parent, Vector3 pos, int table)
        {
            var c = new LootCrate { TableIndex = table };
            parent.AddChild(c);
            c.GlobalPosition = pos;
            return c;
        }

        public override void _Ready()
        {
            base._Ready();   // StorageCrate: the Storage grid + the crate box + the "crates" group (F to open)
            // DETERMINISTIC, and per crate rather than per session (strawberry: "wire loot to roll on a
            // deterministic seed thats chosen/rolled before pressing play on the map").
            //
            // This used to be `new RandomNumberGenerator()` -- time-seeded by Godot -- for the COUNT, while
            // the ids came from LootTables' own unseeded static. Two independent random streams per crate.
            //
            // The seed is derived from the world seed and WHERE THIS CRATE IS, not drawn from a shared
            // sequence. A shared sequence is deterministic and still wrong: what a crate gets would depend on
            // the order crates were built, which differs between a fresh map load and a save restore. Both
            // the count and the ids now come from this one stream, so a crate's contents are a function of
            // which crate it is.
            var rng = new RandomNumberGenerator { Seed = LootSeed.For(GlobalPosition.X, GlobalPosition.Y, GlobalPosition.Z) };
            int n = rng.RandiRange(MinItems, MaxItems);
            int added = 0;
            for (int i = 0; i < n; i++)
            {
                int id = LootTables.Roll(TableIndex, rng);
                if (id < 0) continue;
                var item = Assets.makeLoot((ushort)id);
                if (item != null) { Add(item); added++; }
            }
            Log.Print($"[loot-crate] table {TableIndex} ({LootTables.TableName(TableIndex)}) -> {added} items");
        }
    }
}
