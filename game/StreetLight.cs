using Godot;

namespace UnturnedGodot
{
    // Cheap + pretty streetlight: a SOFT downward sodium pool (the real light) + an emissive underside panel. Spawned per Street_Light_0 placement (WorldBuilder) in WORLD space at the lamp head, emitting from
    // the head's underside so it aims straight down regardless of the pole tilt. Colour TEMPERATURE is tweakable
    // (StreetLight.ColorTempK: 2000K warm sodium default for the BC towns; ~5000K cold-white LED for a city map).
    // The grid-power / night-gating / reaction-delay-flicker machinery is shared with LampLight via GridLight
    // (the base class) -- this file owns only the streetlight's own visuals: spot + lens.
    //
    // NO FAKE BEAM, NO DUST (strawberry 2026-10-04: "remove the cones as well as dust flecks", keeping only the actual
    // light sources). The additive shaft and its mote cloud are gone; BeamMesh + ConeGradient stay, because the
    // lighthouse, the deployable spotlight and the TV still draw their shafts with them.
    public partial class StreetLight : GridLight
    {
        public static float ColorTempK = 2000f;   // 2000K warm sodium ... 5000K cold LED
        public static float Energy     = 12.0f;    // ground-pool brightness (master: reined in from nuclear; raised source still gives the weight)
        public static float LensEmission = 3.0f;   // lens emission multiplier -- on a SHADED material, so it reaches HDR and blooms
                                                   // (headlights sit at 2f the same way; see the lens material for why Unshaded killed it)
        public const  float Watts      = 200f;     // realistic high-pressure-sodium draw (grid consumer)

        SpotLight3D _spot;
        MeshInstance3D _panel;
        MeshInstance3D _lens;    // the prop's real bulb geometry, owned by the placement, adopted as _panel when present
        Material _lensOffMat, _lensLitMat;   // dark (the prop's own, whatever type that is) vs emissive; swapped instead of hiding the bulb
        bool _panelLit;          // whether the lens is EMITTING -- not the same as visible, since real geometry stays put
        float _reach = 12f;

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

        /// <param name="lens">The prop's OWN bulb geometry, already split onto its own MeshInstance3D by
        /// WorldBuilder (ObjMesh.SplitLens). When supplied the lamp drives that instead of building its stand-in
        /// disc, so the glow lands on the real lens. The node stays parented to the prop -- it has to keep the
        /// placement's basis to sit inside the fixture -- and this only owns its material + visibility.</param>
        public static StreetLight Make(Vector3 lampWorldPos, float reach, MeshInstance3D lens = null)
        {
            var sl = new StreetLight { Position = lampWorldPos, TopLevel = true, _reach = Mathf.Max(4f, reach), _lens = lens };
            sl.InitJitter(lampWorldPos);   // per-fixture brightness jitter + reaction-delay fraction (GridLight)
            return sl;
        }

        // Vertical fade for the cone: bright at the lamp end, transparent by the base -- so the shaft dissolves into the
        // ground pool instead of ending in a hard rim. Mapped along the cylinder's V (height).
        internal static ImageTexture ConeGradient()
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


        // THE BEAM SHAFT. Was a plain truncated cone with a 0.14 top radius -- NARROWER than the lens it hangs off
        // (0.435 x 0.738) -- so the shaft pinched in right below the fixture and flared out again underneath, which
        // reads as two separate cones stacked on each other (strawberry). Lofting it fixes the silhouette at the
        // source: the top ring is a RECTANGLE matching the lens footprint, and it morphs to a circle over the first
        // stretch of the drop, so the beam leaves the lamp as exactly the shape of the thing emitting it and is round
        // by the time anyone reads it as a cone.
        //
        // Built by hand rather than with a CylinderMesh because no primitive changes cross-section along its length.
        /// <param name="keepRect">Skip the rectangle-to-circle morph entirely, so the cross-section stays boxy for
        /// its whole length. The lamp wants the morph (a round pool on the ground); a TV does not -- light leaving a
        /// screen keeps the screen's shape (master: "maintain a square shape"). Default preserves the lamp.</param>
        /// <param name="endScale">If &gt; 0, the far end is the near end scaled by this factor, which KEEPS THE
        /// ASPECT RATIO. The default path lerps both half-extents toward baseR instead, which converges any starting
        /// rectangle to a square and would quietly discard a widescreen screen's proportions.</param>
        /// <param name="endScaleV">Separate growth for the SECOND half-extent, so a beam can open wider than it
        /// opens tall -- what makes a headlight a flat wedge rather than a slab. -1 (the default) means "same as
        /// endScale", which is every existing caller's behaviour unchanged.</param>
        internal static ArrayMesh BeamMesh(float len, float halfA, float halfB, float baseR, float morphEnd = 0.38f, int seg = 24, int rings = 16,
                                           bool keepRect = false, float endScale = -1f, float endScaleV = -1f)
        {
            // Cross-section at depth t (0 = at the lens, 1 = at the base): a rectangle blended toward a circle.
            // The rectangle point for an angle is the circle point pushed out to the rect boundary (max-norm), which
            // keeps the two parametrisations in step so corresponding points lerp without the surface twisting.
            Vector3 Section(float th, float t)
            {
                float a, b;
                if (endScale > 0f) { a = halfA * Mathf.Lerp(1f, endScale, t); b = halfB * Mathf.Lerp(1f, endScaleV > 0f ? endScaleV : endScale, t); }
                else { a = Mathf.Lerp(halfA, baseR, t); b = Mathf.Lerp(halfB, baseR, t); }
                float ct = Mathf.Cos(th), st = Mathf.Sin(th);
                float m = Mathf.Max(Mathf.Abs(ct), Mathf.Abs(st));
                if (m < 0.0001f) m = 0.0001f;
                var rect = new Vector2(a * ct / m, b * st / m);
                if (keepRect) return new Vector3(rect.X, -t * len, rect.Y);
                float r = (a + b) * 0.5f;
                var circ = new Vector2(r * ct, r * st);
                var p = rect.Lerp(circ, Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t / morphEnd, 0f, 1f)));
                return new Vector3(p.X, -t * len, p.Y);
            }

            var v = new System.Collections.Generic.List<Vector3>();
            var n = new System.Collections.Generic.List<Vector3>();
            var u = new System.Collections.Generic.List<Vector2>();
            for (int ri = 0; ri < rings; ri++)
            {
                float t0 = (float)ri / rings, t1 = (float)(ri + 1) / rings;
                for (int si = 0; si < seg; si++)
                {
                    float th0 = Mathf.Tau * si / seg, th1 = Mathf.Tau * (si + 1) / seg;
                    Vector3 p00 = Section(th0, t0), p10 = Section(th1, t0), p01 = Section(th0, t1), p11 = Section(th1, t1);
                    // v = t * SideV, reproducing Godot's CylinderMesh UV exactly so swapping the mesh changes the
                    // silhouette and NOTHING else. Two non-obvious things are baked into that factor, both measured
                    // off the real meshes rather than assumed:
                    //
                    //  1. CylinderMesh gives the SIDE only v in [0, 0.5] -- the top half of UV space is reserved for
                    //     the caps even with CapTop/CapBottom off. So the shipped beam has only ever sampled the
                    //     bottom half of ConeGradient, peaking at 0.5^1.7 ~= 0.31 alpha, not 1.0. Mapping the full
                    //     [0,1] range (the obvious thing to write) made the shaft ~2x denser at every depth.
                    //  2. v=0 is at the TOP, so the gradient runs faint-at-the-lamp -> dense-at-the-ground. That is
                    //     the opposite of what ConeGradient's own comment claims ("bright at the lamp end ... mesh V
                    //     runs base->lamp"), but it is the look the night tuning was built against, so it stands.
                    //     Flipping it, and using the gradient's unused top half, is a taste call, not a bug fix.
                    const float SideV = 0.5f;
                    Vector2 u00 = new((float)si / seg, t0 * SideV), u10 = new((float)(si + 1) / seg, t0 * SideV);
                    Vector2 u01 = new((float)si / seg, t1 * SideV), u11 = new((float)(si + 1) / seg, t1 * SideV);
                    void Emit(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc)
                    {
                        var fn = (b - a).Cross(c - a).Normalized();
                        v.Add(a); v.Add(b); v.Add(c);
                        n.Add(fn); n.Add(fn); n.Add(fn);
                        u.Add(ua); u.Add(ub); u.Add(uc);
                    }
                    Emit(p00, p01, p10, u00, u01, u10);
                    Emit(p10, p01, p11, u10, u01, u11);
                }
            }
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = n.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = u.ToArray();
            var m2 = new ArrayMesh();
            m2.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m2;
        }

        // Build the fixture's own emitters -- called once by GridLight._Ready, before the initial power/night
        // state is applied and Refresh() runs.
        protected override void BuildVisual()
        {
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
                // FILL THE WHOLE CONE (strawberry 2026-09-04 "convert to big spots that fill the whole cone"): the angle
                // attenuation used to be 1.9 -- a pool that was dim well inside the 38-deg visual cone and gone at its
                // rim, so the beam shaft lit far more ground than the light did. Now the spot reaches the cone's rim at
                // near-full strength (0.6 = a short soft edge) and its range clears the ground with margin.
                Position = under, RotationDegrees = new Vector3(-90f, 0f, 0f),
                SpotRange = len + 24f, SpotAngle = half + 4f, SpotAngleAttenuation = 0.6f, SpotAttenuation = 1.0f,
                LightColor = col, LightEnergy = Energy * _worn, ShadowEnabled = false,
            };
            _spot.AddToGroup(LightShadowBudget.Group);
            AddChild(_spot);

            // 2) THE EMISSIVE LENS: the part of the fixture that visibly glows when the lamp is on.
            //
            //    Preferred form is the prop's OWN bulb geometry (strawberry), split off the mesh by ObjMesh.SplitLens
            //    and handed in. It is a real box inside the head, and the housing has no floor under it, so its
            //    underside genuinely reads from the street. Same warm sodium colour + intensity as the disc it
            //    replaces, so the night tuning that was built against the old look still holds.
            //
            //    Fallback is the old flat disc on the head's underside, for callers with no prop mesh to split --
            //    the --lighttest harness and the L1 tests build bare lamps with no Street_Light_0 behind them.
            // NOT Unshaded. Unshaded outputs ALBEDO and drops EMISSION, so the 4.5x multiplier this material has
            // carried since the stand-in disc was doing precisely nothing -- the lens was a flat albedo-coloured
            // rectangle that could never cross the environment's 0.9 glow threshold and therefore never bloomed.
            // The vehicle headlights read hotter with a multiplier of only 2f for exactly this reason: their lens
            // material is normally shaded, so emission lands in HDR and the glow pass picks it up (strawberry:
            // "more intense glow on the bulb like we do with headlights").
            var lensMat = new StandardMaterial3D
            {
                AlbedoColor = col, EmissionEnabled = true, Emission = col,
                EmissionEnergyMultiplier = LensEmission * _worn,
                Metallic = 0f, Roughness = 0.4f,
            };
            if (_lens != null)
            {
                // The lens is REAL GEOMETRY, so it must not be hidden when the lamp is off -- the bulb triangles were
                // taken out of the body mesh, and hiding them leaves an empty socket in the fixture in broad daylight
                // (strawberry). Only the MATERIAL changes: the prop's own material when dark, so the bulb reads as the
                // warm tan it is textured with, and the emissive one when lit. WorldBuilder hands the dark material in
                // as the node's starting MaterialOverride; falling back to lensMat just means it never goes dark.
                _lensOffMat = _lens.MaterialOverride ?? lensMat;
                _lensLitMat = lensMat;
                _lens.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                _panel = _lens;   // adopted, NOT reparented: it keeps the placement basis that puts it in the fixture
            }
            else
            {
                _panel = new MeshInstance3D
                {
                    Position = under,
                    Mesh = new CylinderMesh { TopRadius = 0.26f, BottomRadius = 0.26f, Height = 0.03f, RadialSegments = 14, CapTop = true, CapBottom = true },
                    MaterialOverride = lensMat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(_panel);
            }
        }

        // DayNightCycle sweeps this group name with SetNight/SetPowered (streetlights are night-gated, the
        // GridLight default) -- see GridLight.LightGroup.
        protected override string LightGroup => "streetlights";

        /// <summary>Collider meta carrying the lamp, so a bullet that lands on a Street_Light_0 can find it.</summary>
        public static readonly StringName HitMeta = "streetlight";

        /// <summary>Is this world point on the BULB rather than the pole or the housing? The prop's collider is one
        /// trimesh over the whole mesh, so a shot at the lamp head arrives indistinguishable from a shot at the
        /// post -- the lens's own bounds are what separate them. Deliberately NOT a second collider on the lens:
        /// the trimesh already covers those faces, so a coincident box would just race it for the raycast hit.</summary>
        public bool IsBulbHit(Vector3 worldPoint)
        {
            if (_lens?.Mesh == null || !IsInstanceValid(_lens)) return false;
            var local = _lens.GlobalTransform.AffineInverse() * worldPoint;
            var box = _lens.Mesh.GetAabb().Grow(0.06f);   // bullets land ON the surface, i.e. exactly on the boundary
            return box.HasPoint(local);
        }

        /// <summary>L1: a lamp lights with TWO separate things -- the real spot and the emissive lens. "The light
        /// went out" has two independent failure modes, so a test asserts each rather than trusting one to stand for
        /// the other. (There used to be a third, the fake cone, removed 2026-10-04.)</summary>
        public bool LitSpotForTest => _spot != null && _spot.LightEnergy > 0f;
        // EMITTING, not merely visible: an adopted lens stays in the scene when the lamp is off (it is the prop's own
        // bulb), so visibility stopped being the signal for "lit" the moment real geometry replaced the stand-in disc.
        public bool LitPanelForTest => _panel != null && _panelLit;
        public bool LensPresentForTest => _panel != null && _panel.Visible;
        /// <summary>The lamp built NO fake shaft and NO dust: nothing but the spot and the lens hangs off it.</summary>
        public bool HasFakeBeamOrDustForTest
        {
            get
            {
                foreach (var ch in GetChildren())
                    if (ch is CpuParticles3D || ch is GpuParticles3D || (ch is MeshInstance3D mi && mi != _panel)) return true;
                return false;
            }
        }

        // Apply a lit/dark state to the two emitters (spot + emissive lens). Split out of Refresh so the
        // transition flicker (GridLight) can drive an intermediate (blinking) state without re-deriving Lit.
        protected override void ApplyLit(bool lit)
        {
            if (_spot != null) _spot.LightEnergy = lit ? Energy * _worn : 0f;
            _panelLit = lit;
            if (_panel != null)
            {
                if (_lens != null)
                {
                    // real bulb geometry: present whenever the fixture is, only its material changes. It disappears
                    // ONLY with the prop itself, i.e. when the pole is smashed and DestructibleField hides the rest.
                    _panel.Visible = !_broken;
                    _panel.MaterialOverride = lit ? _lensLitMat : _lensOffMat;
                }
                else _panel.Visible = lit;   // the stand-in disc is pure glow -- there is nothing to show when dark
            }
        }
    }
}
