using System;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>ScanNearbyItems' source 6x6 Items.tryAddItem, evaluated on private
    /// clones. Contributions retain exact world identity through source merging
    /// and currency normalization; opening a dashboard never edits world loot.</summary>
    public static class NearbyPacking
    {
        public const byte Side = 6;
        public const int MaxRows = Side * Side;
        public const int MaxContributions = 4096;
        public readonly record struct Source(uint WorldId, ulong Token, Item Item, Item Display = null);
        public readonly record struct Contribution(uint WorldId, ulong Token, ushort ItemId, ushort Amount);
        public sealed record Row(ItemJar Jar, Contribution[] Sources);
        public sealed record View(bool HasCandidates, Row[] Rows);

        public static View Pack(IEnumerable<Source> sources)
        {
            var page = new Items(PlayerInventory.AREA); page.loadSize(Side, Side);
            var members = new Dictionary<ItemJar, List<Contribution>>();
            bool found = false; int count = 0;
            foreach (var source in sources)
            {
                if (source.WorldId == 0 || source.Token == 0 || source.Item?.GetAsset() == null) continue;
                found = true;
                var before = page.items.ToDictionary(j => j, j => j.item.amount);
                page.tryAddItem((source.Display ?? source.Item).Clone());
                foreach (var jar in page.items)
                {
                    bool existed = before.TryGetValue(jar, out ushort amount);
                    int added = jar.item.amount - amount;
                    if (existed && added == 0) continue;
                    if (!members.TryGetValue(jar, out var parts)) members[jar] = parts = new();
                    if (++count > MaxContributions) throw new InvalidOperationException("Nearby contribution budget exceeded");
                    parts.Add(new(source.WorldId, source.Token, source.Item.id, checked((ushort)added)));
                }
            }
            return new(found, page.items.Select(j => new Row(j, members[j].ToArray())).ToArray());
        }
    }
}
