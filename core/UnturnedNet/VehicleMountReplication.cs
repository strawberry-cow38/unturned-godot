using System;
using System.Collections.Generic;
using System.Linq;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    public readonly record struct VehicleMountState(float Yaw, float Pitch, ushort Ammo, float CooldownSeconds, bool CrewAlive)
    {
        public bool IsValid => float.IsFinite(Yaw) && Yaw >= -180 && Yaw <= 180
            && float.IsFinite(Pitch) && Pitch >= -180 && Pitch <= 180
            && float.IsFinite(CooldownSeconds) && CooldownSeconds >= 0 && CooldownSeconds <= 60;
    }
    public sealed record VehicleMountsState(uint VehicleNetId, VehicleMountState[] Mounts, bool HeadOut, float GunnerRise = 0);

    /// <summary>Source mounts in original definition order. Atomic row deltas and tombstones
    /// keep removal/late join consistent; no client simulation or ammo writes.</summary>
    public sealed class VehicleMountReplication : IReplicatedSystem
    {
        public const int MaxMounts = 16, MaxVehicles = 256;
        readonly SortedDictionary<uint, VehicleMountsState> _vehicles = new();
        readonly Dictionary<uint,long> _changed = new(), _removed = new();
        long _fullAfter = -1;
        public byte SystemId => ReplicationIds.SystemVehicleMounts;
        public IEnumerable<VehicleMountsState> All => _vehicles.Values;
        public bool TryGet(uint id, out VehicleMountsState value) => _vehicles.TryGetValue(id, out value);
        public void ServerSet(VehicleMountsState value, long tick)
        {
            if (value.VehicleNetId == 0 || value.Mounts == null || value.Mounts.Length is < 1 or > MaxMounts
                || !float.IsFinite(value.GunnerRise) || value.GunnerRise < 0 || value.GunnerRise > 1
                || value.Mounts.Any(m => !m.IsValid)) throw new ArgumentException("Invalid vehicle mounts");
            if (!_vehicles.ContainsKey(value.VehicleNetId) && _vehicles.Count == MaxVehicles) throw new InvalidOperationException("Mounted vehicle limit");
            if (_vehicles.TryGetValue(value.VehicleNetId, out var old) && old.HeadOut == value.HeadOut && old.GunnerRise == value.GunnerRise && old.Mounts.SequenceEqual(value.Mounts)) return;
            _vehicles[value.VehicleNetId] = value with { Mounts = value.Mounts.ToArray() }; _changed[value.VehicleNetId] = tick; _removed.Remove(value.VehicleNetId);
        }
        public void ServerRemove(uint id, long tick)
        {
            if (!_vehicles.Remove(id)) return;
            _changed.Remove(id); _removed[id]=tick;
            // More removals than live capacity can accumulate inside one dirty
            // window. Bound their storage and replace the whole view for older
            // baselines instead of emitting an unreadable/unbounded tombstone list.
            if(_removed.Count>MaxVehicles){_removed.Clear();_fullAfter=tick;}
        }
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) { w.WriteBit(true); WriteRows(w, _vehicles.Values); }
        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            if(baselineTick<_fullAfter){WriteFull(w,ctx);return;}
            w.WriteBit(false);
            WriteRows(w, _vehicles.Values.Where(v => _changed[v.VehicleNetId] > baselineTick));
            var removed = _removed.Where(p => p.Value > baselineTick).OrderBy(p => p.Key).Select(p => p.Key).ToArray();
            w.WriteUInt16((ushort)removed.Length); foreach(var id in removed) w.WriteUInt32(id);
            long tick=ctx.ServerTick;
            foreach(var id in _removed.Where(p => tick - p.Value > SnapshotComposer.DirtyRingDepthTicks).Select(p => p.Key).ToArray()) _removed.Remove(id);
        }
        static void WriteRows(NetPakWriter w, IEnumerable<VehicleMountsState> values)
        {
            var rows=values.ToArray();w.WriteUInt16((ushort)rows.Length);
            foreach (var row in rows) {
                w.WriteUInt32(row.VehicleNetId); w.WriteUInt8((byte)row.Mounts.Length); w.WriteBit(row.HeadOut); w.WriteFloat(row.GunnerRise);
                foreach (var mount in row.Mounts) {
                    w.WriteFloat(mount.Yaw); w.WriteFloat(mount.Pitch); w.WriteUInt16(mount.Ammo);
                    w.WriteFloat(mount.CooldownSeconds); w.WriteBit(mount.CrewAlive);
                }
            }
        }
        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if(!r.ReadBit(out bool replacement)||full&&!replacement)return;
            full=replacement;
            if (!r.ReadUInt16(out ushort count) || count > MaxVehicles) return;
            var next = new SortedDictionary<uint, VehicleMountsState>();
            for (int n = 0; n < count; n++) {
                if (!r.ReadUInt32(out uint id) || id == 0 || next.ContainsKey(id)
                    || !r.ReadUInt8(out byte size) || size is < 1 or > MaxMounts || !r.ReadBit(out bool head)
                    || !r.ReadFloat(out float rise) || !float.IsFinite(rise) || rise < 0 || rise > 1) return;
                var mounts = new VehicleMountState[size];
                for (int i = 0; i < size; i++) {
                    if (!r.ReadFloat(out float yaw) || !r.ReadFloat(out float pitch) || !r.ReadUInt16(out ushort ammo)
                        || !r.ReadFloat(out float cooldown) || !r.ReadBit(out bool crew)) return;
                    mounts[i] = new(yaw, pitch, ammo, cooldown, crew);
                    if (!mounts[i].IsValid) return;
                }
                next[id] = new(id, mounts, head, rise);
            }
            var removed=new HashSet<uint>();
            if(!full) {
                if(!r.ReadUInt16(out ushort removeCount)||removeCount>MaxVehicles)return;
                for(int i=0;i<removeCount;i++)if(!r.ReadUInt32(out uint id)||id==0||next.ContainsKey(id)||!removed.Add(id))return;
            }
            int retained=full?0:_vehicles.Keys.Count(id=>!removed.Contains(id)&&!next.ContainsKey(id));
            if(retained+next.Count>MaxVehicles)return;
            if(full)_vehicles.Clear();
            foreach(var id in removed)_vehicles.Remove(id);
            foreach(var pair in next)_vehicles[pair.Key]=pair.Value;
        }
        public ulong StateHash()
        {
            ulong hash = NetHash.MixUInt32(NetHash.FnvOffset, (uint)_vehicles.Count);
            foreach (var row in _vehicles.Values) {
                hash = NetHash.MixUInt32(hash, row.VehicleNetId); hash = NetHash.MixUInt32(hash, row.HeadOut ? 1u : 0);
                hash = NetHash.MixUInt32(hash, BitConverter.SingleToUInt32Bits(row.GunnerRise));
                hash = NetHash.MixUInt32(hash, (uint)row.Mounts.Length);
                foreach (var m in row.Mounts) {
                    hash = NetHash.MixUInt32(hash, BitConverter.SingleToUInt32Bits(m.Yaw));
                    hash = NetHash.MixUInt32(hash, BitConverter.SingleToUInt32Bits(m.Pitch));
                    hash = NetHash.MixUInt32(hash, m.Ammo); hash = NetHash.MixUInt32(hash, BitConverter.SingleToUInt32Bits(m.CooldownSeconds));
                    hash = NetHash.MixUInt32(hash, m.CrewAlive ? 1u : 0);
                }
            }
            return hash;
        }
    }
}
