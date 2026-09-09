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
            var order = new[] { "dinky_trailer", "small_trailer", "medium_trailer", "large_trailer", "horsebox_trailer" };
            T.Check("the trailers are appended after the SUV TypeId, in size order",
                order.Select((n, i) => System.Array.IndexOf(Vehicle.SpecNames, n)
                                       == System.Array.IndexOf(Vehicle.SpecNames, "wagon") + 1 + i).All(ok => ok)
                && Vehicle.SpecNames.Last() == order.Last());
            T.Check("server and replica load authored mesh", trailer.GetNode<MeshInstance3D>("Body").Mesh != null
                && puppet.GetChildren().OfType<MeshInstance3D>().Any(n => n.Mesh == ContentProvider.ParseObj("res://content/dinky_trailer_body.txt")));
            var wheels = trailer.GetChildren().OfType<VehicleWheel3D>().ToArray();
            T.Check("one axle, two passive unsteered wheels on both paths", wheels.Length == 2 && puppet.Wheels.Length == 2
                && wheels.All(w => !w.UseAsTraction && !w.UseAsSteering));
            // The medium is the only tandem: four wheels, two per axle, and the two axle Z values must
            // be distinct -- four wheels stacked on one Z would pass a bare count check.
            var med = Vehicle.BuildByName("large_trailer");
            World.AddChild(med); med.Position = new Vector3(-20f, 1f, 0f);
            yield return Ticks(2);
            var medWheels = med.GetChildren().OfType<VehicleWheel3D>().ToArray();
            int medZ = medWheels.Select(w => Mathf.Round(w.Position.Z * 1000f)).Distinct().Count();
            T.Check("large runs two axles, four passive wheels at two distinct Z", medWheels.Length == 4 && medZ == 2
                && medWheels.All(w => !w.UseAsTraction && !w.UseAsSteering));
            // Off the BUILT bodies, not off the specs: the mesh is what a player sees, and it is the
            // thing that would silently stay the old size if the generator had not been re-run.
            Aabb Box(string k) { var v = Vehicle.BuildByName(k); return v.GetNode<MeshInstance3D>("Body").Mesh.GetAabb(); }
            // The horsebox is the large's chassis with a roof on it, so it breaks the size ladder --
            // same deck, taller box. Ordering applies to the four open sizes; the horsebox is checked
            // against the large it is based on instead.
            var open = order.Take(4).Select(Box).ToArray();
            T.Check("each open size is longer and wider than the one below it",
                open.Zip(open.Skip(1), (a, b) => b.Size.Z > a.Size.Z && b.Size.X > a.Size.X).All(ok => ok));
            Aabb bl = Box("large_trailer"), bh = Box("horsebox_trailer");
            T.Check("horsebox is the large's deck, enclosed and taller",
                Mathf.Abs(bh.Size.Z - bl.Size.Z) < 0.001f && Mathf.Abs(bh.Size.X - bl.Size.X) < 0.001f
                && bh.Size.Y > bl.Size.Y + 1f);
            // The two WIDE classes carry their wheels UNDER the deck, so their floors have to clear the
            // tyre. A box that merely got wider without rising would put the wheel through its own
            // floor -- which is the whole cost of removing the width limit, and worth a tripwire.
            foreach (var k in new[] { "medium_trailer", "large_trailer" })
            {
                var v = Vehicle.BuildByName(k);
                var deck = v.GetNode<MeshInstance3D>("Body").Mesh.GetAabb();
                var top = v.GetChildren().OfType<VehicleWheel3D>().Max(w => w.Position.Y) + 0.6f;
                T.Check(k + " deck clears its compressed tyre", deck.Position.Y + deck.Size.Y > top);
            }
            T.Check("no drive classification or drive access zones", trailer.IsTrailer && !trailer.CanTow
                && order.All(n => !Vehicle.IsRoadVehicle(n)) && !Vehicle.IsRoadVehicle("car_trailer"));
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
