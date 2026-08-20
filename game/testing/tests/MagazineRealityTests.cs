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
                if (def.ShellReload) continue;            // shell guns feed loose rounds, not a mag item
                if (def.MagazineId <= 0) continue;        // declares no magazine at all -- a separate question
                var mag = Assets.find((ushort)def.MagazineId);
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

            // THE CONTROL. The six hard-coded guns must be in the OK set -- if they are not, the harness itself is
            // broken rather than the content, and every count above is meaningless.
            foreach (var known in new[] { "eaglefire", "maplestrike", "augewehr", "nightraider" })
                T.Check($"control: {known} feeds from a real magazine (its mag is hard-coded in ItemCatalog)",
                        ok.Contains(known));

            yield break;
        }
    }
}
