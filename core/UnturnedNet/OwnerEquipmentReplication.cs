using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>Accepted held instance and source weapon clocks, only to its
    /// owner. Inventory fields remain in InventoryReplication; Token joins the
    /// selected instance to that atomic inventory/token view.</summary>
    public sealed record OwnerEquipmentState
    {
        public EquipmentAddress Held { get; init; }
        public ulong Token { get; init; }
        public ulong SelectionVersion { get; init; }
        public bool Reloading { get; init; }
        public bool Unloading { get; init; }
        public bool Rechambering { get; init; }
        public bool Broken { get; init; }
        public bool CanFire { get; init; }
        public ushort Ammo { get; init; }
        public ushort Capacity { get; init; }
        public bool Chambered { get; init; }
        public sbyte Firemode { get; init; }
        public int MagazineId { get; init; }
        public ushort ShellItemId { get; init; }
        public bool Silenced { get; init; }

        public void Write(NetPakWriter w)
        {
            Held.Write(w); w.WriteUInt64(Token); w.WriteUInt64(SelectionVersion);
            w.WriteBit(Reloading); w.WriteBit(Unloading); w.WriteBit(Rechambering); w.WriteBit(Broken); w.WriteBit(CanFire);
            w.WriteUInt16(Ammo); w.WriteUInt16(Capacity); w.WriteBit(Chambered); w.WriteInt8(Firemode);
            w.WriteInt32(MagazineId); w.WriteUInt16(ShellItemId); w.WriteBit(Silenced);
        }
        public static bool TryRead(NetPakReader r, out OwnerEquipmentState value)
        {
            value = null;
            if (!EquipmentAddress.TryRead(r, out var held) || !r.ReadUInt64(out ulong token) || !r.ReadUInt64(out ulong version)
                || !r.ReadBit(out bool reload) || !r.ReadBit(out bool unload) || !r.ReadBit(out bool rechamber)
                || !r.ReadBit(out bool broken) || !r.ReadBit(out bool canFire) || !r.ReadUInt16(out ushort ammo)
                || !r.ReadUInt16(out ushort capacity) || !r.ReadBit(out bool chambered) || !r.ReadInt8(out sbyte mode)
                || mode < -1 || mode > 3 || !r.ReadInt32(out int mag) || !r.ReadUInt16(out ushort shell)
                || !r.ReadBit(out bool silenced) || (token == 0) != (held.Id == 0)) return false;
            value = new() { Held = held, Token = token, SelectionVersion = version, Reloading = reload, Unloading = unload,
                Rechambering = rechamber, Broken = broken, CanFire = canFire, Ammo = ammo, Capacity = capacity,
                Chambered = chambered, Firemode = mode, MagazineId = mag, ShellItemId = shell, Silenced = silenced };
            return true;
        }
    }

    public sealed class OwnerEquipmentReplication : IReplicatedSystem
    {
        sealed record Entry(OwnerEquipmentState State, long Changed);
        readonly Dictionary<ushort, Entry> _owners = new();
        public byte SystemId => ReplicationIds.SystemOwnerEquipment;
        public int Count => _owners.Count;
        public bool TryGet(ushort owner, out OwnerEquipmentState value)
        {
            value = _owners.TryGetValue(owner, out var entry) ? entry.State : null;
            return value != null;
        }
        public void ServerSet(ushort owner, OwnerEquipmentState value, long tick)
        {
            if (value == null) { _owners.Remove(owner); return; }
            if (!_owners.TryGetValue(owner, out var previous) || previous.State != value)
                _owners[owner] = new(value, tick + 1);
        }
        public void ServerRemove(ushort owner) => _owners.Remove(owner);
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => Write(w, ctx.ClientPlayerId, long.MinValue);
        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick) => Write(w, ctx.ClientPlayerId, baselineTick);
        void Write(NetPakWriter w, ushort owner, long baseline)
        {
            bool include = _owners.TryGetValue(owner, out var entry) && entry.Changed > baseline;
            w.WriteBit(include); if (!include) return;
            w.WriteUInt16(owner); entry.State.Write(w);
        }
        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadBit(out bool present) || !present || !r.ReadUInt16(out ushort owner)
                || !OwnerEquipmentState.TryRead(r, out var state)) return;
            _owners[owner] = new(state, 0);
        }
        // Owner-only; deliberately excluded from global sync-check hashes.
        public ulong StateHash() => 0;
    }
}
