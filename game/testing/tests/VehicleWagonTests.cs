using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Guards the separate command/server and MP replica builders against silent jeep fallback.
    public sealed class VehicleWagonTests : GameTest
    {
        public override string Name => "vehicle.wagon";

        public override IEnumerable<Step> Run()
        {
            var wagon = Vehicle.BuildByName("wagon", 3);
            wagon.Freeze = true;
            World.AddChild(wagon);
            var sedan = Vehicle.BuildByName("sedan", 3);
            sedan.Freeze = true;
            sedan.Position = new Vector3(12f, 0f, 0f);
            World.AddChild(sedan);
            var replica = Vehicle.BuildPuppetByName("wagon", 3);
            replica.Position = new Vector3(24f, 0f, 0f);
            World.AddChild(replica);

            T.Check("command vehicle resolves to wagon", wagon.SpecKey == "wagon" && wagon.DisplayName == "Station Wagon");
            T.Check("wagon is in console/network catalogue once", Vehicle.SpecNames.Count(n => n == "wagon") == 1);
            T.Check("sedan's four seat positions retained", wagon.SeatCount == 4 && wagon.SeatLocals.SequenceEqual(sedan.SeatLocals));
            T.Check("driver body offset retained on server and replica", wagon.SeatOffset == sedan.SeatOffset && replica.SeatOffset == wagon.SeatOffset);
            T.Check("roof registers a cabin", wagon.HasCabin && wagon.GetNodeOrNull<CollisionShape3D>("RoofBox") != null);
            T.Check("all eight window apertures load", wagon.GlassCount == 8);
            T.Check("four real wheels and four replica wheels", wagon.GetChildren().OfType<VehicleWheel3D>().Count() == 4 && replica.Wheels.Length == 4);

            var expected = ContentProvider.ParseObj("res://content/wagon_body.txt");
            var actual = wagon.GetNodeOrNull<MeshInstance3D>("Body")?.Mesh;
            var replicated = replica.GetNodeOrNull<MeshInstance3D>("Body")?.Mesh;
            T.Check("server body is the authored wagon mesh", actual != null && actual == expected);
            T.Check("MP lookup uses the same wagon mesh", replicated != null && replicated == expected);
            if (actual != null)
            {
                var bounds = actual.GetAabb();
                T.Check("body dimensions match measured design", bounds.Size.DistanceTo(new Vector3(2.52f, 2.44f, 5.487996f)) < 0.00001f);
            }
            Vehicle.GetBodyBox("wagon", out var size, out var center);
            var hull = new Aabb(center - size / 2f, size).Grow(0.00001f);
            // THE HULL IS A FITTED LOWER SHELL AND MUST NOT ENCLOSE THE BODY. An earlier pass asserted
            // hull.Encloses(body), which is only satisfiable by a box that swallows the greenhouse --
            // making the window apertures solid and doubling up with RoofBox("Station Wagon"). The
            // SEDAN is the control: it is the shape this wagon was measured against, it is a shipped
            // roofed car, and its own hull does not enclose its own body either. If containment ever
            // becomes the rule, this control fails first and says so.
            Vehicle.GetBodyBox("sedan", out var sedanSize, out var sedanCenter);
            var sedanHull = new Aabb(sedanCenter - sedanSize / 2f, sedanSize).Grow(0.00001f);
            var sedanBody = ContentProvider.ParseObj("res://content/sedan_body.txt");
            T.Check("CONTROL: the sedan's own hull does not enclose the sedan body either",
                    sedanBody != null && !sedanHull.Encloses(sedanBody.GetAabb()));
            T.Check("wagon hull is fitted, not enclosing, like every roofed car",
                    actual != null && !hull.Encloses(actual.GetAabb()));
            T.Check("wagon hull stops below the 1.10 beltline so the glasshouse stays open",
                    hull.Position.Y + hull.Size.Y < 1.10f);
            T.Check("wagon hull is centred on the real extents, not drifted off the mesh",
                    Mathf.Abs(center.Z - (-0.061f)) < 0.005f);
            foreach (var end in new[] { "front", "rear" })
            {
                string partName = $"wagon_bumper_{end}";
                var bumper = ContentProvider.ParseObj($"res://content/{partName}.txt");
                T.Check($"{end} donor bumper loads on server and replica", bumper != null
                    && wagon.GetNodeOrNull<MeshInstance3D>(partName)?.Mesh == bumper
                    && replica.GetChildren().OfType<MeshInstance3D>().Any(mi => mi.Mesh == bumper));
                // The bumpers are the vehicle's real contact surface, so the hull has to reach them in
                // Z even though it deliberately falls short of the body in Y. Checked on the one axis
                // that matters rather than as full containment.
                T.Check($"hull spans the {end} bumper in Z", bumper != null
                        && hull.Position.Z <= bumper.GetAabb().Position.Z + 0.15f
                        && hull.End.Z >= bumper.GetAabb().End.Z - 0.15f);
            }
            T.Check("Golf headlights retain all 40 triangles", ContentProvider.ParseObj("res://content/wagon_headlights.txt")?.GetFaces().Length == 120);
            T.Check("Golf taillights retain all 20 triangles", ContentProvider.ParseObj("res://content/wagon_taillights.txt")?.GetFaces().Length == 60);
            T.Check("capacities retained from sedan", wagon.FuelMax == sedan.FuelMax && wagon.HealthMax == sedan.HealthMax);
            T.Check("mass between sedan and police", Mathf.IsEqualApprox(wagon.Mass, 1650f));
            yield return Ticks(1);
        }
    }
}
