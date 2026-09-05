using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>The two things a non-explosive throwable leaves behind: a coloured smoke cloud and a burning
    /// flare (strawberry 2026-09-05: "smoke grenades ... emit smoke in a radius, coloured depending on the
    /// colour of the one you threw. flares glow and flicker brightly, giving off sparks for a period before
    /// dissipating. smoke also dissipates after a while").
    ///
    /// Both DISSIPATE rather than vanish, and both do it the same way: stop EMITTING at the end of the
    /// declared burn, then free one particle-lifetime later so the last puff still gets to fade out on its own
    /// colour ramp. A node that QueueFree'd on the timer would make a full cloud disappear between frames,
    /// which is the one thing "dissipates after a while" is not.</summary>
    public partial class SmokeCloud : Node3D
    {
        public Color Tint = new Color(0.8f, 0.8f, 0.8f);
        public float Radius = 6f;
        public float Duration = 22f;

        const float PuffLife = 6.5f;   // how long ONE puff lives -- also the post-stop grace before the node frees

        CpuParticles3D _p;
        float _t;

        public bool Emitting => _p != null && _p.Emitting;   // test seam: has the cloud stopped producing yet?

        public override void _Ready()
        {
            _p = new CpuParticles3D
            {
                // 220, not 90. At 90 the puffs stayed visually SEPARATE -- the render showed a cluster of
                // distinguishable blobs where a smokescreen has to read as one body you cannot see through.
                // Density is the lever, not size: the particles were already 3-5 m across and overlapping
                // barely, so making them bigger would only have made bigger blobs.
                Emitting = true, OneShot = false, Amount = 220, Lifetime = PuffLife, Explosiveness = 0f,
                // Puffs are BORN spread through the volume, not fired from a point: a smokescreen is a body of
                // smoke you cannot see through, and a jet from one spot reads as a chimney.
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = Radius * 0.55f,
                Direction = Vector3.Up, Spread = 60f,
                InitialVelocityMin = 0.35f, InitialVelocityMax = 1.4f,
                Gravity = new Vector3(0f, 0.35f, 0f),   // smoke RISES, slowly -- positive gravity, not the world's -9.81
                DampingMin = 0.3f, DampingMax = 0.8f,
                ScaleAmountMin = Radius * 0.5f, ScaleAmountMax = Radius * 0.95f,
                AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -12f, AngularVelocityMax = 12f,
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
                    VertexColorUseAsAlbedo = true,
                    AlbedoTexture = PlayerController.BlastSoftTex(), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            // Fade IN as well as out. A puff that is born at full opacity pops; the smokescreen wants to look
            // like it is being made, not stamped.
            var ramp = new Gradient();
            ramp.SetColor(0, new Color(Tint, 0f));
            ramp.SetColor(1, new Color(Tint, 0f));
            ramp.AddPoint(0.08f, new Color(Tint, 0.88f));   // reach full opacity SOONER: at 0.12 the newest puffs
            ramp.AddPoint(0.70f, new Color(Tint, 0.72f));   // were still translucent, which is what made the mass read as separate pieces
            _p.ColorRamp = ramp;
            var grow = new Curve(); grow.AddPoint(new Vector2(0f, 0.35f)); grow.AddPoint(new Vector2(1f, 1f));
            _p.ScaleAmountCurve = grow;
            // Without this the whole cloud disappears the moment the emitter's ORIGIN leaves the frustum, which
            // for a 6 m cloud you are standing inside is most of the time you can see it.
            float v = Radius * 4f;
            _p.VisibilityAabb = new Aabb(new Vector3(-v, -v, -v), new Vector3(v * 2f, v * 2f, v * 2f));
            AddChild(_p);
        }

        public override void _Process(double delta)
        {
            _t += (float)delta;
            if (_p == null) return;
            if (_p.Emitting && _t >= Duration) _p.Emitting = false;        // stop making smoke...
            else if (!_p.Emitting && _t >= Duration + PuffLife) QueueFree();  // ...and leave once the last puff has faded
        }
    }

    /// <summary>A lit road flare lying where it landed: a coloured light that flickers, a spit of sparks, and a
    /// thin plume in its own colour, all of it winding down to nothing at the end of the burn.
    ///
    /// The flicker is deliberately NOT a sine. A flare is a chemical fire and its light is noisy; a clean
    /// oscillation reads as a machine, which is exactly what the first pass looked like. Two detuned sines plus
    /// a per-frame jitter gives a signal with no period a viewer can lock onto.</summary>
    public partial class FlareBurn : Node3D
    {
        public Color Tint = new Color(0.9f, 0.25f, 0.2f);
        public float Duration = 45f;

        const float FadeTail = 4f;    // the last seconds ramp everything to zero, so it dies down instead of switching off

        OmniLight3D _light, _glow;
        CpuParticles3D _sparks, _plume;
        MeshInstance3D _core;
        float _t, _seed;
        float _baseEnergy = 9f;    // 3.4 lit almost nothing -- no pool on the ground at all in the first render

        public float Elapsed => _t;   // test seam

        public override void _Ready()
        {
            _seed = (float)GD.RandRange(0.0, 100.0);

            // The light. Tinted toward WHITE rather than the raw palette colour: a real flare's flame is much
            // hotter than its casing, and lighting the ground in flat saturated purple looks like a filter.
            _light = new OmniLight3D
            {
                LightColor = Tint.Lerp(Colors.White, 0.35f),
                LightEnergy = _baseEnergy, OmniRange = 22f, ShadowEnabled = false,
                // RAISED off the deck, and this is a bug fix rather than a taste call. Both lights sat at the
                // node origin, which after the flare settles is ~6 cm above flat ground -- and a point light that
                // close to a horizontal surface has N.L ~ 0 everywhere except directly beneath it, so it lit
                // essentially nothing however far its energy was cranked. The first two renders showed no pool at
                // all at energy 3.4 and then at 9; the geometry was the problem, not the brightness.
                Position = new Vector3(0f, 0.45f, 0f),
            };
            AddChild(_light);
            // A second, wide, dim light purely for the POOL on the ground. One omni bright enough to throw a
            // pool blows out its own core into a white disc; splitting the job keeps the burning tip readable
            // while the ground still says "something is burning here".
            _glow = new OmniLight3D
            {
                LightColor = Tint.Lerp(Colors.White, 0.15f),
                LightEnergy = _baseEnergy * 0.45f, OmniRange = 46f, ShadowEnabled = false,
                Position = new Vector3(0f, 0.9f, 0f),   // higher still: the wide pool wants the shallowest grazing angle of the two
            };
            AddChild(_glow);

            // The burning tip itself -- a small unshaded blob so the source of the light is visible, not just
            // its effect on the ground.
            _core = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.075f, Height = 0.15f },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = Tint.Lerp(Colors.White, 0.6f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                },
                Position = new Vector3(0f, 0.06f, 0f),
            };
            AddChild(_core);

            _sparks = new CpuParticles3D
            {
                Emitting = true, OneShot = false, Amount = 90, Lifetime = 0.85f, Explosiveness = 0f,   // 34 read as a thin wisp rather than a spit of sparks
                Direction = Vector3.Up, Spread = 42f,
                InitialVelocityMin = 1.6f, InitialVelocityMax = 4.2f,
                Gravity = new Vector3(0f, -7.5f, 0f),   // sparks ARC: thrown up, pulled down, bright the whole way
                DampingMin = 0.5f, DampingMax = 1.5f,
                ScaleAmountMin = 0.05f, ScaleAmountMax = 0.12f,
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,   // ADDITIVE: a spark is emitted light, so it should blow out white where several overlap
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
                    VertexColorUseAsAlbedo = true,
                    AlbedoTexture = PlayerController.BlastSoftTex(), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
                Position = new Vector3(0f, 0.06f, 0f),
            };
            var sr = new Gradient();
            sr.SetColor(0, new Color(1f, 1f, 0.92f, 1f));                       // white hot at birth...
            sr.SetColor(1, new Color(Tint.R, Tint.G, Tint.B, 0f));              // ...cooling to the flare's own colour as it dies
            sr.AddPoint(0.3f, new Color(Tint.Lerp(Colors.White, 0.5f), 0.95f));
            _sparks.ColorRamp = sr;
            _sparks.VisibilityAabb = new Aabb(new Vector3(-8f, -8f, -8f), new Vector3(16f, 16f, 16f));
            AddChild(_sparks);

            _plume = new CpuParticles3D
            {
                Emitting = true, OneShot = false, Amount = 22, Lifetime = 3.2f, Explosiveness = 0f,
                Direction = Vector3.Up, Spread = 22f,
                InitialVelocityMin = 0.6f, InitialVelocityMax = 1.5f,
                Gravity = new Vector3(0f, 0.5f, 0f),
                DampingMin = 0.4f, DampingMax = 1f,
                ScaleAmountMin = 0.35f, ScaleAmountMax = 0.9f,
                AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -20f, AngularVelocityMax = 20f,
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
                    VertexColorUseAsAlbedo = true,
                    AlbedoTexture = PlayerController.BlastSoftTex(), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
                Position = new Vector3(0f, 0.1f, 0f),
            };
            var pr = new Gradient();
            pr.SetColor(0, new Color(Tint, 0f)); pr.SetColor(1, new Color(Tint, 0f));
            pr.AddPoint(0.15f, new Color(Tint, 0.42f));
            _plume.ColorRamp = pr;
            _plume.VisibilityAabb = new Aabb(new Vector3(-12f, -12f, -12f), new Vector3(24f, 24f, 24f));
            AddChild(_plume);
        }

        public override void _Process(double delta)
        {
            _t += (float)delta;
            float left = Duration - _t;
            if (left <= 0f) { QueueFree(); return; }

            // Wind everything down over the last seconds instead of cutting it. Emitting goes false first so the
            // sparks in flight still land.
            float fade = Mathf.Clamp(left / FadeTail, 0f, 1f);
            if (fade < 0.35f)
            {
                if (_sparks != null) _sparks.Emitting = false;
                if (_plume != null) _plume.Emitting = false;
            }

            float ph = _t * 1.0f + _seed;
            // Two incommensurable rates plus a per-frame jitter: nothing here repeats on a period the eye finds.
            float flicker = 0.82f
                          + 0.10f * Mathf.Sin(ph * 11.3f)
                          + 0.06f * Mathf.Sin(ph * 27.7f + 1.7f)
                          + 0.06f * (float)GD.RandRange(-1.0, 1.0);
            if (_light != null)
            {
                _light.LightEnergy = _baseEnergy * flicker * fade;
                _light.OmniRange = 22f * (0.92f + 0.08f * flicker) * Mathf.Max(0.2f, fade);
            }
            if (_glow != null) _glow.LightEnergy = _baseEnergy * 0.45f * flicker * fade;   // the pool breathes with the flame, at the same rate
            if (_core != null && _core.MaterialOverride is StandardMaterial3D cm)
                cm.AlbedoColor = new Color(Tint.Lerp(Colors.White, 0.6f), Mathf.Clamp(flicker, 0f, 1f) * fade);
        }
    }
}
