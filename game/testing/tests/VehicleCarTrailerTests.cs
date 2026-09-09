using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Construction/coupling regression only. No claim of stable driving or multiplayer hitch interaction.
    public class VehicleCarTrailer : GameTest
    {
        public override string Name => "vehicle.trailers";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var trailer = Vehicle.BuildByName("dinky_trailer");
            trailer.Freeze = true;
            World.AddChild(trailer);
            var puppet = Vehicle.BuildPuppetByName("dinky_trailer", 0);
            World.AddChild(puppet);
            puppet.Position = new Vector3(20f, 1f, 0f);
            yield return Ticks(2);

            T.Check("canonical server and replica identity", trailer.SpecKey == "dinky_trailer"
                && trailer.DisplayName == "Dinky Trailer" && puppet.SpecKey == trailer.SpecKey);
            // "car_trailer" is the name this vehicle shipped under and is kept as an ALIAS, so the
            // spawn command people already have does not die when the family gained a size prefix.
            T.Check("the old car_trailer name still spawns the dinky one",
                Vehicle.BuildByName("car_trailer").SpecKey == "dinky_trailer");
            // The three sizes occupy TypeIds 33/34/35 IN ORDER. SpecNames indices are replicated, so
            // an insert or a reorder silently rebuilds other people's vehicles as something else.
            T.Check("three trailers appended after the SUV TypeId, in size order",
                System.Array.IndexOf(Vehicle.SpecNames, "dinky_trailer") == System.Array.IndexOf(Vehicle.SpecNames, "wagon") + 1
                && System.Array.IndexOf(Vehicle.SpecNames, "small_trailer") == System.Array.IndexOf(Vehicle.SpecNames, "dinky_trailer") + 1
                && System.Array.IndexOf(Vehicle.SpecNames, "medium_trailer") == System.Array.IndexOf(Vehicle.SpecNames, "small_trailer") + 1
                && Vehicle.SpecNames.Last() == "medium_trailer");
            T.Check("server and replica load authored mesh", trailer.GetNode<MeshInstance3D>("Body").Mesh != null
                && puppet.GetChildren().OfType<MeshInstance3D>().Any(n => n.Mesh == ContentProvider.ParseObj("res://content/dinky_trailer_body.txt")));
            var wheels = trailer.GetChildren().OfType<VehicleWheel3D>().ToArray();
            T.Check("one axle, two passive unsteered wheels on both paths", wheels.Length == 2 && puppet.Wheels.Length == 2
                && wheels.All(w => !w.UseAsTraction && !w.UseAsSteering));
            // The medium is the only tandem: four wheels, two per axle, and the two axle Z values must
            // be distinct -- four wheels stacked on one Z would pass a bare count check.
            var med = Vehicle.BuildByName("medium_trailer");
            World.AddChild(med); med.Position = new Vector3(-20f, 1f, 0f);
            yield return Ticks(2);
            var medWheels = med.GetChildren().OfType<VehicleWheel3D>().ToArray();
            int medZ = medWheels.Select(w => Mathf.Round(w.Position.Z * 1000f)).Distinct().Count();
            T.Check("medium runs two axles, four passive wheels at two distinct Z", medWheels.Length == 4 && medZ == 2
                && medWheels.All(w => !w.UseAsTraction && !w.UseAsSteering));
            // Off the BUILT bodies, not off the specs: the mesh is what a player sees, and it is the
            // thing that would silently stay the old size if the generator had not been re-run.
            Aabb Box(string k) { var v = Vehicle.BuildByName(k); return v.GetNode<MeshInstance3D>("Body").Mesh.GetAabb(); }
            Aabb bd = Box("dinky_trailer"), bs = Box("small_trailer"), bm = Box("medium_trailer");
            T.Check("each size is longer and wider than the one below it",
                bs.Size.Z > bd.Size.Z && bm.Size.Z > bs.Size.Z && bs.Size.X > bd.Size.X && bm.Size.X > bs.Size.X);
            T.Check("no drive classification or drive access zones", trailer.IsTrailer && !trailer.CanTow
                && !Vehicle.IsRoadVehicle("dinky_trailer") && !Vehicle.IsRoadVehicle("small_trailer")
                && !Vehicle.IsRoadVehicle("medium_trailer") && !Vehicle.IsRoadVehicle("car_trailer"));
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
