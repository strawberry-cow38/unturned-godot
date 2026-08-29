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
        public bool Indestructible;               // a window-fill can mark a pane unbreakable (master's per-opening options)
        public static readonly Color DefaultHue = new Color(0.62f, 0.73f, 0.78f);   // light blue-grey glass (WorldBuilder.MatFor's Glass_ look)
        /// <summary>Fired the instant the pane shatters (BEFORE it frees itself). The window-fill subscribes so it can mark
        /// its opening broken -- a same-frame save then agrees with the screen. The pane persists nothing itself (tinyclaw).</summary>
        public event System.Action OnShattered;
        bool _shattered;
        Vector3 _half = new Vector3(0.5f, 0.7f, 0.02f);
        Color _hue = DefaultHue;                  // the pane's glass tint; the material + shatter shards both colour off it

        /// <summary>Build a pane sized to the opening (`size` = width x height metres, thin). Position is the CALLER's -- the
        /// wall places the returned node itself (no position passed in). `tint` colours the glass AND its shatter shards;
        /// `hp` / `indestructible` are the per-opening options. The damage path + shatter stay ours; the caller sets numbers.</summary>
        public static GlassPane Build(Vector2 size, Color? tint = null, float hp = 1f, bool indestructible = false, float thickness = 0.04f)
        {
            var pane = new GlassPane { CollisionLayer = 1u << 6, CollisionMask = 0u };   // bit6 = glass/see-through layer (bullets hit it; doesn't block item line-of-sight, per ItemTests)
            pane._hue = tint ?? DefaultHue;
            pane.Health = pane.HealthMax = Mathf.Max(1f, hp);
            pane.Indestructible = indestructible;
            pane._half = new Vector3(size.X * 0.5f, size.Y * 0.5f, thickness * 0.5f);
            var box = new Vector3(size.X, size.Y, thickness);
            pane.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = box }, MaterialOverride = GlassMat(pane._hue) });
            pane.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box } });
            pane.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);   // a stray hit reads as a hard 'tink'; the shatter carries the real read
            return pane;
        }

        // The see-through glass material (matches WorldBuilder.MatFor's Glass_ look: mostly transparent, glossy), tinted by `hue`.
        static StandardMaterial3D GlassMat(Color hue) => new StandardMaterial3D
        {
            AlbedoColor = new Color(hue.R, hue.G, hue.B, 0.26f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Metallic = 0f, Roughness = 0.06f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // The shard colour: the pane's glass hue lightened toward white, so broken glass reads bright.
        // shards TRANSLUCENT (master 2026-08-09): glass fragments should read see-through, not solid glass-coloured chips.
        // Alpha 0.5 with the material's Alpha transparency -> the shard sprite becomes half-transparent (glassy).
        static Color ShardTint(Color hue) => new Color(Mathf.Lerp(hue.R, 1f, 0.35f), Mathf.Lerp(hue.G, 1f, 0.35f), Mathf.Lerp(hue.B, 1f, 0.35f), 0.5f);

        public void TakeDamage(float amount)
        {
            if (_shattered || Indestructible) return;   // an indestructible pane takes hits but never shatters (tinyclaw caught this missing -- guard in TakeDamage, NOT Shatter, so a scripted Shatter() still works)
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
            OnShattered?.Invoke();   // tell the window-fill NOW (before we free) so its opening + a same-frame save agree with the screen
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
                    AlbedoColor = ShardTint(_hue),   // FLAT glass-hue shard at a clean UNIFORM 50% alpha (ShardTint a=0.5). No AlbedoTexture: the glass rubble sprite's soft alpha multiplied the 0.5 down to a faint, uneven shard -- master wanted "50% transp on the WHOLE particle" i.e. uniform across the quad. Colour still off the prop hue via AlbedoColor (master+tc reliable-multiply intent kept).
                };
                Vector3 halfExt = new Vector3(Mathf.Max(_half.X, 0.2f), Mathf.Max(_half.Y, 0.2f), 0.2f);   // emit across the pane's whole face
                var ps = new CpuParticles3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, 
                    Emitting = false, OneShot = true,   // fired below AFTER positioning. GlassPane.Shatter runs from TakeDamage INSIDE StepBullets (a 50Hz physics tick); Emitting=true in the ctor arms the one-shot at construction and it burns its cycle before the first _process -> fires EMPTY -> shards never appear (the pane still vanishes + marks broken, so it looks deliberate). Same bug + fix as ImpactFx.
                    Amount = Mathf.Clamp(Mathf.RoundToInt(fx.Count * 2f), 16, 40),   // a pane throws more glass than a fragment
                    Lifetime = Mathf.Max(1.2f, fx.LifeMax * 1.2f), Explosiveness = 0.9f, Randomness = 0.5f,
                    Direction = faceN, Spread = 85f,   // fan out of the pane face
                    InitialVelocityMin = fx.SpeedMin * 0.5f, InitialVelocityMax = fx.SpeedMax * 0.7f,
                    Gravity = new Vector3(0f, -7f * fx.Gravity, 0f),
                    ScaleAmountMin = fx.SizeMin * 0.45f * ParticleFx.SizeScale, ScaleAmountMax = fx.SizeMax * 0.55f * ParticleFx.SizeScale,
                    AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -400f, AngularVelocityMax = 400f,
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                    Mesh = new QuadMesh { Size = Vector2.One, Material = fmat },
                    // HUGE cull box -> the fast shards never frustum-cull the system (the flicker/derender bug); same
                    // lesson as ImpactFx + DestructibleField.PlayBreakEffect.
                    VisibilityAabb = new Aabb(new Vector3(-60f, -60f, -60f), new Vector3(120f, 120f, 120f)),
                };
                scene.AddChild(ps);
                ps.GlobalPosition = centre;
                ps.Emitting = true;   // THE FIX: arm the one-shot AFTER AddChild+position -> a clean emission cycle regardless of the spawning (physics) tick
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
            if (!_sndTried)
            {
                _sndTried = true;
                // Prefer the retail Glass rubble clip (effect 64) -- the same extracted source every other destructible
                // now breaks with -- so glass is consistent + source-accurate. Fall back to the older impact_glass.wav
                // (a bullet-ping sound) only if the rubble clip wasn't extracted.
                if (RubbleSnd.TryGet(GlassEffectId, out var rs)) _snd = rs;
                else { string p = ProjectSettings.GlobalizePath("res://content/impact_glass.wav"); if (System.IO.File.Exists(p)) _snd = AudioStreamWav.LoadFromFile(p); }
            }
            if (_snd == null) return;   // no glass sfx available -> shatter is silent (asset follow-up)
            var pl = new AudioStreamPlayer3D { Stream = _snd, UnitSize = 6f, MaxDistance = 80f, VolumeDb = -2f };
            scene.AddChild(pl); pl.GlobalPosition = pos; pl.Play();
            pl.Finished += () => { if (IsInstanceValid(pl)) pl.QueueFree(); };
        }
    }
}
