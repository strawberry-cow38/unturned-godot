using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-09-03: "do it on real collision. we will fix as we go." -- turning UG_MESHHITBOX on by
    // default, which moves what a player walks into and what a bullet stops on off the convex hulls and onto
    // the model itself, riding on a StaticBody3D child.
    //
    // WHY THIS TEST IS AN A/B IN ONE PROCESS. MeshHitbox reads its environment variable every call and the
    // layer assignment happens in Build(), so setting the variable around each BuildByName gives two cars in
    // one boot that differ in exactly the thing under test. That matters more than convenience: run as two
    // separate suite invocations, the OFF run's green tells you nothing about the ON run.
    //
    // Every behavioural check below is an INVARIANT -- something that must hold whichever way the flag is set.
    // That is the point: a flag that changes observable behaviour has not been made safe to turn on.
    //
    // THE FIXTURE IS CHECKED FIRST, and it is not ceremony. An earlier version of this file passed all ten of
    // its checks on a bug I had already seen in the log, because two of them could not fail: one asserted the
    // helper I had just written rather than the production path, and one measured "how far past the windscreen
    // did the round get" with a ray that stopped 2 m SHORT of the windscreen -- a negative distance sails
    // through a `< 0.35` bound. If the two legs ever stop differing, everything below is vacuous.
    public sealed class VehicleMeshHitboxTests : GameTest
    {
        public override string Name => "vehicle.mesh_hitbox";
        public override double TimeoutSimSeconds => 180;

        // What PlayerController.StepBullets scans (world + enemy + ragdoll + vehicle + props).
        const uint BulletMask = (1u << 0) | (1u << 1) | (1u << 4) | (1u << 5) | (1u << 6);
        // What the player's own ground/step probes scan.
        const uint WalkMask = (1u << 0) | (1u << 5) | (1u << 6);

        static Vehicle BuildWith(bool meshHitbox, string name)
        {
            var was = System.Environment.GetEnvironmentVariable("UG_MESHHITBOX");
            // "0", not null. The default is now ON, so CLEARING the variable builds a mesh-hitbox car -- the
            // control leg would silently become a second copy of the experiment and every comparison below
            // would be a car against itself. (The fixture checks in Run() exist to catch exactly this, and
            // would have; it is still better not to need them.)
            System.Environment.SetEnvironmentVariable("UG_MESHHITBOX", meshHitbox ? "1" : "0");
            try { return Vehicle.BuildByName(name); }
            finally { System.Environment.SetEnvironmentVariable("UG_MESHHITBOX", was); }
        }

        static bool IsPaneBody(GodotObject o) => o is StaticBody3D sb && sb.Name == "GlassHit";

        /// <summary>First thing `mask` hits between two world points, as (collider, point), or (null, Zero).</summary>
        static (GodotObject, Vector3) Cast(Node3D anyNode, Vector3 from, Vector3 to, uint mask)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, to, mask);
            var hit = anyNode.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return (null, Vector3.Zero);
            return (hit["collider"].As<GodotObject>(), hit["position"].AsVector3());
        }

        public override IEnumerable<Step> Run()
        {
            // GROUND FIRST. The sandbox has none of its own, so an unfrozen car free-falls -- the first run of
            // this file cast every ray at a transform the car had left several metres earlier and reported "the
            // flank ray hits nothing" as a finding about collision layers when it was a finding about gravity.
            // Setting Freeze by hand does not fix it either: Vehicle owns its own freeze state and clears it.
            // With real ground under them, Vehicle's own settle logic parks and freezes each car, which is both
            // stable and the state a parked car is actually in.
            Rigs.Ground(World);
            var cars = new List<(string Mode, Vehicle Car)>();
            float z = 0f;
            foreach (bool mesh in new[] { false, true })
            {
                var car = BuildWith(mesh, "sedan");
                World.AddChild(car);
                // Far apart: two cars sharing a spot resolve into each other and the ON leg's rays would be
                // reporting the OFF leg's hulls.
                car.GlobalPosition = new Vector3(0f, 1.5f, z);
                z += 40f;
                cars.Add((mesh ? "MESH" : "hulls", car));
            }
            yield return Ticks(150);   // let both drop and settle onto the ground before anything is measured

            var p = new PlayerController { CaptureMouse = false };
            World.AddChild(p);
            yield return Ticks(4);

            // ---- 0. THE FIXTURE. Both legs must have been built, and built DIFFERENTLY.
            var hullCar = cars[0].Car; var meshCar = cars[1].Car;
            GD.Print($"[HITBOX] hulls: tris={hullCar.DebugHitMeshTris} layer=0b{System.Convert.ToString(hullCar.CollisionLayer, 2)}");
            GD.Print($"[HITBOX] MESH : tris={meshCar.DebugHitMeshTris} layer=0b{System.Convert.ToString(meshCar.CollisionLayer, 2)}");
            T.Check("the control leg really has NO mesh hitbox", hullCar.DebugHitMeshTris == 0);
            T.Check("the mesh leg really HAS one", meshCar.DebugHitMeshTris > 0);
            T.Check("...and they do not sit on the same collision layer", hullCar.CollisionLayer != meshCar.CollisionLayer);
            // Settled, not still falling. Without this the geometry under every ray below is a moving target and
            // a miss says nothing about collision layers -- which is exactly how this file first "passed".
            foreach (var (m2, c2) in cars)
                T.Check($"[{m2}] the car has come to rest on the ground (y={c2.GlobalPosition.Y:0.00}, |v|={c2.LinearVelocity.Length():0.00})",
                        c2.LinearVelocity.Length() < 0.5f);
            if (hullCar.DebugHitMeshTris != 0 || meshCar.DebugHitMeshTris == 0) yield break;   // nothing below means anything

            foreach (var (m, car) in cars)
            {
                var xf = car.GlobalTransform;

                // ---- 1. A BULLET STILL DAMAGES THE CAR. The check the whole change lives or dies on, and it
                // goes through the REAL StepBullets path rather than re-implementing its predicate here: with
                // the mesh on bit 5 the collider a ray returns is the HitMesh CHILD, so `collider is Vehicle`
                // stops matching and a car takes no damage, its glass does not break and its lamps do not shoot
                // out -- silently, with nothing logged anywhere.
                float hp0 = car.Health;
                // The player has to be NEAR the muzzle: SpawnBullet is stepped by this player's own
                // _PhysicsProcess and the round is culled on distance from the shooter.
                p.TeleportTo(xf * new Vector3(9f, 0.55f, 0f));
                yield return Ticks(2);
                // AT THE DOOR SKIN (local y 0.55), not at window height. Fired at y 1.0 the round goes through
                // the side glass, and a round through a window breaks the pane and stops there BY DESIGN -- so
                // the car's health is untouched and "no damage" looks exactly like the bug this check exists to
                // find. It failed identically in both legs, which is what gave it away.
                var muzzle = xf * new Vector3(8f, 0.55f, 0f);
                var aim = xf * new Vector3(-8f, 0.55f, 0f);
                p.DebugFireBullet(muzzle, (aim - muzzle).Normalized(), 40f);
                yield return Ticks(20);
                GD.Print($"[HITBOX] {m}: bullet at the door took the car from {hp0:0} to {car.Health:0} hp " +
                         $"(panes broken: {car.GlassBrokenCount})");
                T.Check($"[{m}] a bullet fired at the car's door actually damages it ({hp0:0} -> {car.Health:0})",
                        car.Health < hp0);

                // ---- 2. AND THE COLLIDER RESOLVES. Same fact from the other side, so a failure says WHICH
                // half broke. In MESH mode the collider is deliberately NOT the Vehicle -- that is asserted
                // rather than merely tolerated, so nobody "simplifies" Vehicle.Owning back to a plain cast and
                // finds the suite still green.
                var (fc, fp) = Cast(car, xf * new Vector3(6f, 1.0f, 0f), xf * new Vector3(-6f, 1.0f, 0f), BulletMask);
                T.Check($"[{m}] a bullet ray across the flank hits something", fc != null);
                if (fc != null)
                {
                    GD.Print($"[HITBOX] {m}: flank ray hit {fc.GetType().Name} '{(fc as Node)?.Name}'");
                    T.Check($"[{m}] ...and it resolves to the vehicle", Vehicle.Owning(fc) == car);
                    if (m == "MESH")
                        T.Check("[MESH] ...via a CHILD body, which is the whole reason Owning has to exist",
                                fc is not Vehicle);
                }

                // ---- 3. THE WINDSCREEN APERTURE IS NOT A HOLE. The hull used to be what stopped a round at
                // the windscreen; the panes carry no collider of their own, so with the hull out of the bullet
                // layers a shot aimed at the windscreen flies into the cabin.
                //
                // Cast along the PANE'S OWN NORMAL, not at it from the car's centre. Aiming from the centre
                // sends the ray up the bonnet, where it stops 2 m short of the glass having tested nothing --
                // and "2 m short" is a NEGATIVE overshoot, which passes any `overshoot < tolerance` bound.
                int wi = -1;
                for (int i = 0; i < car.GlassCount; i++) if (car.GlassLabel(i) == "windshield") wi = i;
                T.Check($"[{m}] the sedan has a windscreen pane to aim at", wi >= 0);
                if (wi >= 0)
                {
                    var pane = car.GlassPaneCenter(wi);
                    var n = car.GlassPaneNormal(wi);
                    var wFrom = pane + n * 3f;
                    var (wc, wp) = Cast(car, wFrom, pane - n * 3f, BulletMask);
                    float reach = wc == null ? 999f : (wp - wFrom).Length() - 3f;   // signed: +ve = past the pane
                    GD.Print($"[HITBOX] {m}: windscreen {pane} n={n} -> {(wc == null ? "NOTHING HIT" : $"{(wc as Node)?.Name} at {reach:+0.000;-0.000} m relative to the pane")}");
                    // Bounded on BOTH sides. The upper bound is the bug (a round reaching the cabin); the lower
                    // bound is the fixture (a ray that stopped short never reached the glass, so it proves
                    // nothing about whether the glass stops anything).
                    T.Check($"[{m}] the ray actually reached the windscreen rather than stopping short (reach={reach:0.00} m)",
                            wc != null && reach > -0.30f);
                    T.Check($"[{m}] a shot at the windscreen stops AT it, not inside the cabin (reach={reach:0.00} m)",
                            wc != null && reach < 0.30f);

                    // ...AND A BROKEN ONE IS A HOLE AGAIN. The other half of giving panes a collider: if the
                    // collider outlives the glass, shooting a window out leaves an invisible pane you still
                    // cannot shoot or climb through, which is a worse bug than the one it fixed. Only checked
                    // in MESH mode -- with the hulls still on the bullet layers there is a hull behind the
                    // glass either way, so the question does not arise and asserting it would fail for a
                    // reason that has nothing to do with the pane.
                    if (m == "MESH")
                    {
                        T.Check("[MESH] the windscreen breaks", car.BreakGlass(wi));
                        yield return Ticks(4);
                        var (bc, bp) = Cast(car, wFrom, pane - n * 3f, BulletMask);
                        // BY DISTANCE, not by identity. The first version asserted the ray no longer hit ANY
                        // pane body and failed -- correctly, but for an unrelated reason: with the windscreen
                        // gone the round carries on through the cabin and lands on the REAR window's collider
                        // 3 m further back, which is exactly right and is not the windscreen stopping it.
                        float bReach = bc == null ? 999f : (bp - wFrom).Length() - 3f;
                        GD.Print($"[HITBOX] MESH: after breaking it, the same ray hits {(bc == null ? "NOTHING (clean through)" : $"{(bc as Node)?.Name} {bReach:+0.00;-0.00} m past the windscreen")}");
                        T.Check($"[MESH] a broken windscreen no longer stops a round at the windscreen (reach={bReach:0.00} m)",
                                bc == null || bReach > 0.30f);
                        T.Check("[MESH] ...and the pane it now reaches is deeper in the car, not the broken one",
                                bc == null || !IsPaneBody(bc) || bReach > 1.0f);
                        car.RepairGlass(wi);
                        yield return Ticks(4);
                        var (rc2, rp2) = Cast(car, wFrom, pane - n * 3f, BulletMask);
                        float rReach = rc2 == null ? 999f : (rp2 - wFrom).Length() - 3f;
                        T.Check($"[MESH] ...and repairing it puts the pane back (reach={rReach:0.00} m)",
                                IsPaneBody(rc2) && Mathf.Abs(rReach) < 0.30f);
                    }
                }

                // ---- 4. THE ROOF IS SOLID FROM ABOVE, at the height the model actually has. This is the
                // improvement the change exists for: a convex hull cannot follow the roof's curve and stops a
                // down-ray about 7 cm proud of it.
                var roofFrom = xf * new Vector3(0f, 6f, 0.2f);
                var (rc, rp) = Cast(car, roofFrom, roofFrom + Vector3.Down * 8f, WalkMask);
                T.Check($"[{m}] a down-ray over the cabin lands on the car, not the ground",
                        rc != null && Vehicle.Owning(rc) == car);
                if (rc != null) GD.Print($"[HITBOX] {m}: roof down-ray stops at local y {(xf.AffineInverse() * rp).Y:0.000}");

                // ---- 5. A PLAYER ON THE ROOF IS CARRIED BY THE MOVING CAR. The named risk of putting the
                // hitbox on a StaticBody3D child: a character standing on a static body reads its platform
                // velocity from ConstantLinearVelocity, which is zero unless something sets it, so the car
                // drives out from under the rider.
                //
                // Measured as a RATIO of the player's displacement to the car's, not as an absolute distance:
                // "the player moved" is satisfied by sliding off the roof, and "the car moved" says nothing
                // about the player. The control leg is what establishes the number is achievable at all.
                p.TeleportTo(xf * new Vector3(0f, 2.6f, 0.2f));
                yield return Ticks(25);                      // land on the roof and settle
                var (fl, _) = Cast(car, p.GlobalPosition + Vector3.Up * 0.2f, p.GlobalPosition + Vector3.Down * 1.5f, WalkMask);
                T.Check($"[{m}] the player is standing on the car before it is driven", Vehicle.Owning(fl) == car);
                if (Vehicle.Owning(fl) == car)
                {
                    var p0 = p.GlobalPosition; var c0 = car.GlobalPosition;
                    car.EngineOn = true;
                    for (int i = 0; i < 90; i++) { car.Drive(1f, 0f, false); yield return Ticks(1); }
                    float carMoved = car.GlobalPosition.DistanceTo(c0);
                    float ridMoved = p.GlobalPosition.DistanceTo(p0);
                    float ratio = carMoved > 0.5f ? ridMoved / carMoved : 0f;
                    GD.Print($"[HITBOX] {m}: car moved {carMoved:0.00} m, rider moved {ridMoved:0.00} m (ratio {ratio:0.00})");
                    T.Check($"[{m}] the car actually drove, so the carry has something to fail at ({carMoved:0.0} m)",
                            carMoved > 2f);
                    T.Check($"[{m}] a player on the roof is carried with it (rider/car = {ratio:0.00})", ratio > 0.6f);
                    car.EngineOn = false;
                }
            }

            // ---- 6. RAMMING ANOTHER CAR STILL SHOVES IT. The chassis gave bit 0 up to the HitMesh, but a car
            // still MASKS bit 0 (it is how it finds the terrain), and the HitMesh is a StaticBody3D. So car A
            // meets car B's hitbox as an immovable wall at the same moment it meets B's hulls as a body, and
            // the static half wins on mass. Nothing in the layer plan says this cannot happen, so it is
            // measured rather than argued about -- by how far the TARGET is pushed, which is the thing a
            // static wall cannot do.
            {
                foreach (var (_, c) in cars) c.QueueFree();
                yield return Ticks(4);
                var rams = new List<(string Mode, Vehicle Hitter, Vehicle Target)>();
                float rz = 200f;
                foreach (bool mesh in new[] { false, true })
                {
                    var hitter = BuildWith(mesh, "sedan"); World.AddChild(hitter);
                    var target = BuildWith(mesh, "sedan"); World.AddChild(target);
                    hitter.GlobalPosition = new Vector3(0f, 1.5f, rz);
                    // PUT THE TARGET WHERE THE HITTER IS ACTUALLY GOING. Placed at +Z by hand, the hitter drove
                    // 87 m in the other direction and never touched it -- 0.00 m of shove in BOTH legs, which
                    // reads exactly like the bug this is looking for and is really just a fixture pointing
                    // backwards. Forward is -Z of the body's own basis, so ask the body.
                    target.GlobalPosition = hitter.GlobalPosition + (-hitter.GlobalTransform.Basis.Z).Normalized() * 14f;
                    rz += 100f;
                    rams.Add((mesh ? "MESH" : "hulls", hitter, target));
                }
                yield return Ticks(150);
                foreach (var (m, hitter, target) in rams)
                {
                    var t0 = target.GlobalPosition;
                    hitter.EngineOn = true;
                    for (int i = 0; i < 260; i++) { hitter.Drive(1f, 0f, false); yield return Ticks(1); }
                    float shoved = target.GlobalPosition.DistanceTo(t0);
                    GD.Print($"[HITBOX] {m}: rammed target moved {shoved:0.00} m (hitter ended {hitter.GlobalPosition.DistanceTo(t0):0.0} m from it)");
                    float gap = hitter.GlobalPosition.DistanceTo(target.GlobalPosition);
                    T.Check($"[{m}] the hitter actually reached the target, so a zero shove means something ({gap:0.0} m apart)",
                            gap < 8f);
                    T.Check($"[{m}] a rammed car is shoved rather than treated as a wall ({shoved:0.00} m)", shoved > 0.5f);
                    hitter.EngineOn = false;
                }
            }
        }
    }
}
