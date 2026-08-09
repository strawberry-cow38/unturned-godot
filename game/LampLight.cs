using Godot;

namespace UnturnedGodot
{
    // Simple indoor light fixture (Light_0 = CEILING light, Lamp_0 = desk lamp, Lamp_1 = table lamp -- master
    // 2026-08-09 corrected the earlier wrong labels; master also names a "standing lamp" Light_1, but it is NOT yet
    // extracted to content/, so the Light_1 arm of WorldBuilder's condition is inert-but-ready), sharing the grid-power /
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
        public static bool DebugNoOmni;                // --lamptest UG_LAMP_NOOMNI=1: kill the room OmniLight so ONLY the emissive tube shows (proves the split)

        OmniLight3D _omni;
        MeshInstance3D _fixture;               // the prop's own mesh (LOD0), handed in by WorldBuilder
        MeshInstance3D _tube;                  // the bulb/tube sub-mesh split off the fixture -- the ONLY part that glows
        Material _fixtureOffMat, _fixtureLitMat;
        bool _built;                           // BuildVisual is idempotent (guarded) -- a 2nd SplitLens(body) has no lens tris and would fall back to glowing the housing

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
            if (_built) return;   // a 2nd call would SplitLens(body) -> no lens -> glow falls back to the housing = the bug restored (tinyclaw)
            _built = true;
            _omni = new OmniLight3D
            {
                LightColor = BulbColor, OmniRange = Range, LightEnergy = Energy * _worn, ShadowEnabled = false,
            };
            AddChild(_omni);

            if (_fixture == null || !IsInstanceValid(_fixture)) return;
            // Precompute the lit material once: keep its albedo/texture, add warm emission on top.
            _fixtureOffMat = _fixture.MaterialOverride;
            var lit = (_fixtureOffMat as StandardMaterial3D)?.Duplicate() as StandardMaterial3D
                      ?? new StandardMaterial3D { AlbedoColor = BulbColor };
            lit.EmissionEnabled = true;
            lit.Emission = BulbColor;
            lit.EmissionEnergyMultiplier = FixtureEmission * _worn;
            _fixtureLitMat = lit;

            // Glow ONLY the bulb/tube, not the whole housing (master 2026-08-09). SplitLens carves the triangles on
            // the palette's warm entry -- the same "the tan texel IS the bulb" identity the streetlight lens uses, and
            // after Load's V-flip Light_0's fluorescent-tube entry lands in that same u>0.5,v>0.5 quadrant. So the tubes
            // glow while the grey housing stays matte. A fixture with no distinct bulb texel (SplitLens returns no lens)
            // falls back to glowing the whole small fixture.
            var (body, tube) = ObjMesh.SplitLens(_fixture.Mesh as ArrayMesh);
            if (tube != null)
            {
                _fixture.Mesh = body;                                                        // housing keeps its matte material
                _tube = new MeshInstance3D { Name = "Tube", Mesh = tube, MaterialOverride = _fixtureOffMat };
                _fixture.AddChild(_tube);                                                     // overlays the fixture in its own local space
            }
        }

        protected override void ApplyLit(bool lit)
        {
            if (_omni != null) _omni.LightEnergy = (lit && !DebugNoOmni) ? Energy * _worn : 0f;
            if (_fixtureLitMat == null) return;
            // Swap ONLY the tube's material to emissive (if we split one off); the housing keeps its matte material.
            // (Bug fix: this used to swap _fixture itself -- but once BuildVisual sets _fixture.Mesh = body, that glows
            //  the HOUSING and leaves the tube matte, i.e. the exact "whole thing glows" master rejected.)
            var glow = (_tube != null && IsInstanceValid(_tube)) ? _tube : _fixture;
            if (glow != null && IsInstanceValid(glow))
                glow.MaterialOverride = lit ? _fixtureLitMat : _fixtureOffMat;
        }

        public bool LitForTest => _omni != null && _omni.LightEnergy > 0f;
    }
}
