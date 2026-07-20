using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>SP/MP-unify wave 2 (A1): world containers / store-shelves as replicated fixture entities
    /// (SystemId 14). RESERVATION STUB: registered EMPTY (composes a 0-count block, hashes empty) so the
    /// composer/applier slot and the v11 wire id exist for the coordinated bump. Owner: cow tools -- FILL
    /// THIS BODY (ContainerEntity{NetId,KindId,Pos,Yaw,W,H,DisplayCell[]}, ServerRegisterFixture/SetDisplay/
    /// Remove, relevancy interest, a diff-driven StorageReplicaView on the client) per the plan's A1 section.
    /// Cross of DeployableReplication + WorldItemReplication. NOT in EnableSyncCheck (relevancy-filtered).</summary>
    public sealed class ContainerReplication : IReplicatedSystem
    {
        public byte SystemId => ReplicationIds.SystemContainers;

        // ---- IReplicatedSystem (empty stub: one 0-count block, symmetric read, empty hash) ----
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => w.WriteUInt8(0);
        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick) => w.WriteUInt8(0);
        public void ReadSnapshot(NetPakReader r, bool full) { r.ReadUInt8(out _); }
        public ulong StateHash() => NetHash.FnvOffset;
    }
}
