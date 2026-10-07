using Godot;
using SDG.Unturned;
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
        // CPU-skin the installed body using its ACTUAL animated skeleton and bind poses. A mesh
        // AABB is the standing bind pose, not the seated crown; gear / boarding transients are out of scope.
        static IEnumerable<Vector3> SeatedHeadVertices(Vehicle car, RiggedCharacter rig)
        {
            if (rig?.Skeleton == null || rig.Body?.Skin == null) yield break;
            int skull = rig.Skeleton.FindBone("Skull");
            if (skull < 0) yield break;
            var mesh = rig.Body.Mesh; var skin = rig.Body.Skin;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                if (vertices.Length == 0) continue;
                int influences = bones.Length / vertices.Length;
                for (int v = 0; v < vertices.Length; v++)
                {
                    var posed = Vector3.Zero; float skullWeight = 0f;
                    for (int j = 0; j < influences; j++)
                    {
                        int index = v * influences + j; float weight = weights[index];
                        if (weight <= 0f) continue;
                        int bind = bones[index]; int bone = skin.GetBindBone(bind);
                        if (bone == skull) skullWeight += weight;
                        posed += (rig.Skeleton.GetBoneGlobalPose(bone) * skin.GetBindPose(bind) * vertices[v]) * weight;
                    }
                    // Majority-Skull vertices are the bare head, rather than torso blend vertices.
                    if (skullWeight > .5f) yield return car.ToLocal(rig.Skeleton.ToGlobal(posed));
                }
            }
        }
        static IEnumerable<MeshInstance3D> DescendantMeshes(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is MeshInstance3D mi) yield return mi;
                foreach (var mesh in DescendantMeshes(child)) yield return mesh;
            }
        }
        static float EnclosureCeiling(Vehicle car, Vector3 point)
        {
            float nearest = SedanMk2Tests.RayY(car.GetNode<MeshInstance3D>("Body"), car,
                new Vector3(point.X, 1.24f, point.Z), Vector3.Up);
            foreach (var pane in DescendantMeshes(car).Where(m => m.Name.ToString().StartsWith("Glass_")))
            {
                float hit = SedanMk2Tests.RayY(pane, car, new Vector3(point.X, 1.24f, point.Z), Vector3.Up);
                if (!float.IsNaN(hit)) nearest = float.IsNaN(nearest) ? hit : Mathf.Min(nearest, hit);
            }
            return nearest;
        }
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
            var frame = car.GetNode<MeshInstance3D>("Body");
            var floor = SedanMk2Tests.InstalledMesh(car, "sedan_mk2_cabin_floor.txt");
            var rig = (RiggedCharacter)typeof(PlayerController).GetField("_body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player);
            var head = SeatedHeadVertices(car, rig).ToArray();
            T.Check("settled real driver has an animated source head and installed cabin floor", head.Length > 0 && floor != null);
            if (head.Length > 0 && floor != null)
            {
                var crown = head.OrderByDescending(v => v.Y).First();
                float eyeRoof = SedanMk2Tests.RayY(frame, car, eye, Vector3.Up);
                float crownRoof = SedanMk2Tests.RayY(frame, car, new Vector3(crown.X, 1.65f, crown.Z), Vector3.Up);
                float minHeadGap = head.Min(v => EnclosureCeiling(car, v) - v.Y);
                T.Check($"settled camera tracks the source seated eye, below actual roof ({eye}, roof={eyeRoof:F5})",
                    Mathf.Abs(eye.X - car.SeatBodyLocal(0).X) < .08f && Mathf.Abs(eye.Y - 1.41576505f) < .02f
                    && eyeRoof > eye.Y && eyeRoof > 2.00f && eyeRoof < 2.04f
                    && eye.Z > car.SteerPivotLocal.Z + .1f && eye.Z < car.SeatLocal(0).Z + .5f);
                T.Check($"actual CPU-skinned seated crown clears frame AND glass over the whole head footprint (crown={crown.Y:F5}, minGap={minHeadGap:F5})",
                    Mathf.Abs(crown.Y - 1.7448f) < .02f && minHeadGap > .15f && Mathf.Abs(minHeadGap - .264f) < .02f && crownRoof > crown.Y);

                // The player's capsule is disabled while seated. Test the on-foot envelope independently:
                // actual triangle roof/floor + the authoritative stance heights, NOT that seated shape.
                // Crouch is a vertical-headroom check (seats/furniture can still obstruct lateral motion).
                float floorY = SedanMk2Tests.RayY(floor, car, new Vector3(crown.X, .3f, crown.Z), Vector3.Down);
                float standingHeight = PlayerMovementDef.HeightForStance(EPlayerStance.STAND);
                float crouchingHeight = PlayerMovementDef.HeightForStance(EPlayerStance.CROUCH);
                const float onFootRadius = .35f; // PlayerController's authoritative on-foot radius
                float minCeiling = crownRoof;
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * Mathf.Tau / 8f;
                    var sample = new Vector3(crown.X + onFootRadius * Mathf.Cos(angle), 1.65f,
                        crown.Z + onFootRadius * Mathf.Sin(angle));
                    minCeiling = Mathf.Min(minCeiling, SedanMk2Tests.RayY(frame, car, sample, Vector3.Up));
                }
                float clearance = crownRoof - floorY;
                T.Check("independent on-foot standing capsule cannot fit under actual roof; crouch has overhead room across its radius",
                    Mathf.Abs(floorY - .03276f) < .0002f && Mathf.IsEqualApprox(standingHeight, 2f)
                    && Mathf.IsEqualApprox(crouchingHeight, 1.2f) && clearance < standingHeight
                    && minCeiling - floorY > crouchingHeight);
                var skull = rig.Skeleton.GetBoneGlobalPose(rig.Skeleton.FindBone("Skull")).Origin;
                T.Check("V15 retains the source seated Skull height; only the body anchor rises",
                    Mathf.Abs(skull.Y - 1.00716505f) < .02f);
                GD.Print($"[mk2-seated-clearance] ticksAfterEnter=25 seatBody={car.SeatBodyLocal(0)} skull={skull} eye={eye} eyeRoof={eyeRoof:F7} eyeGap={eyeRoof-eye.Y:F7} crown={crown} crownRoof={crownRoof:F7} minHeadGap={minHeadGap:F7} headVertices={head.Length} floor={floorY:F7} floorToCeiling={clearance:F7} standingTop={floorY+standingHeight:F7} standHeight={standingHeight:F3} crouchHeight={crouchingHeight:F3} radius={onFootRadius:F3} minFootprintCeiling={minCeiling:F7} source=installedTriangles+animatedSkin (not disabled seated capsule)");
            }
            // Check the actual passenger poses too; rear backrest centers are not head positions.
            // Include glazing: a metal-only roof test could miss a head intersecting the rear pane.
            for (int seat = 1; seat < 4; seat++)
            {
                T.Check($"clearance audit: switch to real seat {seat}", player.TrySwitchSeat(seat));
                yield return Ticks(25);
                var posedHead = SeatedHeadVertices(car, rig).ToArray();
                T.Check($"seat {seat}: installed source skeleton produces a nonempty seated head", posedHead.Length > 0);
                if (posedHead.Length == 0) continue;
                float minGap = posedHead.Min(v => EnclosureCeiling(car, v) - v.Y);
                float seatedCrown = posedHead.Max(v => v.Y);
                T.Check($"seat {seat}: actual settled bare head clears roof AND glass (minGap={minGap:F5})",
                    minGap > .10f
                    && Mathf.Abs(minGap - (seat < 2 ? .264f : .246f)) < .02f
                    && Mathf.Abs(seatedCrown - 1.7448f) < .02f);
                GD.Print($"[mk2-passenger-clearance] seat={seat} anchor={car.SeatBodyLocal(seat)} crownY={seatedCrown:F6} minimumRoofOrGlassGap={minGap:F6}");
            }
            // Actual physics overlap, independently of the disabled seated player capsule.
            // Use the clear center aisle and its highest installed floor/tunnel surface.
            float aisleFloor = float.NegativeInfinity;
            foreach (string path in new[] { "sedan_mk2_cabin_floor.txt", "sedan_mk2_floor_tunnel.txt" })
            {
                var mi = SedanMk2Tests.InstalledMesh(car, path);
                float y = SedanMk2Tests.RayY(mi, car, new Vector3(0f, .3f, .2f), Vector3.Down);
                if (!float.IsNaN(y)) aisleFloor = Mathf.Max(aisleFloor, y);
            }
            T.Check("V15 raised center tunnel is the actual .18 m aisle floor",
                Mathf.Abs(aisleFloor - .18f) < .0002f);
            foreach (var stance in new[] { EPlayerStance.STAND, EPlayerStance.CROUCH })
            {
                float h = PlayerMovementDef.HeightForStance(stance);
                using var capsule = new CapsuleShape3D { Height = h, Radius = .35f };
                var query = new PhysicsShapeQueryParameters3D {
                    Shape = capsule, CollisionMask = Vehicle.HitMeshBit, CollideWithBodies = true,
                    Transform = car.GlobalTransform * new Transform3D(Basis.Identity,
                        new Vector3(0f, aisleFloor + .01f + h / 2f, .2f)) };
                var overlaps = World.GetWorld3D().DirectSpaceState.IntersectShape(query, 32);
                bool blocked = overlaps.Any(hit => Vehicle.Owning(hit["collider"].AsGodotObject() as Node) == car);
                T.Check($"real on-foot {stance} capsule overlap in cabin aisle: {(blocked ? "blocked" : "clear")}",
                    float.IsFinite(aisleFloor) && blocked == (stance == EPlayerStance.STAND));
                GD.Print($"[mk2-native-stance] {stance} height={h:F2} floor={aisleFloor:F5} hits={overlaps.Count} blockedByCar={blocked}");
            }
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
