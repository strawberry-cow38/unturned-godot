using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE GUN TABLE (strawberry: "could we un-hardcode eaglefire n maplestrike to be in line with the rest of the
    // weapons", "also masterkey wasnt letting me equip it either", "unknown gun should spit an error center screen
    // and fallback to unarmed").
    //
    // Three separate failures used to hide behind each other here, all of them SILENT:
    //   1. gunName was wired from guns_visual.tsv -- the VISUAL table -- so a fully ported gun missing from it got no
    //      gunName and simply did nothing when you pressed its hotbar key. That was masterkey.
    //   2. eaglefire/maplestrike/masterkey were hardcoded C# switch arms, so they were the three guns the data path
    //      never exercised -- the ones least likely to catch a regression in it.
    //   3. an unknown gun fell back to the EAGLEFIRE VISUAL: it fired, reloaded and looked like a working weapon, so
    //      a missing row was indistinguishable from a finished port.
    //
    // None of the three produced an error, which is why the checks below are about identity and refusal rather than
    // about anything crashing.
    public sealed class GunTableWiringTests : GameTest
    {
        public override string Name => "gun.table_wiring";

        // Every gun the CONTENT ships: a <name>.dat with a <name>_gun.txt model beside it. This is the definition the
        // catalog now uses, restated here from the files rather than from the code under test.
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
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var ported = PortedGuns(dir);
            T.Check($"the content ships guns to wire ({ported.Count})", ported.Count > 20);

            // EVERY ported gun has a visual row. This is the check that would have caught masterkey: it is not "the
            // table has N rows", it is "the two lists agree", so adding a gun to one file and not the other fails.
            var missing = new List<string>();
            foreach (var g in ported) if (!Viewmodel.IsKnownGun(g)) missing.Add(g);
            T.Check($"every ported gun has a guns_visual.tsv row (missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))})",
                missing.Count == 0);

            // The three that used to be hardcoded are now ordinary rows -- the specific thing strawberry asked for.
            foreach (var g in new[] { "eaglefire", "maplestrike", "masterkey" })
                T.Check($"{g} is in the table like every other gun", Viewmodel.IsKnownGun(g));

            // ...and every one of them is EQUIPPABLE, i.e. carries a gunName the equip path dispatches on. masterkey
            // failing this is the actual reported bug.
            ItemCatalog.RegisterAll();
            var unwired = new List<string>();
            foreach (var g in ported)
            {
                bool wired = false;
                foreach (var a in Assets.all()) if (a.gunName == g) { wired = true; break; }
                if (!wired) unwired.Add(g);
            }
            T.Check($"every ported gun is wired to an item you can equip (unwired: {(unwired.Count == 0 ? "none" : string.Join(", ", unwired))})",
                unwired.Count == 0);
            T.Check("...masterkey specifically, the one that was reported", WiredTo("masterkey"));

            // An unknown gun is NOT known -- the negative case, so IsKnownGun isn't just returning true.
            T.Check("a gun that doesn't exist is not known", !Viewmodel.IsKnownGun("definitely_not_a_gun"));
            T.Check("...and neither is null or empty", !Viewmodel.IsKnownGun(null) && !Viewmodel.IsKnownGun(""));

            // ---- THE REFUSAL. "unknown gun should spit an error center screen and fallback to unarmed" -- and the
            // fallback is the load-bearing half: the old behaviour handed you a working eaglefire under a false name.
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);

            p.EquipHeldGun("eaglefire");
            yield return Ticks(2);
            T.Check($"a known gun equips normally ({p.HeldGunName}, gun out = {p.HasGunOut})",
                p.HasGunOut && p.HeldGunName == "eaglefire");

            // Refusing must leave you UNARMED, not holding the previous weapon and not holding a mislabelled one.
            p.EquipHeldGun("definitely_not_a_gun");
            yield return Ticks(2);
            T.Check($"an unknown gun leaves you unarmed, not armed with something else (gun out = {p.HasGunOut}, held = {p.HeldGunName})",
                !p.HasGunOut);
            T.Check("...and specifically NOT silently holding an eaglefire", !(p.HasGunOut && p.HeldGunName == "eaglefire"));
        }

        static bool WiredTo(string gun)
        {
            foreach (var a in Assets.all()) if (a.gunName == gun) return true;
            return false;
        }
    }

    // THE MOVE MUST BE LOSSLESS. Pulling eaglefire/maplestrike/masterkey out of C# and into a TSV is a refactor whose
    // whole risk is that the table can't express something the switch arm could -- a magazine mesh, a dark albedo
    // tint -- and the loss shows up as a gun that renders slightly wrong, which nothing headless would notice and
    // which I can't check by eye on this box. So the former hardcoded values are pinned here as literals and the
    // resolved GunVisual is compared field by field.
    //
    // The values below are transcribed from the deleted switch arms. They are the SPEC, not a copy of the current
    // output: if the table stops producing them, this fails and the table is wrong.
    public sealed class GunVisualTableLosslessTests : GameTest
    {
        public override string Name => "gun.visual_table_lossless";

        public override IEnumerable<Step> Run()
        {
            // (gun, sight, mag, albedo, shoot, reload, hammer, aim, muzzle, tint, ejects)
            var expect = new (string Name, string Gun, string Sight, string Mag, string Albedo,
                              string Shoot, string Reload, string Hammer, Vector3 Aim, Vector3 Muzzle, Color Tint, bool Ejects)[]
            {
                ("eaglefire", "eaglefire_gun.txt", "eaglefire_iron_sights.txt", "eaglefire_mag.txt", "eaglefire_albedo.png",
                 "eaglefire_shoot.ogg", "eaglefire_reload.ogg", "eaglefire_hammer.ogg",
                 new Vector3(0f, -0.4688f, -0.2098f), new Vector3(0f, 0.78f, -0.079f), new Color(0.40f, 0.36f, 0.32f), true),
                // maplestrike has no shoot/reload clip of its own -- it used eaglefire's, and the loader's Snd()
                // fallback has to reproduce that rather than leaving it silent.
                ("maplestrike", "maplestrike_gun.txt", "maplestrike_iron_sights.txt", "eaglefire_mag.txt", "maplestrike_albedo.png",
                 "eaglefire_shoot.ogg", "eaglefire_reload.ogg", "maplestrike_hammer.ogg",
                 new Vector3(0f, -0.4388f, -0.2291f), new Vector3(0f, 0.78f, -0.079f), new Color(0.44f, 0.40f, 0.28f), true),
                // masterkey is the shotgun: NO sight, NO magazine, and Ejects=false (no per-shot shell eject). Its
                // hammer clip is missing, so it falls back to eaglefire's.
                ("masterkey", "masterkey_gun.txt", null, null, "masterkey_albedo.png",
                 "masterkey_shoot.ogg", "masterkey_reload.ogg", "eaglefire_hammer.ogg",
                 new Vector3(0f, -0.40f, -0.19f), new Vector3(0f, 0.615f, -0.042f), new Color(0.46f, 0.28f, 0.13f), false),
            };

            foreach (var e in expect)
            {
                var g = Viewmodel.VisualForTest(e.Name);
                T.Check($"{e.Name}: model + albedo ({g.Gun}, {g.Albedo})", g.Gun == e.Gun && g.Albedo == e.Albedo);
                T.Check($"{e.Name}: sight ({g.Sight ?? "<none>"})", g.Sight == e.Sight);
                // The magazine is the field the 4-column table format could NOT carry, and a missing mag mesh is
                // invisible on a static screenshot -- you notice it in the reload animation.
                T.Check($"{e.Name}: magazine ({g.Mag ?? "<none>"})", g.Mag == e.Mag);
                T.Check($"{e.Name}: sounds ({g.Shoot}, {g.Reload}, {g.Hammer})",
                    g.Shoot == e.Shoot && g.Reload == e.Reload && g.Hammer == e.Hammer);
                T.Check($"{e.Name}: aim hook ({g.AimHook})", g.AimHook.DistanceTo(e.Aim) < 1e-4f);
                T.Check($"{e.Name}: muzzle hook ({g.MuzzleHook})", g.MuzzleHook.DistanceTo(e.Muzzle) < 1e-4f);
                // The tint is the other thing the table couldn't carry. These three tint a texture the game darkens;
                // defaulting them to white would wash all three out and nothing would error.
                T.Check($"{e.Name}: albedo tint ({g.Tint})",
                    Mathf.Abs(g.Tint.R - e.Tint.R) < 0.005f && Mathf.Abs(g.Tint.G - e.Tint.G) < 0.005f && Mathf.Abs(g.Tint.B - e.Tint.B) < 0.005f);
                T.Check($"{e.Name}: ejects a shell = {g.Ejects}", g.Ejects == e.Ejects);
            }

            // The 28 rows that were ALREADY data must be unaffected by the two new optional columns: no magazine, and
            // a white tint. A parser that misread a short row would quietly restyle the whole arsenal.
            var plain = Viewmodel.VisualForTest("cobra");
            T.Check($"an existing 4-column row still has no magazine ({plain.Mag ?? "<none>"})", plain.Mag == null);
            T.Check($"...and an untinted white albedo ({plain.Tint})",
                Mathf.Abs(plain.Tint.R - 1f) < 1e-4f && Mathf.Abs(plain.Tint.G - 1f) < 1e-4f && Mathf.Abs(plain.Tint.B - 1f) < 1e-4f);
            yield break;
        }
    }
}
