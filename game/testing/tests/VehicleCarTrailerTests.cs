using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Construction/coupling regression only. No claim of stable driving or multiplayer hitch interaction.
    public class VehicleCarTrailer : GameTest
    {
        public override string Name => "vehicle.car_trailer";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var trailer = Vehicle.BuildByName("car_trailer");
            trailer.Freeze = true;
            World.AddChild(trailer);
            var puppet = Vehicle.BuildPuppetByName("car_trailer", 0);
            World.AddChild(puppet);
            puppet.Position = new Vector3(20f, 1f, 0f);
            yield return Ticks(2);

            T.Check("canonical server and replica identity", trailer.SpecKey == "car_trailer"
                && trailer.DisplayName == "Car Trailer" && puppet.SpecKey == trailer.SpecKey);
            T.Check("trailer is appended after the SUV TypeId", Vehicle.SpecNames.Last() == "car_trailer"
                && System.Array.IndexOf(Vehicle.SpecNames, "car_trailer") == System.Array.IndexOf(Vehicle.SpecNames, "wagon") + 1);
            T.Check("server and replica load authored mesh", trailer.GetNode<MeshInstance3D>("Body").Mesh != null
                && puppet.GetChildren().OfType<MeshInstance3D>().Any(n => n.Mesh == ContentProvider.ParseObj("res://content/car_trailer_body.txt")));
            var wheels = trailer.GetChildren().OfType<VehicleWheel3D>().ToArray();
            T.Check("one axle, two passive unsteered wheels on both paths", wheels.Length == 2 && puppet.Wheels.Length == 2
                && wheels.All(w => !w.UseAsTraction && !w.UseAsSteering));
            T.Check("no drive classification or drive access zones", trailer.IsTrailer && !trailer.CanTow
                && !Vehicle.IsRoadVehicle("car_trailer"));
            var gear = trailer.GetNode<CollisionShape3D>("LandingGear");
            T.Check("parked support exists and is deployed", gear != null && !gear.Disabled);

            foreach (var name in new[] { "golf", "hatchback", "sedan", "wagon", "jeep", "offroader", "truck", "van" })
            {
                var car = Vehicle.BuildByName(name);
                car.Freeze = true;
                World.AddChild(car);
                car.Position = new Vector3(0f, 1f, 0f);
                T.Check(name + " has its own visible receiver", car.CanTow && !car.IsTrailer
                    && car.GetNodeOrNull<MeshInstance3D>(name + "_hitch")?.Mesh != null);
                trailer.Position = car.FifthWheelWorld - trailer.KingpinLocal;
                T.Check(name + " couples through existing PinJoint", car.CoupleTo(trailer)
                    && car.CoupledTrailer == trailer && trailer.CoupledCab == car
                    && World.GetChildren().OfType<PinJoint3D>().Any(j => !j.IsQueuedForDeletion()));
                T.Check(name + " pins align and stand retracts", car.FifthWheelWorld.DistanceTo(trailer.KingpinWorld) < .00001f
                    && gear.Disabled);
                T.Check(name + " imports the geometric yaw limit", trailer.HitchYawLimit < car.HitchYawLimit);
                trailer.Uncouple();
                T.Check(name + " detaches and redeploys stand", car.CoupledTrailer == null && trailer.CoupledCab == null && !gear.Disabled);
                trailer.Freeze = true;
                car.QueueFree();
            }
            yield return Ticks(2);
        }
    }
}
