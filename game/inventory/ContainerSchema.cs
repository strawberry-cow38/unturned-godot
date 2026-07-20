using System.Collections.Generic;

namespace UnturnedGodot
{
    // MP (A1): the shared container KIND table -- a stable KindId <-> (mesh, display, label, grid dims) mapping the
    // server and client both derive from WorldBuilder's ContainerShelf registry (distinct kinds, mesh-sorted, so the
    // ids are order-independent). The ContainerReplication fixture carries only the KindId; each side resolves the
    // visual + the grid dims from here. Built once, lazily -- no world build required (the client never runs it).
    public static class ContainerSchema
    {
        public struct Kind
        {
            public ushort KindId;
            public string Mesh;
            public bool Display;      // true = OPEN-tier shelf (loot shown on tiers); false = solid F-open prop
            public string Label;
            public byte Width, Height;
        }

        static Kind[] _all;
        public static Kind[] All => _all ??= Build();

        static Kind[] Build()
        {
            var kinds = WorldBuilder.ContainerKinds();
            var arr = new Kind[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
            {
                var (mesh, display, label) = kinds[i];
                var (w, h) = StoreShelf.GridDims(mesh, display);
                arr[i] = new Kind { KindId = (ushort)i, Mesh = mesh, Display = display, Label = label, Width = w, Height = h };
            }
            return arr;
        }

        /// <summary>Resolve a manifest container to its stable KindId (falls back to 0 for an unknown kind).</summary>
        public static ushort KindFor(string mesh, bool display, string label)
        {
            foreach (var k in All) if (k.Mesh == mesh && k.Display == display && k.Label == label) return k.KindId;
            return 0;
        }

        public static Kind Get(ushort kindId) => (kindId < All.Length) ? All[kindId] : (All.Length > 0 ? All[0] : default);
    }
}
