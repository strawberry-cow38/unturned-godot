using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // GUNS NAME THEIR REAL CARTRIDGE (strawberry 2026-09-04: "make guns say their caliber").
    //
    // Retail names the round after the gun -- "chambered in Timberwolf ammunition" -- which tells a player nothing
    // they can act on: not which magazine fits, not how it compares to the rifle in the other hand. The .dat has
    // carried Caliber_Name all along and GunDef has read it since the per-caliber damage rebalance; the description
    // was the one place it never reached.
    //
    // The sweep at the end is the check that matters. Spot-checking the Eaglefire proves the rewrite RAN; only
    // walking every gun proves none were missed, and a gun added later with a caliber and retail text fails here
    // rather than shipping with "chambered in Fusilaut ammunition".
    public sealed class GunCaliberTextTests : GameTest
    {
        public override string Name => "gun.description_names_caliber";

        static int CountOf(string s, string needle)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            yield return Ticks(1);

            var eagle = SDG.Unturned.Assets.find(4);
            T.Check($"the Eaglefire names 5.56x45mm NATO ({eagle?.description})",
                    eagle != null && eagle.description.Contains("5.56x45mm NATO"));
            T.Check("...and no longer says \"Military ammunition\"",
                    eagle != null && !eagle.description.Contains("Military ammunition"));

            // The tail must survive: the rewrite replaces the noun phrase, not the rest of the line.
            var pdw = SDG.Unturned.Assets.find(116);
            T.Check($"the PDW keeps its second sentence ({pdw?.description})",
                    pdw != null && pdw.description.Contains(".300 AAC Blackout") && pdw.description.Contains("Internally suppressed"));

            // A shotgun already names its cartridge, and "Shells" is the word that says what it feeds. Rewriting it
            // to "chambered in 12 Gauge" would be a downgrade dressed as a fix, so the guard skips it.
            var blunt = SDG.Unturned.Assets.find(112);
            T.Check($"a shotgun is left alone -- it already said 12 Gauge ({blunt?.description})",
                    blunt != null && blunt.description.Contains("12 Gauge Shells"));

            // The Shadowstalker's retail text wrapped its fake round in a <color> tag. Its REAL caliber is
            // "Railgun Slug" -- not its own name, which is what I assumed when writing this and was wrong -- so the
            // guard does not skip it and the rewrite eats the tag along with the phrase it wrapped. That is correct:
            // the tag decorated a cartridge name that is now gone. What must NOT happen is a HALF-eaten tag, so the
            // assertion is markup balance rather than markup presence.
            var shadow = SDG.Unturned.Assets.find(1441);
            bool balanced = shadow != null &&
                CountOf(shadow.description, "<color=") == CountOf(shadow.description, "</color>");
            T.Check($"the Shadowstalker's markup is balanced after the rewrite ({shadow?.description})", balanced);
            T.Check($"...and it names its real cartridge", shadow != null && shadow.description.Contains("Railgun Slug"));

            // No description anywhere may be left with a dangling tag by the rewrite.
            int unbalanced = 0; string firstUnbalanced = null;
            foreach (var a2 in SDG.Unturned.Assets.all())
            {
                if (a2?.description == null) continue;
                if (CountOf(a2.description, "<color=") != CountOf(a2.description, "</color>"))
                { unbalanced++; firstUnbalanced ??= $"{a2.itemName}: {a2.description}"; }
            }
            T.Check($"no item has unbalanced colour markup ({unbalanced}{(unbalanced > 0 ? " -- " + firstUnbalanced : "")})", unbalanced == 0);

            // ---- THE SWEEP ----
            int named = 0, retailLeft = 0, noCaliber = 0;
            string firstBad = null;
            string dir = ProjectSettings.GlobalizePath("res://content/");
            foreach (var dat in System.IO.Directory.GetFiles(dir, "*.dat"))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(dat);
                if (!System.IO.File.Exists(dir + stem + "_gun.txt")) continue;   // the ported-gun gate, as the loader uses
                GunDef g;
                try { g = GunDef.FromDatText(System.IO.File.ReadAllText(dat)); } catch { continue; }
                if (g == null || string.IsNullOrEmpty(g.Id) || !ushort.TryParse(g.Id, out var id)) continue;
                var a = SDG.Unturned.Assets.find(id);
                if (a == null || string.IsNullOrEmpty(a.description)) continue;
                string cal = (g.CaliberName ?? "").Trim().Trim('"');
                if (cal.Length == 0) { noCaliber++; continue; }
                if (a.description.IndexOf(cal, System.StringComparison.OrdinalIgnoreCase) >= 0) { named++; continue; }
                retailLeft++;
                firstBad ??= $"{a.itemName} ({id}) has caliber {cal} but says \"{a.description}\"";
            }

            T.Check($"the sweep found ported guns to check ({named + retailLeft} with a caliber, {noCaliber} without)",
                    named + retailLeft > 30);
            T.Check($"every gun with a caliber names it in its description ({named} named{(retailLeft > 0 ? " -- " + firstBad : "")})",
                    retailLeft == 0);
        }
    }
}
