using Godot;
using System.Collections.Generic;
using UV = UnityEngine.Vector3;          // the sim's vector -- BallisticsMath speaks it, Godot.Vector3 is the ambient one here
using SDG.Unturned;                      // BallisticsMath -- the same integrator StepBullets runs

namespace UnturnedGodot.Testing
{
    // HEAVY CALIBER SNIPERS (strawberry: "grizzly, timberwolf, ekho. they fall into the heavy caliber sniper
    // category. high range, very little drop, not zero like the railguns, but barely noticable until ur at real
    // sniping ranges").
    //
    // Two halves, and the second is the one that actually needs a test. The first is that the three carry their
    // cartridge's real muzzle velocity and gravity 1x, which a reader can check by opening the .dat. The second is
    // what that PRODUCES -- and nothing in the .dat says it, because drop is the output of parse + a 50 Hz step loop,
    // not a field. "Barely noticeable up close, real at sniping range" is a claim about a curve, so it is measured
    // here against the same integrator the game runs.
    //
    // The control matters as much as the claim. Gravity is per-gun via Bullet_Gravity_Multiplier, and the obvious
    // way to get these numbers is to change the GLOBAL default in GunDef instead -- which would pass every check
    // below while silently retuning all 54 guns. So an untouched gun is measured in the same run and required to
    // still drop like it always did. Without it this file cannot tell "the three snipers were tuned" from
    // "everything was tuned".
    public sealed class BallisticsTuningTests : GameTest
    {
        public override string Name => "gun.ballistics_tuning";

        static GunDef Def(string dir, string gun) => GunDef.FromDatText(System.IO.File.ReadAllText(dir + gun + ".dat"));

        /// <summary>Drop in metres at `dist`, flown through the EXACT loop StepBullets runs (BallisticsMath, 0.02 s
        /// steps, position before gravity). Recomputing it with 0.5*g*t^2 here would test my arithmetic against
        /// itself and miss the half-step the discrete integrator actually carries. Returns null if the gun's
        /// BallisticSteps run out before it gets there -- which is itself a finding, not a pass.</summary>
        static float? DropAt(GunDef g, float dist)
        {
            var pos = UV.zero;
            var vel = new UV(g.MuzzleVelocity, 0f, 0f);
            float gravity = -9.81f * g.GravityMultiplier;
            for (int i = 0; i < g.BallisticSteps; i++)
            {
                var prev = pos;
                pos = SDG.Unturned.BallisticsMath.NextPos(pos, vel);
                vel = SDG.Unturned.BallisticsMath.StepVel(vel, gravity);
                if (pos.x >= dist)
                {
                    float f = (dist - prev.x) / (pos.x - prev.x);
                    return -(prev.y + (pos.y - prev.y) * f);
                }
            }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var heavy = new (string Gun, float Real)[]
            {
                ("grizzly",    853f),   // Barrett M82, .50 BMG
                ("timberwolf", 900f),   // PGW C14 Timberwolf, .338 Lapua Magnum
                ("ekho",       853f),   // CheyTac M200 -- rechambered .50 BMG, so it shares the grizzly's round
            };

            foreach (var (gun, real) in heavy)
            {
                var d = Def(dir, gun);
                T.Check($"{gun} carries its cartridge's real muzzle velocity ({d.MuzzleVelocity:0} m/s, want {real:0})",
                    Godot.Mathf.Abs(d.MuzzleVelocity - real) < 1f);
                T.Check($"...at gravity 1x ({d.GravityMultiplier:0.##})", Godot.Mathf.IsEqualApprox(d.GravityMultiplier, 1f));

                // RANGE IS PRESERVED. Ballistic_Steps was deleted so it derives as ceil(Range/travel); leaving a
                // stale hand-written value there is the quiet way this edit goes wrong, because raising travel
                // without touching steps extends the gun's real reach well past its declared Range and nothing
                // says so. A bullet that dies at 240 m on a 300 m rifle is the same bug in the other direction.
                // REACH IS NOW THE PER-ROUND HARD WALL, not the declared Range. master 2026-08-21: "i want the
                // 5.56 guns to have *technically* infinite range, just the damage dropoff would limit it ... base
                // the dropoff per round and hard wall too". A .50 BMG / .338 wall is 2100 m, past PEI's 1920 m
                // playable width, so the round crosses the whole map and only falloff limits it. The OLD assertion
                // here (300-320 m) was the bullet-delete wall master asked to remove -- it fired correctly when
                // that changed, which is the job. What it must still catch is a wall so short the round dies
                // mid-flight over the map.
                float reach = d.MuzzleVelocity * 0.02f * d.BallisticSteps;
                T.Check($"...and its round crosses the whole map before expiring ({reach:0} m against PEI's 1920 m)",
                    reach >= 1920f);

                // THE ACTUAL CLAIM. "Barely noticeable" up close: under 10 cm at 100 m is inside a torso, so no
                // holdover. "Not zero like the railguns" at range: the railgun target is ~10 cm at 300 m, so a
                // sniper must be several times that or it has quietly become one.
                float? near = DropAt(d, 100f), far = DropAt(d, 300f);
                T.Check($"{gun} drops barely anything at 100 m ({near * 100f:0.#} cm)", near is > 0f and < 0.10f);
                T.Check($"...and a real, learnable arc at 300 m ({far * 100f:0.#} cm)", far is > 0.35f and < 0.75f);
                T.Check($"...which is NOT railgun-flat ({(far ?? 0f) / 0.10f:0.#}x the railgun's 10 cm)",
                    far is > 0.30f);
            }

            // THE CONTROL. An untouched gun, measured the same way in the same run. If someone gets the numbers
            // above by moving GunDef's global Bullet_Gravity_Multiplier default from 4 instead of setting it per
            // gun, every check above still passes and this one fails -- which is the only reason it is here.
            //
            // The subject WAS the eaglefire until the 5.56 pass (2026-08-15) deliberately retuned it to 940 m/s
            // and gravity 1.4. A control has to be something nobody has touched, so it moved to the zubeknakov:
            // 7.62x39, outside both the sniper pass this test guards and the 5.56 pass. When this fires, check
            // whether the AK was retuned on purpose before "fixing" it -- a stale control is a real signal that
            // the scope of a change grew, which is exactly what it caught here.
            // ...and the subject moved AGAIN, exactly as the note above predicted it would. The zubeknakov was
            // retuned by the 2026-08-20 "balance the rest of the guns" pass (7.62x39, AK-47, 715 m/s), so it is no
            // longer untouched and cannot be the control. It moved to the nailgun for the same reason the
            // gun.damage_falloff control did: a nailgun is a TOOL, not a balance target, so no caliber pass will
            // ever claim it. This is the second time this control went stale from scope growth; the third would
            // mean the toys got swept too, which is a real finding rather than a test to fix.
            // ...and the control moved a THIRD time, but only half of it: master's 2026-08-21 balance pass is
            // deliberately GLOBAL (every cartridge gets a dropoff and a wall), so "an untouched gun" no longer
            // exists and the falloff-absent half of this control is dead by design rather than by accident.
            // The GRAVITY half is still exactly as load-bearing as when it was written: if someone produces the
            // sniper numbers by moving GunDef's global Bullet_Gravity_Multiplier default instead of setting it
            // per gun, every check above still passes and this one still fails. That is what it is here for.
            var ctl = Def(dir, "nailgun");
            T.Check($"control: gravity did NOT go global -- the nailgun is still on the default ({ctl.GravityMultiplier:0.##}, want 4)",
                Godot.Mathf.IsEqualApprox(ctl.GravityMultiplier, 4f));
            float? ctlDrop = DropAt(ctl, 20f);
            T.Check($"...and still drops on the default gravity over 20 m ({ctlDrop * 100f:0.#} cm), so gravity did not go global",
                ctlDrop is > 0.05f);

            yield break;
        }
    }
}
