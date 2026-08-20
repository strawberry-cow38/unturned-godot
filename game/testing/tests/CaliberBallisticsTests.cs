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

                // THE FORMULA, asserted as a relation to v so a later family cannot quietly drift off it.
                T.Check($"...falloff starts at 0.1202*v ({d.FalloffStart:0} m, want {0.1202f * v:0.#})",
                    Mathf.Abs(d.FalloffStart - 0.1202f * v) < 1.5f);
                T.Check($"...and ends at 0.2256*v ({d.FalloffEnd:0} m, want {0.2256f * v:0.#})",
                    Mathf.Abs(d.FalloffEnd - 0.2256f * v) < 1.5f);
                T.Check($"...to half damage ({d.FalloffMin:0.##})", Mathf.IsEqualApprox(d.FalloffMin, 0.5f));

                // FalloffStart > 0 is what ARMS the feature at all -- GunDef treats <=0 as disabled, which is
                // exactly how the other 47 guns stayed on the old cliff while looking configured.
                T.Check($"...and the falloff is actually ARMED, not start<=0 ({d.FalloffStart:0} > 0)", d.FalloffStart > 0f);

                // ~455 m of flight, so Range stops being a wall the bullet is deleted at.
                float flight = d.MuzzleVelocity * 0.02f * d.BallisticSteps;
                T.Check($"...and flies ~455 m before expiring ({flight:0} m), so range is a slope not a cliff",
                    flight > 430f && flight < 480f);
            }

            foreach (var (gun, v) in FalloffOnly)
            {
                var d = Def(dir, gun);
                T.Check($"{gun} gets falloff ({d.FalloffStart:0}/{d.FalloffEnd:0} m, want {0.1202f * v:0}/{0.2256f * v:0})",
                    Mathf.Abs(d.FalloffStart - 0.1202f * v) < 1.5f && Mathf.Abs(d.FalloffEnd - 0.2256f * v) < 1.5f);
                // ...and KEEPS its tier. If a later sweep hands these the 1.4/455 m treatment, this is what says so.
                T.Check($"...but KEEPS the heavy-sniper tier: gravity 1x ({d.GravityMultiplier:0.##}) and ~307 m reach ({d.MuzzleVelocity * 0.02f * d.BallisticSteps:0} m)",
                    Mathf.IsEqualApprox(d.GravityMultiplier, 1f)
                    && d.MuzzleVelocity * 0.02f * d.BallisticSteps is > 300f and < 320f);
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
            var ctl = Def(dir, "nailgun");
            T.Check($"control: the nailgun is untouched ({ctl.MuzzleVelocity:0} m/s, gravity {ctl.GravityMultiplier:0.##}, falloff {ctl.FalloffStart:0})",
                Mathf.IsEqualApprox(ctl.GravityMultiplier, 4f) && ctl.FalloffStart <= 0f);

            yield break;
        }
    }
}
