using System;
using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>v70: select one original graph node. Identity comes from the
    /// authenticated command sender; neither a price nor an XP total is trusted.</summary>
    public struct LearnSkillNodeCommand
    {
        public const int MaxIdBytes = 63;
        public string NodeId;
        public static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdBytes) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }
        public void Write(NetPakWriter writer)
        {
            if (!ValidId(NodeId)) throw new ArgumentException("Skill node id must be 1..63 lowercase ASCII letters, digits or underscores");
            writer.WriteString(NodeId, 6);
        }
        public static bool TryRead(NetPakReader reader, out LearnSkillNodeCommand command)
        {
            command = default;
            if (!reader.ReadString(out string id, 6) || !ValidId(id)) return false;
            command.NodeId = id;
            return true;
        }
    }

    public sealed partial class SkillsReplication
    {
        public const int MaxLearnedNodes = 64;
        public static readonly SkillTree Tree = SkillTree.Placeholder();

        public bool CanLearn(ushort owner, string nodeId)
            => _byOwner.TryGetValue(owner, out var entry)
            && entry.Progress.Count < MaxLearnedNodes
            && entry.Progress.CanTake(Tree, Tree.NodeOf(nodeId), entry.Skills.experience, out _);

        public bool ServerTryLearn(ushort owner, string nodeId, long tick)
        {
            if (!CanLearn(owner, nodeId)) return false;
            var entry = _byOwner[owner];
            // Same source Take/TrySpend sequence, serialized on the authority
            // tick. CanTake reads this exact pool; no async mutation intervenes.
            uint cost = entry.Progress.Take(Tree, Tree.NodeOf(nodeId), entry.Skills.experience);
            if (cost == 0) return false;
            entry.Skills.TrySpend(cost);
            entry.LastChangedTick = Stamp(tick);
            return true;
        }

        public void ServerRestoreLearned(ushort owner, IEnumerable<string> learned, long tick)
        {
            if (!_byOwner.TryGetValue(owner, out var entry)) return;
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            if (learned != null)
                foreach (string id in learned)
                    if (LearnSkillNodeCommand.ValidId(id) && Tree.NodeOf(id) != null) ids.Add(id);
            if (ids.Count > MaxLearnedNodes) throw new InvalidOperationException("Source skill graph exceeds wire budget");
            entry.Progress.NetSetTaken(ids);
            entry.LastChangedTick = Stamp(tick);
        }

        static SortedSet<string> SortedLearned(SkillProgress progress)
            => new SortedSet<string>(progress.Taken, StringComparer.Ordinal);
        static void WriteLearned(NetPakWriter writer, SkillProgress progress)
        {
            var ids = SortedLearned(progress);
            if (ids.Count > MaxLearnedNodes) throw new InvalidOperationException("Learned graph exceeds wire budget");
            writer.WriteUInt8((byte)ids.Count);
            foreach (string id in ids) new LearnSkillNodeCommand { NodeId = id }.Write(writer);
        }
        static bool ReadLearned(NetPakReader reader, out List<string> learned)
        {
            learned = new List<string>();
            if (!reader.ReadUInt8(out byte count) || count > MaxLearnedNodes) return false;
            string previous = null;
            for (int i = 0; i < count; i++)
            {
                if (!LearnSkillNodeCommand.TryRead(reader, out var node)
                    || Tree.NodeOf(node.NodeId) == null
                    || (previous != null && StringComparer.Ordinal.Compare(previous, node.NodeId) >= 0)) return false;
                learned.Add(node.NodeId);
                previous = node.NodeId;
            }
            return true;
        }
    }
}
