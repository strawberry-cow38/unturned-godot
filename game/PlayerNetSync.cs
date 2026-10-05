using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server side of the on-foot CLIENT-AUTHORITY model (mp-clientauth-foot, wire v9) -- the
    // VehicleNetSync hold shape applied to walkers. The server no longer simulates any owner's
    // movement: the owner's client streams its transform (PlayerStateCommand), the engine-free
    // ServerPlayerAuthority envelope-validates + adopts it into the entity (ServerDrive), and THIS
    // sync keeps one physical FOLLOWER body per remote peer teleported onto the entity every tick --
    // so zombies (sensing/attacks via PlayerRegistry), server ballistics and reach checks still see
    // a real body at the adopted position. The body is a PlayerController { NetAvatar, NetHold }:
    // its _PhysicsProcess movement tail never runs; stance/moving dressing comes from the adopted
    // claim (hitbox capsule + zombie stealth radius). One body, zero divergence -- the pre-v9 avatar
    // (input consumption, write-back, ack band, misprediction events) is deleted, not bypassed.
    // Every external entity move (seat teleport, vehicle-exit spot, console teleport, respawn) is
    // adopted the same way: the body follows the ENTITY, never the reverse. Follows through
    // TeleportTo (interp snapshots reset + velocity zeroed, §7 risk 5) -- a bare GlobalPosition
    // write on a live PlayerController is undone by its own interp restore.
    // Ticked between "net.server.sim" and the publish syncs on the world's SimRoot (DedicatedServer).
    public sealed class PlayerNetSync
    {
        readonly NetWorldServer _server;
        readonly Node _host;

        public System.Func<ushort> LocalPlayerId;   // listen-server/loopback local shell owner (null on dedicated) -- its node IS the authority (MpLoopback drives it); never body it

        sealed class Tracked
        {
            public PlayerController Body;
            public UnityEngine.Vector3 LastPos;   // last entity pos the body was placed at (moving = it changed)
            public int FootNoiseTicks;            // MP hearing: per-avatar throttle for the server-side footstep-noise emit (20 ticks = the SP 0.4 s cadence)
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
            ushort localId = LocalPlayerId?.Invoke() ?? 0;

            foreach (var e in _server.Players.All)
            {
                if (e.OwnerPlayerId == localId) continue;
                if (!_tracked.TryGetValue(e.OwnerPlayerId, out var t))
                {
                    var body = new PlayerController { NetAvatar = true, NetHold = true, CaptureMouse = false };
                    // P3b (SP/MP-unify): route zombie melee/acid + explosion-blast hits landing on this server-
                    // owned follower body into the server HP sink (this is the SOLE thing that damages the body --
                    // fall/OOB never call TakeDamage on a NetAvatar, they are server-derived from the owner's claims).
                    ushort owner = e.OwnerPlayerId;
                    body.NetDamageSink = amount => _server.Combat.DamagePlayerExternal(owner, amount);
                    _host.AddChild(body);                       // in the tree FIRST, else GlobalPosition no-ops
                    body.GlobalPosition = ToG(e.Pos);
                    body.RotationDegrees = new Vector3(0f, e.YawDegrees, 0f);
                    body.Spawn = body.GlobalPosition;           // never the default (0,1,0) -- underground on PEI
                    t = new Tracked { Body = body, LastPos = e.Pos };
                    _tracked[e.OwnerPlayerId] = t;
                }
                if (!GodotObject.IsInstanceValid(t.Body)) continue;   // freed externally; the stale sweep below retires it

                // the ONE follow rule: body onto entity, whatever moved it (adopted claim, seat teleport,
                // exit spot, console teleport, respawn). TeleportTo resets interp + zeroes velocity.
                bool moved = (e.Pos - t.LastPos).sqrMagnitude > 1e-9f;
                if (moved) t.Body.TeleportTo(ToG(e.Pos));
                t.Body.RotationDegrees = new Vector3(0f, e.YawDegrees, 0f);
                t.LastPos = e.Pos;

                // dressing from the adopted claim: the hitbox capsule must match the CLAIMED stance (a
                // crouched player is hit as crouched) and the zombie stealth radius reads stance+moving
                EPlayerStance stance = _server.PlayerHost.TryGetDrivenState(e.OwnerPlayerId, out var st) ? st.Stance : EPlayerStance.STAND;
                t.Body.NetHoldPose(stance, moved);

                // MP hearing (VoX 2026-07-20): a moving remote player must make FOOTSTEP noise the SERVER's zombie
                // AI can hear. The SP emit (PlayerController Phase-3 hearing) lives in the movement tail that never
                // runs on this NetHold avatar, so re-derive it here from the adopted stance + moved flag, throttled
                // per-avatar to the SP 0.4 s cadence (20 ticks @ 50 Hz). Skip a DRIVER (a seat teleport moves the
                // entity; the car isn't making footsteps), and the loudness follows the same stance/sneaky curve as
                // SP (sprint loud .. prone near-silent, quieted by SNEAKYBEAKY). Pre-fix, footsteps were emitted
                // only on the client's local tree, so a dedicated server's zombies only ever aggro'd on SIGHT.
                if (t.FootNoiseTicks > 0) t.FootNoiseTicks--;
                if (moved && t.FootNoiseTicks <= 0 && !_server.VehicleHost.IsDriver(e.OwnerPlayerId))
                {
                    t.FootNoiseTicks = 20;
                    float sneaky = _server.Skills.TryGet(e.OwnerPlayerId, out var se) ? se.Skills.SneakyBeakyNoiseMultiplier() : 1f;
                    float loud = StealthDetection.Radius(stance, true) * sneaky;
                    if (loud > 2f) SoundBus.Emit(t.Body.GetTree(), t.Body.GlobalPosition, loud);
                }
            }

            // entities gone (peer disconnected) or bodies freed externally -> retire the follower
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

        static Vector3 ToG(UnityEngine.Vector3 v) => new Vector3(v.x, v.y, v.z);
    }
}
