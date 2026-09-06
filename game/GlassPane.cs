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
            pane._mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = box }, MaterialOverride = GlassMat(pane._hue) };
            pane.AddChild(pane._mesh);
            pane.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box } });
            pane.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);   // a stray hit reads as a hard 'tink'; the shatter carries the real read
            return pane;
        }

        MeshInstance3D _mesh;
        /// <summary>Rain-on-glass shelter for THIS pane (the shader's per-instance `covered`): true = no rain reaches it, so no
        /// beads or runners. A pane cannot tell an exterior window from a window-shaped opening in an interior partition -- the
        /// wall that spawned it can (WallSurface: 0.50 partition vs 0.70 exterior thickness, the editor's own convention), so
        /// the wall sets it. Default false: a standalone pane in the open rains.</summary>
        public bool Covered
        {
            set { if (_mesh != null && GodotObject.IsInstanceValid(_mesh)) _mesh.SetInstanceShaderParameter("covered", value ? 1f : 0f); }
        }

        // The see-through glass material (matches WorldBuilder.MatFor's Glass_ look: mostly transparent, glossy), tinted by `hue`
        // -- now the RAIN GLASS shader (strawberry 2026-09-05): the same tinted glass, plus beads + runners while it rains.
        static Material GlassMat(Color hue) => RainGlassMat(new Color(hue.R, hue.G, hue.B, 0.26f), 0f, 0.06f);

        static Shader _rainGlass;
        /// <summary>content/rain_glass.gdshader as a material: `tint` = the glass colour + alpha (what the StandardMaterial3D
        /// AlbedoColor used to carry), metallic/roughness likewise. Shared by the window panes here and every vehicle's glass
        /// (Vehicle.AddGlassOverlay), so a windscreen and a window bead + streak the same way in the same rain.</summary>
        public static ShaderMaterial RainGlassMat(Color tint, float metallic, float roughness)
        {
            _rainGlass ??= GD.Load<Shader>("res://content/rain_glass.gdshader");
            var m = new ShaderMaterial { Shader = _rainGlass };
            m.SetShaderParameter("tint", tint);
            m.SetShaderParameter("metallic", metallic);
            m.SetShaderParameter("roughness", roughness);
            if (int.TryParse(System.Environment.GetEnvironmentVariable("UG_GLASSDEBUGVIEW"), out var dbg) && dbg > 0) m.SetShaderParameter("debug_view", dbg);   // harness: false-colour the shader's inputs
            return m;
        }

        // The shard colour: the pane's glass hue lightened toward white, so broken glass reads bright.
        // shards TRANSLUCENT (master 2026-08-09): glass fragments should read see-through, not solid glass-coloured chips.
        // Alpha 0.5 with the material's Alpha transparency -> the shard sprite becomes half-transparent (glassy).
        /// <summary>The shard colour: the pane's hue lightened toward white, at 50% alpha.
        ///
        /// MEASURED 2026-08-29, because "50% transp on the WHOLE particle" is NOT what this achieves and
        /// the file used to imply it did. Rendering the shatter twice -- once at this 0.5, once forced to
        /// 1.0 -- changes 61% of the shard cluster, so the alpha IS applied (sky, ground, wall and HUD came
        /// back pixel-identical between the two runs, so that is a real difference and not render noise).
        ///
        /// But 16-40 quads fan out of one pane and OVERLAP, and stacked alpha compounds: three layers of
        /// 0.5 is 1 - 0.5^3 = 0.875. Measured ~0.9 effective where they pile up, so the cluster reads
        /// nearly solid while every individual shard is correctly half-transparent.
        ///
        /// So if the CLUSTER needs to read 50%, the lever is Amount or this value -- not the material, and
        /// not the transparency mode. Both of those are already right.</summary>
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
            SpawnShards(scene, centre, faceN, new Vector3(Mathf.Max(_half.X, 0.2f), Mathf.Max(_half.Y, 0.2f), 0.2f), ShardTint(_hue));
            PlayBreakSound(scene, centre);
            QueueFree();   // the glass is gone once it shatters
        }

        /// <summary>The glass shard burst on its own -- a window pane here, a car window or a lamp lens in Vehicle
        /// (BreakGlass/BreakLamp; master 2026-09-05 "give vehicle glass glass break particles as well as headlights and
        /// tail lights"). `halfExt` = the emission box (world-aligned) the shards start across; `tint` = flat shard colour
        /// (see ShardTint); `countScale` shrinks the burst for a small lens.</summary>
        public static void SpawnShards(Node scene, Vector3 centre, Vector3 faceN, Vector3 halfExt, Color tint, float countScale = 1f, float sizeMul = 1f)
        {
            if (scene == null || !RubbleFx.TryGet(GlassEffectId, out var fx) || fx.Tex == null) return;
            var fmat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,   // flat glass colour (not lit-dark) so the shards read as glass
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BillboardKeepScale = true,   // WITHOUT this the Particles billboard normalises the instance basis and every shard is the 1 m quad -- ScaleAmount* did nothing (found on the jeep's headlight, 2026-09-05)
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = tint,   // FLAT glass-hue shard at a clean UNIFORM 50% alpha (ShardTint a=0.5). No AlbedoTexture: the glass rubble sprite's soft alpha multiplied the 0.5 down to nothing
            };
            var ps = new CpuParticles3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Emitting = false, OneShot = true,   // fired below AFTER positioning. GlassPane.Shatter runs from TakeDamage INSIDE StepBullets (a 50Hz physics tick); Emitting=true in the ctor armed a half-cycle
                Amount = ParticleFx.Amount(Mathf.Clamp(Mathf.RoundToInt(fx.Count * 2f * countScale), 8, 40)),   // a pane throws more glass than a fragment
                Lifetime = Mathf.Max(1.2f, fx.LifeMax * 1.2f), Explosiveness = 0.9f, Randomness = 0.5f,
                Direction = faceN, Spread = 85f,   // fan out of the pane face
                InitialVelocityMin = fx.SpeedMin * 0.5f, InitialVelocityMax = fx.SpeedMax * 0.7f,
                Gravity = new Vector3(0f, -7f * fx.Gravity, 0f),
                ScaleAmountMin = fx.SizeMin * 0.45f * ParticleFx.SizeScale * sizeMul, ScaleAmountMax = fx.SizeMax * 0.55f * ParticleFx.SizeScale * sizeMul,   // sizeMul: a car window / lamp lens throws small chips, not pane-sized sheets
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
            if (System.Environment.GetEnvironmentVariable("UG_SHARDDBG") == "1") GD.Print($"[shards] size {fx.SizeMin}..{fx.SizeMax} x SizeScale {ParticleFx.SizeScale} x mul {sizeMul} -> scale {ps.ScaleAmountMin:0.000}..{ps.ScaleAmountMax:0.000} amount {ps.Amount} ext {halfExt}");
            var t = scene.GetTree().CreateTimer(ps.Lifetime + 0.6f);
            t.Timeout += () => { if (IsInstanceValid(ps)) ps.QueueFree(); };
        }

        static AudioStream _snd; static bool _sndTried;
        public static void PlayBreakSound(Node scene, Vector3 pos)
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
