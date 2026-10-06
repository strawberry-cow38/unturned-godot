using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // INDUSTRIAL ITEM PIPES (strawberry 2026-10-06: "new industrial pipe tool. new deployables, storage
    // adapter: connects to any storage container, has a pipe i/o input and output. 1-3 splitter and 3-1
    // industrial pipe combiners. item mover (industrial pipe i/o input, output, power i/o input,
    // passthrough.) moves items from whatevers connected on the input side to whatevers connected on the
    // output side when powered").
    //
    // The pipe GRAPH lives here and rides the deployables snapshot (SystemDeployables), the same way wires
    // do: the topology is server-owned, a change goes out as a reliable event, and a late joiner gets it in
    // the snapshot. What does NOT live here is the moving -- that is ServerItemMovers, server-only, because
    // the items themselves are in crate grids the client never holds. A pipe is a fact about the world; a
    // transfer is something the server does to two containers and repaints to whoever is looking.
    //
    // ⚠ ITEM PORTS ARE NOT POWER PORTS, and they are kept out of every power table on purpose. They have their
    // own array on the def (DeployableNetDef.ItemPorts), their own sub-address space (a pipe's port byte
    // indexes ItemPorts, never Ports), and they never reach PowerSolver. Folding them into PortKind would put
    // a pipe socket in front of the solver, and the solver reads every port it is handed.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Which way items cross an item port. Def-table only (never on the wire: a pipe carries the port
    /// INDEX, and both ends rebuild the direction from the same def), so the content hash is what keeps the two
    /// sides agreeing about it.</summary>
    public enum ItemPortDir : byte { In = 0, Out = 1 }

    /// <summary>What an item device DOES, which is all the mover's router needs to know about it. Def-table
    /// only, like FixtureKind.</summary>
    public enum ItemDeviceKind : byte { None = 0, Adapter = 1, Splitter = 2, Combiner = 3, Mover = 4 }

    /// <summary>How a splitter shares items between its outputs (strawberry: "allow configuring the splitter to
    /// distribute in various ways").
    ///
    /// ⚠ APPEND-ONLY. The byte crosses the wire in the configure command, the configured event and the
    /// deployable snapshot, and it is written into saves -- inserting a mode in the middle silently changes
    /// how every placed splitter in every existing world behaves.</summary>
    public enum SplitterMode : byte { RoundRobin = 0, Overflow = 1, Weighted = 2 }

    /// <summary>A splitter's or a mover's settings. One shape for both, because the wire and the save are
    /// simpler with one shape and the unused half is five bytes: a mover ignores Mode/Weights, a splitter
    /// ignores Rate.</summary>
    public sealed class ItemDeviceConfig
    {
        /// <summary>A splitter has three outputs and a combiner three inputs. Weights are indexed by output
        /// ordinal (0..2), not by port index -- the In port sits at index 0 on a splitter.</summary>
        public const int Ways = 3;
        /// <summary>Per-output weight ceiling ("weights 0-9" -- one digit you can read off a stepper).</summary>
        public const byte MaxWeight = 9;
        /// <summary>strawberry: "allow the items/s to be configured on the mover, 1/s - 32/s".</summary>
        public const byte MinRate = 1, MaxRate = 32;
        /// <summary>strawberry: "32 stack quantity per second" -- the default a mover comes down with.</summary>
        public const byte DefaultRate = 32;

        public SplitterMode Mode = SplitterMode.RoundRobin;
        public readonly byte[] Weights = { 1, 1, 1 };
        public byte Rate = DefaultRate;

        /// <summary>The server's range gate. A forged rate of 255 would be a mover that empties a container in
        /// a tick; a forged mode of 7 would be a splitter no build knows how to run.</summary>
        public static bool IsValid(byte mode, byte w0, byte w1, byte w2, byte rate)
            => mode <= (byte)SplitterMode.Weighted
               && w0 <= MaxWeight && w1 <= MaxWeight && w2 <= MaxWeight
               && rate >= MinRate && rate <= MaxRate;

        public static ItemDeviceConfig From(byte mode, byte w0, byte w1, byte w2, byte rate)
        {
            var c = new ItemDeviceConfig { Mode = (SplitterMode)mode, Rate = rate };
            c.Weights[0] = w0; c.Weights[1] = w1; c.Weights[2] = w2;
            return c;
        }

        public ItemDeviceConfig Clone() => From((byte)Mode, Weights[0], Weights[1], Weights[2], Rate);

        public bool SameAs(ItemDeviceConfig o) => o != null && o.Mode == Mode && o.Rate == Rate
            && o.Weights[0] == Weights[0] && o.Weights[1] == Weights[1] && o.Weights[2] == Weights[2];

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8((byte)Mode);
            w.WriteUInt8(Weights[0]); w.WriteUInt8(Weights[1]); w.WriteUInt8(Weights[2]);
            w.WriteUInt8(Rate);
        }

        public static bool TryRead(NetPakReader r, out ItemDeviceConfig c)
        {
            c = null;
            if (!r.ReadUInt8(out byte mode) || !r.ReadUInt8(out byte w0) || !r.ReadUInt8(out byte w1)
                || !r.ReadUInt8(out byte w2) || !r.ReadUInt8(out byte rate)) return false;
            c = From(mode, w0, w1, w2, rate);
            return true;
        }

        public ulong Mix(ulong h)
        {
            h = NetHash.MixByte(h, (byte)Mode);
            h = NetHash.MixByte(h, Weights[0]); h = NetHash.MixByte(h, Weights[1]); h = NetHash.MixByte(h, Weights[2]);
            return NetHash.MixByte(h, Rate);
        }
    }

    /// <summary>The engine-free rules a pipe has to satisfy. The client tool previews with the same numbers
    /// the server validates against, so "green on screen, refused by the server" can only mean the slack
    /// below, not two copies of a constant that drifted.</summary>
    public static class ItemPipeRules
    {
        /// <summary>Route nodes between the two ports -- the hose tool's budget (PlayerController.MaxHoseNodes),
        /// which strawberry accepted as "pipes feel like the hose tool".</summary>
        public const int MaxNodes = 20;
        /// <summary>Polyline length, port to port -- the hose tool's 40 m.</summary>
        public const float MaxLength = 40f;
        /// <summary>The server measures from each device's ORIGIN, not its port: it has the entity position,
        /// not the def's port offsets. Ports sit within ~0.5 m of the origin on every item device, so two
        /// metres of slack (one per end) keeps a pipe the client drew at 39.9 m from being refused for the
        /// difference between where the cube is and where the box is.</summary>
        public const float LengthSlack = 2f;
        /// <summary>How close the sender must be to the end it is connecting -- WireReach, the wire's number.
        ///
        /// ⚠ ONE END, NOT BOTH, which is where this departs from CanConnectWire. A wire checks the sender against
        /// BOTH endpoints at 16 m, which caps a connectable wire at 32 m even when the player stands exactly in
        /// the middle -- and a pipe is allowed 40. The player is standing at the In port when they click it, so
        /// that is the end the reach is about; the other end is bounded by MaxLength through the polyline.</summary>
        public const float Reach = DeployableReplication.WireReach;

        public static float PolylineLength(Vector3 from, IReadOnlyList<Vector3> nodes, Vector3 to)
        {
            float len = 0f;
            Vector3 prev = from;
            if (nodes != null)
                for (int i = 0; i < nodes.Count; i++) { len += (nodes[i] - prev).magnitude; prev = nodes[i]; }
            return len + (to - prev).magnitude;
        }

        public static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        internal static void WritePath(NetPakWriter w, Vector3[] path)
        {
            int n = path?.Length ?? 0;
            w.WriteUInt8((byte)n);
            for (int i = 0; i < n; i++) NetWire.WritePos(w, path[i]);
        }

        /// <summary>Refuses a count above MaxNodes BEFORE allocating: the length byte is attacker-controlled, and a
        /// path the server would reject anyway is not worth reading 255 positions for.</summary>
        internal static bool ReadPath(NetPakReader r, out Vector3[] path)
        {
            path = null;
            if (!r.ReadUInt8(out byte n) || n > MaxNodes) return false;
            path = new Vector3[n];
            for (int i = 0; i < n; i++)
                if (!NetWire.ReadPos(r, out path[i])) return false;
            return true;
        }
    }

    // ---- wire messages (hand-written Write/TryRead, the ConnectWireCommand pattern; ids in ReplicationIds) ----

    /// <summary>v56: run a pipe from an Out item port to an In item port, through the route nodes the player
    /// clicked. The nodes are WORLD points and they are the pipe's shape on every screen -- unlike a wire, which
    /// the server keeps as two endpoints and every client draws straight.</summary>
    public struct ConnectPipeCommand
    {
        public uint SrcId; public byte SrcPort;
        public uint DstId; public byte DstPort;
        public Vector3[] Path;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(SrcId); w.WriteUInt8(SrcPort); w.WriteUInt32(DstId); w.WriteUInt8(DstPort);
            ItemPipeRules.WritePath(w, Path);
        }

        public static bool TryRead(NetPakReader r, out ConnectPipeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint src) || !r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst) || !r.ReadUInt8(out byte dstPort)) return false;
            if (!ItemPipeRules.ReadPath(r, out var path)) return false;
            cmd = new ConnectPipeCommand { SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort, Path = path };
            return true;
        }
    }

    public struct RemovePipeCommand
    {
        public uint PipeId;
        public void Write(NetPakWriter w) => w.WriteUInt32(PipeId);
        public static bool TryRead(NetPakReader r, out RemovePipeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new RemovePipeCommand { PipeId = id };
            return true;
        }
    }

    /// <summary>v56: set a splitter's mode/weights or a mover's rate (the F panel). The client sends the whole
    /// config rather than a delta, so a dropped-and-retransmitted press cannot land twice as a double step.</summary>
    public struct ConfigureItemDeviceCommand
    {
        public uint NetId;
        public byte Mode, W0, W1, W2, Rate;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId);
            w.WriteUInt8(Mode); w.WriteUInt8(W0); w.WriteUInt8(W1); w.WriteUInt8(W2); w.WriteUInt8(Rate);
        }

        public static bool TryRead(NetPakReader r, out ConfigureItemDeviceCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt8(out byte m) || !r.ReadUInt8(out byte w0) || !r.ReadUInt8(out byte w1)
                || !r.ReadUInt8(out byte w2) || !r.ReadUInt8(out byte rate)) return false;
            cmd = new ConfigureItemDeviceCommand { NetId = id, Mode = m, W0 = w0, W1 = w1, W2 = w2, Rate = rate };
            return true;
        }
    }

    public struct PipeConnectedEvent
    {
        public uint PipeId;
        public uint SrcId; public byte SrcPort;
        public uint DstId; public byte DstPort;
        public Vector3[] Path;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(PipeId);
            w.WriteUInt32(SrcId); w.WriteUInt8(SrcPort); w.WriteUInt32(DstId); w.WriteUInt8(DstPort);
            ItemPipeRules.WritePath(w, Path);
        }

        public static bool TryRead(NetPakReader r, out PipeConnectedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt32(out uint src) || !r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst) || !r.ReadUInt8(out byte dstPort)) return false;
            if (!ItemPipeRules.ReadPath(r, out var path)) return false;
            evt = new PipeConnectedEvent { PipeId = id, SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort, Path = path };
            return true;
        }
    }

    public struct PipeRemovedEvent
    {
        public uint PipeId;
        public void Write(NetPakWriter w) => w.WriteUInt32(PipeId);
        public static bool TryRead(NetPakReader r, out PipeRemovedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new PipeRemovedEvent { PipeId = id };
            return true;
        }
    }

    public struct ItemDeviceConfiguredEvent
    {
        public uint NetId;
        public ItemDeviceConfig Config;

        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); Config.Write(w); }

        public static bool TryRead(NetPakReader r, out ItemDeviceConfiguredEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id) || !ItemDeviceConfig.TryRead(r, out var cfg)) return false;
            evt = new ItemDeviceConfiguredEvent { NetId = id, Config = cfg };
            return true;
        }
    }

    public sealed class ItemPipeEntity
    {
        public uint NetIdValue;
        public uint SrcId; public byte SrcPort;   // an Out item port (index into the def's ItemPorts)
        public uint DstId; public byte DstPort;   // an In item port
        /// <summary>The route nodes BETWEEN the two ports, quantized at store like every other position the
        /// server keeps, so a replica that round-tripped them through the wire hashes equal.</summary>
        public Vector3[] Path = System.Array.Empty<Vector3>();
        public long LastChangedTick;
    }

    /// <summary>
    /// The pipe registry. Owned by DeployableReplication and written INSIDE its snapshot block, after the
    /// wires, rather than being a system of its own: a pipe cannot outlive either deployable it connects, and
    /// keeping both in one block means the removal cascade and the join snapshot cannot disagree about which
    /// pipes exist. Same mutation-stamp and tombstone rules as the wire registry beside it.
    /// </summary>
    public sealed class ItemPipeGraph
    {
        readonly NetEntityRegistry<ItemPipeEntity> _pipes = new NetEntityRegistry<ItemPipeEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();
        // (deployable, item-port) -> the pipe on it. One pipe per port is a RULE (CanConnectPipe), so this is a
        // map rather than a multimap -- and the router asks it several times per mover per tick.
        readonly Dictionary<(uint, byte), uint> _byPort = new Dictionary<(uint, byte), uint>();

        public int Count => _pipes.Count;

        public bool TryGet(uint pipeId, out ItemPipeEntity p) => _pipes.TryGet(new NetId(pipeId), out p);

        public IEnumerable<ItemPipeEntity> All
        {
            get
            {
                foreach (uint id in _pipes.SortedIdValues())
                {
                    _pipes.TryGet(new NetId(id), out var p);
                    yield return p;
                }
            }
        }

        public bool IsPortPiped(uint netId, byte port) => _byPort.ContainsKey((netId, port));

        public bool TryGetOnPort(uint netId, byte port, out ItemPipeEntity p)
        {
            p = null;
            return _byPort.TryGetValue((netId, port), out uint id) && _pipes.TryGet(new NetId(id), out p);
        }

        static long Stamp(long tick) => tick + 1;   // see DeployableReplication.Stamp

        public ItemPipeEntity ServerConnect(NetId id, uint srcId, byte srcPort, uint dstId, byte dstPort, Vector3[] path, long tick)
        {
            var q = new Vector3[path?.Length ?? 0];
            for (int i = 0; i < q.Length; i++) q[i] = PlayerReplication.Quantize(path[i]);
            var p = new ItemPipeEntity
            {
                NetIdValue = id.Value, SrcId = srcId, SrcPort = srcPort, DstId = dstId, DstPort = dstPort,
                Path = q, LastChangedTick = Stamp(tick),
            };
            Add(p);
            _removedAtTick.Remove(id.Value);
            return p;
        }

        void Add(ItemPipeEntity p)
        {
            if (_pipes.TryGet(new NetId(p.NetIdValue), out var old)) Unindex(old);   // a re-read replaces, it must not leave the old ends indexed
            _pipes.Add(new NetId(p.NetIdValue), p);
            _byPort[(p.SrcId, p.SrcPort)] = p.NetIdValue;
            _byPort[(p.DstId, p.DstPort)] = p.NetIdValue;
        }

        void Unindex(ItemPipeEntity p)
        {
            if (_byPort.TryGetValue((p.SrcId, p.SrcPort), out uint a) && a == p.NetIdValue) _byPort.Remove((p.SrcId, p.SrcPort));
            if (_byPort.TryGetValue((p.DstId, p.DstPort), out uint b) && b == p.NetIdValue) _byPort.Remove((p.DstId, p.DstPort));
        }

        bool RemoveNoTombstone(uint pipeId)
        {
            if (!_pipes.TryGet(new NetId(pipeId), out var p)) return false;
            Unindex(p);
            return _pipes.Remove(new NetId(pipeId));
        }

        public bool ServerRemove(uint pipeId, long tick)
        {
            if (!RemoveNoTombstone(pipeId)) return false;
            _removedAtTick[pipeId] = Stamp(tick);
            return true;
        }

        /// <summary>Drop every pipe touching a deployable that is going away (salvage, pickup) and return their
        /// ids so the host can broadcast the removals -- the wire cascade's shape.</summary>
        public List<uint> ServerRemoveAllOn(uint netId, long tick)
        {
            var gone = new List<uint>();
            foreach (var p in All)
                if (p.SrcId == netId || p.DstId == netId) gone.Add(p.NetIdValue);
            foreach (uint id in gone) ServerRemove(id, tick);
            return gone;
        }

        public void Clear()
        {
            foreach (var p in new List<ItemPipeEntity>(All)) Unindex(p);
            foreach (uint id in new List<uint>(_pipes.SortedIdValues())) _pipes.Remove(new NetId(id));
        }

        // ---- client-side event application (idempotent, like the wire events) ----

        public void ApplyConnected(in PipeConnectedEvent evt, long tick)
        {
            if (TryGet(evt.PipeId, out _)) return;
            ServerConnect(new NetId(evt.PipeId), evt.SrcId, evt.SrcPort, evt.DstId, evt.DstPort, evt.Path, tick);
        }

        public void ApplyRemoved(in PipeRemovedEvent evt, long tick) => ServerRemove(evt.PipeId, tick);

        // ---- snapshot block (written by DeployableReplication after its wires) ----

        internal void WriteFull(NetPakWriter w)
        {
            var ids = _pipes.SortedIdValues();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids) { _pipes.TryGet(new NetId(id), out var p); WritePipe(w, p); }
        }

        internal void WriteDelta(NetPakWriter w, long baselineTick, long serverTick)
        {
            var changed = new List<uint>();
            foreach (uint id in _pipes.SortedIdValues())
            {
                _pipes.TryGet(new NetId(id), out var p);
                if (p.LastChangedTick > baselineTick) changed.Add(id);
            }
            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed) { _pipes.TryGet(new NetId(id), out var p); WritePipe(w, p); }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick) if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);
            List<uint> stale = null;
            foreach (var kv in _removedAtTick)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks) (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) _removedAtTick.Remove(id);
        }

        internal bool Read(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return false;
            if (full) Clear();
            for (int i = 0; i < count; i++)
            {
                if (!ReadPipe(r, out var p)) return false;
                Add(p);
            }
            if (full) return true;
            if (!r.ReadUInt16(out ushort removedCount)) return false;
            for (int i = 0; i < removedCount; i++)
            {
                if (!r.ReadUInt32(out uint id)) return false;
                RemoveNoTombstone(id);
            }
            return true;
        }

        internal ulong Mix(ulong h)
        {
            foreach (var p in All)
            {
                h = NetHash.MixUInt32(h, p.NetIdValue);
                h = NetHash.MixUInt32(h, p.SrcId); h = NetHash.MixByte(h, p.SrcPort);
                h = NetHash.MixUInt32(h, p.DstId); h = NetHash.MixByte(h, p.DstPort);
                h = NetHash.MixUInt32(h, (uint)p.Path.Length);
                foreach (var v in p.Path) { h = NetHash.MixFloat(h, v.x); h = NetHash.MixFloat(h, v.y); h = NetHash.MixFloat(h, v.z); }
            }
            return h;
        }

        static void WritePipe(NetPakWriter w, ItemPipeEntity p)
        {
            w.WriteUInt32(p.NetIdValue);
            w.WriteUInt32(p.SrcId); w.WriteUInt8(p.SrcPort);
            w.WriteUInt32(p.DstId); w.WriteUInt8(p.DstPort);
            ItemPipeRules.WritePath(w, p.Path);
        }

        static bool ReadPipe(NetPakReader r, out ItemPipeEntity p)
        {
            p = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt32(out uint src) || !r.ReadUInt8(out byte srcPort)) return false;
            if (!r.ReadUInt32(out uint dst) || !r.ReadUInt8(out byte dstPort)) return false;
            if (!ItemPipeRules.ReadPath(r, out var path)) return false;
            p = new ItemPipeEntity { NetIdValue = id, SrcId = src, SrcPort = srcPort, DstId = dst, DstPort = dstPort, Path = path };
            return true;
        }
    }
}
