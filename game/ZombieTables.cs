using Godot;
using System.Collections.Generic;

// PEI'S OWN ZOMBIE WARDROBE -- Spawns/Zombies.dat (strawberry 2026-09-17: "do u have access to the zombie
// clothes table for PEI from the source?"). The map has always shipped one and nothing in this port had ever
// opened it, which is why zombies wore a baked atlas: not a missing feature so much as an unread file.
//
// It pairs with Spawns/Animals.dat, which despite the name is the ZOMBIE spawn point list (EditorSpawns reads
// it as zombies too -- that is the format's own legacy naming, not a mixup here). Every point carries a table
// index as its first byte, and that byte was being skipped with a comment claiming "PEI = one NORMAL zombie
// table". It is not one: across PEI's 1456 points the byte takes 18 distinct values. So a police zombie really
// is meant to be standing outside the police station in police kit, and the data to do it was already on disk.
//
// FORMAT, derived by inspection against the three sibling table files that were already parsed here
// (Items.dat in LootTables, Vehicles.dat and Fauna.dat in EditorSpawns) and then VALIDATED rather than assumed:
//
//     u8  version                     (PEI = 10)
//     u32 unknown
//     u8  tableCount                  (PEI = 19)
//     per table:
//        u32  unknown
//        u8   r, g, b                 (the editor's swatch -- Police is blue, Fire red, Military green)
//        u8   nameLen, name
//        16B  unknown                 (uniform width on every table; nothing here needs it)
//        u8   slotCount               (4 on every PEI table)
//        per slot: f32 chance, u8 idCount, u16 id * idCount
//
// The unknown blocks are skipped honestly rather than guessed at. What makes that safe is the check in Load():
// the parse must land EXACTLY on the end of the file, so a wrong width cannot pass quietly -- it desynchronises
// and the final offset misses. Two independent confirmations that the layout is right, not merely self-consistent:
// every one of PEI's 81 ids resolves in clothing_content.tsv, and each slot's ids are all of ONE kind --
// slot 0 shirts, 1 pants, 2 hats, 3 gear (vests and masks). A misread would scatter those.
namespace UnturnedGodot
{
    public static class ZombieTables
    {
        public const int SlotShirt = 0, SlotPants = 1, SlotHat = 2, SlotGear = 3;

        public struct Slot
        {
            public float Chance;      // 1.0 on most; Police's vest is 0.107, Lighthouse's hat 0.6
            public ushort[] Ids;      // empty = the table leaves this slot bare, which several do
        }

        public sealed class Table
        {
            public string Name;
            public Color Swatch;
            public Slot[] Slots;
        }

        static Table[] _tables = System.Array.Empty<Table>();
        static string _loadedFrom;

        public static int Count => _tables.Length;
        public static bool Loaded => _tables.Length > 0;

        /// <summary>The table a spawn point's index names, or null if there is none -- an out-of-range index, or a
        /// map with no Zombies.dat at all (every generated island). Callers fall back rather than fail.</summary>
        public static Table Get(int index) => (uint)index < (uint)_tables.Length ? _tables[index] : null;

        public static void Reset() { _tables = System.Array.Empty<Table>(); _loadedFrom = null; }

        public static void Load(string mapRoot)
        {
            if (string.IsNullOrEmpty(mapRoot)) return;
            string path = System.IO.Path.Combine(mapRoot, "Spawns", "Zombies.dat");
            if (_loadedFrom == path) return;                    // same map, already parsed
            _tables = System.Array.Empty<Table>();
            _loadedFrom = path;
            if (!System.IO.File.Exists(path)) { Log.Print("[ztables] no Zombies.dat -- zombies fall back to the full wardrobe"); return; }

            var b = System.IO.File.ReadAllBytes(path);
            try
            {
                int o = 0;
                byte ver = b[o++];
                o += 4;                                          // unknown u32
                int n = b[o++];
                var built = new Table[n];
                for (int t = 0; t < n; t++)
                {
                    o += 4;                                      // unknown u32
                    var col = new Color(b[o] / 255f, b[o + 1] / 255f, b[o + 2] / 255f); o += 3;
                    int ln = b[o++];
                    string name = System.Text.Encoding.ASCII.GetString(b, o, ln); o += ln;
                    o += 16;                                     // unknown fixed block
                    int slots = b[o++];
                    var sl = new Slot[slots];
                    for (int s = 0; s < slots; s++)
                    {
                        float chance = System.BitConverter.ToSingle(b, o); o += 4;
                        int c = b[o++];
                        var ids = new ushort[c];
                        for (int i = 0; i < c; i++) { ids[i] = System.BitConverter.ToUInt16(b, o); o += 2; }
                        sl[s] = new Slot { Chance = chance, Ids = ids };
                    }
                    built[t] = new Table { Name = name, Swatch = col, Slots = sl };
                }
                // THE CHECK THAT MAKES THE SKIPPED BLOCKS SAFE. A wrong width desynchronises everything after it,
                // and the giveaway is that the walk does not finish flush with the file. Refusing the whole parse
                // is right: half-decoded tables would dress zombies in whatever the misalignment happened to spell.
                if (o != b.Length)
                {
                    Log.Err($"[ztables] {path}: parsed {o} of {b.Length} bytes (v{ver}, {n} tables) -- layout mismatch, ignoring the file");
                    return;
                }
                _tables = built;
                Log.Print($"[ztables] v{ver}: {n} tables from {path}");
            }
            catch (System.Exception e)
            {
                Log.Err($"[ztables] {path}: {e.GetType().Name} -- ignoring the file");
            }
        }

        /// <summary>Roll one slot: null when the table leaves it bare or the chance does not land. `rand` is the
        /// caller's stream so a zombie's whole outfit comes from ONE seed and survives being demoted and
        /// re-promoted -- otherwise walking away and back changes its clothes.</summary>
        public static int Roll(Table t, int slot, ref uint rand)
        {
            if (t == null || t.Slots == null || (uint)slot >= (uint)t.Slots.Length) return -1;
            var s = t.Slots[slot];
            if (s.Ids == null || s.Ids.Length == 0) return -1;
            rand ^= rand << 13; rand ^= rand >> 17; rand ^= rand << 5;
            if (s.Chance < 1f && (rand % 1000u) / 1000f >= s.Chance) return -1;
            rand ^= rand << 13; rand ^= rand >> 17; rand ^= rand << 5;
            return s.Ids[rand % (uint)s.Ids.Length];
        }
    }
}
