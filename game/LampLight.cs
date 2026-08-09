using Godot;

namespace UnturnedGodot
{
    // Indoor light fixture (subclass of GridLight, sibling of StreetLight). NOT night-gated -- ON whenever the grid
    // is live (master). Shares the grid-power + reaction-delay-flicker machinery. The visible "on" look is the
    // light-EMITTING part of the mesh glowing warm (emissive material) -- an OmniLight alone lights the room but
    // leaves the fixture reading OFF. WHICH part glows and WHERE the point light sits is PER FIXTURE (master
    // 2026-08-09, tinyclaw texel/geometry recon):
    //
    //   CeilingStrip (Light_0) : SplitLens carves the warm diffuser (palette u>0.5,v>0.5); omni drops just BELOW the
    //                            prop, so a flush ceiling strip lights the room from under its diffuser, not its housing.
    //   FloorShade   (Lamp_1)  : the shade is the 181-grey TOP-LEFT texel -> u<0.5,v>0.5 after Load's V-flip, which
    //                            SplitLens misses; a UV-centroid split carves it. Omni sits at the shade centre.
    //   DeskBulb     (Lamp_0)  : no bulb texel exists; the head "opening" face is emissive by a geometry rule. Omni
    //                            sits just IN FRONT of that face, at HALF energy.  (geometry split TBD render-pick)
    //   Generic                : no distinct emitter -> the whole small fixture glows; omni at its centre.
    //
    // Every position is taken off the fixture's REAL GlobalTransform, never a fixed axis swizzle: most placements are
    // ex=270 but not all (one Lamp_1 is ex=295.019 -- tinyclaw), so a hardcoded axis would hang that light in the air.
    public partial class LampLight : GridLight
    {
        public enum Kind { Generic, CeilingStrip, FloorShade, DeskBulb }

        // ONE source of truth for prop-name -> kind + which kinds are player-toggleable, so WorldBuilder and
        // --lamptest can't drift into disagreeing (tinyclaw: keep the prop list next to the kind table, not as a
        // separate name test per caller).
        public static Kind KindFor(string name) => name switch
        {
            "Light_0" or "Light_1" => Kind.CeilingStrip,
            "Lamp_1"               => Kind.FloorShade,
            "Lamp_0"               => Kind.DeskBulb,
            _                      => Kind.Generic,
        };
        // The STANDING (Lamp_1) + DESK (Lamp_0) lamps get the look-outline + F on/off toggle (master 2026-08-09);
        // the ceiling strip is grid-only, no manual switch.
        public static bool IsToggle(Kind k) => k is Kind.FloorShade or Kind.DeskBulb;
        public const string LookMeta = "lampdevice";   // meta on the prop body collider -> this lamp (PlayerController look-ray)

        public static Color BulbColor = new Color(1f, 0.90f, 0.72f);   // warm white (incandescent-ish)
        public static float Range = 8f;                // OmniLight3D radius -- a room, not a street
        public static float Energy = 2.2f;             // base LightEnergy, scaled by _worn
        public static float FixtureEmission = 0.9f;    // emissive multiplier (SHADED -> HDR/bloom); a warm glow, not blown white
        public static bool DebugNoOmni;                // --lamptest UG_LAMP_NOOMNI=1: kill the room OmniLight so only the emissive shows
        public static float DebugBulbSide = 1f;        // DeskBulb render-pick: +1 = the +X-facing head opening (f18/f19), -1 = -X (f0/f1)

        Kind _kind = Kind.Generic;
        public Kind LampKind => _kind;         // so WorldBuilder can gate the look-meta on IsToggle without re-deriving from the name
        OmniLight3D _omni;
        float _omniEnergy;                     // resolved energy (incl. the DeskBulb half-scale), reused by ApplyLit
        MeshInstance3D _fixture;               // the prop's own mesh (LOD0), handed in by WorldBuilder
        MeshInstance3D _emissive;              // the light-emitting sub-mesh split off the fixture -- the ONLY part that glows
        Material _fixtureOffMat, _fixtureLitMat;
        MeshInstance3D _outline;               // whole-lamp white silhouette (OutlineOverlay), shown while looked at -- toggle lamps only
        AudioStreamPlayer3D _hum;              // ceiling-strip fluorescent hum, looping; volume RIDES the flicker (ApplyEffective) + hard-mutes off/broken
        float _humDb;                          // the hum's full volume in dB (dropped to -80 = silent when the light is off)
        bool _built;                           // BuildVisual is idempotent -- a 2nd split of `body` has no emitter tris and would glow the housing
        bool _on = true;                       // player F-toggle state, INDEPENDENT of the grid -- ON by default (master)
        bool _lastLit;                         // last grid-lit state; effective visual = _lastLit && _on

        protected override bool NightGated => false;
        protected override string LightGroup => "gridlights";

        public static LampLight Make(Vector3 worldPos, MeshInstance3D fixture = null, Kind kind = Kind.Generic)
        {
            var l = new LampLight { Position = worldPos, TopLevel = true, _fixture = fixture, _kind = kind };
            l.InitJitter(worldPos);   // per-fixture brightness jitter + reaction-delay fraction (GridLight)
            return l;
        }

        protected override void BuildVisual()
        {
            if (_built) return;   // a 2nd call would split `body` -> no emitter tris -> glow falls back to the housing (the bug)
            _built = true;

            float scale = _kind == Kind.DeskBulb ? 0.5f : 1f;   // master: desk lamp at half intensity
            _omniEnergy = Energy * _worn * scale;
            _omni = new OmniLight3D { LightColor = BulbColor, OmniRange = Range, LightEnergy = _omniEnergy, ShadowEnabled = false };
            _omni.AddToGroup(LightShadowBudget.Group);   // opt in to the shadow budget; it decides when this one casts

            if (_fixture != null && IsInstanceValid(_fixture))
            {
                // Precompute the lit material once: keep the albedo/texture, add warm emission on top.
                _fixtureOffMat = _fixture.MaterialOverride;
                var lit = (_fixtureOffMat as StandardMaterial3D)?.Duplicate() as StandardMaterial3D
                          ?? new StandardMaterial3D { AlbedoColor = BulbColor };
                lit.EmissionEnabled = true; lit.Emission = BulbColor; lit.EmissionEnergyMultiplier = FixtureEmission * _worn;
                _fixtureLitMat = lit;

                // Carve the emitting sub-mesh per kind; the housing keeps its own matte material.
                var src = _fixture.Mesh as ArrayMesh;
                ArrayMesh body = null, emit = null;
                switch (_kind)
                {
                    case Kind.CeilingStrip: (body, emit) = ObjMesh.SplitLens(src); break;
                    case Kind.FloorShade:   (body, emit) = ObjMesh.SplitByUvCentroid(src, uv => uv.X < 0.5f && uv.Y > 0.5f); break;
                    case Kind.DeskBulb:     (body, emit) = ObjMesh.SplitByFace(src, (c, n) => c.Z > 0.6f && n.Z < -0.4f && n.X * DebugBulbSide > 0f); break;   // head "opening" face: on the head, facing down + to the picked side
                    // Generic: no split (whole small fixture glows).
                }
                if (emit != null)
                {
                    _fixture.Mesh = body;
                    _emissive = new MeshInstance3D { Name = "Emissive", Mesh = emit, MaterialOverride = _fixtureOffMat };
                    _fixture.AddChild(_emissive);   // overlays the fixture in its own local space
                }

                // Toggle lamps get a whole-lamp white silhouette for the look-at affordance (mirrors GasPump/ObjectDoor).
                if (IsToggle(_kind) && src != null)
                {
                    _outline = OutlineOverlay.MakeOutline(src);
                    _fixture.AddChild(_outline);
                }
            }

            AddChild(_omni);
            _omni.Position = ComputeOmniLocal();   // after the split, so FloorShade/DeskBulb can anchor to the emitter

            // Fluorescent ballast hum on the CEILING strip only (an incandescent desk/table lamp doesn't hum). A quiet
            // looping 3D tone whose volume rides the flicker -- ApplyEffective runs per stutter frame, so the hum stutters
            // WITH the light and hard-mutes when it's off or smashed (not a fade). Random phase per fixture so a corridor
            // of tubes isn't one giant phase-locked tube (tinyclaw's spec).
            if (_kind == Kind.CeilingStrip)
            {
                var hum = PlayerController.LoadWavOneShot("res://content/sounds/fluorescent_hum.wav", loop: true);
                if (hum != null)
                {
                    _humDb = Mathf.LinearToDb(0.35f);   // master: the "quieter + further" tune read LOUDER (a higher UnitSize holds full
                    // volume further out), so reverted to the original -- it was fine how it was.
                    _hum = new AudioStreamPlayer3D { Stream = hum, UnitSize = 2.2f, MaxDistance = 13f, VolumeDb = -80f };
                    AddChild(_hum);
                    _hum.Play();
                    _hum.Seek(GD.Randf() * 6f);   // 6.000s loop -> random offset decorrelates neighbouring tubes
                }
            }
        }

        // Where the point light sits, in this node's local space (LampLight is TopLevel at the fixture centre). Always
        // off the fixture's real GlobalTransform, so the ex=295 outlier lands correctly.
        Vector3 ComputeOmniLocal()
        {
            if (_fixture == null || !IsInstanceValid(_fixture)) return Vector3.Zero;
            var xf = _fixture.GlobalTransform;
            var fA = _fixture.GetAabb();
            Vector3 fCenterW = xf * fA.GetCenter();
            switch (_kind)
            {
                case Kind.CeilingStrip:
                {
                    float bottomY = WorldBottomY(fA, xf) - 0.9f;   // FLOATING well below the prop (master 2026-08-09: "a lil bit lower" then "lower still")
                    return new Vector3(fCenterW.X, bottomY, fCenterW.Z) - GlobalPosition;
                }
                case Kind.FloorShade:
                {
                    var em = _emissive?.Mesh as ArrayMesh;
                    Vector3 shadeW = em != null ? xf * em.GetAabb().GetCenter() : fCenterW;
                    return shadeW - GlobalPosition;                                   // centre of the shade
                }
                case Kind.DeskBulb:
                {
                    var em = _emissive?.Mesh as ArrayMesh;
                    if (em == null) return Vector3.Zero;
                    Vector3 bulbW = xf * em.GetAabb().GetCenter();                    // bulb-face centre (world)
                    Vector3 nrmW = (xf.Basis * AverageNormal(em)).Normalized();       // the face normal (world): down + forward
                    return (bulbW + nrmW * 0.18f) - GlobalPosition;                   // just IN FRONT of the face, along its normal (tinyclaw)
                }
                default: return Vector3.Zero;                                         // fixture centre
            }
        }

        static float WorldBottomY(Aabb local, Transform3D xf)
        {
            float min = float.MaxValue;
            for (int i = 0; i < 8; i++) min = Mathf.Min(min, (xf * local.GetEndpoint(i)).Y);
            return min;
        }

        static Vector3 AverageNormal(ArrayMesh m)
        {
            var N = m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector3 s = Vector3.Zero;
            foreach (var n in N) s += n;
            return s.Length() > 0.001f ? s.Normalized() : Vector3.Down;
        }

        protected override void ApplyLit(bool lit) { _lastLit = lit; ApplyEffective(); }

        // Effective visual = grid-lit AND the player toggle. Split out so Toggle() can re-apply it without a grid event.
        // Swaps ONLY the emitter's material to emissive (if we split one off); the housing keeps its matte material.
        void ApplyEffective()
        {
            bool eff = _lastLit && _on;
            if (_omni != null) _omni.LightEnergy = (eff && !DebugNoOmni) ? _omniEnergy : 0f;
            if (_hum != null) _hum.VolumeDb = eff ? _humDb : -80f;   // hum stutters with the flicker (per-frame during a transition) + hard-mutes off/broken
            if (_fixtureLitMat == null) return;
            var emitMi = (_emissive != null && IsInstanceValid(_emissive)) ? _emissive : _fixture;
            if (emitMi != null && IsInstanceValid(emitMi))
                emitMi.MaterialOverride = eff ? _fixtureLitMat : _fixtureOffMat;
        }

        // F while looking at a standing/desk lamp flips its manual on/off (grid power is still required to actually emit).
        public void Toggle() { _on = !_on; ApplyEffective(); }

        // Whole-lamp white silhouette while looked at (mirrors ObjectDoor/GasPump/TVDevice's SetLookFocused).
        public void SetLookFocused(bool on) { if (_outline != null && IsInstanceValid(_outline)) OutlineOverlay.ShowOutline(on, Colors.White, _outline); }

        public bool LitForTest => _omni != null && _omni.LightEnergy > 0f;
    }
}
