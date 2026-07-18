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
            var rng = new RandomNumberGenerator();
            int n = rng.RandiRange(MinItems, MaxItems);
            int added = 0;
            for (int i = 0; i < n; i++)
            {
                int id = LootTables.Roll(TableIndex);
                if (id < 0) continue;
                var item = Assets.makeLoot((ushort)id);
                if (item != null) { Add(item); added++; }
            }
            GD.Print($"[loot-crate] table {TableIndex} ({LootTables.TableName(TableIndex)}) -> {added} items");
        }
    }
}
