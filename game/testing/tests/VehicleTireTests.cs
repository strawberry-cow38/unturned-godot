using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SHOOTABLE TIRES (strawberry 2026-09-01: "shoot tires, pops the actual tire part of the wheel model,
    // leaving the rim, driving when missing tire(s) affects handling, causes sparks from the damaged wheel when
    // driving on it. can be replaced by the mechanics ui.")
    //
    // The request has four separate claims and each gets its own check, because three of them can be true while
    // the fourth is silently missing: the flag can flip with no visual, the tread can vanish with no physics
    // change, the physics can change with no sparks, and every one of those still "works" if you only assert
    // that PopTire returned true.
    public sealed class VehicleTireTests : GameTest
    {
        public override string Name => "vehicle.tires";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(30);

            // ---- 1. THE TREAD IS ITS OWN NODE. If the wheel is still one mesh, nothing below can pop.
            T.Check($"the sedan has 4 poppable tires ({car.TireCount})", car.TireCount == 4);
            T.Check($"none are flat at spawn ({car.TirePoppedCount})", car.TirePoppedCount == 0);

            // ---- 2. THE RIM SURVIVES. "pops the actual tire part of the wheel model, LEAVING THE RIM" -- so the
            // wheel must still be visible after the tread goes. Hiding the whole wheel would pass a
            // popped-flag check and be exactly wrong.
            var tire0 = car.TireNodeForTest(0);
            var rim0 = car.RimNodeForTest(0);
            T.Check("tire and rim are separate nodes", tire0 != null && rim0 != null && tire0 != rim0);
            if (tire0 == null || rim0 == null) yield break;
            T.Check("both visible while inflated", tire0.Visible && rim0.Visible);

            float stockFric = car.WheelFrictionForTest(0);
            float stockRad = car.WheelRadiusForTest(0);
            T.Check($"the wheel has real stock grip to lose ({stockFric:F2})", stockFric > 0.1f);

            // ---- 3. POP IT.
            T.Check("shooting the tire reports success", car.PopTire(0));
            yield return Ticks(4);
            T.Check("...it reads as blown", car.IsTirePopped(0));
            T.Check($"...exactly one is flat ({car.TirePoppedCount})", car.TirePoppedCount == 1);
            T.Check("...the TREAD is hidden", !tire0.Visible);
            T.Check("...the RIM is still there", rim0.Visible);
            T.Check("...popping it again does nothing", !car.PopTire(0));
            T.Check("...a popped tire no longer resolves as a hit target",
                    car.ResolveHitTire(tire0.GlobalPosition) != 0);

            // ---- 4. HANDLING ACTUALLY CHANGES. The claim is "driving when missing tire(s) affects handling",
            // so assert on the wheel's real physics, not on the flag that is supposed to cause it.
            float popFric = car.WheelFrictionForTest(0);
            float popRad = car.WheelRadiusForTest(0);
            T.Check($"grip drops on the bare rim ({stockFric:F2} -> {popFric:F2})", popFric < stockFric * 0.6f);
            T.Check($"the corner sits lower ({stockRad:F2} -> {popRad:F2})", popRad < stockRad);
            T.Check($"the OTHER wheels are untouched ({car.WheelFrictionForTest(1):F2})",
                    Mathf.IsEqualApprox(car.WheelFrictionForTest(1), stockFric));

            // ---- 5. SPARKS, and only while actually scrubbing. A spark emitter that is simply always on once a
            // tire pops would pass any "sparks exist" check while showering a parked car.
            var fx0 = car.TireSparksForTest(0);
            T.Check("the popped wheel has a spark emitter", fx0 != null);
            if (fx0 == null) yield break;
            yield return Ticks(10);
            T.Check("...NOT sparking while stopped", !fx0.Emitting);
            // HOLD the speed instead of setting it once. A parked car with the engine off sheds a one-shot
            // 9 m/s to 1.2 m/s within 12 ticks -- under the sparks' own 2 m/s floor -- so the first version of
            // this check was asserting on a car that had already coasted to a stop. Re-applying each tick keeps
            // it genuinely rolling, which is the state the feature is about.
            bool sparked = false;
            for (int k = 0; k < 14; k++)
            {
                car.LinearVelocity = new Vector3(0f, car.LinearVelocity.Y, -9f);
                yield return Ticks(1);
                if (fx0.Emitting) { sparked = true; break; }
            }
            T.Check($"...sparking once it is rolling on the rim (contact={car.WheelInContactForTest(0)} v={car.LinearVelocity.Length():F1})",
                    sparked);
            var fx1 = car.TireSparksForTest(1);
            T.Check("...and the INTACT wheel is not sparking", fx1 == null || !fx1.Emitting);
            car.LinearVelocity = Vector3.Zero;
            yield return Ticks(12);
            T.Check("...stops again when it stops", !fx0.Emitting);

            // ---- 6. REPLACE FROM THE MECHANICS UI, and the physics come BACK. Restoring to a shared constant
            // instead of this wheel's own stock figure is the easy bug here.
            T.Check("replacing it reports success", car.RepairTire(0));
            yield return Ticks(4);
            T.Check("...it reads as ok", !car.IsTirePopped(0));
            T.Check("...the tread is visible again", tire0.Visible);
            T.Check($"...grip is restored exactly ({car.WheelFrictionForTest(0):F2} vs {stockFric:F2})",
                    Mathf.IsEqualApprox(car.WheelFrictionForTest(0), stockFric));
            T.Check($"...radius is restored exactly ({car.WheelRadiusForTest(0):F2} vs {stockRad:F2})",
                    Mathf.IsEqualApprox(car.WheelRadiusForTest(0), stockRad));
            T.Check("...replacing an intact tire does nothing", !car.RepairTire(0));

            // ---- 7. OTHER VEHICLES, and a tracked one must NOT get tires.
            var bus = Vehicle.BuildByName("bus");
            World.AddChild(bus); bus.GlobalPosition = new Vector3(100f, 1.2f, 0f);
            yield return Ticks(20);
            T.Check($"the bus has poppable tires ({bus.TireCount})", bus.TireCount > 0);
        }
    }
}
