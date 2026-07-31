using Godot;
using System.IO;

namespace UnturnedGodot
{
    /// <summary>
    /// The marker a landed supply drop leaves behind: a rising smoke column and a thump.
    ///
    /// Retail's Carepackage.OnCollisionEnter triggers effect 2c17fbd0f0ce49aeb3bc4637b68809a2 --
    /// "Carepackage Flare" -- reliable and at EffectManager.INSANE relevant distance, so it goes to
    /// everyone on the map, not just whoever is nearby. That is the point of it. The plane telegraphs
    /// the drop while it is in the air; the column telegraphs it after, for the people who were looking
    /// somewhere else.
    ///
    /// The audio deliberately does NOT travel that far: retail's AudioSource is 3D with a 32 m max
    /// distance. Everyone sees the smoke, only the neighbours hear it land.
    ///
    /// Numbers come from tools/extract_carepackage_fx.py reading the prefab, not from taste.
    /// </summary>
    public partial class CarepackageFlare : Node3D
    {
        sealed class Def
        {
            public float Duration = 60f, Lifetime = 20f, SizeMin = 1.5f, SizeMax = 3f, Rate = 10f;
            public int MaxParticles = 200;
            public float ConeAngle = 45f, ConeRadius = 0.25f;
            public float RiseMin = 1.8f, RiseMax = 2.2f, Drift = 0.025f, SpinMax = 0.2618f;
            public float Volume = 0.5f, MinDistance = 1f, MaxDistance = 32f;
            public ImageTexture Smoke;
            public AudioStream Land;
        }

        static Def _def;
        static bool _loaded;

        /// <summary>Drop a marker at a landing point. Static because a caller has a position and wants
        /// an effect, not a node to own -- this cleans itself up once the column has burned out.</summary>
        public static void Spawn(Node parent, Vector3 at)
        {
            var f = new CarepackageFlare();
            parent.AddChild(f);
            f.GlobalPosition = at;
            f.Build();
        }

        void Build()
        {
            var d = Load();

            if (d.Smoke != null)
            {
                var mat = new StandardMaterial3D
                {
                    // Legacy Shaders/Particles/Additive (Soft): additive, unshaded, no depth write, so
                    // the column glows against the sky rather than reading as grey cardboard.
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    AlbedoTexture = d.Smoke,
                    AlbedoColor = Colors.White,
                    NoDepthTest = false,
                };
                var ps = new CpuParticles3D
                {
                    Emitting = true,
                    // Rate x lifetime, capped where the prefab caps it. Godot has no separate emit rate:
                    // Amount over Lifetime IS the rate, so the count has to be derived rather than copied.
                    Amount = Mathf.Min(d.MaxParticles, Mathf.RoundToInt(d.Rate * d.Lifetime)),
                    Lifetime = d.Lifetime,
                    OneShot = false,
                    Randomness = 1f,
                    Direction = Vector3.Up,
                    // Spread stays at ZERO even though the prefab's shape is a 45-degree cone. The cone
                    // only steers a particle that has launch speed, and startSpeed here is 0 -- the
                    // column climbs entirely from velocity-over-lifetime, straight up at ~2 m/s with
                    // 0.025 m/s of drift. Feeding the rise speed through a 45-degree spread (the obvious
                    // reading of the two modules together) turns a narrow plume into a 40 m fan of
                    // confetti, which is what it looked like before this line said zero.
                    Spread = 0f,
                    InitialVelocityMin = d.RiseMin, InitialVelocityMax = d.RiseMax,
                    Gravity = Vector3.Zero,                       // smoke does not fall
                    ScaleAmountMin = d.SizeMin, ScaleAmountMax = d.SizeMax,
                    ScaleAmountCurve = GrowThenFade(),            // the prefab's size-over-lifetime curve
                    AngleMin = -180f, AngleMax = 180f,
                    AngularVelocityMin = -Mathf.RadToDeg(d.SpinMax), AngularVelocityMax = Mathf.RadToDeg(d.SpinMax),
                    ColorRamp = FadeOut(),                        // alpha 1 -> 0 across the life
                    // The shape's only remaining job: scatter the birth POSITION over its 0.25 m base,
                    // so the plume has some width at the bottom rather than being a single line.
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
                    EmissionSphereRadius = Mathf.Max(d.ConeRadius, 0.05f),
                    Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                    // Without an explicit AABB a fast, tall particle system gets culled the moment its
                    // ORIGIN leaves the frustum -- the column vanishes when you look up at the top of it.
                    VisibilityAabb = new Aabb(new Vector3(-15f, -2f, -15f), new Vector3(30f, 60f, 30f)),
                };
                AddChild(ps);
                // Stop emitting when the prefab does, then let the last particles live out their span.
                Fire(d.Duration, () => { if (IsInstanceValid(ps)) ps.Emitting = false; });
            }

            if (d.Land != null)
            {
                var a = new AudioStreamPlayer3D
                {
                    Stream = d.Land,
                    VolumeDb = Mathf.LinearToDb(d.Volume),
                    UnitSize = d.MinDistance,
                    MaxDistance = d.MaxDistance,
                    Autoplay = true,
                };
                AddChild(a);
            }

            Fire(d.Duration + d.Lifetime + 1f, QueueFree);
        }

        void Fire(float seconds, System.Action then)
        {
            var t = new Timer { WaitTime = seconds, OneShot = true, Autostart = true };
            t.Timeout += () => then();
            AddChild(t);
        }

        /// <summary>The prefab's size-over-lifetime: 0 at birth, ~0.96 at 83%, 0 at death. A puff that
        /// pops into existence at full size and vanishes at full size reads as a sprite being switched
        /// on and off, which is exactly what the curve is there to avoid.</summary>
        static Curve GrowThenFade()
        {
            var c = new Curve();
            c.AddPoint(new Vector2(0f, 0f));
            c.AddPoint(new Vector2(0.83f, 0.96f));
            c.AddPoint(new Vector2(1f, 0f));
            return c;
        }

        static Gradient FadeOut()
        {
            var g = new Gradient();
            g.SetColor(0, new Color(1f, 1f, 1f, 1f));
            g.SetColor(1, new Color(1f, 1f, 1f, 0f));
            return g;
        }

        static Def Load()
        {
            if (_loaded) return _def;
            _loaded = true;
            _def = new Def();

            string tex = ProjectSettings.GlobalizePath("res://content/carepackage_smoke.png");
            if (File.Exists(tex))
            {
                var img = Image.LoadFromFile(tex);
                if (img != null) _def.Smoke = ImageTexture.CreateFromImage(img);
            }
            string wav = ProjectSettings.GlobalizePath("res://content/carepackage_land.wav");
            if (File.Exists(wav)) _def.Land = AudioStreamWav.LoadFromFile(wav);

            string js = ProjectSettings.GlobalizePath("res://content/carepackage_fx.json");
            if (!File.Exists(js))
            {
                GD.Print("[flare] no carepackage_fx.json -- using the prefab defaults baked into Def");
                return _def;
            }
            var parsed = Json.ParseString(File.ReadAllText(js));
            if (parsed.VariantType != Variant.Type.Dictionary) return _def;
            var d = parsed.AsGodotDictionary();
            _def.Duration = F(d, "duration", _def.Duration);
            _def.MaxParticles = Mathf.RoundToInt(F(d, "max_particles", _def.MaxParticles));
            _def.Rate = F(d, "rate_per_second", _def.Rate);
            _def.ConeAngle = F(d, "cone_angle", _def.ConeAngle);
            _def.ConeRadius = F(d, "cone_radius", _def.ConeRadius);
            var life = Pair(d, "lifetime", _def.Lifetime, _def.Lifetime);
            _def.Lifetime = life.Y;
            var size = Pair(d, "size", _def.SizeMin, _def.SizeMax);
            _def.SizeMin = size.X; _def.SizeMax = size.Y;
            var rise = Pair(d, "rise", _def.RiseMin, _def.RiseMax);
            _def.RiseMin = rise.X; _def.RiseMax = rise.Y;
            _def.Drift = Mathf.Abs(Pair(d, "drift", -_def.Drift, _def.Drift).Y);
            _def.SpinMax = Mathf.Abs(Pair(d, "spin_rad_per_sec", -_def.SpinMax, _def.SpinMax).Y);
            if (d.ContainsKey("audio"))
            {
                var a = d["audio"].AsGodotDictionary();
                _def.Volume = F(a, "volume", _def.Volume);
                _def.MinDistance = F(a, "min_distance", _def.MinDistance);
                _def.MaxDistance = F(a, "max_distance", _def.MaxDistance);
            }
            return _def;
        }

        static float F(Godot.Collections.Dictionary d, string k, float fallback) =>
            d.ContainsKey(k) ? (float)d[k].AsDouble() : fallback;

        static Vector2 Pair(Godot.Collections.Dictionary d, string k, float lo, float hi)
        {
            if (!d.ContainsKey(k)) return new Vector2(lo, hi);
            var arr = d[k].AsGodotArray();
            return arr.Count >= 2 ? new Vector2((float)arr[0].AsDouble(), (float)arr[1].AsDouble())
                                  : new Vector2(lo, hi);
        }
    }
}
