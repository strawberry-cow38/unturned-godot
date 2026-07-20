using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>SP/MP-unify wave 2 (B5): owner-only fine vitals (food/water/stamina/infection) as an
    /// IReplicatedSystem (SystemId 13 -- resolves the long-reserved owner-vitals slot). RESERVATION STUB:
    /// registered EMPTY (composes a 0-count owner block, hashes empty) so the composer/applier slot and the
    /// v11 wire id exist for the coordinated bump; the real body (a per-player PlayerVitalsSim, server-side
    /// starvation routed through ServerCombat.DamagePlayerExternal, owner adoption) lands in the B5 commit.
    /// Modeled on SkillsReplication (the owner-only pilot). Owner: tinyclaw. NOT in EnableSyncCheck.</summary>
    public sealed class PlayerVitalsReplication : IReplicatedSystem
    {
        public byte SystemId => ReplicationIds.SystemVitals;

        // ---- IReplicatedSystem (empty stub: one 0-count block, symmetric read, empty hash) ----
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => w.WriteUInt8(0);
        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick) => w.WriteUInt8(0);
        public void ReadSnapshot(NetPakReader r, bool full) { r.ReadUInt8(out _); }
        public ulong StateHash() => NetHash.FnvOffset;
    }
}
