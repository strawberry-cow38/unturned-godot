using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Player vitals as an IReplicatedSystem (MP_VITALS_PLAN §5, SystemId 13, wire v8) -- OWNER-ONLY,
    /// mirroring the SkillsReplication shape verbatim: WriteFull/WriteDelta consult ctx.ClientPlayerId
    /// and emit at most ONE entry (the receiving client's own), so another player's food/infection never
    /// crosses the wire to you; observers keep the coarse alive/health view on SystemPlayerCombat.
    ///
    /// The server side never stores here -- ServerVitals is the authority and this system ENCODES from it
    /// at compose time (Server != null on the server, null on clients). The client side holds exactly one
    /// decoded replica (its own); ReadSnapshot is the only writer there. NOT in the EnableSyncCheck set:
    /// owner-only blocks differ per client by design (the Skills/Inventory precedent).
    ///
    /// Wire layout after the block header, 9 bytes when present (append-only -- new fields go after
    /// flags, new flags claim bits 3-7):
    ///   count:u8 (0|1), owner:u16, health:u8 (Ceiling 0..100), food:u8, water:u8, stamina:u8,
    ///   infection:u8 (all round(v*255)), flags:u8 (bit0 Bleeding, bit1 Broken, bit2 SurvivalDrain).
    /// </summary>
    public sealed class VitalsReplication : IReplicatedSystem
    {
        /// <summary>The client-side decoded replica (and the golden/hash unit): exactly what the wire
        /// carried. Broken here is HUD/heal state ONLY -- the shell's movement-gating Broken is computed
        /// locally from its own landing and only ever CLEARED off this flag (MP_VITALS_PLAN §10 risk 7).</summary>
        public sealed class Entry
        {
            public ushort OwnerPlayerId;
            public byte Health;                      // 0..100, the coarse-byte Ceiling convention
            public byte Food, Water, Stamina, Infection;   // quantized 0..255 over the sim's 0..1
            public byte Flags;

            public bool Bleeding => (Flags & ServerVitals.FlagBleeding) != 0;
            public bool Broken => (Flags & ServerVitals.FlagBroken) != 0;
            public bool SurvivalDrain => (Flags & ServerVitals.FlagSurvivalDrain) != 0;
        }

        public byte SystemId => ReplicationIds.SystemPlayerVitals;

        /// <summary>The authoritative source, set only on the SERVER (NetWorldServer wires it); null on
        /// client replicas.</summary>
        public ServerVitals Server;

        readonly Dictionary<ushort, Entry> _byOwner = new Dictionary<ushort, Entry>();

        public int Count => _byOwner.Count;
        public bool TryGet(ushort ownerPlayerId, out Entry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        // ---- IReplicatedSystem (owner-only: both paths write the SAME single-entry shape) ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteOwnerBlock(w, ctx.ClientPlayerId, always: true);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool dirty = Server != null && Server.TryGet(ctx.ClientPlayerId, out var e) && e.LastChangedTick > baselineTick;
            WriteOwnerBlock(w, ctx.ClientPlayerId, always: dirty);
        }

        void WriteOwnerBlock(NetPakWriter w, ushort clientPlayerId, bool always)
        {
            ServerVitals.Entry e = null;
            if (!always || Server == null || !Server.TryGet(clientPlayerId, out e)) { w.WriteUInt8(0); return; }
            w.WriteUInt8(1);
            w.WriteUInt16(e.OwnerPlayerId);
            w.WriteUInt8(ServerVitals.QuantizeHealth(e.Sim.Health));
            w.WriteUInt8(ServerVitals.Quantize01(e.Sim.Food));
            w.WriteUInt8(ServerVitals.Quantize01(e.Sim.Water));
            w.WriteUInt8(ServerVitals.Quantize01(e.Sim.Stamina));
            w.WriteUInt8(ServerVitals.Quantize01(e.Sim.Infection));
            w.WriteUInt8(Server.FlagsFor(e));
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt8(out byte count)) return;
            if (count == 0) return;   // owner-only: full snapshots simply re-state my entry; nothing to clear
            if (!r.ReadUInt16(out ushort owner)) return;
            if (!r.ReadUInt8(out byte health)) return;
            if (!r.ReadUInt8(out byte food)) return;
            if (!r.ReadUInt8(out byte water)) return;
            if (!r.ReadUInt8(out byte stamina)) return;
            if (!r.ReadUInt8(out byte infection)) return;
            if (!r.ReadUInt8(out byte flags)) return;
            if (!_byOwner.TryGetValue(owner, out var e))
            {
                e = new Entry { OwnerPlayerId = owner };
                _byOwner[owner] = e;
            }
            e.Health = health; e.Food = food; e.Water = water; e.Stamina = stamina; e.Infection = infection; e.Flags = flags;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            List<ushort> owners;
            if (Server != null) owners = Server.Owners();   // already sorted
            else { owners = new List<ushort>(_byOwner.Keys); owners.Sort(); }
            foreach (ushort id in owners) h = MixOwner(h, id);
            return h;
        }

        /// <summary>Owner-only parity: the server's hash of ONE player's ENCODED entry (the exact bytes
        /// the wire carries), comparable against that client's replica StateHash() -- which only ever
        /// contains its own entry.</summary>
        public ulong StateHashFor(ushort ownerPlayerId) => MixOwner(NetHash.FnvOffset, ownerPlayerId);

        ulong MixOwner(ulong h, ushort ownerPlayerId)
        {
            if (Server != null)
            {
                if (!Server.TryGet(ownerPlayerId, out var se)) return h;
                h = NetHash.MixUInt32(h, se.OwnerPlayerId);
                h = NetHash.MixByte(h, ServerVitals.QuantizeHealth(se.Sim.Health));
                h = NetHash.MixByte(h, ServerVitals.Quantize01(se.Sim.Food));
                h = NetHash.MixByte(h, ServerVitals.Quantize01(se.Sim.Water));
                h = NetHash.MixByte(h, ServerVitals.Quantize01(se.Sim.Stamina));
                h = NetHash.MixByte(h, ServerVitals.Quantize01(se.Sim.Infection));
                h = NetHash.MixByte(h, Server.FlagsFor(se));
                return h;
            }
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return h;
            h = NetHash.MixUInt32(h, e.OwnerPlayerId);
            h = NetHash.MixByte(h, e.Health);
            h = NetHash.MixByte(h, e.Food);
            h = NetHash.MixByte(h, e.Water);
            h = NetHash.MixByte(h, e.Stamina);
            h = NetHash.MixByte(h, e.Infection);
            h = NetHash.MixByte(h, e.Flags);
            return h;
        }
    }
}
