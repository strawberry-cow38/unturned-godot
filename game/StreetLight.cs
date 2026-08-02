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
        public static float LensEmission = 3.0f;   // lens emission multiplier -- on a SHADED material, so it reaches HDR and blooms
                                                   // (headlights sit at 2f the same way; see the lens material for why Unshaded killed it)
        public static int   MoteCount     = 26;     // dust/bug motes per lamp -- one additive quad each; 0 disables them entirely
        public static float MoteCullRange = 22f;    // motes retire well inside the cone's own draw distance: a close-up detail
        public static float ConeExtend     = 1.10f; // the visual cone runs 10% past the lamp's reach so it still covers
                                                    // ground that sits higher than the pole base -- kerbs, sidewalks (strawberry)
        public static float MoteOpacity    = 0.5f;  // base mote opacity (strawberry). The time-of-day fade SCALES this,
                                                    // so lowering it lowers the whole curve rather than only its peak
        public static float MoteFadeMargin = 7f;    // distance band over which motes fade in/out instead of popping
        public static float MoteFadeLead   = 0.05f; // how far ahead of the lamp (in day fraction) the motes fade
        public static float MoteFadeGap    = 0.012f;// ...and how far BEFORE lights-off they finish fading (strawberry)
        public const  float Watts      = 200f;     // realistic high-pressure-sodium draw (grid consumer)

        SpotLight3D _spot;
        CpuParticles3D _motes;   // dust/bugs drifting in the beam (strawberry); null when MoteCount is 0
        MeshInstance3D _cone, _panel;
        MeshInstance3D _lens;    // the prop's real bulb geometry, owned by the placement, adopted as _panel when present
        Material _lensOffMat, _lensLitMat;   // dark (the prop's own, whatever type that is) vs emissive; swapped instead of hiding the bulb
        bool _panelLit;          // whether the lens is EMITTING -- not the same as visible, since real geometry stays put
        float _reach = 12f;
        float _worn = 1f;   // per-lamp brightness jitter (±5%) so fixtures read old/worn, not identical
        bool _night = false;    // dark enough to be lit (driven by DayNightCycle); starts off, self-inits in _Ready
        bool _powered = true;   // grid feeding the fixture (default on until a grid says otherwise)

        // --- power-transition flicker (master: a street should power down RAGGEDLY, not all at once). A lamp does
        // NOT snap on/off: it waits a per-lamp REACTION delay while blinking irregularly toward the new state, then
        // settles clean. The random delay is what staggers the street. Cosmetic + SP-local (like the shot-out bulb),
        // but the DELAY is deterministic per position so every MP client staggers identically. Mirrors Deployable's
        // ramp flicker. Ticks (_Process) ONLY while mid-transition -- idle lamps cost nothing (PEI has hundreds).
        public static float ReactionMax = 2.2f;   // per-lamp on/off delay = this * (0.15..1.0); the spread does the staggering
        float _reactionFrac;   // [0,1] deterministic per-lamp -> reaction delay = ReactionMax*(0.15+0.85*frac)
        float _reaction, _transT, _flickT;
        bool _transitioning, _targetLit, _shownLit, _flickShow;

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
            // master: ±5% per-lamp brightness so they read old/worn. DETERMINISTIC (a stable hash of the world position),
            // so a given fixture is always the same brightness -- identical every load and for every MP player, no flicker.
            float h = Mathf.Sin(lampWorldPos.X * 12.9898f + lampWorldPos.Z * 78.233f) * 43758.5453f;
            float worn = 0.95f + (h - Mathf.Floor(h)) * 0.10f;   // 0.95 .. 1.05
            // a SECOND independent hash (different constants) so a lamp's reaction delay isn't correlated with its
            // brightness -- deterministic per position so every client staggers the street the same way.
            float h2 = Mathf.Sin(lampWorldPos.X * 39.3468f + lampWorldPos.Z * 11.7654f) * 24634.6345f;
            return new StreetLight { Position = lampWorldPos, TopLevel = true, _reach = Mathf.Max(4f, reach), _worn = worn, _lens = lens, _reactionFrac = h2 - Mathf.Floor(h2) };
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


        // THE BEAM SHAFT. Was a plain truncated cone with a 0.14 top radius -- NARROWER than the lens it hangs off
        // (0.435 x 0.738) -- so the shaft pinched in right below the fixture and flared out again underneath, which
        // reads as two separate cones stacked on each other (strawberry). Lofting it fixes the silhouette at the
        // source: the top ring is a RECTANGLE matching the lens footprint, and it morphs to a circle over the first
        // stretch of the drop, so the beam leaves the lamp as exactly the shape of the thing emitting it and is round
        // by the time anyone reads it as a cone.
        //
        // Built by hand rather than with a CylinderMesh because no primitive changes cross-section along its length.
        static ArrayMesh BeamMesh(float len, float halfA, float halfB, float baseR, float morphEnd = 0.38f, int seg = 24, int rings = 16)
        {
            // Cross-section at depth t (0 = at the lens, 1 = at the base): a rectangle blended toward a circle.
            // The rectangle point for an angle is the circle point pushed out to the rect boundary (max-norm), which
            // keeps the two parametrisations in step so corresponding points lerp without the surface twisting.
            Vector3 Section(float th, float t)
            {
                float a = Mathf.Lerp(halfA, baseR, t), b = Mathf.Lerp(halfB, baseR, t);
                float ct = Mathf.Cos(th), st = Mathf.Sin(th);
                float m = Mathf.Max(Mathf.Abs(ct), Mathf.Abs(st));
                if (m < 0.0001f) m = 0.0001f;
                var rect = new Vector2(a * ct / m, b * st / m);
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

        // The lens is a box in the PROP's frame, so on a rotated lamp its footprint is rotated too -- a beam whose
        // top rectangle is world-axis-aligned would sit visibly crooked under the fixture. Take whichever of the
        // box's three axes is most vertical as "down"; the other two span the footprint the beam should start as.
        static (float A, float B, float Yaw) LensFootprint(MeshInstance3D lens)
        {
            var b = lens.Transform.Basis;
            var half = lens.Mesh.GetAabb().Size * 0.5f;
            var axes = new[] { b.X * half.X, b.Y * half.Y, b.Z * half.Z };
            int vert = 0;
            for (int i = 1; i < 3; i++)
                if (Mathf.Abs(axes[i].Normalized().Y) > Mathf.Abs(axes[vert].Normalized().Y)) vert = i;
            var h0 = axes[(vert + 1) % 3]; h0 = new Vector3(h0.X, 0f, h0.Z);
            var h1 = axes[(vert + 2) % 3]; h1 = new Vector3(h1.X, 0f, h1.Z);
            if (h0.Length() < 0.01f || h1.Length() < 0.01f) return (0.22f, 0.22f, 0f);
            return (h0.Length(), h1.Length(), Mathf.Atan2(-h0.Z, h0.X));
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


        /// <summary>Mote opacity for a time of day (0..1). Pure + static so the curve is testable -- the visual
        /// half needs an eyeball, but "reaches zero BEFORE the lamp cuts out" is a property, not a matter of taste.
        ///
        /// Lamps switch at DayNightCycle.IsNightTime's thresholds (off at dawn 0.26, on at dusk 0.74). Motes fade
        /// OUT over MoteFadeLead ending MoteFadeGap early, so the beam is already dust-free when the lamp dies
        /// rather than losing both at once; and fade IN just after dusk lights it (strawberry).</summary>
        public static float MoteFadeFor(float timeOfDay)
        {
            const float dawn = 0.26f, dusk = 0.74f;   // must track DayNightCycle.IsNightTime
            float t = Mathf.PosMod(timeOfDay, 1f);
            float outEnd = dawn - MoteFadeGap, outStart = outEnd - MoteFadeLead;
            float inStart = dusk + MoteFadeGap, inEnd = inStart + MoteFadeLead;
            if (t < outStart) return 1f;                                   // deep night
            if (t < outEnd) return 1f - (t - outStart) / MoteFadeLead;     // fading out ahead of lights-off
            if (t < inStart) return 0f;                                    // day (and the last moments before lights-off)
            if (t < inEnd) return (t - inStart) / MoteFadeLead;            // fading in after dusk lights the lamp
            return 1f;
        }

        float _moteFade = 1f;
        /// <summary>Drive the mote opacity from the world clock. Emission stops entirely at zero so a daytime lamp
        /// simulates nothing at all.</summary>
        public void SetMoteFade(float a)
        {
            a = Mathf.Clamp(a, 0f, 1f);
            if (Mathf.IsEqualApprox(_moteFade, a)) return;
            _moteFade = a;
            ApplyMoteFade();
        }

        /// <summary>THE single definition of "this lamp is emitting". It exists because it was previously written
        /// out twice -- Refresh and ApplyMoteFade each computed it -- and adding the shot-out bulb to one of them
        /// left the other behind: a lamp you shot went dark but kept its dust drifting in a beam that was no longer
        /// there. Two copies of a condition drift the moment a term is added to it, so there is only one.</summary>
        bool Lit => _night && _powered && !_broken && !_bulbOut;

        void ApplyMoteFade()
        {
            if (_motes == null) return;
            // INSTANTLY, not faded (strawberry): Visible=false cuts the already-live motes on the spot, where
            // Emitting=false alone would only stop new ones and leave the existing cloud drifting for its
            // remaining lifetime -- seconds of dust hanging under a lamp that is visibly out.
            float a = Lit ? _moteFade : 0f;
            _motes.Emitting = a > 0.001f;
            _motes.Visible = a > 0.001f;
            if (_motes.MaterialOverride is StandardMaterial3D m)
                m.AlbedoColor = new Color(_moteBase.R, _moteBase.G, _moteBase.B, _moteBase.A * a);
        }
        Color _moteBase = Colors.White;

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

            // 3) THE FAKE BEAM: a wide lofted shaft that fades (gradient) from the lamp to transparent by the base, soft +
            //    additive. Wider at the base per master; the fade dissolves it into the ground pool. Its cross-section
            //    starts as the LENS RECTANGLE and rounds off on the way down -- see BeamMesh for why it is not a cone.
            float coneLen = len * ConeExtend;   // the SHAFT runs past the reach; motes below still use `len` so they
                                                //  never drift under the pavement
            float baseR = coneLen * Mathf.Tan(Mathf.DegToRad(half)) * 1.035f;   // master: cone 10% smaller
            // Start the shaft as the lens's own rectangle. Falls back to a near-square top for a lamp built without a
            // prop lens (the --lighttest fallback and the L1 tests), which is just the old look.
            var (topA, topB, topYaw) = _lens?.Mesh != null ? LensFootprint(_lens) : (0.16f, 0.16f, 0f);
            _cone = new MeshInstance3D
            {
                Position = under,
                Rotation = new Vector3(0f, topYaw, 0f),
                Mesh = BeamMesh(coneLen, topA, topB, baseR),
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
                    // CLAMP, do not repeat (strawberry: "maybe the gradient is looping to the top"). ConeGradient is
                    // 1x64 sampled LINEARLY, so with the default repeat wrap the v=0 edge blends texel 0 (alpha 0)
                    // with its wrapped neighbour texel 63 (alpha 1) -- the top of the shaft sampled ~0.5 alpha
                    // instead of ~0. That put a bright band right under the lens with a dark gap beneath it, which
                    // reads as a second cone stacked on the beam. Measured: R hit 40 at the top, collapsed to 5
                    // eight pixels down, then climbed again with the real gradient.
                    //
                    // This is a MATERIAL bug, not a mesh one -- it predates the loft and was in the old cone too,
                    // which is why killing the geometric pinch did not kill the artefact.
                    TextureRepeat = false,
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
                    AlbedoColor = new Color(col.R, col.G, col.B, MoteOpacity * _worn),
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
                    VisibilityRangeEndMargin = MoteFadeMargin,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,   // dissolve over the margin
                                                                                                     // rather than blinking out at the line
                    CustomAabb = new Aabb(new Vector3(-coneR, -len, -coneR), new Vector3(coneR * 2f, len * 1.1f, coneR * 2f)),
                    MaterialOverride = moteMat,
                };
                _moteBase = moteMat.AlbedoColor;
                AddChild(_motes);
            }

            // initial state: dark unless it's night AND the town grid is live; the DayNightCycle sweep drives both after.
            var dn = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            _night = dn == null || DayNightCycle.IsNightTime(dn.Time);   // no cycle in this mode -> default to "night" (lit if grid on)
            _powered = PowerNet.GlobalPower;                              // municipal grid feed (the town mains switch); default on
            Refresh();
            SetProcess(false);   // idle lamps do NOT tick every frame -- _Process runs ONLY while a transition is flickering (PEI has hundreds)
        }

        // Grid hook: the fixture is drawing its Watts. Composited with the day/night state in Refresh(). animate=true
        // (the DayNightCycle sweep) plays the reaction-delay + flicker; the default snaps instantly (tests, spawn).
        public void SetPowered(bool on, bool animate = false) { if (_powered == on) return; _powered = on; if (animate) BeginTransition(); else Refresh(); }

        // Day/night hook (driven by DayNightCycle): street lamps light dusk->dawn and go dark by day.
        public void SetNight(bool on, bool animate = false) { if (_night == on) return; _night = on; if (animate) BeginTransition(); else Refresh(); }

        /// <summary>Smashed pole -> the lamp is dead for good (until the prop respawns). This has to be STATE
        /// rather than a one-shot "turn it off": Refresh() re-derives lit from night+power on every day/night
        /// tick and every grid toggle, so a lamp merely switched off would light itself again at the next dusk
        /// while its pole lay in rubble. Same shape as the vehicle alarm relighting a wreck.</summary>
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            // A rubble RESET rebuilds the prop, so it comes back with an intact bulb. Without this a lamp that was
            // shot out once would stay dark through every respawn, permanently, with no way to tell why.
            if (!broken) _bulbOut = false;
            Refresh();
        }
        bool _broken;

        /// <summary>Collider meta carrying the lamp, so a bullet that lands on a Street_Light_0 can find it.</summary>
        public static readonly StringName HitMeta = "streetlight";

        bool _bulbOut;
        public bool BulbOutForTest => _bulbOut;

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

        /// <summary>Shoot the bulb out: this lamp goes dark and STAYS dark, with the pole still standing. Distinct
        /// from SetBroken -- broken means the prop is rubble and the lens goes with it; a shot-out bulb keeps its
        /// geometry and simply stops emitting, which is what a smashed lamp actually looks like from the street.</summary>
        public bool ShootOutBulb()
        {
            if (_bulbOut || _broken) return false;
            _bulbOut = true;
            Refresh();
            return true;
        }

        /// <summary>L1: a lamp lights with THREE separate things -- the real spot, the emissive lens, and the
        /// fake additive cone. "The light went out" has three independent failure modes, so a test asserts each
        /// rather than trusting one to stand for the others.</summary>
        public bool LitSpotForTest => _spot != null && _spot.LightEnergy > 0f;
        public bool TransitioningForTest => _transitioning;   // still mid reaction-delay/flicker, not settled
        // EMITTING, not merely visible: an adopted lens stays in the scene when the lamp is off (it is the prop's own
        // bulb), so visibility stopped being the signal for "lit" the moment real geometry replaced the stand-in disc.
        public bool LitPanelForTest => _panel != null && _panelLit;
        public bool LensPresentForTest => _panel != null && _panel.Visible;
        public bool LitConeForTest => _cone != null && _cone.Visible;
        public bool LitMotesForTest => _motes != null && _motes.Emitting;
        // Emitting and Visible are different failures: stopping emission still leaves the live cloud drifting for
        // its lifetime, which is exactly what "kill the particles INSTANTLY" rules out. Asserted separately.
        public bool MotesVisibleForTest => _motes != null && _motes.Visible;

        // A lamp glows only when it's dark AND the grid is feeding it -- and isn't smashed. Toggles the real
        // spot + the emissive lens + the cone.
        void Refresh()
        {
            if (_transitioning) { _transitioning = false; SetProcess(false); }   // a HARD state change (spawn / broken / shot-out) cancels any in-flight flicker
            bool lit = Lit;
            _shownLit = lit;
            ApplyLit(lit);
            ApplyMoteFade();   // folds the lit state together with the time-of-day fade; zero = no sim cost at all
        }

        // Apply a lit/dark state to the three emitters (spot + emissive lens + cone). Split out of Refresh so the
        // transition flicker can drive an intermediate (blinking) state without re-deriving Lit or touching the motes.
        void ApplyLit(bool lit)
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
            if (_cone != null) _cone.Visible = lit;
        }

        // The grid or the day/night clock flipped this lamp and asked to ANIMATE it: don't snap. Wait a per-lamp
        // reaction delay while blinking irregularly toward the new state (odds of showing the NEW state ramp 0->1
        // across the delay), then settle clean. The random per-lamp delay staggers the whole street (master).
        void BeginTransition()
        {
            bool target = Lit;
            if (target == _shownLit && !_transitioning) return;   // no visible change (e.g. daytime) and nothing in flight
            _targetLit = target;
            if (!_transitioning)
            {
                _transitioning = true;
                _transT = 0f; _flickT = 0f; _flickShow = _shownLit;
                _reaction = ReactionMax * (0.15f + 0.85f * _reactionFrac);
                SetProcess(true);
            }
            // already mid-transition (the grid flapped): keep the running delay, just re-aim at the new target.
        }

        public override void _Process(double delta)
        {
            if (!_transitioning) return;
            _transT += (float)delta;
            _flickT -= (float)delta;
            if (_flickT <= 0f)
            {
                float p = Mathf.Clamp(_transT / Mathf.Max(0.05f, _reaction), 0f, 1f);   // 0 at the start -> 1 at settle
                _flickShow = GD.Randf() < p ? _targetLit : !_targetLit;                  // early: mostly the OLD state; late: mostly the NEW
                _flickT = 0.04f + GD.Randf() * 0.08f;                                    // irregular ~8-25 Hz stutter, like the deployable lamp
                ApplyLit(_flickShow);
            }
            if (_transT >= _reaction)   // settle to the clean final state (+ motes)
            {
                _transitioning = false;
                SetProcess(false);
                Refresh();
            }
        }
    }
}
