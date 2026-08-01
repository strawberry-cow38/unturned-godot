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
        public const  float Watts      = 200f;     // realistic high-pressure-sodium draw (grid consumer)

        SpotLight3D _spot;
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
                    CullMode = BaseMaterial3D.CullModeEnum.Back,
                    DisableReceiveShadows = true,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_cone);

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

        // A lamp glows only when it's dark AND the grid is feeding it. Toggles the real spot + the emissive lens + the cone.
        void Refresh()
        {
            bool lit = _night && _powered;
            if (_spot != null) _spot.LightEnergy = lit ? Energy * _worn : 0f;
            if (_panel != null) _panel.Visible = lit;
            if (_cone != null) _cone.Visible = lit;
        }
    }
}
