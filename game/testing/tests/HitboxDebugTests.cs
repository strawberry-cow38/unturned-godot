using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The hitbox debug console toggles (`hitbox client|server|off`) -- the collision-parity overlay for the
    // MP pullback workstream. These are the headless smoke layer: the REAL DevConsole parse path flips the
    // toggle state, the overlay stays inert (no node) until toggled, and the client sweep actually builds
    // wireframes on real colliders + frees them on off. The look itself is a live-session eyeball check.
    public class HitboxConsoleToggle : GameTest
    {
        public override string Name => "debug.hitbox_console";
        public override IEnumerable<Step> Run()
        {
            var con = new DevConsole();
            World.AddChild(con);
            yield return Ticks(1);

            T.Check("inert by default: no toggle set, no overlay node", !HitboxDebugOverlay.ClientEnabled && !HitboxDebugOverlay.ServerEnabled && !HitboxDebugOverlay.InstanceAlive);

            con.Run("hitbox client");   // the real console parse path, not a direct API call
            T.Check("`hitbox client` toggles the client overlay on", HitboxDebugOverlay.ClientEnabled && !HitboxDebugOverlay.ServerEnabled);
            T.Check("overlay node attached once a toggle is on", HitboxDebugOverlay.InstanceAlive);

            con.Run("hitbox server");
            T.Check("`hitbox server` toggles server on, independent of client", HitboxDebugOverlay.ClientEnabled && HitboxDebugOverlay.ServerEnabled);

            con.Run("hitbox client");
            T.Check("`hitbox client` again toggles client OFF, server stays on", !HitboxDebugOverlay.ClientEnabled && HitboxDebugOverlay.ServerEnabled);

            con.Run("hitbox bogus");
            T.Check("unknown arg leaves the toggles untouched", !HitboxDebugOverlay.ClientEnabled && HitboxDebugOverlay.ServerEnabled);

            con.Run("hitbox");          // bare verb = status report, no flip (the arg-required early-return is bypassed for hitbox)
            T.Check("bare `hitbox` reports without flipping", !HitboxDebugOverlay.ClientEnabled && HitboxDebugOverlay.ServerEnabled);

            con.Run("hitbox off");
            yield return Ticks(2);      // QueueFree flushes
            T.Check("`hitbox off` kills both + frees the overlay node", !HitboxDebugOverlay.ClientEnabled && !HitboxDebugOverlay.ServerEnabled && !HitboxDebugOverlay.InstanceAlive);
        }
    }

    // The client sweep against real colliders: a deployable's BoxShape3D and the player shell's capsule
    // each get a wireframe child; `off` frees every wire. Headless-safe -- GetDebugMesh is pure mesh data.
    public class HitboxClientWireframes : GameTest
    {
        public override string Name => "debug.hitbox_client_wires";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, Vector3.Zero, 0f);
            var player = Rigs.Player(World, new Vector3(3f, 0.5f, 0f));   // in group "players" -> also the sweep's viewpoint
            yield return Ticks(1);

            HitboxDebugOverlay.Console("client", Tree);
            yield return Ticks(3);   // the first _PhysicsProcess sweeps immediately

            T.Check($"sweep built wireframes (got {HitboxDebugOverlay.DebugClientWires})", HitboxDebugOverlay.DebugClientWires >= 2);
            T.Check("deployable box collider carries a wireframe child", HasWire(gen));
            T.Check("player shell capsule carries a wireframe child", HasWire(player));

            HitboxDebugOverlay.Console("off", Tree);
            yield return Ticks(2);
            T.Check("off: wires freed", HitboxDebugOverlay.DebugClientWires == 0 && !HasWire(gen) && !HasWire(player));
        }

        static bool HasWire(Node body)
        {
            foreach (var c in body.GetChildren())
                if (c is CollisionShape3D cs && !cs.Disabled)
                    foreach (var w in cs.GetChildren())
                        if (w is MeshInstance3D mi && GodotObject.IsInstanceValid(mi) && !mi.IsQueuedForDeletion()) return true;
            return false;
        }
    }
}
