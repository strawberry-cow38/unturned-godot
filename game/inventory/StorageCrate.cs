using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A deployable storage container in the world (bounded port of InteractableStorage): a crate you walk up to and
    // open (F) to reveal its own item grid in the dashboard, drag items in/out, then close. Its contents live in an
    // Items page; opening loads them into the player's STORAGE page (7) so the existing dashboard + TryDrag handle it,
    // closing saves them back. In the "crates" group for proximity interaction.
    public partial class StorageCrate : Node3D
    {
        public Items Storage;   // this crate's own grid (independent of the player)
        public byte Width = 5, Height = 4;

        public static StorageCrate Spawn(Node parent, Vector3 pos, byte w = 5, byte h = 4)
        {
            var c = new StorageCrate { Width = w, Height = h };
            parent.AddChild(c);
            c.GlobalPosition = pos;
            return c;
        }

        public override void _Ready()
        {
            AddToGroup("crates");
            Storage = new Items(PlayerInventory.STORAGE);
            Storage.loadSize(Width, Height);

            // a plain wooden crate (no pop-off lid -- that read as a Steam gamble/mystery box). Unturned's storage
            // Crate is a simple wooden cube you deploy as a barricade.
            var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.75f, 0.75f, 0.75f) } };
            box.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.37f, 0.21f), Roughness = 0.9f };
            box.Position = new Vector3(0, 0.375f, 0);
            AddChild(box);

            var label = new Label3D
            {
                Text = "Crate",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.85f, 0.75f, 0.55f),
                PixelSize = 0.006f, Position = new Vector3(0, 0.95f, 0),
                NoDepthTest = true, FontSize = 56, OutlineSize = 10,
            };
            AddChild(label);
        }

        // seed the crate with an item (for demo/loot)
        public void Add(Item item) => Storage.tryAddItem(item);
    }
}
