using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Client -> server: spend XP to raise one skill (MP_PLAN §3.2). The server's own
    /// PlayerSkills.TryUpgrade is the validator -- cost/cap math never runs client-authoritatively.</summary>
    public struct UpgradeSkillCommand
    {
        public byte Speciality;   // EPlayerSpeciality (0..2)
        public byte Index;        // skill index within the speciality

        public void Write(NetPakWriter w) { w.WriteUInt8(Speciality); w.WriteUInt8(Index); }

        public static bool TryRead(NetPakReader r, out UpgradeSkillCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte spec)) return false;
            if (!r.ReadUInt8(out byte idx)) return false;
            cmd = new UpgradeSkillCommand { Speciality = spec, Index = idx };
            return true;
        }
    }

    /// <summary>Server -> owner: XP landed (kill/harvest/craft/console award) -- the HUD ping (§3.2).
    /// Total rides along so the HUD never waits on the next owner-block snapshot to show the new pool.</summary>
    public struct XpAwardedEvent
    {
        public uint Amount;
        public uint TotalExperience;

        public void Write(NetPakWriter w) { w.WriteUInt32(Amount); w.WriteUInt32(TotalExperience); }

        public static bool TryRead(NetPakReader r, out XpAwardedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint amount)) return false;
            if (!r.ReadUInt32(out uint total)) return false;
            evt = new XpAwardedEvent { Amount = amount, TotalExperience = total };
            return true;
        }
    }

    /// <summary>
    /// Skills as an IReplicatedSystem (MP_PLAN §3.2, SystemId 5) -- the OWNER-ONLY pilot: the first consumer
    /// of the §2.6 interest hook. WriteFull/WriteDelta consult ctx.ClientPlayerId and emit at most ONE entry:
    /// that client's own skills (experience:u32 + the 22 level bytes). Another player's XP/levels never cross
    /// the wire to you, which is both the privacy rule and the proof the per-client compose path works.
    ///
    /// Server side holds a real PlayerSkills per connected player (the same model SP plays on); the client
    /// side holds exactly one replica (its own). All mutation flows through Server* methods driven by the
    /// command plane -- ReadSnapshot is the only writer on a client.
    /// </summary>
    public sealed class SkillsReplication : IReplicatedSystem
    {
        public sealed class SkillsEntry
        {
            public ushort OwnerPlayerId;
            public PlayerSkills Skills = new PlayerSkills();
            public long LastChangedTick;
        }

        public byte SystemId => ReplicationIds.SystemSkills;

        readonly Dictionary<ushort, SkillsEntry> _byOwner = new Dictionary<ushort, SkillsEntry>();

        public int Count => _byOwner.Count;

        public bool TryGet(ushort ownerPlayerId, out SkillsEntry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        // ---- server side ----

        // see DeployableReplication.Stamp: mutations stamp one tick ahead so a change landing after this
        // tick's snapshot composed still beats the acked baseline (the compose-boundary off-by-one)
        static long Stamp(long tick) => tick + 1;

        public SkillsEntry ServerAdd(ushort ownerPlayerId, long tick)
        {
            var e = new SkillsEntry { OwnerPlayerId = ownerPlayerId, LastChangedTick = Stamp(tick) };
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId) => _byOwner.Remove(ownerPlayerId);

        /// <summary>Server-computed XP award (kill/harvest/craft hooks + the console verb). Returns the new
        /// total so the caller can put it in the XpAwarded event.</summary>
        public uint ServerAward(ushort ownerPlayerId, uint xp, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return 0;
            e.Skills.AwardExperience(xp);
            e.LastChangedTick = Stamp(tick);
            return e.Skills.experience;
        }

        /// <summary>The UpgradeSkill choke point: PlayerSkills.TryUpgrade IS the validation (level cap +
        /// XP cost); a false return mutates nothing.</summary>
        public bool ServerTryUpgrade(ushort ownerPlayerId, byte speciality, byte index, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;
            if (speciality >= PlayerSkills.SPECIALITIES) return false;
            if (index >= e.Skills.skills[speciality].Length) return false;
            if (!e.Skills.TryUpgrade(speciality, index)) return false;
            e.LastChangedTick = Stamp(tick);
            return true;
        }

        /// <summary>Console `skill <name> <level>` support: set a level directly (dev cheat, server-gated).</summary>
        public bool ServerSetSkillLevel(ushort ownerPlayerId, string name, int level, long tick, out string label, out byte applied)
        {
            label = null; applied = 0;
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;
            if (!e.Skills.TryFind(name, out var sk, out label)) return false;
            sk.level = (byte)(level < 0 ? 0 : (level > sk.max ? sk.max : level));
            applied = sk.level;
            e.LastChangedTick = Stamp(tick);
            return true;
        }

        // ---- IReplicatedSystem (owner-only: both paths write the SAME single-entry shape) ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteOwnerBlock(w, ctx.ClientPlayerId, always: true);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool dirty = _byOwner.TryGetValue(ctx.ClientPlayerId, out var e) && e.LastChangedTick > baselineTick;
            WriteOwnerBlock(w, ctx.ClientPlayerId, always: dirty);
        }

        void WriteOwnerBlock(NetPakWriter w, ushort clientPlayerId, bool always)
        {
            if (!always || !_byOwner.TryGetValue(clientPlayerId, out var e)) { w.WriteUInt8(0); return; }
            w.WriteUInt8(1);
            w.WriteUInt16(e.OwnerPlayerId);
            w.WriteUInt32(e.Skills.experience);
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var row = e.Skills.skills[s];
                for (int i = 0; i < row.Length; i++) w.WriteUInt8(row[i].level);
            }
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt8(out byte count)) return;
            if (count == 0) return;   // owner-only: full snapshots simply re-state my entry; nothing to clear
            if (!r.ReadUInt16(out ushort owner)) return;
            if (!r.ReadUInt32(out uint experience)) return;
            if (!_byOwner.TryGetValue(owner, out var e))
            {
                e = new SkillsEntry { OwnerPlayerId = owner };
                _byOwner[owner] = e;
            }
            e.Skills.NetSetExperience(experience);
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var row = e.Skills.skills[s];
                for (int i = 0; i < row.Length; i++)
                {
                    if (!r.ReadUInt8(out byte level)) return;
                    row[i].level = level;
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort id in owners) h = MixEntry(h, _byOwner[id]);
            return h;
        }

        /// <summary>Owner-only parity: the server's hash of ONE player's entry, comparable against that
        /// client's replica StateHash() (which only ever contains its own entry).</summary>
        public ulong StateHashFor(ushort ownerPlayerId)
        {
            ulong h = NetHash.FnvOffset;
            if (_byOwner.TryGetValue(ownerPlayerId, out var e)) h = MixEntry(h, e);
            return h;
        }

        static ulong MixEntry(ulong h, SkillsEntry e)
        {
            h = NetHash.MixUInt32(h, e.OwnerPlayerId);
            h = NetHash.MixUInt32(h, e.Skills.experience);
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var row = e.Skills.skills[s];
                for (int i = 0; i < row.Length; i++) h = NetHash.MixByte(h, row[i].level);
            }
            return h;
        }
    }
}
