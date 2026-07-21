using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // Deployables + the power grid (MP_PLAN §3.1) -- THE showcase for the replication model: the server
    // owns the GRAPH (deployables, ports, wires, health/fuel, toggle intent) and replicates the solver's
    // INPUTS, never its outputs. Topology changes are reliable Events; health/fuel scalars ride the
    // low-cadence Snap block; and every side -- server and each client replica -- runs the SAME pure
    // PowerSolver.Solve on its own copy of the graph, so Live/Powered/Draw (lamps, load bars, vibration)
    // all derive locally, exactly as they derive from PowerNet.Recompute in single-player.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>One port on a deployable def: kind (PowerPortKind byte) + watts. Port ORDER is the def's
    /// authored order (DeployableDef.Ports), which is stable -- (deployableNetId, portIndex) is the §2.6
    /// sub-address every wire endpoint uses.</summary>
    public struct DeployablePortSpec
    {
        public byte Kind;      // PowerPortKind: 0 Output, 1 Consumer, 2 Passthrough
        public float Watts;
    }

    /// <summary>A3/A2: a server-placed WORLD FIXTURE kind carried on the DEF table. None = an ordinary
    /// player-placeable deployable (generator/spotlight/splitter...); nonzero = a fixed world fixture the
    /// server places at world-build and streams over the EXISTING SystemDeployables graph -- GridSource is
    /// the mains breaker box (A3), GasPump the fuel pump (A2). Def-table ONLY: it is NEVER serialized (DefId
    /// is the wire key both sides rebuild the def from), so it costs zero wire-shape change. The game's
    /// DeployableDef.Fixture mirrors these values 1:1 (bridged in DeployableNetSchema); the enum lives here
    /// because the server choke point (ServerTransactions.RunConsole's grid mains toggle) filters fixtures by
    /// kind and core cannot see the game assembly.</summary>
    // Append-only (never renumber -- these cross the def table + gate the client ReplicaView dispatch). 3-7 are the
    // new deployable-type fixtures (sentry/trap/beacon/charge/oil pump): each is a server-auth sim on the host + a
    // VIEW-ONLY client replica the ReplicaView Materializes (see CLAUDE.md "definition of done").
    public enum FixtureKind : byte { None = 0, GridSource = 1, GasPump = 2, Sentry = 3, Trap = 4, Beacon = 5, Charge = 6, OilPump = 7 }

    /// <summary>The def-derived half of the solver's inputs. Both sides register the SAME defs (game code
    /// registers DeployableDef.All on server and client; L0 tests register fixtures) -- the content hash
    /// handshake is what guarantees they match, so only the defId crosses the wire.</summary>
    public sealed class DeployableNetDef
    {
        public ushort DefId;
        public float Health;
        public float FuelCapacity;   // 0 = no tank (not a generator; toggle commands are rejected)
        public float Range;          // placement reach (ItemBarricadeAsset Range) -- the server's range check
        public FixtureKind FixtureKind;   // A3/A2: a server-placed world fixture kind (None = a normal player-placeable deployable). Def-table only, never on the wire.
        public DeployablePortSpec[] Ports = System.Array.Empty<DeployablePortSpec>();
        public ushort SalvageItemId; // what a blowtorched wreck breaks into (Deployable.Salvage; 0 = nothing)
        public byte SalvageCount;
    }

    /// <summary>Instance-scoped def registry (no static state -- test isolation for free).</summary>
    public sealed class DeployableSchema
    {
        readonly Dictionary<ushort, DeployableNetDef> _byId = new Dictionary<ushort, DeployableNetDef>();

        public void Register(DeployableNetDef def) => _byId[def.DefId] = def;

        public bool TryGet(ushort defId, out DeployableNetDef def) => _byId.TryGetValue(defId, out def);
    }

    // ---- wire messages (hand-written Write/TryRead, MoveInput pattern; ids in ReplicationIds) ----

    public struct PlaceDeployableCommand
    {
        public ushort DefId;
        public Vector3 Pos;
        public float YawDegrees;

        public void Write(NetPakWriter w) { w.WriteUInt16(DefId); NetWire.WritePos(w, Pos); w.WriteDegrees(YawDegrees, NetQuantization.YawBits); }

        public static bool TryRead(NetPakReader r, out PlaceDeployableCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort defId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            cmd = new PlaceDeployableCommand { DefId = defId, Pos = pos, YawDegrees = yaw };
            return true;
        }
    }

    public struct SalvageDeployableCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out SalvageDeployableCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new SalvageDeployableCommand { NetId = id };
            return true;
        }
    }

    /// <summary>B2: hold-F to return a live placed deployable to the bag (distinct from Salvage's scrap --
    /// this hands back the actual item with its HP quality + fuel restored). Same {uint NetId} shape as
    /// SalvageDeployableCommand.</summary>
    public struct PickupDeployableCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out PickupDeployableCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new PickupDeployableCommand { NetId = id };
            return true;
        }
    }

    /// <summary>A2: pull fuel from a gas-station pump (a FixtureKind.GasPump deployable) into a held gas can.
    /// {uint PumpNetId}, the same shape as Salvage/Pickup. The server owns the absolute per-station fuel tank
    /// (game GasStationServer via the IFuelStation seam) and is the SOLE mutation point -- it drains the tank,
    /// fills the sender's can, and writes the recomputed 0..100 PERCENT onto EVERY same-station pump's Fuel
    /// scalar in one tick. The pump's own entity carries no absolute litres; entity.Fuel is that percent.</summary>
    public struct ExtractFuelCommand
    {
        public uint PumpNetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(PumpNetId);
        public static bool TryRead(NetPakReader r, out ExtractFuelCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new ExtractFuelCommand { PumpNetId = id };
            return true;
        }
    }

    /// <summary>A player presses the C4 detonator -> the server blows every charge THEY placed at once (source: the
    /// detonator fires all of the owner's InteractableCharges). No payload -- the target set is "the sender's charges",
    /// resolved server-side from ownership, so a client can't detonate someone else's charges.</summary>
    public struct DetonateChargesCommand
    {
        public void Write(NetPakWriter w) { }   // no payload; the server acts on the authenticated sender id
        public static bool TryRead(NetPakReader r, out DetonateChargesCommand cmd) { cmd = default; return true; }
    }

    public struct ConnectWireCommand
    {
        public uint SrcId; public byte SrcPort;
        public uint DstId; public byte DstPort;

        public void Write(NetPakWriter w) { w.WriteUInt32(SrcId); w.WriteUInt8(SrcPort); w.WriteUInt32(DstId); w.WriteUInt8(DstPort); }

        public static bool TryRead(NetPakReader r, out ConnectWireCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint src)) return false;
            if (!r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst)) return false;
            if (!r.ReadUInt8(out byte dstPort)) return false;
            cmd = new ConnectWireCommand { SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort };
            return true;
        }
    }

    public struct RemoveWireCommand
    {
        public uint WireId;
        public void Write(NetPakWriter w) => w.WriteUInt32(WireId);
        public static bool TryRead(NetPakReader r, out RemoveWireCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new RemoveWireCommand { WireId = id };
            return true;
        }
    }

    public struct ToggleDeployableCommand
    {
        public uint NetId;
        public bool On;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(On); }
        public static bool TryRead(NetPakReader r, out ToggleDeployableCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadBit(out bool on)) return false;
            cmd = new ToggleDeployableCommand { NetId = id, On = on };
            return true;
        }
    }

    // ---- events (server -> client, ReliableOrdered: topology facts, §3.1) ----

    public struct DeployablePlacedEvent
    {
        public uint NetId;
        public ushort DefId;
        public ushort OwnerPlayerId;
        public Vector3 Pos;
        public float YawDegrees;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId); w.WriteUInt16(DefId); w.WriteUInt16(OwnerPlayerId);
            NetWire.WritePos(w, Pos); w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
        }

        public static bool TryRead(NetPakReader r, out DeployablePlacedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort defId)) return false;
            if (!r.ReadUInt16(out ushort owner)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            evt = new DeployablePlacedEvent { NetId = id, DefId = defId, OwnerPlayerId = owner, Pos = pos, YawDegrees = yaw };
            return true;
        }
    }

    public struct DeployableRemovedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out DeployableRemovedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new DeployableRemovedEvent { NetId = id };
            return true;
        }
    }

    public struct WireConnectedEvent
    {
        public uint WireId;
        public uint SrcId; public byte SrcPort;
        public uint DstId; public byte DstPort;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(WireId);
            w.WriteUInt32(SrcId); w.WriteUInt8(SrcPort);
            w.WriteUInt32(DstId); w.WriteUInt8(DstPort);
        }

        public static bool TryRead(NetPakReader r, out WireConnectedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint wireId)) return false;
            if (!r.ReadUInt32(out uint src)) return false;
            if (!r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst)) return false;
            if (!r.ReadUInt8(out byte dstPort)) return false;
            evt = new WireConnectedEvent { WireId = wireId, SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort };
            return true;
        }
    }

    public struct WireRemovedEvent
    {
        public uint WireId;
        public void Write(NetPakWriter w) => w.WriteUInt32(WireId);
        public static bool TryRead(NetPakReader r, out WireRemovedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new WireRemovedEvent { WireId = id };
            return true;
        }
    }

    public struct DeployableToggledEvent
    {
        public uint NetId;
        public bool On;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(On); }
        public static bool TryRead(NetPakReader r, out DeployableToggledEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadBit(out bool on)) return false;
            evt = new DeployableToggledEvent { NetId = id, On = on };
            return true;
        }
    }

    /// <summary>
    /// Deployables + wires as an IReplicatedSystem (SystemId 6). One class serves both sides: the server
    /// mutates through the Server* methods (each paired with a Can* validator the command choke point
    /// calls); the client mutates ONLY through ApplyXxx (event plane) and ReadSnapshot (join/scalars).
    /// Solve() is the §3.1 payoff -- it feeds the replicated graph to the pure PowerSolver, on either side.
    /// </summary>
    public sealed class DeployableReplication : IReplicatedSystem
    {
        public sealed class DeployableEntity
        {
            public uint NetIdValue;
            public ushort DefId;
            public ushort OwnerPlayerId;
            public Vector3 Pos;
            public float YawDegrees;
            public float Health;
            public float Fuel;
            public bool ToggledOn;
            public bool OnFire;
            public long LastChangedTick;

            /// <summary>The engine's effective running state (Deployable.IsPowered's wire mirror): toggled
            /// on, not on fire, and -- if it has a tank -- fuel left.</summary>
            public bool Producing(DeployableNetDef def) => ToggledOn && !OnFire && (def.FuelCapacity <= 0f || Fuel > 0f);

            // solver outputs, refreshed by Solve() -- NEVER on the wire (§3.1: inputs only)
            public PowerPortResult[] Solved = System.Array.Empty<PowerPortResult>();
        }

        public struct PowerPortResult
        {
            public float Live;
            public bool Powered;
            public float Draw;
        }

        public sealed class WireEntity
        {
            public uint NetIdValue;
            public uint SrcId; public byte SrcPort;
            public uint DstId; public byte DstPort;
            public long LastChangedTick;
        }

        /// <summary>Server-side placement range check: the def's own reach plus slack for the eye-to-surface
        /// geometry the server doesn't re-trace. Deliberately looser than the client UI's ghost rules -- a
        /// legal SP-style placement always passes; teleport-grade abuse doesn't.</summary>
        public const float PlaceRangeSlack = 4f;
        /// <summary>Server-side wiring reach: the sender must be near both endpoints (the SP wire tool walks
        /// wire points by hand, so reach per endpoint is the honest bound).</summary>
        public const float WireReach = 16f;

        public byte SystemId => ReplicationIds.SystemDeployables;

        public readonly DeployableSchema Schema = new DeployableSchema();

        readonly NetEntityRegistry<DeployableEntity> _deployables = new NetEntityRegistry<DeployableEntity>();
        readonly NetEntityRegistry<WireEntity> _wires = new NetEntityRegistry<WireEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();       // deployable tombstones
        readonly Dictionary<uint, long> _removedWiresAtTick = new Dictionary<uint, long>();  // wire tombstones

        public int Count => _deployables.Count;
        public int WireCount => _wires.Count;

        public bool TryGet(uint netId, out DeployableEntity e) => _deployables.TryGet(new NetId(netId), out e);
        public bool TryGetWire(uint wireId, out WireEntity w) => _wires.TryGet(new NetId(wireId), out w);

        public IEnumerable<DeployableEntity> All
        {
            get
            {
                foreach (uint id in SortedIds(_deployables))
                {
                    _deployables.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        public IEnumerable<WireEntity> AllWires
        {
            get
            {
                foreach (uint id in SortedIds(_wires))
                {
                    _wires.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        // ---- validation (the command choke point calls these; every rule the plan names) ----

        public bool CanPlace(ushort defId, Vector3 pos, Vector3 senderPos)
        {
            if (!Schema.TryGet(defId, out var def)) return false;
            return (pos - senderPos).magnitude <= def.Range + PlaceRangeSlack;
        }

        public bool CanConnectWire(uint srcId, byte srcPort, uint dstId, byte dstPort, Vector3 senderPos)
        {
            if (srcId == dstId) return false;   // SP rule: a wire never loops back onto its own deployable
            if (!TryGet(srcId, out var src) || !TryGet(dstId, out var dst)) return false;
            if (src.OnFire || dst.OnFire) return false;   // ConnectionPort.Usable: a burning deployable's ports are dead
            if (!Schema.TryGet(src.DefId, out var srcDef) || !Schema.TryGet(dst.DefId, out var dstDef)) return false;
            if (srcPort >= srcDef.Ports.Length || dstPort >= dstDef.Ports.Length) return false;
            byte srcKind = srcDef.Ports[srcPort].Kind, dstKind = dstDef.Ports[dstPort].Kind;
            if (srcKind != (byte)PowerPortKind.Output && srcKind != (byte)PowerPortKind.Passthrough) return false;
            if (dstKind != (byte)PowerPortKind.Consumer) return false;
            if (IsPortWired(srcId, srcPort) || IsPortWired(dstId, dstPort)) return false;   // one wire per port
            if ((src.Pos - senderPos).magnitude > WireReach || (dst.Pos - senderPos).magnitude > WireReach) return false;
            return true;
        }

        public bool IsPortWired(uint netId, byte portIndex)
        {
            foreach (var w in AllWires)
                if ((w.SrcId == netId && w.SrcPort == portIndex) || (w.DstId == netId && w.DstPort == portIndex))
                    return true;
            return false;
        }

        public bool CanToggle(uint netId, out DeployableEntity e)
        {
            // The SP gate minus the cosmetic warmup-ramp buffer (client-side feel, not authority):
            // a fuelled, not-on-fire generator toggles (Deployable.CanTogglePower).
            if (!TryGet(netId, out e)) return false;
            if (!Schema.TryGet(e.DefId, out var def) || def.FuelCapacity <= 0f) return false;
            return !e.OnFire;
        }

        // ---- server-side mutation (each bumps LastChangedTick so the scalar Snap block stays honest) ----

        // Mutation stamps are tick+1: a mutation can land AFTER this tick's snapshot already composed
        // (game hooks and tests mutate between ticks), and the client acking that snapshot would advance
        // its baseline PAST an equal-tick stamp -- the change would never delta out. One tick ahead always
        // beats any already-composed snapshot; command-driven mutations just arrive one snapshot later.
        static long Stamp(long tick) => tick + 1;

        public DeployableEntity ServerPlace(NetId id, ushort defId, ushort owner, Vector3 pos, float yawDegrees, long tick)
        {
            if (!Schema.TryGet(defId, out var def)) return null;
            var e = new DeployableEntity
            {
                NetIdValue = id.Value,
                DefId = defId,
                OwnerPlayerId = owner,
                Pos = PlayerReplication.Quantize(pos),
                YawDegrees = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits),
                Health = def.Health,
                Fuel = def.FuelCapacity,   // a fresh build starts FULL (Deployable.Spawn does the same)
                LastChangedTick = Stamp(tick),
            };
            _deployables.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>Remove a deployable + cascade its wires (Deployable.DisconnectWires' graph mirror).
        /// Returns the cascaded wire ids so the host can broadcast WireRemoved facts; the client-side
        /// ApplyRemoved cascades identically, so the events are belt-and-braces, not load-bearing.</summary>
        public List<uint> ServerRemove(uint netId, long tick)
        {
            var cascaded = new List<uint>();
            if (!_deployables.Remove(new NetId(netId))) return cascaded;
            _removedAtTick[netId] = Stamp(tick);
            foreach (var w in AllWires)
                if (w.SrcId == netId || w.DstId == netId) cascaded.Add(w.NetIdValue);
            foreach (uint wid in cascaded) RemoveWireInternal(wid, tick);
            return cascaded;
        }

        public WireEntity ServerConnectWire(NetId wireId, uint srcId, byte srcPort, uint dstId, byte dstPort, long tick)
        {
            var w = new WireEntity { NetIdValue = wireId.Value, SrcId = srcId, SrcPort = srcPort, DstId = dstId, DstPort = dstPort, LastChangedTick = Stamp(tick) };
            _wires.Add(wireId, w);
            _removedWiresAtTick.Remove(wireId.Value);
            return w;
        }

        public bool ServerRemoveWire(uint wireId, long tick) => RemoveWireInternal(wireId, tick);

        public bool ServerToggle(uint netId, bool on, long tick)
        {
            if (!TryGet(netId, out var e) || e.ToggledOn == on) return false;
            e.ToggledOn = on;
            e.LastChangedTick = Stamp(tick);
            return true;
        }

        /// <summary>Scalar publish (health/fuel/fire) -- the game's node layer or a test writes the
        /// authoritative values through here; the low-cadence Snap block carries them out.</summary>
        public void ServerSetScalars(uint netId, float health, float fuel, bool onFire, long tick)
        {
            if (!TryGet(netId, out var e)) return;
            float qh = QuantizeScalar(health), qf = QuantizeScalar(fuel);
            if (e.Health == qh && e.Fuel == qf && e.OnFire == onFire) return;
            e.Health = qh;
            e.Fuel = qf;
            e.OnFire = onFire;
            e.LastChangedTick = Stamp(tick);
        }

        bool RemoveWireInternal(uint wireId, long tick)
        {
            if (!_wires.Remove(new NetId(wireId))) return false;
            _removedWiresAtTick[wireId] = Stamp(tick);
            return true;
        }

        // ---- client-side event application (idempotent: a delta snapshot may have raced the event in) ----

        public void ApplyPlaced(in DeployablePlacedEvent evt, long tick)
        {
            if (TryGet(evt.NetId, out _)) return;
            ServerPlace(new NetId(evt.NetId), evt.DefId, evt.OwnerPlayerId, evt.Pos, evt.YawDegrees, tick);
        }

        public void ApplyRemoved(in DeployableRemovedEvent evt, long tick) => ServerRemove(evt.NetId, tick);

        public void ApplyWireConnected(in WireConnectedEvent evt, long tick)
        {
            if (TryGetWire(evt.WireId, out _)) return;
            ServerConnectWire(new NetId(evt.WireId), evt.SrcId, evt.SrcPort, evt.DstId, evt.DstPort, tick);
        }

        public void ApplyWireRemoved(in WireRemovedEvent evt, long tick) => RemoveWireInternal(evt.WireId, tick);

        public void ApplyToggled(in DeployableToggledEvent evt, long tick) => ServerToggle(evt.NetId, evt.On, tick);

        // ---- the §3.1 payoff: the same pure solve, on whichever side owns this instance ----

        /// <summary>Feed the replicated graph to PowerSolver and store per-port Live/Powered/Draw on each
        /// entity (DeployableEntity.Solved, indexed by the def's port order). Deterministic: same graph in,
        /// same lamp states out -- the §2.5 determinism boundary's one lean.</summary>
        public void Solve()
        {
            var devices = new List<PowerDevice>();
            var entities = new List<DeployableEntity>();
            var portMap = new Dictionary<(uint, byte), PowerPort>();
            foreach (var e in All)
            {
                if (!Schema.TryGet(e.DefId, out var def)) continue;
                var dev = new PowerDevice { Producing = e.Producing(def), OnFire = e.OnFire };
                for (byte i = 0; i < def.Ports.Length; i++)
                    portMap[(e.NetIdValue, i)] = dev.AddPort((PowerPortKind)def.Ports[i].Kind, def.Ports[i].Watts);
                devices.Add(dev);
                entities.Add(e);
            }

            var wires = new List<PowerWire>();
            foreach (var w in AllWires)
                if (portMap.TryGetValue((w.SrcId, w.SrcPort), out var src) && portMap.TryGetValue((w.DstId, w.DstPort), out var dst))
                    wires.Add(new PowerWire(src, dst));

            PowerSolver.Solve(devices, wires);

            for (int i = 0; i < entities.Count; i++)
            {
                var ports = devices[i].Ports;
                var results = new PowerPortResult[ports.Count];
                for (int p = 0; p < ports.Count; p++)
                    results[p] = new PowerPortResult { Live = ports[p].Live, Powered = ports[p].Powered, Draw = ports[p].Draw };
                entities[i].Solved = results;
            }
        }

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds(_deployables);
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids) { _deployables.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
            var wireIds = SortedIds(_wires);
            w.WriteUInt16((ushort)wireIds.Count);
            foreach (uint id in wireIds) { _wires.TryGet(new NetId(id), out var e); WriteWire(w, e); }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds(_deployables))
            {
                _deployables.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed) { _deployables.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
            WriteRemoved(w, _removedAtTick, baselineTick);

            var changedWires = new List<uint>();
            foreach (uint id in SortedIds(_wires))
            {
                _wires.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changedWires.Add(id);
            }
            w.WriteUInt16((ushort)changedWires.Count);
            foreach (uint id in changedWires) { _wires.TryGet(new NetId(id), out var e); WriteWire(w, e); }
            WriteRemoved(w, _removedWiresAtTick, baselineTick);

            PruneTombstones(_removedAtTick, ctx.ServerTick);
            PruneTombstones(_removedWiresAtTick, ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return;
            if (full) { _deployables.Clear(); _wires.Clear(); }
            for (int i = 0; i < count; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _deployables.Add(new NetId(e.NetIdValue), e);
            }
            if (!full && !ReadRemovals(r, _deployables)) return;

            if (!r.ReadUInt16(out ushort wireCount)) return;
            for (int i = 0; i < wireCount; i++)
            {
                if (!ReadWire(r, out var e)) return;
                _wires.Add(new NetId(e.NetIdValue), e);
            }
            if (!full) ReadRemovals(r, _wires);
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (var e in All)
            {
                h = NetHash.MixUInt32(h, e.NetIdValue);
                h = NetHash.MixUInt32(h, e.DefId);
                h = NetHash.MixUInt32(h, e.OwnerPlayerId);
                h = NetHash.MixFloat(h, e.Pos.x); h = NetHash.MixFloat(h, e.Pos.y); h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixFloat(h, e.Health);
                h = NetHash.MixFloat(h, e.Fuel);
                h = NetHash.MixByte(h, (byte)((e.ToggledOn ? 1 : 0) | (e.OnFire ? 2 : 0)));
            }
            foreach (var w in AllWires)
            {
                h = NetHash.MixUInt32(h, w.NetIdValue);
                h = NetHash.MixUInt32(h, w.SrcId); h = NetHash.MixByte(h, w.SrcPort);
                h = NetHash.MixUInt32(h, w.DstId); h = NetHash.MixByte(h, w.DstPort);
            }
            return h;
        }

        // scalar wire grid: 12 int + 2 frac bits covers health/fuel (max 4095.75, 1/4 grain); quantized at
        // the authority so server state and replica state compare with exact equality
        public static float QuantizeScalar(float v) => NetQuantization.QuantizeClampedFloat(v, 12, 2);

        static void WriteEntity(NetPakWriter w, DeployableEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt16(e.DefId);
            w.WriteUInt16(e.OwnerPlayerId);
            NetWire.WritePos(w, e.Pos);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteClampedFloat(e.Health, 12, 2);
            w.WriteClampedFloat(e.Fuel, 12, 2);
            w.WriteBit(e.ToggledOn);
            w.WriteBit(e.OnFire);
        }

        static bool ReadEntity(NetPakReader r, out DeployableEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort defId)) return false;
            if (!r.ReadUInt16(out ushort owner)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadClampedFloat(12, 2, out float health)) return false;
            if (!r.ReadClampedFloat(12, 2, out float fuel)) return false;
            if (!r.ReadBit(out bool on)) return false;
            if (!r.ReadBit(out bool fire)) return false;
            e = new DeployableEntity
            {
                NetIdValue = id, DefId = defId, OwnerPlayerId = owner, Pos = pos, YawDegrees = yaw,
                Health = health, Fuel = fuel, ToggledOn = on, OnFire = fire,
            };
            return true;
        }

        static void WriteWire(NetPakWriter w, WireEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt32(e.SrcId); w.WriteUInt8(e.SrcPort);
            w.WriteUInt32(e.DstId); w.WriteUInt8(e.DstPort);
        }

        static bool ReadWire(NetPakReader r, out WireEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt32(out uint src)) return false;
            if (!r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst)) return false;
            if (!r.ReadUInt8(out byte dstPort)) return false;
            e = new WireEntity { NetIdValue = id, SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort };
            return true;
        }

        static void WriteRemoved(NetPakWriter w, Dictionary<uint, long> tombstones, long baselineTick)
        {
            var removed = new List<uint>();
            foreach (var kv in tombstones)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);
        }

        bool ReadRemovals<T>(NetPakReader r, NetEntityRegistry<T> registry)
        {
            if (!r.ReadUInt16(out ushort removedCount)) return false;
            for (int i = 0; i < removedCount; i++)
            {
                if (!r.ReadUInt32(out uint id)) return false;
                registry.Remove(new NetId(id));
            }
            return true;
        }

        static void PruneTombstones(Dictionary<uint, long> tombstones, long serverTick)
        {
            List<uint> stale = null;
            foreach (var kv in tombstones)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks)
                    (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) tombstones.Remove(id);
        }

        static List<uint> SortedIds<T>(NetEntityRegistry<T> registry)
        {
            var ids = new List<uint>();
            foreach (var id in registry.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
