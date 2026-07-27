using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine; // SDG.Compat Vector3

namespace UnturnedGodot.Net
{
    // MP for doors, beds and deadzones (SP/MP unify). The rule everywhere: the SERVER decides, using the
    // SAME engine-free logic singleplayer runs (DoorLogic / BedClaims / DeadzoneSim). A client sends an
    // intent and renders what comes back; it never asserts that a door is open or that a bed is its.
    //
    // Deadzones deliberately add NO wire. The server already owns player vitals (SystemVitals 13), so
    // contaminated ground is just damage the server applies -- inventing a "you are irradiated" message
    // would be a second source of truth for something the vitals block already carries.

    // ---- commands (client -> server) ----

    public struct ToggleDoorCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); }
        public static bool TryRead(NetPakReader r, out ToggleDoorCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new ToggleDoorCommand { NetId = id };
            return true;
        }
    }

    public struct SetDoorLockedCommand
    {
        public uint NetId;
        public bool Locked;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(Locked); }
        public static bool TryRead(NetPakReader r, out SetDoorLockedCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadBit(out bool locked)) return false;
            cmd = new SetDoorLockedCommand { NetId = id, Locked = locked };
            return true;
        }
    }

    public struct ClaimBedCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); }
        public static bool TryRead(NetPakReader r, out ClaimBedCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new ClaimBedCommand { NetId = id };
            return true;
        }
    }

    // ---- events (server -> client, ReliableOrdered facts) ----

    /// <summary>A door's full observable state, not just the swing. Locking used to be invisible to
    /// everyone but the server: the owner got no confirmation and every other client went on drawing an
    /// unlocked door. Carrying both bits costs one bit over a toggle-only event and means one message can
    /// answer "what is this door doing" completely.</summary>
    public struct DoorStateEvent
    {
        public uint NetId;
        public bool Open;
        public bool Locked;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(Open); w.WriteBit(Locked); }
        public static bool TryRead(NetPakReader r, out DoorStateEvent e)
        {
            e = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadBit(out bool open)) return false;
            if (!r.ReadBit(out bool locked)) return false;
            e = new DoorStateEvent { NetId = id, Open = open, Locked = locked };
            return true;
        }
    }

    public struct BedClaimedEvent
    {
        public uint NetId;
        public ushort Owner;   // player id; 0 = released
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt16(Owner); }
        public static bool TryRead(NetPakReader r, out BedClaimedEvent e)
        {
            e = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort owner)) return false;
            e = new BedClaimedEvent { NetId = id, Owner = owner };
            return true;
        }
    }

    /// <summary>
    /// The server's authoritative door and bed state, engine-free.
    ///
    /// Every decision routes through the same DoorLogic/BedClaims the singleplayer game uses, so there is
    /// one rule set rather than a client copy and a server copy that drift. Reach is checked here too --
    /// a command naming a door across the map is refused whatever the client believes.
    /// </summary>
    public sealed class ServerInteractables
    {
        /// <summary>How close a player must be to work a door or claim a bed. Generous enough for the
        /// arm's-length interaction the client offers, tight enough that a remote command is refused.</summary>
        public const float InteractReach = 4f;

        public struct ServerDoor
        {
            public uint NetId;
            public Vector3 Pos;
            public DoorLogic.DoorState State;
        }

        readonly Dictionary<uint, ServerDoor> _doors = new Dictionary<uint, ServerDoor>();
        readonly Dictionary<uint, int> _bedIdByNet = new Dictionary<uint, int>();
        readonly Dictionary<int, uint> _netByBedId = new Dictionary<int, uint>();
        readonly BedClaims _beds = new BedClaims();
        int _nextBedId = 1;

        public double Now;   // sim seconds, driven by the host tick -- NOT wall clock

        /// <summary>The composer's clock, so every mutation below can stamp itself. Left null by a harness
        /// that never composes snapshots (everything then stamps against tick 0, which is harmless because
        /// nothing is reading the stamp).</summary>
        public Func<long> TickSource;

        /// <summary>Tick of the most recent change to ANY door or bed. The snapshot block resends the whole
        /// table whenever this beats a client's baseline, rather than tracking a tick per entry -- see the
        /// note on InteractableStateReplication for when that stops being the right trade.</summary>
        public long LastChangedTick { get; private set; }

        // Stamped by every mutator rather than by their callers: a caller that forgets to mark a change
        // produces a client that is silently, permanently wrong about one door, which is precisely the kind
        // of bug that survives every test that only ever looks at a freshly-joined client.
        void Stamp()
        {
            long t = (TickSource?.Invoke() ?? 0L) + 1L;
            if (t > LastChangedTick) LastChangedTick = t;
        }

        public int DoorCount => _doors.Count;
        public int BedCount => _beds.Count;
        public BedClaims Beds => _beds;

        public void RegisterDoor(uint netId, Vector3 pos, ulong owner, bool locked = false)
        {
            _doors[netId] = new ServerDoor
            {
                NetId = netId,
                Pos = pos,
                State = new DoorLogic.DoorState { Owner = owner, Locked = locked, LastToggled = double.NegativeInfinity },
            };
            Stamp();
        }

        public void RegisterBed(uint netId, Vector3 pos, float yaw = 0f)
        {
            if (_bedIdByNet.ContainsKey(netId)) return;
            int id = _nextBedId++;
            _bedIdByNet[netId] = id;
            _netByBedId[id] = netId;
            _beds.Register(id, pos, yaw);
            Stamp();
        }

        public void RemoveDoor(uint netId) { if (_doors.Remove(netId)) Stamp(); }

        public void RemoveBed(uint netId)
        {
            if (!_bedIdByNet.TryGetValue(netId, out int id)) return;
            _beds.Remove(id);
            _bedIdByNet.Remove(netId);
            _netByBedId.Remove(id);
            Stamp();
        }

        /// <summary>Every door, for the snapshot block. Ordered by NetId so the state hash and the wire
        /// bytes do not depend on dictionary iteration order.</summary>
        public IEnumerable<ServerDoor> Doors
        {
            get
            {
                var ids = new List<uint>(_doors.Keys);
                ids.Sort();
                foreach (var id in ids) yield return _doors[id];
            }
        }

        /// <summary>Every bed as (netId, owner), NetId-ordered for the same reason.</summary>
        public IEnumerable<KeyValuePair<uint, ulong>> BedOwners
        {
            get
            {
                var ids = new List<uint>(_bedIdByNet.Keys);
                ids.Sort();
                foreach (var id in ids)
                    yield return new KeyValuePair<uint, ulong>(id, _beds.OwnerOf(_bedIdByNet[id]));
            }
        }

        public bool TryGetDoor(uint netId, out ServerDoor door) => _doors.TryGetValue(netId, out door);
        public bool IsDoorOpen(uint netId) => _doors.TryGetValue(netId, out var d) && d.State.IsOpen;
        public bool IsDoorLocked(uint netId) => _doors.TryGetValue(netId, out var d) && d.State.Locked;

        /// <summary>Is this sender allowed to work this door from where they are standing? Reach is the
        /// server's business; the door's own rules are DoorLogic's.</summary>
        public bool CanToggleDoor(uint netId, Vector3 senderPos, ulong player, ulong group)
        {
            if (!_doors.TryGetValue(netId, out var d)) return false;
            if ((d.Pos - senderPos).magnitude > InteractReach) return false;
            // arcBlocked is false here: the server has no physics arc test, and refusing on the client's
            // say-so would let a client veto other people's doors.
            return DoorLogic.CanToggle(d.State, player, group, Now, false, out _);
        }

        /// <summary>Apply a validated toggle. Returns the new open state.</summary>
        public bool ToggleDoor(uint netId, out bool open)
        {
            open = false;
            if (!_doors.TryGetValue(netId, out var d)) return false;
            d.State = DoorLogic.Toggle(d.State, Now);
            _doors[netId] = d;
            open = d.State.IsOpen;
            Stamp();
            return true;
        }

        public bool SetDoorLocked(uint netId, ulong player, bool locked)
        {
            if (!_doors.TryGetValue(netId, out var d)) return false;
            if (!DoorLogic.TrySetLocked(ref d.State, player, locked)) return false;
            _doors[netId] = d;
            Stamp();
            return true;
        }

        // ---- beds ----

        public bool CanClaimBed(uint netId, Vector3 senderPos, ulong player)
        {
            if (!_bedIdByNet.TryGetValue(netId, out int id)) return false;
            if (!_beds.TryGet(id, out var bed)) return false;
            if ((bed.Position - senderPos).magnitude > InteractReach) return false;
            return _beds.CanClaim(id, player, Now);
        }

        public bool ClaimBed(uint netId, ulong player, out uint releasedNetId)
        {
            releasedNetId = 0;
            if (!_bedIdByNet.TryGetValue(netId, out int id)) return false;
            // Remember what they held, so the caller can tell everyone the old bed came free.
            bool had = _beds.TryGetOwnedBedId(player, out int previous);
            if (!_beds.Claim(id, player, Now)) return false;
            if (had && previous != id && _netByBedId.TryGetValue(previous, out uint prevNet)) releasedNetId = prevNet;
            Stamp();
            return true;
        }

        public ulong BedOwner(uint netId) =>
            _bedIdByNet.TryGetValue(netId, out int id) ? _beds.OwnerOf(id) : 0UL;

        /// <summary>Where this player respawns. False = no bed, use the map spawn.</summary>
        public bool TryGetSpawn(ulong player, out Vector3 pos, out float yaw) => _beds.TryGetSpawn(player, out pos, out yaw);

        /// <summary>A player disconnected or was removed -- their claim stays (a bed persists through a
        /// logout, same as the rest of a base), so nothing to do here beyond documenting the choice.</summary>
        public void OnPlayerLeft(ulong player) { /* claims persist deliberately */ }
    }

    /// <summary>
    /// Door + bed state on the snapshot plane (SystemId 17).
    ///
    /// The events carry every CHANGE, which is all a client that was already connected needs. A client that
    /// joins later has missed all of them, and would otherwise render a wide-open base as sealed and an
    /// owned bed as free -- so this block is the join answer, and the events remain the low-latency path.
    ///
    /// It resends the entire table whenever anything changed since a client's baseline, rather than tracking
    /// a per-entry tick. Doors and beds are placed structures that change on human timescales and number in
    /// the handful per map, so "something moved -> resend all of it" costs a few dozen bytes on the rare tick
    /// where it fires and nothing at all otherwise. That trade inverts once a map carries hundreds of
    /// player-built doors: at that point this wants per-entry change ticks like PlayerReplication's, and the
    /// wire shape below is already a list so it can grow one without a redesign.
    /// </summary>
    public sealed class InteractableStateReplication : IReplicatedSystem
    {
        public byte SystemId => ReplicationIds.SystemInteractables;

        /// <summary>Server side: where the state is read from. Null on a client (which only ever reads).</summary>
        public ServerInteractables Source;

        readonly Dictionary<uint, DoorView> _doors = new Dictionary<uint, DoorView>();
        readonly Dictionary<uint, ushort> _bedOwners = new Dictionary<uint, ushort>();

        public struct DoorView { public bool Open, Locked; }

        /// <summary>Bumped on every applied block, so a game-side view can poll instead of diffing.</summary>
        public long Version { get; private set; }

        public int DoorCount => _doors.Count;
        public int BedCount => _bedOwners.Count;
        public bool TryGetDoor(uint netId, out DoorView view) => _doors.TryGetValue(netId, out view);
        public ushort BedOwner(uint netId) => _bedOwners.TryGetValue(netId, out var o) ? o : (ushort)0;
        public IEnumerable<KeyValuePair<uint, DoorView>> ReplicaDoors => _doors;
        public IEnumerable<KeyValuePair<uint, ushort>> ReplicaBeds => _bedOwners;

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteTable(w);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            // Nothing changed since this client's baseline -> a single bit, not a table.
            if (Source == null || Source.LastChangedTick <= baselineTick) { w.WriteBit(false); return; }
            w.WriteBit(true);
            WriteTable(w);
        }

        void WriteTable(NetPakWriter w)
        {
            if (Source == null) { w.WriteUInt16(0); w.WriteUInt16(0); return; }
            var doors = new List<ServerInteractables.ServerDoor>(Source.Doors);
            w.WriteUInt16((ushort)doors.Count);
            foreach (var d in doors)
            {
                w.WriteUInt32(d.NetId);
                w.WriteBit(d.State.IsOpen);
                w.WriteBit(d.State.Locked);
            }
            var beds = new List<KeyValuePair<uint, ulong>>(Source.BedOwners);
            w.WriteUInt16((ushort)beds.Count);
            foreach (var b in beds)
            {
                w.WriteUInt32(b.Key);
                w.WriteUInt16((ushort)b.Value);   // player ids are ushort on the wire; 0 = unclaimed
            }
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!full)
            {
                if (!r.ReadBit(out bool present)) return;
                if (!present) return;
            }
            if (!r.ReadUInt16(out ushort doorCount)) return;
            // The table is authoritative and complete, so it REPLACES rather than merges -- a door removed
            // server-side has to disappear here, and a merge would keep it forever.
            _doors.Clear();
            for (int i = 0; i < doorCount; i++)
            {
                if (!r.ReadUInt32(out uint netId)) return;
                if (!r.ReadBit(out bool open)) return;
                if (!r.ReadBit(out bool locked)) return;
                _doors[netId] = new DoorView { Open = open, Locked = locked };
            }
            if (!r.ReadUInt16(out ushort bedCount)) return;
            _bedOwners.Clear();
            for (int i = 0; i < bedCount; i++)
            {
                if (!r.ReadUInt32(out uint netId)) return;
                if (!r.ReadUInt16(out ushort owner)) return;
                _bedOwners[netId] = owner;
            }
            Version++;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            if (Source != null)
            {
                foreach (var d in Source.Doors)
                    h = NetHash.MixUInt32(h, d.NetId ^ (d.State.IsOpen ? 0x1u : 0u) ^ (d.State.Locked ? 0x2u : 0u));
                foreach (var b in Source.BedOwners)
                    h = NetHash.MixUInt32(h, b.Key ^ ((uint)b.Value << 8));
                return h;
            }
            var doorIds = new List<uint>(_doors.Keys); doorIds.Sort();
            foreach (var id in doorIds)
                h = NetHash.MixUInt32(h, id ^ (_doors[id].Open ? 0x1u : 0u) ^ (_doors[id].Locked ? 0x2u : 0u));
            var bedIds = new List<uint>(_bedOwners.Keys); bedIds.Sort();
            foreach (var id in bedIds)
                h = NetHash.MixUInt32(h, id ^ ((uint)_bedOwners[id] << 8));
            return h;
        }
    }
}
