using Godot;

namespace UnturnedGodot
{
    // Simple indoor light fixture (Light_0 = CEILING light, Light_1 = standing lamp, Lamp_0 = desk lamp, Lamp_1 =
    // table lamp -- master 2026-08-09 corrected the earlier wrong labels), sharing the grid-power /
    // reaction-delay-flicker machinery with StreetLight via GridLight. Unlike a streetlight an indoor lamp is NOT
    // night-gated -- it is ON WHENEVER the grid is live (master).
    //
    // The visible "on" look is the FIXTURE MESH itself glowing warm. An OmniLight alone lights the ROOM but leaves
    // the fixture dark: the light sits inside the housing, so it lights the fixture's interior faces, not the
    // exterior ones the player sees -- the fixture read as OFF even while it was emitting (master caught this). So
    // ApplyLit swaps the fixture's material to an emissive copy (same trick as StreetLight's lens; SHADED so the
    // emission reaches HDR + the bloom pass, not Unshaded which drops emission).
    public partial class LampLight : GridLight
    {
        public static Color BulbColor = new Color(1f, 0.90f, 0.72f);   // warm white (incandescent-ish)
        public static float Range = 8f;                // OmniLight3D radius -- a room, not a street
        public static float Energy = 2.2f;             // base LightEnergy, scaled by _worn like StreetLight.Energy
        public static float FixtureEmission = 0.9f;    // fixture-glow emission multiplier (SHADED -> HDR/bloom); a warm glow, not blown-out white

        OmniLight3D _omni;
        MeshInstance3D _fixture;               // the prop's own mesh (LOD0), handed in by WorldBuilder -- glows when lit
        Material _fixtureOffMat, _fixtureLitMat;

        protected override bool NightGated => false;             // always-on when powered (master), not dusk->dawn
        protected override string LightGroup => "gridlights";    // DayNightCycle sweeps this with SetPowered only (no SetNight)

        public static LampLight Make(Vector3 worldPos, MeshInstance3D fixture = null)
        {
            var l = new LampLight { Position = worldPos, TopLevel = true, _fixture = fixture };
            l.InitJitter(worldPos);   // per-fixture brightness jitter + reaction-delay fraction (GridLight)
            return l;
        }

        protected override void BuildVisual()
        {
            _omni = new OmniLight3D
            {
                LightColor = BulbColor, OmniRange = Range, LightEnergy = Energy * _worn, ShadowEnabled = false,
            };
            AddChild(_omni);

            // Precompute the fixture's lit material once: keep its albedo/texture, add warm emission on top.
            if (_fixture != null && IsInstanceValid(_fixture))
            {
                _fixtureOffMat = _fixture.MaterialOverride;
                var lit = (_fixtureOffMat as StandardMaterial3D)?.Duplicate() as StandardMaterial3D
                          ?? new StandardMaterial3D { AlbedoColor = BulbColor };
                lit.EmissionEnabled = true;
                lit.Emission = BulbColor;
                lit.EmissionEnergyMultiplier = FixtureEmission * _worn;
                _fixtureLitMat = lit;
            }
        }

        protected override void ApplyLit(bool lit)
        {
            if (_omni != null) _omni.LightEnergy = lit ? Energy * _worn : 0f;
            if (_fixture != null && IsInstanceValid(_fixture) && _fixtureLitMat != null)
                _fixture.MaterialOverride = lit ? _fixtureLitMat : _fixtureOffMat;
        }

        public bool LitForTest => _omni != null && _omni.LightEnergy > 0f;
    }
}
