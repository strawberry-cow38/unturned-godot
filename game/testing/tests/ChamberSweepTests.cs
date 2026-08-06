using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHICH GUNS CARRY A ROUND IN THE CHAMBER (master: "do a sweep on all weapons to fix which guns can have one in
    // the chamber when reloading, rn the Ace can have 6 + 1").
    //
    // There is NO SOURCE ANSWER to defer to: retail has rechambering for bolt/pump cycling but its reload just fills
    // to Ammo_Max -- no +1 concept exists in UseableGun or ItemGunAsset. So this is the port's own design, settled by
    // master, and the whole point of a sweep is that it covers every gun rather than the one that was reported.
    //
    // The reported bug is one line of it. The interesting half is that the OLD rule (`!IsShotgun`) was wrong in BOTH
    // directions at once -- it gave the Ace a 7th round it has nowhere to put, and refused pumps a ghost-loaded shell
    // they genuinely can hold. A fix that only chased the Ace would have left the second half in place forever,
    // because nobody reports a gun holding one round FEWER than it should.
    public sealed class ChamberSweepTests : GameTest
    {
        public override string Name => "gun.chamber_sweep";

        public override IEnumerable<Step> Run()
        {
            // Read the .dat files straight off disk, like GunCaliberTests -- GunDef has no registry, and this also
            // means the sweep sees every gun that SHIPS rather than every gun some catalogue happens to list.
            string dir = ProjectSettings.GlobalizePath("res://content/");
            GunDef Def(string g) { try { return GunDef.FromDatText(System.IO.File.ReadAllText(dir + g + ".dat")); } catch { return null; } }

            // ---- THE REPORTED BUG, by name.
            var ace = Def("ace");
            T.Check($"found the ace ({ace?.RealWeapon})", ace != null);
            if (ace != null)
            {
                T.Check($"the ace is recognised as a REVOLVER ({ace.RealWeapon})", ace.IsRevolver);
                T.Check("...so it gets NO chambered round -- the cylinder IS the chambers", !ace.HasChamberRound);
                // The old rule, stated as the arithmetic that produced the bug: !IsShotgun was true for the ace.
                T.Check($"...where the old `!IsShotgun` rule would have allowed {ace.AmmoMax}+1", !ace.IsShotgun);
            }

            // ---- THE OTHER HALF, which nobody would have reported: pumps were being DENIED a round they should have.
            var pump = Def("bluntforce");
            T.Check($"found the pump ({pump?.RealWeapon})", pump != null);
            if (pump != null)
            {
                T.Check("a pump DOES get one (ghost loading -- chamber a shell, then refill the tube)", pump.HasChamberRound);
                T.Check("...and the old rule refused it, because a pump is an IsShotgun", pump.IsShotgun);
            }

            // ---- EVERY OTHER ACTION, so this is a sweep and not three spot-checks.
            foreach (var (id, want, why) in new[]
            {
                ("eaglefire", true,  "magazine-fed rifle"),
                ("colt",      true,  "magazine-fed pistol -- NOT a revolver, despite sharing Action Trigger with one"),
                ("timberwolf", true, "bolt: top the magazine up with one chambered"),
                ("schofield", true,  "reads Action Bolt / Real_Weapon Mosin-Nagant -- a bolt rifle here, not a revolver"),
                ("sawed_off", false, "break action: both barrels ARE the chambers"),
                ("masterkey", false, "break action"),
                ("crossbow",  false, "string"),
                ("bow_maple", false, "string"),
                ("launcher_rocket", false, "one tube, one rocket"),
            })
            {
                var g = Def(id);
                if (g == null) { T.Check($"{id}: .dat present", false); continue; }
                T.Check($"{id} ({g.Action}) {(want ? "carries" : "does NOT carry")} a chambered round -- {why}",
                    g.HasChamberRound == want);
            }

            // ---- NOTHING IS UNCLASSIFIED. A gun whose Action this rule has never seen would fall through the switch
            // to the revolver test and silently get +1 -- the exact shape of the original bug. Assert every gun in
            // the catalogue resolves to a deliberate answer.
            int seen = 0, revolvers = 0;
            var unknownActions = new List<string>();
            var known = new HashSet<string> { "Trigger", "Bolt", "Pump", "Break", "String", "Rail", "Rocket", "Minigun" };
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.dat"))
            {
                var g = Def(System.IO.Path.GetFileNameWithoutExtension(f));
                if (g == null || string.IsNullOrEmpty(g.Action)) continue;
                seen++;
                if (g.IsRevolver) revolvers++;
                if (!known.Contains(g.Action) && !unknownActions.Contains(g.Action)) unknownActions.Add(g.Action);
            }
            T.Check($"the sweep actually covered the arsenal ({seen} guns)", seen >= 30);
            T.Check($"every Action is one this rule classifies deliberately{(unknownActions.Count > 0 ? " -- UNKNOWN: " + string.Join(", ", unknownActions) : "")}",
                unknownActions.Count == 0);
            // One revolver today (the ace). Pinned as a COUNT so adding a second without extending RevolverModels
            // shows up here rather than as a quiet 6+1 in someone's hands.
            T.Check($"exactly one revolver is currently identified ({revolvers})", revolvers == 1);

            yield break;
        }
    }
}
