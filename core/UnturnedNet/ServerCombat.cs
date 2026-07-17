using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Static-geometry raycast the host world supplies (the dedicated/loopback Godot world wires
    /// its DirectSpaceState here so server bullets stop at buildings). Null = open field (the L0 default).</summary>
    public delegate bool CombatWorldRay(Vector3 from, Vector3 to, out Vector3 hitPoint);

    /// <summary>
    /// The server-side owner of zombie BRAINS (game: ZombieNetSync routing to the real ZombieController;
    /// L0 tests: a mock hp table). ServerCombat resolves the hit against replicated server positions, the
    /// host applies the damage to the authoritative brain. Returns true iff THIS hit killed the zombie
    /// (drives kill credit + the ZombieDied event; the host should treat the zombie as dead from then on).
    /// </summary>
    public interface IZombieHost
    {
        bool DamageZombie(uint zombieNetId, float damage, Vector3 point, Vector3 dir, ushort attackerPlayerId, bool headshot);
    }

    /// <summary>Server-side gun parameters (defaults = the Eaglefire's real .dat numbers + the Phase-1
    /// Hitscan zone table). v1: one host-settable profile per player, seeded from Default -- per-player
    /// held-item replication arrives with the Phase 6 inventory surface.</summary>
    public sealed class ServerGunProfile
    {
        public float PlayerDamage = 40f;        // Eaglefire Player_Damage
        public float ZombieDamage = 99f;        // Eaglefire Zombie_Damage (flat vs zombies, like the SP StepBullets path)
        public float HeadMult = 3.0f;           // the NetServer.Hitscan zone table (head/torso/leg)
        public float TorsoMult = 1.0f;
        public float LegMult = 0.6f;
        public int FirerateTicks = 4;           // .dat Firerate; min shot gap = Firerate+1 ticks (the SP off-by-one rule)
        public float MuzzleVelocity = 500f;
        public int BallisticSteps = 20;
        public float GravityMultiplier = 4f;    // bullet gravity = -9.81 * this
        public int Pellets = 1;
        public int MagCapacity = 30;
        public int ReloadTicks = 82;            // 1.633 s Gun_Reload
        public float MaxAimOriginOffset = 3f;   // Fire.Origin must sit within this of the avatar (eye 1.75 + muzzle 0.4 + grain)
    }

    public sealed class ServerMeleeProfile
    {
        public float PlayerDamage = 40f;
        public float ZombieDamage = 50f;        // Military Knife Zombie_Damage
        public float Range = 1.75f;
        public float StrongMult = 1.5f;         // .dat Strength on a strong swing
        public int CooldownTicks = 23;          // ~0.45 s weak-swing cadence
        public int HitDelayTicks = 16;          // damage lands at ~70% of the swing (the SP deferred-hit rule)
    }

    public sealed class ServerGrenadeProfile
    {
        public float PlayerDamage = 175f;       // Grenade.dat
        public float ZombieDamage = 175f;
        public float Radius = 8f;
        public int FuseTicks = 125;             // 2.5 s
        public int CooldownTicks = 50;          // 1 s between throws (the SP _grenadeCd)
        public float MaxThrowSpeed = 48f;       // sanity cap on the commanded velocity
    }

    public sealed class ServerCombatDiagnostics
    {
        public long ShotsAccepted;
        public long ShotsRejectedRate;          // faster than Firerate+1 ticks
        public long ShotsRejectedAmmo;          // empty magazine
        public long ShotsRejectedReloading;
        public long ShotsRejectedRange;         // claimed muzzle origin too far from the avatar
        public long ShotsRejectedDeadOrMissing;
        public long ShotsRejectedMalformed;     // degenerate aim direction
        public long MeleeAccepted, MeleeRejected;
        public long GrenadesAccepted, GrenadesRejected;
        public long BulletHitsPlayer, BulletHitsZombie, BulletHitsWorld, BulletsExpired;
    }

    /// <summary>
    /// The authoritative combat step (MP_PLAN §3.4/§4 Phase 5), engine-free. The Fire command carries the
    /// client's aim ray; the SERVER spawns the bullet and steps it through the exact SP gravity-drop model
    /// (BallisticsMath) against SERVER positions -- players from PlayerReplication, zombies from
    /// ZombieReplication -- with the head/torso/leg zone multipliers; fire-rate/ammo/origin are validated
    /// here so an abusive client is rejected, never trusted. Melee is a server-side deferred-hit timer;
    /// grenades are server-spawned entities that snap while flying and explode by event. Death/respawn is
    /// server-owned (PlayerCombatReplication). Runs from NetWorldServer.TickSimulation AFTER the player sim,
    /// BEFORE replication (§2.5 order).
    /// </summary>
    public sealed class ServerCombat
    {
        // the Phase-1 NetServer.Hitscan zone cylinder, generalized to a 3D segment test
        const float PlayerZoneRadius = 0.42f;
        const float PlayerZoneTopY = 1.8f;
        const float PlayerHeadMinY = 1.45f;
        const float PlayerTorsoMinY = 0.78f;
        const float ZombieZoneRadius = 0.4f;      // ZombieController capsule radius
        const float ZombieHeadFrac = 0.82f;       // ZombieController.IsHeadshot: top ~18% of the collider
        public const int RespawnDelayTicks = 175; // 3.5 s -- the SP death-cam timer

        readonly PlayerReplication _players;
        readonly PlayerCombatReplication _state;
        readonly ZombieReplication _zombies;
        readonly ProjectileReplication _projectiles;
        readonly NetIdMinter _ids;
        readonly Action<byte[]> _broadcast;
        readonly Action<ushort, byte[]> _sendTo;

        public IZombieHost ZombieHost;
        public CombatWorldRay WorldRay;                       // optional world-geometry occlusion + bullet stops
        public Func<float, float, float> GroundHeight;        // (x,z) -> ground y for grenade bounces; null = y 0

        public ServerGunProfile DefaultGun = new ServerGunProfile();
        public ServerMeleeProfile DefaultMelee = new ServerMeleeProfile();
        public ServerGrenadeProfile DefaultGrenade = new ServerGrenadeProfile();
        readonly Dictionary<ushort, ServerGunProfile> _gunByPlayer = new Dictionary<ushort, ServerGunProfile>();

        public ServerCombatDiagnostics Diag { get; } = new ServerCombatDiagnostics();

        sealed class Bullet
        {
            public Vector3 Pos, Vel;
            public int StepsLeft;
            public float Gravity;
            public ushort Shooter;
            public ushort Seq;
            public ServerGunProfile Gun;
        }
        readonly List<Bullet> _bullets = new List<Bullet>();

        sealed class PendingMelee
        {
            public ushort Attacker;
            public ushort Seq;
            public long LandTick;
            public float YawDegrees;
            public bool Strong;
        }
        readonly List<PendingMelee> _pendingMelee = new List<PendingMelee>();

        sealed class GrenadeEntity
        {
            public uint NetIdValue;
            public ushort Owner;
            public Vector3 Pos, Vel;
            public long ExplodeTick;
        }
        readonly List<GrenadeEntity> _grenades = new List<GrenadeEntity>();

        public int LiveBullets => _bullets.Count;
        public int LiveGrenades => _grenades.Count;

        public ServerCombat(PlayerReplication players, PlayerCombatReplication state, ZombieReplication zombies,
                            ProjectileReplication projectiles, NetIdMinter ids,
                            Action<byte[]> broadcastEvent, Action<ushort, byte[]> sendEventTo)
        {
            _players = players;
            _state = state;
            _zombies = zombies;
            _projectiles = projectiles;
            _ids = ids;
            _broadcast = broadcastEvent;
            _sendTo = sendEventTo;
        }

        /// <summary>Host override of a player's gun profile (until Phase 6 replicates the held item).</summary>
        public void SetGunProfile(ushort playerId, ServerGunProfile profile) => _gunByPlayer[playerId] = profile;
        public ServerGunProfile GunFor(ushort playerId) => _gunByPlayer.TryGetValue(playerId, out var p) ? p : DefaultGun;

        public int AmmoOf(ushort playerId) => _state.TryGet(playerId, out var e) ? e.Ammo : -1;

        // ------------------------------------------------------------------ commands (dispatch choke point)

        public void OnFire(ushort sender, in FireCommand cmd, long tick)
        {
            if (!_state.TryGet(sender, out var cs) || !cs.Alive || !_players.TryGetByOwner(sender, out var pe))
            { Diag.ShotsRejectedDeadOrMissing++; return; }
            var gun = GunFor(sender);
            if (cs.ReloadDoneTick > tick) { Diag.ShotsRejectedReloading++; return; }
            if (tick - cs.LastFireTick <= gun.FirerateTicks) { Diag.ShotsRejectedRate++; return; }   // min gap = Firerate+1 ticks (SP rule)
            if (cs.Ammo <= 0) { Diag.ShotsRejectedAmmo++; return; }
            if ((cmd.Origin - pe.Pos).magnitude > gun.MaxAimOriginOffset) { Diag.ShotsRejectedRange++; return; }
            var dir = cmd.Dir;
            float m = dir.magnitude;
            if (m < 0.5f || float.IsNaN(m)) { Diag.ShotsRejectedMalformed++; return; }
            dir /= m;

            cs.LastFireTick = tick;
            cs.Ammo--;
            for (int i = 0; i < Math.Max(1, gun.Pellets); i++)
                _bullets.Add(new Bullet
                {
                    Pos = cmd.Origin,
                    Vel = dir * gun.MuzzleVelocity,
                    StepsLeft = Math.Max(1, gun.BallisticSteps),
                    Gravity = -9.81f * gun.GravityMultiplier,
                    Shooter = sender,
                    Seq = cmd.Seq,
                    Gun = gun,
                });
            Diag.ShotsAccepted++;
        }

        public void OnReload(ushort sender, in ReloadCommand cmd, long tick)
        {
            if (!_state.TryGet(sender, out var cs) || !cs.Alive) return;
            var gun = GunFor(sender);
            if (cs.Ammo >= gun.MagCapacity || cs.ReloadDoneTick > tick) return;
            cs.ReloadDoneTick = tick + gun.ReloadTicks;
        }

        public void OnMelee(ushort sender, in MeleeCommand cmd, long tick)
        {
            if (!_state.TryGet(sender, out var cs) || !cs.Alive) { Diag.MeleeRejected++; return; }
            if (tick < cs.MeleeReadyTick) { Diag.MeleeRejected++; return; }
            cs.MeleeReadyTick = tick + DefaultMelee.CooldownTicks;
            _pendingMelee.Add(new PendingMelee { Attacker = sender, Seq = cmd.Seq, LandTick = tick + DefaultMelee.HitDelayTicks, YawDegrees = cmd.YawDegrees, Strong = cmd.Strong });
            Diag.MeleeAccepted++;
        }

        public void OnGrenade(ushort sender, in GrenadeCommand cmd, long tick)
        {
            if (!_state.TryGet(sender, out var cs) || !cs.Alive || !_players.TryGetByOwner(sender, out var pe))
            { Diag.GrenadesRejected++; return; }
            if (tick < cs.GrenadeReadyTick) { Diag.GrenadesRejected++; return; }
            if ((cmd.Origin - pe.Pos).magnitude > DefaultGun.MaxAimOriginOffset) { Diag.GrenadesRejected++; return; }
            if (cmd.Velocity.magnitude > DefaultGrenade.MaxThrowSpeed) { Diag.GrenadesRejected++; return; }
            cs.GrenadeReadyTick = tick + DefaultGrenade.CooldownTicks;
            var id = _ids.Mint();
            _grenades.Add(new GrenadeEntity { NetIdValue = id.Value, Owner = sender, Pos = cmd.Origin, Vel = cmd.Velocity, ExplodeTick = tick + DefaultGrenade.FuseTicks });
            _projectiles.ServerSpawn(id, ProjectileKind.Grenade, cmd.Origin, tick);
            Diag.GrenadesAccepted++;
        }

        // ------------------------------------------------------------------ the 50 Hz combat step

        public void Step(long tick)
        {
            foreach (var cs in _state.All)
            {
                if (cs.ReloadDoneTick == tick) cs.Ammo = GunFor(cs.OwnerPlayerId).MagCapacity;
                if (!cs.Alive && cs.RespawnAtTick == tick) Respawn(cs, tick);
            }
            StepBullets(tick);
            StepMelee(tick);
            StepGrenades(tick);
        }

        void Respawn(PlayerCombatReplication.CombatEntity cs, long tick)
        {
            cs.Alive = true;
            cs.HealthExact = 100f;
            cs.Health = 100;
            cs.RespawnAtTick = -1;
            _state.MarkDirty(cs, tick);
            _players.ServerTeleport(cs.OwnerPlayerId, cs.SpawnPos, tick);
            var evt = new PlayerRespawnedEvent { PlayerId = cs.OwnerPlayerId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventPlayerRespawned, evt.Write));
        }

        void StepBullets(long tick)
        {
            for (int i = _bullets.Count - 1; i >= 0; i--)
            {
                var b = _bullets[i];
                var next = BallisticsMath.NextPos(b.Pos, b.Vel);   // the exact SP step (UseableGun 0.02 s segment)

                float bestT = float.MaxValue;
                int hitKind = 0;   // 0 none, 1 player, 2 zombie, 3 world
                ushort hitPlayer = 0;
                ZombieReplication.ZombieEntity hitZombie = null;
                float hitRelY = 0f, hitTop = 0f;
                Vector3 worldPoint = default;

                foreach (var pe in _players.All)
                {
                    if (pe.OwnerPlayerId == b.Shooter) continue;
                    if (_state.TryGet(pe.OwnerPlayerId, out var vs) && !vs.Alive) continue;
                    if (SegmentHitsCylinder(b.Pos, next, pe.Pos, PlayerZoneRadius, PlayerZoneTopY, out float t, out float relY) && t < bestT)
                    { bestT = t; hitKind = 1; hitPlayer = pe.OwnerPlayerId; hitRelY = relY; hitTop = PlayerZoneTopY; }
                }
                if (ZombieHost != null)
                    foreach (var ze in _zombies.All)
                    {
                        if (ze.IsDead) continue;
                        float top = ZombieReplication.HeightFor(ze.Speciality);
                        if (SegmentHitsCylinder(b.Pos, next, ze.Pos, ZombieZoneRadius, top, out float t, out float relY) && t < bestT)
                        { bestT = t; hitKind = 2; hitZombie = ze; hitRelY = relY; hitTop = top; }
                    }
                if (WorldRay != null && WorldRay(b.Pos, next, out var wp))
                {
                    float segLen = (next - b.Pos).magnitude;
                    float t = segLen > 1e-4f ? (wp - b.Pos).magnitude / segLen : 0f;
                    if (t < bestT) { bestT = t; hitKind = 3; worldPoint = wp; }
                }

                if (hitKind != 0)
                {
                    Vector3 point = hitKind == 3 ? worldPoint : b.Pos + (next - b.Pos) * Math.Min(bestT, 1f);
                    Vector3 dir = b.Vel.normalized;
                    switch (hitKind)
                    {
                        case 1:
                        {
                            float mult = hitRelY >= PlayerHeadMinY ? b.Gun.HeadMult : (hitRelY >= PlayerTorsoMinY ? b.Gun.TorsoMult : b.Gun.LegMult);
                            float dmg = b.Gun.PlayerDamage * mult;
                            ApplyPlayerDamage(hitPlayer, dmg, b.Shooter, tick, out bool killed);
                            SendHitConfirm(b.Shooter, b.Seq, HitTargetKind.Player, hitPlayer, dmg, killed, hitRelY >= PlayerHeadMinY);
                            BroadcastImpact(point, ImpactSurface.Flesh);
                            Diag.BulletHitsPlayer++;
                            break;
                        }
                        case 2:
                        {
                            bool head = hitRelY > hitTop * ZombieHeadFrac;
                            float dmg = b.Gun.ZombieDamage;   // flat vs zombies -- the SP StepBullets model
                            bool killed = ZombieHost.DamageZombie(hitZombie.NetIdValue, dmg, point, dir, b.Shooter, head);
                            var hitEvt = new ZombieHitEvent { NetId = hitZombie.NetIdValue, Damage = dmg, Shooter = b.Shooter };
                            _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieHit, hitEvt.Write));
                            if (killed)
                            {
                                _zombies.ServerSetAnim(new NetId(hitZombie.NetIdValue), ZombieNetAnim.Dead, tick);
                                CreditKill(b.Shooter, tick);
                                var died = new ZombieDiedEvent { NetId = hitZombie.NetIdValue, Killer = b.Shooter };
                                _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieDied, died.Write));
                            }
                            SendHitConfirm(b.Shooter, b.Seq, HitTargetKind.Zombie, hitZombie.NetIdValue, dmg, killed, head);
                            BroadcastImpact(point, ImpactSurface.Flesh);
                            Diag.BulletHitsZombie++;
                            break;
                        }
                        case 3:
                            BroadcastImpact(point, ImpactSurface.World);
                            Diag.BulletHitsWorld++;
                            break;
                    }
                    _bullets.RemoveAt(i);
                    continue;
                }

                b.Pos = next;
                b.Vel = BallisticsMath.StepVel(b.Vel, b.Gravity);
                if (--b.StepsLeft <= 0) { _bullets.RemoveAt(i); Diag.BulletsExpired++; }
            }
        }

        void StepMelee(long tick)
        {
            for (int i = _pendingMelee.Count - 1; i >= 0; i--)
            {
                var pm = _pendingMelee[i];
                if (tick < pm.LandTick) continue;
                _pendingMelee.RemoveAt(i);
                if (!_state.TryGet(pm.Attacker, out var acs) || !acs.Alive) continue;   // died mid-swing
                if (!_players.TryGetByOwner(pm.Attacker, out var ape)) continue;

                // re-evaluate targets NOW against server positions (the SP deferred-hit rule: a moving target can be missed)
                float yawRad = pm.YawDegrees * (Mathf.PI / 180f);
                var fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
                var origin = ape.Pos + new Vector3(0f, 1.2f, 0f);
                float reach = DefaultMelee.Range + 0.5f;
                float mult = pm.Strong ? DefaultMelee.StrongMult : 1f;

                float bestD = float.MaxValue;
                ushort bestPlayer = 0;
                ZombieReplication.ZombieEntity bestZombie = null;

                foreach (var pe in _players.All)
                {
                    if (pe.OwnerPlayerId == pm.Attacker) continue;
                    if (_state.TryGet(pe.OwnerPlayerId, out var vs) && !vs.Alive) continue;
                    var to = (pe.Pos + new Vector3(0f, 1f, 0f)) - origin;
                    float d = to.magnitude;
                    if (d < reach && d > 1e-4f && Vector3.Dot(to / d, fwd) > 0.3f && d < bestD) { bestD = d; bestPlayer = pe.OwnerPlayerId; bestZombie = null; }
                }
                if (ZombieHost != null)
                    foreach (var ze in _zombies.All)
                    {
                        if (ze.IsDead) continue;
                        var to = (ze.Pos + new Vector3(0f, 1f, 0f)) - origin;
                        float d = to.magnitude;
                        if (d < reach && d > 1e-4f && Vector3.Dot(to / d, fwd) > 0.3f && d < bestD) { bestD = d; bestZombie = ze; bestPlayer = 0; }
                    }

                if (bestZombie != null)
                {
                    float dmg = DefaultMelee.ZombieDamage * mult;
                    bool killed = ZombieHost.DamageZombie(bestZombie.NetIdValue, dmg, bestZombie.Pos + new Vector3(0f, 1f, 0f), fwd, pm.Attacker, false);
                    var hitEvt = new ZombieHitEvent { NetId = bestZombie.NetIdValue, Damage = dmg, Shooter = pm.Attacker };
                    _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieHit, hitEvt.Write));
                    if (killed)
                    {
                        _zombies.ServerSetAnim(new NetId(bestZombie.NetIdValue), ZombieNetAnim.Dead, tick);
                        CreditKill(pm.Attacker, tick);
                        var died = new ZombieDiedEvent { NetId = bestZombie.NetIdValue, Killer = pm.Attacker };
                        _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieDied, died.Write));
                    }
                    SendHitConfirm(pm.Attacker, pm.Seq, HitTargetKind.Zombie, bestZombie.NetIdValue, dmg, killed, false);
                }
                else if (bestPlayer != 0)
                {
                    float dmg = DefaultMelee.PlayerDamage * mult;
                    ApplyPlayerDamage(bestPlayer, dmg, pm.Attacker, tick, out bool killed);
                    SendHitConfirm(pm.Attacker, pm.Seq, HitTargetKind.Player, bestPlayer, dmg, killed, false);
                }
            }
        }

        void StepGrenades(long tick)
        {
            for (int i = _grenades.Count - 1; i >= 0; i--)
            {
                var g = _grenades[i];
                if (tick >= g.ExplodeTick)
                {
                    Explode(g, tick);
                    _projectiles.ServerRemove(new NetId(g.NetIdValue), tick);
                    _grenades.RemoveAt(i);
                    continue;
                }
                // the SP Grenade step: real 1x gravity, ground bounce with the same damping
                g.Vel = new Vector3(g.Vel.x, g.Vel.y - 9.81f * 0.02f, g.Vel.z);
                var next = g.Pos + g.Vel * 0.02f;
                float groundY = GroundHeight?.Invoke(next.x, next.z) ?? 0f;
                if (next.y < groundY + 0.11f)
                {
                    next.y = groundY + 0.11f;
                    g.Vel = new Vector3(g.Vel.x * 0.4f, Math.Abs(g.Vel.y) * 0.3f, g.Vel.z * 0.4f);
                }
                g.Pos = next;
                _projectiles.ServerPublish(new NetId(g.NetIdValue), next, tick);
            }
        }

        void Explode(GrenadeEntity g, long tick)
        {
            var prof = DefaultGrenade;
            if (ZombieHost != null)
                foreach (var ze in _zombies.All)
                {
                    if (ze.IsDead) continue;
                    float range = (ze.Pos - g.Pos).magnitude;
                    if (range > prof.Radius || Blocked(g.Pos, ze.Pos)) continue;
                    float dmg = ExplosionMath.Linear(prof.ZombieDamage, range, prof.Radius);   // zombies: LINEAR falloff (Zombie.cs:270)
                    bool killed = ZombieHost.DamageZombie(ze.NetIdValue, dmg, ze.Pos, (ze.Pos - g.Pos).normalized, g.Owner, false);
                    if (killed)
                    {
                        _zombies.ServerSetAnim(new NetId(ze.NetIdValue), ZombieNetAnim.Dead, tick);
                        CreditKill(g.Owner, tick);
                        var died = new ZombieDiedEvent { NetId = ze.NetIdValue, Killer = g.Owner };
                        _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieDied, died.Write));
                    }
                }
            foreach (var pe in _players.All)
            {
                if (_state.TryGet(pe.OwnerPlayerId, out var vs) && !vs.Alive) continue;
                float pr = (pe.Pos - g.Pos).magnitude;
                if (pr > prof.Radius || Blocked(g.Pos, pe.Pos)) continue;
                float dmg = ExplosionMath.Squared(prof.PlayerDamage, pr, prof.Radius);   // players: SQUARED falloff (Player.cs:1975); thrower included
                if (dmg > 0f) ApplyPlayerDamage(pe.OwnerPlayerId, dmg, g.Owner, tick, out _);
            }
            var evt = new GrenadeExplodedEvent { Pos = g.Pos, Radius = prof.Radius };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventGrenadeExploded, evt.Write));
        }

        bool Blocked(Vector3 a, Vector3 b)
        {
            if (WorldRay == null) return false;
            var up = new Vector3(0f, 0.8f, 0f);   // chest height, like the SP ExplosionBlocked LoS ray
            return WorldRay(a + up, b + up, out _);
        }

        void ApplyPlayerDamage(ushort victim, float damage, ushort attacker, long tick, out bool killed)
        {
            killed = false;
            if (!_state.TryGet(victim, out var cs) || !cs.Alive) return;
            cs.HealthExact -= damage;
            cs.Health = (byte)Math.Clamp((int)Math.Ceiling(cs.HealthExact), 0, 100);
            _state.MarkDirty(cs, tick);
            if (cs.HealthExact > 0f) return;

            killed = true;
            cs.Alive = false;
            cs.Health = 0;
            cs.Deaths++;
            cs.RespawnAtTick = tick + RespawnDelayTicks;
            _players.ServerClearInput(victim);   // a corpse stops consuming its held-keys input
            if (attacker != 0 && attacker != victim) CreditKill(attacker, tick);
            var evt = new PlayerDiedEvent { Victim = victim, Killer = attacker == victim ? (ushort)0 : attacker };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventPlayerDied, evt.Write));
        }

        /// <summary>Phase 6 (§3.2) XP-award seam: fires on every credited kill -- zombie AND player, since
        /// bullet/melee/grenade/PvP all funnel through CreditKill. The host decides the award; unset = no
        /// coupling.</summary>
        public Action<ushort> KillCredited;

        void CreditKill(ushort playerId, long tick)
        {
            if (!_state.TryGet(playerId, out var cs)) return;
            cs.Kills++;
            _state.MarkDirty(cs, tick);
            KillCredited?.Invoke(playerId);
        }

        void SendHitConfirm(ushort shooter, ushort seq, HitTargetKind kind, uint targetId, float damage, bool killed, bool headshot)
        {
            var evt = new HitConfirmEvent { Seq = seq, TargetKind = (byte)kind, TargetId = targetId, Damage = damage, Killed = killed, Headshot = headshot };
            _sendTo(shooter, NetMessagePak.Pack(ReplicationIds.EventHitConfirm, evt.Write));
        }

        void BroadcastImpact(Vector3 point, ImpactSurface surface)
        {
            var evt = new ImpactFxEvent { Pos = point, Surface = (byte)surface };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventImpactFx, evt.Write));
        }

        /// <summary>Segment-vs-vertical-cylinder (the Phase-1 Hitscan closest-XZ-approach test, generalized
        /// to a 3D segment): true if the bullet's tick segment passes within `radius` of the target's
        /// vertical axis inside the height band [-0.1, top+0.15]. relY = hit height above the feet -- the
        /// zone the multipliers key on.</summary>
        internal static bool SegmentHitsCylinder(Vector3 p0, Vector3 p1, Vector3 feet, float radius, float top, out float t, out float relY)
        {
            float dx = p1.x - p0.x, dz = p1.z - p0.z;
            float axz = dx * dx + dz * dz;
            float ex = p0.x - feet.x, ez = p0.z - feet.z;
            if (axz < 1e-8f)
            {
                // (near-)vertical segment: XZ offset is constant
                t = 0f;
                relY = p0.y - feet.y;
                return ex * ex + ez * ez <= radius * radius && relY >= -0.1f && relY <= top + 0.15f;
            }
            t = -(ex * dx + ez * dz) / axz;      // closest XZ approach to the body axis
            t = Math.Clamp(t, 0f, 1f);           // starting inside the cylinder still hits (point-blank)
            float hx = ex + dx * t, hz = ez + dz * t;
            relY = (p0.y + (p1.y - p0.y) * t) - feet.y;
            return hx * hx + hz * hz <= radius * radius && relY >= -0.1f && relY <= top + 0.15f;
        }
    }
}
