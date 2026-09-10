using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The car lift (master 2026-09-10: "on the main menu it has its ramp, but in game it doesnt.
    /// give it a power io input").
    ///
    /// Two things are worth pinning. The RAMP existing at all -- it was absent because Car_Lift_0's platform
    /// is a SkinnedMeshRenderer with no MeshFilter, and extract_objects_v2 walks the LODGroup for
    /// MeshFilter/MeshRenderer LOD0, so it never saw it. And the motion being a LIFT: the Hinge bone's
    /// rotation curve is two identical keys while its position runs 1.15 m, so anything that treated this as
    /// a door would swing a platform that is meant to rise, and no build would catch it.
    ///
    /// The power gate is asserted as a REFUSAL that leaves the height alone, not just as a false return: a
    /// version that returned false and moved anyway would pass a return-value check.</summary>
    public sealed class CarLiftTests : GameTest
    {
        public override string Name => "prop.car_lift";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            // ---- THE MESH THE RIP WAS MISSING.
            string p = ProjectSettings.GlobalizePath("res://content/objects/Car_Lift_0_ramp.obj");
            T.Check("the platform mesh is on disk", System.IO.File.Exists(p));
            var rampMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + "Car_Lift_0_ramp.obj");
            T.Check("...and loads", rampMesh != null);
            if (rampMesh != null)
            {
                var ab = rampMesh.GetAabb();
                // A car-sized slab: wide and long, and THIN. If this ever comes back as the frame instead
                // (4.4 m tall) the extraction has silently grabbed the wrong renderer.
                T.Check($"it is a flat platform, not the frame (size {ab.Size})",
                        ab.Size.Y < 1.0f && ab.Size.X > 2.5f && ab.Size.Z > 2.5f);
            }

            // ---- THE SOURCE NUMBERS.
            T.Check($"travel is the clip's 1.15 m ({CarLift.TravelMetres})", Mathf.Abs(CarLift.TravelMetres - 1.15f) < 0.001f);
            T.Check($"duration is the clip's 1.967 s ({CarLift.TravelSeconds})", Mathf.Abs(CarLift.TravelSeconds - 1.967f) < 0.01f);

            var lift = CarLift.Spawn(World, Vector3.Zero, Basis.Identity, rampMesh, null);
            yield return Ticks(2);

            T.Check("it is a power CONSUMER", lift.PowerPorts.Count == 1 && !lift.PowerProducing);
            T.Check("...on the deployables group PowerNet reads", lift.IsInGroup("deployables"));
            T.Check("it starts down", !lift.Raised && Mathf.Abs(lift.Height) < 0.001f);

            // ---- NO POWER, NO LIFT. Asserted on the HEIGHT, not just the return value: a version that
            // returned false and moved anyway would sail through a return-only check.
            lift.DebugForcePower = false;
            T.Check("unpowered, F is refused", !lift.Toggle());
            yield return Ticks(30);
            T.Check("...and it has not moved", Mathf.Abs(lift.Height) < 0.001f && !lift.Moving);

            // ---- POWERED, IT RISES, AND STOPS AT THE TOP.
            lift.DebugForcePower = true;
            T.Check("powered, F works it", lift.Toggle());
            T.Check("...and it is travelling", lift.Moving);
            yield return Until(() => !lift.Moving, 6);
            T.Check($"it reaches the top and stops ({lift.Height:0.000} m)", lift.Raised && !lift.Moving);
            T.Check("...without overshooting", lift.Height <= CarLift.TravelMetres + 0.001f);

            // ---- AND BACK DOWN.
            T.Check("F sends it back down", lift.Toggle());
            yield return Until(() => !lift.Moving, 6);
            T.Check($"it returns to the floor ({lift.Height:0.000} m)", Mathf.Abs(lift.Height) < 0.001f);

            // ---- NO REVERSING MID-TRAVEL. A lift that flips direction under a car is how you trap one.
            T.Check("F starts it up again", lift.Toggle());
            T.Check("...and a second press mid-travel is refused", !lift.Toggle());
            T.Check("...so it is still going the way it started", lift.Moving);

            lift.QueueFree();
        }
    }
}
