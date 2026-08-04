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
