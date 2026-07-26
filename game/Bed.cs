using Godot;
using SDG.Unturned;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    // A claimable bed: the thing that decides where you wake up.
    //
    // The ownership rules -- one bed per player, claiming a new one releases the old, a claimed bed is
    // not stealable, destroying a bed takes its owner's spawn with it -- live engine-free in
    // SDG.Unturned.BedClaims and are L0-tested there. This node is the world presence: a mesh, a place to
    // stand, and the registration that keeps the claim table in step with what exists.
    public partial class Bed : StaticBody3D
    {
        /// <summary>The claim table for the level. Beds register on entering the tree and deregister on
        /// leaving, so a salvaged or blown-up bed cannot go on being someone's spawn point.</summary>
        public static readonly BedClaims Claims = new BedClaims();
        static int _nextId = 1;

        public int BedId { get; private set; }
        public ulong Owner => Claims.OwnerOf(BedId);
        public bool IsClaimed => Claims.IsClaimed(BedId);

        float _yaw;
        Vector3 _spawnPos;

        public static Bed Spawn(Node parent, Vector3 basePos, float yawDeg)
        {
            // Record the position BEFORE entering the tree. AddChild runs _Ready, which registers the
            // claim -- assigning GlobalPosition afterwards registered every bed at the origin, so two
            // beds 20 m apart both respawned you in the same spot.
            var b = new Bed { BedId = _nextId++, _yaw = yawDeg, _spawnPos = basePos };
            parent.AddChild(b);
            return b;
        }

        public override void _Ready()
        {
            CollisionLayer = 1 << 0;
            CollisionMask = 0;

            var size = new Vector3(1.0f, 0.45f, 2.0f);
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.52f, 0.60f), Roughness = 1f },
                Position = new Vector3(0f, size.Y * 0.5f, 0f),
            });
            AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = size },
                Position = new Vector3(0f, size.Y * 0.5f, 0f),
            });

            GlobalPosition = _spawnPos;
            RotationDegrees = new Vector3(0f, _yaw, 0f);
            Claims.Register(BedId, new UVector3(_spawnPos.X, _spawnPos.Y, _spawnPos.Z), _yaw);
        }

        public override void _ExitTree()
        {
            // Destroyed or salvaged: the claim table must forget it, or a dead bed keeps respawning people
            // into a hole where their base used to be.
            Claims.Remove(BedId);
        }

        /// <summary>Claim this bed for <paramref name="player"/>, releasing whichever they held before.</summary>
        public bool TryClaim(ulong player, double now) => Claims.Claim(BedId, player, now);

        public bool CanClaim(ulong player, double now) => Claims.CanClaim(BedId, player, now);

        /// <summary>Where this player should respawn, in Godot space. False = no bed, use the map spawn.</summary>
        public static bool TryGetSpawn(ulong player, out Vector3 position, out float yaw)
        {
            position = Vector3.Zero;
            if (!Claims.TryGetSpawn(player, out var p, out yaw)) return false;
            position = new Vector3(p.x, p.y, p.z);
            return true;
        }

        /// <summary>Tests share one static table; this keeps one case from inheriting another's claims.</summary>
        public static void DebugResetAll()
        {
            for (int id = 0; id < _nextId; id++) Claims.Remove(id);
            _nextId = 1;
        }
    }
}
