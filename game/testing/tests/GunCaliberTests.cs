using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // PER-WEAPON REAL CALIBER (strawberry: "could u add a per weapon caliber field. use the wiki's 'trivia' section to
    // see the guns each one is based off. use best judgement. you should alter/adjust mag sizes/ROF too").
    //
    // Caliber_Name is a SECOND axis alongside the .dat's existing integer Caliber, not a replacement for it, and the
    // whole value of the field is that the two disagree in both directions:
    //   - three groups, one cartridge: schofield(5) / nykorev(10) / snayperskya(11) are all 7.62x54mmR, because a
    //     Mosin stripper clip, a PKM belt and an SVD box mag do not interchange.
    //   - one group, two cartridges: group 1 holds the 5.56 rifles AND the .300 BLK honeybadger, which is REAL, since
    //     .300 BLK is 5.56 necked up and feeds from STANAG mags.
    // A test that asserted "same group => same cartridge" would therefore be wrong in a way that looks principled, so
    // what is pinned below is coverage and the two directional facts, not an equivalence.
    //
    // The gaps this closed were all silent. augewehr and nightraider shipped with NO Caliber key at all, so it parsed
    // to 0 and no magazine could ever match them -- a gun that equips, aims and dry-fires looks exactly like a gun
    // that works until you try to reload it. Their default Magazine 123 was not in the catalog either. sawed_off had
    // no Pellets key, so it defaulted to 1: a shotgun firing a single ray still hits, still kills, still feels like a
    // weapon, and is simply not a shotgun.
    public sealed class GunCaliberTests : GameTest
    {
        public override string Name => "gun.caliber_field";

        static List<string> PortedGuns(string dir)
        {
            var l = new List<string>();
            foreach (var dat in System.IO.Directory.GetFiles(dir, "*.dat"))
            {
                string n = System.IO.Path.GetFileNameWithoutExtension(dat);
                if (System.IO.File.Exists(dir + n + "_gun.txt")) l.Add(n);
            }
            l.Sort();
            return l;
        }

        static GunDef Def(string dir, string gun) => GunDef.FromDatText(System.IO.File.ReadAllText(dir + gun + ".dat"));

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var guns = PortedGuns(dir);
            T.Check($"the content ships guns to check ({guns.Count})", guns.Count >= 30);

            // 1. EVERY ported gun carries a cartridge and a real-world basis. An untagged gun is the failure mode that
            //    matters: it reads as "no caliber" rather than as "nobody has done this one yet".
            var untagged = new List<string>();
            var noBasis = new List<string>();
            foreach (var g in guns)
            {
                var d = Def(dir, g);
                if (string.IsNullOrWhiteSpace(d.CaliberName)) untagged.Add(g);
                if (string.IsNullOrWhiteSpace(d.RealWeapon)) noBasis.Add(g);
            }
            T.Check($"every ported gun has a Caliber_Name ({(untagged.Count == 0 ? "all" : string.Join(",", untagged))})", untagged.Count == 0);
            T.Check($"...and a Real_Weapon it was sourced from ({(noBasis.Count == 0 ? "all" : string.Join(",", noBasis))})", noBasis.Count == 0);

            // 2. One cartridge across three DIFFERENT magazine groups -- the fact that makes a separate field necessary.
            var mosin = Def(dir, "schofield");
            var pkm = Def(dir, "nykorev");
            var svd = Def(dir, "snayperskya");
            T.Check($"schofield/nykorev/snayperskya share a cartridge ({mosin.CaliberName})",
                mosin.CaliberName == "7.62x54mmR" && pkm.CaliberName == "7.62x54mmR" && svd.CaliberName == "7.62x54mmR");
            T.Check($"...while sitting in three different mag groups ({mosin.Caliber}/{pkm.Caliber}/{svd.Caliber})",
                mosin.Caliber != pkm.Caliber && pkm.Caliber != svd.Caliber && mosin.Caliber != svd.Caliber);

            // 3. The other direction: one group, two cartridges, and that is correct.
            var eagle = Def(dir, "eaglefire");
            var badger = Def(dir, "honeybadger");
            T.Check($"eaglefire and honeybadger share mag group {eagle.Caliber} (STANAG)", eagle.Caliber == badger.Caliber);
            T.Check($"...on different cartridges ({eagle.CaliberName} vs {badger.CaliberName})", eagle.CaliberName != badger.CaliberName);

            // 4. The two guns that could never load a magazine.
            foreach (var g in new[] { "augewehr", "nightraider" })
            {
                var d = Def(dir, g);
                T.Check($"{g} has a real mag group ({d.Caliber})", d.Caliber > 0);
                T.Check($"...and a magazine that exists in the catalog (id {d.MagazineId})", Assets.find((ushort)d.MagazineId) != null);
            }

            // 5. sawed_off is a shotgun. It is crafted FROM the masterkey, so it fires the masterkey's shell.
            var mk = Def(dir, "masterkey");
            var so = Def(dir, "sawed_off");
            T.Check($"sawed_off fires shot, not a single ray ({so.Pellets} pellets)", so.Pellets > 1);
            T.Check($"...the same shell as the masterkey it is sawn from ({mk.Pellets} vs {so.Pellets}, {mk.CaliberName})",
                so.Pellets == mk.Pellets && so.CaliberName == mk.CaliberName);

            // 5b. THE GROUP-1 SPLIT (master: "the stanag mag group needs to be split up anyway as the aug and g36
            //     dont take stanag mags ... their own unique magazines that arent cross compatible with any other
            //     weapons").
            ItemCatalog.RegisterAll();
            var grp = new Dictionary<int, List<string>>();
            foreach (var g in guns)
            {
                int c = Def(dir, g).Caliber;
                if (!grp.TryGetValue(c, out var l)) grp[c] = l = new List<string>();
                l.Add(g);
            }
            grp.TryGetValue(1, out var stanag);
            stanag ??= new List<string>();
            stanag.Sort();
            // Group 1 is now STANAG-and-only-STANAG: two 5.56 rifles plus the .300 BLK that genuinely feeds from one.
            T.Check($"group 1 is real STANAG only ({string.Join(",", stanag)})",
                stanag.Count == 3 && stanag.Contains("eaglefire") && stanag.Contains("maplestrike") && stanag.Contains("honeybadger"));
            foreach (var g in new[] { "augewehr", "nightraider", "heartbreaker" })
                T.Check($"{g} is out of the STANAG group (now {Def(dir, g).Caliber})", !stanag.Contains(g));

            // The AUG and G36 groups must contain exactly one gun each -- "cross compatible with any other weapons"
            // is the requirement, so a count of 1 IS the assertion, not a proxy for it.
            foreach (var g in new[] { "augewehr", "nightraider" })
            {
                int c = Def(dir, g).Caliber;
                T.Check($"{g}'s group {c} is his alone ({string.Join(",", grp[c])})", grp[c].Count == 1);
            }

            // SCAR-H and M39: CLONES that do not interchange (master's correction -- "split scar and m39 mags into
            // clones of eachother that arent compatible. realism."). Same cartridge, same capacity, same mesh, and
            // deliberately different groups. Asserting both halves matters: identical-in-every-way is the easy half
            // to get right and the incompatibility is the half a "share the mag" refactor would quietly undo.
            var scar = Def(dir, "heartbreaker");
            var m39 = Def(dir, "sabertooth");
            var scarMag = Assets.find((ushort)scar.MagazineId);
            var m39Mag = Assets.find((ushort)m39.MagazineId);
            T.Check($"SCAR + M39 are on the same cartridge ({scar.CaliberName})", scar.CaliberName == m39.CaliberName);
            T.Check($"...their mags are clones (cap {scarMag?.magCapacity} vs {m39Mag?.magCapacity}, round {scarMag?.magRound})",
                scarMag != null && m39Mag != null && scarMag.magCapacity == m39Mag.magCapacity && scarMag.magRound == m39Mag.magRound);
            T.Check($"...but do NOT interchange (grp {scar.Caliber} vs {m39.Caliber}, mag {scar.MagazineId} vs {m39.MagazineId})",
                scar.Caliber != m39.Caliber && scar.MagazineId != m39.MagazineId);
            T.Check($"...and neither mag fits the other's rifle",
                scarMag.magCaliber != m39.Caliber && m39Mag.magCaliber != scar.Caliber);

            // 5c. Every gun touched here points at a magazine that is ACTUALLY a magazine and actually fits it.
            //     A TSV magazine arrives with magCapacity 0, so it has the right name and icon and silently is not a
            //     magazine -- which is exactly how the sabertooth shipped. Capacity>0 is the real test, not non-null.
            foreach (var g in new[] { "eaglefire", "maplestrike", "honeybadger", "augewehr", "nightraider", "heartbreaker", "sabertooth" })
            {
                var d = Def(dir, g);
                var a = Assets.find((ushort)d.MagazineId);
                T.Check($"{g}'s mag {d.MagazineId} is a functioning magazine (cap {a?.magCapacity ?? -1})", a != null && a.IsMagazine);
                T.Check($"...and fits it (mag cal {a?.magCaliber ?? -1} vs gun {d.Caliber})", a != null && a.magCaliber == d.Caliber);
            }

            // 5d. The .300 BLK flag: same group, same mesh, different round. Without both values present the flag
            //     can never disagree with anything and proves nothing.
            var mil = Assets.find(6);
            var blk = Assets.find(9142);
            T.Check($"the .300 mag shares STANAG group 1 ({blk?.magCaliber})", blk != null && blk.magCaliber == 1 && mil.magCaliber == 1);
            T.Check($"...but is flagged a different round ({mil?.magRound} vs {blk?.magRound})",
                !string.IsNullOrEmpty(mil?.magRound) && !string.IsNullOrEmpty(blk?.magRound) && mil.magRound != blk.magRound);
            T.Check($"honeybadger defaults to the .300 mag ({Def(dir, "honeybadger").MagazineId})",
                Def(dir, "honeybadger").MagazineId == 9142);

            // 5e. TRACER WIDTH (master: "scale tracers based on ammo caliber. ie .22 smol 50bmg big", "make pellets
            //     extra small, each pellet gets a tracer").
            //     The load-bearing check is COVERAGE, not the ordering: TracerScale falls back to 1.0 for anything it
            //     does not know, and 1.0 is also the legitimate value for 5.56 -- so a cartridge renamed in a .dat
            //     would silently draw an eaglefire-sized tracer and look completely normal. Everything ballistic must
            //     be explicitly mapped; arrows/bolts/rockets are deliberately not.
            var unmapped = new List<string>();
            foreach (var g in guns)
            {
                string c = Def(dir, g).CaliberName;
                if (c == "Arrow" || c == "Bolt" || c == "Rocket") continue;   // no ballistic tracer by design
                if (!GunDef.TracerScales.ContainsKey(c)) unmapped.Add($"{g}={c}");
            }
            T.Check($"every ballistic cartridge has a tracer width ({(unmapped.Count == 0 ? "all" : string.Join(",", unmapped))})", unmapped.Count == 0);

            float lr22 = GunDef.TracerScale(".22 LR"), nato556 = GunDef.TracerScale("5.56x45mm NATO"), bmg50 = GunDef.TracerScale(".50 BMG");
            T.Check($".22 < 5.56 < .50 BMG ({lr22} / {nato556} / {bmg50})", lr22 < nato556 && nato556 < bmg50);
            // Buckshot is the smallest thing on screen: one shot spawns Pellets bullets, each drawing its own tracer.
            float ga12 = GunDef.TracerScale("12 Gauge"), ga20 = GunDef.TracerScale("20 Gauge");
            T.Check($"buckshot is thinner than a .22 ({ga12} / {ga20} vs {lr22})", ga12 < lr22 && ga20 < lr22);
            T.Check($"...and the masterkey really does fire {mk.Pellets} of them per shot", mk.Pellets > 1);

            // 6. Firerate stays a positive tick count after the ROF pass -- a zero or negative here divides by zero in
            //    the shot cooldown, and the retune touched nine guns.
            var badFr = new List<string>();
            foreach (var g in guns) { var d = Def(dir, g); if (d.Firerate <= 0) badFr.Add($"{g}={d.Firerate}"); }
            T.Check($"every firerate is a positive tick count ({(badFr.Count == 0 ? "all" : string.Join(",", badFr))})", badFr.Count == 0);

            // 7. Mag sizes stayed sane through the resize (nothing zeroed, nothing belt-fed by accident).
            var badMag = new List<string>();
            foreach (var g in guns) { var d = Def(dir, g); if (d.AmmoMax < 1 || d.AmmoMax > 250) badMag.Add($"{g}={d.AmmoMax}"); }
            T.Check($"every magazine is 1..250 ({(badMag.Count == 0 ? "all" : string.Join(",", badMag))})", badMag.Count == 0);

            yield break;
        }
    }
}
