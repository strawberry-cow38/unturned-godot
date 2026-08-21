using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A LAND VEHICLE DRIVEN INTO WATER (strawberry 2026-08-21: "non aquatic vehicles driven into water will have
    // their engines cut, and float on the surface for a short time, before sinking.")
    //
    // WHAT WAS THERE BEFORE: nothing. The ocean's only collider is a bullets-only box on bit 9, and neither the
    // player nor a vehicle masks that bit, so a car driven into the sea fell straight through the surface and went
    // on driving along the seabed with its engine running. Every assertion below therefore had to be written to
    // fail against that, not merely to describe the new behaviour -- see the CONTROLS, which are the half that can
    // tell "the feature works" from "the feature is not reached".
    public sealed class VehicleWaterSwampTests : GameTest
    {
        public override string Name => "vehicle.water_swamp";
        public override double TimeoutSimSeconds => 120;

        static Vehicle Car(Node w, string spec, Vector3 at)
        {
            var v = Vehicle.BuildByName(spec);
            w.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true;
            v.Fuel = v.FuelMax > 0f ? v.FuelMax : 100f;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 0f;   // flat test sea at Y0, same convention as vehicle.boat_hull_probe

            // NO GROUND PLANE in this phase, deliberately. Rigs.Ground is an infinite WorldBoundaryShape3D at Y0,
            // which is exactly where the sea is -- it would hold the car up and the sink half could never be read.
            var jeep = Car(World, "jeep", new Vector3(0f, 1f, 0f));
            yield return Ticks(60);   // ~1.2 s: long enough to drop in and latch

            T.Check($"a jeep in the sea is swamped (y {jeep.GlobalPosition.Y:0.00})", jeep.Swamped);
            T.Check("...and its engine drowned", !jeep.EngineOn);

            // The engine must STAY cut. Cutting once on entry lets the driver simply restart and carry on along
            // the bottom, which is the behaviour this feature exists to remove -- so restart it and check it dies.
            jeep.EngineOn = true;
            yield return Ticks(5);
            T.Check("...and cannot be restarted while it is under", !jeep.EngineOn);

            // FLOAT WINDOW. A car with no buoyancy at all would be ~77 m down by now (free fall from y1 over 4 s),
            // so "still near the surface" is a claim the old behaviour fails by three orders of magnitude.
            yield return Ticks(140);   // ~4 s total, inside SwampFloatSeconds
            float floatY = jeep.GlobalPosition.Y;
            T.Check($"it is still floating at the surface 4 s in (y {floatY:0.00})", floatY > -3f);

            // ...and floating means floating, not slowly submerging: it should sit where its displacement balances
            // its weight, which for SwampHullDensity 800 is roughly four fifths under.
            T.Check($"...and it is riding ON the water, not hovering above it (y {floatY:0.00})", floatY < 1.5f);

            // SINK. Air is gone at SwampFloatSeconds + SwampSinkSeconds = 9 s; give it 6 s past that.
            yield return Ticks(550);   // ~15 s total
            float sunkY = jeep.GlobalPosition.Y;
            T.Check($"the air ran out and it went down (y {floatY:0.00} -> {sunkY:0.00})", sunkY < floatY - 5f);
            T.Check($"...and it is still going down, not resting on nothing (vy {jeep.LinearVelocity.Y:0.0} m/s)", jeep.LinearVelocity.Y < -0.5f);

            // ...but going down through WATER, not through air. Damping survives the loss of lift; without it the
            // hull free-falls at g and this reads well past 40 m/s by now.
            T.Check($"...at a water sink rate, not a free fall (vy {jeep.LinearVelocity.Y:0.0} m/s)", jeep.LinearVelocity.Y > -25f);

            // ---- CONTROL 1: A BOAT IN THE SAME WATER. Proves the gate is on WaterMode and not on "is wet": the
            // runabout is WaterMode.Boat, so it must never enter the swamp path at all and must keep its engine.
            var boat = Car(World, "runabout", new Vector3(30f, 0.5f, 0f));
            yield return Ticks(160);
            T.Check($"control: a runabout in the same sea is NOT swamped (y {boat.GlobalPosition.Y:0.00})", !boat.Swamped);
            T.Check("control: ...and its engine is still running", boat.EngineOn);

            // ---- CONTROL 2: DRY LAND. Drop the sea far below an infinite ground plane and put a jeep on it. If
            // this one swamps, the trigger is not depth at all and every check above is measuring something else.
            Terrain.SeaLevelY = -60f;
            Rigs.Ground(World);
            var dry = Car(World, "jeep", new Vector3(-30f, 1.2f, 0f));
            yield return Ticks(160);
            T.Check($"control: a jeep on dry land does not swamp (y {dry.GlobalPosition.Y:0.00}, sea {Terrain.SeaLevelY:0})", !dry.Swamped);
            T.Check("control: ...and keeps its engine", dry.EngineOn);

            Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
        }
    }
}
