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
        public uint NetId = 0;   // A1 (MP): the server ContainerReplication entity this materialized crate mirrors (0 = SP-local); the F-open request addresses the server by this (B9)
        public virtual bool Preserves => false;   // a fridge overrides this -> its contents are skipped by the daily food-spoilage sweep (PlayerController.FoodSpoilTick)

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
            BuildVisual();
        }

        // the container's world appearance. Base = a plain wooden crate; subclasses (StoreShelf) draw their own prop
        // + display their contents. Called from _Ready after the Storage grid exists.
        protected virtual void BuildVisual()
        {
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

    // A refrigerator: a powered storage crate. Food inside DOESN'T spoil while the town grid is live
    // (PowerNet.GlobalPower) -- cut the grid (toggleGlobalPower / a power-out) and it warms up, its food spoiling
    // like any crate again. Source: an InteractableStorage on a fridge that draws power to preserve.
    public partial class Refrigerator : StorageCrate
    {
        public override bool Preserves => PowerNet.GlobalPower;   // preserves only while powered by the town mains

        public static new Refrigerator Spawn(Node parent, Vector3 pos, byte w = 5, byte h = 4)
        {
            var c = new Refrigerator { Width = w, Height = h };
            parent.AddChild(c);
            c.GlobalPosition = pos;
            return c;
        }

        protected override void BuildVisual()
        {
            // a tall white/steel fridge box (vs the crate's small wooden cube)
            var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.7f, 1.7f, 0.7f) } };
            body.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.88f, 0.9f), Metallic = 0.3f, Roughness = 0.35f };
            body.Position = new Vector3(0, 0.85f, 0);
            AddChild(body);
            // a chunky door handle so it reads as a fridge, not just a white box
            var handle = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.5f, 0.04f) } };
            handle.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.3f, 0.32f), Metallic = 0.5f, Roughness = 0.3f };
            handle.Position = new Vector3(0.28f, 1.0f, 0.36f);
            AddChild(handle);

            var label = new Label3D
            {
                Text = "Refrigerator",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.85f, 0.9f, 0.95f),
                PixelSize = 0.006f, Position = new Vector3(0, 1.95f, 0),
                NoDepthTest = true, FontSize = 56, OutlineSize = 10,
            };
            AddChild(label);
        }
    }
}
