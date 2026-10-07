using Godot;
using SDG.Unturned;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnturnedGodot.Testing
{
    // L1, opt-in registration by the main worker. No render, wire, controller or UI changes.
    public sealed class SedanMk2Tests : GameTest
    {
        public override string Name => "vehicle.sedan_mk2";
        static bool Near(Vector3 a, Vector3 b) => a.DistanceTo(b) < 0.0001f;
        static object Spec(string key) => typeof(Vehicle).GetMethod("SpecFor", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { key });
        static object Field(object spec, string name) => spec.GetType().GetField(name).GetValue(spec);
        static IEnumerable<MeshInstance3D> Meshes(Node root)
        {
            foreach (var child in root.GetChildren())
            {
                if (child is MeshInstance3D mesh) yield return mesh;
                foreach (var nested in Meshes(child)) yield return nested;
            }
        }
        static bool SameGeometry(Mesh a, Mesh b)
        {
            if (a == null || b == null) return false;
            var af = a.GetFaces(); var bf = b.GetFaces();
            return af.Length > 0 && af.Length == bf.Length && af.Zip(bf, Near).All(x => x);
        }
        // Intersect an infinite unit-direction line with actual installed triangles. Thickness is
        // measured through the panel, not the AABB (which would confuse lid slope with thickness).
        static float ThroughThickness(Mesh mesh, Vector3 origin, Vector3 direction)
        {
            var faces = mesh.GetFaces(); var hits = new List<float>();
            for (int i = 0; i + 2 < faces.Length; i += 3)
            {
                var a = faces[i]; var e1 = faces[i + 1] - a; var e2 = faces[i + 2] - a;
                var h = direction.Cross(e2); float det = e1.Dot(h);
                if (Mathf.Abs(det) < .0000001f) continue;
                var t = origin - a; float u = t.Dot(h) / det; var q = t.Cross(e1);
                float v = direction.Dot(q) / det;
                if (u >= -.00001f && v >= -.00001f && u + v <= 1.00001f)
                    hits.Add(e2.Dot(q) / det);
            }
            return hits.Count < 2 ? 0f : hits.Max() - hits.Min();
        }
        // Nearest vertical ray hit on installed triangles, in owner/body coordinates. This does not
        // consult the disabled seated collider or the fitted fallback boxes. NaN means no roof/floor.
        internal static float RayY(MeshInstance3D mesh, Node owner, Vector3 origin, Vector3 direction)
        {
            var faces = mesh.Mesh.GetFaces(); var pose = InOwner(mesh, owner);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i + 2 < faces.Length; i += 3)
            {
                var a = pose * faces[i]; var e1 = pose * faces[i + 1] - a; var e2 = pose * faces[i + 2] - a;
                var h = direction.Cross(e2); float det = e1.Dot(h);
                if (Mathf.Abs(det) < .0000001f) continue;
                var delta = origin - a; float u = delta.Dot(h) / det; var q = delta.Cross(e1);
                float v = direction.Dot(q) / det; float distance = e2.Dot(q) / det;
                if (u >= -.00001f && v >= -.00001f && u + v <= 1.00001f && distance >= 0f)
                    nearest = Mathf.Min(nearest, distance);
            }
            return float.IsPositiveInfinity(nearest) ? float.NaN : (origin + direction * nearest).Y;
        }
        internal static MeshInstance3D InstalledMesh(Node owner, string path)
        {
            var source = ContentProvider.ParseObj("res://content/" + path);
            return Meshes(owner).FirstOrDefault(mi => SameGeometry(mi.Mesh, source));
        }

        void PanelThickness(MeshInstance3D panel, int index)
        {
            if (index < 4)
            {
                // Full lower skin, away from the window frame, bevel and rear arch boundary.
                foreach (float y in new[] { .55f, .8f })
                {
                    float thickness = ThroughThickness(panel.Mesh, new Vector3(0f, y, index < 2 ? -.5f : .65f), Vector3.Right);
                    T.Check($"panel {index}: lower metal at Y={y} is at least 170 mm ({thickness:F4} m)", thickness >= .17f);
                }
            }
            else
            {
                float slope = index == 4 ? .125046f / 1.401192f : -.125f / .991946f;
                var normal = new Vector3(0f, 1f, -slope).Normalized();
                float thickness = ThroughThickness(panel.Mesh, new Vector3(0f, 1.3f, index == 4 ? -2.25f : 2.35f), normal);
                T.Check($"panel {index}: sloped lid is approximately 120 mm thick ({thickness:F4} m)",
                    thickness >= .11f && thickness <= .135f);
            }
        }
        void CleanSeatGeometry(Node owner)
        {
            var installed = ExactMesh(owner, "sedan_mk2_stock_seats.txt");
            if (installed == null) return;
            var original = ContentProvider.ParseObj("res://content/sedan_seats.txt").GetFaces();
            var expected = original.Select(v => {
                bool rear = v.Z > .25f;
                var moved = v * 1.06f + new Vector3(0f, .021f, rear ? .14f : .30f);
                if (rear) moved.X *= .82f;
                return moved;
            }).ToArray();
            var actual = installed.Mesh.GetFaces();
            // GetFaces snaps native triangle-mesh coordinates to 0.1 mm. The original is snapped
            // before scaling, the installed export afterwards: allow the combined <=0.18 mm
            // Euclidean rounding bound, not arbitrary model deformation. Match multiplicity too.
            var unmatched = new List<Vector3>(actual);
            bool same = expected.Length > 0 && expected.Length == actual.Length;
            foreach (var point in expected)
            {
                int match = unmatched.FindIndex(other => point.DistanceTo(other) < .0002f);
                if (match < 0)
                {
                    same = false; break;
                }
                unmatched.RemoveAt(match);
            }
            GD.Print($"[mk2-seat-shape] {expected.Length / 3} stock triangles, max nearest native rounding error={expected.Max(point => actual.Min(other => point.DistanceTo(other))):F7} m");
            T.Check(owner.Name + ": entire clean stock seat shape retained, moved and rear-narrowed without CSG notches",
                same && unmatched.Count == 0);
            var rearFaces = actual.Where(v => v.Z > .5f).ToArray();
            T.Check(owner.Name + ": rear bench clears the full inboard housing backs",
                rearFaces.Length > 0 && rearFaces.All(v => Mathf.Abs(v.X) < .795f));
        }
        static Transform3D InOwner(Node3D node, Node owner)
        {
            var t = node.Transform;
            for (var parent = node.GetParent(); parent != owner; parent = parent.GetParent())
                if (parent is Node3D spatial) t = spatial.Transform * t;
            return t;
        }
        void PoseIdentity(string label, Transform3D pose)
        {
            T.Check(label, Near(pose.Origin, Vector3.Zero) && Near(pose.Basis.X, Vector3.Right)
                && Near(pose.Basis.Y, Vector3.Up) && Near(pose.Basis.Z, Vector3.Back));
        }
        MeshInstance3D ExactMesh(Node owner, string path)
        {
            var source = ContentProvider.ParseObj("res://content/" + path);
            var found = Meshes(owner).Where(m => SameGeometry(m.Mesh, source)).ToArray();
            if (found.Length == 0 && owner is Vehicle && (path.Contains("headlight") || path.Contains("taillight")))
            {
                // The real builder deliberately splits shootable lamp pairs; the puppet keeps one mesh.
                var expected = source.GetFaces();
                var halves = Meshes(owner).Where(mi => {
                    var faces = mi.Mesh?.GetFaces();
                    return faces != null && faces.Length > 0 && faces.All(v => expected.Any(e => Near(v, e)));
                }).ToArray();
                var actual = halves.SelectMany(mi => mi.Mesh.GetFaces()).ToArray();
                var left = expected.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z).ToArray();
                var right = actual.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z).ToArray();
                T.Check($"{owner.Name}: two shootable halves exactly reconstruct {path}", halves.Length == 2
                    && left.Length == right.Length && left.Zip(right, Near).All(x => x));
                return halves.FirstOrDefault();
            }
            T.Check($"{owner.Name}: exactly one copy of {path}", found.Length == 1);
            return found.FirstOrDefault();
        }
        void Occupancy(VehiclePanelRig rig, string label)
        {
            rig.ResetOccupancyBaseline();
            rig.ApplyOccupancy(11, new uint[] { 22, 0, 0 });
            T.Check(label + ": initial occupancy never pulses", Enumerable.Range(0, 6).All(i => !rig.IsOpen(i)));
            rig.ApplyOccupancy(22, new uint[] { 11, 0, 0 });
            T.Check(label + ": internal seat swap has no external door pulse", Enumerable.Range(0, 4).All(i => !rig.IsOpen(i)));
            rig.ApplyOccupancy(22, new uint[] { 11, 33, 0 });
            T.Check(label + ": rear-left entry pulses only seat 2", rig.IsOpen(2)
                && new[] { 0, 1, 3, 4, 5 }.All(i => !rig.IsOpen(i)));
            rig.Tick(VehiclePanelRig.SwingSeconds);
            T.Check(label + ": entered door reaches fully open", Mathf.IsEqualApprox(rig.GetOpenAmount(2), 1f));
            rig.Tick(VehiclePanelRig.SeatPulseSeconds + VehiclePanelRig.SwingSeconds);
            T.Check(label + ": pulse auto-closes", !rig.IsOpen(2) && rig.GetFraction(2) == 0f);
            rig.ApplyOccupancy(22, new uint[] { 11, 33, 0 });
            T.Check(label + ": unchanged identity sample does not pulse", !rig.IsOpen(2));
            rig.ApplyOccupancy(22, new uint[] { 11, 44, 0 });
            T.Check(label + ": same-seat external replacement pulses that seat", rig.IsOpen(2)
                && new[] { 0, 1, 3 }.All(i => !rig.IsOpen(i)));
            rig.Tick(4f);
            rig.ApplyOccupancy(22, new uint[] { 11, 0, 0 });
            T.Check(label + ": exit pulses only the departing seat", rig.IsOpen(2)
                && new[] { 0, 1, 3 }.All(i => !rig.IsOpen(i)));
            rig.Tick(4f);
            rig.SetCompartment(Vehicle.AccessKind.Hood, true);
            rig.Tick(VehiclePanelRig.SwingSeconds);
            T.Check(label + ": local hood latch is selective", rig.GetFraction(4) == 1f && !rig.IsOpen(5));
            rig.SetCompartment(Vehicle.AccessKind.Hood, false);
            rig.SetCompartment(Vehicle.AccessKind.Trunk, true);
            rig.Tick(VehiclePanelRig.SwingSeconds);
            T.Check(label + ": trunk independent of hood", rig.GetFraction(4) == 0f && rig.GetFraction(5) == 1f);
            rig.SetCompartment(Vehicle.AccessKind.Trunk, false);
            rig.Tick(VehiclePanelRig.SwingSeconds);
        }

        public override IEnumerable<Step> Run()
        {
            var oldSpec = Spec("sedan"); var newSpec = Spec("sedan_mk2");
            T.Check("old sedan TypeId remains index 3", Vehicle.SpecNames[3] == "sedan"
                && Array.IndexOf(Vehicle.SpecNames, "sedan_mk2") > 3);
            T.Check("old body/wheel/palette/glass paths unchanged", (string)Field(oldSpec, "Body") == "sedan_body.txt"
                && (string)Field(oldSpec, "Wheel") == "sedan_wheel.txt"
                && (string)Field(oldSpec, "Palette") == "sedan_palette.png"
                && (string)Field(oldSpec, "GlassMesh") == "sedan_glass.txt");
            foreach (var name in new[] { "Mass", "Engine", "SteerMax", "SteerMin", "SpeedMax", "SpeedMin", "Brake",
                "Fuel", "Health", "WheelRadius", "ReverseGear", "ShiftUpRpm", "Sound", "Horn", "Wheel", "WheelTex" })
                T.Check("Mk II retains stock " + name, Equals(Field(oldSpec, name), Field(newSpec, name)));
            T.Check("forward gearing unchanged", ((float[])Field(oldSpec, "ForwardGears"))
                .SequenceEqual((float[])Field(newSpec, "ForwardGears")));
            T.Check("new spec identity and split-glass prefix", (string)Field(newSpec, "Name") == "Sedan Mk II"
                && (string)Field(newSpec, "Body") == "sedan_mk2_frame.txt"
                && (string)Field(newSpec, "GlassMesh") == "sedan_mk2_glass.txt");

            // Keep detached builds: this checks construction, not suspension compression/handling.
            var old = Vehicle.BuildSedan(5); var car = Vehicle.BuildSedanMk2(5);
            var puppet = Vehicle.BuildPuppetByName("sedan_mk2", 5);
            try
            {
                T.Check("old BuildSedan still uses original geometry", SameGeometry(old.GetNode<MeshInstance3D>("Body").Mesh,
                    ContentProvider.ParseObj("res://content/sedan_body.txt")));
                T.Check("old builder did not acquire authored panels", old.AuthoredPanelRig == null);
                Vector3[] oldSeats = { new(-.5f, -.079f, -.625f), new(.5f, -.079f, -.625f),
                    new(-.5f, -.079f, .772f), new(.5f, -.079f, .772f) };
                var oldWheels = old.GetChildren().OfType<VehicleWheel3D>().ToArray();
                T.Check("old sedan keeps four original seats and wheels", old.SeatCount == 4 && oldWheels.Length == 4);
                T.Check("old tuned visible driver origin stays unchanged", Near(old.SeatOffset, new Vector3(-.5f, -.04f, -.566f)));
                for (int i = 0; i < 4; i++)
                    T.Check($"old seat {i}: prefab origin unchanged", Near(old.SeatLocal(i), oldSeats[i]));
                for (int i = 0; i < Math.Min(oldWheels.Length, 4); i++)
                    T.Check($"old wheel {i}: mount and tyre unchanged",
                        Near(oldWheels[i].Position, new Vector3(i % 2 == 0 ? -1.30f : 1.30f, .25f, i < 2 ? -1.62f : 1.38f))
                        && oldWheels[i].WheelRestLength == .25f && oldWheels[i].WheelRadius == .6f);
                var rig = car.AuthoredPanelRig;
                T.Check("new builder opted in to six authored panels", rig != null && rig.Count == 6);
                if (rig == null) yield break;
                var defs = (AuthoredPanelDef[])Field(newSpec, "AuthoredPanels");
                string[] panelFiles = { "front_door_left", "front_door_right", "rear_door_left", "rear_door_right", "hood", "trunk_lid" };
                string[] labels = { "l_front", "r_front", "l_rear", "r_rear" };
                Vector3[] pivots = { new(-1.3091f, 1.0598f, -1.39178f), new(1.3091f, 1.0598f, -1.39178f),
                    new(-1.3091f, 1.0598f, 0.2279f), new(1.3091f, 1.0598f, 0.2279f),
                    new(0f, 1.209352463f, -1.5953f), new(0f, 1.204066608f, 1.9451f) };
                float[] degrees = { -55f, 55f, -55f, 55f, 50f, -50f };
                for (int i = 0; i < 6; i++)
                {
                    var d = rig.GetDefinition(i);
                    T.Check($"panel {i}: exact authored hinge and seat mapping", d.MeshPath == $"sedan_mk2_{panelFiles[i]}.txt"
                        && d.PanelIndex == i && d.SeatIndex == (i < 4 ? i : i == 4 ? -1 : -2)
                        && Near(d.Pivot, pivots[i]) && Near(d.Axis, i < 4 ? Vector3.Up : Vector3.Right)
                        && d.Degrees == degrees[i] && d.GlassLabel == (i < 4 ? labels[i] : null));
                    PoseIdentity($"panel {i}: closed metal stays in authored body space", rig.GetPose(i));
                }
                var parts = ((ValueTuple<string, Color>[])Field(newSpec, "Parts"));
                var painted = (string[])Field(newSpec, "PaintedParts");
                T.Check("ten flat fixed parts and four palette interiors", parts.Length == 10 && painted.Length == 4);
                T.Check("only the wheel filename triggers the steer builder", parts.Count(p => p.Item1.Contains("steer")) == 1);
                foreach (var owner in new Node3D[] { car, puppet })
                {
                    var body = ExactMesh(owner, "sedan_mk2_frame.txt");
                    if (body == null) continue;
                    var installedFaces = body.Mesh.GetFaces().Select(v => InOwner(body, owner) * v).ToArray();
                    float top = installedFaces.Max(v => v.Y);
                    T.Check(owner.Name + ": V14 installed greenhouse top is 1.9107 m", Mathf.Abs(top - 1.91068692f) < .0002f);
                    var floor = ExactMesh(owner, "sedan_mk2_cabin_floor.txt");
                    if (floor != null)
                    {
                        foreach (var footprint in new[] { new Vector3(-.53f, .3f, -.29996f), new Vector3(0f, .3f, 0f) })
                        {
                            float floorY = RayY(floor, owner, footprint, Vector3.Down);
                            float roofY = RayY(body, owner, new Vector3(footprint.X, 1.65f, footprint.Z), Vector3.Up);
                            float clearance = roofY - floorY;
                            float expectedRoof = footprint.X == 0f ? 1.8248f : 1.8176f;
                            T.Check($"{owner.Name}: actual roof ray at X={footprint.X} clears seated crown but not standing height",
                                Mathf.Abs(floorY - (-.08724f)) < .0002f && roofY > 1.6248f
                                && Mathf.Abs(roofY - expectedRoof) < .001f
                                && clearance < PlayerMovementDef.HeightForStance(EPlayerStance.STAND)
                                && clearance > PlayerMovementDef.HeightForStance(EPlayerStance.CROUCH));
                            GD.Print($"[mk2-greenhouse] {owner.Name} X={footprint.X:F3} top={top:F7} roof={roofY:F7} floor={floorY:F7} floorToCeiling={clearance:F7} seatedCrownReference=1.6248 stand={PlayerMovementDef.HeightForStance(EPlayerStance.STAND):F3} crouch={PlayerMovementDef.HeightForStance(EPlayerStance.CROUCH):F3}");
                        }
                    }
                    var paint = body.MaterialOverride as ShaderMaterial;
                    T.Check(owner.Name + ": body palette texture loaded", paint != null
                        && paint.GetShaderParameter("palette").AsGodotObject() is Texture2D);
                    foreach (var path in painted.Concat(defs.Select(d => d.MeshPath)))
                    {
                        var mi = ExactMesh(owner, path);
                        if (mi == null) continue;
                        T.Check(path + ": shares body paint material", mi.MaterialOverride == body.MaterialOverride);
                        PoseIdentity(path + ": closed body space", InOwner(mi, owner));
                        T.Check(path + ": palette UV loaded", mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV]
                            .AsVector2Array().Length > 0);
                        int panelIndex = Array.FindIndex(defs, d => d.MeshPath == path);
                        if (panelIndex >= 0) PanelThickness(mi, panelIndex);
                    }
                    foreach (var d in defs)
                    {
                        var pivot = owner.GetNodeOrNull<Node3D>($"AuthoredPivot_{d.PanelIndex}");
                        T.Check($"{owner.Name}: panel {d.PanelIndex} has direct authored hinge", pivot != null
                            && Near(pivot.Position, d.Pivot) && pivot.GetChildren().OfType<MeshInstance3D>()
                                .Any(mi => mi.Name.ToString() == $"AuthoredPanel_{d.PanelIndex}" && Near(mi.Position, -d.Pivot)));
                        if (d.GlassLabel != null)
                            T.Check($"{owner.Name}: moving glass attached to hinge {d.PanelIndex}", pivot != null
                                && pivot.GetChildren().OfType<MeshInstance3D>().Any(mi => mi.Name.ToString() == "Glass_" + d.GlassLabel));
                    }
                    foreach (var part in parts) ExactMesh(owner, part.Item1);
                    CleanSeatGeometry(owner);
                    foreach (var label in new[] { "windshield", "rear", "l_front", "r_front", "l_rear", "r_rear" })
                    {
                        var pane = ExactMesh(owner, $"sedan_mk2_glass_{label}.txt");
                        if (pane != null) PoseIdentity(label + ": closed glass stays in body space", InOwner(pane, owner));
                    }
                }
                Vector3[] seats = { new(-0.53f, -0.06274f, -0.3625f), new(0.53f, -0.06274f, -0.3625f),
                    new(-0.4346f, -0.06274f, 0.95832f), new(0.4346f, -0.06274f, 0.95832f) };
                T.Check("explicit four seats", car.SeatCount == 4);
                for (int i = 0; i < 4; i++) T.Check($"seat {i}: total authored seat shift and rear narrowing", Near(car.SeatLocal(i), seats[i]));
                T.Check("puppet and real visible seat origins agree at the total +.30 body bias",
                    Near(car.SeatOffset, new Vector3(-.53f, -.0214f, -.29996f)) && Near(car.SeatOffset, puppet.SeatOffset));
                for (int i = 0; i < 4; i++)
                    T.Check($"seat {i}: visible body follows the moved seat without an extra row shift",
                        Near(car.SeatBodyLocal(i), seats[i] + new Vector3(0f, .04134f, .06254f)));
                T.Check("fallback driver eye derives from the explicit moved driver seat",
                    Near(car.DriverEyeLocal, seats[0] + new Vector3(0f, 1.358505f, .06254f))
                    && Mathf.Abs(car.DriverEyeLocal.Y - 1.29576505f) < .00001f);
                T.Check("eye over driver seat, behind repositioned wheel", Near(car.DriverEyeLocal, puppet.DriverEyeLocal)
                    && Mathf.Abs(car.DriverEyeLocal.X - seats[0].X) < .001f
                    && car.DriverEyeLocal.Z > car.SteerPivotLocal.Z && Mathf.Abs(car.DriverEyeLocal.Z - seats[0].Z) < .1f);
                T.Check("steering wheel uses baked shift once", Near(car.SteerPivotLocal, new Vector3(-.49184f, .96864f, -1.00096f))
                    && puppet.SteerPivot != null && Near(puppet.SteerPivot.Position, car.SteerPivotLocal));
                var wheels = car.GetChildren().OfType<VehicleWheel3D>().ToArray();
                T.Check("four physical and four puppet wheels", wheels.Length == 4 && puppet.Wheels.Length == 4);
                for (int i = 0; i < Math.Min(Math.Min(wheels.Length, puppet.Wheels.Length), 4); i++)
                {
                    var rest = new Vector3(i % 2 == 0 ? -1.09f : 1.09f, 0f, i < 2 ? -1.9292f : 1.949f);
                    T.Check($"wheel {i}: stock-height mount and zero-Y nominal visual centre",
                        Near(wheels[i].Position, rest + Vector3.Up * .25f) && wheels[i].WheelRestLength == .25f
                        && Near(wheels[i].Position - Vector3.Up * wheels[i].WheelRestLength, rest)
                        && Near(puppet.Wheels[i].Pivot.Position, rest) && wheels[i].WheelRadius == .6f);
                }
                var belly = car.GetChildren().OfType<CollisionShape3D>().FirstOrDefault(c => c.Shape is BoxShape3D
                    && Near(((BoxShape3D)c.Shape).Size, new Vector3(2.65f, .97096f, 5.99536f)));
                T.Check("main hull scales about ground", belly != null && Near(belly.Position, new Vector3(0f, .60188f, -.06678f)));
                var roof = car.GetNodeOrNull<CollisionShape3D>("RoofBox");
                T.Check("roof and cabin registration", roof?.Shape is BoxShape3D box
                    && Near(box.Size, new Vector3(2.65f, .11f, 2.4592f)) && Near(roof.Position, new Vector3(0f, 1.8556869f, .2067f)));
                if (roof?.Shape is BoxShape3D fallbackRoof)
                {
                    float bottom = roof.Position.Y - fallbackRoof.Size.Y / 2f;
                    float top = roof.Position.Y + fallbackRoof.Size.Y / 2f;
                    float standingTop = -.08724f + PlayerMovementDef.HeightForStance(EPlayerStance.STAND);
                    T.Check("fallback slab clears seated crown and intersects independent standing envelope",
                        Mathf.Abs(bottom - 1.8006869f) < .0001f && Mathf.Abs(top - 1.9106869f) < .0001f
                        && bottom > 1.6248f && standingTop > bottom
                        && -.08724f < top
                        && -.08724f + PlayerMovementDef.HeightForStance(EPlayerStance.CROUCH) < bottom);
                }
                Occupancy(rig, "real rig");
                rig.PulseSeat(0); rig.Tick(VehiclePanelRig.SwingSeconds);
                var movingPane = rig.GetGlassPane(0);
                T.Check("moving glass inherits the opened hinge", movingPane != null
                    && movingPane.GetParent() == rig.GetPivot(0)
                    && !Near(InOwner(movingPane, car).Basis.X, Vector3.Right));
                if (movingPane != null)
                    foreach (var hit in movingPane.GetChildren().OfType<StaticBody3D>())
                        T.Check("existing glass query stays attached and resolves ownership", Vehicle.Owning(hit) == car
                            && hit.GetParent() == movingPane);
                rig.Tick(4f);

                // Public puppet seam, independent of replica-loop timing/property naming.
                var visualOwner = new Node3D(); var paintMaterial = new StandardMaterial3D();
                try { Occupancy(Vehicle.CreateAuthoredPanelRig(visualOwner, defs, paintMaterial), "puppet seam"); }
                finally { visualOwner.Free(); paintMaterial.Dispose(); }
                // Explicit true makes this independent of UG_MESHHITBOX. No moving chassis shape.
                var queryOwner = new Vehicle();
                try
                {
                    var qr = Vehicle.BuildAuthoredPanelRig(queryOwner, defs, rig.GetMesh(0).MaterialOverride, true);
                    T.Check("six panel-only query colliders", qr.QueryColliderCount == 6);
                    T.Check("queries never add chassis shapes", !queryOwner.GetChildren().OfType<CollisionShape3D>().Any());
                    qr.PulseSeat(0); qr.Tick(VehiclePanelRig.SwingSeconds);
                    for (int i = 0; i < 6; i++)
                    {
                        var hit = qr.GetQueryBody(i);
                        T.Check($"panel {i}: collider follows mesh and resolves owner", hit != null
                            && hit.GetParent() == qr.GetMesh(i) && Vehicle.Owning(hit) == queryOwner
                            && hit.CollisionMask == 0 && Near(hit.Position, Vector3.Zero));
                    }
                    T.Check("open pose rotates query and metal together", !Near(qr.GetPose(0).Basis.X, Vector3.Right));
                }
                finally { queryOwner.Free(); }
            }
            finally { old.Free(); car.Free(); puppet.Free(); }
            yield return Ticks(1);
        }
    }
}
