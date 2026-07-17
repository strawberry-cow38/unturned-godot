using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server side of the PLAYER authority split (PEI_CLIENT_PLAN §2.3 / C2), the VehicleNetSync shape:
    // one headless PlayerController avatar body per REMOTE peer, walking the server's REAL world.
    //   1. spawns a PlayerController { NetAvatar = true } at each new player entity's spawn (and frees it
    //      when the entity goes -- disconnects drain through the same scan);
    //   2. per tick writes back the body's post-physics transform through the existing ServerDrive seam
    //      (paired with the seq of the input that PRODUCED that position, so lastProcessedInputSeq acks
    //      stay honest), which marks the entity ExternallyDriven -- ServerStep's flat demo integration
    //      never touches it again;
    //   3. then injects the peer's held MoveInput (yaw + axes + the v2 jump bit + the stance bits) through
    //      the Scripted* seams; the body MoveAndSlides on real terrain/objects on its own physics tick.
    //      Stance MUST ride along (the mp-inchworm fix): the client shell predicts at its stance's speed,
    //      so the avatar has to integrate at the SAME one -- a sprinting shell against a stand-walking
    //      avatar grows (SPRINT-STAND)*dt of error every tick and the reconciler drags it back as a lurch.
    // Seated peers (VehicleHost.IsDriver) are NEVER written back -- the seat teleport owns the entity
    // (§3.6); the body parks under the seat by FOLLOWING the entity. Any other external entity move
    // (vehicle-exit teleport, combat respawn, console teleport) is adopted the same way: whenever the
    // entity's position isn't the one this sync last wrote, the BODY snaps to the ENTITY, never the
    // reverse -- ServerTeleport stays authoritative over avatar physics.
    // Ticked between "net.server.sim" and the publish syncs on the world's SimRoot (DedicatedServer).
    public sealed class PlayerNetSync
    {
        readonly NetWorldServer _server;
        readonly Node _host;

        public System.Func<ushort> LocalPlayerId;   // listen-server/loopback local shell owner (null on dedicated) -- its node IS the authority (MpLoopback drives it); never avatar it

        sealed class Tracked
        {
            public PlayerController Body;
            public ushort LastInputSeq;                 // seq of the input the body last physics-stepped (pairs with the write-back)
            public UnityEngine.Vector3 LastDrivenPos;   // what we last wrote (wire-quantized) -- a differing entity pos means someone else teleported it
            public bool Seated;
        }
        readonly Dictionary<ushort, Tracked> _tracked = new();
        readonly List<ushort> _stale = new();

        public int TrackedCount => _tracked.Count;
        public bool TryGetBody(ushort ownerPlayerId, out PlayerController body)
        {
            body = _tracked.TryGetValue(ownerPlayerId, out var t) ? t.Body : null;
            return body != null && GodotObject.IsInstanceValid(body);
        }

        public PlayerNetSync(NetWorldServer server, Node host)
        {
            _server = server;
            _host = host;
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            ushort localId = LocalPlayerId?.Invoke() ?? 0;

            foreach (var e in _server.Players.All)
            {
                if (e.OwnerPlayerId == localId) continue;
                if (!_tracked.TryGetValue(e.OwnerPlayerId, out var t))
                {
                    // an entity already driven by someone else (a listen-server shell ServerDriving directly,
                    // the loopback pattern) is not ours to avatar -- adopting it would double-drive the seam
                    if (e.IsExternallyDriven) continue;
                    // DeterministicGround: pair of ClientWorldSession.SpawnShell -- avatar + shell must
                    // make the SAME grounded decision or their vertical integration diverges (rubberband)
                    var body = new PlayerController { NetAvatar = true, CaptureMouse = false, DeterministicGround = true };
                    _host.AddChild(body);                       // in the tree FIRST, else GlobalPosition no-ops
                    body.GlobalPosition = ToG(e.Pos);
                    body.RotationDegrees = new Vector3(0f, e.YawDegrees, 0f);
                    body.Spawn = body.GlobalPosition;           // never the default (0,1,0) -- underground on PEI
                    body.ScriptedInput = UnityEngine.Vector2.zero;   // never fall through to keyboard polling
                    body.ScriptedJump = false;
                    body.ScriptedStance = EPlayerStance.STAND;       // wire-driven from the first held input on
                    t = new Tracked { Body = body, LastDrivenPos = e.Pos };
                    _tracked[e.OwnerPlayerId] = t;
                }
                if (!GodotObject.IsInstanceValid(t.Body)) continue;   // freed externally; the stale sweep below retires it

                // seated: the seat teleport owns the entity (VehicleHost.Step teleports it to the vehicle
                // every tick; ServerClearInput dropped the held walk input on enter). The body just follows.
                if (_server.VehicleHost.IsDriver(e.OwnerPlayerId))
                {
                    t.Body.GlobalPosition = ToG(e.Pos);
                    t.Body.Velocity = Vector3.Zero;
                    t.Body.ScriptedInput = UnityEngine.Vector2.zero;
                    t.Body.ScriptedJump = false;
                    t.LastDrivenPos = e.Pos;
                    t.Seated = true;
                    continue;
                }
                if (t.Seated || (e.Pos - t.LastDrivenPos).sqrMagnitude > 1e-9f)
                {
                    // the entity moved OUTSIDE this sync (vehicle-exit teleport beside the door, combat
                    // respawn, console teleport): adopt it -- body snaps to entity, write-back next tick
                    t.Seated = false;
                    t.Body.GlobalPosition = ToG(e.Pos);
                    t.Body.Velocity = Vector3.Zero;
                    t.LastDrivenPos = e.Pos;
                }
                else
                {
                    // 1) authoritative write-back: last tick's post-physics result, under the seq of the
                    // input that produced it (ServerDrive quantizes + marks ExternallyDriven)
                    var pos = t.Body.GlobalPosition;
                    float yaw = t.Body.RotationDegrees.Y;
                    _server.Players.ServerDrive(e.OwnerPlayerId, ToU(pos), yaw, t.LastInputSeq, tick);
                    t.LastDrivenPos = e.Pos;   // ServerDrive just stamped the quantized pos onto the entity
                }

                // 2) inject this tick's held input (held-keys model: the latest keeps applying until
                // replaced); no input held -> stand still (death/enter-vehicle cleared it, or none yet)
                if (_server.Players.TryGetHeldInput(e.OwnerPlayerId, out var inp))
                {
                    t.Body.RotationDegrees = new Vector3(0f, inp.YawDegrees, 0f);
                    t.Body.ScriptedInput = new UnityEngine.Vector2(inp.MoveX, inp.MoveY);
                    t.Body.ScriptedJump = inp.Jump;
                    t.Body.ScriptedStance = inp.Stance;   // integrate at the stance the shell predicted at (the inchworm fix)
                    t.LastInputSeq = inp.Seq;
                }
                else
                {
                    t.Body.ScriptedInput = UnityEngine.Vector2.zero;
                    t.Body.ScriptedJump = false;
                    t.Body.ScriptedStance = EPlayerStance.STAND;
                }
            }

            // entities gone (peer disconnected) or bodies freed externally -> retire the avatar
            _stale.Clear();
            foreach (var kv in _tracked)
                if (!_server.Players.TryGetByOwner(kv.Key, out _) || !GodotObject.IsInstanceValid(kv.Value.Body))
                    _stale.Add(kv.Key);
            foreach (var owner in _stale)
            {
                if (GodotObject.IsInstanceValid(_tracked[owner].Body)) _tracked[owner].Body.QueueFree();
                _tracked.Remove(owner);
            }
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
        static Vector3 ToG(UnityEngine.Vector3 v) => new Vector3(v.x, v.y, v.z);
    }
}
