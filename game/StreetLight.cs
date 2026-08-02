using Godot;

namespace UnturnedGodot
{
    // Cheap + pretty streetlight: a SOFT downward sodium pool (the real light) + a fading fake light-cone + an emissive
    // underside panel. Spawned per Street_Light_0 placement (WorldBuilder) in WORLD space at the lamp head, emitting from
    // the head's underside so it aims straight down regardless of the pole tilt. Colour TEMPERATURE is tweakable
    // (StreetLight.ColorTempK: 2000K warm sodium default for the BC towns; ~5000K cold-white LED for a city map).
    public partial class StreetLight : Node3D
    {
        public static float ColorTempK = 2000f;   // 2000K warm sodium ... 5000K cold LED
        public static float Energy     = 12.0f;    // ground-pool brightness (master: reined in from nuclear; raised source still gives the weight)
        public static int   MoteCount     = 26;     // dust/bug motes per lamp -- one additive quad each; 0 disables them entirely
        public static float MoteCullRange = 38f;    // motes retire well inside the cone's own draw distance: a close-up detail
        public const  float Watts      = 200f;     // realistic high-pressure-sodium draw (grid consumer)

        SpotLight3D _spot;
        CpuParticles3D _motes;   // dust/bugs drifting in the beam (strawberry); null when MoteCount is 0
        MeshInstance3D _cone, _panel;
        float _reach = 12f;
        float _worn = 1f;   // per-lamp brightness jitter (±5%) so fixtures read old/worn, not identical
        bool _night = false;    // dark enough to be lit (driven by DayNightCycle); starts off, self-inits in _Ready
        bool _powered = true;   // grid feeding the fixture (default on until a grid says otherwise)

        // Blackbody colour-temperature -> sRGB (Tanner Helland approximation). low K = orange, high K = blue-white.
        public static Color KelvinToColor(float kelvin)
        {
            float t = Mathf.Clamp(kelvin, 1000f, 12000f) / 100f;
            float r, g, b;
            if (t <= 66f) { r = 255f; g = 99.4708025861f * Mathf.Log(t) - 161.1195681661f; }
            else { r = 329.698727446f * Mathf.Pow(t - 60f, -0.1332047592f); g = 288.1221695283f * Mathf.Pow(t - 60f, -0.0755148492f); }
            if (t >= 66f) b = 255f;
            else if (t <= 19f) b = 0f;
            else b = 138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f;
            return new Color(Mathf.Clamp(r, 0f, 255f) / 255f, Mathf.Clamp(g, 0f, 255f) / 255f, Mathf.Clamp(b, 0f, 255f) / 255f);
        }

        public static StreetLight Make(Vector3 lampWorldPos, float reach)
        {
            // master: ±5% per-lamp brightness so they read old/worn. DETERMINISTIC (a stable hash of the world position),
            // so a given fixture is always the same brightness -- identical every load and for every MP player, no flicker.
            float h = Mathf.Sin(lampWorldPos.X * 12.9898f + lampWorldPos.Z * 78.233f) * 43758.5453f;
            float worn = 0.95f + (h - Mathf.Floor(h)) * 0.10f;   // 0.95 .. 1.05
            return new StreetLight { Position = lampWorldPos, TopLevel = true, _reach = Mathf.Max(4f, reach), _worn = worn };
        }

        // Vertical fade for the cone: bright at the lamp end, transparent by the base -- so the shaft dissolves into the
        // ground pool instead of ending in a hard rim. Mapped along the cylinder's V (height).
        static ImageTexture ConeGradient()
        {
            int n = 64;
            var img = Image.CreateEmpty(1, n, false, Image.Format.Rgba8);
            for (int y = 0; y < n; y++)
            {
                float topward = (float)y / (n - 1);                      // bright at the lamp end, 0 at the base (mesh V runs base->lamp)
                float a = Mathf.Pow(Mathf.Clamp(topward, 0f, 1f), 1.7f); // curved falloff
                img.SetPixel(0, y, new Color(1f, 1f, 1f, a));
            }
            return ImageTexture.CreateFromImage(img);
        }


        // Points inside the light cone (apex at the lamp, opening downward), for the mote emitter. Radius grows
        // with depth so the cloud is cone-shaped rather than a box that overhangs the beam. Deterministic per
        // lamp is unnecessary -- these are cosmetic dust, not simulation -- but the cloud is fixed at build time
        // so a given fixture keeps its own scatter.
        static Vector3[] ConePoints(float len, float coneR, int n)
        {
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = 0.12f + GD.Randf() * 0.83f;              // depth down the cone, avoiding the very apex/base
                float r = t * coneR * 0.92f * Mathf.Sqrt(GD.Randf());   // sqrt keeps them area-uniform, not centre-clumped
                float a = GD.Randf() * Mathf.Tau;
                pts[i] = new Vector3(Mathf.Cos(a) * r, -t * len, Mathf.Sin(a) * r);
            }
            return pts;
        }

        public override void _Ready()
        {
            AddToGroup("streetlights");
            var col = KelvinToColor(ColorTempK);
            var under = new Vector3(0f, -0.18f, 0f);   // middle of the head's UNDERSIDE -- light emits from here
            float half = 38f;                          // wide-ish cone / pool half-angle
            float len = _reach;

            // 1) THE REAL LIGHT: a soft downward pool on the ground. -Z is the beam axis -> pitch -90 aims it down.
            //    Wide angle + strong angle-attenuation = a soft-edged pool, not a hard disc.
            _spot = new SpotLight3D
            {
                // Light emits from the SAME point as the emissive panel (the head underside) so the glow and the beam are
                // one source -- raising the spot above the lamp is what made the "emissive spot look wrong". Weight/reach
                // comes from the wide angle + soft falloff instead. Pool spreads WIDER than the cone (42 > cone half 38).
                Position = under, RotationDegrees = new Vector3(-90f, 0f, 0f),
                SpotRange = len + 24f, SpotAngle = 42f, SpotAngleAttenuation = 1.9f, SpotAttenuation = 1.0f,
                LightColor = col, LightEnergy = Energy * _worn, ShadowEnabled = false,
            };
            AddChild(_spot);

            // 2) EMISSIVE UNDERSIDE PANEL: a flat disc on the head's underside that glows when on (the fixture reads as lit).
            _panel = new MeshInstance3D
            {
                Position = under,
                Mesh = new CylinderMesh { TopRadius = 0.26f, BottomRadius = 0.26f, Height = 0.03f, RadialSegments = 14, CapTop = true, CapBottom = true },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 4.5f * _worn,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_panel);

            // 3) THE FAKE CONE: a WIDE truncated cone that fades (gradient) from the lamp to transparent by the base, soft +
            //    additive. Wider at the base per master; the fade dissolves it into the ground pool.
            _cone = new MeshInstance3D
            {
                Position = under + new Vector3(0f, -len / 2f, 0f),
                Mesh = new CylinderMesh
                {
                    TopRadius = 0.14f, BottomRadius = len * Mathf.Tan(Mathf.DegToRad(half)) * 1.035f, Height = len,   // master: cone 10% smaller
                    RadialSegments = 20, Rings = 1, CapTop = false, CapBottom = false,
                },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(col.R, col.G, col.B, 0.07f * _worn),   // overall softness; the texture fades it lamp->base
                    AlbedoTexture = ConeGradient(),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // render the INSIDE of the cone too (strawberry): back-face
                                                                       // culling meant walking under a lamp showed nothing but a
                                                                       // hole. Additive + unshaded, so the far wall just adds a
                                                                       // second faint layer rather than double-darkening.
                    DisableReceiveShadows = true,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_cone);

            // 4) DUST / BUGS IN THE BEAM (strawberry): a handful of motes drifting inside the cone. Deliberately
            //    tiny -- MoteCount particles on ONE unshaded additive quad, no shadows, no collision, no attractor.
            //    Culled HARD: CustomAabb is set explicitly because a particle system's auto-computed bounds are
            //    derived from emitted positions and go wrong for slow drifters (they collapse toward the emitter
            //    and the whole system pops out at glancing angles), and VisibilityRangeEnd retires the motes well
            //    before the cone itself stops drawing -- they are a close-up detail, invisible at range anyway.
            if (MoteCount > 0)
            {
                float coneR = len * Mathf.Tan(Mathf.DegToRad(half));
                var moteMat = new StandardMaterial3D
                {
                    AlbedoColor = new Color(col.R, col.G, col.B, 0.95f * _worn),
                    EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.6f,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    DisableReceiveShadows = true,
                };
                _motes = new CpuParticles3D
                {
                    Position = under,
                    Amount = MoteCount,
                    Lifetime = 7f,
                    Preprocess = 7f,   // start at STEADY STATE. Amount/Lifetime = 2 motes a second, so without this a
                                       // lamp sits visibly empty for a full 7s every time night falls or it comes into
                                       // view -- which is most of the time you actually look at one.
                    Randomness = 1f,
                    Mesh = new QuadMesh { Size = new Vector2(0.0495f, 0.0495f) },   // ~5cm, trimmed 10% (strawberry)
                    // Emit from POINTS sampled inside the actual cone, not a box: a box around a cone spawns motes in
                    // the corners it never fills, so dust drifted in the dark outside the beam. Radius scales with
                    // depth so the cloud tapers exactly as the cone does.
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Points,
                    EmissionPoints = ConePoints(len, coneR, 48),
                    Direction = Vector3.Up, Spread = 180f,           // drift any which way, very slowly
                    InitialVelocityMin = 0.02f, InitialVelocityMax = 0.14f,
                    Gravity = new Vector3(0f, -0.03f, 0f),           // barely settling, so motes hang in the beam
                    ScaleAmountMin = 0.6f, ScaleAmountMax = 1.5f,
                    AngleMin = -180f, AngleMax = 180f,   // random spin per mote so they don't all read as the same aligned square
                                                          // (the material billboards in Particles mode, which honours the angle)
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = MoteCullRange,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                    CustomAabb = new Aabb(new Vector3(-coneR, -len, -coneR), new Vector3(coneR * 2f, len * 1.1f, coneR * 2f)),
                    MaterialOverride = moteMat,
                };
                AddChild(_motes);
            }

            // initial state: dark unless it's night AND the town grid is live; the DayNightCycle sweep drives both after.
            var dn = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            _night = dn == null || DayNightCycle.IsNightTime(dn.Time);   // no cycle in this mode -> default to "night" (lit if grid on)
            _powered = PowerNet.GlobalPower;                              // municipal grid feed (the town mains switch); default on
            Refresh();
        }

        // Grid hook: the fixture is drawing its Watts. Composited with the day/night state in Refresh().
        public void SetPowered(bool on) { if (_powered == on) return; _powered = on; Refresh(); }

        // Day/night hook (driven by DayNightCycle): street lamps light dusk->dawn and go dark by day.
        public void SetNight(bool on) { if (_night == on) return; _night = on; Refresh(); }

        /// <summary>Smashed pole -> the lamp is dead for good (until the prop respawns). This has to be STATE
        /// rather than a one-shot "turn it off": Refresh() re-derives lit from night+power on every day/night
        /// tick and every grid toggle, so a lamp merely switched off would light itself again at the next dusk
        /// while its pole lay in rubble. Same shape as the vehicle alarm relighting a wreck.</summary>
        public void SetBroken(bool broken) { if (_broken == broken) return; _broken = broken; Refresh(); }
        bool _broken;

        /// <summary>L1: a lamp lights with THREE separate things -- the real spot, the emissive lens, and the
        /// fake additive cone. "The light went out" has three independent failure modes, so a test asserts each
        /// rather than trusting one to stand for the others.</summary>
        public bool LitSpotForTest => _spot != null && _spot.LightEnergy > 0f;
        public bool LitPanelForTest => _panel != null && _panel.Visible;
        public bool LitConeForTest => _cone != null && _cone.Visible;
        public bool LitMotesForTest => _motes != null && _motes.Emitting;

        // A lamp glows only when it's dark AND the grid is feeding it -- and isn't smashed. Toggles the real
        // spot + the emissive lens + the cone.
        void Refresh()
        {
            bool lit = _night && _powered && !_broken;
            if (_spot != null) _spot.LightEnergy = lit ? Energy * _worn : 0f;
            if (_panel != null) _panel.Visible = lit;
            if (_cone != null) _cone.Visible = lit;
            if (_motes != null) { _motes.Visible = lit; _motes.Emitting = lit; }   // no sim cost at all on an unlit/broken lamp
        }
    }
}
