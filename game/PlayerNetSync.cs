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
    //   3. then consumes the peer's next queued MoveInput in seq order (yaw + axes + the v2 jump bit + the
    //      stance bits) through the Scripted* seams -- one per tick from the PlayerReplication jitter
    //      buffer (the mp-inputbuffer fix), coasting on the last consumed input when the queue starves;
    //      the body MoveAndSlides on real terrain/objects on its own physics tick.
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
            public bool PairingExact = true;            // C1.5: false while the body's last step was a stale-seq coast/hold -- publishing that position would re-pair an already-acked seq with newer motion (the phantom correction)
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
                    // respawn, console teleport): adopt it -- body snaps to entity, write-back next tick.
                    // Through TeleportTo, NOT a bare GlobalPosition write: _PhysicsProcess restores
                    // GlobalPosition from _interpCurr before moving (§7 risk 5), so a bare write is UNDONE
                    // on the body's next tick and the write-back re-asserts the OLD spot onto the entity --
                    // #27's second act: the console teleport held for one tick, then the avatar dragged the
                    // player straight back. TeleportTo resets the interp snapshots (and zeroes velocity).
                    t.Seated = false;
                    t.Body.TeleportTo(ToG(e.Pos));
                    t.LastDrivenPos = e.Pos;
                }
                else if (t.PairingExact)
                {
                    // 1) authoritative write-back: last tick's post-physics result, under the seq of the
                    // input that produced it (ServerDrive quantizes + marks ExternallyDriven)
                    var pos = t.Body.GlobalPosition;
                    float yaw = t.Body.RotationDegrees.Y;
                    _server.Players.ServerDrive(e.OwnerPlayerId, ToU(pos), yaw, t.LastInputSeq, tick);
                    t.LastDrivenPos = e.Pos;   // ServerDrive just stamped the quantized pos onto the entity
                }
                // else (C1.5, the phantom-pairing fix -- found by the plan §3 WAN harness): the body's
                // last step was a starved-coast/hold tick (TryConsumeInput repeated a stale seq). The
                // BODY keeps integrating the held intent (the count invariant: a delayed input's tick is
                // integrated once, when it coasts), but the ENTITY holds the last exact (pos, seq)
                // pairing -- publishing the coast-advanced position under the already-acked seq made the
                // jittered 25 Hz snapshot stream show the owner a 1-3-tick "error" that was never real:
                // the residual WAN inchworm's dominant engine (13.951 m/min of phantom correction on the
                // wan_walk baseline). Retail never publishes speculated state either: no packet -> no
                // simulate (U3 PlayerInput.cs). Observers just see this avatar 1-2 ticks stale during a
                // starve; the next real consume write-back carries the accumulated motion.

                // 2) consume this tick's input IN SEQ ORDER (the mp-inputbuffer fix, real Unturned's
                // serversidePackets model): one dequeue per tick so the avatar integrates the same input
                // stream, in the same order and count, the shell predicted -- the held-latest model
                // skipped/re-integrated ticks under jitter and the count gap resolved as the sprint-stop
                // yank. Starvation coasts on the last consumed input inside TryConsumeInput (bounded by
                // MaxCoastTicks, then a zero-motion hold -- no ghost-running stale intent); false means
                // nothing to integrate at all -> stand still (death/enter-vehicle cleared it, or none yet)
                if (_server.Players.TryConsumeInput(e.OwnerPlayerId, out var inp, out bool seqAdvanced))
                {
                    t.Body.RotationDegrees = new Vector3(0f, inp.YawDegrees, 0f);
                    t.Body.ScriptedInput = new UnityEngine.Vector2(inp.MoveX, inp.MoveY);
                    t.Body.ScriptedJump = inp.Jump;
                    t.Body.ScriptedStance = inp.Stance;   // integrate at the stance the shell predicted at (the inchworm fix)
                    t.LastInputSeq = inp.Seq;
                    t.PairingExact = seqAdvanced;   // stale-seq coast/hold ticks must not be written back (C1.5)
                }
                else
                {
                    t.Body.ScriptedInput = UnityEngine.Vector2.zero;
                    t.Body.ScriptedJump = false;
                    t.Body.ScriptedStance = EPlayerStance.STAND;
                    t.PairingExact = true;   // nothing consumed since spawn/clear: the body stands at the last exact pairing
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
