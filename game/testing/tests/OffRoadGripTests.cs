using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Does the ground under a car mean anything, and do knobby tyres help? strawberry 2026-09-07:
    /// "give quad, offroader, jeep, humvee, 4wd".
    ///
    /// WHY THIS IS A GRIP TEST AND NOT A DIFFERENTIAL ONE. The literal ask was already true before a line was
    /// written: `UseAsTraction = s.Kingpin == Vector3.Zero` gives every non-trailer in the game drive on every
    /// wheel, on one shared tyre mu and one shared WheelFrictionSlip. A ramp probe run before the change
    /// measured what that is worth -- full throttle from rest, 10 s, net height gained:
    ///
    ///     jeep      15/25/35/45 deg: all CLIMBED, 10.7 / 8.2 / 6.5 / 5.1 m/s, peak wheelspin 0.01
    ///     humvee    all CLIMBED, 12.3 / 9.7 / 7.4 / 5.5 m/s
    ///     quad      all CLIMBED
    ///     offroader all CLIMBED
    ///     sedan     all CLIMBED -- and FASTER than the humvee at every gradient (14.5 / 11.7 / 9.1 / 7.4)
    ///     semi      stuck at 25 deg, and NOT on grip: 0.02 slip with 56% of its wheels off the ground
    ///
    /// A 45 degree slope is a 100% grade and the road car took it at 27 km/h with the tyres barely slipping.
    /// So there was nothing to switch on: the sedan was the best off-roader in the game, and no flag could have
    /// changed that, because there was no ground on which a 4x4 could be better. Surfaces had to exist first.
    ///
    /// The checks below are the pair: the table is what the ground costs, and the launch runs are whether the
    /// tyres get it back. The launch runs are on a plate tagged with the SurfMeta the footstep/impact code
    /// already reads, so this exercises the real surface path end to end without standing a terrain up.</summary>
    public class OffRoadGripTests : GameTest
    {
        public override string Name => "vehicle.offroad_grip";
        public override double TimeoutSimSeconds => 400;

        const int LaunchTicks = 100;   // 2 s from rest: the launch is where the traction clamp actually binds

        public override IEnumerable<Step> Run()
        {
            // WARM THE SOLVER FIRST, or the first leg of the first hull is measured against a COLD physics
            // warmstart and every later leg against a warm one -- and since each hull runs road-then-grass,
            // that artifact lands entirely on the road baseline the grass run is divided by. It would have
            // flattered exactly the ratio this test reports. Passive box drops warm it identically to a
            // driving hull (measured in DrivetrainProbe, 62% -> 33.7% airborne either way) without the
            // circularity of warming it with the thing being measured.
            var boxes = new List<RigidBody3D>();
            var pad = new StaticBody3D { CollisionLayer = 1 << 0 };
            pad.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40f, 2f, 40f) } });
            World.AddChild(pad);
            pad.GlobalPosition = new Vector3(-600f, -1f, 0f);
            for (int b = 0; b < 30; b++)
            {
                var box = new RigidBody3D { Mass = 40f };
                box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.5f, 1.5f, 1.5f) } });
                World.AddChild(box);
                box.GlobalPosition = new Vector3(-600f + (b % 6) * 2f, 2f + (b / 6) * 2f, (b % 3) * 2f - 3f);
                boxes.Add(box);
            }
            for (int i = 0; i < 300; i++) yield return Ticks(1);
            foreach (var b in boxes) { World.RemoveChild(b); b.QueueFree(); }
            World.RemoveChild(pad); pad.QueueFree();
            yield return Ticks(5);

            // ---- (1) THE TABLE. Pure, so it says what the model claims independently of any hull.
            float roadCar = Vehicle.GripFor(PlayerController.Surf.Concrete, false);
            float roadOff = Vehicle.GripFor(PlayerController.Surf.Concrete, true);
            T.Check($"tarmac is the 1.0 reference, so nothing on-road moves for anyone ({roadCar:0.00} / {roadOff:0.00})",
                Mathf.IsEqualApprox(roadCar, 1f) && Mathf.IsEqualApprox(roadOff, 1f));

            foreach (var surf in new[] { PlayerController.Surf.Dirt, PlayerController.Surf.Grass, PlayerController.Surf.Sand })
            {
                float car = Vehicle.GripFor(surf, false), off = Vehicle.GripFor(surf, true);
                GD.Print($"[grip] {surf}: road tyres {car:0.00}, knobblies {off:0.00}");
                T.Check($"{surf} costs a road car real grip ({car:0.00})", car < 0.9f);
                T.Check($"{surf}: knobblies keep more of it ({off:0.00} > {car:0.00})", off > car + 0.05f);
                T.Check($"{surf}: but they still notice the ground ({off:0.00} < 1.00)", off < 0.99f);
            }
            // Loose ground is ORDERED. Sand is not merely "not tarmac" -- it is worse than grass, which is
            // worse than a dirt track. A table where the three collapse to one number would pass every check
            // above and still model nothing.
            T.Check($"sand < grass < dirt < road ({Vehicle.GripSand:0.00} < {Vehicle.GripGrass:0.00} < {Vehicle.GripDirt:0.00} < 1.00)",
                Vehicle.GripSand < Vehicle.GripGrass && Vehicle.GripGrass < Vehicle.GripDirt && Vehicle.GripDirt < 1f);

            // ---- (1b) WHO DRIVES WHAT. strawberry 2026-09-07: "nerf all vehicles to be non 4wd unless one of
            // the ones we mentioned." The split is a per-spec rule with no other symptom -- a vehicle added to
            // the fleet silently gets whatever DrivesWheel says, and the first anyone hears of a mistake is a
            // truck that will not climb. So it is asserted per representative rather than inferred from the
            // launch runs, which would pass on a fleet where everything happened to be 2wd.
            foreach (var (car, all) in new[] { ("jeep", true), ("quad", true), ("humvee", true), ("offroader", true),
                                               ("sedan", false), ("semi", false), ("bus", false), ("ural", false) })
            {
                var probe = Vehicle.BuildByName(car);
                World.AddChild(probe);
                yield return Ticks(1);
                int nw = probe.WheelCountForTest, nd = probe.TractionWheelsForTest;
                GD.Print($"[grip] {car}: drives {nd} of {nw} wheels");
                T.Check($"{car} drives {(all ? "every wheel" : "one axle")} ({nd}/{nw})", all ? nd == nw : nd > 0 && nd < nw);
                World.RemoveChild(probe); probe.QueueFree();
                yield return Ticks(1);
            }

            // ---- (2) THE LAUNCH. Same hull, same throttle, two grounds.
            var res = new Dictionary<(string, string), (float dist, float slip, float fric)>();
            foreach (var car in new[] { "jeep", "sedan" })
                foreach (var ground in new[] { "road", "grass" })
                {
                    float d = 0f, sl = 0f, fr = 0f;
                    foreach (var st in Launch(car, ground, r => { d = r.d; sl = r.slip; fr = r.fric; })) yield return st;
                    res[(car, ground)] = (d, sl, fr);
                    GD.Print($"[grip] {car} launch on {ground}: {d:0.00} m in {LaunchTicks * 0.02f:0.0}s | peak slip {sl:0.00} | wheel friction {fr:0.00}");
                }

            float jRoad = res[("jeep", "road")].dist, jGrass = res[("jeep", "grass")].dist;
            float sRoad = res[("sedan", "road")].dist, sGrass = res[("sedan", "grass")].dist;
            float jKeep = jRoad > 0.01f ? jGrass / jRoad : 0f, sKeep = sRoad > 0.01f ? sGrass / sRoad : 0f;
            GD.Print($"[grip] launch kept on grass: jeep {jKeep * 100f:0}% of its road run, sedan {sKeep * 100f:0}%");

            // The discriminating claim, and the one that FAILS on the build this replaces: before the change
            // both ratios were 1.00 because the ground was not consulted at all.
            T.Check($"grass costs the road car a launch ({sKeep * 100f:0}% of its road run)", sKeep < 0.92f);
            T.Check($"the 4x4 keeps most of its launch on grass ({jKeep * 100f:0}%)", jKeep > sKeep + 0.05f);
            T.Check($"...and is slowed by grass at all, rather than being surface-proof ({jKeep * 100f:0}%)", jKeep < 0.995f);
            T.Check($"the road car spins its wheels more on grass ({res[("sedan", "grass")].slip:0.00} vs {res[("jeep", "grass")].slip:0.00})",
                res[("sedan", "grass")].slip > res[("jeep", "grass")].slip);

            // LATERAL grip moved too, read off the wheel Godot is actually cornering with -- not off my own
            // multiplier. A model that only clamped drive force would let this sedan corner on grass as if it
            // were railed, and every check above would still pass.
            T.Check($"loose ground costs the road car cornering grip as well as drive ({res[("sedan", "grass")].fric:0.00} < {res[("sedan", "road")].fric:0.00})",
                res[("sedan", "grass")].fric < res[("sedan", "road")].fric - 0.01f);

            // ---- (3) THE CONTROL. Hard ground is untouched, so nothing that was ever measured on tarmac moved.
            T.Check($"road launches are unchanged: full wheel friction on both hulls ({res[("jeep", "road")].fric:0.00} / {res[("sedan", "road")].fric:0.00})",
                Mathf.IsEqualApprox(res[("jeep", "road")].fric, res[("sedan", "road")].fric));
        }

        IEnumerable<Step> Launch(string car, string ground, System.Action<(float d, float slip, float fric)> report)
        {
            var plate = new StaticBody3D { CollisionLayer = 1 << 0 };
            plate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 2f, 400f) } });
            World.AddChild(plate);
            plate.GlobalPosition = new Vector3(0f, -1f, 0f);
            // "road" = an UNLABELLED collider, which is the case that must not move: the surface probe falls
            // back to Concrete for anything it cannot name, so this is also the every-warehouse-floor path.
            if (ground == "grass") plate.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass);

            var v = Vehicle.BuildByName(car);
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 1.2f, 150f);
            v.Brake = 60f;
            yield return Ticks(150);          // settle, and let the 10 Hz surface sample land before the throttle

            v.EngineOn = true; v.Wake(); v.Brake = 0f;
            yield return Ticks(10);           // one surface sample with the engine on (the probe skips parked cars)
            float z0 = v.GlobalPosition.Z, peak = 0f;
            for (int i = 0; i < LaunchTicks; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                peak = Mathf.Max(peak, v.WheelSlip);
            }
            report((Mathf.Abs(v.GlobalPosition.Z - z0), peak, v.WheelFrictionForTest(0)));

            World.RemoveChild(v); v.QueueFree();
            World.RemoveChild(plate); plate.QueueFree();
            yield return Ticks(5);
        }
    }
}
