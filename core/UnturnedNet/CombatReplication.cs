using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // Phase 5 combat wire messages (MP_PLAN §3.4). Commands ride ReliableOrdered (transactional, §2.3);
    // the client's aim RAY (origin + dir) crosses the wire -- never a hit result, never anything viewmodel.
    // All hand-written Write/TryRead in the MoveInput pattern; ids in ReplicationIds are append-only.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Client "I pulled the trigger": the aim ray + a client-local seq echoed back in HitConfirm.
    /// The SERVER spawns and steps the bullet; damage/kill facts only ever flow server -> client.</summary>
    public struct FireCommand
    {
        public ushort Seq;
        public Vector3 Origin;   // muzzle, validated server-side against the avatar's position
        public Vector3 Dir;      // unit aim direction (re-normalized on read)

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            NetWire.WritePos(w, Origin);
            NetWire.WriteDir(w, Dir);
        }

        public static bool TryRead(NetPakReader r, out FireCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!NetWire.ReadPos(r, out Vector3 origin)) return false;
            if (!NetWire.ReadDir(r, out Vector3 dir)) return false;
            cmd = new FireCommand { Seq = seq, Origin = origin, Dir = dir };
            return true;
        }
    }

    /// <summary>Client melee swing intent. The hit itself is a server-side DEFERRED timer (§3.4 "melee =
    /// deferred-hit timer server-side"): targets re-evaluate against server positions when the swing lands.</summary>
    public struct MeleeCommand
    {
        public ushort Seq;
        public bool Strong;
        public float YawDegrees;   // facing at swing start (forward = (sin yaw, 0, cos yaw), the MoveInput convention)

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteBit(Strong);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
        }

        public static bool TryRead(NetPakReader r, out MeleeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadBit(out bool strong)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            cmd = new MeleeCommand { Seq = seq, Strong = strong, YawDegrees = yaw };
            return true;
        }
    }

    /// <summary>Client grenade throw: origin + initial velocity. The grenade itself is a SERVER-spawned
    /// short-lived entity (§3.4) -- it snaps while flying (ProjectileReplication) and explodes by Event.</summary>
    public struct GrenadeCommand
    {
        public ushort Seq;
        public Vector3 Origin;
        public Vector3 Velocity;   // clamped-float ±64 m/s per axis on the wire

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            NetWire.WritePos(w, Origin);
            NetWire.WriteVel(w, Velocity);
        }

        public static bool TryRead(NetPakReader r, out GrenadeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!NetWire.ReadPos(r, out Vector3 origin)) return false;
            if (!NetWire.ReadVel(r, out Vector3 vel)) return false;
            cmd = new GrenadeCommand { Seq = seq, Origin = origin, Velocity = vel };
            return true;
        }
    }

    /// <summary>Client reload request; the server enforces the reload duration before ammo refills.</summary>
    public struct ReloadCommand
    {
        public ushort Seq;

        public void Write(NetPakWriter w) => w.WriteUInt16(Seq);

        public static bool TryRead(NetPakReader r, out ReloadCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            cmd = new ReloadCommand { Seq = seq };
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // mp-event-coalesce (wire v10): the four combat commands no longer ride their OWN ReliableOrdered
    // datagram (a single drop head-of-line-blocks the reliable-ordered channel and stalls all subsequent
    // combat -- the "complex-packet stutter"). Instead the client folds recent combat events REDUNDANTLY
    // into the 50 Hz UNRELIABLE PlayerStateCommand transform stream and keeps re-including each event until
    // the server ACKs it; the server dedups by a strictly-increasing combat seq (idempotent redundancy,
    // exactly the MoveInputPacket / ServerQueueInput pattern). One CarriedCombatEvent is a Kind tag plus
    // the embedded command (which already carries the combat Seq -- never duplicated). REUSES the existing
    // Fire/Melee/Grenade/Reload Write/TryRead so the encoding stays byte-identical to the standalone form.
    // ---------------------------------------------------------------------------------------------------
    public enum CombatEventKind : byte { Fire = 0, Melee = 1, Grenade = 2, Reload = 3 }

    /// <summary>One combat event carried inside PlayerStateCommand. The embedded command owns the Seq; the
    /// Kind byte selects which one is live. TryRead bound-checks the Kind byte (>3 = malformed = reject).</summary>
    public struct CarriedCombatEvent
    {
        public CombatEventKind Kind;
        public FireCommand Fire;
        public MeleeCommand Melee;
        public GrenadeCommand Grenade;
        public ReloadCommand Reload;

        /// <summary>The combat seq the server dedups on -- the embedded command's own seq.</summary>
        public ushort Seq => Kind switch
        {
            CombatEventKind.Fire => Fire.Seq,
            CombatEventKind.Melee => Melee.Seq,
            CombatEventKind.Grenade => Grenade.Seq,
            CombatEventKind.Reload => Reload.Seq,
            _ => 0,
        };

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8((byte)Kind);
            switch (Kind)
            {
                case CombatEventKind.Fire: Fire.Write(w); break;
                case CombatEventKind.Melee: Melee.Write(w); break;
                case CombatEventKind.Grenade: Grenade.Write(w); break;
                case CombatEventKind.Reload: Reload.Write(w); break;
            }
        }

        public static bool TryRead(NetPakReader r, out CarriedCombatEvent ev)
        {
            ev = default;
            if (!r.ReadUInt8(out byte kind)) return false;
            if (kind > (byte)CombatEventKind.Reload) return false;   // malformed kind -> reject the whole packet
            ev.Kind = (CombatEventKind)kind;
            switch (ev.Kind)
            {
                case CombatEventKind.Fire:    if (!FireCommand.TryRead(r, out ev.Fire)) return false; break;
                case CombatEventKind.Melee:   if (!MeleeCommand.TryRead(r, out ev.Melee)) return false; break;
                case CombatEventKind.Grenade: if (!GrenadeCommand.TryRead(r, out ev.Grenade)) return false; break;
                case CombatEventKind.Reload:  if (!ReloadCommand.TryRead(r, out ev.Reload)) return false; break;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Events (server -> client, ReliableOrdered). Facts the server asserts; clients render fx locally from
    // these (§3.4 "client fx stay client-local, triggered by events -- the viewmodel never crosses the wire").
    // ---------------------------------------------------------------------------------------------------

    public enum HitTargetKind : byte { Player = 1, Zombie = 2 }

    /// <summary>To the SHOOTER only: your shot/swing (by seq) authoritatively landed. Damage waits for this
    /// (§3.4 "firing plays fx immediately, damage waits for HitConfirm").</summary>
    public struct HitConfirmEvent
    {
        public ushort Seq;
        public byte TargetKind;   // HitTargetKind
        public uint TargetId;     // player: ownerPlayerId; zombie: NetId
        public float Damage;
        public bool Killed;
        public bool Headshot;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteUInt8(TargetKind);
            w.WriteUInt32(TargetId);
            NetWire.WriteDamage(w, Damage);
            w.WriteBit(Killed);
            w.WriteBit(Headshot);
        }

        public static bool TryRead(NetPakReader r, out HitConfirmEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadUInt8(out byte kind)) return false;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!NetWire.ReadDamage(r, out float dmg)) return false;
            if (!r.ReadBit(out bool killed)) return false;
            if (!r.ReadBit(out bool head)) return false;
            evt = new HitConfirmEvent { Seq = seq, TargetKind = kind, TargetId = id, Damage = dmg, Killed = killed, Headshot = head };
            return true;
        }
    }

    public enum ImpactSurface : byte { Flesh = 0, World = 1 }

    /// <summary>Broadcast: a bullet ended somewhere -- clients spawn the impact fx (decal/dust/blood) locally.</summary>
    public struct ImpactFxEvent
    {
        public Vector3 Pos;
        public byte Surface;   // ImpactSurface

        public void Write(NetPakWriter w)
        {
            NetWire.WritePos(w, Pos);
            w.WriteUInt8(Surface);
        }

        public static bool TryRead(NetPakReader r, out ImpactFxEvent evt)
        {
            evt = default;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadUInt8(out byte surface)) return false;
            evt = new ImpactFxEvent { Pos = pos, Surface = surface };
            return true;
        }
    }

    public struct PlayerDiedEvent
    {
        public ushort Victim;
        public ushort Killer;   // 0 = environment/none

        public void Write(NetPakWriter w) { w.WriteUInt16(Victim); w.WriteUInt16(Killer); }

        public static bool TryRead(NetPakReader r, out PlayerDiedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort v)) return false;
            if (!r.ReadUInt16(out ushort k)) return false;
            evt = new PlayerDiedEvent { Victim = v, Killer = k };
            return true;
        }
    }

    public struct PlayerRespawnedEvent
    {
        public ushort PlayerId;

        public void Write(NetPakWriter w) => w.WriteUInt16(PlayerId);

        public static bool TryRead(NetPakReader r, out PlayerRespawnedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort id)) return false;
            evt = new PlayerRespawnedEvent { PlayerId = id };
            return true;
        }
    }

    public struct ZombieHitEvent
    {
        public uint NetId;
        public float Damage;
        public ushort Shooter;

        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); NetWire.WriteDamage(w, Damage); w.WriteUInt16(Shooter); }

        public static bool TryRead(NetPakReader r, out ZombieHitEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!NetWire.ReadDamage(r, out float dmg)) return false;
            if (!r.ReadUInt16(out ushort shooter)) return false;
            evt = new ZombieHitEvent { NetId = id, Damage = dmg, Shooter = shooter };
            return true;
        }
    }

    public struct ZombieDiedEvent
    {
        public uint NetId;
        public ushort Killer;   // 0 = not a networked player (e.g. the listen-server local player's direct path)

        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt16(Killer); }

        public static bool TryRead(NetPakReader r, out ZombieDiedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort k)) return false;
            evt = new ZombieDiedEvent { NetId = id, Killer = k };
            return true;
        }
    }

    /// <summary>A zombie started a swing -- an anim/audio cue for client puppets (§3.5).</summary>
    public struct AttackSwingEvent
    {
        public uint NetId;

        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);

        public static bool TryRead(NetPakReader r, out AttackSwingEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new AttackSwingEvent { NetId = id };
            return true;
        }
    }

    public struct GrenadeExplodedEvent
    {
        public Vector3 Pos;
        public float Radius;

        public void Write(NetPakWriter w) { NetWire.WritePos(w, Pos); w.WriteClampedFloat(Radius, 6, 4); }

        public static bool TryRead(NetPakReader r, out GrenadeExplodedEvent evt)
        {
            evt = default;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadClampedFloat(6, 4, out float radius)) return false;
            evt = new GrenadeExplodedEvent { Pos = pos, Radius = radius };
            return true;
        }
    }

    /// <summary>Shared field encoders for the combat messages -- positions ride the exact NetQuantization
    /// grid every snapshot system uses, so server-side comparisons against replicated state are grid-exact.</summary>
    public static class NetWire
    {
        public static void WritePos(NetPakWriter w, Vector3 p)
        {
            w.WriteClampedFloat(p.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            w.WriteClampedFloat(p.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits);
            w.WriteClampedFloat(p.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
        }

        public static bool ReadPos(NetPakReader r, out Vector3 p)
        {
            p = default;
            if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float x)) return false;
            if (!r.ReadClampedFloat(NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits, out float y)) return false;
            if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float z)) return false;
            p = new Vector3(x, y, z);
            return true;
        }

        public const int DirBits = 12;   // ±1/2047 per component ≈ 0.03° -- centimeters of deviation at max gun range

        public static void WriteDir(NetPakWriter w, Vector3 d)
        {
            w.WriteSignedNormalizedFloat(Clamp1(d.x), DirBits);
            w.WriteSignedNormalizedFloat(Clamp1(d.y), DirBits);
            w.WriteSignedNormalizedFloat(Clamp1(d.z), DirBits);
        }

        public static bool ReadDir(NetPakReader r, out Vector3 d)
        {
            d = default;
            if (!r.ReadSignedNormalizedFloat(DirBits, out float x)) return false;
            if (!r.ReadSignedNormalizedFloat(DirBits, out float y)) return false;
            if (!r.ReadSignedNormalizedFloat(DirBits, out float z)) return false;
            d = new Vector3(x, y, z);
            return true;
        }

        public static void WriteVel(NetPakWriter w, Vector3 v)
        {
            w.WriteClampedFloat(v.x, 6, 6);   // ±64 m/s, 1/64 m/s grain -- plenty for a thrown grenade
            w.WriteClampedFloat(v.y, 6, 6);
            w.WriteClampedFloat(v.z, 6, 6);
        }

        public static bool ReadVel(NetPakReader r, out Vector3 v)
        {
            v = default;
            if (!r.ReadClampedFloat(6, 6, out float x)) return false;
            if (!r.ReadClampedFloat(6, 6, out float y)) return false;
            if (!r.ReadClampedFloat(6, 6, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        public static void WriteDamage(NetPakWriter w, float damage) => w.WriteClampedFloat(damage, 9, 4);   // ±512, 1/16 grain
        public static bool ReadDamage(NetPakReader r, out float damage) => r.ReadClampedFloat(9, 4, out damage);

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }

    // ---------------------------------------------------------------------------------------------------
    // Player combat state as its own IReplicatedSystem (SystemId 2). Kept SEPARATE from PlayerReplication
    // so the Phase 3/4 player wire format is untouched (append-only discipline, §5 item 2). Everyone sees
    // alive/dead + coarse health + kills/deaths (§3.4: "other players expose only alive/dead + coarse
    // health"); detailed owner-only vitals land with the Phase 6 interest blocks.
    // ---------------------------------------------------------------------------------------------------
    public sealed class PlayerCombatReplication : IReplicatedSystem
    {
        public sealed class CombatEntity
        {
            public ushort OwnerPlayerId;
            public bool Alive = true;
            public byte Health = 100;            // coarse 0..100 (the wire byte; server keeps the exact float)
            public ushort Kills;
            public ushort Deaths;
            // ---- appearance (B10): worn clothing + held item + stance -- carried on the combat block (delta-on-
            // change, replicated for EVERY player) so a joiner's RemotePlayers puppets dress right, WITHOUT bloating
            // the 25 Hz transform. Server publishes them from each player's server-side worn inventory (PlayerAppearanceNetSync).
            public ushort WornHat, WornGlasses, WornMask, WornShirt, WornVest, WornBackpack, WornPants;
            public ushort HeldId;   // equipped item id (0 = nothing / fists)
            public byte Stance;     // EPlayerStance (stand/crouch/prone/...)
            public long LastChangedTick;

            // ---- server-only (never on the wire; replicas keep defaults) ----
            public float HealthExact = 100f;
            public int Ammo;
            public long LastFireTick = -100000;
            public long ReloadDoneTick = -1;     // > current tick = mid-reload
            public long RespawnAtTick = -1;      // dead until this tick
            public long MeleeReadyTick = -100000;
            public long GrenadeReadyTick = -100000;
            public Vector3 SpawnPos;             // where the server respawns this player
        }

        public byte SystemId => ReplicationIds.SystemPlayerCombat;

        readonly Dictionary<ushort, CombatEntity> _byOwner = new Dictionary<ushort, CombatEntity>();
        readonly Dictionary<ushort, long> _removedAtTick = new Dictionary<ushort, long>();

        public int Count => _byOwner.Count;

        public bool TryGet(ushort ownerPlayerId, out CombatEntity entity) => _byOwner.TryGetValue(ownerPlayerId, out entity);

        public IEnumerable<CombatEntity> All
        {
            get
            {
                foreach (ushort id in SortedOwners())
                    yield return _byOwner[id];
            }
        }

        // ---- server side ----

        public CombatEntity ServerAdd(ushort ownerPlayerId, Vector3 spawnPos, int magAmmo, long tick)
        {
            var e = new CombatEntity { OwnerPlayerId = ownerPlayerId, SpawnPos = spawnPos, Ammo = magAmmo, LastChangedTick = tick };
            _byOwner[ownerPlayerId] = e;
            _removedAtTick.Remove(ownerPlayerId);
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId, long tick)
        {
            if (_byOwner.Remove(ownerPlayerId)) _removedAtTick[ownerPlayerId] = tick;
        }

        public void MarkDirty(CombatEntity e, long tick) => e.LastChangedTick = tick;

        /// <summary>MoveInput gate: a dead player's inputs are dropped at the dispatch choke point. Unknown
        /// senders pass (defensive -- the combat entity is created on PeerConnected, before commands flow).</summary>
        public bool IsAlive(ushort ownerPlayerId) => !_byOwner.TryGetValue(ownerPlayerId, out var e) || e.Alive;

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var owners = SortedOwners();
            w.WriteUInt16((ushort)owners.Count);
            foreach (ushort id in owners) WriteEntity(w, _byOwner[id]);
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<ushort>();
            foreach (ushort id in SortedOwners())
                if (_byOwner[id].LastChangedTick > baselineTick) changed.Add(id);
            var removed = new List<ushort>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (ushort id in changed) WriteEntity(w, _byOwner[id]);
            w.WriteUInt16((ushort)removed.Count);
            foreach (ushort id in removed) w.WriteUInt16(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full) _byOwner.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _byOwner[e.OwnerPlayerId] = e;
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt16(out ushort id)) return;
                    _byOwner.Remove(id);
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (ushort id in SortedOwners())
            {
                var e = _byOwner[id];
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixByte(h, e.Alive ? (byte)1 : (byte)0);
                h = NetHash.MixByte(h, e.Health);
                h = NetHash.MixUInt32(h, e.Kills);
                h = NetHash.MixUInt32(h, e.Deaths);
                h = NetHash.MixUInt32(h, e.WornHat); h = NetHash.MixUInt32(h, e.WornGlasses); h = NetHash.MixUInt32(h, e.WornMask);
                h = NetHash.MixUInt32(h, e.WornShirt); h = NetHash.MixUInt32(h, e.WornVest); h = NetHash.MixUInt32(h, e.WornBackpack); h = NetHash.MixUInt32(h, e.WornPants);
                h = NetHash.MixUInt32(h, e.HeldId); h = NetHash.MixByte(h, e.Stance);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, CombatEntity e)
        {
            w.WriteUInt16(e.OwnerPlayerId);
            w.WriteBit(e.Alive);
            w.WriteUInt8(e.Health);
            w.WriteUInt16(e.Kills);
            w.WriteUInt16(e.Deaths);
            w.WriteUInt16(e.WornHat); w.WriteUInt16(e.WornGlasses); w.WriteUInt16(e.WornMask);
            w.WriteUInt16(e.WornShirt); w.WriteUInt16(e.WornVest); w.WriteUInt16(e.WornBackpack); w.WriteUInt16(e.WornPants);
            w.WriteUInt16(e.HeldId); w.WriteUInt8(e.Stance);
        }

        static bool ReadEntity(NetPakReader r, out CombatEntity e)
        {
            e = null;
            if (!r.ReadUInt16(out ushort owner)) return false;
            if (!r.ReadBit(out bool alive)) return false;
            if (!r.ReadUInt8(out byte health)) return false;
            if (!r.ReadUInt16(out ushort kills)) return false;
            if (!r.ReadUInt16(out ushort deaths)) return false;
            if (!r.ReadUInt16(out ushort wHat) || !r.ReadUInt16(out ushort wGlasses) || !r.ReadUInt16(out ushort wMask)) return false;
            if (!r.ReadUInt16(out ushort wShirt) || !r.ReadUInt16(out ushort wVest) || !r.ReadUInt16(out ushort wBackpack) || !r.ReadUInt16(out ushort wPants)) return false;
            if (!r.ReadUInt16(out ushort held) || !r.ReadUInt8(out byte stance)) return false;
            e = new CombatEntity { OwnerPlayerId = owner, Alive = alive, Health = health, Kills = kills, Deaths = deaths,
                WornHat = wHat, WornGlasses = wGlasses, WornMask = wMask, WornShirt = wShirt, WornVest = wVest,
                WornBackpack = wBackpack, WornPants = wPants, HeldId = held, Stance = stance };
            return true;
        }

        void PruneTombstones(long serverTick)
        {
            List<ushort> stale = null;
            foreach (var kv in _removedAtTick)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks)
                    (stale ??= new List<ushort>()).Add(kv.Key);
            if (stale != null) foreach (ushort id in stale) _removedAtTick.Remove(id);
        }

        List<ushort> SortedOwners()
        {
            var ids = new List<ushort>(_byOwner.Keys);
            ids.Sort();
            return ids;
        }
    }
}
