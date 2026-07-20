using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // ---------------------------------------------------------------------------------------------------
    // MP (A1): the SERVER bridge that stocks + publishes world-build containers as ContainerReplication
    // fixtures. At construction it walks the world-build manifest (WorldBuildResult.Containers): for each
    // one it mints a NetId, registers a crate grid in InventoryReplication (the authoritative contents),
    // rolls loot into it EXACTLY as an SP StoreShelf does (StoreShelf.RollInto -- one source of truth),
    // registers the ContainerReplication fixture (kind/pos/yaw/dims), and projects the display digest.
    // Tick() re-projects the digest only for a container whose grid actually changed (a player took/added
    // an item). Mirrors CropNetSync/WorldItemNetSync, but the fixtures are STATIC (from the manifest, not a
    // live node group), so there is NO server-side StoreShelf node -- the client materializes the visual
    // from the replica by KindId (StorageReplicaView). None of this runs on the SP direct path.
    // ---------------------------------------------------------------------------------------------------
    public sealed class ContainerNetSync
    {
        public const int DivisorTicks = 25;   // 2 Hz digest refresh (contents change only when a player edits an open grid)

        readonly NetWorldServer _server;
        readonly Node _host;

        struct Tracked { public uint NetId; public InventoryReplication.CrateEntry Crate; public ushort KindId; public ulong Sig; }
        readonly List<Tracked> _tracked = new();

        public ContainerNetSync(NetWorldServer server, Node host,
            List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)> manifest)
        {
            _server = server;
            _host = host;
            if (manifest != null) RegisterAll(manifest);
        }

        void RegisterAll(List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)> manifest)
        {
            long tick = _server.Session.CurrentTick;
            foreach (var c in manifest)
            {
                ushort kindId = ContainerSchema.KindFor(c.mesh, c.display, c.label);
                var (w, h) = StoreShelf.GridDims(c.mesh, c.display);
                var (min, max) = StoreShelf.LootCount(c.mesh);

                NetId id = _server.Ids.Mint();
                UnityEngine.Vector3 upos = ToU(c.pos);
                var crate = _server.Inventories.ServerRegisterCrate(id, w, h, upos);   // authoritative grid (InventoryReplication owns the contents)
                StoreShelf.RollInto(crate.Storage, min, max, c.table);                 // stock it identically to an SP StoreShelf

                _server.Containers.ServerRegisterFixture(id, kindId, upos, c.yaw, w, h, tick);
                _server.Containers.ServerSetDisplay(id.Value, ProjectDisplay(crate.Storage, c.display, w), tick);

                _tracked.Add(new Tracked { NetId = id.Value, Crate = crate, KindId = kindId, Sig = GridSig(crate.Storage) });
            }
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            if (tick % DivisorTicks != 0) return;
            for (int i = 0; i < _tracked.Count; i++)
            {
                var t = _tracked[i];
                ulong sig = GridSig(t.Crate.Storage);
                if (sig == t.Sig) continue;                                  // grid unchanged -> nothing to publish
                var kind = ContainerSchema.Get(t.KindId);
                _server.Containers.ServerSetDisplay(t.NetId, ProjectDisplay(t.Crate.Storage, kind.Display, kind.Width), tick);
                t.Sig = sig;
                _tracked[i] = t;
            }
        }

        // Godot world pos -> the net layer's UnityEngine.Vector3, DIRECT component copy (no Z-flip) -- the same
        // convention CropNetSync/WorldItemNetSync use for world fixtures, so the client materializes back cleanly.
        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        // The display digest: an OPEN-tier shelf emits one cell per grid item (linear index y*W+x, which fits a byte for
        // any 8x6-or-smaller grid). A solid prop shows nothing (loot stays hidden until F-open) -> empty digest.
        static ContainerDisplayCell[] ProjectDisplay(Items storage, bool display, byte width)
        {
            if (!display) return System.Array.Empty<ContainerDisplayCell>();
            var cells = new List<ContainerDisplayCell>();
            for (byte i = 0; i < storage.getItemCount(); i++)
            {
                var j = storage.getItem(i);
                if (j?.item == null) continue;
                cells.Add(new ContainerDisplayCell { Cell = (byte)(j.y * width + j.x), ItemId = j.item.id, Rot = j.rot });
            }
            return cells.ToArray();
        }

        // A cheap content signature so Tick() only republishes a digest when the grid actually changed.
        static ulong GridSig(Items storage)
        {
            ulong h = NetHash.FnvOffset;
            for (byte i = 0; i < storage.getItemCount(); i++)
            {
                var j = storage.getItem(i);
                if (j?.item == null) continue;
                h = NetHash.MixUInt32(h, j.item.id);
                h = NetHash.MixByte(h, j.x);
                h = NetHash.MixByte(h, j.y);
                h = NetHash.MixByte(h, j.rot);
            }
            return h;
        }
    }
}
