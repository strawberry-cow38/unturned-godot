using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    (ulong Hash, object View)? _skillTreeView;
    object ReadSkillTree()
    {
        ulong hash = _client.Skills.StateHashFor(_client.PlayerId);
        if (_skillTreeView is { } cached && cached.Hash == hash) return cached.View;
        _client.Skills.TryGet(_client.PlayerId, out var entry);
        var progress = entry?.Progress ?? new SkillProgress();
        uint xp = entry?.Skills.experience ?? 0;
        var tree = SkillsReplication.Tree;
        object result = new
        {
            experience = xp,
            pillars = tree.Pillars.Select(p => new
            {
                id = p.Id, title = p.Title, blurb = p.Blurb, advanced = p.Advanced,
                column = p.Column, requires = p.Requires, open = progress.PillarOpen(p)
            }).ToArray(),
            nodes = tree.Nodes.Select(n =>
            {
                bool can = progress.CanTake(tree, n, xp, out string why);
                return new
                {
                    id = n.Id, title = n.Title, blurb = n.Blurb, pillar = n.Pillar, tier = n.Tier,
                    cost = n.Cost, requires = n.Requires, grants = n.Grants.Select(g => g.Describe()).ToArray(),
                    learned = progress.Has(n.Id), canTake = can, reason = why ?? ""
                };
            }).ToArray()
        };
        _skillTreeView = (hash, result);
        return result;
    }
}
