using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>SP/MP-unify wave 2 (A5): wildlife (deer/pig/cow) as replicated puppets (SystemId 15).
    /// RESERVATION STUB: registered EMPTY (composes a 0-count block, hashes empty) so the composer/applier
    /// slot and the v11 wire id exist for the coordinated bump. Owner: cow tools -- FILL THIS BODY (mirror
    /// the ZombieReplication/ZombieNetSync/ZombiePuppets stack: transform + anim byte + species @low Hz;
    /// host keeps the real body, remote clients get interpolated puppets) per the plan's A5 section. NOT in
    /// EnableSyncCheck (relevancy-filtered -- clients receive, never derive).</summary>
    public sealed class AnimalReplication : IReplicatedSystem
    {
        public byte SystemId => ReplicationIds.SystemAnimals;

        // ---- IReplicatedSystem (empty stub: one 0-count block, symmetric read, empty hash) ----
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => w.WriteUInt8(0);
        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick) => w.WriteUInt8(0);
        public void ReadSnapshot(NetPakReader r, bool full) { r.ReadUInt8(out _); }
        public ulong StateHash() => NetHash.FnvOffset;
    }
}
