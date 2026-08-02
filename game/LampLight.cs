using Godot;

namespace UnturnedGodot
{
    // Simple indoor light fixture (Lamp_0 ceiling light, Lamp_1 standing lamp), sharing the grid-power /
    // reaction-delay-flicker machinery with StreetLight via GridLight. Unlike a streetlight an indoor lamp is NOT
    // night-gated -- it is on WHENEVER the grid is live (master). The visual is deliberately simple compared to
    // StreetLight: a warm OmniLight3D plus a small emissive sphere standing in for the visible bulb -- there is no
    // prop lens to split and no long throw to fake with a cone, so neither of StreetLight's two hardest problems
    // apply here.
    public partial class LampLight : GridLight
    {
        public static Color BulbColor = new Color(1f, 0.86f, 0.66f);   // warm white (incandescent-ish)
        public static float Range = 7f;               // OmniLight3D radius -- a room, not a street
        public static float Energy = 2.0f;             // base LightEnergy, scaled by _worn like StreetLight.Energy
        public static float GlowRadius = 0.08f;        // the stand-in bulb sphere
        public static float GlowEmission = 3.0f;        // emission multiplier -- SHADED material (not Unshaded), same
                                                        // reason as StreetLight's lens: Unshaded drops EMISSION, so a
                                                        // shaded material is what actually reaches HDR and blooms.

        OmniLight3D _omni;
        MeshInstance3D _glow;

        protected override bool NightGated => false;             // always-on when powered (master), not dusk->dawn
        protected override string LightGroup => "gridlights";    // DayNightCycle sweeps this with SetPowered only (no SetNight)

        public static LampLight Make(Vector3 worldPos)
        {
            var l = new LampLight { Position = worldPos, TopLevel = true };
            l.InitJitter(worldPos);   // per-fixture brightness jitter + reaction-delay fraction (GridLight)
            return l;
        }

        // Both emitters sit at the origin: the node itself is already placed at the bulb point by WorldBuilder.
        protected override void BuildVisual()
        {
            _omni = new OmniLight3D
            {
                LightColor = BulbColor, OmniRange = Range, LightEnergy = Energy * _worn, ShadowEnabled = false,
            };
            AddChild(_omni);

            _glow = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = GlowRadius, Height = GlowRadius * 2f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = BulbColor, EmissionEnabled = true, Emission = BulbColor,
                    EmissionEnergyMultiplier = GlowEmission * _worn, Metallic = 0f, Roughness = 0.4f,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_glow);
        }

        protected override void ApplyLit(bool lit)
        {
            if (_omni != null) _omni.LightEnergy = lit ? Energy * _worn : 0f;
            if (_glow != null) _glow.Visible = lit;
        }

        public bool LitForTest => _omni != null && _omni.LightEnergy > 0f;
    }
}
