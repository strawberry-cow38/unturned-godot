using System;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    // Wire format v1 for the session layer (MP_PLAN §2.2 / §5 item 1). Every datagram starts with the
    // 83-bit packet header below; the version byte is the escape hatch for all future format changes.
    // Golden byte tests in tests/UnturnedNet.Tests lock this layout -- changing anything here must bump
    // Version and re-golden in the same commit.

    public enum NetChannel : byte
    {
        Control = 0,             // connect/accept/reject/disconnect/keepalive (keepalive doubles as ack carrier)
        ReliableOrdered = 1,     // msgId window + retransmit + fragmentation, delivered in order exactly once
        UnreliableSequenced = 2, // newest-seq-wins, stale datagrams dropped on the floor
    }

    public enum NetControlType : byte
    {
        Connect = 1,
        Accept = 2,
        Reject = 3,
        Disconnect = 4,
        KeepAlive = 5,
    }

    public enum NetRejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,
        ServerFull = 2,
        ContentMismatch = 3,   // Connect carried a content hash that isn't ours (Phase 4 join gate)
    }

    public enum NetDisconnectReason : byte
    {
        None = 0,
        Timeout = 1,   // ~5 s of silence
        Rejected = 2,  // server refused the handshake (see RejectReason)
        Kicked = 3,    // remote sent Disconnect
        Requested = 4, // local app asked
    }

    public enum NetSessionState : byte
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
    }

    public static class NetProtocol
    {
        public const byte Magic = 0x75; // 'u'
        public const byte Version = 13; // v13 (destructible-props): registers SystemDestructibles(16) -- the rubble alive-bitmap, one bit per placed destructible object keyed by deterministic placement index (the ResourceReplication(12) shape) -- plus EventObjectDestroyed(32)/EventObjectRestored(33) for break/respawn immediacy. Server owns health + Rubble_Reset respawn (ServerDestructibles); combat routes an object hit into it. Composed after Animals(15), included in EnableSyncCheck. v12 (fluid-fix): the inventory item wire (WriteJar/ReadJar) gains the gas-can FUEL LEVEL (WriteClampedFloat 12,2) -- the server fills a can at a pump but the owner-inventory echo dropped the level, so a filled can showed empty on the client ("can won't fill"). Also mixed into the inventory StateHash. v11 (mp-sp-unify wave 2): registers SystemVitals(13 -- resolves the long-reserved owner-vitals slot) + SystemContainers(14) + SystemAnimals(15) as EMPTY stubs (composer/applier slots so the ids exist; bodies land under this SAME v11 as they fill), and reserves CommandPickupDeployable(28)/CommandExtractFuel(29)/CommandAttachTow(30)/CommandDetachTow(31). ONE coordinated bump for the whole wave (never per-gap -- avoids the v8/v9-style launcher fragmentation the note below warns about); the new systems' wire bodies + vehicle-tow fields + combat-appearance block + PlayerStateCommand.HeldItemId re-golden under this v11 as each gap lands. v10 (mp-event-coalesce): PlayerStateCommand 27 carries a redundant list of recent combat events (Fire/Melee/Grenade/Reload) folded into the 50Hz unreliable transform stream + deduped server-side by a strictly-increasing combat seq; owner entity snapshot gains LastProcessedCombatSeq ack; the standalone ReliableOrdered CommandFire/Melee/Grenade/Reload 2-5 datagrams are no longer sent by the client (registrations kept dormant). Kills reliable-ordered HOL-block combat stutter. v9 (mp-clientauth-foot): on-foot client authority -- owner movement changes from an input stream the server simulates to a transform stream the server envelope-validates and adopts: new CommandPlayerState 27 (@50 Hz UnreliableSeq) + new EventPlayerRecov 31 (rollback of an out-of-envelope claim); MoveInput drops the C2 ClaimedPos/HasClaim claim fields (the ack band is gone); EventMisprediction 30 retired. NOTE: v8 is RESERVED by the pending owner-vitals branch (SystemId 13) -- do not reuse; coordinate the v8/v9 ordering at merge. v7 (mp-geomfix P3): Accept carries the server's activeHoliday string -- the client builds the SERVER's holiday world (the ~285 holiday-gated props carry colliders; each machine's local clock silently forked the collision set across a holiday boundary, invisible to the content hash); v6 (mp-predict-a A2): vehicle client authority -- new CommandVehicleState 26 (the predicted driver's reported transform @25 Hz UnreliableSeq, envelope-validated then adopted) + new EventVehicleRecov 29 (the retail rollback of an out-of-envelope driver); v5 (mp-predict-c C1+C2, one coordinated bump): MoveInput datagram = MoveInputPacket carrying the last 3 inputs redundantly, each entry carrying the shell's claimed post-move position (hasClaim:1 + position grid) for the server ack band; v4 (mp-exitfix): VehicleExitedEvent carries the authoritative exit spot (float32 x3); v3 (PEI client C2): MoveInput gained the buttons byte (bit 0 = jump); v2 (Phase 4) = Connect carries contentHash:u64; v1 = Phases 1-3

        /// <summary>Conservative internet-safe datagram budget (MP_PLAN §2.2): no session datagram exceeds this.</summary>
        public const int MaxDatagramBytes = 1200;

        // header layout: magic:8 + version:8 + channel:3 + seq:16 + ack:16 + ackBits:32
        public const int HeaderBits = 83;

        // ReliableOrdered fragment framing after the header: msgId:16 + fragIdx:8 + fragCount:8 + byteLen:16,
        // then AlignToByte + payload. 83+48 = 131 bits -> 17 bytes aligned -> 1183 payload bytes fit the budget.
        public const int MaxFragmentPayload = 1183;
        public const int MaxFragments = 255; // fragCount is 8 bits
        public const int MaxReliableMessageBytes = MaxFragmentPayload * MaxFragments; // ~301 kB

        // UnreliableSequenced framing after the header: byteLen:16, then AlignToByte + payload.
        // 83+16 = 99 bits -> 13 bytes aligned -> 1187 payload bytes. Bigger payloads are refused, not
        // fragmented: losing one fragment of an unreliable snapshot would waste the whole thing.
        public const int MaxUnreliablePayload = 1187;

        // Tick-based timing (the session never reads a wall clock; the driver ticks it at 50 Hz).
        public const int TicksPerSecond = 50;              // matches SimClock.FixedDelta = 0.02
        public const int KeepAliveIntervalTicks = 50;      // 1 Hz keepalive when idle
        public const int TimeoutTicks = 250;               // 5 s of silence = disconnect
        public const int ConnectRetryTicks = 25;           // re-send Connect every 0.5 s while connecting
        public const int ConnectTimeoutTicks = 250;        // give up connecting after 5 s
        public const int MinRtoTicks = 5;                  // RTO floor = 100 ms
        public const double RtoRttMultiplier = 1.5;        // RTO = max(floor, 1.5 x smoothed RTT)

        // Reliable windows. Sender admits new msgIds only while (newest - oldest unacked) < SendWindow,
        // which guarantees the receiver never sees a fragment beyond its (larger) reassembly window.
        public const int SendWindowMessages = 64;
        public const int RecvWindowMessages = 256;

        // Reassembly abuse guards (review M1): the receive window alone would let a peer that never
        // completes the head message pin ~77 MB of fragments (255 msgs x 254 frags x 1183 B). Legit
        // traffic never buffers more than one max-size message (join snapshot <= MaxReliableMessageBytes/2)
        // plus a window of small events, so a few x MaxReliableMessageBytes is generous; and a message a
        // peer can't complete within 10 s of RTO retransmits is dead anyway. Exceeding either marks the
        // session (NetSession.ReassemblyBudgetExceeded) -- the server kicks such peers.
        public const int MaxReassemblyBufferBytes = 4 * MaxReliableMessageBytes; // ~1.2 MB per peer
        public const int ReassemblyTtlTicks = 500;                               // 10 s to complete a message

        public struct Header
        {
            public byte MagicByte;
            public byte Version;
            public NetChannel Channel;
            public ushort Seq;     // per-datagram, connection-wide; 0 is reserved for "none" and never sent
            public ushort Ack;     // newest remote seq seen (0 = nothing received yet)
            public uint AckBits;   // bit n set => seq (Ack - 1 - n) was received
        }

        public static bool WriteHeader(NetPakWriter writer, in Header h)
        {
            bool ok = writer.WriteBits(h.MagicByte, 8);
            ok &= writer.WriteBits(h.Version, 8);
            ok &= writer.WriteBits((uint)h.Channel, 3);
            ok &= writer.WriteBits(h.Seq, 16);
            ok &= writer.WriteBits(h.Ack, 16);
            ok &= writer.WriteBits(h.AckBits, 32);
            return ok;
        }

        public static bool TryReadHeader(NetPakReader reader, out Header h)
        {
            h = default;
            if (!reader.ReadBits(8, out uint magic)) return false;
            if (!reader.ReadBits(8, out uint version)) return false;
            if (!reader.ReadBits(3, out uint channel)) return false;
            if (!reader.ReadBits(16, out uint seq)) return false;
            if (!reader.ReadBits(16, out uint ack)) return false;
            if (!reader.ReadBits(32, out uint ackBits)) return false;
            h = new Header
            {
                MagicByte = (byte)magic,
                Version = (byte)version,
                Channel = (NetChannel)channel,
                Seq = (ushort)seq,
                Ack = (ushort)ack,
                AckBits = ackBits,
            };
            return true;
        }
    }

    /// <summary>Serial (wrap-around) arithmetic for 16-bit sequence numbers and msgIds.</summary>
    public static class NetSeq
    {
        /// <summary>True if a is strictly after b in wrap-around order.</summary>
        public static bool IsNewer(ushort a, ushort b) => a != b && (ushort)(a - b) < 32768;

        public static bool IsNewerOrEqual(ushort a, ushort b) => a == b || IsNewer(a, b);

        /// <summary>Signed distance a-b in wrap-around order (positive when a is newer).</summary>
        public static int Diff(ushort a, ushort b)
        {
            int d = (ushort)(a - b);
            return d < 32768 ? d : d - 65536;
        }
    }
}
