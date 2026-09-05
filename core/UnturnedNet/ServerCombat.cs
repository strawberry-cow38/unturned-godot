using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Static-geometry raycast the host world supplies (the dedicated/loopback Godot world wires
    /// its DirectSpaceState here so server bullets stop at buildings). Null = open field (the L0 default).</summary>
    // destructibleIndex: if the world hit is a destructible prop's collider, its deterministic placement
    // index (the DestructibleReplication wire id); -1 for plain terrain/buildings. Lets a server bullet route
    // damage into ServerDestructibles at the same hit that stops it.
    public delegate bool CombatWorldRay(Vector3 from, Vector3 to, out Vector3 hitPoint, out int destructibleIndex);

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
        public float ObjectDamage = 25f;        // Eaglefire Object_Damage -- vs destructible props (rubble)
        // .dat Vehicle_Damage. Barricades (doors/beds) take THIS, not Object_Damage, because that is what the
        // singleplayer bullet path uses on them -- a door that takes 25 per shot in MP and 35 in SP is the
        // same door with two different break times. Default 35 = Eaglefire, matching GunDef's parse.
        public float VehicleDamage = 35f;
        public float HeadMult = 2.0f;           // the NetServer.Hitscan zone table (head/torso/leg). 2.0 per strawberry 2026-08-15 (was 3.0); MIRRORS Humanoid.HeadMult -- gun.zone_table_mirror asserts they agree
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
        /// <summary>The asset this profile IS, e.g. "eaglefire" -- rides PlayerFiredEvent so other clients can
        /// pick the right report and tracer. Lowercase [a-z0-9_] only; the receiving client refuses anything
        /// else rather than opening it, because it lands in a res:// path.</summary>
        public string AssetName = "eaglefire";
    }

    public sealed class ServerMeleeProfile
    {
        public float PlayerDamage = 40f;
        public float ZombieDamage = 50f;        // Military Knife Zombie_Damage
        public float ObjectDamage = 40f;        // vs destructible props (rubble) -- an axe/knife breaks small props in a few swings
        public float VehicleDamage = 10f;       // barricades take this, matching the SP melee path's `_melee?.VehicleDamage ?? 10f`
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
        // DERIVED, not duplicated. This used to be a literal 125 (2.5 s) alongside SDG.Unturned.Throwables'
        // own fuse, and the moment strawberry's 3 s landed the two disagreed: OnGrenade armed at 150 ticks while
        // ServerCombatTests still stepped DefaultGrenade.FuseTicks + 15 = 140 and waited for a blast that had not
        // happened yet. One number, one place.
        public int FuseTicks = Throwables.FuseTicks;
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

        /// <summary>Stance-adaptive player hit geometry (master: match the model + adapt to crouch/crawl). The server is
        /// headless, so these approximate the POSED rig per stance -- radius + total height + the head/torso relY splits,
        /// all above the feet. Shared with the client hitbox viz so what the shooter sees is what the server tests.
        /// stance: 0/1 STAND/SPRINT, 2 CROUCH, 3 PRONE.</summary>
        public static void PlayerHitZones(byte stance, out float radius, out float top, out float headMin, out float torsoMin)
        {
            switch (stance)
            {
                case 2: radius = 0.34f; top = 1.30f; headMin = 1.00f; torsoMin = 0.46f; break;   // CROUCH: skull bone 0.91, head cube ~1.0-1.30
                case 3: radius = 0.32f; top = 0.50f; headMin = 0.32f; torsoMin = 0.16f; break;    // PRONE: low (the along-body head/torso/leg split is horizontal -- viz uses the Z ranges)
                default: radius = 0.28f; top = 1.82f; headMin = 1.44f; torsoMin = 0.68f; break;   // STAND: skull bone 1.32 but the head CUBE sits ~1.44-1.82; body ~0.56 wide x 0.4 deep
            }
        }
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
        // destructible props (rubble): (index, amount, tick) -> destroyed? Set by the host to ServerDestructibles.DamageObject.
        // Null on the L0 default (no world = nothing destructible to hit).
        public Func<int, float, long, bool> DamageObject;

        /// <summary>SP/MP unify (doors + beds): (from, to, amount, tick) -> did it hit one? Barricades are
        /// NOT in the destructible index space -- they are NetId-keyed nodes -- so they need their own hit
        /// resolution rather than riding DamageObject. Without this the server had no door-damage path at
        /// all: a client's melee early-returns into NetMelee and its bullets are cosmetic, so a door in MP
        /// was simply indestructible, and the raiding loop did not exist. Null leaves that old behaviour
        /// (harnesses with no world nodes), which is why the L1 test asserts on a real broken door.</summary>
        public Func<Vector3, Vector3, float, long, bool> DamageBarricadeAlong;
        public Func<float, float, float> GroundHeight;        // (x,z) -> ground y for grenade bounces; null = y 0

        /// <summary>P3a (SP/MP-unify) respawn-reposition seam: a client-authoritative owner's entity is
        /// overwritten by its next PlayerStateCommand (ServerPlayerAuthority adopts through ServerDrive), so a
        /// bare ServerTeleport to SpawnPos is silently clobbered the very next tick. NetWorldServer wires this
        /// to ServerPlayerAuthority.RepositionOwner, which rides the recov/freeze-until-echo primitive: publish
        /// the entity at SpawnPos, open the recov freeze (discard the owner's claims until it echoes the bumped
        /// counter), and unicast a PlayerRecovEvent so the client teleports its shell there. Returns true iff it
        /// handled the reposition (owner has a client-auth stream); false -> Respawn falls back to ServerTeleport
        /// (a bystander avatar / the loopback node re-asserts its own transform anyway). Null on a bare
        /// NetWorldServer keeps every pre-P3a combat harness byte-identical (ServerTeleport path).</summary>
        public Func<ushort, Vector3, long, bool> RepositionOwner;

        /// <summary>SP/MP unify (beds): where this player wants to come back. Returns null for "no claimed
        /// bed" -- the map spawn (cs.SpawnPos) still wins, so a host that never wires this respawns exactly
        /// where it always did. NetWorldServer points it at ServerInteractables, which answers from the same
        /// BedClaims singleplayer respawns through, so a bed means the same thing in both modes.</summary>
        public Func<ushort, Vector3?> RespawnPositionOf;

        /// <summary>D1 posture (PEI_COMBAT_PLAN §3): while false, players are not combat targets at all --
        /// bullets fly through them, melee ignores them, blasts spare them (self-damage included, since a
        /// D1 shell has no server-auth vitals and an invisible entity death would just rubber-band it).
        /// Zombie damage is untouched. Default TRUE keeps every existing path byte-identical; the D1
        /// dedicated server sets it false until D2 lands server-auth player vitals.</summary>
        public bool PvPEnabled = true;

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
            public ushort ItemId;          // v27: which throwable, so Explode can look up what it actually does
            public ThrowableDef Def;       // resolved ONCE at accept time -- never re-derived from client input later
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

        readonly List<(ushort victim, float damage, ushort attacker)> _externalDamageQueue = new List<(ushort, float, ushort)>();

        /// <summary>P3b (SP/MP-unify): the PUBLIC non-weapon player-damage entry -- the wrapper over the private
        /// ApplyPlayerDamage sink for the damage sources P3a left stranded on the invulnerable NetAvatar/adopted
        /// body: zombie melee + acid, vehicle/deployable blast, server-derived fall, and out-of-bounds. All
        /// funnel through the SAME ApplyPlayerDamage the bullet/melee/grenade paths use, so HP stays fully
        /// server-authored. Like the P3a debug seam it QUEUES to land at the next Step with the LIVE tick (never
        /// out-of-tick: applying it at a stale/already-acked tick would mark the CombatState dirty at a tick the
        /// sync-check already reflects -- a phantom desync); it also lets a game-side actor (a zombie brain, an
        /// exploding deployable) enqueue from its own Godot _PhysicsProcess and have the hit land cleanly inside
        /// the server tick. attacker 0 = environment (no kill credit, Killer 0). NOT PvP-gated: this is PvE/
        /// environmental damage, which lands even on a PvP-off server -- the PvP toggle only governs player-vs-
        /// player target SELECTION (bullets/melee/blast), never the HP sink itself.</summary>
        public void DamagePlayerExternal(ushort victimPlayerId, float damage, ushort attackerPlayerId = 0)
            => _externalDamageQueue.Add((victimPlayerId, damage, attackerPlayerId));

        /// <summary>Test seam (P3a): queue a unit of server-authoritative player damage to land at the NEXT
        /// combat Step, so it runs INSIDE the server tick with the LIVE tick -- exactly like the real bullet/
        /// grenade/melee path funnels through ApplyPlayerDamage. Now a thin alias over DamagePlayerExternal
        /// (identical semantics: environment damage landing at the live tick inside Combat.Step). Lets an L1
        /// owner-adoption/death-render test apply a deterministic amount without staging bullet geometry on a
        /// moving owner.</summary>
        public void QueueDebugPlayerDamage(ushort victim, float damage, ushort attacker) => DamagePlayerExternal(victim, damage, attacker);

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
            // EVERY OTHER CLIENT HEARS AND SEES THIS. Broadcast after the shot is ACCEPTED, so a rejected
            // trigger pull (reloading, dry, rate-limited, out of range) cannot make a phantom crack across the
            // map. Once per trigger pull, not once per pellet -- a shotgun is one report and one muzzle flash.
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventPlayerFired,
                new PlayerFiredEvent { PlayerId = sender, Origin = cmd.Origin, Dir = dir, Gun = gun.AssetName }.Write));
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
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventPlayerMelee, new PlayerMeleeEvent { PlayerId = sender, Strong = cmd.Strong }.Write));   // v25: puppets swing
            Diag.MeleeAccepted++;
        }

        public void OnGrenade(ushort sender, in GrenadeCommand cmd, long tick)
        {
            if (!_state.TryGet(sender, out var cs) || !cs.Alive || !_players.TryGetByOwner(sender, out var pe))
            { Diag.GrenadesRejected++; return; }
            if (tick < cs.GrenadeReadyTick) { Diag.GrenadesRejected++; return; }
            if ((cmd.Origin - pe.Pos).magnitude > DefaultGun.MaxAimOriginOffset) { Diag.GrenadesRejected++; return; }
            if (cmd.Velocity.magnitude > DefaultGrenade.MaxThrowSpeed) { Diag.GrenadesRejected++; return; }
            // WHICH throwable, resolved against the shared table -- not taken as the client says. An id of 0 is
            // the pre-v27 shape and means the frag; any other id that is not a known throwable is REFUSED rather
            // than defaulted, because defaulting an unknown id to the frag would let a client throw a 175-damage
            // blast by naming a bandage.
            ThrowableDef def = cmd.ItemId == 0 ? Throwables.Find(254) : Throwables.Find(cmd.ItemId);
            if (def == null) { Diag.GrenadesRejected++; return; }
            cs.GrenadeReadyTick = tick + DefaultGrenade.CooldownTicks;
            var id = _ids.Mint();
            // A FLARE has no fuse to run out: it is lit before it leaves the hand and the projectile IS the
            // burning flare, so the server flies it for the whole burn and retires it at the end. That is what
            // lets a joined client light it the moment the entity appears (ProjectileReplicaView reads the kind
            // byte) instead of three seconds after it lands. Explosives and smoke end on the fuse.
            long endTick = tick + Throwables.FuseTicks
                         + (def.Kind == EThrowableKind.Flare ? (long)(def.EffectSeconds * 50f) : 0L);
            _grenades.Add(new GrenadeEntity { NetIdValue = id.Value, Owner = sender, Pos = cmd.Origin, Vel = cmd.Velocity,
                                              ExplodeTick = endTick, ItemId = def.Id, Def = def });
            _projectiles.ServerSpawn(id, KindOf(def), cmd.Origin, tick);
            Diag.GrenadesAccepted++;
        }

        static ProjectileKind KindOf(ThrowableDef d) => d.Kind switch
        {
            EThrowableKind.Smoke => ProjectileKind.Smoke,
            EThrowableKind.Flare => ProjectileKind.Flare,
            _ => ProjectileKind.Grenade,
        };

        // ------------------------------------------------------------------ the 50 Hz combat step

        public void Step(long tick)
        {
            if (_externalDamageQueue.Count > 0)   // P3b: drain non-weapon damage (fall/OOB/zombie/blast + the P3a debug seam) at the live tick (see DamagePlayerExternal)
            {
                for (int i = 0; i < _externalDamageQueue.Count; i++)   // index loop tolerates an enqueue during ApplyPlayerDamage's death broadcast
                {
                    var d = _externalDamageQueue[i];
                    ApplyPlayerDamage(d.victim, d.damage, d.attacker, tick, out _);
                }
                _externalDamageQueue.Clear();
            }
            foreach (var cs in _state.All)
            {
                if (cs.ReloadDoneTick == tick) cs.Ammo = GunFor(cs.OwnerPlayerId).MagCapacity;
                if (!cs.Alive && cs.RespawnAtTick == tick) Respawn(cs, tick);
            }
            StepBullets(tick);
            StepMelee(tick);
            StepGrenades(tick);
        }

        /// <summary>Respawn a dead player NOW, on request, instead of when the death timer expires -- the death
        /// screen's Respawn button. Arms the existing clock for the next tick rather than reviving inline: the
        /// tick loop tests `RespawnAtTick == tick` exactly, so a past tick would never fire and an inline revive
        /// would skip the dirty-marking and the broadcast that the normal path does. Returns false if they are
        /// not dead, which makes a double-click a no-op.</summary>
        public bool ServerRequestRespawn(ushort playerId, long tick)
        {
            foreach (var cs in _state.All)
            {
                if (cs.OwnerPlayerId != playerId) continue;
                if (cs.Alive) return false;
                cs.RespawnAtTick = tick + 1;
                return true;
            }
            return false;
        }

        void Respawn(PlayerCombatReplication.CombatEntity cs, long tick)
        {
            cs.Alive = true;
            cs.HealthExact = 100f;
            cs.Health = 100;
            cs.RespawnAtTick = -1;
            _state.MarkDirty(cs, tick);
            // A claimed bed beats the map spawn -- asked here rather than cached at death, so a bed claimed
            // (or destroyed) during the death timer is honoured, which is what happens in singleplayer too.
            Vector3 where = RespawnPositionOf?.Invoke(cs.OwnerPlayerId) ?? cs.SpawnPos;
            // P3a: for a client-authoritative owner, ServerTeleport alone is clobbered by the shell's next
            // PlayerStateCommand -- ride the recov/freeze-until-echo primitive so the reposition holds. The
            // seam falls back to a plain ServerTeleport when the owner isn't client-driven (bystander/loopback).
            if (RepositionOwner == null || !RepositionOwner(cs.OwnerPlayerId, where, tick))
                _players.ServerTeleport(cs.OwnerPlayerId, where, tick);
            PlayerRespawned?.Invoke(cs.OwnerPlayerId, tick);   // a new life gets the spawn outfit back (the death drop took the old one)
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
                float hitRelY = 0f, hitTop = 0f, hitHeadMin = PlayerHeadMinY, hitTorsoMin = PlayerTorsoMinY;   // per-hit zone splits (stance-adaptive)
                Vector3 worldPoint = default;
                int hitDestructible = -1;

                if (PvPEnabled)
                    foreach (var pe in _players.All)
                    {
                        if (pe.OwnerPlayerId == b.Shooter) continue;
                        if (_state.TryGet(pe.OwnerPlayerId, out var vs) && !vs.Alive) continue;
                        PlayerHitZones(pe.Stance, out float pr, out float ptop, out float phm, out float ptm);   // stance-adaptive: crouch/prone shrink the cylinder + lower the splits
                        if (SegmentHitsCylinder(b.Pos, next, pe.Pos, pr, ptop, out float t, out float relY) && t < bestT)
                        { bestT = t; hitKind = 1; hitPlayer = pe.OwnerPlayerId; hitRelY = relY; hitTop = ptop; hitHeadMin = phm; hitTorsoMin = ptm; }
                    }
                if (ZombieHost != null)
                    foreach (var ze in _zombies.All)
                    {
                        if (ze.IsDead) continue;
                        float top = ZombieReplication.HeightFor(ze.Speciality);
                        if (SegmentHitsCylinder(b.Pos, next, ze.Pos, ZombieZoneRadius, top, out float t, out float relY) && t < bestT)
                        { bestT = t; hitKind = 2; hitZombie = ze; hitRelY = relY; hitTop = top; }
                    }
                if (WorldRay != null && WorldRay(b.Pos, next, out var wp, out int wDest))
                {
                    float segLen = (next - b.Pos).magnitude;
                    float t = segLen > 1e-4f ? (wp - b.Pos).magnitude / segLen : 0f;
                    if (t < bestT) { bestT = t; hitKind = 3; worldPoint = wp; hitDestructible = wDest; }
                }

                if (hitKind != 0)
                {
                    Vector3 point = hitKind == 3 ? worldPoint : b.Pos + (next - b.Pos) * Math.Min(bestT, 1f);
                    Vector3 dir = b.Vel.normalized;
                    switch (hitKind)
                    {
                        case 1:
                        {
                            float mult = hitRelY >= hitHeadMin ? b.Gun.HeadMult : (hitRelY >= hitTorsoMin ? b.Gun.TorsoMult : b.Gun.LegMult);
                            float dmg = b.Gun.PlayerDamage * mult;
                            // b.Pos, not the impact point: the indicator has to say which way to turn and face
                            // the shooter, not mark where the bullet happened to end its flight.
                            ApplyPlayerDamage(hitPlayer, dmg, b.Shooter, tick, out bool killed, sourcePos: b.Pos);
                            SendHitConfirm(b.Shooter, b.Seq, HitTargetKind.Player, hitPlayer, dmg, killed, hitRelY >= hitHeadMin);
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
                            // a destructible prop's collider: whittle its health; the break fx rides the
                            // replicated ObjectDestroyed event (not this local impact), so no double-fx.
                            if (hitDestructible >= 0) DamageObject?.Invoke(hitDestructible, b.Gun.ObjectDamage, tick);
                            // ...or a barricade (door/bed), which is not in that index space. Same segment,
                            // so a shot that stopped at a door is the shot that damages it.
                            else DamageBarricadeAlong?.Invoke(b.Pos, next, b.Gun.VehicleDamage, tick);
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

                // re-evaluate targets NOW against server positions (the SP deferred-hit rule: a moving target can be missed).
                // pm.YawDegrees is the shell's RotationDegrees.Y verbatim (PlayerController.cs NetMelee(strong, RotationDegrees.Y)),
                // so forward is the GODOT convention -- (-sin,0,-cos), a body at yaw 0 faces -Z -- the SAME frame the pickup
                // cone (ServerTransactions.SenderFacingItem) and SP melee (-cam.Basis.Z) use, and against which the entity
                // positions below are measured. The old (+sin,+cos) was 180-degrees inverted: the swing hit BEHIND the attacker.
                float yawRad = pm.YawDegrees * (Mathf.PI / 180f);
                var fwd = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
                var origin = ape.Pos + new Vector3(0f, 1.2f, 0f);
                float reach = DefaultMelee.Range + 0.5f;
                float mult = pm.Strong ? DefaultMelee.StrongMult : 1f;

                float bestD = float.MaxValue;
                ushort bestPlayer = 0;
                ZombieReplication.ZombieEntity bestZombie = null;

                if (PvPEnabled)
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
                    ApplyPlayerDamage(bestPlayer, dmg, pm.Attacker, tick, out bool killed, sourcePos: ape.Pos);
                    SendHitConfirm(pm.Attacker, pm.Seq, HitTargetKind.Player, bestPlayer, dmg, killed, false);
                }
                else if (WorldRay != null)
                {
                    // no fighter in the cone -> a short forward ray can still land on a destructible prop's
                    // collider (fence/sign/billboard). The break fx rides the replicated ObjectDestroyed event.
                    var to = origin + fwd * reach;
                    bool hitWorld = WorldRay(origin, to, out _, out int mDest);
                    if (hitWorld && mDest >= 0) DamageObject?.Invoke(mDest, DefaultMelee.ObjectDamage * mult, tick);
                    // ...or a barricade in the same swing. Checked even when the ray found no destructible,
                    // because a door IS the world geometry the ray stopped on.
                    else if (hitWorld) DamageBarricadeAlong?.Invoke(origin, to, DefaultMelee.VehicleDamage * mult, tick);
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
            // Smoke and flares do NO damage -- their retail .dats carry no damage keys and no `Explosive` flag
            // at all. They still broadcast, because every client has to be told to build the cloud or light the
            // flare; the event is the whole of their effect.
            if (g.Def != null && !g.Def.Explosive)
            {
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventGrenadeExploded,
                                              new GrenadeExplodedEvent { Pos = g.Pos, Radius = g.Def.Radius, ItemId = g.ItemId }.Write));
                return;
            }
            var prof = DefaultGrenade;
            // Per-throwable numbers where we have them: the makeshift grenade is 150/6 and does nothing to a
            // vehicle, where the frag is 175/8/100. DefaultGrenade stays the fallback so a pre-v27 throw (ItemId
            // 0) and every existing test still resolve to exactly the numbers they always did.
            float radius = g.Def?.Radius ?? prof.Radius;
            float zombieDamage = g.Def?.ZombieDamage ?? prof.ZombieDamage;
            float playerDamage = g.Def?.PlayerDamage ?? prof.PlayerDamage;
            if (ZombieHost != null)
                foreach (var ze in _zombies.All)
                {
                    if (ze.IsDead) continue;
                    float range = (ze.Pos - g.Pos).magnitude;
                    if (range > radius || Blocked(g.Pos, ze.Pos)) continue;
                    float dmg = ExplosionMath.Linear(zombieDamage, range, radius);   // zombies: LINEAR falloff (Zombie.cs:270)
                    bool killed = ZombieHost.DamageZombie(ze.NetIdValue, dmg, ze.Pos, (ze.Pos - g.Pos).normalized, g.Owner, false);
                    if (killed)
                    {
                        _zombies.ServerSetAnim(new NetId(ze.NetIdValue), ZombieNetAnim.Dead, tick);
                        CreditKill(g.Owner, tick);
                        var died = new ZombieDiedEvent { NetId = ze.NetIdValue, Killer = g.Owner };
                        _broadcast(NetMessagePak.Pack(ReplicationIds.EventZombieDied, died.Write));
                    }
                }
            if (PvPEnabled)
                foreach (var pe in _players.All)
                {
                    if (_state.TryGet(pe.OwnerPlayerId, out var vs) && !vs.Alive) continue;
                    float pr = (pe.Pos - g.Pos).magnitude;
                    if (pr > radius || Blocked(g.Pos, pe.Pos)) continue;
                    float dmg = ExplosionMath.Squared(playerDamage, pr, radius);   // players: SQUARED falloff (Player.cs:1975); thrower included
                    if (dmg > 0f) ApplyPlayerDamage(pe.OwnerPlayerId, dmg, g.Owner, tick, out _, sourcePos: g.Pos);   // the BLAST is the source, not the thrower -- right even after they have moved away or the frag was theirs
                }
            var evt = new GrenadeExplodedEvent { Pos = g.Pos, Radius = radius, ItemId = g.ItemId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventGrenadeExploded, evt.Write));
        }

        bool Blocked(Vector3 a, Vector3 b)
        {
            if (WorldRay == null) return false;
            var up = new Vector3(0f, 0.8f, 0f);   // chest height, like the SP ExplosionBlocked LoS ray
            return WorldRay(a + up, b + up, out _, out _);
        }

        void ApplyPlayerDamage(ushort victim, float damage, ushort attacker, long tick, out bool killed)
            => ApplyPlayerDamage(victim, damage, attacker, tick, out killed, sourcePos: null);

        /// <summary>The single player-damage path (bullets, melee, grenades, and everything queued through
        /// DamagePlayerExternal). <paramref name="sourcePos"/> is optional and purely cosmetic -- it feeds only
        /// the victim's directional hurt indicator (PlayerHurtEvent) and touches no HP math, so a caller with
        /// nothing to point at (fall, OOB, starvation, a deadzone) can safely omit it rather than guess one.</summary>
        void ApplyPlayerDamage(ushort victim, float damage, ushort attacker, long tick, out bool killed, Vector3? sourcePos)
        {
            killed = false;
            if (!_state.TryGet(victim, out var cs) || !cs.Alive) return;
            cs.HealthExact -= damage;
            cs.Health = (byte)Math.Clamp((int)Math.Ceiling(cs.HealthExact), 0, 100);
            _state.MarkDirty(cs, tick);
            // Sent even on a killing blow (the death screen still shows where the last hit came from), so this
            // runs BEFORE the early-return below rather than being folded into the survive-only path.
            var hurt = new PlayerHurtEvent { Damage = damage, HasSource = sourcePos.HasValue, SourcePos = sourcePos ?? Vector3.zero };
            _sendTo(victim, NetMessagePak.Pack(ReplicationIds.EventPlayerHurt, hurt.Write));
            if (cs.HealthExact > 0f) return;

            killed = true;
            cs.Alive = false;
            cs.Health = 0;
            cs.Deaths++;
            cs.RespawnAtTick = tick + RespawnDelayTicks;
            _players.ServerClearInput(victim);   // a corpse stops consuming its held-keys input
            if (attacker != 0 && attacker != victim) CreditKill(attacker, tick);
            PlayerDied?.Invoke(victim, tick);    // the death drop lands here -- its spawn facts go out before the death fact
            var evt = new PlayerDiedEvent { Victim = victim, Killer = attacker == victim ? (ushort)0 : attacker };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventPlayerDied, evt.Write));
        }

        /// <summary>Phase 6 (§3.2) XP-award seam: fires on every credited kill -- zombie AND player, since
        /// bullet/melee/grenade/PvP all funnel through CreditKill. The host decides the award; unset = no
        /// coupling.</summary>
        public Action<ushort> KillCredited;

        /// <summary>Death seam (victim, tick): fires INSIDE the one death path (ApplyPlayerDamage -- bullets,
        /// melee, grenades, and everything queued through DamagePlayerExternal: fall, OOB, zombies, blasts,
        /// starvation, deadzones) after the entity is marked dead and BEFORE the PlayerDied broadcast, so any
        /// facts the handler emits (the death drop's WorldItemSpawned events) reach clients ahead of the
        /// death they explain. NetWorldServer wires this to ServerTransactions.DropInventoryOnDeath: until
        /// 2026-09-02 nothing was wired here at all and a dead player's bag simply came back with them
        /// (strawberry: "your items are kept after death instead of dropping on the ground"). Unset = the old
        /// keep-inventory behaviour, which is what every pre-existing L0 combat harness asserts against.</summary>
        public Action<ushort, long> PlayerDied;

        /// <summary>Respawn seam (player, tick): fires inside Respawn after the entity is revived and
        /// repositioned, BEFORE the PlayerRespawned broadcast and before this tick's inventory commit -- so
        /// a handler that re-grants the spawn outfit (DedicatedServer / MpLoopback, the same clothes the
        /// join seeding hands out) rides the same tick's owner echo as the revive. Game-side because the kit
        /// itself (PlayerController.PopulateSpawnKit) is catalog data core/ cannot see.</summary>
        public Action<ushort, long> PlayerRespawned;

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
