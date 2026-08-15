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
        protected virtual float RenderDist => 80f;   // max render distance for the container's meshes (master: storage containers never de-rendered before). Tunable / overridable per subclass.

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
            ApplyRenderDistance();   // cull the container's meshes past RenderDist -- storage containers never de-rendered before (master). StoreShelf re-applies to each item model as it spawns.
        }

        // Cap the max render distance on every GeometryInstance3D under the container (mesh bodies + billboard labels),
        // so a container stops drawing past RenderDist instead of always rendering (master). Recursive, so a multi-mesh
        // prop or a spawned item model (StoreShelf.PlaceItem passes the model root) is fully covered.
        protected void ApplyRenderDistance(Node root = null)
        {
            root ??= this;
            if (root is GeometryInstance3D gi) gi.VisibilityRangeEnd = RenderDist;
            foreach (var child in root.GetChildren()) ApplyRenderDistance(child);
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

    // A powered storage container (strawberry): places + wires like a spotlight/splitter (IPowerDevice, own
    // ConnectionPort, joins "deployables"), but stays a StorageCrate (not a Deployable) so the F-key router's
    // is-Deployable type-check for hold-F pickup correctly falls through to OpenNearestCrate (F-open) instead.
    // Preserves its Storage contents ONLY while its Consumer port is wired + powered -- cut its power (unwire it,
    // or kill the generator feeding it) and it warms up, spoiling like any plain crate again.
    public partial class Refrigerator : StorageCrate, IPowerDevice
    {
        public const float Watts = 200f;
        readonly System.Collections.Generic.List<ConnectionPort> _powerPorts = new();
        readonly System.Collections.Generic.HashSet<Item> _tracked = new();
        ConnectionPort _consumerPort;
        public ConnectionPort ConsumerPort => _consumerPort;   // exposed so DevConsole / tests can wire a generator straight to it

        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;

        // preserves ONLY while its own port is wired + powered (was PowerNet.GlobalPower in the stub)
        public override bool Preserves => _consumerPort != null && GodotObject.IsInstanceValid(_consumerPort) && _consumerPort.Powered;

        public static Refrigerator Spawn(Node parent, Vector3 pos, byte w = 5, byte h = 4, float yawDeg = 0f)
        {
            var c = new Refrigerator { Width = w, Height = h };
            parent.AddChild(c);
            c.GlobalPosition = pos;
            c.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            return c;
        }

        public override void _Ready()
        {
            base._Ready();                       // "crates" group + Storage grid + BuildVisual
            AddToGroup("deployables");           // PowerNet keys on IPowerDevice members of this group
            var body = new StaticBody3D();       // StorageCrate has NO collider; a real placed device needs one (clearance + ray-hit)
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.7f, 1.7f, 0.7f) }, Position = new Vector3(0f, 0.85f, 0f) });
            AddChild(body);
            _consumerPort = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = new Vector3(0f, 0.25f, -0.36f), Watts = Watts }, "Refrigerator");
            AddChild(_consumerPort);
            _powerPorts.Add(_consumerPort);
            if (GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); GetParent()?.AddChild(pm); }
        }

        // Sync the per-item `preserved` flag so BOTH the spoilage sweep and the inv-UI snowflake see it. Set on FOOD in
        // the grid while powered; clear it on anything that LEFT the fridge (else it'd never spoil again anywhere).
        public override void _Process(double delta)
        {
            bool powered = Preserves;
            var current = new System.Collections.Generic.HashSet<Item>();
            for (byte i = 0; i < Storage.getItemCount(); i++)
            {
                var it = Storage.getItem(i)?.item;
                if (it == null || it.GetAsset()?.type != EItemType.FOOD) continue;
                current.Add(it);
                it.preserved = powered;
            }
            foreach (var old in _tracked) if (!current.Contains(old)) old.preserved = false;
            _tracked.Clear(); foreach (var it in current) _tracked.Add(it);
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
