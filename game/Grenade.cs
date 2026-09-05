using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>A throwable in flight -- a frag, a smoke canister or a lit road flare (strawberry 2026-09-05:
    /// "grenades, smoke grenades and flares ... thrown from the hand. 3s fuse before detonation").
    ///
    /// ONE class for all three because the flight is identical and only the ending differs: an explosive calls
    /// the thrower's Explode, a smoke leaves a <see cref="SmokeCloud"/> where it stopped, and a flare is ALREADY
    /// burning from the moment it leaves the hand (it is struck, then thrown -- a road flare that ignites three
    /// seconds after you throw it is a grenade wearing a flare's model).
    ///
    /// THE GROUND IS NOT AT y=0. The first version of this file bounced off a hard-coded plane at y=0.11 with a
    /// comment admitting it "assumes ground near y=0"; on PEI that is true in exactly one place, so a grenade
    /// thrown on any hill either detonated in mid-air on the way down a slope or sank through the hilltop it
    /// landed on. It raycasts the real world now, on the same mask a dropped WorldItem rests against
    /// (bit0 world/terrain/buildings + bit6 small props).</summary>
    public partial class Grenade : Node3D
    {
        public PlayerController Thrower;
        public Vector3 Vel;

        /// <summary>Which throwable this is. Null = the bare frag defaults below, which is what the older
        /// call sites (Main's --nade harness, the MP replica view) still mean.</summary>
        public ThrowableDef Def;
        public ushort ItemId = 254;
        public Color Tint = new Color(0.62f, 0.62f, 0.62f);   // smoke/flare colour, read off the item's palette by the thrower

        public float Fuse = Throwables.FuseSeconds;

        // Frag defaults (Grenade.dat), kept as fields so the pre-existing harness call sites still compile and
        // still mean what they meant.
        public float Radius = 8f, ZombieDamage = 175f, PlayerDamage = 175f, VehicleDamage = 100f;

        const float Gravity = 9.81f;         // REAL gravity: this is a physics object, not the player's 3x arcade fall
        const float Restitution = 0.28f;     // a grenade is dense and does not bounce much
        const float Friction = 0.55f;        // tangential loss per bounce -- it skids, then stops
        const float RestSpeed = 0.7f;        // below this after a bounce it is lying still
        const float Skin = 0.06f;            // hold this far off a surface so the next ray does not start inside it
        // world/terrain/buildings + small props (what a dropped WorldItem rests on) + a VEHICLE chassis. The
        // chassis bit is not optional cosmetics: Vehicle.SolidBit moves a car with a ripped hull mesh off bit0
        // onto its own bit, so without it a grenade bounced off SOME cars and fell straight through the rest --
        // and which is which depends on whether that model happened to be extracted.
        static readonly uint HitMask = (1u << 0) | (1u << 6) | Vehicle.ChassisBit;

        MeshInstance3D _vis;
        bool _atRest;
        float _spin;
        float _life;

        public bool AtRest => _atRest;         // test seam
        public float FuseLeft => Fuse;

        EThrowableKind Kind => Def?.Kind ?? EThrowableKind.Explosive;

        public override void _Ready()
        {
            _vis = BuildVisual(ItemId);
            AddChild(_vis);
            _spin = (float)GD.RandRange(6.0, 13.0);

            if (Kind == EThrowableKind.Flare)
            {
                // Lit BEFORE the throw, so it burns the whole way down and the sparks trail behind it.
                AddChild(new FlareBurn { Tint = Tint, Duration = Def?.EffectSeconds ?? 45f });
                _life = (Def?.EffectSeconds ?? 45f) + 0.5f;   // outlive the burn's own fade by a hair, then take the subtree with us
            }
        }

        /// <summary>The thrown item's model. Shares the item-model cache every dropped WorldItem uses, so a
        /// smoke canister looks like a smoke canister in the air rather than a frag with the wrong paint.</summary>
        public static MeshInstance3D BuildVisual(ushort itemId = 254)
        {
            var mi = WorldItem.BuildReplicaVisual(itemId, new Color(0.16f, 0.2f, 0.13f));
            if (mi != null && mi.Mesh != null) return mi;
            // The old hand-rolled frag path, kept as the fallback: content/grenade.txt is the item.prefab Model_0
            // that predates the items/ extraction, and an id with no model at all must still throw something.
            var mat = new StandardMaterial3D { Metallic = 0.4f, Roughness = 0.6f };
            string ap = ProjectSettings.GlobalizePath("res://content/grenade_albedo.png");
            if (System.IO.File.Exists(ap)) { var img = ContentProvider.LoadImage(ap); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            else mat.AlbedoColor = new Color(0.16f, 0.2f, 0.13f);
            var mesh = ContentProvider.ParseObj("res://content/grenade.txt");
            return new MeshInstance3D
            {
                Mesh = mesh != null ? (Mesh)mesh : new SphereMesh { Radius = 0.11f, Height = 0.22f },
                MaterialOverride = mat,
            };
        }

        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;

            if (Kind == EThrowableKind.Flare)
            {
                Integrate(dt);
                _life -= dt;
                if (_life <= 0f) QueueFree();   // the burn is over; the FlareBurn child goes with us
                return;
            }

            Integrate(dt);
            Fuse -= dt;
            if (Fuse > 0f) return;

            if (Kind == EThrowableKind.Smoke)
            {
                var cloud = new SmokeCloud
                {
                    Tint = Tint,
                    Radius = Def?.Radius ?? 6f,
                    Duration = Def?.EffectSeconds ?? 22f,
                };
                // POSITION BEFORE AddChild, the same rule SpawnBlastFx documents: a particle system reads the
                // transform it had on ENTERING the tree, and a GlobalPosition written after the add spends the
                // first frames emitting at the world origin.
                cloud.Position = GlobalPosition + Vector3.Up * 0.35f;
                GetParent()?.AddChild(cloud);   // our own parent (the scene in game, the sandbox world under L1) -- CurrentScene would leak the cloud out of a test world
                GameAudio.PlayAt(this, GameAudio.Pick("casings", "general"), GlobalPosition, -2f, 6f, 40f, 0.55f);   // the canister popping (no dedicated retail clip in the rip)
            }
            else if (IsInstanceValid(Thrower))
            {
                Thrower.Explode(GlobalPosition,
                                Def?.Radius ?? Radius,
                                Def?.ZombieDamage ?? ZombieDamage,
                                Def?.PlayerDamage ?? PlayerDamage,
                                Def?.VehicleDamage ?? VehicleDamage);
            }
            QueueFree();
        }

        /// <summary>Ballistic step with a real swept collision against the world. Once it comes to rest the
        /// integration stops entirely -- a settled grenade must not keep casting a ray every tick for the rest
        /// of its fuse, and a lit flare lies on the ground for the better part of a minute.</summary>
        void Integrate(float dt)
        {
            if (_atRest) return;
            if (_vis != null) _vis.RotateY(_spin * dt);

            Vel.Y -= Gravity * dt;
            Vector3 from = GlobalPosition, to = from + Vel * dt;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) { GlobalPosition = to; return; }   // headless/no physics world -> pure ballistic, no crash

            var q = PhysicsRayQueryParameters3D.Create(from, to, HitMask);
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) { GlobalPosition = to; return; }

            Vector3 point = (Vector3)hit["position"], n = ((Vector3)hit["normal"]).Normalized();
            float speed = Vel.Length();
            if (speed > 1.5f)
                GameAudio.PlayAt(this, GameAudio.Pick("casings", "general"), point, -6f, 4f, 30f, 0.7f);   // no dedicated bounce clip in the rip: the brass bounce, pitched down

            Vector3 vn = n * Vel.Dot(n), vt = Vel - vn;              // split into into-the-surface and along-it
            Vel = vt * (1f - Friction) - vn * Restitution;           // skid + bounce back out
            GlobalPosition = point + n * Skin;

            // Rest test on the POST-bounce speed, and only against a surface that can actually hold it: a
            // grenade skidding down a steep face is still moving, not settled.
            if (Vel.Length() < RestSpeed && n.Y > 0.5f) { _atRest = true; Vel = Vector3.Zero; }
        }
    }
}
