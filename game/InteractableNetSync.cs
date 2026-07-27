using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    /// <summary>
    /// Doors, beds and deadzones, server side: the bridge between the built world and the authoritative
    /// state that answers commands about it.
    ///
    /// It does three jobs, and the third is the one worth naming. It REGISTERS each Door/Bed node with
    /// ServerInteractables under a deterministic id; it seeds ServerDeadzones from the built volumes; and it
    /// MIRRORS every authoritative door decision back onto the server's own Door node -- because the server
    /// runs a real world too, and a door that swings only in ServerInteractables is a door that clients see
    /// open and the server's own physics still treats as shut. Bullets and bodies would stop at a door
    /// everyone can see standing wide open.
    ///
    /// Ids come from world-build order, the same trick the resource and destructible bitmaps use: every peer
    /// runs the identical WorldBuilder, so the nth door is the nth door everywhere, and no id has to be
    /// minted or transmitted. Doors and beds number independently -- the commands are typed, so door 1 and
    /// bed 1 can never be confused for each other.
    /// </summary>
    public sealed class InteractableNetSync
    {
        readonly NetWorldServer _server;
        // The id is held BESIDE the node, not read back off it: once a door is broken down the node is gone
        // and its NetId with it, and that is exactly the moment the id is needed to deregister it.
        readonly List<(uint NetId, Door Node)> _doors = new List<(uint, Door)>();

        /// <summary>Ids start at 1: 0 is the "not replicated, this is a singleplayer node" sentinel every
        /// other node in this codebase uses, and a door with NetId 0 must never be routable.</summary>
        public const uint FirstId = 1;

        public InteractableNetSync(NetWorldServer server, Node root, DeadzoneField deadzones = null)
        {
            _server = server;
            if (root != null) RegisterWorld(root);
            if (deadzones != null) SeedDeadzones(deadzones);
        }

        public int DoorCount => _doors.Count;

        void RegisterWorld(Node root)
        {
            uint doorId = FirstId, bedId = FirstId;
            foreach (var node in Walk(root))
            {
                if (node is Door d)
                {
                    d.NetId = doorId++;
                    _doors.Add((d.NetId, d));
                    var p = d.GlobalPosition;
                    _server.Interactables.RegisterDoor(d.NetId, new UVector3(p.X, p.Y, p.Z), d.Owner, d.IsLocked);
                }
                else if (node is Bed b)
                {
                    b.NetId = bedId++;
                    var p = b.GlobalPosition;
                    _server.Interactables.RegisterBed(b.NetId, new UVector3(p.X, p.Y, p.Z), b.RotationDegrees.Y);
                }
            }
            GD.Print($"[interactables] registered {_doors.Count} door(s) + {_server.Interactables.BedCount} bed(s) as server-authoritative");
        }

        void SeedDeadzones(DeadzoneField field)
        {
            // The volumes themselves are authored map data every peer already built, so they are copied
            // across rather than replicated -- the only thing that needs to reach a client is the damage,
            // which the vitals and combat blocks already carry.
            foreach (var v in field.Volumes)
                _server.Deadzones.AddVolume(v.Center, v.HalfExtent, v.Zone);
            GD.Print($"[interactables] seeded {_server.Deadzones.VolumeCount} deadzone volume(s) server-side");
        }

        static IEnumerable<Node> Walk(Node n)
        {
            yield return n;
            foreach (var child in n.GetChildren())
                foreach (var d in Walk(child))
                    yield return d;
        }

        /// <summary>Keep the server's own door nodes in step with the state it is publishing. Polled rather
        /// than event-driven so a decision made anywhere -- a command, a console hook, a future AI that
        /// opens doors -- lands on the node without every such caller having to remember to mirror it.</summary>
        public void Tick()
        {
            for (int i = _doors.Count - 1; i >= 0; i--)
            {
                var (netId, node) = _doors[i];
                if (!GodotObject.IsInstanceValid(node))
                {
                    // Broken down: it stops being commandable, and the removal rides the state block so
                    // clients drop it from their table too.
                    _server.Interactables.RemoveDoor(netId);
                    _doors.RemoveAt(i);
                    continue;
                }
                bool open = _server.Interactables.IsDoorOpen(netId);
                if (node.IsOpen != open) node.ApplyReplicatedToggle(open);
                bool locked = _server.Interactables.IsDoorLocked(netId);
                if (node.IsLocked != locked) node.ApplyReplicatedLocked(locked);
            }
        }
    }
}
