using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    using Surf = PlayerController.Surf;

    // Bullet-impact FX, reimplemented from the source ParticleSystems (Effects/Impacts/<mat>_static) as our own Godot
    // system. Ripped out of PlayerController per master (2026-08-08: impacts were "small, invisible, really delayed,
    // buggy, or don't exist"). The root cause of "invisible": the debris + blood bursts had NO VisibilityAabb, so once
    // a fast particle flew out of the emitter's origin AABB the whole burst got frustum-culled -- only the water splash
    // (which set one) survived. Every burst here carries a generous VisibilityAabb sized for its travel, everything is
    // one consistent CpuParticles3D path, and the per-material counts/speeds/sizes/lifetimes come off the source effects.
    //
    // Surf -> source impact effect: concrete/metal/wood same-named; grass=foliage, dirt/sand=gravel; water = the lit
    // droplet splash; flesh (a player/zombie hit) = the Flesh_Dynamic blood sheet via Blood().
    public static class ImpactFx
    {
        // A burst big enough that a fast particle never leaves it -> the auto-AABB frustum cull can't hide the effect.
        // (This single missing line is why hard-surface + blood impacts "didn't exist" on screen.)
        static readonly Aabb Guard = new Aabb(new Vector3(-10f, -10f, -10f), new Vector3(20f, 20f, 20f));

        // --- caches (static: ProjectSettings/File are static; the tree comes in via `scene`) ---
        static readonly Dictionary<Surf, ImageTexture> _debris = new();
        static readonly Dictionary<Surf, Texture2D> _decal = new();
        static readonly Dictionary<Surf, AudioStream> _snd = new();
        static ImageTexture _blood; static bool _bloodTried;
        static AudioStream _bloodSnd; static bool _bloodSndTried;
        static Texture2D _flashTex; static bool _flashTried;

        static ImageTexture LoadTex(string rel)
        {
            string p = ProjectSettings.GlobalizePath(rel);
            if (!System.IO.File.Exists(p)) return null;
            var img = Image.LoadFromFile(p);
            return img != null ? ImageTexture.CreateFromImage(img) : null;
        }
        static AudioStream LoadWav(string rel)
        {
            string p = ProjectSettings.GlobalizePath(rel);
            return System.IO.File.Exists(p) ? AudioStreamWav.LoadFromFile(p) : null;
        }
        // Surf -> the source effect material stem.
        static string MatName(Surf s) => s switch
        {
            Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "gravel",
            Surf.Grass => "foliage", Surf.Dirt => "gravel", Surf.Water => "water", _ => "concrete",
        };
        static ImageTexture DebrisTex(Surf s)
            => _debris.TryGetValue(s, out var t) ? t : (_debris[s] = LoadTex($"res://content/impact_{MatName(s)}_static_0.png"));
        static Texture2D DecalTex(Surf s)   // only hard surfaces leave a hole; concrete stands in for a missing one
            => _decal.TryGetValue(s, out var t) ? t : (_decal[s] = LoadTex($"res://content/decal_{(s == Surf.Metal ? "metal" : s == Surf.Wood ? "wood" : "concrete")}.png"));
        static AudioStream Snd(Surf s)
            => _snd.TryGetValue(s, out var a) ? a : (_snd[s] = LoadWav($"res://content/impact_{MatName(s)}.wav"));

        /// <summary>A bullet hit at `point` on a surface with outward `normal`, material `surf`. Spawns the debris
        /// burst (+ a decal on hard surfaces, + a spark on metal), or the water splash. `attachTo` parents the decal
        /// to a moving body (a vehicle) so the hole follows it; null = static in the scene.</summary>
        public static void Spawn(Node scene, Vector3 point, Vector3 normal, Surf surf, Node3D attachTo = null)
        {
            if (scene == null) return;
            Vector3 up = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
            var tree = scene.GetTree();

            if (surf == Surf.Water) { WaterSplash(scene, point, 1f); PlaySound(scene, Snd(surf), point); return; }

            bool hard = surf is Surf.Concrete or Surf.Metal or Surf.Wood;
            bool metal = surf == Surf.Metal;

            // --- bullet-hole decal (hard surfaces), projected down the surface normal ---
            var dtex = hard ? DecalTex(surf) : null;
            if (dtex != null)
            {
                var dec = new Decal { TextureAlbedo = dtex, Size = new Vector3(0.55f, 0.35f, 0.55f), AlbedoMix = 1f, Modulate = new Color(0.22f, 0.21f, 0.20f) };   // DARK hole so it reads on light surfaces (a grey decal on a grey wall was invisible); bigger for range
                (attachTo ?? (Node)scene).AddChild(dec);
                Vector3 t = Mathf.Abs(up.Dot(Vector3.Up)) < 0.95f ? Vector3.Up : Vector3.Right;
                Vector3 right = up.Cross(t).Normalized();
                dec.GlobalTransform = new Transform3D(new Basis(right, up, right.Cross(up)), point + up * 0.06f);   // +Y = normal -> projects into the surface
                Kill(tree, dec, 18.0);
            }

            // --- debris burst (source Effects/Impacts/<mat>_static): a one-shot BURST of chips ---
            // concrete/wood/gravel/foliage = 8 @ 0.25-0.5m, 2-4 m/s; metal = 16 @ 0.125-0.25m, 4-8 m/s; gravityModifier 1,
            // ~1s life. The debris texture is a 4-frame chip sheet (a random chip per particle); metal is a single spark sprite.
            var itex = DebrisTex(surf);
            bool sheet = itex != null && itex.GetWidth() >= itex.GetHeight() * 3;
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,   // source: Unity Standard shader -> LIT (+ casts/receives shadow)
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.46f,   // source Cutout (_Mode 1, _Cutoff ~0.46)
                AlbedoColor = Colors.White,   // source startColor is white; the texture carries the material's colour
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                Roughness = 1f, Metallic = 0f, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            if (itex != null) mat.AlbedoTexture = itex;
            if (sheet) { mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }

            var dust = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Explosiveness = 1f,
                Amount = metal ? 16 : 8, Lifetime = 1.0f,
                Direction = up, Spread = 45f,   // source ShapeModule Cone angle 45deg
                InitialVelocityMin = metal ? 4f : 2f, InitialVelocityMax = metal ? 8f : 4f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleAmountMin = metal ? 0.3f : 0.5f, ScaleAmountMax = metal ? 0.6f : 1.0f,   // scaled up ~2x from source (0.25-0.5m) -- source size is invisible at play distance
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = Guard,   // <-- THE FIX: without this the fast chips are frustum-culled and the burst vanishes
            };
            if (sheet) { dust.AnimOffsetMin = 0f; dust.AnimOffsetMax = 1f; }
            scene.AddChild(dust);
            dust.GlobalPosition = point + up * 0.03f;
            Kill(tree, dust, 1.4);

            // readability element: a brief additive FLASH pop at the hit -- reads on ANY surface (the source debris chips
            // are near-invisible in a bright scene at range: grey chips on a grey wall vanish, which the point-blank +
            // dark-background --impacttest harness never revealed). The dark decal above is the persistent read.
            Flash(scene, point);

            PlaySound(scene, Snd(surf), point);
        }

        // The retail water splash (Effects/Impacts/water_static). LIT alpha-cutout droplets (Unity Standard Specular,
        // CUTOUT _Cutoff 0.5), NOT an additive sprite. scale 1 = a bullet plip (~10 droplets, 45deg cone, 3-6 m/s);
        // scale>=2 = an explosion column. Droplets shrink over life + tumble.
        public static void WaterSplash(Node scene, Vector3 point, float scale)
        {
            if (scene == null) return;
            bool big = scale >= 2f;
            var shrink = new Curve(); shrink.AddPoint(new Vector2(0f, 1f)); shrink.AddPoint(new Vector2(1f, 0f));
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                Roughness = 1f, Metallic = 0f, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                AlbedoColor = new Color(0.82f, 0.9f, 1f), AlbedoTexture = DebrisTex(Surf.Water),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            var ps = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Explosiveness = 1f, Randomness = 0.4f,
                Amount = big ? 96 : 10, Lifetime = big ? 2.5f : 1.0f,
                Direction = Vector3.Up, Spread = big ? 18f : 45f,
                InitialVelocityMin = big ? 8f : 3f, InitialVelocityMax = big ? 22f : 6f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleAmountMin = 0.28f * scale, ScaleAmountMax = 0.5f * scale, ScaleAmountCurve = shrink,
                AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -220f, AngularVelocityMax = 220f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.2f * scale,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = new Aabb(new Vector3(-8f, -8f, -8f) * scale, new Vector3(16f, 16f, 16f) * scale),
            };
            scene.AddChild(ps);
            ps.GlobalPosition = point;
            Kill(scene.GetTree(), ps, (big ? 2.5f : 1.0f) + 0.6f);
        }

        // Blood: a player/zombie hit (source Flesh_Dynamic, a 4-frame blood sheet). Sprays back along the shot, burst of
        // 16 @ 0.5-1.0m, 3-6 m/s, gravityModifier 1, ~1s. `dir` = shot direction (blood sprays -dir, back toward the shooter).
        public static void Blood(Node scene, Vector3 point, Vector3 dir)
        {
            if (scene == null) return;
            if (!_bloodTried) { _bloodTried = true; _blood = LoadTex("res://content/impact_flesh_dynamic_0.png"); }
            Vector3 d = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Forward;
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,   // source Flesh_Dynamic: Standard/Cutout, lit
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.46f,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                Roughness = 1f, Metallic = 0f, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                AlbedoColor = _blood != null ? Colors.White : new Color(0.5f, 0.02f, 0.02f),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            if (_blood != null) { mat.AlbedoTexture = _blood; mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }
            var ps = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Explosiveness = 1f,
                Amount = 16, Lifetime = 1.0f,
                Direction = -d, Spread = 60f,
                InitialVelocityMin = 3f, InitialVelocityMax = 6f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleAmountMin = 0.5f, ScaleAmountMax = 1.0f,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = Guard,   // blood was culled the same way the debris was
            };
            if (_blood != null) { ps.AnimOffsetMin = 0f; ps.AnimOffsetMax = 1f; }
            scene.AddChild(ps);
            ps.GlobalPosition = point;
            Kill(scene.GetTree(), ps, 1.4);
            if (!_bloodSndTried) { _bloodSndTried = true; _bloodSnd = LoadWav("res://content/impact_flesh.wav"); }
            PlaySound(scene, _bloodSnd, point);
        }

        static Texture2D FlashTex()
        {
            if (!_flashTried) { _flashTried = true; _flashTex = LoadTex("res://content/muzzleflash.png"); }
            return _flashTex;
        }

        // A brief bright ADDITIVE flash at the impact -- the eye-catcher that reads on ANY surface (additive is always
        // brighter than the background, light or dark). Short-lived; the dust + decal carry the lingering read.
        static void Flash(Node scene, Vector3 point)
        {
            if (scene == null) return;
            var shrink = new Curve(); shrink.AddPoint(new Vector2(0f, 1f)); shrink.AddPoint(new Vector2(1f, 0f));
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(1f, 0.88f, 0.55f),
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            var tex = FlashTex(); if (tex != null) mat.AlbedoTexture = tex;
            var ps = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Explosiveness = 1f,
                Amount = 3, Lifetime = 0.17f,
                Direction = Vector3.Up, Spread = 0f,
                InitialVelocityMin = 0f, InitialVelocityMax = 0f,
                ScaleAmountMin = 1.3f, ScaleAmountMax = 2.0f, ScaleAmountCurve = shrink,
                AngleMin = -180f, AngleMax = 180f,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = Guard,
            };
            scene.AddChild(ps);
            ps.GlobalPosition = point;
            Kill(scene.GetTree(), ps, 0.35);
        }

        static void PlaySound(Node scene, AudioStream a, Vector3 pos)
        {
            if (a == null || scene == null) return;
            var pl = new AudioStreamPlayer3D { Stream = a, UnitSize = 5f, MaxDistance = 70f, VolumeDb = -3f };
            scene.AddChild(pl);
            pl.GlobalPosition = pos;
            pl.Play();
            pl.Finished += () => { if (GodotObject.IsInstanceValid(pl)) pl.QueueFree(); };
        }

        static void Kill(SceneTree tree, Node n, double seconds)
        {
            if (tree == null) return;
            var t = tree.CreateTimer(seconds);
            t.Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
        }
    }
}
