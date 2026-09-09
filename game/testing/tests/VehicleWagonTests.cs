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

            // The spec KEY stays "wagon" -- SpecNames indices ARE the replicated TypeIds and this is 32 --
            // while the DISPLAY name is now SUV. "suv" is an alias into the same spec, the way "off_roader"
            // and "vw_golf" already are, so both spawn commands must land on the same vehicle.
            T.Check("both spawn names resolve to the same vehicle, displayed as SUV",
                wagon.SpecKey == "wagon" && wagon.DisplayName == "SUV"
                && Vehicle.BuildByName("suv") is Vehicle alias && alias.DisplayName == "SUV");
            T.Check("wagon is in console/network catalogue once", Vehicle.SpecNames.Count(n => n == "wagon") == 1);
            var cabinShift = new Vector3(0f, 0f, 0.205f);
            T.Check("front seat table follows steering; rear row retained", wagon.SeatCount == 4
                && wagon.SeatLocals.Select((p, i) => p.IsEqualApprox(sedan.SeatLocals[i] + (i < 2 ? cabinShift : Vector3.Zero))).All(ok => ok));
            T.Check("driver body and camera anchor follows front row on server and replica",
                wagon.SeatOffset.IsEqualApprox(sedan.SeatOffset + cabinShift) && replica.SeatOffset == wagon.SeatOffset);
            T.Check("rear passenger body positions retained", wagon.SeatBodyLocal(2).IsEqualApprox(sedan.SeatBodyLocal(2))
                && wagon.SeatBodyLocal(3).IsEqualApprox(sedan.SeatBodyLocal(3)));
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
                // Height 2.440 -> 2.329 and length 5.487996 -> 5.483611 because the floor came up from -0.270
                // to the fleet's outer-flank height of -0.159; the nose meets the sloping fascia higher up,
                // so it also loses 4.4 mm of length. Width is untouched at 2.520.
                T.Check("body dimensions match measured design", bounds.Size.DistanceTo(new Vector3(2.52f, 2.329f, 5.483611f)) < 0.00001f);
            }
            Vehicle.GetBodyBox("wagon", out var size, out var center);
            var hull = new Aabb(center - size / 2f, size).Grow(0.00001f);
            // THE HULL IS A FITTED LOWER SHELL AND MUST NOT ENCLOSE THE BODY. An earlier pass asserted
            // hull.Encloses(body), which is only satisfiable by a box that swallows the greenhouse --
            // making the window apertures solid and doubling up with RoofBox("SUVWagon"). The
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
                // FLUSH with the floor, not floor + 0.115. The +0.115 offset came from measuring the
                // sedan's hidden inboard belly (-0.273); its VISIBLE outer flank bottoms at -0.159,
                // which is also where its bumper lip sits -- same height, no step. The wagon's floor is
                // now at that outer height too, so the lip meets it.
                T.Check($"{end} bumper lip sits flush with the floor", bumper != null && actual != null
                    && Mathf.Abs(bumper.GetAabb().Position.Y - actual.GetAabb().Position.Y) < 0.00001f);
            }
            T.Check("sedan headlights retain all 20 triangles", ContentProvider.ParseObj("res://content/wagon_headlights.txt")?.GetFaces().Length == 60);
            T.Check("sedan taillights retain all 20 triangles", ContentProvider.ParseObj("res://content/wagon_taillights.txt")?.GetFaces().Length == 60);
            var seats = ContentProvider.ParseObj("res://content/wagon_seats.txt");
            T.Check("wagon's own seat mesh loads on server and replica", seats != null
                && wagon.GetNodeOrNull<MeshInstance3D>("wagon_seats")?.Mesh == seats
                && replica.GetChildren().OfType<MeshInstance3D>().Any(mi => mi.Mesh == seats));
            T.Check("visible front seats follow seat table; rear mesh retained", TranslatedMeshMatches(
                ContentProvider.ParseObj("res://content/sedan_seats.txt"), seats, 0.205f, frontOnly: true));
            var steering = wagon.FindChild("wagon_steer", true, false) as MeshInstance3D;
            T.Check("steering mesh moves back 0.205 m", TranslatedMeshMatches(
                ContentProvider.ParseObj("res://content/sedan_steer.txt"), steering?.Mesh, 0.205f));
            var pivot = new Vector3(-0.464f, 0.894f, -1.211f);
            T.Check("steering pivots and baked mesh offsets agree on server and replica", steering != null
                && steering.GetParent<Node3D>().Position.IsEqualApprox(pivot) && steering.Position.IsEqualApprox(-pivot)
                && replica.SteerPivot != null && replica.SteerPivot.Position.IsEqualApprox(pivot)
                && replica.SteerPivot.GetChildren().OfType<MeshInstance3D>().Any(mi => mi.Mesh == steering.Mesh && mi.Position.IsEqualApprox(-pivot)));
            if (steering?.Mesh != null)
            {
                var wheelBounds = steering.Mesh.GetAabb();
                float poke = -1.25f - wheelBounds.Position.Z;
                float reach = wagon.SeatLocals[0].Z - wheelBounds.GetCenter().Z;
                T.Check("steering wheel reaches cabin within fleet band", poke >= 0.11f && poke <= 0.13f);
                T.Check("wheel-to-driver-seat reach stays in fleet band", reach >= 0.79f && reach <= 0.83f);
            }
            var pipe = ContentProvider.ParseObj("res://content/wagon_exhaust.txt");
            // Derive the tip from the PIPE, not a literal: a hardcoded coordinate here goes stale the
            // moment the outlet moves and fails as though the geometry were broken. What matters is the
            // relationships -- smoke starts at the tip, the tip clears the valance, and the pipe is
            // longer than it is wide (it read as a bulb when it was not).
            var pipeBox = pipe?.GetAabb() ?? default;
            // The duct is TAPERED, so its outlet is the centroid of the face at the rearmost Z --
            // not the AABB centre, which the higher forward end drags 83 mm above the actual opening.
            var pipeVerts = pipe?.GetFaces() ?? System.Array.Empty<Vector3>();
            var mouth = pipeVerts.Where(v => Mathf.Abs(v.Z - pipeBox.End.Z) < 0.0005f).ToArray();
            var tip = mouth.Length > 0
                ? new Vector3(pipeBox.GetCenter().X, mouth.Average(v => v.Y), pipeBox.End.Z)
                : new Vector3(pipeBox.GetCenter().X, pipeBox.GetCenter().Y, pipeBox.End.Z);
            T.Check("sedan exhaust duct (10 triangles) loads on server and replica", pipe?.GetFaces().Length == 30
                && wagon.GetNodeOrNull<MeshInstance3D>("wagon_exhaust")?.Mesh == pipe
                && replica.GetChildren().OfType<MeshInstance3D>().Any(mi => mi.Mesh == pipe));
            T.Check("smoke leaves the duct outlet, which clears the rear valance", pipe != null
                // 1 mm, not IsEqualApprox: ExhaustPos is a rounded float literal in the spec and the
                // outlet centroid is not, so an exact compare fails on 4-decimal rounding rather than
                // on anything a player could see. What matters is that the smoke starts in the pipe.
                && wagon.GetChildren().OfType<CpuParticles3D>().Any(p => p.Direction == new Vector3(0f, 0.35f, 1f)
                        && p.Position.DistanceTo(tip) < 0.002f)
                && actual != null && tip.Z > actual.GetAabb().End.Z);
            var defaultTip = new Vector3(sedanSize.X / 2f - 0.3f, Mathf.Max(0.22f, sedanCenter.Y - sedanSize.Y / 2f + 0.18f), sedanCenter.Z + sedanSize.Z / 2f - 0.05f);
            T.Check("CONTROL: sedan exhaust retains fleet formula", sedan.GetChildren().OfType<CpuParticles3D>()
                .Any(p => p.Direction == new Vector3(0f, 0.35f, 1f) && p.Position.IsEqualApprox(defaultTip)));
            // Health still tracks the sedan; FUEL deliberately no longer does -- it is balanced with the
            // off-road block now, which runs 60-95 kL against the cars' 45-50.
            T.Check($"health retained from the sedan, fuel raised to the off-road block ({wagon.FuelMax:0} vs {sedan.FuelMax:0})",
                wagon.HealthMax == sedan.HealthMax && wagon.FuelMax > sedan.FuelMax);
            T.Check("SUV mass, between the jeep and the off-roader", Mathf.IsEqualApprox(wagon.Mass, 1850f));
            // The point of the rebrand: it must no longer out-run the car it was derived from.
            T.Check($"an SUV does not out-run the sedan ({wagon.SpeedMaxForward:0.0} vs {sedan.SpeedMaxForward:0.0} m/s)",
                wagon.SpeedMaxForward < sedan.SpeedMaxForward);
            yield return Ticks(1);
        }

        static bool TranslatedMeshMatches(Mesh source, Mesh moved, float dz, bool frontOnly = false)
        {
            if (source == null || moved == null) return false;
            var before = source.GetFaces();
            var after = moved.GetFaces();
            return before.Length == after.Length && before.Select((p, i) =>
                (p + new Vector3(0f, 0f, !frontOnly || p.Z < 0f ? dz : 0f)).DistanceTo(after[i]) < 0.00001f).All(ok => ok);
        }
    }
}
