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
    // Parsing the fields proves nothing on its own -- GunDef could read all four perfectly while the fire path still
    // ran the old hardcoded branch, and every parse check would pass. So the load-bearing part is the LIVE shot: a
    // zombie that is never hit directly taking damage anyway, and a second zombie outside the radius taking none.
    // The far one separates "the blast fired" from "the rocket hit something".
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

            // ---- 2. THE FIRE PATH ACTUALLY READS THEM
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("launcher_rocket");
            p.Ammo = 8;
            yield return Ticks(60);   // Fire() refuses until the equip finishes

            // Two zombies on the floor, neither on the flight path (which runs down the x=0 plane): one inside the
            // blast radius, one well outside it.
            ZombieController near = new ZombieController { Target = null, Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(near);
            near.GlobalPosition = new Vector3(3f, 0.2f, -5f);
            var far = new ZombieController { Target = null, Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(far);
            far.GlobalPosition = new Vector3(24f, 0.2f, -5f);
            yield return Ticks(20);

            float nearHp0 = near.Health, farHp0 = far.Health;
            T.Check($"both zombies start alive ({nearHp0} / {farHp0})", nearHp0 > 0f && farHp0 > 0f);

            // Which half is wired: the EQUIPPED gun's blast, not the copy re-parsed above. A dat that parses
            // correctly while the player's live GunDef comes from somewhere else would fail the shot and look
            // exactly like a fire-path bug.
            T.Check($"the equipped launcher carries its blast into play (id {p.Gun?.Id ?? "-"}, r={p.Gun?.BlastRadius ?? -1f})",
                p.Gun != null && Mathf.IsEqualApprox(p.Gun.BlastRadius, 9f));

            // BISECT. Explode() itself, at ground level, before trusting the rocket to get anywhere. If this fails,
            // the blast machinery or the LoS test is the problem; if it passes and the shot below does not, the
            // projectile never arrived. One red check covering two unrelated faults is worth splitting.
            // ~9.9 m out, which is where a -10 degree shot from a 1.75 m eye meets the floor. The distance is not
            // cosmetic: blast player damage is 200 with squared falloff, so a detonation inside the 9 m radius KILLS
            // THE SHOOTER, and a dead shooter cannot fire. An earlier cut of this test blew itself up at 2.5 m and
            // then failed on "the launcher fired", which reads as a broken fire path rather than a suicide.
            var impact = new Vector3(0f, 0.05f, -9.9f);
            p.Explode(impact, 9f, 250f, 200f, 300f);
            yield return Ticks(2);
            T.Check($"Explode reaches a zombie {near.GlobalPosition.DistanceTo(impact):0.#} m away ({nearHp0} -> {(near.Dead ? 0f : near.Health)})",
                near.Dead || near.Health < nearHp0);
            T.Check($"...and not the one at {far.GlobalPosition.DistanceTo(impact):0.#} m ({(far.Dead ? 0f : far.Health)})",
                !far.Dead && Mathf.IsEqualApprox(far.Health, farHp0));

            if (near.Dead) { near.QueueFree(); near = new ZombieController { Target = null, Speciality = ZombieController.ESpeciality.NORMAL }; World.AddChild(near); }
            near.GlobalPosition = new Vector3(3f, 0.2f, -5f);
            yield return Ticks(5);
            nearHp0 = near.Health;

            // FIRE INTO THE GROUND -- not at a wall, and not at a zombie.
            //
            // Two earlier versions of this test are the reason. The first shot a wall and measured nothing. The
            // second parked a zombie on the flight path to detonate against, which passed alone and FAILED in the
            // full suite: it assumed the player was still at spawn height when the shot went off, and in a shared
            // boot the settle timing is not mine to assume -- the shooter is on the floor by then, a metre lower,
            // and the rocket flies somewhere else entirely. The floor is always there, always in front of the
            // muzzle, and a ground-level blast is the one case ExplosionBlocked handles correctly.
            //
            // WHY NOT A WALL (pre-existing bug, found here, NOT fixed): a blast flush on a VERTICAL surface damages
            // nothing at all. ExplosionBlocked casts its line-of-sight ray from `point + up*0.8` -- that 0.8 m is
            // what lifts a blast clear of the floor, but a wall is taller than 0.8 m, so the ray starts INSIDE the
            // wall the rocket just hit and every target reads as shielded. Measured directly: the same blast at a
            // wall face leaves a zombie 5.5 m away untouched (100 -> 100), and 5 cm in front of the face kills it
            // (100 -> 3.6). The old hardcoded rocket branch had this too. The fix is to offset the origin along the
            // hit normal before the LoS test.
            p.GlobalPosition = new Vector3(0f, 1f, 0f);   // re-pin, then MEASURE rather than assume
            yield return Ticks(15);
            float eye = p.EyesWorld.Y;
            T.Check($"the shooter is standing where the geometry below needs it ({eye:0.##} m eye height)",
                eye > 1.5f && eye < 3.5f);

            // RE-PIN both. These are live AI bodies, and with a Target they walk at the player -- so the distance
            // this test measures against drifts with however many ticks happened to elapse. That is what made it
            // fail during an unrelated recoil change: nothing about the blast moved, the zombie did. Target is
            // null now and the positions are re-asserted immediately before the shot.
            near.GlobalPosition = new Vector3(3f, 0.2f, -5f);
            far.GlobalPosition = new Vector3(24f, 0.2f, -5f);
            yield return Ticks(2);
            nearHp0 = near.Health;
            T.Check($"the near zombie is where the geometry needs it ({near.GlobalPosition.DistanceTo(impact):0.#} m from impact, radius 9)",
                near.GlobalPosition.DistanceTo(impact) < 9f);

            p.DebugSetPitch(-10f);   // into the floor ~9.9 m ahead: past the shooter's own blast, inside the zombie's
            yield return Ticks(10);
            T.Check("the launcher fired", p.Fire());
            for (int i = 0; i < 90 && !near.Dead && Mathf.IsEqualApprox(near.Health, nearHp0); i++)
                yield return Ticks(1);   // travelling projectile -- wait for it to land

            float nearHp1 = near.Dead ? 0f : near.Health;
            float farHp1 = far.Dead ? 0f : far.Health;
            T.Check($"a zombie beside the impact takes the blast without being hit ({nearHp0} -> {nearHp1})",
                nearHp1 < nearHp0);
            // THE CONTROL. Without it, "the near zombie took damage" is also what a stray direct hit looks like.
            T.Check($"...and one 24 m away, outside the 9 m radius, takes nothing ({farHp0} -> {farHp1})",
                Mathf.IsEqualApprox(farHp1, farHp0));

            p.QueueFree();
            yield break;
        }
    }
}
