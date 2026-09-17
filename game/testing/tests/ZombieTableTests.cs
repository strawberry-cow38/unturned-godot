using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // PEI's zombie wardrobe (Spawns/Zombies.dat). The format was derived by inspection -- there is no retail source
    // on this box -- so the thing under test is really "does our reading of those bytes still line up with reality".
    //
    // ⚠ The load-bearing check is the LAST one, not the named ids. Asserting "Police shirt is 223" only proves the
    // first few bytes; a layout drift further in would slide every later table sideways and still leave table 0
    // intact. Checking that EVERY id lands in the slot kind it was read as -- all shirts in slot 0, all pants in 1,
    // and so on across all 19 tables -- is what actually fails when the walk desynchronises, because a misread
    // scatters item kinds rather than shifting them tidily.
    public sealed class ZombieTableTests : GameTest
    {
        public override string Name => "zombie.pei_wardrobe";
        public override int Tier => 0;

        static string PeiRoot()
        {
            string dir = System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR");
            if (string.IsNullOrEmpty(dir)) dir = "/home/ec2-user/unturned";
            return System.IO.Path.Combine(dir, "Maps", "PEI");
        }

        public override IEnumerable<Step> Run()
        {
            string root = PeiRoot();
            string dat = System.IO.Path.Combine(root, "Spawns", "Zombies.dat");
            if (!System.IO.File.Exists(dat))
            {
                // Retail data is not on every machine. Say so rather than passing quietly, which would make this
                // test read green on exactly the box where it verifies nothing.
                T.Check($"SKIPPED: no retail PEI at {dat}", true);
                yield break;
            }

            ZombieTables.Reset();
            ZombieTables.Load(root);

            // Load() refuses a file it cannot walk flush to the end, so a non-zero count IS the format check:
            // a wrong field width desynchronises and the parse is thrown away rather than half-applied.
            T.Check($"the file parsed at all (tables: {ZombieTables.Count})", ZombieTables.Count > 0);
            if (ZombieTables.Count == 0) yield break;
            T.Check($"PEI has 19 tables (got {ZombieTables.Count})", ZombieTables.Count == 19);

            var police = ZombieTables.Get(0);
            T.Check("table 0 is Police", police != null && police.Name == "Police");
            T.Check("Police has 4 slots", police != null && police.Slots.Length == 4);
            T.Check("Police wears police kit (223 shirt / 224 pants / 225 hat)",
                    police != null
                    && System.Array.IndexOf(police.Slots[ZombieTables.SlotShirt].Ids, (ushort)223) >= 0
                    && System.Array.IndexOf(police.Slots[ZombieTables.SlotPants].Ids, (ushort)224) >= 0
                    && System.Array.IndexOf(police.Slots[ZombieTables.SlotHat].Ids, (ushort)225) >= 0);

            // A bare slot is a real, shipped state (Medic has no hat, Special no shirt) -- not an error and not
            // something to "fix" by substituting something. If this ever comes back empty the parse has gone wrong
            // in the other direction: inventing content where the map specifies none.
            int bare = 0, filled = 0;
            for (int t = 0; t < ZombieTables.Count; t++)
                foreach (var s in ZombieTables.Get(t).Slots)
                    if (s.Ids.Length == 0) bare++; else filled++;
            T.Check($"both bare and filled slots exist (bare {bare}, filled {filled})", bare > 0 && filled > 0);

            // THE ONE WITH TEETH. Every id, every table, must be the kind of garment its slot says it is.
            var want = new[] { "shirt", "pants", "hat", null };   // slot 3 is GEAR: vest or mask, so checked by set below
            var gear = new HashSet<string> { "vest", "mask", "backpack", "glasses" };
            var wrong = new List<string>();
            int checkedIds = 0;
            for (int t = 0; t < ZombieTables.Count; t++)
            {
                var tab = ZombieTables.Get(t);
                for (int s = 0; s < tab.Slots.Length && s < 4; s++)
                    foreach (var id in tab.Slots[s].Ids)
                    {
                        var e = ClothingContent.Get(id);
                        checkedIds++;
                        string got = e?.Slot;
                        bool ok = s == 3 ? (got != null && gear.Contains(got)) : got == want[s];
                        if (!ok) wrong.Add($"{tab.Name}.slot{s} id {id} is '{got ?? "not clothing"}'");
                    }
            }
            T.Check($"all {checkedIds} ids are the garment kind their slot claims " +
                    $"({(wrong.Count == 0 ? "none wrong" : string.Join("; ", wrong.GetRange(0, System.Math.Min(4, wrong.Count))))})",
                    wrong.Count == 0);

            // Roll is a pure function of the seed, which is what lets a zombie keep its outfit across demote and
            // re-promote. If this ever goes non-deterministic, zombies change clothes when you turn around.
            uint a = 12345u, b = 12345u;
            int ra = ZombieTables.Roll(police, ZombieTables.SlotShirt, ref a);
            int rb = ZombieTables.Roll(police, ZombieTables.SlotShirt, ref b);
            T.Check($"the same seed rolls the same garment ({ra} == {rb})", ra == rb);

            ZombieTables.Reset();
            yield break;
        }
    }
}
