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

        public static Color BulbColor = new Color(1f, 0.90f, 0.72f);   // warm white (incandescent-ish)
        public static float Range = 8f;                // OmniLight3D radius -- a room, not a street
        public static float Energy = 2.2f;             // base LightEnergy, scaled by _worn
        public static float FixtureEmission = 0.9f;    // emissive multiplier (SHADED -> HDR/bloom); a warm glow, not blown white
        public static bool DebugNoOmni;                // --lamptest UG_LAMP_NOOMNI=1: kill the room OmniLight so only the emissive shows
        public static float DebugBulbSide = 1f;        // DeskBulb render-pick: +1 = the +X-facing head opening (f18/f19), -1 = -X (f0/f1)

        Kind _kind = Kind.Generic;
        OmniLight3D _omni;
        float _omniEnergy;                     // resolved energy (incl. the DeskBulb half-scale), reused by ApplyLit
        MeshInstance3D _fixture;               // the prop's own mesh (LOD0), handed in by WorldBuilder
        MeshInstance3D _emissive;              // the light-emitting sub-mesh split off the fixture -- the ONLY part that glows
        Material _fixtureOffMat, _fixtureLitMat;
        bool _built;                           // BuildVisual is idempotent -- a 2nd split of `body` has no emitter tris and would glow the housing

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
            }

            AddChild(_omni);
            _omni.Position = ComputeOmniLocal();   // after the split, so FloorShade/DeskBulb can anchor to the emitter
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

        protected override void ApplyLit(bool lit)
        {
            if (_omni != null) _omni.LightEnergy = (lit && !DebugNoOmni) ? _omniEnergy : 0f;
            if (_fixtureLitMat == null) return;
            // Swap ONLY the emitter's material to emissive (if we split one off); the housing keeps its matte material.
            var glow = (_emissive != null && IsInstanceValid(_emissive)) ? _emissive : _fixture;
            if (glow != null && IsInstanceValid(glow))
                glow.MaterialOverride = lit ? _fixtureLitMat : _fixtureOffMat;
        }

        public bool LitForTest => _omni != null && _omni.LightEnergy > 0f;
    }
}
