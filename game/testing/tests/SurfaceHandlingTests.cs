using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Does the ground change how a vehicle DRIVES, not just how it grips? strawberry 2026-10-10:
    /// "wire up different vehicle handling on different surfaces, ie offroad (grass), offroad (dirt, better than
    /// grass), onroad, on rails, as well as driving through bushes which slow the vehicle down."
    ///
    /// ⚠ HALF OF THIS WAS ALREADY BUILT, and that is exactly why the end-to-end leg below exists. Vehicle.GripFor
    /// has graded grass/dirt/sand since 2026-09-07 and it is genuinely wired (traction clamp + WheelFrictionSlip),
    /// so a test that only asserted the grip table would have passed before this change and reported the feature
    /// as delivered. What was missing is that grip does nothing to a car travelling in a straight line below its
    /// traction limit -- so a car crossed a meadow at its full motorway top speed, which is the thing a driver
    /// actually notices. RollDragFor is the new half, and the only check that can tell the two apart is one that
    /// measures ACHIEVED SPEED on different ground.</summary>
    public sealed class SurfaceHandlingTests : GameTest
    {
        public override string Name => "vehicle.surface_handling";
        public override double TimeoutSimSeconds => 400;

        const int RunTicks = 900;   // 18 s flat out: long enough for the heavy hulls to reach drag equilibrium

        public override IEnumerable<Step> Run()
        {
            // ---- 1. THE ROLLING-DRAG TABLE, pure. Ordered, not merely "not road": a table where the loose
            // surfaces collapsed to one number would satisfy every "is it worse than tarmac" check and model
            // nothing. This is the same shape the grip table's ordering check has.
            T.Check($"tarmac is the 1.0 reference, so nothing on-road changes ({Vehicle.RollDragFor(PlayerController.Surf.Concrete, false):0.00})",
                    Mathf.IsEqualApprox(Vehicle.RollDragFor(PlayerController.Surf.Concrete, false), 1f));
            T.Check($"road < dirt < rails < grass < sand ({Vehicle.RollRoad:0.0} < {Vehicle.RollDirt:0.0} < {Vehicle.RollRails:0.0} < {Vehicle.RollGrass:0.0} < {Vehicle.RollSand:0.0})",
                    Vehicle.RollRoad < Vehicle.RollDirt && Vehicle.RollDirt < Vehicle.RollRails
                    && Vehicle.RollRails < Vehicle.RollGrass && Vehicle.RollGrass < Vehicle.RollSand);
            // master asked for dirt to be "better than grass" in so many words. It is the one ordering named in
            // the request, so it gets its own check rather than being implied by the chain above.
            T.Check($"dirt is better than grass, both ways round (grip {Vehicle.GripDirt:0.00}>{Vehicle.GripGrass:0.00}, drag {Vehicle.RollDirt:0.0}<{Vehicle.RollGrass:0.0})",
                    Vehicle.GripDirt > Vehicle.GripGrass && Vehicle.RollDirt < Vehicle.RollGrass);

            // ---- 2. RAILS ARE THEIR OWN CASE, not a reskin of gravel: firm underfoot (grip near tarmac) and
            // slow (sleepers). A railway that merely copied another row would pass an "is it in the table" test.
            float railGrip = Vehicle.GripFor(PlayerController.Surf.Rails, false);
            float gravelGrip = Vehicle.GripFor(PlayerController.Surf.Gravel, false);
            T.Check($"rails grip near tarmac ({railGrip:0.00}) and well above gravel ({gravelGrip:0.00})",
                    railGrip > 0.85f && railGrip > gravelGrip);
            T.Check($"...but drag much worse than gravel's ({Vehicle.RollRails:0.0} vs {Vehicle.RollDragFor(PlayerController.Surf.Gravel, false):0.0})",
                    Vehicle.RollRails > Vehicle.RollDragFor(PlayerController.Surf.Gravel, false) * 1.5f);

            // ---- 3. KNOBBLIES RECOVER BOTH HALVES, toward road, by the same fraction. If only grip recovered,
            // an off-roader would corner better on sand and still bog down in it exactly like a saloon.
            foreach (var s in new[] { PlayerController.Surf.Dirt, PlayerController.Surf.Grass, PlayerController.Surf.Sand })
            {
                float car = Vehicle.RollDragFor(s, false), off = Vehicle.RollDragFor(s, true);
                GD.Print($"[surf] {s}: roll drag road tyres x{car:0.00}, knobblies x{off:0.00}");
                T.Check($"{s}: knobblies drag less than road tyres ({off:0.00} < {car:0.00})", off < car - 0.05f);
                T.Check($"{s}: but they still notice the ground ({off:0.00} > 1.00)", off > 1.01f);
            }

            // ---- 4. BUSHES. Pure, before any world exists -- an empty field must be a no-op, because that is
            // the state every scene without undergrowth is in and it must not change how anything drives.
            BushField.Clear();
            T.Check($"an empty bush field drags nothing ({BushField.DragAt(Vector3.Zero, 1f):0.000})",
                    Mathf.IsEqualApprox(BushField.DragAt(Vector3.Zero, 1f), 1f));
            BushField.NoteRadius(1.2f);
            BushField.Add(new Vector3(0f, 0f, 0f), 1.2f);
            float centre = BushField.DragAt(Vector3.Zero, 1f);
            float edge = BushField.DragAt(new Vector3(2.0f, 0f, 0f), 1f);
            float clear = BushField.DragAt(new Vector3(40f, 0f, 0f), 1f);
            GD.Print($"[surf] bush drag: centre x{centre:0.00}, clipping the edge x{edge:0.00}, 40 m away x{clear:0.00}");
            T.Check($"driving into a bush costs you ({centre:0.00} > 1.00)", centre > 1.1f);
            T.Check($"...clipping its edge costs LESS than the middle ({edge:0.00} < {centre:0.00})", edge < centre && edge >= 1f);
            T.Check($"...and a bush 40 m away costs nothing ({clear:0.000})", Mathf.IsEqualApprox(clear, 1f));
            // HEIGHT GATE. The grid is 2D and the world is not: without this a bridge deck or a car park's
            // upper floor is dragged by the hedge growing underneath it.
            float above = BushField.DragAt(new Vector3(0f, 12f, 0f), 1f);
            T.Check($"a bush 12 m BELOW the vehicle does not drag it ({above:0.000})", Mathf.IsEqualApprox(above, 1f));
            // THE CAP. A thicket must not multiply into an invisible wall -- the whole reason these are not
            // colliders is that driving into undergrowth should not stop you dead.
            for (int i = 0; i < 40; i++) BushField.Add(new Vector3(0.05f * i, 0f, 0.05f * i), 1.2f);
            float thicket = BushField.DragAt(Vector3.Zero, 1f);
            GD.Print($"[surf] 41 bushes stacked on one point: x{thicket:0.00} (cap {1f + BushField.MaxStack * BushField.DragPerBush:0.00})");
            T.Check($"a thicket is capped, not a brick wall ({thicket:0.00} <= {1f + BushField.MaxStack * BushField.DragPerBush:0.00})",
                    thicket <= 1f + BushField.MaxStack * BushField.DragPerBush + 0.001f);
            BushField.Clear();

            // ---- 5. END TO END: THE SAME CAR, FLAT OUT, ON THREE GROUNDS. This is the check the table checks
            // cannot stand in for. Grip was already wired before today and would make all three legs identical,
            // because a car running in a straight line below its traction limit never asks grip for anything.
            var speeds = new Dictionary<string, float>();
            foreach (var (label, surf) in new[] { ("road", PlayerController.Surf.Concrete),
                                                  ("dirt", PlayerController.Surf.Dirt),
                                                  ("rails", PlayerController.Surf.Rails),
                                                  ("grass", PlayerController.Surf.Grass) })
            {
                float top = 0f;
                foreach (var st in FlatOut("sedan", surf, t => top = t)) yield return st;
                speeds[label] = top;
                GD.Print($"[surf] sedan flat out on {label}: {top:0.00} m/s");
            }
            T.Check($"a car is slower on dirt than on road ({speeds["dirt"]:0.0} < {speeds["road"]:0.0} m/s)",
                    speeds["dirt"] < speeds["road"] * 0.98f);
            T.Check($"...slower again on rails ({speeds["rails"]:0.0} < {speeds["dirt"]:0.0})", speeds["rails"] < speeds["dirt"]);
            T.Check($"...and slowest across a field ({speeds["grass"]:0.0} < {speeds["rails"]:0.0})", speeds["grass"] < speeds["rails"]);
            // The ROAD leg is the control: an unlabelled/concrete floor must still reach the hull's solved
            // equilibrium, or this test is measuring a car that got slower everywhere rather than a surface model.
            var ref0 = Vehicle.BuildByName("sedan"); World.AddChild(ref0); yield return Ticks(1);
            float target = ref0.SpeedMaxForward; World.RemoveChild(ref0); ref0.QueueFree();
            T.Check($"...while the road leg still reaches its solved top speed ({speeds["road"]:0.0} of {target:0.0} m/s)",
                    speeds["road"] > target * 0.80f);
        }

        /// <summary>Full throttle down a long plate tagged with one surface; report the top speed reached.</summary>
        IEnumerable<Step> FlatOut(string car, PlayerController.Surf surf, System.Action<float> report)
        {
            var plate = new StaticBody3D { CollisionLayer = 1 << 0 };
            plate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 2f, 3000f) } });
            World.AddChild(plate);
            plate.GlobalPosition = new Vector3(0f, -1f, 0f);
            // Concrete is left UNTAGGED on purpose: it is both the road case and the every-warehouse-floor
            // fallback, so tagging it would stop this leg from covering the path an unlabelled collider takes.
            if (surf != PlayerController.Surf.Concrete) plate.SetMeta(PlayerController.SurfMeta, (int)surf);

            var v = Vehicle.BuildByName(car);
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 1.2f, 1400f);
            v.Brake = 60f;
            yield return Ticks(150);     // settle, and let the 10 Hz surface sample land before the throttle
            v.EngineOn = true; v.Wake(); v.Brake = 0f;
            yield return Ticks(10);      // one sample with the engine on (the probe skips parked cars)
            float top = 0f;
            for (int i = 0; i < RunTicks; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                var hv = new Vector3(v.LinearVelocity.X, 0f, v.LinearVelocity.Z);
                top = Mathf.Max(top, hv.Length());
            }
            report(top);
            World.RemoveChild(v); v.QueueFree();
            World.RemoveChild(plate); plate.QueueFree();
            yield return Ticks(5);
        }
    }
}
