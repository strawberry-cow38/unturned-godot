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

    /// <summary>Sit on a piece of furniture, or stand up. NetId 0 means STAND -- one command rather than
    /// two, because sitting and standing are the same question ("which seat am I in, if any") and a client
    /// that could send them independently could sit in two chairs by never sending the stand.</summary>
    public struct SitSeatCommand
    {
        public uint NetId;   // 0 = stand up
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); }
        public static bool TryRead(NetPakReader r, out SitSeatCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new SitSeatCommand { NetId = id };
            return true;
        }
    }

    /// <summary>Swing a PROP's door: a shipping container, a crossing gate arm. One id per door ASSEMBLY,
    /// not per leaf -- see ObjectDoor.GroupLead for why a per-leaf table is unrepresentable.</summary>
    public struct ToggleObjectDoorCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); }
        public static bool TryRead(NetPakReader r, out ToggleObjectDoorCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new ToggleObjectDoorCommand { NetId = id };
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

    /// <summary>A seat's occupant changed. Broadcast rather than unicast: the point of it is that OTHER
    /// players see the chair fill, and see it before the next snapshot rather than a tick later.</summary>
    public struct SeatOccupiedEvent
    {
        public uint NetId;
        public ushort Occupant;   // player id; 0 = the seat came free
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt16(Occupant); }
        public static bool TryRead(NetPakReader r, out SeatOccupiedEvent e)
        {
            e = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort who)) return false;
            e = new SeatOccupiedEvent { NetId = id, Occupant = who };
            return true;
        }
    }

    /// <summary>A prop door's open bit. No lock, because a prop door has none -- that is the whole reason
    /// this is not DoorStateEvent.</summary>
    public struct ObjectDoorStateEvent
    {
        public uint NetId;
        public bool Open;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(Open); }
        public static bool TryRead(NetPakReader r, out ObjectDoorStateEvent e)
        {
            e = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadBit(out bool open)) return false;
            e = new ObjectDoorStateEvent { NetId = id, Open = open };
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

        /// <summary>A place to sit: where it is, and who is in it (0 = free). Position is here for the
        /// SAME reason a door's is -- the reach check is the server's business, and a client naming a chair
        /// across the map is refused whatever it believes.</summary>
        public struct ServerSeat
        {
            public uint NetId;
            public Vector3 Pos;
            public ushort Occupant;
        }

        /// <summary>A PROP's door assembly: where it is, and whether it is swung open. Deliberately thinner
        /// than ServerDoor -- no owner, no lock, no toggle cooldown -- because a shipping container has none
        /// of those and inventing them would mean a second rule set to keep in step with DoorLogic.</summary>
        public struct ServerObjectDoor
        {
            public uint NetId;
            public Vector3 Pos;
            public bool Open;
        }

        readonly Dictionary<uint, ServerObjectDoor> _objectDoors = new Dictionary<uint, ServerObjectDoor>();

        readonly Dictionary<uint, ServerSeat> _seats = new Dictionary<uint, ServerSeat>();
        readonly Dictionary<ushort, uint> _seatByPlayer = new Dictionary<ushort, uint>();   // the reverse index, so standing up and disconnecting are O(1) and cannot miss a seat

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

        /// <summary>Put a door back to a saved open/locked state (WorldSave.ApplyWorld). Separate from
        /// RegisterDoor because the world build registers every door at its default before a save is applied,
        /// and separate from the Toggle/SetLocked command paths because those are a PLAYER acting -- they carry
        /// reach checks and a toggle cooldown, neither of which means anything when the world is being rebuilt.
        /// ServerDoor is a struct, so this writes the whole entry back rather than mutating a copy.</summary>
        public bool ServerRestoreDoor(uint netId, bool open, bool locked)
        {
            if (!_doors.TryGetValue(netId, out var d)) return false;
            var st = d.State;
            st.IsOpen = open;
            st.Locked = locked;
            d.State = st;
            _doors[netId] = d;
            Stamp();
            return true;
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

        // ---- prop doors ----

        public int ObjectDoorCount => _objectDoors.Count;

        public void RegisterObjectDoor(uint netId, Vector3 pos, bool open = false)
        {
            if (netId == 0) return;
            if (_objectDoors.TryGetValue(netId, out var had)) { had.Pos = pos; _objectDoors[netId] = had; return; }   // a re-register must not slam a door someone opened
            _objectDoors[netId] = new ServerObjectDoor { NetId = netId, Pos = pos, Open = open };
            Stamp();
        }

        public void RemoveObjectDoor(uint netId) { if (_objectDoors.Remove(netId)) Stamp(); }

        public bool IsObjectDoorOpen(uint netId) => _objectDoors.TryGetValue(netId, out var d) && d.Open;

        /// <summary>Every prop door as (netId, open), NetId-ordered so the wire and the hash do not depend on
        /// dictionary iteration order.</summary>
        public IEnumerable<KeyValuePair<uint, bool>> ObjectDoors
        {
            get
            {
                var ids = new List<uint>(_objectDoors.Keys);
                ids.Sort();
                foreach (var id in ids) yield return new KeyValuePair<uint, bool>(id, _objectDoors[id].Open);
            }
        }

        /// <summary>Reach only. There is nothing else to ask: a prop door has no owner to check and no lock
        /// to respect, and adding a cooldown here would be a SECOND cooldown -- ObjectDoor already swallows
        /// key-repeat client-side, and one enforced server-side would fight it at a different rate.</summary>
        public bool CanToggleObjectDoor(uint netId, Vector3 senderPos)
            => _objectDoors.TryGetValue(netId, out var d) && (d.Pos - senderPos).magnitude <= InteractReach;

        public bool ToggleObjectDoor(uint netId, out bool open)
        {
            open = false;
            if (!_objectDoors.TryGetValue(netId, out var d)) return false;
            d.Open = !d.Open;
            _objectDoors[netId] = d;
            open = d.Open;
            Stamp();
            return true;
        }

        /// <summary>Put a prop door back to a saved state (the world-restore path), without the reach check a
        /// PLAYER's toggle carries -- rebuilding a world is not somebody standing next to it.</summary>
        public bool ServerRestoreObjectDoor(uint netId, bool open)
        {
            if (!_objectDoors.TryGetValue(netId, out var d)) return false;
            d.Open = open;
            _objectDoors[netId] = d;
            Stamp();
            return true;
        }

        // ---- seats ----

        public int SeatCount => _seats.Count;

        public void RegisterSeat(uint netId, Vector3 pos)
        {
            if (netId == 0) return;   // 0 is the STAND sentinel on the wire and can never name a real seat
            if (_seats.TryGetValue(netId, out var had)) { had.Pos = pos; _seats[netId] = had; return; }   // re-register keeps the occupant: a world rebuild must not tip people out of their chairs
            _seats[netId] = new ServerSeat { NetId = netId, Pos = pos, Occupant = 0 };
            Stamp();
        }

        public void RemoveSeat(uint netId)
        {
            if (!_seats.TryGetValue(netId, out var s)) return;
            if (s.Occupant != 0) _seatByPlayer.Remove(s.Occupant);
            _seats.Remove(netId);
            Stamp();
        }

        /// <summary>Every seat as (netId, occupant), NetId-ordered so the wire bytes and the state hash do
        /// not depend on dictionary iteration order.</summary>
        public IEnumerable<KeyValuePair<uint, ushort>> SeatOccupants
        {
            get
            {
                var ids = new List<uint>(_seats.Keys);
                ids.Sort();
                foreach (var id in ids) yield return new KeyValuePair<uint, ushort>(id, _seats[id].Occupant);
            }
        }

        public ushort SeatOccupant(uint netId) => _seats.TryGetValue(netId, out var s) ? s.Occupant : (ushort)0;
        public bool IsSeated(ushort player) => _seatByPlayer.ContainsKey(player);
        public bool TryGetSeatOf(ushort player, out uint netId) => _seatByPlayer.TryGetValue(player, out netId);

        /// <summary>May this player take this seat from where they are standing? A seat they are ALREADY in
        /// is refused rather than accepted as a no-op -- an accepted no-op would broadcast an event saying
        /// nothing changed, and the client would render a re-sit.</summary>
        public bool CanSit(uint netId, Vector3 senderPos, ushort player)
        {
            if (player == 0) return false;
            if (!_seats.TryGetValue(netId, out var s)) return false;
            if (s.Occupant != 0) return false;
            if ((s.Pos - senderPos).magnitude > InteractReach) return false;
            return true;
        }

        /// <summary>Take a validated seat. Standing up from whatever they were in first, and reporting it,
        /// so moving straight from one chair to another cannot leave the first one occupied by a ghost --
        /// the leak that vehicle seats had before EjectFromVehicleOnDeath.</summary>
        public bool Sit(uint netId, ushort player, out uint releasedNetId)
        {
            releasedNetId = 0;
            if (!_seats.TryGetValue(netId, out var s) || s.Occupant != 0 || player == 0) return false;
            if (_seatByPlayer.TryGetValue(player, out uint previous) && previous != netId)
            {
                Stand(player, out releasedNetId);
            }
            s.Occupant = player;
            _seats[netId] = s;
            _seatByPlayer[player] = netId;
            Stamp();
            return true;
        }

        /// <summary>Get this player out of whatever seat they are in. Returns false when they were in none,
        /// so a caller can tell "stood up" from "was never sitting" and not broadcast an event for the
        /// second. Safe to call for a player who has left -- which is why OnPlayerLeft can just call it.</summary>
        public bool Stand(ushort player, out uint freedNetId)
        {
            freedNetId = 0;
            if (!_seatByPlayer.TryGetValue(player, out uint netId)) return false;
            _seatByPlayer.Remove(player);
            if (_seats.TryGetValue(netId, out var s) && s.Occupant == player)
            {
                s.Occupant = 0;
                _seats[netId] = s;
            }
            freedNetId = netId;
            Stamp();
            return true;
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

        /// <summary>A player disconnected or was removed. Their BED claim stays -- a bed persists through a
        /// logout, same as the rest of a base -- but their SEAT does not: a chair held by someone who is no
        /// longer connected is a chair nobody can ever use again, and unlike a bed there is nothing to
        /// preserve. Returns the freed seat (0 = none) so the caller can broadcast it; a seat that goes free
        /// silently leaves every other client drawing an empty chair as occupied.</summary>
        public uint OnPlayerLeft(ulong player)
        {
            Stand((ushort)player, out uint freed);
            return freed;   // bed claims persist deliberately
        }
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
        readonly Dictionary<uint, ushort> _seatOccupants = new Dictionary<uint, ushort>();
        readonly Dictionary<uint, bool> _objectDoors = new Dictionary<uint, bool>();

        public struct DoorView { public bool Open, Locked; }

        /// <summary>Bumped on every applied block, so a game-side view can poll instead of diffing.</summary>
        public long Version { get; private set; }

        public int DoorCount => _doors.Count;
        public int BedCount => _bedOwners.Count;
        public bool TryGetDoor(uint netId, out DoorView view) => _doors.TryGetValue(netId, out view);
        public ushort BedOwner(uint netId) => _bedOwners.TryGetValue(netId, out var o) ? o : (ushort)0;
        public int SeatCount => _seatOccupants.Count;
        public ushort SeatOccupant(uint netId) => _seatOccupants.TryGetValue(netId, out var o) ? o : (ushort)0;
        public IEnumerable<KeyValuePair<uint, ushort>> ReplicaSeats => _seatOccupants;
        public int ObjectDoorCount => _objectDoors.Count;
        public bool ObjectDoorOpen(uint netId) => _objectDoors.TryGetValue(netId, out var o) && o;
        public IEnumerable<KeyValuePair<uint, bool>> ReplicaObjectDoors => _objectDoors;
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
            if (Source == null) { w.WriteUInt16(0); w.WriteUInt16(0); w.WriteUInt16(0); w.WriteUInt16(0); return; }
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
            // v35: seats ride the same table for the same reason doors and beds do -- a client that joins
            // after someone sat down has missed the event, and would otherwise draw them standing in a chair
            // AND offer the chair as free. The whole table resends on any change, per the note above.
            var seats = new List<KeyValuePair<uint, ushort>>(Source.SeatOccupants);
            w.WriteUInt16((ushort)seats.Count);
            foreach (var st in seats)
            {
                w.WriteUInt32(st.Key);
                w.WriteUInt16(st.Value);   // 0 = free
            }
            // v37: PROP doors (shipping containers, crossing arms) -- the join answer, without which a client
            // that connected after somebody opened a container renders it shut AND collides with the leaf.
            var odoors = new List<KeyValuePair<uint, bool>>(Source.ObjectDoors);
            w.WriteUInt16((ushort)odoors.Count);
            foreach (var d in odoors)
            {
                w.WriteUInt32(d.Key);
                w.WriteBit(d.Value);
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
            if (!r.ReadUInt16(out ushort seatCount)) return;
            _seatOccupants.Clear();   // authoritative and complete: REPLACES, so a removed seat disappears
            for (int i = 0; i < seatCount; i++)
            {
                if (!r.ReadUInt32(out uint netId)) return;
                if (!r.ReadUInt16(out ushort who)) return;
                _seatOccupants[netId] = who;
            }
            if (!r.ReadUInt16(out ushort odoorCount)) return;
            _objectDoors.Clear();
            for (int i = 0; i < odoorCount; i++)
            {
                if (!r.ReadUInt32(out uint netId)) return;
                if (!r.ReadBit(out bool open)) return;
                _objectDoors[netId] = open;
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
                foreach (var st in Source.SeatOccupants)
                    h = NetHash.MixUInt32(h, st.Key ^ ((uint)st.Value << 16));   // <<16, not <<8: a bed and a seat sharing a NetId space must not hash alike
                foreach (var d in Source.ObjectDoors)
                    h = NetHash.MixUInt32(h, d.Key ^ (d.Value ? 0x4000u : 0u));   // its own bit again, for the same reason
                return h;
            }
            var doorIds = new List<uint>(_doors.Keys); doorIds.Sort();
            foreach (var id in doorIds)
                h = NetHash.MixUInt32(h, id ^ (_doors[id].Open ? 0x1u : 0u) ^ (_doors[id].Locked ? 0x2u : 0u));
            var bedIds = new List<uint>(_bedOwners.Keys); bedIds.Sort();
            foreach (var id in bedIds)
                h = NetHash.MixUInt32(h, id ^ ((uint)_bedOwners[id] << 8));
            var seatIds = new List<uint>(_seatOccupants.Keys); seatIds.Sort();
            foreach (var id in seatIds)
                h = NetHash.MixUInt32(h, id ^ ((uint)_seatOccupants[id] << 16));
            var odoorIds = new List<uint>(_objectDoors.Keys); odoorIds.Sort();
            foreach (var id in odoorIds)
                h = NetHash.MixUInt32(h, id ^ (_objectDoors[id] ? 0x4000u : 0u));
            return h;
        }
    }
}
