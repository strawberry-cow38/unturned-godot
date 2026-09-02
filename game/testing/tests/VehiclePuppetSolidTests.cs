using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // "No collisions for vehicles on the server" (strawberry 2026-09-02). On a --connect client every vehicle
    // except the one you drive is a VehiclePuppet (the Client world build never spawns real Vehicles; only
    // VehicleReplicaView materializes them), and the puppet's one collider was a bit-5 "look-detection" box
    // by design: the shell's CollisionMask is bit0|bit6, bit0|bit6 & bit5 == 0, so the player walked straight
    // through every parked car (and the locally driven Vehicle, mask bit0|bit8, drove through them). The server
    // was innocent -- under client-authoritative position it adopts the client's claim inside a speed envelope
    // and never re-solves geometry -- so the fix is where the ghost was: the puppet's hull now carries bit 0
    // like the real VehicleBody3D does in singleplayer.
    //
    // This walks a real PlayerController into a real puppet built by the SAME Vehicle.BuildPuppetByName the
    // replica view calls, on the flat ground plane, for long enough to have crossed it three times over.
    // TEETH: with the hull back on bit 5 alone the shell ends up ~13 m down the road, well past the far face.
    public class VehiclePuppetIsSolid : GameTest
    {
        public override string Name => "net.vehicle_puppet_is_solid";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var puppet = Vehicle.BuildPuppetByName("jeep", 0);
            World.AddChild(puppet);
            puppet.GlobalPosition = new Vector3(0f, 0f, -6f);   // 6 m dead ahead of the walker (the shell walks -Z at yaw 0)

            // the hull the puppet actually carries, so the near/far faces come from the node, not a guess
            StaticBody3D hull = null; BoxShape3D box = null; CollisionShape3D shape = null;
            foreach (var c in puppet.GetChildren())
                if (c is StaticBody3D sb)
                    foreach (var cc in sb.GetChildren())
                        if (cc is CollisionShape3D cs && cs.Shape is BoxShape3D b) { hull = sb; shape = cs; box = b; }
            T.Check("the puppet carries a StaticBody3D hull with a box shape", hull != null && box != null);
            if (hull == null || box == null) yield break;
            T.Check($"the hull is on the world layer (bit 0) the shell's mask includes (layer 0x{hull.CollisionLayer:X})", (hull.CollisionLayer & (1u << 0)) != 0);
            T.Check($"...and still on the vehicle layer (bit 5) the look-ray and tow scan probe (layer 0x{hull.CollisionLayer:X})", (hull.CollisionLayer & (1u << 5)) != 0);
            float nearFaceZ = puppet.GlobalPosition.Z + shape.Position.Z + box.Size.Z * 0.5f;   // the face toward the walker (+Z side)
            float farFaceZ = puppet.GlobalPosition.Z + shape.Position.Z - box.Size.Z * 0.5f;
            T.Check($"fixture: the hull spans z {nearFaceZ:0.00} .. {farFaceZ:0.00} (walker starts at 0)", nearFaceZ < -1f && farFaceZ < nearFaceZ);

            var player = new PlayerController { CaptureMouse = false };
            World.AddChild(player);
            player.EquipUnarmed();
            player.GlobalPosition = new Vector3(0f, 1.1f, 0f);
            player.RotationDegrees = Vector3.Zero;
            yield return Ticks(10);   // settle onto the ground
            float startZ = player.TruePhysicsPosition.Z;

            player.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   // walk forward (-Z) into the car
            float minZ = startZ;
            for (int i = 0; i < 200; i++)   // 4 s of walking: unobstructed that is ~15+ m, three car-lengths past the far face
            {
                yield return Ticks(1);
                minZ = Mathf.Min(minZ, player.TruePhysicsPosition.Z);
            }
            player.ScriptedInput = UnityEngine.Vector2.zero;
            var end = player.TruePhysicsPosition;

            T.Check($"the walker actually walked (from z {startZ:0.00} to {end.Z:0.00})", end.Z < startZ - 1.5f);
            // the capsule (radius 0.35) stops with its centre just outside the near face -- never inside the hull,
            // never beyond it. The pre-fix ghost hull let it reach ~z -15.
            T.Check($"the puppet STOPPED the walker at its near face (deepest z {minZ:0.00} vs face {nearFaceZ:0.00})", minZ > nearFaceZ - 0.05f);
            T.Check($"...and it never came out the far side (end z {end.Z:0.00} vs far face {farFaceZ:0.00})", end.Z > farFaceZ);
            T.Check($"it did not climb onto the car either (y {end.Y:0.00})", end.Y < 1.6f);
        }
    }
}
