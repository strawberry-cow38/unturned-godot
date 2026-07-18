using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // CLIENT_PREDICTION_PLAN §5.5 risk 2 -- the Godot freeze SPIKE, kept as the permanent guard for the
    // Part A server hold. The retail model (U3 InteractableVehicle.cs:1490-1519) flips the server's driven
    // vehicle kinematic and MovePosition/MoveRotation-adopts the driver's reported transform; the Godot
    // port of that is Vehicle.NetBeginHold (Freeze + FreezeMode.Static) + NetHoldTeleport per tick. STATIC,
    // not Kinematic: this codebase already learned "kinematic vanished the car" on this Godot/Jolt build
    // (the parked settle freeze, Vehicle.cs) -- the spike's kinematic probe below documents that mode's
    // behavior each run instead of trusting folklore. What the hold MUST guarantee (asserted here):
    //   1. a frozen body takes per-tick teleports verbatim -- wheels/suspension never fight it back;
    //   2. server-side space queries (ballistics/occlusion/interaction, GodotWorldRay mask bit0|bit6) see
    //      the held body at the teleported pose -- NetGhost keeps bit6 up for exactly this;
    //   3. a character in the held body's path still gets displaced (the run-over/shove posture);
    //   4. NetEndHold resumes real physics from the seeded velocity (the exit/disconnect handoff --
    //      retail removePlayer -> updatePhysics).
    public class NetVehicleFreezeHold : GameTest
    {
        public override string Name => "net.vehicle_freeze_hold";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            yield return Ticks(50);   // let the spawn drop settle onto the ground plane
            float restY = jeep.GlobalPosition.Y;

            // --- 1. hold + a 10 m/s teleport track (highway pace, 0.2 m per 50 Hz tick) ---
            jeep.NetGhost(true);
            jeep.NetBeginHold();
            T.Check("hold latched: Freeze on, STATIC mode, NetHeld",
                    jeep.Freeze && jeep.FreezeMode == RigidBody3D.FreezeModeEnum.Static && jeep.NetHeld);

            var basis = jeep.GlobalTransform.Basis;
            var pos = jeep.GlobalPosition;
            float maxDrift = 0f;
            for (int i = 0; i < 100; i++)
            {
                pos += new Vector3(0.2f, 0f, 0f);
                jeep.NetHoldTeleport(new Transform3D(basis, pos));
                yield return Ticks(1);
                maxDrift = Mathf.Max(maxDrift, jeep.GlobalPosition.DistanceTo(pos));
            }
            T.Check($"teleports read back exactly -- no wheel/solver fight on the frozen body (max drift {maxDrift:0.####} m)",
                    maxDrift < 0.001f);

            // --- 2. server queries see the held body at the teleported pose ---
            var space = World.GetWorld3D().DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(pos + Vector3.Up * 3f, pos + Vector3.Down * 1f, (1u << 0) | (1u << 6));
            var hit = space.IntersectRay(q);
            bool sawJeep = hit.Count > 0 && hit["collider"].AsGodotObject() == jeep;
            T.Check("a server-bullet-mask ray (bit0|bit6) hits the HELD body at the teleported pose (NetGhost keeps bit6)", sawJeep);

            // --- 3. the held body still displaces a character driven through it (run-over posture) ---
            var walker = Rigs.Player(World, pos + new Vector3(4f, 0.1f, 0f));
            yield return Ticks(10);   // let the character land + settle on the ground
            var walkerStart = walker.GlobalPosition;
            for (int i = 0; i < 60; i++)
            {
                pos += new Vector3(0.12f, 0f, 0f);   // 6 m/s -- slow enough that the capsule can't be tunnel-skipped
                jeep.NetHoldTeleport(new Transform3D(basis, pos));
                yield return Ticks(1);
            }
            var flat = walker.GlobalPosition - walkerStart; flat.Y = 0f;
            T.Check($"the held body displaced the character in its path ({flat.Length():0.00} m)", flat.Length() > 0.3f);

            // --- kinematic PROBE (report data, not a gate): document what FreezeMode.Kinematic does on
            // this build. The port ships STATIC either way; this line is why. ---
            var probe = Vehicle.BuildByName("jeep");
            World.AddChild(probe);
            probe.GlobalPosition = new Vector3(0f, 1.2f, 30f);
            yield return Ticks(50);
            probe.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            probe.Freeze = true;
            var kinPos = probe.GlobalPosition;
            float kinDrift = 0f;
            for (int i = 0; i < 20; i++)
            {
                kinPos += new Vector3(0.2f, 0f, 0f);
                probe.GlobalTransform = new Transform3D(probe.GlobalTransform.Basis, kinPos);
                yield return Ticks(1);
                kinDrift = Mathf.Max(kinDrift, probe.GlobalPosition.DistanceTo(kinPos));
            }
            var kq = PhysicsRayQueryParameters3D.Create(kinPos + Vector3.Up * 3f, kinPos + Vector3.Down * 1f, (1u << 0) | (1u << 5) | (1u << 6));
            var khit = space.IntersectRay(kq);
            bool kinSeen = khit.Count > 0 && khit["collider"].AsGodotObject() == probe;
            GD.Print($"[freeze-spike] KINEMATIC probe: teleport drift {kinDrift:0.####} m, ray sees body: {kinSeen}");
            probe.QueueFree();

            // --- 4. release: physics resumes from the seeded velocity (exit/disconnect handoff) ---
            jeep.NetEndHold(new Vector3(10f, 0f, 0f), Vector3.Zero);
            jeep.NetGhost(false);
            jeep.Brake = 0f;   // isolate "physics integrates from the seeded state" from brake tuning: the spawn-parked handbrake was still applied (no Drive ever ran here), which braked the first spike run to 0.74 m -- physics WAS live, just stopping. The real release path parks via the SP exit effects anyway.
            T.Check("release: Freeze off, hold flag cleared, base layer restored",
                    !jeep.Freeze && !jeep.NetHeld && (jeep.CollisionLayer & (1u << 0)) != 0);
            var relPos = jeep.GlobalPosition;
            yield return Ticks(1);
            float firstTick = jeep.GlobalPosition.X - relPos.X;
            float firstVx = jeep.LinearVelocity.X;
            T.Check($"the seeded velocity TOOK -- first live tick moved {firstTick:0.###} m at vx {firstVx:0.0} m/s",
                    firstTick > 0.15f && firstVx > 8f);
            yield return Ticks(24);   // 0.5 s total
            float coasted = jeep.GlobalPosition.X - relPos.X;
            // The verified handoff shape (spike run 2026-07-18): the freed body is fully dynamic, but its
            // wheels resume at rotation speed 0, so a 10 m/s release SKIDS on locked-wheel tire friction to
            // rest in ~0.2 s (t0 vx 9.98 -> t8 vx 0.05, 0.74 m traveled). That matches the SP exit feel
            // (the exit effects Park the car anyway) -- what the hold must guarantee is that the body
            // INTEGRATES and SETTLES, never sticks frozen, sleeps mid-air, or launches.
            T.Check($"released body skid-settled under live physics ({coasted:0.00} m, vx now {jeep.LinearVelocity.X:0.00})",
                    coasted > 0.5f && Mathf.Abs(jeep.LinearVelocity.X) < 0.5f);
            T.Check($"grounded through the handoff (dY {jeep.GlobalPosition.Y - restY:0.00} m -- no vanish, no launch)",
                    Mathf.Abs(jeep.GlobalPosition.Y - restY) < 1.5f);
            yield return Ticks(150);
            T.Check("body stays valid + upright after free-rolling out", GodotObject.IsInstanceValid(jeep)
                    && !jeep.Exploded && jeep.GlobalTransform.Basis.Y.Dot(Vector3.Up) > 0.7f);
        }
    }
}

