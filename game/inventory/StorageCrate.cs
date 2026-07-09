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

            var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.6f, 0.6f) } };
            box.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.33f, 0.20f), Roughness = 0.85f };
            box.Position = new Vector3(0, 0.3f, 0);
            AddChild(box);

            var lid = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.84f, 0.08f, 0.64f) } };
            lid.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.25f, 0.15f) };
            lid.Position = new Vector3(0, 0.62f, 0);
            AddChild(lid);

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
