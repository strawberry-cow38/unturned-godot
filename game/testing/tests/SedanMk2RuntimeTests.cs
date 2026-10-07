using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnturnedGodot.Testing
{
    public sealed class SedanMk2RuntimeTests : GameTest
    {
        public override string Name => "vehicle.sedan_mk2_runtime";
        public override double TimeoutSimSeconds => 35;
        static void UI(PlayerController p, string method, Vehicle car) => typeof(PlayerController)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(p, new object[] { car });
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var car = Vehicle.BuildSedanMk2(5); World.AddChild(car); car.Position = new Vector3(0, 1.2f, 0);
            // Same infinite flat floor, same spawn height and paint; keep stock well away from the Mk II.
            var stock = Vehicle.BuildSedan(5); World.AddChild(stock); stock.Position = new Vector3(20, 1.2f, 0);
            yield return Ticks(120);
            var stockRest = stock.GlobalPosition; var mk2Rest = car.GlobalPosition;
            yield return Ticks(20);
            foreach (var parked in new[] { stock, car })
                T.Check($"{parked.DisplayName}: settles on four contacts with low linear/angular velocity",
                    parked.DebugWheelNodes.Count == 4 && parked.DebugWheelNodes.All(w => w.IsInContact())
                    && parked.LinearVelocity.Length() < .1f && parked.AngularVelocity.Length() < .1f);
            float stockYDrift = Mathf.Abs(stock.GlobalPosition.Y - stockRest.Y);
            float mk2YDrift = Mathf.Abs(car.GlobalPosition.Y - mk2Rest.Y);
            T.Check($"both parked ride heights remain settled over the final 20 ticks (stock={stockYDrift:F5}, mk2={mk2YDrift:F5} m)",
                stockYDrift < .005f && mk2YDrift < .005f);
            GD.Print($"[mk2-rest-height] stock={stock.GlobalPosition.Y:F5} mk2={car.GlobalPosition.Y:F5} finalYDrift={stockYDrift:F5}/{mk2YDrift:F5}");
            float rootDelta = Mathf.Abs(car.GlobalPosition.Y - stock.GlobalPosition.Y);
            float bodyDelta = Mathf.Abs(car.GetNode<MeshInstance3D>("Body").GlobalPosition.Y
                - stock.GetNode<MeshInstance3D>("Body").GlobalPosition.Y);
            T.Check($"Mk II matches stock ROOT resting height within 25 mm (stock={stock.GlobalPosition.Y:F4}, mk2={car.GlobalPosition.Y:F4})",
                rootDelta <= .025f);
            // Compare body origins, not roof/bounds tops: the intentional 1.06 scale stays unchanged.
            T.Check($"Mk II matches stock BODY-origin resting height within 25 mm (delta={bodyDelta:F4})", bodyDelta <= .025f);
            T.Check("chassis stands above the ground", car.GlobalPosition.Y > .1f && car.GlobalPosition.Y < 1.2f);
            stock.QueueFree();
            var wheelAudit = new System.Collections.Generic.List<object>();
            for(int i=0;i<4;i++)
            {
                var mi=car.RimNodeForTest(i); var centre=car.ToLocal(mi.GlobalPosition);
                float maxOver=0; float axle=i<2?-1.9292f:1.949f; int sampled=0;
                foreach(var vertex in mi.Mesh.GetFaces())
                {
                    var v=car.ToLocal(mi.ToGlobal(vertex));
                    if(Mathf.Abs(v.X)<.830f || Mathf.Abs(v.X)>1.275f || v.Y<-.13f) continue;
                    sampled++;
                    if(v.Y<0f)
                    {
                        // Straight U legs below the centre, ending at body Y=-.13.
                        maxOver=Mathf.Max(maxOver,Mathf.Abs(v.Z-axle)-.70f);
                        continue;
                    }
                    for(int k=0;k<8;k++)
                    {
                        float a=(k+.5f)*Mathf.Pi/8;
                        maxOver=Mathf.Max(maxOver,(v.Z-axle)*Mathf.Cos(a)+v.Y*Mathf.Sin(a)-.70f*Mathf.Cos(Mathf.Pi/16));
                    }
                }
                T.Check($"loaded tyre {i}: visible tread clears its faceted housing", sampled > 0 && maxOver < .002f);
                GD.Print($"[mk2-loaded-wheel] {i} local centre={centre} max arch-plane overrun={maxOver:F5}");
                wheelAudit.Add(new {wheel=i, centre=new[]{centre.X,centre.Y,centre.Z}, maxOverrun=maxOver});
            }
            var auditPath=System.Environment.GetEnvironmentVariable("UG_MK2_WHEELAUDIT");
            if(!string.IsNullOrEmpty(auditPath)) System.IO.File.WriteAllText(auditPath,System.Text.Json.JsonSerializer.Serialize(wheelAudit));

            var player = new PlayerController(); World.AddChild(player); player.Position = new Vector3(-5, 1, 0);
            yield return Ticks(3);
            player.EnterVehicle(car, 0);
            T.Check("real boarding selects driver and only its door", player.IsDriving && player.SeatIndex == 0
                && car.AuthoredPanelRig.IsOpen(0) && !car.AuthoredPanelRig.IsOpen(1));
            yield return Ticks(25);
            T.Check("actual physics loop animates door", car.AuthoredPanelRig.GetFraction(0) > .99f);
            var eye = car.ToLocal(player.Camera.GlobalPosition);
            T.Check($"actual seated eye sits inside the new cabin ({eye})", Mathf.Abs(eye.X - car.SeatBodyLocal(0).X) < .08f
                && eye.Y > 1.2f && eye.Y < 2.31f && eye.Z > car.SteerPivotLocal.Z + .1f && eye.Z < car.SeatLocal(0).Z + .5f);
            yield return Ticks(150);
            T.Check("door finishes auto-closing on parked car", car.AuthoredPanelRig.GetFraction(0) == 0);
            T.Check("interior seat switch succeeds", player.TrySwitchSeat(1));
            T.Check("seat switch is not an exterior-door event", !car.AuthoredPanelRig.IsOpen(0) && !car.AuthoredPanelRig.IsOpen(1));
            T.Check("clear passenger door accepts real safe exit", player.TryExitVehicle());
            T.Check("real exit pulses correct passenger door", !player.IsDriving && car.AuthoredPanelRig.IsOpen(1));
            yield return Ticks(160);
            UI(player, "OpenVehicleHood", car);
            var mechanics = player.GetChildren().OfType<MechanicsPanel>().Single();
            T.Check("real mechanics UI opens the hood", mechanics.IsOpen && car.AuthoredPanelRig.IsOpen(4));
            yield return Ticks(25);
            T.Check("hood reaches its authored open pose", car.AuthoredPanelRig.GetFraction(4) > .99f);
            mechanics.Hide();
            T.Check("closing mechanics releases the hood", !car.AuthoredPanelRig.IsOpen(4));
            UI(player, "OpenVehicleTrunk", car);
            T.Check("real storage UI opens trunk", car.AuthoredPanelRig.IsOpen(5));
            var trunk = car.EnsureTrunk();
            player.CloseCrate();
            T.Check("closing storage releases trunk, retains same crate", !car.AuthoredPanelRig.IsOpen(5)
                && object.ReferenceEquals(trunk, car.EnsureTrunk()));
            UI(player, "OpenVehicleTrunk", car);
            player.QueueFree(); yield return Ticks(3);
            T.Check("player teardown releases trunk lid", !car.AuthoredPanelRig.IsOpen(5));
            yield return Ticks(50);
            var pilot = new PlayerController(); World.AddChild(pilot); pilot.Position = new Vector3(-5, 1, 0);
            yield return Ticks(3);
            pilot.EnterVehicle(car, 0); pilot.ScriptedDrive = new Vector2(0, 1); // held W through real ignition/driver path
            yield return Ticks(200);
            float movingSpeed = car.LinearVelocity.Length();
            T.Check($"new vehicle drives on native wheels (speed={movingSpeed:0.00}, Z={car.Position.Z:0.00})", movingSpeed > 2f && car.Position.Z < -1f);
            pilot.ScriptedDrive = Vector2.Zero;
            pilot.ExitVehicleAt(car.GlobalPosition + Vector3.Left * 4 + Vector3.Up);
            for (int i=0;i<100;i++) { car.Drive(0, 0, true); yield return Ticks(1); }
            T.Check("held handbrake slows the new vehicle", car.LinearVelocity.Length() < 2f);
            pilot.QueueFree();
        }
    }
}
