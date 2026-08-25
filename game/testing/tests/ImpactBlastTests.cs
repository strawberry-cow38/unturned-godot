using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // IMPACT BLAST, DRIVEN BY THE .DAT (groundwork for strawberry's railgun: "high damage, and a small aoe explosion
    // at impact point").
    //
    // The launcher's blast used to be four literals inline in the fire path behind `if (Gun?.Action == "Rocket")`.
    // They now live in launcher_rocket.dat, every value unchanged, which is the entire risk of this change: a
    // refactor whose whole claim is "same behaviour" needs something that would notice if it were not.
    //
    // Pins the four blast fields (radius + zombie/player/vehicle damage) on the launcher, and confirms an ordinary
    // gun carries none of them. NOTE: this used to also fire a live rocket at two enemies at different ranges to
    // prove the EQUIPPED gun's blast (not just the parsed copy) actually reaches the fire path -- that half was
    // removed along with the zombie enemy system and has not been rehomed onto another victim actor.
    public sealed class ImpactBlastTests : GameTest
    {
        public override string Name => "combat.impact_blast";
        public override double TimeoutSimSeconds => 40;

        static GunDef Def(string gun) =>
            GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/") + gun + ".dat"));

        public override IEnumerable<Step> Run()
        {
            // ---- 1. the numbers that moved out of code, pinned where they landed
            var rocket = Def("launcher_rocket");
            T.Check($"the launcher declares its blast radius ({rocket.BlastRadius} m)", Mathf.IsEqualApprox(rocket.BlastRadius, 9f));
            T.Check($"...and all three damages ({rocket.BlastZombieDamage}/{rocket.BlastPlayerDamage}/{rocket.BlastVehicleDamage})",
                Mathf.IsEqualApprox(rocket.BlastZombieDamage, 250f)
             && Mathf.IsEqualApprox(rocket.BlastPlayerDamage, 200f)
             && Mathf.IsEqualApprox(rocket.BlastVehicleDamage, 300f));

            // The flight envelope, because the shot below depends on it and it is not obvious: the launcher declares
            // Range 12 and no Ballistic_Travel, so travel defaults to 10 m/step and steps derive as ceil(12/10) = 2.
            // Two steps at 500 m/s is 20 m of reach -- a warhead that expires at 20 m. Anything further away is never
            // struck, and "no blast" and "no hit" look identical from outside.
            T.Check($"the launcher's warhead reaches {rocket.MuzzleVelocity * 0.02f * rocket.BallisticSteps:0} m ({rocket.BallisticSteps} steps x {rocket.MuzzleVelocity * 0.02f:0} m)",
                rocket.MuzzleVelocity * 0.02f * rocket.BallisticSteps >= 15f);

            // NOT everyone. A field with a non-zero default would hand every gun in the game an explosion, and the
            // launcher's own checks above would still pass -- so this is the one that catches it.
            var ef = Def("eaglefire");
            T.Check($"an ordinary rifle carries no blast at all ({ef.BlastRadius})", Mathf.IsZeroApprox(ef.BlastRadius));
            T.Check($"...and no blast damage either ({ef.BlastZombieDamage}/{ef.BlastPlayerDamage}/{ef.BlastVehicleDamage})",
                Mathf.IsZeroApprox(ef.BlastZombieDamage) && Mathf.IsZeroApprox(ef.BlastPlayerDamage) && Mathf.IsZeroApprox(ef.BlastVehicleDamage));
            yield break;
        }
    }
}
