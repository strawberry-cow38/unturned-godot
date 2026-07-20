using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Vehicles as an IReplicatedSystem (MP_PLAN §3.6, SystemId 9). SERVER side: the real Vehicle nodes
    /// (VehicleBody3D on the headless server world) own all physics; a game-side sync (VehicleNetSync)
    /// publishes each node's transform + velocities + wheel-steer + scalars into these entities every tick
    /// (the composer's SnapshotDivisorTicks=2 makes the wire cadence the plan's 25 Hz). CLIENT side:
    /// replicas drive mesh-only VehiclePuppet nodes that dead-reckon on the replicated velocity -- no
    /// VehicleBody3D, no physics, ever, from this data. Wheel SPIN is deliberately not on the wire: a
    /// rolling wheel's spin derives from the replicated linear velocity ("wheel steer/spin summary" =
    /// steer angle + velocity), so only steer rides the snapshot.
    /// </summary>
    public sealed class VehicleReplication : IReplicatedSystem
    {
        // Flags bits (byte on the wire)
        public const byte FlagEngineOn = 1 << 0;
        public const byte FlagHeadlights = 1 << 1;
        public const byte FlagTaillights = 1 << 2;
        public const byte FlagSiren = 1 << 3;
        public const byte FlagBraking = 1 << 4;
        public const byte FlagExploded = 1 << 5;

        // scalar wire ranges (WriteClampedFloat int/frac bits) -- generous vs the .dat maxima
        // (fuel <= 3000, health <= 1000, battery <= 10000; see Vehicle.cs specs)
        const int FuelIntBits = 12, FuelFracBits = 1;
        const int HealthIntBits = 10, HealthFracBits = 1;
        const int BatteryIntBits = 14, BatteryFracBits = 0;
        public const int SteerBits = 9;   // steer wraps [0,360) like every degree field; real range is +-~35deg (public: VehicleStateCommand rides the same encoding)

        // A6 rope-tow relationship: restLen is a signed ClampedFloat clamped to [Vehicle.TowRestMin 2.0,
        // Vehicle.TowAttachReach 4.5]; 3 int bits (range [-4,4)) caps a long rope at ~3.94m (a purely
        // cosmetic sag length -- the PHYSICS rope keeps its real rest length on the host node), 4 frac bits
        // = 1/16 m. Both sides quantize through the SAME QuantizeClampedFloat, so the wire round-trips
        // bit-exact and StateHash stays byte-identical server<->client (a globally-mirrored system).
        public const int TowRestIntBits = 3, TowRestFracBits = 4;

        public sealed class VehicleEntity
        {
            public uint NetIdValue { get; internal set; }
            public byte TypeId { get; internal set; }        // index into the game's vehicle spec table (Vehicle.SpecNames order)
            public byte Variant { get; internal set; }       // spawn paint variant -- paint derives deterministically (Vehicle.SpawnPaint)
            public ushort DriverPlayerId { get; internal set; }   // 0 = empty seat (single driver, §3.6 v1)
            public Vector3 Pos { get; internal set; }
            public float YawDegrees { get; internal set; }
            public float PitchDegrees { get; internal set; }
            public float RollDegrees { get; internal set; }
            public Vector3 LinVel { get; internal set; }
            public Vector3 AngVel { get; internal set; }
            public float SteerDegrees { get; internal set; } // wrapped [0,360) by the wire encoding; >180 means negative
            public float Fuel { get; internal set; }
            public float Health { get; internal set; }
            public float Battery { get; internal set; }
            public byte Flags { get; internal set; }
            // A6 rope tow (published by the disjoint ServerPublishTow writer, never the transform/vitals paths):
            public uint TowedNetId { get; internal set; }   // the vehicle THIS entity tows (0 = not towing). "Am I towed" is DERIVED client-side (being someone else's TowedNetId) -- NEVER replicated (a redundant TowedByNetId would be a hash-divergence footgun).
            public float TowRestLen { get; internal set; }  // the tow rope's rest length (quantized via QuantizeClampedFloat); cosmetic sag on the client rope. 0 when not towing.
            public long LastChangedTick { get; internal set; }

            public bool Exploded => (Flags & FlagExploded) != 0;
            public bool EngineOn => (Flags & FlagEngineOn) != 0;

            /// <summary>Steer as a signed angle for puppet dressing (the wire wraps into [0,360)).</summary>
            public float SteerSigned => SteerDegrees > 180f ? SteerDegrees - 360f : SteerDegrees;

            // server-only: latest-wins driver input (held-input model, like MoveInput); never replicated
            internal DriveInputCommand CurrentInput;
            internal bool HasInput;

            /// <summary>Server-only, never replicated/hashed: the spec's Speed_Max (m/s) -- the Part A
            /// envelope's per-packet horizontal cap derives from it (retail sqrDelta =
            /// (TargetForwardVelocity * 0.1)^2, U3 VehicleAsset.cs:2319-2333). 0 = unknown spec: the
            /// envelope fails CLOSED to the fuel-empty tight cap, never open.</summary>
            public float SpeedMaxMps { get; internal set; }
        }

        public byte SystemId => ReplicationIds.SystemVehicles;

        readonly NetEntityRegistry<VehicleEntity> _vehicles = new NetEntityRegistry<VehicleEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public int Count => _vehicles.Count;

        public bool TryGet(NetId id, out VehicleEntity entity) => _vehicles.TryGet(id, out entity);
        public bool TryGet(uint id, out VehicleEntity entity) => _vehicles.TryGet(new NetId(id), out entity);

        public IEnumerable<VehicleEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _vehicles.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        // ---- server side ----

        public VehicleEntity ServerSpawn(NetId id, byte typeId, byte variant, Vector3 pos, long tick, float speedMaxMps = 0f)
        {
            var e = new VehicleEntity
            {
                NetIdValue = id.Value,
                TypeId = typeId,
                Variant = variant,
                Pos = PlayerReplication.Quantize(pos),
                LastChangedTick = tick,
                SpeedMaxMps = speedMaxMps,
            };
            _vehicles.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>Publish the node's current physics state (quantized; dirty only on real change -- a
        /// frozen parked car costs no delta bytes between snapshots).</summary>
        public void ServerPublish(NetId id, Vector3 pos, Vector3 eulerDegrees, Vector3 linVel, Vector3 angVel,
                                  float steerDegrees, float fuel, float health, float battery, byte flags, long tick)
        {
            if (!_vehicles.TryGet(id, out var e)) return;
            var newPos = PlayerReplication.Quantize(pos);
            float newYaw = NetQuantization.QuantizeDegrees(eulerDegrees.y, NetQuantization.YawBits);
            float newPitch = NetQuantization.QuantizeDegrees(eulerDegrees.x, NetQuantization.PitchBits);
            float newRoll = NetQuantization.QuantizeDegrees(eulerDegrees.z, NetQuantization.PitchBits);
            var newLin = QuantizeVel(linVel);
            var newAng = QuantizeVel(angVel);
            float newSteer = NetQuantization.QuantizeDegrees(steerDegrees, SteerBits);
            float newFuel = NetQuantization.QuantizeClampedFloat(fuel, FuelIntBits, FuelFracBits);
            float newHealth = NetQuantization.QuantizeClampedFloat(health, HealthIntBits, HealthFracBits);
            float newBattery = NetQuantization.QuantizeClampedFloat(battery, BatteryIntBits, BatteryFracBits);
            if (newPos == e.Pos && newYaw == e.YawDegrees && newPitch == e.PitchDegrees && newRoll == e.RollDegrees
                && newLin == e.LinVel && newAng == e.AngVel && newSteer == e.SteerDegrees
                && newFuel == e.Fuel && newHealth == e.Health && newBattery == e.Battery && flags == e.Flags) return;
            e.Pos = newPos;
            e.YawDegrees = newYaw; e.PitchDegrees = newPitch; e.RollDegrees = newRoll;
            e.LinVel = newLin; e.AngVel = newAng;
            e.SteerDegrees = newSteer;
            e.Fuel = newFuel; e.Health = newHealth; e.Battery = newBattery;
            e.Flags = flags;
            e.LastChangedTick = tick;
        }

        /// <summary>Part A adoption (CLIENT_PREDICTION_PLAN §5.2 A3): write the DRIVER-reported
        /// transform/velocity/steer/dressing-flags into the entity -- retail's post-validation
        /// MovePosition/MoveRotation + "Replicated* stored as-is" (U3 InteractableVehicle.cs:3167-3182).
        /// Quantized EXACTLY like ServerPublish so replicas mirror to StateHash parity. Fuel/health/battery
        /// and the Exploded bit stay SERVER truth -- ServerPublishVitals owns those; the two writers cover
        /// disjoint fields so a predicted-driven vehicle has no double writer.</summary>
        public void ServerAdoptDriverState(NetId id, Vector3 pos, Vector3 eulerDegrees, Vector3 linVel, Vector3 angVel,
                                           float steerDegrees, byte flags, long tick)
        {
            if (!_vehicles.TryGet(id, out var e)) return;
            var newPos = PlayerReplication.Quantize(pos);
            float newYaw = NetQuantization.QuantizeDegrees(eulerDegrees.y, NetQuantization.YawBits);
            float newPitch = NetQuantization.QuantizeDegrees(eulerDegrees.x, NetQuantization.PitchBits);
            float newRoll = NetQuantization.QuantizeDegrees(eulerDegrees.z, NetQuantization.PitchBits);
            var newLin = QuantizeVel(linVel);
            var newAng = QuantizeVel(angVel);
            float newSteer = NetQuantization.QuantizeDegrees(steerDegrees, SteerBits);
            byte newFlags = (byte)((flags & ~FlagExploded) | (e.Flags & FlagExploded));   // Exploded is never client-writable
            if (newPos == e.Pos && newYaw == e.YawDegrees && newPitch == e.PitchDegrees && newRoll == e.RollDegrees
                && newLin == e.LinVel && newAng == e.AngVel && newSteer == e.SteerDegrees && newFlags == e.Flags) return;
            e.Pos = newPos;
            e.YawDegrees = newYaw; e.PitchDegrees = newPitch; e.RollDegrees = newRoll;
            e.LinVel = newLin; e.AngVel = newAng;
            e.SteerDegrees = newSteer;
            e.Flags = newFlags;
            e.LastChangedTick = tick;
        }

        /// <summary>The server-owned scalar half of a predicted-driven vehicle (fuel burn, damage, the
        /// Exploded flag) -- published from the node by VehicleNetSync while adoption owns the transform.</summary>
        public void ServerPublishVitals(NetId id, float fuel, float health, float battery, bool exploded, long tick)
        {
            if (!_vehicles.TryGet(id, out var e)) return;
            float newFuel = NetQuantization.QuantizeClampedFloat(fuel, FuelIntBits, FuelFracBits);
            float newHealth = NetQuantization.QuantizeClampedFloat(health, HealthIntBits, HealthFracBits);
            float newBattery = NetQuantization.QuantizeClampedFloat(battery, BatteryIntBits, BatteryFracBits);
            byte newFlags = (byte)(exploded ? e.Flags | FlagExploded : e.Flags & ~FlagExploded);
            if (newFuel == e.Fuel && newHealth == e.Health && newBattery == e.Battery && newFlags == e.Flags) return;
            e.Fuel = newFuel; e.Health = newHealth; e.Battery = newBattery;
            e.Flags = newFlags;
            e.LastChangedTick = tick;
        }

        /// <summary>A6: the rope-tow relationship half of the vehicle block -- a THIRD disjoint writer.
        /// The tow fields (TowedNetId + TowRestLen) are NEVER touched by ServerPublish /
        /// ServerAdoptDriverState / ServerPublishVitals, so a driven or predicted-and-adopted vehicle has no
        /// double writer: transform, vitals, and tow cover strictly disjoint fields. The NODE
        /// (Vehicle.Towing + _towRestLen) is the single source of truth; the entity is publish-only. RestLen
        /// is quantized with the SAME QuantizeClampedFloat the wire uses so the stored value is already
        /// bit-identical to what every client reconstructs -> StateHash parity needs no tolerance. Dirty-
        /// checks both fields and stamps LastChangedTick ONLY on a real change, so a static rope (or a car
        /// that isn't towing) costs zero delta bytes between snapshots. towedNetId==0 zeroes restLen too, so
        /// a detach clears both fields in one publish.</summary>
        public void ServerPublishTow(NetId id, uint towedNetId, float restLen, long tick)
        {
            if (!_vehicles.TryGet(id, out var e)) return;
            float newRest = towedNetId != 0 ? NetQuantization.QuantizeClampedFloat(restLen, TowRestIntBits, TowRestFracBits) : 0f;
            if (towedNetId == e.TowedNetId && newRest == e.TowRestLen) return;
            e.TowedNetId = towedNetId;
            e.TowRestLen = newRest;
            e.LastChangedTick = tick;
        }

        /// <summary>Occupancy write (0 = seat freed). Both claim paths land here: remote Enter/Exit commands
        /// via ServerVehicles, and the listen-server local player's direct SP enter/exit via VehicleNetSync.</summary>
        public void ServerSetDriver(NetId id, ushort driverPlayerId, long tick)
        {
            if (!_vehicles.TryGet(id, out var e) || e.DriverPlayerId == driverPlayerId) return;
            e.DriverPlayerId = driverPlayerId;
            e.LastChangedTick = tick;
        }

        /// <summary>Latest-wins input queue (DriveInput rides UnreliableSequenced -- a reordered stale
        /// command must never override a newer one).</summary>
        public void ServerQueueInput(uint vehicleNetId, in DriveInputCommand input)
        {
            if (!_vehicles.TryGet(new NetId(vehicleNetId), out var e)) return;
            if (e.HasInput && !NetSeq.IsNewer(input.Seq, e.CurrentInput.Seq)) return;
            e.CurrentInput = input;
            e.HasInput = true;
        }

        public bool TryGetInput(uint vehicleNetId, out DriveInputCommand input)
        {
            input = default;
            if (!_vehicles.TryGet(new NetId(vehicleNetId), out var e) || !e.HasInput) return false;
            input = e.CurrentInput;
            return true;
        }

        public void ServerClearInput(NetId id)
        {
            if (_vehicles.TryGet(id, out var e)) e.HasInput = false;
        }

        public void ServerRemove(NetId id, long tick)
        {
            if (_vehicles.Remove(id)) _removedAtTick[id.Value] = tick;
        }

        // ---- client-side event application (idempotent -- a delta snapshot may carry the same state) ----

        public void ApplyEntered(VehicleEnteredEvent evt, long tick)
            => ServerSetDriver(new NetId(evt.NetId), evt.PlayerId, tick);

        public void ApplyExited(VehicleExitedEvent evt, long tick)
        {
            if (_vehicles.TryGet(new NetId(evt.NetId), out var e) && e.DriverPlayerId == evt.PlayerId)
                ServerSetDriver(new NetId(evt.NetId), 0, tick);
        }

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids)
            {
                _vehicles.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _vehicles.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed)
            {
                _vehicles.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full) _vehicles.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _vehicles.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    _vehicles.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (uint id in SortedIds())
            {
                _vehicles.TryGet(new NetId(id), out var e);
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixByte(h, e.TypeId);
                h = NetHash.MixByte(h, e.Variant);
                h = NetHash.MixUInt32(h, e.DriverPlayerId);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixFloat(h, e.PitchDegrees);
                h = NetHash.MixFloat(h, e.RollDegrees);
                h = NetHash.MixFloat(h, e.LinVel.x);
                h = NetHash.MixFloat(h, e.LinVel.y);
                h = NetHash.MixFloat(h, e.LinVel.z);
                h = NetHash.MixFloat(h, e.AngVel.x);
                h = NetHash.MixFloat(h, e.AngVel.y);
                h = NetHash.MixFloat(h, e.AngVel.z);
                h = NetHash.MixFloat(h, e.SteerDegrees);
                h = NetHash.MixFloat(h, e.Fuel);
                h = NetHash.MixFloat(h, e.Health);
                h = NetHash.MixFloat(h, e.Battery);
                h = NetHash.MixByte(h, e.Flags);
                // A6: mix the tow fields AFTER Flags, symmetric with WriteEntity/ReadEntity. One StateHash()
                // serves both server + client, so the mix is inherently symmetric; the quantized restLen
                // (stored by ServerPublishTow) is byte-identical to the client's wire-read value.
                h = NetHash.MixUInt32(h, e.TowedNetId);
                h = NetHash.MixFloat(h, e.TowRestLen);
            }
            return h;
        }

        static Vector3 QuantizeVel(Vector3 v) => new Vector3(
            NetQuantization.QuantizeClampedFloat(v.x, 6, 6),
            NetQuantization.QuantizeClampedFloat(v.y, 6, 6),
            NetQuantization.QuantizeClampedFloat(v.z, 6, 6));

        static void WriteEntity(NetPakWriter w, VehicleEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt8(e.TypeId);
            w.WriteUInt8(e.Variant);
            w.WriteUInt16(e.DriverPlayerId);
            NetWire.WritePos(w, e.Pos);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteDegrees(e.PitchDegrees, NetQuantization.PitchBits);
            w.WriteDegrees(e.RollDegrees, NetQuantization.PitchBits);
            NetWire.WriteVel(w, e.LinVel);
            NetWire.WriteVel(w, e.AngVel);
            w.WriteDegrees(e.SteerDegrees, SteerBits);
            w.WriteClampedFloat(e.Fuel, FuelIntBits, FuelFracBits);
            w.WriteClampedFloat(e.Health, HealthIntBits, HealthFracBits);
            w.WriteClampedFloat(e.Battery, BatteryIntBits, BatteryFracBits);
            w.WriteUInt8(e.Flags);
            w.WriteUInt32(e.TowedNetId);                                        // A6: appended after Flags
            w.WriteClampedFloat(e.TowRestLen, TowRestIntBits, TowRestFracBits); // A6: cosmetic rope rest length
        }

        static bool ReadEntity(NetPakReader r, out VehicleEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt8(out byte typeId)) return false;
            if (!r.ReadUInt8(out byte variant)) return false;
            if (!r.ReadUInt16(out ushort driver)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadDegrees(out float pitch, NetQuantization.PitchBits)) return false;
            if (!r.ReadDegrees(out float roll, NetQuantization.PitchBits)) return false;
            if (!NetWire.ReadVel(r, out Vector3 lin)) return false;
            if (!NetWire.ReadVel(r, out Vector3 ang)) return false;
            if (!r.ReadDegrees(out float steer, SteerBits)) return false;
            if (!r.ReadClampedFloat(FuelIntBits, FuelFracBits, out float fuel)) return false;
            if (!r.ReadClampedFloat(HealthIntBits, HealthFracBits, out float health)) return false;
            if (!r.ReadClampedFloat(BatteryIntBits, BatteryFracBits, out float battery)) return false;
            if (!r.ReadUInt8(out byte flags)) return false;
            if (!r.ReadUInt32(out uint towedNetId)) return false;                                     // A6: appended after Flags
            if (!r.ReadClampedFloat(TowRestIntBits, TowRestFracBits, out float towRestLen)) return false; // A6
            e = new VehicleEntity
            {
                NetIdValue = id, TypeId = typeId, Variant = variant, DriverPlayerId = driver,
                Pos = pos, YawDegrees = yaw, PitchDegrees = pitch, RollDegrees = roll,
                LinVel = lin, AngVel = ang, SteerDegrees = steer,
                Fuel = fuel, Health = health, Battery = battery, Flags = flags,
                TowedNetId = towedNetId, TowRestLen = towRestLen,
            };
            return true;
        }

        void PruneTombstones(long serverTick)
        {
            List<uint> stale = null;
            foreach (var kv in _removedAtTick)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks)
                    (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) _removedAtTick.Remove(id);
        }

        List<uint> SortedIds()
        {
            var ids = new List<uint>();
            foreach (var id in _vehicles.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Command plane (§3.6): the driver's per-tick control intent (UnreliableSequenced, held-input model)
    // and the transactional Enter/Exit (ReliableOrdered), validated at the §2.3 choke point.
    // ---------------------------------------------------------------------------------------------------

    public struct DriveInputCommand
    {
        public ushort Seq;        // client-local, monotonically increasing (wrap via NetSeq)
        public uint NetId;        // which vehicle -- validated against the sender's driven vehicle
        public float Throttle;    // [-1,1], quantized to 8 bits
        public float Steer;       // [-1,1]
        public bool Handbrake;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteUInt32(NetId);
            w.WriteSignedNormalizedFloat(Clamp1(Throttle), 8);
            w.WriteSignedNormalizedFloat(Clamp1(Steer), 8);
            w.WriteBit(Handbrake);
        }

        public static bool TryRead(NetPakReader r, out DriveInputCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadUInt32(out uint netId)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float throttle)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float steer)) return false;
            if (!r.ReadBit(out bool handbrake)) return false;
            cmd = new DriveInputCommand { Seq = seq, NetId = netId, Throttle = throttle, Steer = steer, Handbrake = handbrake };
            return true;
        }

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// Part A (CLIENT_PREDICTION_PLAN §5.2 A2, wire Version 6): the predicted DRIVER's reported vehicle
    /// state -- the port's DrivingPlayerInputPacket (U3 PlayerInput.cs:658-726). UnreliableSequenced @
    /// 25 Hz (every 2nd tick, the snapshot cadence), latest-wins by Seq server-side. Pos/rot/vel/steer ride
    /// the SAME quantizers as the vehicle snapshot block, so an adopted claim replicates back bit-exact.
    /// The old DriveInput axes ride along as wheel/light dressing + the server fallback; RecovAck echoes
    /// the server's rollback counter (retail input.recov). NaN/extent sanity is structural: every field
    /// decodes from bounded ClampedFloat/Degrees bit-fields -- the reader cannot produce NaN/Inf or an
    /// out-of-world position.
    /// </summary>
    public struct VehicleStateCommand
    {
        public ushort Seq;         // client-local, monotonically increasing (wrap via NetSeq); latest-wins
        public uint NetId;         // which vehicle -- validated against the sender's driven vehicle
        public byte RecovAck;      // echo of the last VehicleRecovEvent counter received (0 = none yet)
        public Vector3 Pos;
        public float YawDegrees, PitchDegrees, RollDegrees;
        public Vector3 LinVel, AngVel;
        public float SteerDegrees; // the wheel-steer summary (signed via the [0,360) wrap, like the snapshot)
        public float Throttle;     // [-1,1] -- the old DriveInput payload (dressing + non-predicted fallback)
        public float Steer;        // [-1,1]
        public bool Handbrake;
        public byte Flags;         // VehicleReplication.Flag* dressing bits (engine/lights/siren/braking); Exploded is never client-writable

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteUInt32(NetId);
            w.WriteUInt8(RecovAck);
            NetWire.WritePos(w, Pos);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
            w.WriteDegrees(PitchDegrees, NetQuantization.PitchBits);
            w.WriteDegrees(RollDegrees, NetQuantization.PitchBits);
            NetWire.WriteVel(w, LinVel);
            NetWire.WriteVel(w, AngVel);
            w.WriteDegrees(SteerDegrees, VehicleReplication.SteerBits);
            w.WriteSignedNormalizedFloat(Clamp1(Throttle), 8);
            w.WriteSignedNormalizedFloat(Clamp1(Steer), 8);
            w.WriteBit(Handbrake);
            w.WriteUInt8(Flags);
        }

        public static bool TryRead(NetPakReader r, out VehicleStateCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadUInt32(out uint netId)) return false;
            if (!r.ReadUInt8(out byte recovAck)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadDegrees(out float pitch, NetQuantization.PitchBits)) return false;
            if (!r.ReadDegrees(out float roll, NetQuantization.PitchBits)) return false;
            if (!NetWire.ReadVel(r, out Vector3 lin)) return false;
            if (!NetWire.ReadVel(r, out Vector3 ang)) return false;
            if (!r.ReadDegrees(out float steerDeg, VehicleReplication.SteerBits)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float throttle)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float steer)) return false;
            if (!r.ReadBit(out bool handbrake)) return false;
            if (!r.ReadUInt8(out byte flags)) return false;
            cmd = new VehicleStateCommand
            {
                Seq = seq, NetId = netId, RecovAck = recovAck,
                Pos = pos, YawDegrees = yaw, PitchDegrees = pitch, RollDegrees = roll,
                LinVel = lin, AngVel = ang, SteerDegrees = steerDeg,
                Throttle = throttle, Steer = steer, Handbrake = handbrake, Flags = flags,
            };
            return true;
        }

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// Part A: the server's rollback of an out-of-envelope predicted driver -- retail tellRecov
    /// (U3 InteractableVehicle.cs:2095-2109). ReliableOrdered, unicast to the driver: the last-GOOD
    /// entity state + the incremented counter. The client teleports its local vehicle to this, zeroes
    /// velocity, freezes until its outgoing RecovAck echoes the counter; the server discards state
    /// packets whose ack lags (the :3069-3085 wait).
    /// </summary>
    public struct VehicleRecovEvent
    {
        public uint NetId;
        public Vector3 Pos;
        public float YawDegrees, PitchDegrees, RollDegrees;
        public Vector3 LinVel;
        public byte RecovCounter;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId);
            NetWire.WritePos(w, Pos);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
            w.WriteDegrees(PitchDegrees, NetQuantization.PitchBits);
            w.WriteDegrees(RollDegrees, NetQuantization.PitchBits);
            NetWire.WriteVel(w, LinVel);
            w.WriteUInt8(RecovCounter);
        }

        public static bool TryRead(NetPakReader r, out VehicleRecovEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint netId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadDegrees(out float pitch, NetQuantization.PitchBits)) return false;
            if (!r.ReadDegrees(out float roll, NetQuantization.PitchBits)) return false;
            if (!NetWire.ReadVel(r, out Vector3 lin)) return false;
            if (!r.ReadUInt8(out byte counter)) return false;
            evt = new VehicleRecovEvent
            {
                NetId = netId, Pos = pos, YawDegrees = yaw, PitchDegrees = pitch, RollDegrees = roll,
                LinVel = lin, RecovCounter = counter,
            };
            return true;
        }
    }

    public struct EnterVehicleCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out EnterVehicleCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new EnterVehicleCommand { NetId = id };
            return true;
        }
    }

    public struct ExitVehicleCommand
    {
        // no payload: the server knows which vehicle the sender drives (single driver, §3.6 v1)
        public void Write(NetPakWriter w) { }
        public static bool TryRead(NetPakReader r, out ExitVehicleCommand cmd) { cmd = default; return true; }
    }

    /// <summary>B11: tie a tow rope between two vehicles -- the tower's REAR node hooks the towed's FRONT node.
    /// {uint TowerNetId, uint TowedNetId}. The server RESOLVES both NetIds to real Vehicle nodes and, at the
    /// game-side choke point (VehicleNetSync.OnAttachTow), validates existence + not-remote-driven +
    /// not-already-roped + reach before calling towerNode.AttachTow(towedNode). No rest length rides -- AttachTow
    /// computes it from the LIVE gap; the committed relationship echoes back through A6's replicated TowedNetId/
    /// TowRestLen (the CompleteWire discipline -- the client never mutates tow state locally).</summary>
    public struct AttachTowCommand
    {
        public uint TowerNetId;
        public uint TowedNetId;
        public void Write(NetPakWriter w) { w.WriteUInt32(TowerNetId); w.WriteUInt32(TowedNetId); }
        public static bool TryRead(NetPakReader r, out AttachTowCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint tower)) return false;
            if (!r.ReadUInt32(out uint towed)) return false;   // fail-closed: a short read leaves cmd=default and returns false (no partial apply)
            cmd = new AttachTowCommand { TowerNetId = tower, TowedNetId = towed };
            return true;
        }
    }

    /// <summary>B11: untie a vehicle's tow rope. {uint NetId} of EITHER end -- the handler resolves to the tower
    /// exactly like Vehicle.DetachTow. The cleared relationship echoes back through A6's TowedNetId->0.</summary>
    public struct DetachTowCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out DetachTowCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;   // fail-closed on a short read
            cmd = new DetachTowCommand { NetId = id };
            return true;
        }
    }

    public struct VehicleEnteredEvent
    {
        public uint NetId;
        public ushort PlayerId;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt16(PlayerId); }
        public static bool TryRead(NetPakReader r, out VehicleEnteredEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort player)) return false;
            evt = new VehicleEnteredEvent { NetId = id, PlayerId = player };
            return true;
        }
    }

    public struct VehicleExitedEvent
    {
        public uint NetId;
        public ushort PlayerId;
        /// <summary>The AUTHORITATIVE exit spot (ServerExit's beside-the-door teleport, post-AdjustExitSpot).
        /// Rides the event so the exiting client places its shell correctly even when the snapshot stream is
        /// starved/held and its vehicle replica is stale (docs/EXIT_POSITION_ROOTCAUSE.md). Raw float32 x3
        /// (12 bytes, exact -- the terrain clamp's height must survive the wire verbatim) on a rare
        /// ReliableOrdered event. Vector3.zero = no spot (the vehicle despawned before the exit); consumers
        /// fall back locally. Wire shape change -> NetProtocol.Version 4.</summary>
        public Vector3 Pos;
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId); w.WriteUInt16(PlayerId);
            w.WriteFloat(Pos.x); w.WriteFloat(Pos.y); w.WriteFloat(Pos.z);
        }
        public static bool TryRead(NetPakReader r, out VehicleExitedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort player)) return false;
            if (!r.ReadFloat(out float x) || !r.ReadFloat(out float y) || !r.ReadFloat(out float z)) return false;
            evt = new VehicleExitedEvent { NetId = id, PlayerId = player, Pos = new Vector3(x, y, z) };
            return true;
        }
    }

    /// <summary>
    /// Server-side enter/drive/exit arbitration (MP_PLAN §3.6 / §4 Phase 7), engine-free. Registers the
    /// vehicle commands on the §2.3 choke point: sender identity from the connection, one driver per
    /// vehicle, reach to enter. Occupancy TRUTH is the entity's DriverPlayerId (which also covers the
    /// listen-server local player, whose direct SP enter is published by VehicleNetSync); this class
    /// additionally tracks which vehicle each REMOTE player drives so Exit/DriveInput validate cheaply
    /// and a disconnect frees the seat.
    /// </summary>
    public sealed class ServerVehicles
    {
        /// <summary>Server-side enter reach: SP enters by look-focus within ~4 m + generous vehicle bounds;
        /// the server bounds it from the entity CENTER (long vehicles reach farther than their center), so
        /// it is generous like ServerTransactions.PickupReach.</summary>
        public const float EnterReach = 6f;

        // ---- Part A: the retail plausibility envelope (CLIENT_PREDICTION_PLAN §5.2 A3). All constants
        // spec- or retail-derived, never tuned magic. ----
        /// <summary>Retail's horizontal cap is sqrDelta = (TargetForwardVelocity * 0.1)^2 at the 0.08 s
        /// input RATE (U3 VehicleAsset.cs:2319-2333) -- i.e. one nominal interval of top speed + 25 %
        /// slack. The port keeps the 1.25 slack but scales by the ACTUAL ticks a packet covers (retail can
        /// use a fixed per-packet cap because its input stream is reliable+ordered -- every packet is
        /// consumed; our UnreliableSequenced stream drops overtaken packets, so an accepted packet may
        /// legitimately span several intervals of motion).</summary>
        public const float EnvelopeSlack = 1.25f;
        /// <summary>Retail fuel-empty override: HorizontalDistanceSquared > 0.5f (squared metres, ~0.71 m)
        /// when usesFuel && fuel == 0 (U3 InteractableVehicle.cs:3096). Also the fail-CLOSED cap for an
        /// entity spawned without a spec SpeedMax.</summary>
        public const float FuelEmptySqrCap = 0.5f;
        /// <summary>Retail CAR vertical caps: climb 12.5 m/s, fall 25 m/s (defaults, U3
        /// VehicleAsset.cs:2336-2349; check :3138-3152 -- approxSpeed = |dy| / delta).</summary>
        public const float ValidSpeedUpCar = 12.5f;
        public const float ValidSpeedDownCar = 25f;
        /// <summary>Elapsed-tick clamp for the envelope interval: floor 2 ticks (the 25 Hz send cadence --
        /// a burst of packets in one tick must not shrink the cap to zero), ceiling 25 ticks (0.5 s -- a
        /// cheater fabricating silence cannot inflate the cap beyond half a second of top speed; a longer
        /// REAL stall rolls the driver back via recov, which is safe, just abrupt).</summary>
        public const int EnvelopeMinTicks = 2;
        public const int EnvelopeMaxTicks = 25;

        /// <summary>Optional game-side exit-spot adjuster (PEI_CLIENT_PLAN §7 risk 6): the raw exit spot
        /// (vehicle pos + right*2.4 + 1 up) has no ground snap, so on a hillside it can land BELOW the
        /// terrain surface and drop the avatar through the world. The dedicated server wires a
        /// Terr.SampleHeight clamp here; null (every test/demo default) keeps the raw spot.</summary>
        public Func<Vector3, Vector3> AdjustExitSpot;

        readonly VehicleReplication _vehicles;
        readonly PlayerReplication _players;
        readonly PlayerCombatReplication _combat;
        readonly Func<long> _tick;
        readonly Action<byte[]> _broadcast;
        readonly Action<ushort, byte[]> _sendTo;   // Part A: recov is a driver-unicast reliable event

        readonly Dictionary<ushort, uint> _drivenByPlayer = new Dictionary<ushort, uint>();

        /// <summary>The raw (unquantized) driver-reported state the server last ADOPTED -- what
        /// VehicleNetSync teleports the held node to each tick (game-side), engine-free here.</summary>
        public struct PredictedVehicleState
        {
            public Vector3 Pos;
            public float YawDegrees, PitchDegrees, RollDegrees;
            public Vector3 LinVel, AngVel;
            public float SteerDegrees;
            public float Throttle, Steer;
            public bool Handbrake;
            public byte Flags;
        }

        // Part A per-driven-vehicle authority state, keyed by vehicle NetId. Created at ServerEnter,
        // dropped at ServerExit -- a fresh driver always starts with a clean counter/seq window.
        sealed class DrivenState
        {
            public bool HasSeq; public ushort LastSeq;
            public long LastAcceptedTick;          // envelope interval baseline (enter tick until the first accept)
            public byte RecovCounter;              // increments per violation (retail input.recov)
            public bool Recovering;                // discard states until RecovAck echoes the counter
            public bool Predicted;                 // latched by the first ACCEPTED state -- flips VehicleNetSync to hold mode
            public bool HasAdopted;
            public PredictedVehicleState Adopted;
        }
        readonly Dictionary<uint, DrivenState> _driven = new Dictionary<uint, DrivenState>();

        public ServerVehicles(VehicleReplication vehicles, PlayerReplication players, PlayerCombatReplication combat,
                              Func<long> tick, Action<byte[]> broadcast, Action<ushort, byte[]> sendTo = null)
        {
            _vehicles = vehicles; _players = players; _combat = combat;
            _tick = tick; _broadcast = broadcast; _sendTo = sendTo;
        }

        public void Register(CommandRegistry commands)
        {
            commands.Register<EnterVehicleCommand>(ReplicationIds.CommandEnterVehicle, EnterVehicleCommand.TryRead,
                (sender, cmd) => ServerEnter(sender, cmd.NetId),
                validate: (sender, cmd) => CanEnter(sender, cmd.NetId));

            commands.Register<ExitVehicleCommand>(ReplicationIds.CommandExitVehicle, ExitVehicleCommand.TryRead,
                (sender, cmd) => ServerExit(sender),
                validate: (sender, cmd) => IsDriver(sender));

            commands.Register<DriveInputCommand>(ReplicationIds.CommandDriveInput, DriveInputCommand.TryRead,
                (sender, cmd) => _vehicles.ServerQueueInput(cmd.NetId, in cmd),
                validate: (sender, cmd) => _drivenByPlayer.TryGetValue(sender, out uint driven) && driven == cmd.NetId);

            // Part A: the predicted driver's reported state. Sender identity from the CONNECTION, never
            // the payload (the §2.3 choke-point rule) -- only the vehicle's actual driver passes.
            commands.Register<VehicleStateCommand>(ReplicationIds.CommandVehicleState, VehicleStateCommand.TryRead,
                (sender, cmd) => OnVehicleState(sender, cmd),
                validate: (sender, cmd) => _drivenByPlayer.TryGetValue(sender, out uint driven) && driven == cmd.NetId);
        }

        /// <summary>True once the driver's client has had a state packet ACCEPTED for this vehicle -- the
        /// game-side sync flips the node to the freeze-hold and stops calling Drive.</summary>
        public bool IsPredictedDriven(uint vehicleNetId)
            => _driven.TryGetValue(vehicleNetId, out var st) && st.Predicted;

        public bool TryGetPredictedState(uint vehicleNetId, out PredictedVehicleState state)
        {
            if (_driven.TryGetValue(vehicleNetId, out var st) && st.Predicted && st.HasAdopted)
            {
                state = st.Adopted;
                return true;
            }
            state = default;
            return false;
        }

        /// <summary>The Part A adopt-or-recov pipeline (CLIENT_PREDICTION_PLAN §5.2 A3), retail-shaped
        /// (U3 InteractableVehicle.cs:3069-3182): seq latest-wins -> recov ack gate -> the plausibility
        /// envelope -> adopt into the entity (observers dead-reckon off the driver's truth) or roll the
        /// driver back. Runs at command-dispatch time inside TickSimulation.</summary>
        void OnVehicleState(ushort sender, VehicleStateCommand cmd)
        {
            if (!_vehicles.TryGet(new NetId(cmd.NetId), out var e)) return;
            if (!_driven.TryGetValue(cmd.NetId, out var st)) return;   // validate guarantees a driver, so this exists
            long tick = _tick();

            // latest-wins by Seq (the DriveInput guard pattern -- UnreliableSequenced dedups per datagram,
            // but a fragment/burst boundary can still deliver two commands out of order)
            if (st.HasSeq && !NetSeq.IsNewer(cmd.Seq, st.LastSeq)) return;
            st.HasSeq = true; st.LastSeq = cmd.Seq;

            // recov ack wait (retail :3069-3085): while recovering, discard every state whose RecovAck
            // lags the counter -- the client is still driving a rolled-back-in-flight position. The wire
            // is ReliableOrdered for the event itself, so no retail-style 5 s resend is needed: the echo
            // WILL come. The packet that carries it resumes validation below, from the last-good baseline.
            if (st.Recovering)
            {
                if (cmd.RecovAck != st.RecovCounter) return;
                st.Recovering = false;
            }

            // ---- the envelope. NaN/extent sanity is structural: every field decoded from bounded
            // ClampedFloat/Degrees bit-fields (see VehicleStateCommand), so only range plausibility is
            // checked here, exactly like retail. ----
            float dt = Math.Clamp(tick - st.LastAcceptedTick, EnvelopeMinTicks, EnvelopeMaxTicks) * (float)SimClock.FixedDelta;

            // horizontal delta cap: (SpeedMax x interval x slack)^2, with the retail fuel-empty 0.5f
            // squared-metres override (U3 :3096-3105); unknown spec fails closed to the same tight cap
            float dx = cmd.Pos.x - e.Pos.x, dz = cmd.Pos.z - e.Pos.z;
            float capSq;
            if (e.Fuel <= 0f || e.SpeedMaxMps <= 0f) capSq = FuelEmptySqrCap;
            else { float cap = e.SpeedMaxMps * dt * EnvelopeSlack; capSq = cap * cap; }
            bool violation = dx * dx + dz * dz > capSq;

            // vertical speed caps (U3 :3138-3152): climb validSpeedUp, fall validSpeedDown
            if (!violation)
            {
                float dy = cmd.Pos.y - e.Pos.y;
                float validSpeed = dy > 0f ? ValidSpeedUpCar : ValidSpeedDownCar;
                violation = Math.Abs(dy) / dt > validSpeed;
            }

            if (violation)
            {
                // recov (retail tellRecov): counter++, ship the LAST-GOOD entity state to the driver,
                // hold adoption until the echo. The entity is untouched -- observers keep the last-good.
                st.RecovCounter++;
                st.Recovering = true;
                if (NetLog.Enabled) NetLog.Sink($"[NET] vehicle {cmd.NetId} driver {sender}: state out of envelope (d {Math.Sqrt(dx * dx + dz * dz):0.0} m / cap {Math.Sqrt(capSq):0.0} m in {dt:0.00} s) -> recov #{st.RecovCounter}");
                var evt = new VehicleRecovEvent
                {
                    NetId = e.NetIdValue, Pos = e.Pos,
                    YawDegrees = e.YawDegrees, PitchDegrees = e.PitchDegrees, RollDegrees = e.RollDegrees,
                    LinVel = e.LinVel, RecovCounter = st.RecovCounter,
                };
                _sendTo?.Invoke(sender, NetMessagePak.Pack(ReplicationIds.EventVehicleRecov, evt.Write));
                return;
            }

            // ---- adopt: the driver's report becomes the vehicle's truth ----
            st.LastAcceptedTick = tick;
            st.Predicted = true;
            st.HasAdopted = true;
            st.Adopted = new PredictedVehicleState
            {
                Pos = cmd.Pos, YawDegrees = cmd.YawDegrees, PitchDegrees = cmd.PitchDegrees, RollDegrees = cmd.RollDegrees,
                LinVel = cmd.LinVel, AngVel = cmd.AngVel, SteerDegrees = cmd.SteerDegrees,
                Throttle = cmd.Throttle, Steer = cmd.Steer, Handbrake = cmd.Handbrake, Flags = cmd.Flags,
            };
            _vehicles.ServerAdoptDriverState(new NetId(cmd.NetId), cmd.Pos,
                new Vector3(cmd.PitchDegrees, cmd.YawDegrees, cmd.RollDegrees),
                cmd.LinVel, cmd.AngVel, cmd.SteerDegrees, cmd.Flags, tick);
        }

        public bool IsDriver(ushort playerId) => _drivenByPlayer.ContainsKey(playerId);

        public bool TryGetDriven(ushort playerId, out uint vehicleNetId) => _drivenByPlayer.TryGetValue(playerId, out vehicleNetId);

        public bool CanEnter(ushort sender, uint netId)
        {
            if (!_vehicles.TryGet(new NetId(netId), out var v)) return false;
            if (v.DriverPlayerId != 0 || v.Exploded) return false;   // one driver per vehicle; wrecks aren't seats
            if (IsDriver(sender)) return false;
            if (!_combat.IsAlive(sender)) return false;
            if (!_players.TryGetByOwner(sender, out var p)) return false;
            return (v.Pos - p.Pos).magnitude <= EnterReach;
        }

        void ServerEnter(ushort sender, uint netId)
        {
            long tick = _tick();
            _vehicles.ServerSetDriver(new NetId(netId), sender, tick);
            _drivenByPlayer[sender] = netId;
            _players.ServerClearInput(sender);   // held walk input must not keep integrating under the seat
            // Part A: a fresh driver gets a clean authority window -- counter 0, no seq, the envelope
            // interval anchored at the enter tick (the first state's dt clamps to EnvelopeMaxTicks anyway)
            _driven[netId] = new DrivenState { LastAcceptedTick = tick };
            var evt = new VehicleEnteredEvent { NetId = netId, PlayerId = sender };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventVehicleEntered, evt.Write));
        }

        /// <summary>Free the seat: clears occupancy + input, teleports the (remote) player's entity beside
        /// the driver door (the SP exit spot: vehicle pos + right * 2.4 + up), broadcasts the fact.
        /// Idempotent -- false if the player wasn't driving.</summary>
        public bool ServerExit(ushort playerId)
        {
            if (!_drivenByPlayer.TryGetValue(playerId, out uint netId)) return false;
            _drivenByPlayer.Remove(playerId);
            _driven.Remove(netId);   // Part A: authority returns to the server (VehicleNetSync releases the hold when Predicted drops)
            long tick = _tick();
            var spot = Vector3.zero;   // zero = no spot (vehicle already despawned) -> clients fall back locally
            if (_vehicles.TryGet(new NetId(netId), out var v))
            {
                _vehicles.ServerSetDriver(new NetId(netId), 0, tick);
                _vehicles.ServerClearInput(new NetId(netId));
                float yawRad = v.YawDegrees * (Mathf.PI / 180f);
                // Godot yaw basis: right (basis.X) = (cos yaw, 0, -sin yaw)
                var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
                spot = v.Pos + right * 2.4f + new Vector3(0f, 1.0f, 0f);
                if (AdjustExitSpot != null) spot = AdjustExitSpot(spot);   // §7 risk 6: terrain-snap a below-ground slope exit
                _players.ServerTeleport(playerId, spot, tick);
            }
            // the event carries the final (post-clamp) spot: the exiting client's replica may be frozen
            // by a snapshot starvation/hold, but this fact rides ReliableOrdered and always arrives
            var evt = new VehicleExitedEvent { NetId = netId, PlayerId = playerId, Pos = spot };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventVehicleExited, evt.Write));
            return true;
        }

        /// <summary>A vanished vehicle (despawned wreck node) drops its driver without an entity to exit beside.</summary>
        public void OnVehicleRemoved(uint netId)
        {
            ushort driver = 0;
            foreach (var kv in _drivenByPlayer)
                if (kv.Value == netId) { driver = kv.Key; break; }
            if (driver != 0) ServerExit(driver);
        }

        public void OnPeerDisconnected(ushort playerId) => ServerExit(playerId);

        /// <summary>Per-tick housekeeping (inside TickSimulation, after command dispatch + player step):
        /// the driver's avatar rides the vehicle (entity teleport, like SP's GlobalPosition follow), and a
        /// driver who died in the seat is force-exited.</summary>
        public void Step(long tick)
        {
            List<ushort> exits = null;
            foreach (var kv in _drivenByPlayer)
            {
                if (!_combat.IsAlive(kv.Key)) { (exits ??= new List<ushort>()).Add(kv.Key); continue; }
                if (_vehicles.TryGet(new NetId(kv.Value), out var v))
                    _players.ServerTeleport(kv.Key, v.Pos, tick);
            }
            if (exits != null) foreach (ushort p in exits) ServerExit(p);
        }
    }
}
