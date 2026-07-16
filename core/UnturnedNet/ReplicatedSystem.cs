using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Per-compose context (MP_PLAN §2.3): carried on every WriteFull/WriteDelta call from day one, even
    /// though every v1 system ignores ViewPos (§2.6's "hook now, policy later" -- v1 interest policy is
    /// AllRelevant plus owner-only blocks keyed on ClientPlayerId). Distance rings / relevancy cells slot in
    /// later by reading ViewPos without any protocol change.
    /// </summary>
    public readonly struct ReplicationContext
    {
        public readonly long ServerTick;
        public readonly ushort ClientPlayerId;
        public readonly Vector3 ViewPos;

        public ReplicationContext(long serverTick, ushort clientPlayerId, Vector3 viewPos)
        {
            ServerTick = serverTick;
            ClientPlayerId = clientPlayerId;
            ViewPos = viewPos;
        }
    }

    /// <summary>
    /// The snapshot plane's per-system contract (MP_PLAN §2.3), unchanged from the plan's sketch. SystemId
    /// is an explicit append-only registry (no reflection): once a system ships, its id is never reused or
    /// renumbered, even if the system is later retired. WriteFull is also the join/late-join/baseline-reset
    /// path -- there is no separate late-join resend mechanism per system. WriteDelta must only emit state
    /// that changed since baselineTick (an entity's own lastChangedTick > baselineTick), which is what makes
    /// per-client baselines and the 64-tick dirty ring (SnapshotComposer.DirtyRingDepthTicks) work.
    /// </summary>
    public interface IReplicatedSystem
    {
        byte SystemId { get; }

        void WriteFull(NetPakWriter w, in ReplicationContext ctx);

        void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick);

        /// <summary>Client side: read one block (produced by either WriteFull or WriteDelta -- full tells
        /// the reader whether to first clear any prior replica state).</summary>
        void ReadSnapshot(NetPakReader r, bool full);

        /// <summary>Test-only sync verification (MP_PLAN §6): must be order-independent (sort by NetId
        /// before folding, see NetHash) so server and client hashes agree regardless of dictionary iteration
        /// order.</summary>
        ulong StateHash();
    }
}
