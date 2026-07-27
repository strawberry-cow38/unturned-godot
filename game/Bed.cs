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

        /// <summary>Beds are barricades: breakable. Destroying one is how you take away someone's
        /// respawn -- the rule BedClaims already implements, which had no way to fire while beds were
        /// indestructible.</summary>
        public float Health = 200f, HealthMax = 200f;

        float _yaw;
        Vector3 _spawnPos;
        StandardMaterial3D _mat;

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
            _mat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.52f, 0.60f), Roughness = 1f };
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = _mat,
                Position = new Vector3(0f, size.Y * 0.5f, 0f),
            });
            AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = size },
                Position = new Vector3(0f, size.Y * 0.5f, 0f),
            });

            GlobalPosition = _spawnPos;
            RotationDegrees = new Vector3(0f, _yaw, 0f);
            Register();
        }

        // Registered on ENTERING the tree, not only in _Ready: _Ready fires once, so a bed that is
        // re-parented (removed then re-added) would deregister on exit and never come back.
        public override void _EnterTree() => Register();

        void Register()
        {
            if (Claims.TryGet(BedId, out _)) return;   // already known (first _Ready, or never left)
            Claims.Register(BedId, new UVector3(_spawnPos.X, _spawnPos.Y, _spawnPos.Z), _yaw);
        }

        /// <summary>Break it. Returns true if this destroyed the bed -- which takes its owner's respawn
        /// point with it, via the _ExitTree deregistration.</summary>
        public bool TakeDamage(float amount)
        {
            if (amount <= 0f || Health <= 0f) return false;
            Health -= amount;
            if (Health > 0f) return false;
            Health = 0f;
            QueueFree();
            return true;
        }

        public bool IsDestroyed => Health <= 0f;

        /// <summary>Look-focus highlight, matching the other focusables.</summary>
        public void SetLookFocused(bool on)
        {
            if (_mat == null) return;
            _mat.EmissionEnabled = on;
            _mat.Emission = new Color(0.20f, 0.24f, 0.35f);
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

        /// <summary>Drop every claim. Called when a world is built so a map reload does not inherit the
        /// previous level's spawn points -- the claim table is static, which is fine for one world at a
        /// time and would need to become world-scoped for split-screen or a second simultaneous level.</summary>
        public static void ResetForNewWorld()
        {
            for (int id = 0; id < _nextId; id++) Claims.Remove(id);
            _nextId = 1;
        }

        /// <summary>Tests share one static table; this keeps one case from inheriting another's claims.</summary>
        public static void DebugResetAll() => ResetForNewWorld();
    }
}
