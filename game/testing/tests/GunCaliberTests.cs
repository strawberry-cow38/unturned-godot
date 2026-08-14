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
            // FIRST, before anything reads Assets: this suite used to rely on some earlier gun test having registered
            // the catalog, so it passed in a `gun.*` run and failed standalone. A test that needs a neighbour to have
            // run first is not a test of anything.
            ItemCatalog.RegisterAll();
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var guns = PortedGuns(dir);
            T.Check($"the content ships guns to check ({guns.Count})", guns.Count >= 30);

            // 1. A REAL-WORLD BASIS WHERE ONE EXISTS -- and nothing invented where it does not.
            //
            // This originally demanded a Caliber_Name AND a Real_Weapon from every ported gun, and that requirement is
            // what manufactured the data it was meant to protect: a gun the game describes as "Russian minigun
            // chambered in Hell's Fury ammunition" cannot be tagged truthfully, so it got tagged "9x19mm Parabellum /
            // Heckler & Koch MP5K" -- wrong nation, wrong class, wrong cartridge, and green. A check that demands data
            // for things which have none gets paid in fabrication.
            //
            // The attributions are now SOURCED (master supplied them: peacemaker=P90, viper=MP5, vonya=Saiga,
            // bulldog=Uzi, card=PPSh, scalar=Vector, bane=AA-12, fusilaut=FAMAS, teklowvka=TEC-9, kryzkarek=Makarov,
            // ekho=M200) rather than inferred from the mesh, and ten of the eleven corroborate the game's own stated
            // nationality -- the TEC-9 included, which is why a "Swedish pistol" is right: Interdynamic AB, Stockholm.
            //
            // TWO AXES, and they move independently, which is the thing this check now encodes:
            //   Real_Weapon  -- the firearm the MODEL is based on. Can exist even when the ammo is invented.
            //   Caliber_Name -- the REAL cartridge. Derived from Real_Weapon where there is one; absent otherwise.
            // The guns with no real basis were required to stay EMPTY, so that the next pass could not re-invent them.
            // Emptiness has stopped being the right guard: master authored values for all four on 2026-08-14 ("just
            // make it 7.62x51. its fine" / "nailgun and pbg are their respective new calibers" / "shadowstalker mk2
            // fictional too"). Those are DESIGN DECISIONS about what the gun fires, not claims about what Nelson meant,
            // and an empty-check would now delete them on sight.
            //
            // So the guard is PINNED VALUES instead, which has more teeth than emptiness ever did: an exact match
            // fails both on a stripped annotation AND on a re-invented one. The original failure -- fury tagged
            // "9x19mm Parabellum / Heckler & Koch MP5K" to satisfy a check -- fails here loudly rather than sitting
            // green. Change one only when master says so, and change it here in the same commit.
            //
            // None of the four names a real firearm MODEL, which is the line that matters. "Minigun" is a category,
            // "Nail gun" and "Paintball marker" are tools, and the mk2 is explicitly marked fictional like the mk1.
            // A specific manufacturer and model appearing in this table is the tell that someone inferred it.
            //
            // The nailgun/paintballgun entries also settle the older THIRD case: a real model basis and no real
            // cartridge, because the gun throws an OBJECT rather than firing a bullet -- decided by what the MAGAZINE
            // holds ("Designed to fit 20 nails", "...35 paintballs"), never by the ammo's flavour name. `card` is a
            // PPSh-41 firing "Calling Card" ammo whose magazine holds "71 rounds", and it was filed as a card-thrower
            // on the strength of the name alone until master asked what I was talking about. Hell's Fury, Vonya and
            // Calling Card are all just Unturned naming its ammo types. They now carry a caliber, but a made-up one
            // that names no cartridge -- which is the distinction the old branch was reaching for.
            var authored = new System.Collections.Generic.Dictionary<string, (string Caliber, string Weapon)>
            {
                ["fury"]             = ("7.62x51mm NATO", "Minigun"),                  // Russian minigun, "Hell's Fury"
                ["nailgun"]          = ("Nail",           "Nail gun"),                 // construction tool
                ["paintballgun"]     = ("Paintball",      "Paintball marker"),         // painting tool
                ["shadowstalkermk2"] = ("Railgun Slug",   "Rorsch MK2 (fictional)"),   // prototype railgun, like the mk1
            };
            var untagged = new List<string>();
            var drifted = new List<string>();
            foreach (var g in guns)
            {
                var d = Def(dir, g);
                if (authored.TryGetValue(g, out var want))
                {
                    if (d.CaliberName != want.Caliber || d.RealWeapon != want.Weapon)
                        drifted.Add($"{g}={d.CaliberName ?? "-"}/{d.RealWeapon ?? "-"} (want {want.Caliber}/{want.Weapon})");
                }
                else if (string.IsNullOrWhiteSpace(d.CaliberName)) untagged.Add(g);
            }
            T.Check($"every gun with a real-world basis carries its cartridge ({(untagged.Count == 0 ? "all" : string.Join(",", untagged))})", untagged.Count == 0);
            T.Check($"...and the ones with no real basis carry exactly what master authored ({(drifted.Count == 0 ? "all four" : string.Join("; ", drifted))})", drifted.Count == 0);

            // 1b. REAL rate of fire and magazine size (master: "rebalance all the new guns to have real ROF and
            //     mag sizes"). This is a DELIBERATE DIVERGENCE FROM RETAIL and needs pinning, because every one of
            //     these matched retail exactly before the rebalance -- so the next person who re-ports a gun from the
            //     bundles reverts it and nothing notices. That is the failure this file keeps finding.
            //
            //     Firerate is TICKS BETWEEN SHOTS at 50 Hz, so RPM = 3000/ticks, and the ladder is coarse where these
            //     guns live: 1500 / 1000 / 750 / 600 / 500. 900 rpm is not expressible at all -- the P90 and the PPSh
            //     both land on 3 ticks (1000) because it is nearer than 4 (750). Asserted as TICKS rather than as an
            //     rpm with a tolerance, so the quantisation is visible instead of hidden behind a rounding window.
            //
            //     ROF is rebalanced ONLY where the real weapon has a cyclic rate. A semi-auto or bolt gun is
            //     trigger-limited, so retail's number there is a click-rate allowance rather than a wrong cyclic
            //     figure; those keep it and only their magazines move.
            var rof = new (string Gun, int Ticks, int Mag)[]
            {
                ("peacemaker",  3, 50),   // FN P90        900 rpm -> 1000 (nearest)
                ("viper",       4, 30),   // H&K MP5       800 -> 750
                ("bulldog",     5, 32),   // IMI Uzi       600 exact  (retail had 1500 rpm and a 45 mag)
                ("card",        3, 71),   // PPSh-41       900 -> 1000; 71-round drum is retail's already
                ("scalar",      3, 30),   // KRISS Vector 1200 -> 1000
                ("bane",       10, 20),   // AA-12         300 exact
                ("fusilaut",    3, 25),   // FAMAS F1     1000 exact
                ("mp40",        6, 32),   // MP 40         500 exact -- retail was already right
                ("swissgewehr", 4, 30),   // SIG SG 550    700 -> 750
            };
            foreach (var r in rof)
            {
                var d = Def(dir, r.Gun);
                T.Check($"{r.Gun} fires at its real cyclic rate ({d.Firerate} ticks = {3000f / Mathf.Max(d.Firerate, 1):0} rpm)", d.Firerate == r.Ticks);
                T.Check($"...on a real magazine ({d.AmmoMax})", d.AmmoMax == r.Mag);
            }
            // The trigger-limited ones: magazine only, and the rate deliberately LEFT as retail had it.
            foreach (var (g, mag) in new[] { ("vonya", 8), ("teklowvka", 32), ("kryzkarek", 12), ("luger", 8), ("ekho", 7) })
                T.Check($"{g} carries its real magazine ({Def(dir, g).AmmoMax})", Def(dir, g).AmmoMax == mag);

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
            // Group 1 is STANAG-and-only-STANAG: three 5.56 rifles (eaglefire, maplestrike, and the swissgewehr --
            // SG 550 in 5.56, retail puts it AND its own mag 1490 in caliber 1) plus the .300 BLK that genuinely feeds
            // from a STANAG mag. swissgewehr's real-life proprietary mags live in Caliber_Name/Real_Weapon, not here:
            // Caliber is the retail gameplay axis and this is a 1:1 base (tinyclaw verified vs the bundles).
            T.Check($"group 1 is real STANAG only ({string.Join(",", stanag)})",
                stanag.Count == 4 && stanag.Contains("eaglefire") && stanag.Contains("maplestrike")
                && stanag.Contains("honeybadger") && stanag.Contains("swissgewehr"));
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
            foreach (var g in new[] { "eaglefire", "maplestrike", "honeybadger", "augewehr", "nightraider", "heartbreaker", "sabertooth",
                                      "grizzly", "ekho" })   // both were inert until the .50 share; they belong in this net now
            {
                var d = Def(dir, g);
                var a = Assets.find((ushort)d.MagazineId);
                T.Check($"{g}'s mag {d.MagazineId} is a functioning magazine (cap {a?.magCapacity ?? -1})", a != null && a.IsMagazine);
                T.Check($"...and fits it (mag cal {a?.magCaliber ?? -1} vs gun {d.Caliber})", a != null && a.magCaliber == d.Caliber);
            }

            // 5c-ii. ONE ROUND, ONE GROUP: the .50 pair (strawberry: "possible to make the ekho take .50? weird to
            //     have a proprietary ammo"). The point of the change is INTERCHANGE, so same-cartridge is not enough
            //     to assert -- 7.62x54mmR is shared by three guns that cannot swap a single magazine between them.
            //     What has to hold is the group, and it has to hold in the direction a player would notice: each
            //     gun's own magazine seats in the OTHER gun.
            var grz = Def(dir, "grizzly");
            var ekh = Def(dir, "ekho");
            T.Check($"grizzly and ekho share the .50 cartridge ({grz.CaliberName} / {ekh.CaliberName})",
                grz.CaliberName == ".50 BMG" && ekh.CaliberName == ".50 BMG");
            T.Check($"...and the SAME magazine group, so the ammo is not proprietary ({grz.Caliber} / {ekh.Caliber})",
                grz.Caliber == ekh.Caliber);
            var grzMag = Assets.find((ushort)grz.MagazineId);
            var ekhMag = Assets.find((ushort)ekh.MagazineId);
            T.Check($"the grizzly's own mag loads an ekho (cal {grzMag?.magCaliber ?? -1} vs {ekh.Caliber})",
                grzMag != null && grzMag.IsMagazine && grzMag.magCaliber == ekh.Caliber);
            T.Check($"...and the ekho's loads a grizzly (cal {ekhMag?.magCaliber ?? -1} vs {grz.Caliber})",
                ekhMag != null && ekhMag.IsMagazine && ekhMag.magCaliber == grz.Caliber);
            // Different BOXES of the same round -- an M82's 10 and an M200's 7. Equal capacities here would mean
            // someone collapsed them into one item rather than sharing a cartridge.
            T.Check($"...while staying different magazines ({grzMag?.magCapacity ?? -1} vs {ekhMag?.magCapacity ?? -1} rounds)",
                grzMag != null && ekhMag != null && grzMag.magCapacity == 10 && ekhMag.magCapacity == 7);

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
                if (string.IsNullOrWhiteSpace(c)) continue;   // fictional ammo -> no real cartridge -> GunDef.TracerScale defaults it (and ContainsKey(null) THROWS)
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

            // 5f. Cobra is SEMI-ONLY, and Real_Weapon is the Glock 17 rather than the 18C.
            //     The wiki trivia offers BOTH ("based on a Glock 18C with an olive drab frame" AND "based on the
            //     Glock 17"); this port takes the 17, on master's evidence rather than the page's: the cobra is POLICE
            //     loot, and Canadian police do not carry select-fire Glocks. So semi is not a divergence from the real
            //     weapon to be defended -- it IS the real weapon, and the 18C attribution was the error.
            //     Full-auto is planned as a craftable conversion (master), which is exactly what an auto sear is in
            //     reality, so Firerate stays at 1000 rpm: unreachable by human clicking on a semi, and already the
            //     right cyclic rate for the converted gun when that lands.
            var cob = Def(dir, "cobra");
            T.Check($"cobra is semi-only -- police-issue Glock 17, not an 18C (auto={cob.HasAuto}, burst={cob.BurstCount})",
                !cob.HasAuto && cob.BurstCount == 0);
            T.Check($"...and still has a fire mode at all (semi={cob.HasSemi})", cob.HasSemi);
            T.Check($"...recorded as the gun it actually is ({cob.RealWeapon})", cob.RealWeapon == "Glock 17");

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
