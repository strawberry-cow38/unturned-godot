using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE REST OF THE GUNS (strawberry: "lets balance the rest of the guns. read the gold standard of the 5.56
    // weapons"). The 5.56 pass (fe8801bc) gave seven guns real muzzle velocities and distance falloff. Every other
    // gun that carries neither ballistic key falls through GunDef.ComputeBallistics to `travel = 10f` -- 500 m/s,
    // gravity 4 -- so a Luger, an MP40 and a PKM were all firing the identical bullet and only Range told them
    // apart. This file guards the families as they are brought over, one at a time.
    //
    // WHY A READ-BACK TEST AT ALL: the keys are appended to the .dat BELOW its `Blueprints [ ... ]` list. The
    // parser does read past a list (DatParser's root loop keeps consuming top-level keys after ReadList returns),
    // but "the values are in the file" and "the values reach GunDef" are different claims, and the whole gun suite
    // passed 20/20 BEFORE these edits -- so a green suite cannot tell a live edit from a dead one.
    //
    // THE GOLD STANDARD IS A FORMULA, not hand-picked numbers. Verified against all seven 5.56 guns to within a
    // metre: Damage_Falloff_Start = 0.1202 * v, Damage_Falloff_End = 0.2256 * v, Ballistic_Travel = v / 50 (the
    // 50 Hz tick), Ballistic_Steps ~= 455 / travel so every gun gets ~455 m of flight, gravity 1.4. Asserted here
    // as the RELATION rather than as literals, so a family added later cannot drift off the standard quietly.
    public sealed class CaliberBallisticsTests : GameTest
    {
        public override string Name => "gun.caliber_ballistics";

        static GunDef Def(string dir, string gun) => GunDef.FromDatText(System.IO.File.ReadAllText(dir + gun + ".dat"));

        // MASTER'S PER-CARTRIDGE TABLE (2026-08-21). One row per real cartridge: where full damage ends, where
        // it floors, what fraction survives, and how far the round flies before it is dropped. Rifle walls sit
        // past PEI's 1920 m playable width on purpose -- "technically infinite range, just the damage dropoff
        // would limit it". This is the authority the .dats are checked against; changing a value here without
        // changing the .dats (or the reverse) is exactly what these checks exist to catch.
        readonly record struct Fall(float Start, float End, float Floor, float Wall);
        static readonly System.Collections.Generic.Dictionary<string, Fall> Falloff = new()
        {
            ["5.56x45mm NATO"]    = new(113, 212, 0.65f, 2100), ["7.62x39mm"]         = new(120, 230, 0.68f, 2100),
            ["7.62x51mm NATO"]    = new(160, 320, 0.72f, 2100), ["7.62x54mmR"]        = new(170, 340, 0.72f, 2100),
            [".338 Lapua Magnum"] = new(250, 520, 0.80f, 2100), [".50 BMG"]           = new(280, 600, 0.82f, 2100),
            [".300 AAC Blackout"] = new( 70, 150, 0.55f, 1100), ["9x39mm"]            = new( 70, 150, 0.55f, 1100),
            ["5.7x28mm"]          = new( 90, 190, 0.60f, 1400), ["7.62x25mm Tokarev"] = new( 65, 150, 0.55f,  950),
            [".44 Magnum"]        = new( 55, 130, 0.55f,  900), [".45 ACP"]           = new( 40,  95, 0.50f,  700),
            ["9x19mm Parabellum"] = new( 45, 105, 0.52f,  750), ["9x18mm Makarov"]    = new( 40,  95, 0.50f,  700),
            [".22 LR"]            = new( 35,  90, 0.45f,  500), ["Railgun Slug"]      = new(400, 900, 0.90f, 2100),
            ["Arrow"]             = new( 40,  90, 0.60f,  260), ["Bolt"]              = new( 45, 100, 0.60f,  280),
            ["Nail"]              = new( 15,  40, 0.40f,  120), ["Paintball"]         = new( 12,  30, 0.40f,   90),
            ["12 Gauge"]          = new( 18,  45, 0.35f,    0), ["20 Gauge"]          = new( 15,  38, 0.35f,    0),
        };

        // gun, real weapon, its real muzzle velocity at its real barrel length (wiki trivia named the weapon;
        // the velocity is the cartridge's published figure for that barrel).
        static readonly (string Gun, string Real, float V)[] Retuned =
        {
            ("luger",     "Luger P08",    350f),
            ("bulldog",   "IMI Mini Uzi", 352f),
            ("teklowvka", "TEC-9",        355f),
            ("cobra",     "Glock 18C",    375f),
            ("mp40",      "MP40",         380f),
            ("viper",     "H&K MP5",      400f),
            ("colt",        "M1911",              253f),
            ("avenger",     "H&K USP .45",        260f),   // strawberry's own change, not the wiki's Beretta 96
            ("empire",      "H&K UMP45",          285f),
            ("scalar",      "KRISS Vector .45",   300f),
            ("matamorez",   "VSS Vintorez",       295f),
            ("yuri",        "PP-19 Bizon",        415f),
            ("kryzkarek",   "Makarov PMM",        430f),
            ("card",        "TT-33 Tokarev",      430f),
            ("desert_falcon","Desert Eagle .44",  448f),
            ("sportshot",   "Ruger 10/22",        380f),
            ("peacemaker",  "FN P90",             715f),
            ("hawkhound",   "Ruger Gunsite Scout",790f),
            ("nykorev",     "PKM",                825f),
            ("snayperskya", "SVD Dragunov",       830f),
            ("schofield",   "Mosin-Nagant",       865f),
            ("sabertooth",  "M39 EMR",            865f),
            // THE FIVE THAT NEEDED A RULING, resolved by strawberry: "listen to the dat" -- where the wiki's
            // real-weapon trivia and the .dat's own Caliber_Name disagree, the .dat wins.
            ("heartbreaker","FN SCAR-H",          820f),   // dat says 7.62x51; the wiki's SCAR-L is 5.56
            ("ace",         ".44 Magnum revolver",430f),   // dat says .44 + Ammo_Max 6; the wiki's Python is .357
            ("fury",        "M134 Minigun",       850f),   // Action Minigun, Ammo_Max 250 -- no trivia line to find
            ("honeybadger", "AAC Honey Badger",   305f),   // SUBSONIC .300 BLK; the dat's Range 125 is the shortest here
            ("hmg",         "M2-class .50 BMG",   853f),   // had NO Caliber_Name at all -- outside the system entirely
        };

        // THE HEAVY SNIPER TIER GETS THE FALLOFF HALF ONLY. grizzly/timberwolf/ekho are gravity 1x with a ~307 m
        // reach BY DESIGN (strawberry: "high range, very little drop"), and gun.ballistics_tuning pins both. Giving
        // them the standard's 1.4 gravity and ~455 m flight would fail that test and delete the tier -- so the two
        // halves of the gold standard are separable, and this is the family that proves it.
        static readonly (string Gun, float V)[] FalloffOnly =
        {
            ("grizzly", 853f), ("timberwolf", 900f), ("ekho", 853f),
        };

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/");

            foreach (var (gun, real, v) in Retuned)
            {
                var d = Def(dir, gun);
                // THE READ-BACK. Not "the file contains 7.5" -- what GunDef resolved after parsing.
                T.Check($"{gun} ({real}) carries its real muzzle velocity ({d.MuzzleVelocity:0} m/s, want {v:0}) -- was the 500 m/s fallback",
                    Mathf.Abs(d.MuzzleVelocity - v) < 1f);
                T.Check($"...at the 5.56 pass's gravity 1.4, not the default 4 ({d.GravityMultiplier:0.##})",
                    Mathf.IsEqualApprox(d.GravityMultiplier, 1.4f));

                // FALLOFF IS PER CARTRIDGE SINCE 2026-08-21, not derived from velocity (master: "base the
                // dropoff per round and hard wall too"). The old 0.1202*v / 0.2256*v relation was the 5.56 pass's
                // shape applied to everyone; it fired when the table replaced it, which is what it was for.
                // What has to stay true is that every gun of a cartridge agrees with its cartridge's row -- so
                // the drift check moved from "matches the formula" to "matches the table", same teeth.
                var row = Falloff[d.CaliberName];
                T.Check($"...falloff window is its cartridge's ({d.FalloffStart:0}..{d.FalloffEnd:0} m, table says {row.Start}..{row.End})",
                    Mathf.Abs(d.FalloffStart - row.Start) < 1.5f && Mathf.Abs(d.FalloffEnd - row.End) < 1.5f);
                T.Check($"...and its cartridge's floor ({d.FalloffMin:0.##}, table says {row.Floor:0.##})",
                    Mathf.Abs(d.FalloffMin - row.Floor) < 0.005f);

                // FalloffStart > 0 is what ARMS the feature at all -- GunDef treats <=0 as disabled, which is
                // exactly how the other 47 guns stayed on the old cliff while looking configured.
                T.Check($"...and the falloff is actually ARMED, not start<=0 ({d.FalloffStart:0} > 0)", d.FalloffStart > 0f);

                // FLIGHT IS THE PER-ROUND WALL now. 455 m was the old universal figure; master replaced it with
                // a wall per cartridge, rifle rounds past the map's 1920 m so only dropoff limits them.
                float flight = d.MuzzleVelocity * 0.02f * d.BallisticSteps;
                T.Check($"...and flies its cartridge's wall ({flight:0} m, table says {row.Wall})",
                    Mathf.Abs(flight - row.Wall) < row.Wall * 0.05f);
            }

            foreach (var (gun, v) in FalloffOnly)
            {
                var d = Def(dir, gun);
                var hrow = Falloff[d.CaliberName];
                T.Check($"{gun} gets its cartridge's falloff ({d.FalloffStart:0}/{d.FalloffEnd:0} m, table says {hrow.Start}/{hrow.End})",
                    Mathf.Abs(d.FalloffStart - hrow.Start) < 1.5f && Mathf.Abs(d.FalloffEnd - hrow.End) < 1.5f);
                // ...and KEEPS its tier. If a later sweep hands these the 1.4/455 m treatment, this is what says so.
                // The tier is GRAVITY, not reach -- reach became the per-round wall in master's balance pass, and
                // for a .50/.338 that wall is past the map on purpose. Gravity 1x is what still makes these three
                // shoot flatter than everything else, so that is the half worth pinning.
                T.Check($"...but KEEPS the heavy-sniper tier: gravity 1x ({d.GravityMultiplier:0.##}), reach now the per-round wall ({d.MuzzleVelocity * 0.02f * d.BallisticSteps:0} m)",
                    Mathf.IsEqualApprox(d.GravityMultiplier, 1f)
                    && d.MuzzleVelocity * 0.02f * d.BallisticSteps >= 1920f);
            }

            // THE HMG HAD NO CARTRIDGE AT ALL. It carried no Caliber_Name key, so it sat outside the whole caliber
            // system silently -- gun.caliber_field counts coverage, and a gun that declares nothing is not a gun
            // that declares something wrong, so nothing flagged it. Pinned here so it cannot fall back out.
            T.Check($"the hmg declares a cartridge now ({Def(dir, "hmg").CaliberName})",
                !string.IsNullOrEmpty(Def(dir, "hmg").CaliberName));

            // THE CONTROL, and it is deliberately NOT the zubeknakov. gun.ballistics_tuning uses the AK as its
            // untouched subject, but the AK is 7.62x39 and is IN this sweep -- it will be retuned, and that test's
            // control must move when it is. The nailgun is a tool, not a balance target, so it stays on the
            // fallback by design and is a control that will not go stale mid-sweep.
            // Same split as gun.ballistics_tuning: master's balance pass is global, so the nailgun HAS falloff now
            // and the "untouched" premise is gone. Gravity is the half that still catches a global default being
            // moved instead of per-gun values being set, so that is the half that stays.
            var ctl = Def(dir, "nailgun");
            T.Check($"control: gravity did NOT go global -- the nailgun is still on the default ({ctl.GravityMultiplier:0.##}, want 4)",
                Mathf.IsEqualApprox(ctl.GravityMultiplier, 4f));

            yield break;
        }
    }
}
