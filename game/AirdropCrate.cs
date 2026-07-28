using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// A supply crate on its way down, and the box you loot once it lands.
    ///
    /// The crate does NOT integrate its own fall. Height comes from AirdropSim, which is a closed-form
    /// function of elapsed time, so a client that joined mid-descent or dropped frames still draws the
    /// crate exactly where the server has it. A locally-integrated fall would drift, and the drift
    /// would be invisible until two players disagreed about where the loot landed.
    /// </summary>
    public partial class AirdropCrate : StaticBody3D
    {
        public const float Size = 1.1f;

        /// <summary>Crates are breakable, so a drop cannot become permanent scenery if nobody loots it.</summary>
        public float Health = 120f;

        /// <summary>What is inside. Held as plain item ids rather than a Container because a crate is
        /// opened once and emptied -- it is a parcel, not a storage box you keep coming back to. The
        /// contents spill as WorldItems, which reuses the whole existing pickup and replication path
        /// instead of inventing a second one that would need its own MP story.</summary>
        public readonly List<ushort> Contents = new();

        public bool Emptied { get; private set; }

        public uint NetId { get; set; }
        public bool Landed { get; private set; }

        static readonly Dictionary<uint, AirdropCrate> _byNetId = new();

        public static bool TryGetByNetId(uint id, out AirdropCrate c)
        {
            if (_byNetId.TryGetValue(id, out c) && IsInstanceValid(c)) return true;
            _byNetId.Remove(id);
            c = null;
            return false;
        }

        public static IEnumerable<AirdropCrate> All
        {
            get { foreach (var kv in _byNetId) if (IsInstanceValid(kv.Value)) yield return kv.Value; }
        }

        MeshInstance3D _chute;

        public override void _ExitTree() { if (NetId != 0) _byNetId.Remove(NetId); }

        public static AirdropCrate Spawn(Node parent, Vector3 at, uint netId = 0)
        {
            var c = new AirdropCrate { NetId = netId };
            parent.AddChild(c);
            c.GlobalPosition = at;
            c.Build();
            if (netId != 0) _byNetId[netId] = c;
            return c;
        }

        void Build()
        {
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(Size, Size, Size) } });
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(Size, Size, Size) },
                // Deliberately loud: the whole point of an airdrop is that everyone can see it coming.
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.42f, 0.10f) },
            });

            _chute = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 2.2f, Height = 2.2f, RadialSegments = 16, Rings = 6 },
                Position = new Vector3(0f, 2.0f, 0f),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.92f, 0.92f, 0.88f),
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            AddChild(_chute);
        }

        /// <summary>Put the crate where the sim says it is. Called every tick while falling.</summary>
        public void ApplyPosition(Vector3 p) => GlobalPosition = p;

        /// <summary>Down. The chute is cut so a landed crate does not sit under a balloon for ever.</summary>
        public void MarkLanded()
        {
            if (Landed) return;
            Landed = true;
            if (IsInstanceValid(_chute)) _chute.QueueFree();
        }

        /// <summary>Break it open. The contents spill on the ground rather than vanishing with the
        /// crate: destroying a supply drop should scatter the supplies, not delete them.</summary>
        public bool TakeDamage(float amount)
        {
            Health -= amount;
            if (Health > 0f) return false;
            SpillContents();
            QueueFree();
            return true;
        }

        /// <summary>Open it. Same spill as breaking it, minus the crate dying -- so a player can walk
        /// up and loot rather than having to beat the parcel to death first.</summary>
        public bool Open()
        {
            if (Emptied || !Landed) return false;   // no looting a crate that is still in the air
            SpillContents();
            return true;
        }

        void SpillContents()
        {
            if (Emptied) return;
            Emptied = true;
            var parent = GetParent();
            if (parent == null) return;
            for (int i = 0; i < Contents.Count; i++)
            {
                // Fanned out slightly so a five-item drop is not one pile occupying a single point.
                float a = Contents.Count > 1 ? (float)i / Contents.Count * Mathf.Tau : 0f;
                var at = GlobalPosition + new Vector3(Mathf.Cos(a) * 0.7f, 0.4f, Mathf.Sin(a) * 0.7f);
                WorldItem.Spawn(parent, new SDG.Unturned.Item(Contents[i]), at);
            }
            Contents.Clear();
        }
    }
}
