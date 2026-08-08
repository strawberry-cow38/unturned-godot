using Godot;

namespace UnturnedGodot
{
    // A full window-sized DESTRUCTIBLE glass pane (master 2026-08-08: "we have a destructible glass fragment prop but
    // not a full pane -- so just dupe that and reshape"). A flat transparent glass rectangle that SHATTERS into the
    // retail Glass_0 rubble shards (Rubble_Effect id 64 -- the SAME effect the Glass_0/Glass_1 fragments use) plus a
    // break sound when shot, then it's gone (glass leaves an empty frame). Self-contained like Door/Bed: a StaticBody3D
    // with health, damaged through the StepBullets collider path; one shot shatters it (glass ships at Health 1).
    //
    // The shatter mirrors DestructibleField.PlayBreakEffect's chip spawn -- INCLUDING the huge cull AABB, so the fast
    // shards don't get frustum-culled mid-flight (the flicker/derender bug). See [[reference_unturned_impact_fx]].
    public partial class GlassPane : StaticBody3D
    {
        public const int GlassEffectId = 64;      // Glass_0/Glass_1 -> Rubble_Effect 64 (content/objects/rubble.txt)
        public float Health = 1f, HealthMax = 1f; // glass = one shot (the retail fragment ships at 1 hp)
        bool _shattered;
        Vector3 _half = new Vector3(0.5f, 0.7f, 0.02f);

        /// <summary>Build a pane `width` x `height` metres (thin). Local +Z is the pane's face normal.</summary>
        public static GlassPane Build(float width = 1.0f, float height = 1.4f, float thickness = 0.04f)
        {
            var pane = new GlassPane { CollisionLayer = 1u << 6, CollisionMask = 0u };   // bit6 = glass/see-through layer (bullets hit it; doesn't block item line-of-sight, per ItemTests)
            pane._half = new Vector3(width * 0.5f, height * 0.5f, thickness * 0.5f);
            var size = new Vector3(width, height, thickness);
            pane.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = GlassMat() });
            pane.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            pane.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);   // a stray hit reads as a hard 'tink'; the shatter carries the real read
            return pane;
        }

        // The see-through glass material (matches WorldBuilder.MatFor's Glass_ look: light blue-grey, mostly transparent, glossy).
        static StandardMaterial3D GlassMat() => new StandardMaterial3D
        {
            AlbedoColor = new Color(0.62f, 0.73f, 0.78f, 0.26f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Metallic = 0f, Roughness = 0.06f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        public void TakeDamage(float amount)
        {
            if (_shattered) return;
            Health -= amount;
            if (Health <= 0f) Shatter();
        }

        /// <summary>Shatter: the retail Glass_0 rubble shards (effect 64) fanned out of the pane face + a break sound,
        /// then the pane is freed. Mirrors DestructibleField.PlayBreakEffect's chip spawn, with the huge cull AABB so the
        /// fast shards aren't frustum-culled mid-flight.</summary>
        public void Shatter()
        {
            if (_shattered) return;
            _shattered = true;
            var scene = GetTree()?.CurrentScene;
            Vector3 centre = GlobalPosition;
            Vector3 faceN = GlobalTransform.Basis.Z.Normalized();
            if (scene != null && RubbleFx.TryGet(GlassEffectId, out var fx) && fx.Tex != null)
            {
                var fmat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,   // flat glass colour (not lit-dark) so the shards read as glass
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    AlbedoColor = new Color(0.72f, 0.84f, 0.9f), AlbedoTexture = fx.Tex,   // shards COLOURED OFF THE PROP (the pane's light glass blue-grey) via AlbedoColor -- a reliable multiply, NOT the fragile per-particle vertex-colour buffer (master + tc)
                };
                Vector3 halfExt = new Vector3(Mathf.Max(_half.X, 0.2f), Mathf.Max(_half.Y, 0.2f), 0.2f);   // emit across the pane's whole face
                var ps = new CpuParticles3D
                {
                    Emitting = true, OneShot = true,
                    Amount = Mathf.Clamp(Mathf.RoundToInt(fx.Count * 2f), 16, 40),   // a pane throws more glass than a fragment
                    Lifetime = Mathf.Max(1.2f, fx.LifeMax * 1.2f), Explosiveness = 0.9f, Randomness = 0.5f,
                    Direction = faceN, Spread = 85f,   // fan out of the pane face
                    InitialVelocityMin = fx.SpeedMin * 0.5f, InitialVelocityMax = fx.SpeedMax * 0.7f,
                    Gravity = new Vector3(0f, -7f * fx.Gravity, 0f),
                    ScaleAmountMin = fx.SizeMin * 0.45f, ScaleAmountMax = fx.SizeMax * 0.55f,
                    AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -400f, AngularVelocityMax = 400f,
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                    Mesh = new QuadMesh { Size = Vector2.One, Material = fmat },
                    // HUGE cull box -> the fast shards never frustum-cull the system (the flicker/derender bug); same
                    // lesson as ImpactFx + DestructibleField.PlayBreakEffect.
                    VisibilityAabb = new Aabb(new Vector3(-60f, -60f, -60f), new Vector3(120f, 120f, 120f)),
                };
                scene.AddChild(ps);
                ps.GlobalPosition = centre;
                var t = GetTree().CreateTimer(ps.Lifetime + 0.6f);
                t.Timeout += () => { if (IsInstanceValid(ps)) ps.QueueFree(); };
            }
            PlayBreakSound(scene, centre);
            QueueFree();   // the glass is gone once it shatters
        }

        static AudioStream _snd; static bool _sndTried;
        static void PlayBreakSound(Node scene, Vector3 pos)
        {
            if (scene == null) return;
            if (!_sndTried) { _sndTried = true; string p = ProjectSettings.GlobalizePath("res://content/impact_glass.wav"); if (System.IO.File.Exists(p)) _snd = AudioStreamWav.LoadFromFile(p); }
            if (_snd == null) return;   // no glass sfx extracted yet -> shatter is silent for now (asset follow-up)
            var pl = new AudioStreamPlayer3D { Stream = _snd, UnitSize = 6f, MaxDistance = 80f, VolumeDb = -2f };
            scene.AddChild(pl); pl.GlobalPosition = pos; pl.Play();
            pl.Finished += () => { if (IsInstanceValid(pl)) pl.QueueFree(); };
        }
    }
}
