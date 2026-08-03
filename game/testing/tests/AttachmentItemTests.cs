using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // "irons are now items but they cant be moved in the inventory and disappear when dropped" (strawberry).
    //
    // Detaching a sight is the first time an attachment has ever existed as a loose item in this port, so its
    // inventory and world behaviour had never been exercised by anything. This drives both halves of the report on
    // the real item the T-menu hands back, rather than reasoning about which layer is at fault.
    public sealed class AttachmentItemTests : GameTest
    {
        public override string Name => "gun.attachment_item_behaves";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);

            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(2);
            p.Inventory.wearBackpack(new Item(253));

            var asset = Assets.find(5);   // Eaglefire Iron Sights, the item a detach hands back
            T.Check($"the irons item resolves ({asset?.itemName})", asset != null);
            T.Check($"...with a real grid footprint ({asset.size_x}x{asset.size_y})", asset.size_x > 0 && asset.size_y > 0);

            // ---- 1. IT GOES IN. Exactly what AttachmentMenu's detach does.
            bool added = p.Inventory.tryAddItem(new Item(5));
            T.Check("a detached sight goes into the bag", added);

            // find where it landed
            byte page = 255, gx = 0, gy = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2) && page == 255; b++)
            {
                var pg = p.Inventory.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item?.id == 5) { page = b; gx = j.x; gy = j.y; break; }
                }
            }
            T.Check($"...and is findable in the grid (page {page} at {gx},{gy})", page != 255);

            // ---- 2. IT MOVES. TryDrag is the model behind the inventory drag; if this refuses, the UI can't move it
            // however the mouse behaves.
            var pg2 = p.Inventory.items[page];
            byte before = pg2.getItemCount();
            bool moved = false;
            for (byte ty = 0; ty < 4 && !moved; ty++)
                for (byte tx = 0; tx < 4 && !moved; tx++)
                {
                    if (tx == gx && ty == gy) continue;
                    if (pg2.getIndex(tx, ty) != byte.MaxValue) continue;      // occupied
                    moved = p.Inventory.TryDrag(page, gx, gy, page, tx, ty, 0);
                }
            T.Check("a loose sight can be dragged to another cell", moved);
            T.Check($"...without duplicating or vanishing ({before} -> {pg2.getItemCount()})", pg2.getItemCount() == before);

            // ---- 3. IT SURVIVES BEING DROPPED. The report says it disappears, so this asserts the world item
            // actually exists afterwards -- a drop that spawns nothing destroys the item outright.
            int worldBefore = World.GetTree().GetNodesInGroup("worlditems").Count;
            p.DropWorldItem(new Item(5), p.GlobalPosition + Vector3.Forward);
            yield return Ticks(4);
            int worldAfter = World.GetTree().GetNodesInGroup("worlditems").Count;
            T.Check($"a dropped sight exists in the world ({worldBefore} -> {worldAfter})", worldAfter == worldBefore + 1);

            WorldItem spawned = null;
            foreach (var n in World.GetTree().GetNodesInGroup("worlditems"))
                if (n is WorldItem w && w.Item?.id == 5) { spawned = w; break; }
            T.Check("...and it is the sight, carrying its id", spawned != null);
            if (spawned != null)
                T.Check($"...and did not fall through the floor ({spawned.GlobalPosition.Y:F2})", spawned.GlobalPosition.Y > -1f);

            // A gun drops fine today, so it is the control: if the gun passes and the sight fails, the fault is
            // specific to attachments rather than to dropping.
            p.DropWorldItem(new Item(4), p.GlobalPosition + Vector3.Right);
            yield return Ticks(4);
            bool gunDropped = false;
            foreach (var n in World.GetTree().GetNodesInGroup("worlditems"))
                if (n is WorldItem w && w.Item?.id == 4) { gunDropped = true; break; }
            T.Check("control: a dropped GUN exists in the world too", gunDropped);

            // ---- 4. "DISAPPEAR" may mean INVISIBLE rather than deleted. The node existing proves nothing to a
            // player: an attachment with no ripped drop model renders nothing and is indistinguishable from gone.
            int meshes = 0, visible = 0;
            if (spawned != null)
                foreach (var n in spawned.GetChildren())
                    if (n is MeshInstance3D mi) { meshes++; if (mi.Visible && mi.Mesh != null) visible++; }
            T.Check($"the dropped sight actually RENDERS something ({visible} visible mesh(es) of {meshes})", visible > 0);

            // ---- 5. WHERE it lands with NO BACKPACK. tryAddItem starts at page SLOTS and takes the first page with
            // room; if that is a page the dashboard does not draw, the item is in the bag but unreachable -- which
            // presents exactly as "can't be moved".
            var bare = new PlayerInventory();
            bool bareAdd = bare.tryAddItem(new Item(5));
            byte barePage = 255;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2) && barePage == 255; b++)
            {
                var pg3 = bare.items[b];
                if (pg3 == null) continue;
                for (byte i = 0; i < pg3.getItemCount(); i++) if (pg3.getItem(i)?.item?.id == 5) { barePage = b; break; }
            }
            T.Check($"with no backpack a detached sight still finds a home (added={bareAdd}, page {barePage})", bareAdd && barePage != 255);
        }
    }
}
