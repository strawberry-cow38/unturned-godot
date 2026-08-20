using Godot;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // EVERY GUN'S MAGAZINE MUST ACTUALLY BE A MAGAZINE (strawberry: "go through and make sure every gun works
    // with attachments, every gun remembers attachments, ammo count etc etc").
    //
    // THE BUG THIS EXISTS TO PROVE. ItemCatalog.LoadCatalogFile() reads items_catalog.tsv and sets id, name,
    // type, rarity, size and description -- but NOT magCapacity and NOT magCaliber. ItemAsset.IsMagazine is
    // literally `magCapacity > 0`, so every tsv-loaded magazine is silently not a magazine. UsesMagItem then
    // goes false and PlayerController's reload falls through to `else Ammo = max`: a full magazine out of thin
    // air, with no item consumed. Only the guns whose magazine is hard-coded in ItemCatalog with magCap+magCal
    // actually feed from an item.
    //
    // This is NOT a new discovery -- gun.avenger_usp_45 already documents the exact shape ("an inert TSV
    // magazine has the right name, the right icon, magCapacity 0, and is silently not a magazine, so reloads
    // fell through to a free top-up and no magazine was ever consumed. That is the exact shape the sabertooth
    // shipped with"). It was fixed for those two guns by hard-coding them, and never swept.
    //
    // WHY THE SUITE NEVER CAUGHT IT, which is the part worth keeping: gun.mag_reload is 73 checks and every one
    // of them is on the MAPLESTRIKE -- one of the six guns that work. A reload test that only ever reloads a
    // working gun cannot report a broken reload. Same blind spot that hid the hmg's missing Caliber_Name from
    // gun.caliber_field: a coverage count sees a wrong declaration, never an absent one.
    public sealed class MagazineRealityTests : GameTest
    {
        public override string Name => "gun.magazine_reality";

        static GunDef Def(string dir, string gun) => GunDef.FromDatText(System.IO.File.ReadAllText(dir + gun + ".dat"));

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

        public override IEnumerable<Step> Run()
        {
            // REGISTER THE CATALOG FIRST. Without this Assets is EMPTY, every magazine looks inert, and the
            // test reports "52 inert" whether or not the bug exists -- which is precisely what the first run of
            // this file did. The four controls below caught it: they are guns whose magazines ARE hard-coded and
            // must always be real, so all four failing meant the harness was broken, not the content. A run
            // inside the full suite would have hidden it, because an earlier test calls RegisterAll and leaves
            // it loaded -- the bug only shows under --only, which is how this file is meant to be run.
            ItemCatalog.RegisterAll();

            string dir = ProjectSettings.GlobalizePath("res://content/");
            var guns = PortedGuns(dir);
            T.Check($"found the ported gun set ({guns.Count} guns)", guns.Count > 40);

            var inert = new List<string>();
            var mismatched = new List<string>();
            var ok = new List<string>();

            foreach (var g in guns)
            {
                var def = Def(dir, g);
                if (def.ShellReload) continue;            // tube-fed: loose rounds one at a time, not a mag item
                if (def.MagazineId <= 0) continue;        // declares no magazine at all -- a separate question
                var mag = Assets.find((ushort)def.MagazineId);
                // A GUN THAT FEEDS LOOSE AMMO IS NOT MAG-FED, whatever its .dat's Magazine key points at. The
                // shotguns point theirs at the buckshot item, and master moved the ace onto loose .44 outright
                // ("ace clip shouldnt exist"). Skipping on ShellReload alone was too narrow -- that only catches
                // the one-at-a-time guns, and the ace/quadbarrel/sawed-off fill in a single reload. The honest
                // condition is what the ITEM is: isAmmo means loose rounds, and PlayerController agrees -- it
                // tests UsesShells BEFORE UsesMagItem, so these never reach the magazine path at all.
                if (mag != null && mag.isAmmo) continue;
                if (mag == null || !mag.IsMagazine) { inert.Add($"{g}(mag {def.MagazineId})"); continue; }
                if (mag.magCaliber != def.Caliber) { mismatched.Add($"{g}(gun cal {def.Caliber} vs mag {mag.magCaliber})"); continue; }
                ok.Add(g);
            }

            // THE HEADLINE. Every mag-fed gun must feed from a real magazine item, or its reload is free.
            T.Check($"every mag-fed gun has a REAL magazine ({ok.Count} real, {inert.Count} inert) "
                    + (inert.Count > 0 ? "-- inert: " + string.Join(", ", inert.Take(12)) + (inert.Count > 12 ? $" +{inert.Count - 12} more" : "") : ""),
                    inert.Count == 0);

            // ...and it must be a magazine that FITS. A real mag on the wrong caliber group still cannot be found
            // by FindBestMag, which is the same symptom by a different route.
            T.Check($"every real magazine matches its gun's caliber group ({mismatched.Count} mismatched)"
                    + (mismatched.Count > 0 ? ": " + string.Join(", ", mismatched.Take(8)) : ""),
                    mismatched.Count == 0);

            // THE TWO GUNS MOVED OFF MAGAZINES ENTIRELY. gun.magazine_reality passing says every mag-fed gun has a
            // real magazine -- it says NOTHING about whether these two actually feed loose rounds, because they are
            // SKIPPED above. Skipping a gun and verifying a gun look identical in a pass count, so the feed wiring
            // is asserted here explicitly or it is not asserted at all.
            //
            // This is wiring, not behaviour: it checks that UsesShells CAN resolve (an isAmmo item exists at the
            // gun's caliber, which is exactly what ShellAsset looks up) and that ShellReload reads the intended
            // way. A full behavioural test would need a player, an inventory and a live reload; that is worth
            // having and is not what this is.
            var aceDef = Def(dir, "ace");
            var aceAmmo = Assets.find((ushort)aceDef.MagazineId);
            T.Check($"ace: its item is LOOSE AMMO, not a clip ({aceAmmo?.itemName}, isAmmo={aceAmmo?.isAmmo})",
                    aceAmmo != null && aceAmmo.isAmmo);
            T.Check($"ace: the ammo's caliber matches so ShellAsset can find it (ammo {aceAmmo?.magCaliber} vs gun {aceDef.Caliber})",
                    aceAmmo != null && aceAmmo.magCaliber == aceDef.Caliber && aceDef.Caliber > 0);
            T.Check($"ace: fills the whole cylinder in ONE reload, not round-by-round (ShellReload={aceDef.ShellReload}, Action={aceDef.Action})",
                    !aceDef.ShellReload);

            var mosinDef = Def(dir, "schofield");
            var mosinAmmo = Assets.find((ushort)mosinDef.MagazineId);
            T.Check($"mosin: its item is LOOSE AMMO ({mosinAmmo?.itemName}, isAmmo={mosinAmmo?.isAmmo})",
                    mosinAmmo != null && mosinAmmo.isAmmo);
            T.Check($"mosin: reloads ONE ROUND AT A TIME ({mosinDef.ShellReload}) -- and via the Shell_Reload key, since Action is {mosinDef.Action}, not Pump",
                    mosinDef.ShellReload && mosinDef.Action != "Pump");

            // the three homemade wood bolt rifles feed the SAME way -- their own 5.56 loose round (item 478),
            // int-caliber group 17 which is theirs alone, so flipping it isAmmo never sweeps a STANAG mag gun in.
            foreach (var wr in new[] { "rifle_birch", "rifle_pine", "rifle_maple" })
            {
                var wd = Def(dir, wr); var wa = Assets.find((ushort)wd.MagazineId);
                T.Check($"{wr}: its item is LOOSE 5.56 AMMO ({wa?.itemName}, isAmmo={wa?.isAmmo})", wa != null && wa.isAmmo);
                T.Check($"{wr}: ammo caliber matches so ShellAsset finds it ({wa?.magCaliber} vs {wd.Caliber})", wa != null && wa.magCaliber == wd.Caliber && wd.Caliber > 0);
                T.Check($"{wr}: reloads one round at a time via Shell_Reload, Action {wd.Action}", wd.ShellReload && wd.Action == "Bolt");
            }

            // ...and the opt-in must not have swept the other bolt guns in with it.
            foreach (var bolt in new[] { "timberwolf", "snayperskya" })
                T.Check($"control: {bolt} is still magazine-fed, not shell-fed (the Shell_Reload key is per-gun)",
                        !Def(dir, bolt).ShellReload);

            // THE CONTROL. The six hard-coded guns must be in the OK set -- if they are not, the harness itself is
            // broken rather than the content, and every count above is meaningless.
            foreach (var known in new[] { "eaglefire", "maplestrike", "augewehr", "nightraider" })
                T.Check($"control: {known} feeds from a real magazine (its mag is hard-coded in ItemCatalog)",
                        ok.Contains(known));

            yield break;
        }
    }
}
